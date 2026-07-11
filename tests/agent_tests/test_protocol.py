import socket
import struct
import unittest

from pyruntime_inspector_agent.protocol import ProtocolError, read_frame, write_frame


class ProtocolTests(unittest.TestCase):
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
