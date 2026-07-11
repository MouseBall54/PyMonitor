import os
import sys
import unittest

from pyruntime_inspector_agent import monitoring


def monitored_function(value):
    if value < 0:
        raise ValueError("sample")
    return value + 1


@unittest.skipUnless(hasattr(sys, "monitoring"), "requires Python 3.12+")
class MonitoringTests(unittest.TestCase):
    def tearDown(self):
        monitoring.cleanup()

    def test_records_selected_events_without_values_or_agent_frames(self):
        result = monitoring.start(
            ["PY_START", "PY_RETURN", "RAISE", "LINE"],
            500,
            os.path.dirname(__file__),
        )
        self.assertTrue(result["active"])
        self.assertIn(result["toolId"], (3, 4))

        monitored_function(1)
        try:
            monitored_function(-1)
        except ValueError:
            pass
        monitoring.stop()
        events = monitoring.list_events(limit=500)

        self.assertTrue(events["items"])
        self.assertTrue({"PY_START", "PY_RETURN", "RAISE"}.issubset(
            {item["eventName"] for item in events["items"]}))
        self.assertTrue(all("pyruntime_inspector_agent" not in item["filename"] for item in events["items"]))
        self.assertTrue(all("value" not in item for item in events["items"]))

    def test_ring_buffer_and_dropped_count_are_bounded(self):
        monitoring.start(["LINE"], 100, __file__)
        for index in range(250):
            monitored_function(index)
        monitoring.stop()
        result = monitoring.list_events(limit=1000)

        self.assertEqual(100, len(result["items"]))
        self.assertGreater(result["droppedCount"], 0)

    def test_reports_tool_id_conflict_without_taking_reserved_ids(self):
        claimed = []
        try:
            for tool_id in (3, 4):
                if sys.monitoring.get_tool(tool_id) is None:
                    sys.monitoring.use_tool_id(tool_id, "PyRuntimeInspectorConflictTest")
                    claimed.append(tool_id)
            if len(claimed) != 2:
                self.skipTest("Monitoring tool IDs 3 and 4 were already occupied by the test environment.")
            with self.assertRaises(monitoring.MonitoringError) as context:
                monitoring.start(["LINE"])
            self.assertEqual("TOOL_ID_CONFLICT", context.exception.code)
        finally:
            for tool_id in claimed:
                sys.monitoring.free_tool_id(tool_id)


if __name__ == "__main__":
    unittest.main()
