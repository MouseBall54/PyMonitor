"""Fresh, cache-aware entry point for cooperative PyMonitor attach."""

import hmac
import importlib
import json
import os
import socket
import struct
import sys
import types


_PACKAGE_NAME = "pyruntime_inspector_agent"
_PROTOCOL_VERSION = "1.0"
_MAX_HEADER_BYTES = 1024 * 1024
_MAX_REQUEST_ID_LENGTH = 128
_MAX_ERROR_MESSAGE_LENGTH = 4096


def start_bootstrap(
    agent_directory,
    expected_version,
    expected_bootstrap_abi,
    host,
    port,
    token,
    attach_mode,
):
    expected_init = _expected_init_path(agent_directory)
    cached_modules = _cached_package_modules()
    cached = dict.get(cached_modules, _PACKAGE_NAME)
    if cached is None and cached_modules:
        mismatch = _partial_cache_message(cached_modules, expected_version, expected_bootstrap_abi, expected_init)
        _report_bootstrap_error(host, port, token, "STALE_AGENT", mismatch)
        raise RuntimeError(mismatch)
    if cached is not None:
        mismatch = _module_mismatch(cached, expected_version, expected_bootstrap_abi, expected_init)
        mismatch = mismatch or _module_tree_mismatch(cached_modules, expected_init)
        if mismatch is not None:
            _report_bootstrap_error(host, port, token, "STALE_AGENT", mismatch)
            raise RuntimeError(mismatch)

    package = importlib.import_module(_PACKAGE_NAME)
    loaded_modules = _cached_package_modules()
    mismatch = _module_mismatch(package, expected_version, expected_bootstrap_abi, expected_init)
    mismatch = mismatch or _module_tree_mismatch(loaded_modules, expected_init)
    if mismatch is not None:
        _report_bootstrap_error(host, port, token, "STALE_AGENT", mismatch)
        raise RuntimeError(mismatch)

    try:
        return package.start_inspector(
            host=host,
            port=port,
            token=token,
            attach_mode=attach_mode,
        )
    except Exception as exc:
        if getattr(exc, "code", None) == "ACTIVE_AGENT_CONFLICT":
            _report_bootstrap_error(host, port, token, exc.code, str(exc))
        raise


def _expected_init_path(agent_directory):
    if type(agent_directory) is not str or not agent_directory:
        raise ValueError("agent_directory must be a non-empty path.")
    return _normalized_path(os.path.join(
        agent_directory,
        _PACKAGE_NAME,
        "__init__.py",
    ))


def _cached_package_modules():
    snapshot = dict.copy(sys.modules)
    prefix = _PACKAGE_NAME + "."
    return {
        name: module
        for name, module in dict.items(snapshot)
        if name == _PACKAGE_NAME or name.startswith(prefix)
    }


def _module_mismatch(package, expected_version, expected_bootstrap_abi, expected_init):
    if type(expected_version) is not str or not expected_version:
        raise ValueError("expected_version must be a non-empty string.")
    if type(expected_bootstrap_abi) is not int or expected_bootstrap_abi < 1:
        raise ValueError("expected_bootstrap_abi must be a positive integer.")
    if type(package) is not types.ModuleType:
        return _restart_message(
            "an invalid cached module",
            None,
            None,
            expected_version,
            expected_bootstrap_abi,
            expected_init,
        )
    namespace = types.ModuleType.__getattribute__(package, "__dict__")
    actual_version = dict.get(namespace, "__version__")
    actual_bootstrap_abi = dict.get(namespace, "__bootstrap_abi__")
    raw_path = dict.get(namespace, "__file__")
    actual_path = _normalized_path(raw_path) if type(raw_path) is str and raw_path else None
    if (
        actual_version != expected_version
        or actual_bootstrap_abi != expected_bootstrap_abi
        or actual_path != expected_init
    ):
        return _restart_message(
            actual_version,
            actual_bootstrap_abi,
            actual_path,
            expected_version,
            expected_bootstrap_abi,
            expected_init,
        )
    return None


def _module_tree_mismatch(cached_modules, expected_init):
    expected_directory = os.path.dirname(expected_init)
    for name, module in dict.items(cached_modules):
        if type(module) is not types.ModuleType:
            return _tree_restart_message(name, "an invalid module object")
        namespace = types.ModuleType.__getattribute__(module, "__dict__")
        raw_path = dict.get(namespace, "__file__")
        if type(raw_path) is not str or not raw_path:
            return _tree_restart_message(name, "an unknown path")
        actual_path = _normalized_path(raw_path)
        try:
            inside_expected_package = os.path.commonpath((expected_directory, actual_path)) == expected_directory
        except ValueError:
            inside_expected_package = False
        if not inside_expected_package:
            return _tree_restart_message(name, actual_path)
    return None


def _partial_cache_message(cached_modules, expected_version, expected_bootstrap_abi, expected_init):
    names = ", ".join(sorted(cached_modules)[:5])
    return (
        f"A partial PyMonitor Agent module cache is still loaded ({names}); "
        f"this PyMonitor requires Agent {expected_version}, bootstrap ABI {expected_bootstrap_abi}, "
        f"from {expected_init}. Fully stop and restart the Python debuggee, then run Quick Attach again."
    )


def _tree_restart_message(name, location):
    return (
        f"PyMonitor Agent module {name} is already loaded from {location}. "
        "Fully stop and restart the Python debuggee, then run Quick Attach again."
    )


def _restart_message(
    actual_version,
    actual_bootstrap_abi,
    actual_path,
    expected_version,
    expected_bootstrap_abi,
    expected_path,
):
    loaded = actual_version if type(actual_version) is str else "unknown version"
    loaded_abi = actual_bootstrap_abi if type(actual_bootstrap_abi) is int else "unknown"
    location = actual_path or "an unknown path"
    return (
        f"PyMonitor Agent {loaded} (bootstrap ABI {loaded_abi}) is already loaded from {location}; "
        f"this PyMonitor requires Agent {expected_version} (bootstrap ABI {expected_bootstrap_abi}) "
        f"from {expected_path}. "
        "Fully stop and restart the Python debuggee, then run Quick Attach again."
    )


def _normalized_path(path):
    return os.path.normcase(os.path.realpath(path))


def _report_bootstrap_error(host, port, token, code, message):
    """Best-effort protocol error so the waiting UI fails immediately."""
    try:
        with socket.create_connection((host, port), timeout=2.0) as sock:
            sock.settimeout(2.0)
            request = _read_request(sock)
            params = request.get("params")
            candidate = params.get("token") if type(params) is dict else None
            request_id = request.get("requestId")
            if (
                request.get("protocolVersion") != _PROTOCOL_VERSION
                or request.get("messageType") != "request"
                or request.get("method") != "session.hello"
                or type(request_id) is not str
                or not request_id
                or len(request_id) > _MAX_REQUEST_ID_LENGTH
                or type(candidate) is not str
                or type(token) is not str
                or not hmac.compare_digest(candidate, token)
            ):
                return False
            _write_response(sock, request_id, code, message)
            return True
    except Exception:
        return False


def _read_request(sock):
    header_size = struct.unpack(">I", _receive_exact(sock, 4))[0]
    if not 1 <= header_size <= _MAX_HEADER_BYTES:
        raise ValueError("Invalid bootstrap request header length.")
    header = json.loads(_receive_exact(sock, header_size).decode("utf-8"))
    if type(header) is not dict or header.get("binaryLength", 0) != 0:
        raise ValueError("Invalid bootstrap request frame.")
    return header


def _receive_exact(sock, size):
    data = bytearray()
    while len(data) < size:
        chunk = sock.recv(size - len(data))
        if not chunk:
            raise EOFError("Connection closed while reading the bootstrap request.")
        data.extend(chunk)
    return bytes(data)


def _write_response(sock, request_id, code, message):
    response = {
        "protocolVersion": _PROTOCOL_VERSION,
        "messageType": "response",
        "requestId": request_id,
        "ok": False,
        "error": {
            "code": str(code)[:128],
            "message": str(message)[:_MAX_ERROR_MESSAGE_LENGTH],
            "details": {},
        },
        "binaryLength": 0,
    }
    encoded = json.dumps(response, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    if len(encoded) > _MAX_HEADER_BYTES:
        raise ValueError("Bootstrap error response is too large.")
    sock.sendall(struct.pack(">I", len(encoded)) + encoded)
