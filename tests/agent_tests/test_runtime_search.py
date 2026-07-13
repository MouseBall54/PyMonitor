import unittest
from unittest import mock

from pyruntime_inspector_agent import console_namespaces, frames, modules, runtime_search
from pyruntime_inspector_agent.console_namespaces import register_namespace, unregister_namespace
from pyruntime_inspector_agent.handles import HandleStore
from pyruntime_inspector_agent.runtime_search import (
    _console_roots,
    _gc_root,
    search_roots,
    search_runtime,
)
from pyruntime_inspector_agent.safe_objects import SafeObjectInspector
from pyruntime_inspector_agent.server import InspectorAgent


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

    def test_invalid_runtime_query_is_rejected_before_console_discovery(self):
        with mock.patch.object(
            console_namespaces,
            "_gc_objects_snapshot",
            side_effect=AssertionError("console discovery must not run"),
        ):
            with self.assertRaises(ValueError):
                search_runtime(self.inspector, self.handles, -1, "   ")

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

    def test_root_namespaces_are_sampled_round_robin_within_object_budget(self):
        pulls = {"large": 0, "later": 0}

        def entries(source, values):
            for item in values:
                pulls[source] += 1
                yield item

        roots = [
            {
                "sourceKind": "module",
                "name": "large",
                "location": "Modules / large",
                "moduleName": "large",
                "scopeType": "module",
                "value": None,
                "entries": lambda: entries("large", ((f"item_{index}", index) for index in range(100))),
            },
            {
                "sourceKind": "module",
                "name": "later",
                "location": "Modules / later",
                "moduleName": "later",
                "scopeType": "module",
                "value": None,
                "entries": lambda: entries("later", [("later_needle", 42)]),
            },
        ]

        result = search_roots(self.inspector, roots, "later_needle", max_objects=2)

        self.assertTrue(any(item["name"] == "later_needle" for item in result["items"]))
        self.assertEqual({"large": 1, "later": 1}, pulls)
        self.assertTrue(result["objectLimitReached"])

    def test_exhaustive_search_ignores_object_depth_and_child_limits(self):
        target = {"exhaustive_needle": 42}
        roots = self.root([object(), target])

        with mock.patch(
            "pyruntime_inspector_agent.runtime_search.MAX_CHILDREN_PER_OBJECT",
            1,
        ):
            result = search_roots(
                self.inspector,
                roots,
                "exhaustive_needle",
                max_objects=1,
                max_depth=0,
                exhaustive=True,
            )

        self.assertTrue(any(
            item["name"].endswith("'exhaustive_needle'")
            for item in result["items"]
        ))
        self.assertTrue(result["exhaustive"])
        self.assertIsNone(result["maxObjects"])
        self.assertIsNone(result["maxDepth"])
        self.assertTrue(result["scanComplete"])
        self.assertFalse(result["objectLimitReached"])
        self.assertFalse(result["depthLimitReached"])
        self.assertFalse(result["childrenTruncated"])

    def test_runtime_search_reports_incomplete_console_discovery(self):
        discovery = {
            "items": [],
            "scanComplete": False,
            "scannedCount": 10,
            "trackedTotal": 20,
            "truncated": True,
            "namespaceLimitReached": False,
        }
        with mock.patch.object(console_namespaces, "list_namespaces", return_value=discovery), \
                mock.patch.object(console_namespaces, "_gc_objects_snapshot", return_value=[]), \
                mock.patch.object(modules, "_module_registry", return_value=None), \
                mock.patch.object(frames, "_current_frames_snapshot", return_value={}):
            result = search_runtime(self.inspector, self.handles, -1, "missing")

        self.assertFalse(result["scanComplete"])
        self.assertFalse(result["consoleDiscoveryComplete"])
        self.assertTrue(result["consoleDiscoveryTruncated"])
        self.assertEqual(10, result["consoleDiscoveryScannedCount"])
        self.assertEqual(20, result["consoleDiscoveryTrackedTotal"])

    def test_gc_root_exposes_tracked_objects_as_an_explicit_runtime_location(self):
        target = _NestedTarget()
        target.cycle = target

        root = _gc_root(1_000_000)
        entries = root["entries"]()

        self.assertEqual("gc", root["sourceKind"])
        self.assertEqual("GC-tracked objects", root["location"])
        self.assertTrue(any(value is target for _, value in entries))

    def test_console_namespace_is_a_direct_search_root_with_variable_location(self):
        namespace = {"console_search_needle": 2468}
        registration_id = register_namespace("Embedded exec console", namespace)
        self.addCleanup(unregister_namespace, registration_id)

        with mock.patch.object(console_namespaces, "_gc_objects_snapshot", return_value=[]):
            roots = _console_roots(self.handles)
        result = search_roots(self.inspector, roots, "console_search_needle")

        match = next(item for item in result["items"] if item["name"] == "console_search_needle")
        self.assertEqual("variable", match["kind"])
        self.assertEqual("console", match["sourceKind"])
        self.assertEqual("console", match["scopeType"])
        self.assertIsNotNone(match["consoleHandle"])
        self.assertEqual("namespace", match["consoleAttributeName"])
        self.assertTrue(match["location"].startswith("Console namespaces / Embedded exec console @0x"))

    def test_console_value_does_not_execute_spoofed_class_property(self):
        class HostileValue:
            @property
            def __class__(self):
                raise AssertionError("runtime search must not read target __class__")

        namespace = {"hostile_console_value": HostileValue()}
        registration_id = register_namespace("Hostile console", namespace)
        self.addCleanup(unregister_namespace, registration_id)

        with mock.patch.object(console_namespaces, "_gc_objects_snapshot", return_value=[]):
            roots = _console_roots(self.handles)
        result = search_roots(self.inspector, roots, "hostile_console_value")

        self.assertTrue(any(item["name"] == "hostile_console_value" for item in result["items"]))

    def test_server_dispatches_exhaustive_runtime_search(self):
        agent = InspectorAgent("127.0.0.1", 1, "a" * 64, "cooperative")
        expected = {"items": []}

        with mock.patch.object(runtime_search, "search_runtime", return_value=expected) as search:
            result, binary, detach = agent._dispatch("runtime.search", {
                "query": "needle",
                "maxResults": 11,
                "maxObjects": 22,
                "maxDepth": 3,
                "exhaustive": True,
            })

        self.assertIs(expected, result)
        self.assertEqual(b"", binary)
        self.assertFalse(detach)
        search.assert_called_once_with(
            agent._objects,
            agent._handles,
            agent._thread.ident,
            "needle",
            11,
            22,
            3,
            True,
        )


if __name__ == "__main__":
    unittest.main()
