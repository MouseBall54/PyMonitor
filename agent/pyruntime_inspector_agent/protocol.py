import json
import struct

PROTOCOL_VERSION = "1.0"
MAX_HEADER_BYTES = 1024 * 1024
MAX_BINARY_BYTES = 8 * 1024 * 1024


class ProtocolError(Exception):
    pass


def _receive_exact(sock, size):
    data = bytearray()
    while len(data) < size:
        chunk = sock.recv(size - len(data))
        if not chunk:
            raise EOFError("Connection closed while receiving a frame.")
        data.extend(chunk)
    return bytes(data)


def read_frame(sock):
    header_size = struct.unpack(">I", _receive_exact(sock, 4))[0]
    if header_size == 0 or header_size > MAX_HEADER_BYTES:
        raise ProtocolError("Invalid JSON header length.")
    try:
        header = json.loads(_receive_exact(sock, header_size).decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ProtocolError("Invalid JSON header.") from exc
    if type(header) is not dict:
        raise ProtocolError("JSON header must be an object.")
    binary_size = header.get("binaryLength", 0)
    if type(binary_size) is not int or binary_size < 0 or binary_size > MAX_BINARY_BYTES:
        raise ProtocolError("Invalid binary payload length.")
    return header, _receive_exact(sock, binary_size) if binary_size else b""


def write_frame(sock, header, binary=b""):
    if type(header) is not dict or type(binary) is not bytes:
        raise TypeError("header must be dict and binary must be bytes")
    if len(binary) > MAX_BINARY_BYTES:
        raise ProtocolError("Binary payload is too large.")
    outgoing = dict(header)
    outgoing["binaryLength"] = len(binary)
    encoded = json.dumps(outgoing, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    if len(encoded) > MAX_HEADER_BYTES:
        raise ProtocolError("JSON header is too large.")
    sock.sendall(struct.pack(">I", len(encoded)) + encoded + binary)
