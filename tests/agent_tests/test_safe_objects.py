import os
import subprocess
import sys
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
        summary = self.inspector.summarize(value)
        self.assertEqual("<test_safe_objects.Dangerous object>", summary["safePreview"])
        children = self.inspector.list_children(summary["handleId"], page_size=1000)
        member_names = {item["name"] for item in children["items"]}
        self.assertIn("dangerous_property", member_names)
        self.assertEqual(0, Dangerous.property_reads)

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
        self.assertEqual(0, Dangerous.property_reads)

    def test_pagination_and_expired_handles(self):
        summary = self.inspector.summarize(list(range(12)))
        page = self.inspector.list_children(summary["handleId"], offset=5, page_size=3)
        self.assertEqual(["[5]", "[6]", "[7]"], [item["name"] for item in page["items"]])
        self.assertTrue(self.handles.release(summary["handleId"]))
        with self.assertRaises(ObjectExpiredError):
            self.inspector.describe(summary["handleId"])

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
