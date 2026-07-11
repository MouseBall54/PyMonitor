import threading
import unittest

from pyruntime_inspector_agent.frames import list_frames, list_scope
from pyruntime_inspector_agent.handles import HandleStore
from pyruntime_inspector_agent.safe_objects import SafeObjectInspector


class FrameTests(unittest.TestCase):
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
