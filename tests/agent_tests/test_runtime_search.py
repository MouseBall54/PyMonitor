import unittest

from pyruntime_inspector_agent.handles import HandleStore
from pyruntime_inspector_agent.runtime_search import _gc_root, search_roots
from pyruntime_inspector_agent.safe_objects import SafeObjectInspector


class _NestedTarget:
    search_label = "class-attribute-needle"

    def __init__(self):
        self.branch = {"leaves": [{"ultimate_needle": 42}]}

    def calculate_needle_total(self, amount=1):
        return amount

    @property
    def dangerous_needle_property(self):
        raise AssertionError("runtime search must not execute properties")

    def __repr__(self):
        raise AssertionError("runtime search must not call user repr")


class RuntimeSearchTests(unittest.TestCase):
    def setUp(self):
        self.handles = HandleStore()
        self.inspector = SafeObjectInspector(self.handles)

    def root(self, value):
        return [{
            "sourceKind": "module",
            "name": "demo_runtime",
            "location": "Modules / demo_runtime",
            "moduleName": "demo_runtime",
            "scopeType": "module",
            "value": None,
            "entries": lambda: [("root_instance", value)],
        }]

    def test_recursively_finds_nested_value_and_reports_complete_location(self):
        result = search_roots(self.inspector, self.root(_NestedTarget()), "ultimate_needle")

        match = next(item for item in result["items"] if item["name"].endswith("'ultimate_needle'"))
        self.assertEqual(
            "Modules / demo_runtime / root_instance / branch[0] 'leaves'[0][0] 'ultimate_needle'",
            match["location"],
        )
        self.assertIn("ultimate_needle", match["name"])
        self.assertEqual("demo_runtime", match["moduleName"])
        self.assertEqual("int", match["value"]["typeName"])
        self.assertGreaterEqual(match["depth"], 3)

    def test_searches_class_methods_and_properties_without_executing_target_code(self):
        target = _NestedTarget()

        methods = search_roots(self.inspector, self.root(target), "calculate_needle")
        properties = search_roots(self.inspector, self.root(target), "dangerous_needle")

        method = next(item for item in methods["items"] if item["kind"] == "method")
        prop = next(item for item in properties["items"] if item["kind"] == "property")
        self.assertEqual("calculate_needle_total", method["name"])
        self.assertIn("Class", method["location"])
        self.assertEqual("dangerous_needle_property", prop["name"])
        self.assertEqual("property", prop["classMember"]["kind"])
        self.assertEqual("root_instance", method["rootName"])

    def test_searches_class_attribute_values_and_class_hierarchy_metadata(self):
        target = _NestedTarget()

        attributes = search_roots(self.inspector, self.root(target), "class-attribute-needle")
        hierarchy = search_roots(self.inspector, self.root(target), "builtins object")

        attribute = next(item for item in attributes["items"] if item["kind"] == "class attribute")
        class_result = next(item for item in hierarchy["items"] if item["kind"] == "class")
        self.assertEqual("search_label", attribute["name"])
        self.assertIn("memberPreview", attribute["matchFields"])
        self.assertIn("Class", class_result["location"])

    def test_matches_instance_type_and_reports_bounded_incomplete_scan(self):
        target = _NestedTarget()

        matched = search_roots(self.inspector, self.root(target), "NestedTarget")
        bounded = search_roots(self.inspector, self.root(target), "missing", max_objects=1)

        self.assertTrue(any(item["kind"] in ("variable", "class") for item in matched["items"]))
        self.assertFalse(bounded["scanComplete"])
        self.assertTrue(bounded["objectLimitReached"])
        self.assertEqual(1, bounded["objectsScanned"])

    def test_rejects_empty_and_excessive_search_limits(self):
        with self.assertRaises(ValueError):
            search_roots(self.inspector, self.root(object()), "  ")
        with self.assertRaises(ValueError):
            search_roots(self.inspector, self.root(object()), "needle", max_depth=33)

    def test_breadth_first_search_reaches_a_shallow_later_root_before_deep_expansion(self):
        roots = [
            {
                "sourceKind": "module",
                "name": "large",
                "location": "Modules / large",
                "moduleName": "large",
                "scopeType": "module",
                "value": None,
                "entries": lambda: [("large_graph", {"nested": {"deeper": [1, 2, 3]}})],
            },
            {
                "sourceKind": "module",
                "name": "user_module",
                "location": "Modules / user_module",
                "moduleName": "user_module",
                "scopeType": "module",
                "value": None,
                "entries": lambda: [("shallow_needle", 42)],
            },
        ]

        result = search_roots(self.inspector, roots, "shallow_needle", max_objects=2)

        match = next(item for item in result["items"] if item["name"] == "shallow_needle")
        self.assertEqual("Modules / user_module / shallow_needle", match["location"])
        self.assertEqual(2, result["rootsScanned"])

    def test_gc_root_exposes_tracked_objects_as_an_explicit_runtime_location(self):
        target = _NestedTarget()
        target.cycle = target

        root = _gc_root(1_000_000)
        entries = root["entries"]()

        self.assertEqual("gc", root["sourceKind"])
        self.assertEqual("GC-tracked objects", root["location"])
        self.assertTrue(any(value is target for _, value in entries))


if __name__ == "__main__":
    unittest.main()
