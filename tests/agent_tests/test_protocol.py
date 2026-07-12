import socket
import struct
import unittest
from unittest import mock

from pyruntime_inspector_agent.protocol import ProtocolError, read_frame, write_frame
from pyruntime_inspector_agent import server
from pyruntime_inspector_agent.server import ActiveAgentConflictError, InspectorAgent, _bound_result


class ProtocolTests(unittest.TestCase):
    def test_inspector_thread_remains_available_while_debuggee_is_suspended(self):
        agent = InspectorAgent("127.0.0.1", 49152, "a" * 64, "cooperative")

        self.assertTrue(agent._thread.pydev_do_not_trace)
        self.assertTrue(agent._thread.is_pydev_daemon_thread)

    def test_active_agent_is_reused_only_for_the_same_connection(self):
        agent = InspectorAgent("127.0.0.1", 49152, "a" * 64, "cooperative")
        with mock.patch.object(server, "_active_agent", agent):
            self.assertIs(
                agent,
                server.start_inspector(
                    host="127.0.0.1",
                    port=49152,
                    token="a" * 64,
                    attach_mode="cooperative",
                ),
            )
            with self.assertRaisesRegex(ActiveAgentConflictError, "different connection settings"):
                server.start_inspector(
                    host="127.0.0.1",
                    port=49153,
                    token="b" * 64,
                    attach_mode="cooperative",
                )

    def test_round_trip_with_binary_payload(self):
        left, right = socket.socketpair()
        self.addCleanup(left.close)
        self.addCleanup(right.close)
        write_frame(left, {"requestId": "one", "binaryLength": 999}, b"abc")
        header, binary = read_frame(right)
        self.assertEqual("one", header["requestId"])
        self.assertEqual(3, header["binaryLength"])
        self.assertEqual(b"abc", binary)

    def test_rejects_oversized_header_before_reading_it(self):
        left, right = socket.socketpair()
        self.addCleanup(left.close)
        self.addCleanup(right.close)
        left.sendall(struct.pack(">I", 1024 * 1024 + 1))
        with self.assertRaises(ProtocolError):
            read_frame(right)

    def test_outbound_result_bounds_target_strings_and_collections(self):
        bounded, truncated = _bound_result({
            "items": [{"name": "x" * 1_100_000} for _ in range(2_100)],
        })

        self.assertTrue(truncated)
        self.assertEqual(2_000, len(bounded["items"]))
        self.assertLessEqual(max(len(item["name"]) for item in bounded["items"]), 4096)
        left, right = socket.socketpair()
        self.addCleanup(left.close)
        self.addCleanup(right.close)
        write_frame(left, {"result": bounded})
        header, _ = read_frame(right)
        self.assertEqual(2_000, len(header["result"]["items"]))
