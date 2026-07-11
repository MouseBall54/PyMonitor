import tracemalloc
import unittest

from pyruntime_inspector_agent import memory


class MemoryTests(unittest.TestCase):
    def tearDown(self):
        memory.stop()

    def test_start_snapshot_diff_statistics_and_stop(self):
        started = memory.start(3)
        self.assertTrue(started["tracing"])
        self.assertEqual(3, started["tracebackDepth"])
        before = memory.take_snapshot("before")
        allocation = [bytearray(1024) for _ in range(20)]
        after = memory.take_snapshot("after")

        snapshots = memory.list_snapshots()
        self.assertEqual([before["snapshotId"], after["snapshotId"]], [item["snapshotId"] for item in snapshots["items"]])
        differences = memory.diff(before["snapshotId"], after["snapshotId"], limit=20)
        self.assertTrue(any(item["sizeDiffBytes"] > 0 for item in differences["items"]))
        statistics = memory.statistics(limit=5)
        self.assertLessEqual(len(statistics["items"]), 5)
        self.assertGreater(sum(len(value) for value in allocation), 0)

        stopped = memory.stop()
        self.assertFalse(stopped["tracing"])
        self.assertTrue(stopped["wasTracing"])

    def test_cleanup_preserves_tracing_started_by_target(self):
        tracemalloc.start(1)
        try:
            result = memory.start(5)
            self.assertTrue(result["alreadyTracing"])
            self.assertFalse(result["startedByInspector"])
            memory.cleanup()
            self.assertTrue(tracemalloc.is_tracing())
        finally:
            tracemalloc.stop()

    def test_snapshots_are_bounded(self):
        memory.start()
        for index in range(12):
            memory.take_snapshot(str(index))
        snapshots = memory.list_snapshots()
        self.assertEqual(8, len(snapshots["items"]))
        self.assertEqual("4", snapshots["items"][0]["label"])


if __name__ == "__main__":
    unittest.main()
