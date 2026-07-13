import sys
import threading
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
        self.assertEqual("main-then-insertion", result["ordering"])
        self.assertTrue(result["scanComplete"])
        self.assertTrue(result["totalIsExact"])
        self.assertFalse(result["mutationDetected"])
        row = next(item for item in result["items"] if item["name"] == self.module_name)
        self.assertEqual(len(self.module.__dict__), row["entryCount"])
        self.assertTrue(row["entryCountIsExact"])
        self.assertFalse(row["isMain"])

    def test_module_registry_dict_subclass_bypasses_overrides(self):
        original_registry = sys.modules
        registry = HostileModuleRegistry(original_registry)
        sys.modules = registry
        try:
            listed = modules.list_modules(page_size=1000)
            namespace = modules.list_namespace(self.inspector, self.module_name, page_size=10)
        finally:
            sys.modules = original_registry

        self.assertEqual([], registry.override_calls)
        self.assertTrue(listed["scanComplete"])
        self.assertIn(self.module_name, {item["name"] for item in listed["items"]})
        self.assertIn("example_value", {item["name"] for item in namespace["items"]})

    def test_module_entries_ignore_non_string_keys_without_equality(self):
        hostile_name = HostileModuleName()
        registry = {
            "__main__": sys.modules["__main__"],
            hostile_name: self.module,
            self.module_name: self.module,
        }

        entries = list(modules._module_entries(registry))

        self.assertEqual(["__main__", self.module_name], [name for name, _ in entries])
        self.assertEqual(0, hostile_name.equality_calls)

    def test_non_dict_module_registry_returns_structured_argument_error(self):
        original_registry = sys.modules
        sys.modules = object()
        try:
            with self.assertRaisesRegex(ValueError, "module registry"):
                modules.list_modules(page_size=1000)
            with self.assertRaisesRegex(ValueError, "module registry"):
                modules.list_namespace(self.inspector, self.module_name)
        finally:
            sys.modules = original_registry

    def test_lists_idle_module_namespace_with_pagination(self):
        first = modules.list_namespace(self.inspector, self.module_name, page_size=2)
        second = modules.list_namespace(self.inspector, self.module_name, offset=2, page_size=2)
        names = [item["name"] for item in first["items"] + second["items"]]
        self.assertEqual(list(self.module.__dict__), names)
        self.assertEqual("insertion", first["ordering"])
        value = next(
            item["value"]
            for item in first["items"] + second["items"]
            if item["name"] == "example_value"
        )
        self.assertEqual("1235", value["safePreview"])

    def test_large_module_namespace_retains_only_insertion_order_page(self):
        module_name = "pyruntime_inspector_large_module"
        module = types.ModuleType(module_name)
        namespace = types.ModuleType.__getattribute__(module, "__dict__")
        namespace.clear()
        for index in range(20_000):
            namespace[f"value_{20_000 - index:05}"] = index
        sys.modules[module_name] = module
        self.addCleanup(sys.modules.pop, module_name, None)
        offset = 19_990

        result = modules.list_namespace(
            self.inspector,
            module_name,
            offset=offset,
            page_size=10,
        )

        self.assertEqual(20_000, result["total"])
        self.assertEqual("insertion", result["ordering"])
        self.assertEqual(
            [f"value_{20_000 - index:05}" for index in range(offset, offset + 10)],
            [item["name"] for item in result["items"]],
        )

    def test_sustained_module_namespace_key_mutation_returns_retry_error(self):
        namespace = types.ModuleType.__getattribute__(self.module, "__dict__")
        requests = [threading.Event(), threading.Event()]
        completed = [threading.Event(), threading.Event()]
        worker_errors = []

        def mutate_namespace():
            for attempt_index in range(2):
                if not requests[attempt_index].wait(2):
                    worker_errors.append(f"scan {attempt_index} was not reached")
                    return
                namespace[f"concurrent_{attempt_index}"] = attempt_index
                completed[attempt_index].set()

        worker = threading.Thread(target=mutate_namespace)
        worker.start()
        signaled_attempts = set()
        started_attempts = set()
        scan_code = modules._bounded_scan.__code__

        def trace_scan(frame, event, argument):
            if frame.f_code is scan_code and event == "line":
                scan_locals = frame.f_locals
                attempt_index = _trace_local(scan_locals, "attempt_index")
                total = _trace_local(scan_locals, "total")
                if attempt_index in (0, 1) and total == 0:
                    started_attempts.add(attempt_index)
                if (
                    attempt_index in started_attempts
                    and total == 1
                    and attempt_index not in signaled_attempts
                ):
                    signaled_attempts.add(attempt_index)
                    requests[attempt_index].set()
                    if not completed[attempt_index].wait(2):
                        raise AssertionError(f"concurrent mutation {attempt_index} timed out")
            return trace_scan

        previous_trace = sys.gettrace()
        sys.settrace(trace_scan)
        try:
            with self.assertRaisesRegex(ValueError, "namespace changed during inspection"):
                modules.list_namespace(self.inspector, self.module_name, page_size=10)
        finally:
            sys.settrace(previous_trace)
            for request in requests:
                request.set()
            worker.join(2)

        self.assertFalse(worker.is_alive())
        self.assertEqual([], worker_errors)
        self.assertEqual({0, 1}, signaled_attempts)

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

    def test_namespace_pages_are_capped_without_limiting_module_metadata_pages(self):
        with self.assertRaises(ValueError):
            modules.list_namespace(self.inspector, self.module_name, page_size=201)
        modules.list_modules(page_size=1000)

    def test_volatile_module_entry_count_does_not_abort_module_listing(self):
        original_scan = modules._bounded_scan

        def fail_only_entry_count(entries_factory, offset, page_size):
            if page_size == 0:
                raise ValueError("The namespace changed during inspection; retry the request.")
            return original_scan(entries_factory, offset, page_size)

        modules._bounded_scan = fail_only_entry_count
        try:
            result = modules.list_modules(page_size=1000)
        finally:
            modules._bounded_scan = original_scan

        row = next(item for item in result["items"] if item["name"] == self.module_name)
        self.assertFalse(row["entryCountIsExact"])
        self.assertGreaterEqual(row["entryCount"], 1)
        self.assertTrue(result["mutationDetected"])

    def test_module_namespace_bounds_oversized_variable_names(self):
        long_name = "x" * 1_100_000
        self.module.__dict__[long_name] = 7
        result = modules.list_namespace(self.inspector, self.module_name, page_size=10)
        row = next(item for item in result["items"] if item["value"]["safePreview"] == "7")
        self.assertLessEqual(len(row["name"]), 513)


class Dangerous:
    attribute_reads = 0

    def __repr__(self):
        raise AssertionError("repr must not run")

    def __getattribute__(self, name):
        if name not in {"attribute_reads", "__class__"}:
            type(self).attribute_reads += 1
            raise AssertionError("attribute access must not run")
        return object.__getattribute__(self, name)


class HostileModuleRegistry(dict):
    def __init__(self, source):
        super().__init__(source)
        self.override_calls = []

    def get(self, *args, **kwargs):
        self.override_calls.append("get")
        raise AssertionError("sys.modules.get must not be called")

    def items(self):
        self.override_calls.append("items")
        raise AssertionError("sys.modules.items must not be called")


class HostileModuleName:
    def __init__(self):
        self.equality_calls = 0

    __hash__ = object.__hash__

    def __eq__(self, other):
        self.equality_calls += 1
        raise AssertionError("non-string module keys must not be compared")


def _trace_local(mapping, name):
    try:
        return mapping[name]
    except KeyError:
        return None


if __name__ == "__main__":
    unittest.main()
