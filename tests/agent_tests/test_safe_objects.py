import os
import json
import subprocess
import sys
import time
import types
import unittest

from pyruntime_inspector_agent import classes
from pyruntime_inspector_agent.handles import HandleStore, ObjectExpiredError
from pyruntime_inspector_agent.safe_objects import SafeObjectInspector


class ExplosiveDefault:
    def __repr__(self):
        raise AssertionError("default repr must not run")


EXPLOSIVE_DEFAULT = ExplosiveDefault()


class Dangerous:
    property_reads = 0

    def __repr__(self):
        raise AssertionError("repr must not run")

    @property
    def dangerous_property(self):
        type(self).property_reads += 1
        raise AssertionError("property must not run")

    @classmethod
    def from_config(cls, config):
        return cls()

    @staticmethod
    def normalize(value):
        return value

    def predict(self, image, threshold=0.5):
        return []

    def with_unsafe_default(self, value=EXPLOSIVE_DEFAULT):
        return value


class SafeObjectTests(unittest.TestCase):
    def setUp(self):
        Dangerous.property_reads = 0
        self.handles = HandleStore(max_entries=64)
        self.inspector = SafeObjectInspector(self.handles)

    def test_custom_object_never_runs_repr_or_property(self):
        value = Dangerous()
        value.threshold = 0.5
        summary = self.inspector.summarize(value)
        self.assertEqual("<test_safe_objects.Dangerous object>", summary["safePreview"])
        children = self.inspector.list_children(summary["handleId"], page_size=200)
        member_names = {item["name"] for item in children["items"]}
        self.assertIn("threshold", member_names)
        self.assertNotIn("dangerous_property", member_names)
        class_member_names = {item["name"] for item in classes.describe(value)["members"]}
        self.assertIn("dangerous_property", class_member_names)
        self.assertEqual(0, Dangerous.property_reads)

    def test_instance_fields_bypass_target_owned_dict_descriptor(self):
        class HostileDictDescriptor:
            calls = 0

            def __get__(self, instance, owner):
                type(self).calls += 1
                raise AssertionError("target-owned __dict__ descriptor must not run")

        class Host:
            __dict__ = HostileDictDescriptor()

        value = Host()
        value.answer = 42
        summary = self.inspector.summarize(value)
        children = self.inspector.list_children(summary["handleId"], page_size=200)

        self.assertEqual(["answer"], [item["name"] for item in children["items"]])
        self.assertEqual(0, HostileDictDescriptor.calls)

    def test_hostile_type_and_module_metadata_are_never_formatted(self):
        class HostileText:
            calls = 0

            def __format__(self, specification):
                type(self).calls += 1
                raise AssertionError("type metadata formatting must not run")

            def __str__(self):
                type(self).calls += 1
                raise AssertionError("type metadata string conversion must not run")

        class Host:
            pass

        hostile = HostileText()
        Host.__module__ = hostile
        summary = self.inspector.summarize(Host())
        description = classes.describe(Host())

        module = types.ModuleType("safe_name")
        module.__name__ = hostile
        module_summary = self.inspector.summarize(module)

        self.assertEqual("<unknown>", summary["moduleName"])
        self.assertTrue(summary["qualifiedTypeName"].startswith("<unknown>."))
        self.assertTrue(summary["qualifiedTypeName"].endswith(".Host"))
        self.assertEqual("<unknown>", description["module"])
        self.assertEqual("module <unnamed>", module_summary["safePreview"])
        self.assertEqual(0, HostileText.calls)

    def test_nested_qualified_type_name_and_oversized_metadata_are_bounded(self):
        class Outer:
            class Inner:
                pass

        nested = self.inspector.summarize(Outer.Inner())
        self.assertEqual(
            f"{__name__}.SafeObjectTests.test_nested_qualified_type_name_and_oversized_metadata_are_bounded.<locals>.Outer.Inner",
            nested["qualifiedTypeName"],
        )

        class Oversized:
            pass

        Oversized.__name__ = "N" * 1_100_000
        Oversized.__qualname__ = "Q" * 1_100_000
        Oversized.__module__ = "M" * 1_100_000
        summary = self.inspector.summarize(Oversized())

        self.assertLessEqual(len(summary["typeName"]), 257)
        self.assertLessEqual(len(summary["moduleName"]), 257)
        self.assertLessEqual(len(summary["qualifiedTypeName"]), 515)
        self.assertLessEqual(len(summary["safePreview"]), 525)
        self.assertTrue(summary["typeName"].endswith("…"))
        self.assertLess(len(json.dumps(summary)), 10_000)

        value = Oversized()
        value.__dict__["F" * 1_100_000] = 1
        child = self.inspector.list_children(
            self.inspector.summarize(value)["handleId"],
            page_size=10,
        )["items"][0]
        self.assertLessEqual(len(child["name"]), 513)
        self.assertEqual(child["name"], child["pathSegment"])

    def test_classifies_static_members_without_reading_property(self):
        description = classes.describe(Dangerous())
        kinds = {member["name"]: member["kind"] for member in description["members"]}
        self.assertEqual("property", kinds["dangerous_property"])
        self.assertEqual("classmethod", kinds["from_config"])
        self.assertEqual("staticmethod", kinds["normalize"])
        self.assertEqual("instance method", kinds["predict"])
        signature = next(member["signature"] for member in description["members"] if member["name"] == "predict")
        self.assertEqual("(self, image, threshold=0.5)", signature)
        unsafe_signature = next(member["signature"] for member in description["members"] if member["name"] == "with_unsafe_default")
        self.assertEqual("(self, value=<default>)", unsafe_signature)
        predict = next(member for member in description["members"] if member["name"] == "predict")
        self.assertEqual("methods", predict["group"])
        self.assertFalse(predict["inherited"])
        self.assertEqual(
            ["positionalOrKeyword", "positionalOrKeyword", "positionalOrKeyword"],
            [parameter["kind"] for parameter in predict["parameters"]],
        )
        self.assertEqual(0.5, float(predict["parameters"][2]["defaultPreview"]))
        self.assertEqual(__file__, predict["source"]["file"])
        self.assertGreater(predict["source"]["line"], 0)
        self.assertEqual(0, Dangerous.property_reads)

    def test_classifies_builtin_function_method_and_data_descriptors(self):
        class DataDescriptor:
            def __get__(self, instance, owner=None):
                raise AssertionError("descriptor must not run")

            def __set__(self, instance, value):
                raise AssertionError("descriptor must not run")

        class Host:
            builtin_function = len
            method_descriptor = list.append
            data_descriptor = DataDescriptor()

        kinds = {member["name"]: member["kind"] for member in classes.describe(Host())["members"]}
        self.assertEqual("function", kinds["builtin_function"])
        self.assertEqual("method descriptor", kinds["method_descriptor"])
        self.assertEqual("data descriptor", kinds["data_descriptor"])

    def test_signature_does_not_inspect_malicious_wrapped_callable(self):
        attribute_reads = []

        class HostileCallable:
            armed = False

            def __call__(self):
                return None

            def __getattribute__(self, name):
                if name != "armed" and object.__getattribute__(self, "armed"):
                    attribute_reads.append(name)
                    raise AssertionError(f"attribute access must not run: {name}")
                return object.__getattribute__(self, name)

        callable_value = HostileCallable()
        wrapped = staticmethod(callable_value)
        object.__setattr__(callable_value, "armed", True)

        class Host:
            unsafe = wrapped

        member = next(item for item in classes.describe(Host())["members"] if item["name"] == "unsafe")
        self.assertEqual("staticmethod", member["kind"])
        self.assertIsNone(member["signature"])
        self.assertEqual([], member["parameters"])
        self.assertEqual([], attribute_reads)

    def test_class_description_is_bounded_and_marks_inherited_members(self):
        namespace = {f"field_{index}": index for index in range(500)}
        huge = type("Huge", (), namespace)
        description = classes.describe(huge())

        self.assertEqual(classes.MAX_CLASS_MEMBERS, len(description["members"]))
        self.assertTrue(description["membersTruncated"])
        self.assertGreater(description["memberTotal"], len(description["members"]))
        inherited = classes.describe(Dangerous())["members"]
        self.assertTrue(next(member for member in inherited if member["name"] == "__str__")["inherited"])

    def test_exact_function_signature_ignores_custom_signature_attribute(self):
        class HostileSignature:
            def __getattribute__(self, name):
                raise AssertionError(f"custom signature must not be inspected: {name}")

        def function(value: "Model", /, option=1, *items, flag: bool = False, **kwargs) -> "Result":
            return value, option, items, flag, kwargs

        function.__signature__ = HostileSignature()

        class Host:
            method = function

        member = next(item for item in classes.describe(Host())["members"] if item["name"] == "method")
        self.assertEqual(
            ["positionalOnly", "positionalOrKeyword", "varPositional", "keywordOnly", "varKeyword"],
            [parameter["kind"] for parameter in member["parameters"]],
        )
        if sys.version_info >= (3, 14):
            self.assertIsNone(member["parameters"][0]["annotationText"])
            self.assertIsNone(member["signatureDetails"]["returnAnnotation"])
        else:
            self.assertEqual("Model", member["parameters"][0]["annotationText"])
            self.assertEqual("Result", member["signatureDetails"]["returnAnnotation"])

    def test_pagination_and_expired_handles(self):
        summary = self.inspector.summarize(list(range(12)))
        page = self.inspector.list_children(summary["handleId"], offset=5, page_size=3)
        self.assertEqual(["[5]", "[6]", "[7]"], [item["name"] for item in page["items"]])
        self.assertTrue(self.handles.release(summary["handleId"]))
        with self.assertRaises(ObjectExpiredError):
            self.inspector.describe(summary["handleId"])

    def test_handle_ttl_and_lru_eviction_are_enforced(self):
        ttl_store = HandleStore(max_entries=2, ttl_seconds=0.01)
        expired = ttl_store.put(["ttl"])
        time.sleep(0.05)
        with self.assertRaises(ObjectExpiredError):
            ttl_store.get(expired)
        self.assertEqual(0, len(ttl_store))

        lru_store = HandleStore(max_entries=2, ttl_seconds=30)
        first_value, second_value, third_value = [1], [2], [3]
        first = lru_store.put(first_value)
        second = lru_store.put(second_value)
        self.assertIs(first_value, lru_store.get(first))
        third = lru_store.put(third_value)
        with self.assertRaises(ObjectExpiredError):
            lru_store.get(second)
        self.assertIs(first_value, lru_store.get(first))
        self.assertIs(third_value, lru_store.get(third))

    def test_maximal_child_page_keeps_every_returned_handle_live(self):
        handles = HandleStore(max_entries=512)
        inspector = SafeObjectInspector(handles)
        root = inspector.summarize(list(range(500)))
        page = inspector.list_children(root["handleId"], page_size=200)

        self.assertEqual(200, len(page["items"]))
        self.assertTrue(page["hasMore"])
        for item in page["items"]:
            self.assertIsNotNone(handles.get(item["value"]["handleId"]))
        with self.assertRaises(ValueError):
            inspector.list_children(root["handleId"], page_size=201)

    def test_handles_are_reused_and_metadata_detects_safe_mutation(self):
        value = []
        before = self.inspector.summarize(value)
        value.append(1)
        after = self.inspector.summarize(value)

        self.assertEqual(before["handleId"], after["handleId"])
        self.assertEqual(before["identityToken"], after["identityToken"])
        self.assertNotEqual(before["metadataToken"], after["metadataToken"])
        self.assertNotEqual(before["changeToken"], after["changeToken"])

        same_length = [1, 2, 3]
        before_same_length = self.inspector.summarize(same_length)
        same_length[0] = 9
        after_same_length = self.inspector.summarize(same_length)
        self.assertEqual(before_same_length["identityToken"], after_same_length["identityToken"])
        self.assertNotEqual(before_same_length["metadataToken"], after_same_length["metadataToken"])

        class MutableObject:
            pass

        mutable = MutableObject()
        mutable.status = "before"
        before_field = self.inspector.summarize(mutable)
        mutable.status = "after"
        after_field = self.inspector.summarize(mutable)
        self.assertEqual(before_field["identityToken"], after_field["identityToken"])
        self.assertNotEqual(before_field["metadataToken"], after_field["metadataToken"])

    def test_cv_image_in_place_changes_update_metadata_token(self):
        try:
            import numpy
        except ImportError:
            self.skipTest("NumPy is not installed.")

        image = numpy.zeros((240, 320, 3), dtype=numpy.uint8)
        before = self.inspector.summarize(image)
        image[40:43, 40:281, 1] = 255
        image[198:201, 40:281, 1] = 255
        after = self.inspector.summarize(image)

        self.assertEqual(before["identityToken"], after["identityToken"])
        self.assertNotEqual(before["metadataToken"], after["metadataToken"])
        self.assertNotEqual(before["changeToken"], after["changeToken"])

    def test_large_integer_and_large_container_previews_are_bounded(self):
        integer = self.inspector.summarize(10 ** 5000)
        container = self.inspector.summarize(list(range(100_000)))

        self.assertIn("bits=", integer["safePreview"])
        self.assertLess(len(integer["safePreview"]), 80)
        self.assertLess(len(container["safePreview"]), 200)
        self.assertIn("…", container["safePreview"])

    def test_cycle_and_depth_metadata_support_bounded_navigation(self):
        first = []
        second = [first]
        first.append(second)
        root = self.inspector.summarize(first)
        first_page = self.inspector.list_children(
            root["handleId"],
            depth=0,
            ancestor_identity_tokens=[root["identityToken"]],
        )
        second_summary = first_page["items"][0]["value"]
        second_page = self.inspector.list_children(
            second_summary["handleId"],
            depth=1,
            ancestor_identity_tokens=[root["identityToken"], second_summary["identityToken"]],
        )
        cycle = second_page["items"][0]

        self.assertTrue(cycle["isCycle"])
        self.assertEqual(0, cycle["cycleToDepth"])
        self.assertFalse(cycle["canExpand"])
        with self.assertRaises(ValueError):
            self.inspector.list_children(root["handleId"], depth=32)

    def test_ten_thousand_inspect_release_cycles_stay_bounded(self):
        for value in range(10_000):
            handle = self.inspector.summarize(value)["handleId"]
            self.handles.release(handle)
        self.assertLessEqual(len(self.handles), 64)

    def test_importing_agent_does_not_import_numpy(self):
        env = dict(os.environ)
        output = subprocess.check_output(
            [sys.executable, "-c", "import sys; import pyruntime_inspector_agent; print('numpy' in sys.modules)"],
            env=env,
            text=True,
        )
        self.assertEqual("False", output.strip())
