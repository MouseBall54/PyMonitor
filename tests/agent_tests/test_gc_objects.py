import unittest
from unittest import mock

from pyruntime_inspector_agent import gc_objects
from pyruntime_inspector_agent.handles import HandleStore
from pyruntime_inspector_agent.safe_objects import SafeObjectInspector


class GcObjectTests(unittest.TestCase):
    def setUp(self):
        Dangerous.attribute_reads = 0
        self.inspector = SafeObjectInspector(HandleStore(max_entries=64))

    def test_lists_sorted_gc_tracked_objects_with_pagination(self):
        values = [Zulu(), Alpha(), Dangerous()]
        with mock.patch.object(gc_objects.gc, "get_objects", return_value=values):
            first = gc_objects.list_objects(self.inspector, page_size=2)
            second = gc_objects.list_objects(self.inspector, offset=2, page_size=2)

        names = [item["name"] for item in first["items"] + second["items"]]
        self.assertEqual(sorted(names, key=str.casefold), names)
        self.assertEqual(3, first["total"])
        self.assertEqual(3, first["trackedTotal"])
        self.assertEqual(3, first["scannedCount"])
        self.assertFalse(first["truncated"])
        self.assertEqual("gc-tracked", first["scopeType"])

    def test_searches_type_module_and_address_without_executing_user_code(self):
        danger = Dangerous()
        address = hex(id(danger))
        with mock.patch.object(gc_objects.gc, "get_objects", return_value=[Alpha(), danger]):
            by_type = gc_objects.list_objects(self.inspector, query="dangerous")
            by_module = gc_objects.list_objects(self.inspector, query=__name__)
            by_address = gc_objects.list_objects(self.inspector, query=address)

        self.assertEqual([f"{__name__}.Dangerous"], [item["name"] for item in by_type["items"]])
        self.assertEqual(2, by_module["total"])
        self.assertEqual(1, by_address["total"])
        self.assertIn("Dangerous object", by_type["items"][0]["value"]["safePreview"])
        self.assertEqual(0, Dangerous.attribute_reads)

    def test_reports_scan_truncation_without_forcing_collection(self):
        values = [Alpha(), Zulu(), Dangerous()]
        with mock.patch.object(gc_objects.gc, "get_objects", return_value=values), \
                mock.patch.object(gc_objects.gc, "collect") as collect:
            result = gc_objects.list_objects(self.inspector, max_objects=2)

        self.assertEqual(3, result["trackedTotal"])
        self.assertEqual(2, result["scannedCount"])
        self.assertEqual(2, result["total"])
        self.assertTrue(result["truncated"])
        collect.assert_not_called()

    def test_rejects_invalid_query_paging_and_scan_bounds(self):
        invalid_calls = [
            {"query": None},
            {"query": "x" * 201},
            {"offset": -1},
            {"page_size": 0},
            {"page_size": gc_objects.MAX_PAGE_SIZE + 1},
            {"max_objects": 0},
            {"max_objects": gc_objects.MAX_OBJECTS + 1},
        ]
        for arguments in invalid_calls:
            with self.subTest(arguments=arguments), self.assertRaises(ValueError):
                gc_objects.list_objects(self.inspector, **arguments)


class Alpha:
    pass


class Zulu:
    pass


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
