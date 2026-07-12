import sys
import threading
import types
import unittest

from pyruntime_inspector_agent import frames as frame_module
from pyruntime_inspector_agent.frames import list_frames, list_scope, list_threads
from pyruntime_inspector_agent.handles import HandleStore
from pyruntime_inspector_agent.safe_objects import SafeObjectInspector


class FrameTests(unittest.TestCase):
    def test_thread_listing_bypasses_user_overrides(self):
        reads = []
        ready = threading.Event()
        stop = threading.Event()

        class HostileThread(threading.Thread):
            armed = False

            def __getattribute__(self, name):
                if name in {"ident", "name", "daemon", "is_alive"} and object.__getattribute__(self, "armed"):
                    reads.append(name)
                    raise AssertionError(f"thread metadata access must bypass {name}")
                return object.__getattribute__(self, name)

        thread = HostileThread(name="hostile-worker", target=lambda: (ready.set(), stop.wait()))
        thread.start()
        self.addCleanup(thread.join)
        self.addCleanup(stop.set)
        self.assertTrue(ready.wait(2))
        thread_id = thread.ident
        object.__setattr__(thread, "armed", True)
        try:
            row = next(item for item in list_threads(-1)["items"] if item["threadId"] == thread_id)
        finally:
            object.__setattr__(thread, "armed", False)

        self.assertEqual("hostile-worker", row["name"])
        self.assertFalse(row["daemon"])
        self.assertTrue(row["alive"])
        self.assertTrue(row["hasTopFrame"])
        self.assertEqual([], reads)

    def test_lists_worker_local_and_global_scopes_separately(self):
        ready = threading.Event()
        stop = threading.Event()

        def worker():
            local_marker = "frame-local"
            ready.set()
            stop.wait()
            return local_marker

        thread = threading.Thread(target=worker)
        thread.start()
        self.addCleanup(thread.join)
        self.addCleanup(stop.set)
        self.assertTrue(ready.wait(2))
        handles = HandleStore()
        inspector = SafeObjectInspector(handles)
        rows = list_frames(handles, agent_thread_id=-1)["items"]
        frame = next(row for row in rows if row["threadId"] == thread.ident and row["functionName"] == "worker")
        local_scope = list_scope(handles, inspector, frame["frameHandle"], "locals")
        global_scope = list_scope(handles, inspector, frame["frameHandle"], "globals")
        self.assertIn("local_marker", {item["name"] for item in local_scope["items"]})
        self.assertNotIn("local_marker", {item["name"] for item in global_scope["items"]})
        with self.assertRaises(ValueError):
            list_scope(handles, inspector, frame["frameHandle"], "locals", page_size=201)

    def test_large_scope_retains_only_insertion_order_page(self):
        namespace = {"sys": sys}
        for index in range(20_000):
            namespace[f"value_{20_000 - index:05}"] = index
        capture = types.FunctionType(_capture_frame.__code__, namespace)
        frame = capture()
        handles = HandleStore()
        inspector = SafeObjectInspector(handles)
        offset = 19_991

        result = list_scope(
            handles,
            inspector,
            handles.put(frame),
            "globals",
            offset=offset,
            page_size=10,
        )

        self.assertEqual(20_001, result["total"])
        self.assertEqual("insertion", result["ordering"])
        self.assertEqual(
            [f"value_{20_000 - index:05}" for index in range(offset - 1, offset + 9)],
            [item["name"] for item in result["items"]],
        )

    def test_transient_scope_key_mutation_retries_with_complete_metadata(self):
        namespace = {"sys": sys, "value": 1}
        capture = types.FunctionType(_capture_frame.__code__, namespace)
        frame = capture()
        handles = HandleStore()
        inspector = SafeObjectInspector(handles)
        requests = [threading.Event()]
        completed = [threading.Event()]
        worker_errors = []

        def mutate_namespace():
            for attempt_index in range(1):
                if not requests[attempt_index].wait(2):
                    worker_errors.append(f"scan {attempt_index} was not reached")
                    return
                namespace[f"concurrent_{attempt_index}"] = attempt_index
                completed[attempt_index].set()

        worker = threading.Thread(target=mutate_namespace)
        worker.start()
        signaled_attempts = set()
        started_attempts = set()
        scan_code = frame_module._bounded_scan.__code__

        def trace_scan(active_frame, event, argument):
            if active_frame.f_code is scan_code and event == "line":
                scan_locals = active_frame.f_locals
                attempt_index = _trace_local(scan_locals, "attempt_index")
                total = _trace_local(scan_locals, "total")
                if attempt_index == 0 and total == 0:
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
            result = list_scope(
                handles,
                inspector,
                handles.put(frame),
                "globals",
                page_size=10,
            )
        finally:
            sys.settrace(previous_trace)
            for request in requests:
                request.set()
            worker.join(2)

        self.assertFalse(worker.is_alive())
        self.assertEqual([], worker_errors)
        self.assertEqual({0}, signaled_attempts)
        self.assertTrue(result["mutationDetected"])
        self.assertTrue(result["scanComplete"])
        self.assertTrue(result["totalIsExact"])
        self.assertEqual(["sys", "value", "concurrent_0"], [item["name"] for item in result["items"]])

    def test_deep_thread_stack_is_bounded_before_handles_are_created(self):
        ready = threading.Event()
        stop = threading.Event()

        def recurse(depth):
            if depth == 0:
                ready.set()
                stop.wait()
                return
            recurse(depth - 1)

        thread = threading.Thread(target=recurse, args=(300,), name="deep-stack")
        thread.start()
        self.addCleanup(thread.join)
        self.addCleanup(stop.set)
        self.assertTrue(ready.wait(2))
        handles = HandleStore(max_entries=512)

        result = list_frames(handles, agent_thread_id=threading.get_ident())
        worker_rows = [item for item in result["items"] if item["threadId"] == thread.ident]

        self.assertTrue(result["truncated"])
        self.assertLessEqual(len(result["items"]), result["limit"])
        self.assertEqual(result["perThreadLimit"], len(worker_rows))
        for row in worker_rows:
            self.assertIsInstance(handles.get(row["frameHandle"]), types.FrameType)

    @unittest.skipUnless(sys.version_info >= (3, 13), "requires CPython 3.13+ frame locals proxies")
    def test_exact_frame_locals_proxy_is_read_without_dynamic_mapping_methods(self):
        def capture():
            local_marker = "proxy-local"
            return sys._getframe(), local_marker

        frame, _ = capture()
        mapping = frame.f_locals

        self.assertEqual("FrameLocalsProxy", type(mapping).__name__)
        self.assertEqual(
            "proxy-local",
            dict(frame_module._namespace_entries(mapping))["local_marker"],
        )

    def test_heap_fake_frame_locals_proxy_is_rejected_without_calling_items(self):
        calls = []

        class FrameLocalsProxy:
            __module__ = "builtins"

            def items(self):
                calls.append("items")
                raise AssertionError("fake items must not run")

        with self.assertRaisesRegex(ValueError, "not a dictionary"):
            list(frame_module._namespace_entries(FrameLocalsProxy()))
        self.assertEqual([], calls)


def _capture_frame():
    return sys._getframe()


def _trace_local(mapping, name):
    try:
        return mapping[name]
    except KeyError:
        return None
