import code
import gc
import sys
import types
import unittest
from unittest import mock

from pyruntime_inspector_agent import register_namespace, unregister_namespace
from pyruntime_inspector_agent import console_namespaces
from pyruntime_inspector_agent.console_namespaces import list_namespace, list_namespaces
from pyruntime_inspector_agent.handles import HandleStore, ObjectExpiredError
from pyruntime_inspector_agent.safe_objects import SafeObjectInspector


class ConsoleNamespaceTests(unittest.TestCase):
    def setUp(self):
        self.handles = HandleStore()
        self.inspector = SafeObjectInspector(self.handles)

    def test_interactive_console_variables_declared_after_discovery_are_visible(self):
        console = code.InteractiveConsole()
        with mock.patch.object(console_namespaces, "_gc_objects_snapshot", return_value=[console]):
            listed = list_namespaces(self.handles)
        row = self._row_for_mapping(listed, console.locals)

        before = list_namespace(
            self.handles,
            self.inspector,
            row["consoleHandle"],
            row["attributeName"],
            page_size=200,
        )
        self.assertNotIn("terminal_declared_value", {item["name"] for item in before["items"]})

        self.assertFalse(console.push("terminal_declared_value = {'phase': 2}"))
        after = list_namespace(
            self.handles,
            self.inspector,
            row["consoleHandle"],
            row["attributeName"],
            page_size=200,
        )

        value = next(item["value"] for item in after["items"] if item["name"] == "terminal_declared_value")
        self.assertEqual("dict", value["typeName"])
        self.assertEqual("console", after["scopeType"])
        self.assertEqual(row["consoleHandle"], after["consoleHandle"])

        console.locals = {"replacement_value": 99}
        replaced = list_namespace(
            self.handles,
            self.inspector,
            row["consoleHandle"],
            row["attributeName"],
            page_size=200,
        )
        self.assertEqual(["replacement_value"], [item["name"] for item in replaced["items"]])

    def test_recognizable_custom_terminal_namespace_is_discovered_without_attribute_access(self):
        reads = []

        class HostileMeta(type):
            def __hash__(cls):
                raise AssertionError("console discovery must not hash target classes")

            def __eq__(cls, other):
                raise AssertionError("console discovery must not compare target classes")

        class EmbeddedTerminal(metaclass=HostileMeta):
            def __init__(self):
                self.namespace = {"custom_terminal_value": 81}

            def __getattribute__(self, name):
                if name == "namespace":
                    reads.append(name)
                    raise AssertionError("console discovery must not invoke target attribute access")
                return object.__getattribute__(self, name)

        terminal = EmbeddedTerminal()
        mapping = object.__getattribute__(terminal, "__dict__")["namespace"]

        with mock.patch.object(console_namespaces, "_gc_objects_snapshot", return_value=[terminal]):
            listed = list_namespaces(self.handles)
        row = self._row_for_mapping(listed, mapping)

        self.assertEqual("custom", row["kind"])
        self.assertEqual("namespace", row["attributeName"])
        self.assertEqual([], reads)

    def test_namespace_field_lookup_does_not_compare_target_dictionary_keys(self):
        comparisons = []

        class EvilKey:
            def __hash__(self):
                return hash("locals")

            def __eq__(self, other):
                comparisons.append(other)
                raise AssertionError("console discovery must not compare target dictionary keys")

        class EmbeddedTerminal:
            def __init__(self):
                self.namespace = {"safe_value": 42}
                self.__dict__[EvilKey()] = "decoy"

        terminal = EmbeddedTerminal()
        with mock.patch.object(console_namespaces, "_gc_objects_snapshot", return_value=[terminal]):
            listed = list_namespaces(self.handles)

        self.assertEqual("namespace", listed["items"][0]["attributeName"])
        self.assertEqual([], comparisons)

    def test_unrelated_namespace_field_is_not_reported_as_a_console(self):
        class WorkerState:
            def __init__(self):
                self.namespace = {"not_a_console": True}

        state = WorkerState()
        mapping = state.namespace

        with mock.patch.object(console_namespaces, "_gc_objects_snapshot", return_value=[state]):
            listed = list_namespaces(self.handles)

        self.assertFalse(any(
            object.__getattribute__(self.handles.get(item["consoleHandle"]), "__dict__").get(
                item["attributeName"]
            ) is mapping
            for item in listed["items"]
        ))

    def test_separate_exec_dictionary_can_be_registered_and_unregistered(self):
        namespace = {}
        exec("exec_terminal_value = 1357", namespace)
        registration_id = register_namespace("Internal exec terminal", namespace)
        self.addCleanup(unregister_namespace, registration_id)

        with mock.patch.object(console_namespaces, "_gc_objects_snapshot", return_value=[]):
            listed = list_namespaces(self.handles)
        row = next(item for item in listed["items"] if item["kind"] == "registered")
        scope = list_namespace(
            self.handles,
            self.inspector,
            row["consoleHandle"],
            row["attributeName"],
            page_size=200,
        )

        value = next(item["value"] for item in scope["items"] if item["name"] == "exec_terminal_value")
        self.assertEqual("1357", value["safePreview"])
        owner = self.handles.get(row["consoleHandle"])
        self.assertTrue(unregister_namespace(registration_id))
        with mock.patch.object(console_namespaces, "_gc_objects_snapshot", return_value=[owner]):
            self.assertEqual([], list_namespaces(self.handles)["items"])
        with self.assertRaises((ValueError, ObjectExpiredError)):
            list_namespace(
                self.handles,
                self.inspector,
                row["consoleHandle"],
                row["attributeName"],
            )

    def test_ipython_user_namespace_uses_static_backing_field_with_custom_metaclass(self):
        module_name = "IPython.core.interactiveshell"
        prior_module = sys.modules.get(module_name)
        fake_module = types.ModuleType(module_name)

        class ShellMeta(type):
            pass

        class InteractiveShell(metaclass=ShellMeta):
            def __init__(self):
                self._user_ns = {"ipython_terminal_value": 21}

            @property
            def user_ns(self):
                raise AssertionError("console discovery must not execute IPython properties")

        fake_module.InteractiveShell = InteractiveShell
        sys.modules[module_name] = fake_module
        self.addCleanup(self._restore_module, module_name, prior_module)
        shell = InteractiveShell()

        with mock.patch.object(console_namespaces, "_gc_objects_snapshot", return_value=[shell]):
            listed = list_namespaces(self.handles)
        row = self._row_for_mapping(listed, shell._user_ns)

        self.assertEqual("ipython", row["kind"])
        self.assertEqual("_user_ns", row["attributeName"])
        self.assertTrue(row["displayName"].endswith(".user_ns"))

    def test_replaced_console_type_symbol_does_not_execute_spoofed_class_property(self):
        module_namespace = types.ModuleType.__getattribute__(code, "__dict__")
        original = dict.get(module_namespace, "InteractiveConsole")

        class HostileSymbol:
            @property
            def __class__(self):
                raise AssertionError("console type discovery must not read target __class__")

        code.InteractiveConsole = HostileSymbol()
        self.addCleanup(setattr, code, "InteractiveConsole", original)

        with mock.patch.object(console_namespaces, "_gc_objects_snapshot", return_value=[]):
            listed = list_namespaces(self.handles)

        self.assertEqual([], listed["items"])

    def test_scan_and_page_limits_are_validated(self):
        with self.assertRaises(ValueError):
            list_namespaces(self.handles, max_objects=0)
        with self.assertRaises(ValueError):
            list_namespace(self.handles, self.inspector, "not-a-handle", "locals", page_size=201)

        console = code.InteractiveConsole()
        with mock.patch.object(
            console_namespaces,
            "_gc_objects_snapshot",
            return_value=[object(), console],
        ):
            bounded = list_namespaces(self.handles, max_objects=1)
        self.assertEqual(2, bounded["trackedTotal"])
        self.assertEqual(1, bounded["scannedCount"])
        self.assertTrue(bounded["truncated"])
        self.assertFalse(bounded["scanComplete"])

    def test_replaced_gc_snapshot_function_is_rejected_without_execution(self):
        calls = []

        def hostile_get_objects():
            calls.append(True)
            raise AssertionError("replaced gc.get_objects must not execute")

        with mock.patch.object(gc, "get_objects", hostile_get_objects):
            with self.assertRaisesRegex(ValueError, "GC snapshot function"):
                list_namespaces(self.handles)
        self.assertEqual([], calls)

    def _row_for_mapping(self, result, mapping):
        return next(
            item
            for item in result["items"]
            if object.__getattribute__(self.handles.get(item["consoleHandle"]), "__dict__").get(
                item["attributeName"]
            ) is mapping
        )

    @staticmethod
    def _restore_module(name, module):
        if module is None:
            sys.modules.pop(name, None)
        else:
            sys.modules[name] = module


if __name__ == "__main__":
    unittest.main()
