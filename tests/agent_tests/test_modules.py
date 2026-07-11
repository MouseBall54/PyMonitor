import sys
import types
import unittest

from pyruntime_inspector_agent import modules
from pyruntime_inspector_agent.handles import HandleStore
from pyruntime_inspector_agent.safe_objects import SafeObjectInspector


class ModuleTests(unittest.TestCase):
    def setUp(self):
        Dangerous.attribute_reads = 0
        self.module_name = "pyruntime_inspector_test_module"
        self.module = types.ModuleType(self.module_name)
        self.module.__dict__.clear()
        self.module.example_value = 1235
        self.module.edd = 121
        self.module._danger = Dangerous()
        sys.modules[self.module_name] = self.module
        self.addCleanup(sys.modules.pop, self.module_name, None)
        self.inspector = SafeObjectInspector(HandleStore())

    def test_lists_loaded_modules_without_importing_new_modules(self):
        before = set(sys.modules)
        result = modules.list_modules(page_size=1000)
        self.assertEqual(before, set(sys.modules))
        row = next(item for item in result["items"] if item["name"] == self.module_name)
        self.assertEqual(len(self.module.__dict__), row["entryCount"])
        self.assertFalse(row["isMain"])

    def test_lists_idle_module_namespace_with_pagination(self):
        first = modules.list_namespace(self.inspector, self.module_name, page_size=2)
        second = modules.list_namespace(self.inspector, self.module_name, offset=2, page_size=2)
        names = [item["name"] for item in first["items"] + second["items"]]
        self.assertEqual(
            sorted(self.module.__dict__, key=lambda name: (name.casefold(), name)),
            names,
        )
        value = next(item["value"] for item in second["items"] if item["name"] == "example_value")
        self.assertEqual("1235", value["safePreview"])

    def test_module_namespace_does_not_execute_repr_or_attributes(self):
        result = modules.list_namespace(self.inspector, self.module_name, page_size=100)
        danger = next(item["value"] for item in result["items"] if item["name"] == "_danger")
        self.assertIn("Dangerous object", danger["safePreview"])
        self.assertEqual(0, Dangerous.attribute_reads)

    def test_rejects_missing_or_non_module_entries(self):
        with self.assertRaises(ValueError):
            modules.list_namespace(self.inspector, "missing")
        sys.modules[self.module_name] = object()
        with self.assertRaises(ValueError):
            modules.list_namespace(self.inspector, self.module_name)


class Dangerous:
    attribute_reads = 0

    def __repr__(self):
        raise AssertionError("repr must not run")

    def __getattribute__(self, name):
        if name not in {"attribute_reads", "__class__"}:
            type(self).attribute_reads += 1
            raise AssertionError("attribute access must not run")
        return object.__getattribute__(self, name)


if __name__ == "__main__":
    unittest.main()
