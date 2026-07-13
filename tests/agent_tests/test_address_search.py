import gc
import os
import subprocess
import struct
import sys
import textwrap
import time
import types
import unittest
from unittest import mock

from pyruntime_inspector_agent import address_search, console_namespaces
from pyruntime_inspector_agent.handles import HandleStore
from pyruntime_inspector_agent.safe_objects import SafeObjectInspector
from pyruntime_inspector_agent.server import InspectorAgent


class _Target:
    pass


class AddressSearchTests(unittest.TestCase):
    def setUp(self):
        self.handles = HandleStore()
        self.inspector = SafeObjectInspector(self.handles)

    @staticmethod
    def root(entries):
        return [{
            "sourceKind": "module",
            "name": "address_demo",
            "location": "Modules / address_demo",
            "moduleName": "address_demo",
            "scopeType": "module",
            "value": None,
            "entries": lambda: entries,
        }]

    @staticmethod
    def discovery():
        return {
            "items": [],
            "scanComplete": True,
            "scannedCount": 0,
            "trackedTotal": 0,
            "truncated": False,
            "namespaceLimitReached": False,
        }

    @classmethod
    def root_metadata(cls):
        return {
            "consoleDiscovery": cls.discovery(),
            "frameRootsIncluded": 0,
            "frameRootLimitReached": False,
        }

    def find(self, target, entries=(), gc_snapshot=(), **limits):
        roots = self.root(entries)
        discovery = self.discovery()
        metadata = self.root_metadata()
        with mock.patch.object(
            console_namespaces,
            "_gc_objects_snapshot",
            return_value=list(gc_snapshot),
        ), mock.patch.object(
            console_namespaces,
            "list_namespaces",
            return_value=discovery,
        ), mock.patch.object(
            address_search.runtime_search,
            "_runtime_roots",
            return_value=(roots, metadata),
        ):
            return address_search.find_address(
                self.inspector,
                self.handles,
                -1,
                hex(id(target)),
                **limits,
            )

    def test_accepts_trimmed_uppercase_prefix_and_returns_canonical_address(self):
        target = _Target()

        with mock.patch.object(
            console_namespaces,
            "_gc_objects_snapshot",
            return_value=[],
        ), mock.patch.object(
            console_namespaces,
            "list_namespaces",
            return_value=self.discovery(),
        ), mock.patch.object(
            address_search.runtime_search,
            "_runtime_roots",
            return_value=(self.root([("target", target)]), self.root_metadata()),
        ):
            result = address_search.find_address(
                self.inspector,
                self.handles,
                -1,
                f"  0X{id(target):X}  ",
            )

        self.assertEqual(hex(id(target)), result["addressHex"])
        self.assertEqual("address", result["mode"])
        self.assertTrue(result["targetFound"])

        with mock.patch.object(
            console_namespaces,
            "_gc_objects_snapshot",
            return_value=[],
        ), mock.patch.object(
            console_namespaces,
            "list_namespaces",
            return_value=self.discovery(),
        ), mock.patch.object(
            address_search.runtime_search,
            "_runtime_roots",
            return_value=(self.root([("target", target)]), self.root_metadata()),
        ):
            leading_zero_result = address_search.find_address(
                self.inspector,
                self.handles,
                -1,
                "0x0000000000000000" + f"{id(target):x}",
            )
        self.assertEqual(hex(id(target)), leading_zero_result["addressHex"])

    def test_rejects_invalid_zero_and_pointer_overflow_addresses_before_gc_scan(self):
        pointer_max = (1 << (struct.calcsize("P") * 8)) - 1
        invalid = (
            None,
            123,
            "",
            "1234",
            "0x",
            "0x0",
            "-0x1",
            "+0x1",
            "0x1 trailing",
            "0x1_000",
            hex(pointer_max + 1),
        )
        with mock.patch.object(
            console_namespaces,
            "_gc_objects_snapshot",
            side_effect=AssertionError("invalid addresses must be rejected before GC scanning"),
        ):
            for address in invalid:
                with self.subTest(address=address), self.assertRaises(ValueError):
                    address_search.find_address(
                        self.inspector,
                        self.handles,
                        -1,
                        address,
                    )

    def test_address_matching_is_exact_not_prefix_or_preview_text(self):
        target = _Target()
        lookalike = f"value contains {hex(id(target))}"

        result = self.find(target, [("lookalike", lookalike)])

        self.assertFalse(result["targetFound"])
        self.assertEqual([], result["items"])

    def test_finds_module_variable_with_global_search_compatible_row(self):
        target = _Target()

        result = self.find(target, [("module_target", target)])

        item = result["items"][0]
        self.assertEqual("variable", item["kind"])
        self.assertEqual("moduleVariable", item["relation"])
        self.assertEqual("variable", item["targetKind"])
        self.assertEqual("module_target", item["name"])
        self.assertEqual("Modules / address_demo / module_target", item["location"])
        self.assertEqual(item["location"], item["objectPath"])
        self.assertEqual(["address"], item["matchFields"])
        self.assertEqual("address_demo", item["moduleName"])
        self.assertEqual(hex(id(target)), item["value"]["addressHex"])
        self.assertFalse(result["snapshotAllocationBounded"])
        self.assertEqual(address_search.DEFAULT_MAX_EDGES, result["maxEdges"])
        self.assertEqual(
            address_search.DEFAULT_MAX_DURATION_MS,
            result["maxDurationMilliseconds"],
        )

    def test_marks_frame_and_console_namespace_variables_with_source_metadata(self):
        target = _Target()
        roots = [
            {
                "sourceKind": "frame",
                "name": "worker",
                "location": "Threads / 7 / worker / Locals",
                "frameHandle": "frame-handle",
                "scopeType": "locals",
                "value": None,
                "entries": lambda: [("frame_target", target)],
            },
            {
                "sourceKind": "console",
                "name": "Embedded console",
                "location": "Console namespaces / Embedded console @0x1",
                "consoleHandle": "console-handle",
                "consoleAttributeName": "namespace",
                "scopeType": "console",
                "value": None,
                "entries": lambda: [("console_target", target)],
            },
        ]
        with mock.patch.object(
            console_namespaces,
            "_gc_objects_snapshot",
            return_value=[],
        ), mock.patch.object(
            console_namespaces,
            "list_namespaces",
            return_value=self.discovery(),
        ), mock.patch.object(
            address_search.runtime_search,
            "_runtime_roots",
            return_value=(roots, self.root_metadata()),
        ):
            result = address_search.find_address(
                self.inspector,
                self.handles,
                -1,
                hex(id(target)),
            )

        by_relation = {item["relation"]: item for item in result["items"]}
        self.assertEqual("frame-handle", by_relation["frameVariable"]["frameHandle"])
        self.assertEqual("locals", by_relation["frameVariable"]["scopeType"])
        self.assertEqual("console-handle", by_relation["consoleVariable"]["consoleHandle"])
        self.assertEqual("namespace", by_relation["consoleVariable"]["consoleAttributeName"])

    def test_finds_nested_collection_and_exact_dict_key_and_value_edges(self):
        target = _Target()
        graph = [
            {target: "as-key"},
            {"as-value": target},
        ]

        result = self.find(target, [("graph", graph)])

        relations = {item["relation"] for item in result["items"]}
        self.assertIn("dictKey", relations)
        self.assertIn("dictValue", relations)
        self.assertTrue(all("graph" in item["location"] for item in result["items"]))
        self.assertTrue(any("key" in item["location"] for item in result["items"]))
        self.assertTrue(any("value" in item["location"] for item in result["items"]))

    def test_finds_instance_field_and_raw_class_attribute_without_descriptor_access(self):
        target = _Target()

        class Owner:
            class_target = target

            @property
            def dangerous(self):
                raise AssertionError("address search must not execute descriptors")

        owner = Owner()
        owner.instance_target = target

        result = self.find(target, [("owner", owner), ("owner_class", Owner)])

        relations = {item["relation"] for item in result["items"]}
        self.assertIn("instanceField", relations)
        self.assertIn("classAttribute", relations)
        class_item = next(item for item in result["items"] if item["relation"] == "classAttribute")
        self.assertEqual("object", class_item["kind"])
        self.assertEqual("class_target", class_item["name"])

    def test_gc_owner_edge_finds_untracked_target_without_pointer_dereference(self):
        target = "untracked-address-target-" + str(id(self))
        owner = [target]

        result = self.find(target, gc_snapshot=[owner])

        item = next(item for item in result["items"] if item["sourceKind"] == "gc")
        self.assertTrue(result["targetFound"])
        self.assertEqual("listItem", item["relation"])
        self.assertEqual("collectionItem", item["targetKind"])
        self.assertEqual("builtins.list", item["ownerTypeName"])
        self.assertEqual(hex(id(owner)), item["ownerAddressHex"])
        self.assertTrue(item["location"].startswith("GC objects / builtins.list @0x"))

    def test_preserves_aliases_and_different_top_level_paths(self):
        target = _Target()
        shared = {"target": target}

        result = self.find(target, [("first_alias", shared), ("second_alias", shared)])

        rooted = [item for item in result["items"] if item["sourceKind"] == "module"]
        self.assertEqual({"first_alias", "second_alias"}, {item["rootName"] for item in rooted})
        self.assertEqual(2, len(rooted))

    def test_interleaves_root_entries_with_nested_nodes_under_a_small_budget(self):
        target = _Target()
        roots = [
            {
                "sourceKind": "module",
                "name": "large",
                "location": "Modules / large",
                "moduleName": "large",
                "scopeType": "module",
                "value": None,
                "entries": lambda: ((f"item_{index}", index) for index in range(100)),
            },
            {
                "sourceKind": "module",
                "name": "nested",
                "location": "Modules / nested",
                "moduleName": "nested",
                "scopeType": "module",
                "value": None,
                "entries": lambda: [("graph", [target])],
            },
        ]
        with mock.patch.object(
            console_namespaces,
            "_gc_objects_snapshot",
            return_value=[],
        ), mock.patch.object(
            console_namespaces,
            "list_namespaces",
            return_value=self.discovery(),
        ), mock.patch.object(
            address_search.runtime_search,
            "_runtime_roots",
            return_value=(roots, self.root_metadata()),
        ):
            result = address_search.find_address(
                self.inspector,
                self.handles,
                -1,
                hex(id(target)),
                max_objects=4,
            )

        self.assertTrue(result["targetFound"])
        match = next(item for item in result["items"] if item["relation"] == "listItem")
        self.assertEqual("graph", match["rootName"])
        self.assertLessEqual(result["rootObjectsScanned"], 4)

    def test_prioritizes_main_module_over_many_library_roots(self):
        target = _Target()
        roots = [
            {
                "sourceKind": "module",
                "name": f"library_{index}",
                "location": f"Modules / library_{index}",
                "moduleName": f"library_{index}",
                "scopeType": "module",
                "value": None,
                "entries": lambda: [("noise", object())],
            }
            for index in range(20)
        ]
        roots.append({
            "sourceKind": "module",
            "name": "__main__",
            "location": "Modules / __main__",
            "moduleName": "__main__",
            "scopeType": "module",
            "value": None,
            "entries": lambda: [("main_target", target)],
        })
        with mock.patch.object(
            console_namespaces,
            "_gc_objects_snapshot",
            return_value=[],
        ), mock.patch.object(
            console_namespaces,
            "list_namespaces",
            return_value=self.discovery(),
        ), mock.patch.object(
            address_search.runtime_search,
            "_runtime_roots",
            return_value=(roots, self.root_metadata()),
        ):
            result = address_search.find_address(
                self.inspector,
                self.handles,
                -1,
                hex(id(target)),
                max_objects=1,
            )

        self.assertTrue(result["targetFound"])
        self.assertEqual("Modules / __main__ / main_target", result["items"][0]["location"])

    def test_fairly_schedules_normal_roots_after_three_priority_entries(self):
        target = _Target()
        roots = [
            {
                "sourceKind": "console",
                "name": "busy console",
                "location": "Console namespaces / busy",
                "scopeType": "console",
                "value": None,
                "entries": lambda: ((f"noise_{index}", object()) for index in range(100)),
            },
            {
                "sourceKind": "module",
                "name": "normal",
                "location": "Modules / normal",
                "moduleName": "normal",
                "scopeType": "module",
                "value": None,
                "entries": lambda: [("target", target)],
            },
        ]
        with mock.patch.object(
            console_namespaces,
            "_gc_objects_snapshot",
            return_value=[],
        ), mock.patch.object(
            console_namespaces,
            "list_namespaces",
            return_value=self.discovery(),
        ), mock.patch.object(
            address_search.runtime_search,
            "_runtime_roots",
            return_value=(roots, self.root_metadata()),
        ):
            result = address_search.find_address(
                self.inspector,
                self.handles,
                -1,
                hex(id(target)),
                max_objects=4,
            )

        self.assertTrue(result["targetFound"])
        self.assertEqual("Modules / normal / target", result["items"][0]["location"])

    def test_reports_matching_owner_edge_even_when_nested_queue_is_saturated(self):
        target = _Target()

        class Owner:
            pass

        owner = Owner()
        owner.instance_target = target
        roots = [
            {
                "sourceKind": "module",
                "name": "large",
                "location": "Modules / large",
                "moduleName": "large",
                "scopeType": "module",
                "value": None,
                "entries": lambda: [("large_graph", [object() for _ in range(100)])],
            },
            {
                "sourceKind": "module",
                "name": "owner",
                "location": "Modules / owner",
                "moduleName": "owner",
                "scopeType": "module",
                "value": None,
                "entries": lambda: [("holder", owner)],
            },
        ]
        with mock.patch.object(
            console_namespaces,
            "_gc_objects_snapshot",
            return_value=[],
        ), mock.patch.object(
            console_namespaces,
            "list_namespaces",
            return_value=self.discovery(),
        ), mock.patch.object(
            address_search.runtime_search,
            "_runtime_roots",
            return_value=(roots, self.root_metadata()),
        ):
            result = address_search.find_address(
                self.inspector,
                self.handles,
                -1,
                hex(id(target)),
                max_objects=5,
            )

        match = next(item for item in result["items"] if item["relation"] == "instanceField")
        self.assertEqual("Modules / owner / holder / instance_target", match["location"])
        self.assertTrue(result["objectLimitReached"])

    def test_reserves_up_to_half_the_object_budget_for_the_gc_snapshot(self):
        target = _Target()
        gc_snapshot = [[], [], [], [target]]

        result = self.find(
            target,
            ((f"root_{index}", index) for index in range(100)),
            gc_snapshot=gc_snapshot,
            max_objects=10,
        )

        self.assertTrue(result["targetFound"])
        self.assertEqual(6, result["rootObjectsScanned"])
        self.assertEqual(4, result["gcObjectsScanned"])
        self.assertTrue(result["gcScanComplete"])
        self.assertTrue(any(item["sourceKind"] == "gc" for item in result["items"]))

    def test_reserves_edge_budget_for_gc_owner_relationships(self):
        target = "gc-edge-target-" + str(id(self))
        owner = [target]

        result = self.find(
            target,
            ((f"noise_{index}", object()) for index in range(100)),
            gc_snapshot=[owner],
            max_objects=10,
            max_edges=4,
        )

        self.assertTrue(result["targetFound"])
        self.assertTrue(result["rootBudgetReached"])
        self.assertTrue(result["gcScanComplete"])
        self.assertEqual(4, result["edgesScanned"])
        self.assertTrue(any(item["sourceKind"] == "gc" for item in result["items"]))

    def test_root_time_budget_does_not_block_the_gc_phase(self):
        target = _Target()
        state = {
            "items": [],
            "resultKeys": set(),
            "targetFound": False,
            "targetSummary": None,
            "objectsScanned": 0,
            "rootObjectsScanned": 0,
            "gcObjectsScanned": 0,
            "rootsScanned": 0,
            "edgesScanned": 0,
            "maxEdges": 10,
            "deadline": 10.0,
            "rootDeadline": 1.0,
            "rootEdgeLimit": 7,
            "objectLimitReached": False,
            "resultLimitReached": False,
            "depthLimitReached": False,
            "childrenTruncated": False,
            "edgeLimitReached": False,
            "deadlineReached": False,
            "rootBudgetReached": False,
        }

        with mock.patch.object(address_search.time, "perf_counter", return_value=2.0):
            root_complete = address_search._search_runtime_roots(
                self.inspector,
                self.root([("noise", object())]),
                id(target),
                10,
                10,
                1,
                state,
            )
            state["rootDeadline"] = None
            state["rootEdgeLimit"] = None
            gc_complete = address_search._search_gc_snapshot(
                self.inspector,
                [target],
                set(),
                id(target),
                10,
                10,
                1,
                state,
            )

        self.assertFalse(root_complete)
        self.assertTrue(state["rootBudgetReached"])
        self.assertTrue(gc_complete)
        self.assertTrue(state["targetFound"])
        self.assertEqual(1, state["gcObjectsScanned"])

    def test_gc_max_depth_zero_only_matches_exact_tracked_owners(self):
        child = "gc-depth-child-" + str(id(self))
        owner = [child]

        child_result = self.find(child, gc_snapshot=[owner], max_depth=0)
        owner_result = self.find(owner, gc_snapshot=[owner], max_depth=0)

        self.assertFalse(child_result["targetFound"])
        self.assertEqual(0, child_result["edgesScanned"])
        self.assertTrue(child_result["depthLimitReached"])
        self.assertFalse(child_result["gcScanComplete"])
        self.assertTrue(owner_result["targetFound"])
        self.assertEqual("gcObject", owner_result["items"][0]["relation"])
        self.assertEqual(0, owner_result["items"][0]["depth"])
        self.assertTrue(owner_result["depthLimitReached"])

    def test_cycle_is_reported_once_per_structural_edge_without_looping(self):
        target = []
        target.append(target)

        result = self.find(target, [("cycle", target)], max_objects=20)

        self.assertTrue(result["scanComplete"])
        self.assertEqual(2, len(result["items"]))
        self.assertEqual({"moduleVariable", "listItem"}, {
            item["relation"] for item in result["items"]
        })
        self.assertLessEqual(result["objectsScanned"], 2)

    def test_preserves_same_text_location_from_different_frames(self):
        target = _Target()
        roots = []
        for frame_handle in ("frame-a", "frame-b"):
            roots.append({
                "sourceKind": "frame",
                "name": "worker",
                "location": "Threads / 1 / worker / Locals",
                "moduleName": "__main__",
                "frameHandle": frame_handle,
                "scopeType": "locals",
                "consoleHandle": None,
                "consoleAttributeName": None,
                "value": None,
                "entries": lambda target=target: [("target", target)],
            })
        metadata = self.root_metadata()
        metadata["frameRootsIncluded"] = 2
        with mock.patch.object(
            console_namespaces,
            "_gc_objects_snapshot",
            return_value=[],
        ), mock.patch.object(
            console_namespaces,
            "list_namespaces",
            return_value=self.discovery(),
        ), mock.patch.object(
            address_search.runtime_search,
            "_runtime_roots",
            return_value=(roots, metadata),
        ):
            result = address_search.find_address(
                self.inspector,
                self.handles,
                -1,
                hex(id(target)),
            )

        frame_items = [
            item for item in result["items"]
            if item["relation"] == "frameVariable"
        ]
        self.assertEqual(2, len(frame_items))
        self.assertEqual({"frame-a", "frame-b"}, {item["frameHandle"] for item in frame_items})

    def test_cpython_audit_hooks_observe_snapshot_operations(self):
        code = textwrap.dedent(
            """
            import sys
            from pyruntime_inspector_agent import address_search
            from pyruntime_inspector_agent.handles import HandleStore
            from pyruntime_inspector_agent.safe_objects import SafeObjectInspector

            events = []
            def audit(event, _arguments):
                if event in ("gc.get_objects", "sys._current_frames"):
                    events.append(event)

            sys.addaudithook(audit)
            target = object()
            handles = HandleStore()
            address_search.find_address(
                SafeObjectInspector(handles),
                handles,
                -1,
                hex(id(target)),
                max_results=10,
                max_objects=1_000,
                max_depth=1,
                max_edges=5_000,
                max_duration_ms=10_000,
            )
            print(",".join(sorted(set(events))))
            """
        )
        environment = os.environ.copy()
        completed = subprocess.run(
            [sys.executable, "-c", code],
            check=True,
            capture_output=True,
            text=True,
            env=environment,
            timeout=20,
        )

        self.assertIn("gc.get_objects", completed.stdout)
        self.assertIn("sys._current_frames", completed.stdout)

    def test_reports_depth_object_result_and_child_limits_as_incomplete(self):
        target = _Target()
        depth_limited = self.find(target, [("root", [[target]])], max_depth=0)
        object_limited = self.find(
            target,
            [("first", object()), ("second", target)],
            max_objects=1,
        )
        with mock.patch.object(address_search, "MAX_CHILDREN_PER_OBJECT", 1):
            child_limited = self.find(target, [("root", [object(), target])])

        self.assertFalse(depth_limited["scanComplete"])
        self.assertTrue(depth_limited["depthLimitReached"])
        self.assertFalse(depth_limited["targetFound"])
        self.assertFalse(object_limited["scanComplete"])
        self.assertTrue(object_limited["objectLimitReached"])
        self.assertFalse(child_limited["scanComplete"])
        self.assertTrue(child_limited["childrenTruncated"])

    def test_reports_result_limit_and_target_not_found(self):
        target = _Target()
        limited = self.find(
            target,
            [("first", target), ("second", target)],
            max_results=1,
        )
        missing = self.find(target, [("other", object())])

        self.assertEqual(1, limited["total"])
        self.assertTrue(limited["resultLimitReached"])
        self.assertFalse(limited["scanComplete"])
        self.assertFalse(missing["targetFound"])
        self.assertEqual(0, missing["total"])

    def test_global_edge_limit_bounds_relationship_scanning(self):
        target = _Target()

        result = self.find(
            target,
            [("root", [object(), target])],
            max_edges=1,
        )

        self.assertFalse(result["targetFound"])
        self.assertFalse(result["scanComplete"])
        self.assertTrue(result["edgeLimitReached"])
        self.assertEqual(1, result["edgesScanned"])
        self.assertEqual(1, result["maxEdges"])

    def test_expired_discovery_deadline_still_scans_direct_main_roots(self):
        target = _Target()
        direct_roots = [
            {
                "sourceKind": "module",
                "name": "__main__",
                "location": "Modules / __main__",
                "moduleName": "__main__",
                "scopeType": "module",
                "value": None,
                "entries": lambda: [("target", target)],
            },
            {
                "sourceKind": "frame",
                "name": "worker",
                "location": "Threads / 1 / worker / Locals",
                "frameHandle": "frame-handle",
                "scopeType": "locals",
                "value": None,
                "entries": lambda: [("target", target)],
            },
        ]
        clock_calls = 0

        def clock():
            nonlocal clock_calls
            clock_calls += 1
            return 0.0 if clock_calls == 1 else 2.0

        with mock.patch.object(
            console_namespaces,
            "_gc_objects_snapshot",
            return_value=[],
        ), mock.patch.object(
            console_namespaces,
            "list_namespaces",
            side_effect=AssertionError("expired search must not discover consoles"),
        ), mock.patch.object(
            address_search.runtime_search,
            "_runtime_roots",
            return_value=(direct_roots, self.root_metadata()),
        ), mock.patch.object(
            address_search.time,
            "perf_counter",
            side_effect=clock,
        ):
            result = address_search.find_address(
                self.inspector,
                self.handles,
                -1,
                hex(id(target)),
                max_duration_ms=1_000,
            )

        self.assertTrue(result["targetFound"])
        self.assertFalse(result["scanComplete"])
        self.assertTrue(result["deadlineReached"])
        self.assertEqual(2, result["objectsScanned"])
        self.assertEqual(
            {"moduleVariable", "frameVariable"},
            {item["relation"] for item in result["items"]},
        )
        self.assertEqual(1_000, result["maxDurationMilliseconds"])

    def test_bounded_module_root_generator_is_lazy_and_reports_its_limit(self):
        registry = {
            f"module_{index}": types.ModuleType(f"module_{index}")
            for index in range(4)
        }
        metadata = {
            "moduleRootLimitReached": False,
            "moduleRegistryMutationDetected": False,
            "moduleRootsIncluded": 0,
            "namespaceRawLimitReached": False,
            "namespaceMutationDetected": False,
            "namespaceDeadlineReached": False,
        }
        roots = address_search.runtime_search._bounded_module_roots(
            registry,
            2,
            time.perf_counter() + 10,
            100,
            metadata,
        )

        self.assertEqual(0, metadata["moduleRootsIncluded"])
        first = next(roots)
        self.assertEqual("module_0", first["moduleName"])
        self.assertEqual(1, metadata["moduleRootsIncluded"])
        self.assertEqual(["module_1"], [root["moduleName"] for root in roots])
        self.assertTrue(metadata["moduleRootLimitReached"])

    def test_bounded_module_root_generator_retries_registry_mutation(self):
        registry = {
            "first": types.ModuleType("first"),
            "second": types.ModuleType("second"),
        }
        metadata = {
            "moduleRootLimitReached": False,
            "moduleRegistryMutationDetected": False,
            "moduleRootsIncluded": 0,
            "namespaceRawLimitReached": False,
            "namespaceMutationDetected": False,
            "namespaceDeadlineReached": False,
        }
        roots = address_search.runtime_search._bounded_module_roots(
            registry,
            10,
            time.perf_counter() + 10,
            100,
            metadata,
        )

        self.assertEqual("first", next(roots)["moduleName"])
        registry["third"] = types.ModuleType("third")
        remaining = [root["moduleName"] for root in roots]

        self.assertEqual(["second", "third"], remaining)
        self.assertTrue(metadata["moduleRegistryMutationDetected"])
        self.assertFalse(metadata["moduleRootLimitReached"])

    def test_address_request_retries_module_registry_mutation_and_reports_it(self):
        target = _Target()
        first = types.ModuleType("first")
        second = types.ModuleType("second")
        third = types.ModuleType("third")
        for module, name, value in (
            (first, "noise", object()),
            (second, "noise", object()),
            (third, "target", target),
        ):
            namespace = types.ModuleType.__getattribute__(module, "__dict__")
            dict.clear(namespace)
            dict.__setitem__(namespace, name, value)
        registry = {"first": first, "second": second}
        original = address_search.runtime_search._bounded_module_roots

        def mutating_roots(*args, **kwargs):
            roots = original(*args, **kwargs)
            yield next(roots)
            registry["third"] = third
            yield from roots

        with mock.patch.object(
            console_namespaces,
            "_gc_objects_snapshot",
            return_value=[],
        ), mock.patch.object(
            console_namespaces,
            "list_namespaces",
            return_value=self.discovery(),
        ), mock.patch.object(
            address_search.runtime_search.modules,
            "_module_registry",
            return_value=registry,
        ), mock.patch.object(
            address_search.runtime_search.frames,
            "_current_frames_snapshot",
            return_value={},
        ), mock.patch.object(
            address_search.runtime_search,
            "_bounded_module_roots",
            side_effect=mutating_roots,
        ):
            result = address_search.find_address(
                self.inspector,
                self.handles,
                -1,
                hex(id(target)),
                max_objects=10,
            )

        self.assertTrue(result["targetFound"])
        self.assertTrue(result["moduleRegistryMutationDetected"])
        self.assertFalse(result["scanComplete"])

    def test_address_module_namespace_raw_keys_are_capped_before_next_value(self):
        target = _Target()
        main = types.ModuleType("__main__")
        namespace = types.ModuleType.__getattribute__(main, "__dict__")
        dict.clear(namespace)
        for index in range(32):
            dict.__setitem__(namespace, index, object())
        dict.__setitem__(namespace, "target", target)
        registry = {"__main__": main}

        with mock.patch.object(
            console_namespaces,
            "_gc_objects_snapshot",
            return_value=[],
        ), mock.patch.object(
            console_namespaces,
            "list_namespaces",
            return_value=self.discovery(),
        ), mock.patch.object(
            address_search.runtime_search.modules,
            "_module_registry",
            return_value=registry,
        ), mock.patch.object(
            address_search.runtime_search.frames,
            "_current_frames_snapshot",
            return_value={},
        ), mock.patch.object(address_search, "MAX_CHILDREN_PER_OBJECT", 8):
            result = address_search.find_address(
                self.inspector,
                self.handles,
                -1,
                hex(id(target)),
                max_objects=1,
                max_edges=1,
                max_duration_ms=1_000,
            )

        self.assertFalse(result["targetFound"])
        self.assertEqual(0, result["objectsScanned"])
        self.assertEqual(0, result["edgesScanned"])
        self.assertTrue(result["namespaceRawLimitReached"])
        self.assertFalse(result["scanComplete"])

    def test_console_owner_raw_keys_are_capped_and_reported(self):
        class ConsoleTrap:
            pass

        owner = ConsoleTrap()
        state = object.__getattribute__(owner, "__dict__")
        for index in range(32):
            dict.__setitem__(state, index, object())
        dict.__setitem__(state, "namespace", {"target": object()})
        target = _Target()
        metadata = self.root_metadata()

        with mock.patch.object(
            console_namespaces,
            "_gc_objects_snapshot",
            return_value=[owner],
        ), mock.patch.object(
            console_namespaces,
            "_known_console_types",
            return_value={"python": (), "ipython": ()},
        ), mock.patch.object(
            address_search.runtime_search,
            "_runtime_roots",
            return_value=([], metadata),
        ), mock.patch.object(address_search, "MAX_CHILDREN_PER_OBJECT", 8):
            result = address_search.find_address(
                self.inspector,
                self.handles,
                -1,
                hex(id(target)),
                max_objects=1,
                max_edges=16,
                max_duration_ms=1_000,
            )

        self.assertEqual(1, result["consoleDiscoveryScannedCount"])
        self.assertTrue(result["consoleRawEntryLimitReached"])
        self.assertFalse(result["consoleDiscoveryComplete"])
        self.assertFalse(result["scanComplete"])

    def test_large_first_gc_owner_is_capped_before_later_owner_is_scanned(self):
        target = "later-untracked-target-" + str(id(self))
        first_owner = [object() for _ in range(address_search.MAX_CHILDREN_PER_OBJECT + 1)]
        later_owner = [target]

        result = self.find(target, gc_snapshot=[first_owner, later_owner])

        self.assertTrue(result["targetFound"])
        self.assertTrue(result["childrenTruncated"])
        self.assertEqual(address_search.MAX_CHILDREN_PER_OBJECT + 1, result["edgesScanned"])
        self.assertTrue(any(
            item["ownerAddressHex"] == hex(id(later_owner))
            for item in result["items"]
        ))

    def test_rejects_invalid_edge_and_deadline_limits_before_gc_scan(self):
        invalid = (
            {"max_edges": 0},
            {"max_edges": address_search.MAX_EDGES + 1},
            {"max_edges": True},
            {"max_duration_ms": 0},
            {"max_duration_ms": address_search.MAX_DURATION_MS + 1},
            {"max_duration_ms": 1.5},
        )
        with mock.patch.object(
            console_namespaces,
            "_gc_objects_snapshot",
            side_effect=AssertionError("invalid limits must be rejected before GC scanning"),
        ):
            for limits in invalid:
                with self.subTest(limits=limits), self.assertRaises(ValueError):
                    address_search.find_address(
                        self.inspector,
                        self.handles,
                        -1,
                        hex(id(self)),
                        **limits,
                    )

    def test_hostile_repr_class_equality_hash_and_attribute_hooks_are_not_called(self):
        class Hostile:
            @property
            def __class__(self):
                raise AssertionError("address search must not read target __class__")

            def __getattribute__(self, name):
                if name not in ("__dict__",):
                    raise AssertionError("address search must not invoke user attribute access")
                return object.__getattribute__(self, name)

            def __repr__(self):
                raise AssertionError("address search must not call user repr")

            def __eq__(self, other):
                raise AssertionError("address search must not call user equality")

            def __hash__(self):
                raise AssertionError("address search must not call user hash")

        target = Hostile()

        result = self.find(target, [("hostile", target)])

        self.assertTrue(result["targetFound"])
        self.assertTrue(result["items"][0]["value"]["safePreview"].endswith(".Hostile object>"))

    def test_never_uses_gc_get_referrers(self):
        target = _Target()
        owner = [target]

        with mock.patch.object(
            gc,
            "get_referrers",
            side_effect=AssertionError("gc.get_referrers must not execute"),
        ):
            result = self.find(target, gc_snapshot=[owner])

        self.assertTrue(result["targetFound"])

    def test_rejects_spoofed_gc_get_objects(self):
        with mock.patch.object(gc, "get_objects", lambda: []):
            with self.assertRaisesRegex(ValueError, "GC snapshot function"):
                address_search.find_address(
                    self.inspector,
                    self.handles,
                    -1,
                    hex(id(self)),
                )

    def test_excludes_handle_store_entry_containers_from_gc_relationships(self):
        target = _Target()
        self.handles.put(target)
        entries = object.__getattribute__(self.handles, "_entries")
        record = next(iter(entries.values()))

        result = self.find(target, gc_snapshot=[record])

        self.assertFalse(result["targetFound"])
        self.assertEqual([], result["items"])

    def test_gc_snapshot_is_taken_before_console_and_runtime_root_discovery(self):
        events = []
        target = _Target()

        def snapshot():
            events.append("snapshot")
            return []

        def discover(*_args, **_kwargs):
            events.append("consoles")
            return self.discovery()

        def roots(*_args, **_kwargs):
            events.append("roots")
            return self.root([("target", target)]), self.root_metadata()

        with mock.patch.object(console_namespaces, "_gc_objects_snapshot", side_effect=snapshot), \
                mock.patch.object(console_namespaces, "list_namespaces", side_effect=discover), \
                mock.patch.object(address_search.runtime_search, "_runtime_roots", side_effect=roots):
            address_search.find_address(
                self.inspector,
                self.handles,
                -1,
                hex(id(target)),
            )

        self.assertEqual(["snapshot", "consoles", "roots"], events)

    def test_console_discovery_reuses_the_initial_gc_snapshot(self):
        target = _Target()
        metadata = self.root_metadata()
        with mock.patch.object(
            console_namespaces,
            "_gc_objects_snapshot",
            return_value=[],
        ) as snapshot, mock.patch.object(
            address_search.runtime_search,
            "_runtime_roots",
            return_value=(self.root([("target", target)]), metadata),
        ):
            result = address_search.find_address(
                self.inspector,
                self.handles,
                -1,
                hex(id(target)),
            )

        self.assertTrue(result["targetFound"])
        snapshot.assert_called_once_with()

    def test_server_dispatches_runtime_find_address_with_runtime_search_limits(self):
        agent = InspectorAgent("127.0.0.1", 1, "a" * 64, "cooperative")
        expected = {"mode": "address", "items": []}

        with mock.patch.object(address_search, "find_address", return_value=expected) as find:
            result, binary, detach = agent._dispatch("runtime.findAddress", {
                "address": "0x1234",
                "maxResults": 11,
                "maxObjects": 22,
                "maxDepth": 3,
                "maxEdges": 33,
                "maxDurationMilliseconds": 44,
            })

        self.assertIs(expected, result)
        self.assertEqual(b"", binary)
        self.assertFalse(detach)
        find.assert_called_once_with(
            agent._objects,
            agent._handles,
            agent._thread.ident,
            "0x1234",
            11,
            22,
            3,
            33,
            44,
        )


if __name__ == "__main__":
    unittest.main()
