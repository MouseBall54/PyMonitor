"""Fresh, cache-aware entry point for cooperative PyMonitor attach."""

import hashlib
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
_MAX_RUNTIME_SOURCE_FILES = 128
_MAX_RUNTIME_SOURCE_FILE_BYTES = 2 * 1024 * 1024
_MAX_RUNTIME_SOURCE_BYTES = 16 * 1024 * 1024
_FRESH_ENTRYPOINTS = frozenset(("bootstrap.py", "managed_launch.py"))
_IGNORED_CACHED_MODULES = frozenset((
    _PACKAGE_NAME + ".bootstrap",
    _PACKAGE_NAME + ".managed_launch",
))


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
        mismatch = _package_mismatch(
            cached,
            cached_modules,
            expected_version,
            expected_bootstrap_abi,
            expected_init,
        )
        if mismatch is not None:
            _report_bootstrap_error(host, port, token, "STALE_AGENT", mismatch)
            raise RuntimeError(mismatch)

    package = importlib.import_module(_PACKAGE_NAME)
    loaded_modules = _cached_package_modules()
    mismatch = _package_mismatch(
        package,
        loaded_modules,
        expected_version,
        expected_bootstrap_abi,
        expected_init,
    )
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


def _package_mismatch(package, cached_modules, expected_version, expected_bootstrap_abi, expected_init):
    mismatch = _module_mismatch(package, expected_version, expected_bootstrap_abi, expected_init)
    if mismatch is not None:
        return mismatch
    actual_init = _module_init_path(package)
    mismatch = _module_tree_mismatch(cached_modules, actual_init)
    if mismatch is not None or actual_init == expected_init:
        return mismatch
    relocation_mismatch = _relocated_runtime_mismatch(
        cached_modules,
        os.path.dirname(actual_init),
        os.path.dirname(expected_init),
    )
    if relocation_mismatch is None:
        return None
    return _runtime_restart_message(
        package,
        actual_init,
        expected_version,
        expected_bootstrap_abi,
        expected_init,
        relocation_mismatch,
    )


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
        or actual_path is None
        or os.path.basename(actual_path) != "__init__.py"
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


def _module_init_path(package):
    namespace = types.ModuleType.__getattribute__(package, "__dict__")
    raw_path = dict.get(namespace, "__file__")
    return _normalized_path(raw_path)


def _module_tree_mismatch(cached_modules, package_init):
    package_directory = os.path.dirname(package_init)
    for name, module in dict.items(cached_modules):
        if type(module) is not types.ModuleType:
            return _tree_restart_message(name, "an invalid module object")
        namespace = types.ModuleType.__getattribute__(module, "__dict__")
        raw_path = dict.get(namespace, "__file__")
        if type(raw_path) is not str or not raw_path:
            return _tree_restart_message(name, "an unknown path")
        actual_path = _normalized_path(raw_path)
        try:
            inside_package = os.path.commonpath((package_directory, actual_path)) == package_directory
        except ValueError:
            inside_package = False
        if not inside_package:
            return _tree_restart_message(name, actual_path)
    return None


def _relocated_runtime_mismatch(cached_modules, actual_directory, expected_directory):
    try:
        actual_manifest = _runtime_source_manifest(actual_directory)
        expected_manifest = _runtime_source_manifest(expected_directory)
    except (OSError, ValueError) as exc:
        return f"runtime sources could not be verified ({exc})"
    if set(actual_manifest) != set(expected_manifest):
        return "runtime source file sets differ"
    for relative_path, actual_digest in dict.items(actual_manifest):
        if expected_manifest[relative_path] != actual_digest:
            return f"runtime source differs: {relative_path}"

    expected_modules = {
        _module_name_from_source(relative_path)
        for relative_path in expected_manifest
    }
    cached_runtime_modules = set(cached_modules).difference(_IGNORED_CACHED_MODULES)
    if cached_runtime_modules != expected_modules:
        return "cached runtime module set differs"
    for name in cached_runtime_modules:
        module = cached_modules[name]
        namespace = types.ModuleType.__getattribute__(module, "__dict__")
        actual_path = _normalized_path(dict.get(namespace, "__file__"))
        relative_path = _relative_source_path(actual_directory, actual_path)
        if _module_name_from_source(relative_path) != name:
            return f"cached runtime module path differs: {name}"
    return None


def _runtime_source_manifest(package_directory):
    package_directory = _normalized_path(package_directory)
    sources = []
    for current_directory, directory_names, file_names in os.walk(package_directory):
        directory_names[:] = sorted(name for name in directory_names if name != "__pycache__")
        for file_name in sorted(file_names):
            if not file_name.endswith(".py"):
                continue
            source_path = _normalized_path(os.path.join(current_directory, file_name))
            relative_path = _relative_source_path(package_directory, source_path)
            if relative_path in _FRESH_ENTRYPOINTS:
                continue
            sources.append((relative_path, source_path))
            if len(sources) > _MAX_RUNTIME_SOURCE_FILES:
                raise ValueError("too many runtime source files")

    manifest = {}
    total_bytes = 0
    for relative_path, source_path in sorted(sources):
        source_size = os.path.getsize(source_path)
        if source_size > _MAX_RUNTIME_SOURCE_FILE_BYTES:
            raise ValueError(f"runtime source is too large: {relative_path}")
        total_bytes += source_size
        if total_bytes > _MAX_RUNTIME_SOURCE_BYTES:
            raise ValueError("runtime source payload is too large")
        digest = hashlib.sha256()
        with open(source_path, "rb") as source:
            while True:
                block = source.read(64 * 1024)
                if not block:
                    break
                digest.update(block)
        manifest[relative_path] = digest.hexdigest()
    if not manifest:
        raise ValueError("runtime source payload is empty")
    return manifest


def _relative_source_path(package_directory, source_path):
    try:
        if os.path.commonpath((package_directory, source_path)) != package_directory:
            raise ValueError("runtime source is outside the package directory")
    except ValueError as exc:
        raise ValueError("runtime source is outside the package directory") from exc
    return os.path.relpath(source_path, package_directory).replace(os.sep, "/")


def _module_name_from_source(relative_path):
    parts = relative_path[:-3].split("/")
    if parts[-1] == "__init__":
        parts.pop()
    return _PACKAGE_NAME + ("." + ".".join(parts) if parts else "")


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


def _runtime_restart_message(
    package,
    actual_path,
    expected_version,
    expected_bootstrap_abi,
    expected_path,
    detail,
):
    namespace = types.ModuleType.__getattribute__(package, "__dict__")
    actual_version = dict.get(namespace, "__version__")
    actual_bootstrap_abi = dict.get(namespace, "__bootstrap_abi__")
    loaded = actual_version if type(actual_version) is str else "unknown version"
    loaded_abi = actual_bootstrap_abi if type(actual_bootstrap_abi) is int else "unknown"
    return (
        f"PyMonitor Agent {loaded} (bootstrap ABI {loaded_abi}) is already loaded from {actual_path}, "
        f"but its runtime sources are incompatible with this PyMonitor ({detail}); "
        f"this PyMonitor requires Agent {expected_version} "
        f"(bootstrap ABI {expected_bootstrap_abi}) from {expected_path}. "
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
