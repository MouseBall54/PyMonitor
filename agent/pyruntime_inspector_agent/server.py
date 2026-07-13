import hmac
import os
import socket
import threading

from . import arrays, classes, dataframes, gc_objects, matplotlib_figures, memory, modules, monitoring, runtime_search
from .frames import list_frames, list_scope, list_threads
from .handles import HandleStore, ObjectExpiredError
from .monitoring import MonitoringError
from .protocol import PROTOCOL_VERSION, ProtocolError, read_frame, write_frame
from .runtime_info import get_runtime_info
from .safe_objects import SafeObjectInspector

AGENT_VERSION = "26.7.11"
BOOTSTRAP_ABI = 2
_MAX_REQUEST_ID_LENGTH = 128
_MAX_METHOD_LENGTH = 128
_MAX_RESULT_STRING_CHARS = 4096
_MAX_RESULT_TEXT_CHARS = 256 * 1024
_MAX_RESULT_COLLECTION_ITEMS = 2000
_active_agent = None
_active_agent_lock = threading.Lock()


class ActiveAgentConflictError(RuntimeError):
    code = "ACTIVE_AGENT_CONFLICT"


class InspectorAgent:
    def __init__(self, host, port, token, attach_mode):
        if host != "127.0.0.1":
            raise ValueError("Phase 0 only permits the 127.0.0.1 loopback host.")
        if type(port) is not int or not 1 <= port <= 65535:
            raise ValueError("A valid controller port is required.")
        if type(token) is not str or len(token) < 64:
            raise ValueError("A 256-bit token encoded as at least 64 characters is required.")
        self._host = host
        self._port = port
        self._token = token
        self._attach_mode = attach_mode
        self._handles = HandleStore()
        self._objects = SafeObjectInspector(self._handles)
        self._thread = threading.Thread(target=self._run, name="PyMonitorAgent", daemon=True)
        # debugpy/pydevd suspends ordinary application threads when a breakpoint
        # is hit.  These marker attributes are the debugger's documented
        # internal opt-out and keep the read-only inspector transport responsive
        # while the debuggee's application threads are paused.  They are plain
        # Thread attributes and are harmless when no debugger is installed.
        self._thread.pydev_do_not_trace = True
        self._thread.is_pydev_daemon_thread = True
        self._stopped = threading.Event()

    def start(self):
        self._thread.start()
        return self

    def wait(self, timeout=None):
        return self._stopped.wait(timeout)

    @property
    def is_stopped(self):
        return self._stopped.is_set()

    def matches_connection(self, host, port, token, attach_mode):
        return (
            host == self._host
            and port == self._port
            and type(token) is str
            and hmac.compare_digest(token, self._token)
            and attach_mode == self._attach_mode
        )

    def _run(self):
        try:
            with socket.create_connection((self._host, self._port), timeout=10.0) as sock:
                sock.settimeout(None)
                self._serve(sock)
        except (ConnectionError, OSError, ProtocolError, EOFError):
            pass
        finally:
            self._handles.clear()
            memory.cleanup()
            monitoring.cleanup()
            self._stopped.set()

    def _serve(self, sock):
        authenticated = False
        while True:
            request, binary = read_frame(sock)
            request_id = request.get("requestId")
            method = request.get("method")
            if (
                request.get("protocolVersion") != PROTOCOL_VERSION
                or request.get("messageType") != "request"
                or type(request_id) is not str
                or not request_id
                or len(request_id) > _MAX_REQUEST_ID_LENGTH
                or type(method) is not str
                or not method
                or len(method) > _MAX_METHOD_LENGTH
                or binary
            ):
                self._send_error(sock, request_id, "INVALID_REQUEST", "The request envelope is invalid.")
                return
            if not authenticated:
                if method != "session.hello":
                    self._send_error(sock, request_id, "AUTH_REQUIRED", "Authenticate before inspection.")
                    return
                candidate = request.get("params", {}).get("token")
                if type(candidate) is not str or not hmac.compare_digest(candidate, self._token):
                    self._send_error(sock, request_id, "AUTH_FAILED", "The session token is invalid.")
                    return
                authenticated = True
                self._send_result(sock, request_id, {
                    "protocolVersion": PROTOCOL_VERSION,
                    "agentVersion": AGENT_VERSION,
                    "bootstrapAbi": BOOTSTRAP_ABI,
                })
                continue
            try:
                result, binary, detach = self._dispatch(method, request.get("params", {}))
                self._send_result(sock, request_id, result, binary)
                if detach:
                    return
            except ObjectExpiredError as exc:
                self._send_error(sock, request_id, "OBJECT_EXPIRED", str(exc))
            except MonitoringError as exc:
                self._send_error(sock, request_id, exc.code, str(exc))
            except (KeyError, TypeError, ValueError) as exc:
                self._send_error(sock, request_id, "INVALID_ARGUMENT", str(exc))
            except Exception:
                self._send_error(sock, request_id, "INSPECTION_FAILED", "The inspection request could not be completed.")

    def _dispatch(self, method, params):
        if type(params) is not dict:
            raise ValueError("params must be an object.")
        if method == "session.detach":
            return {"detached": True}, b"", True
        if method == "runtime.getInfo":
            return get_runtime_info(AGENT_VERSION, self._attach_mode), b"", False
        if method == "threads.list":
            return list_threads(self._thread.ident), b"", False
        if method == "frames.list":
            return list_frames(self._handles, self._thread.ident), b"", False
        if method == "scopes.list":
            return list_scope(self._handles, self._objects, params["frameHandle"], params["scopeType"], params.get("offset", 0), params.get("pageSize", 100)), b"", False
        if method == "modules.list":
            return modules.list_modules(params.get("offset", 0), params.get("pageSize", 100)), b"", False
        if method == "modules.listNamespace":
            return modules.list_namespace(self._objects, params["moduleName"], params.get("offset", 0), params.get("pageSize", 100)), b"", False
        if method == "gc.listObjects":
            return gc_objects.list_objects(
                self._objects,
                params.get("query", ""),
                params.get("offset", 0),
                params.get("pageSize", 100),
                params.get("maxObjects", gc_objects.DEFAULT_MAX_OBJECTS),
            ), b"", False
        if method == "runtime.search":
            return runtime_search.search_runtime(
                self._objects,
                self._handles,
                self._thread.ident,
                params["query"],
                params.get("maxResults", runtime_search.DEFAULT_MAX_RESULTS),
                params.get("maxObjects", runtime_search.DEFAULT_MAX_OBJECTS),
                params.get("maxDepth", runtime_search.DEFAULT_MAX_DEPTH),
            ), b"", False
        if method == "objects.describe":
            return self._objects.describe(params["handleId"]), b"", False
        if method == "objects.listChildren":
            return self._objects.list_children(
                params["handleId"],
                params.get("offset", 0),
                params.get("pageSize", 100),
                params.get("depth", 0),
                params.get("ancestorIdentityTokens"),
            ), b"", False
        if method == "objects.release":
            return {"released": self._handles.release(params["handleId"])}, b"", False
        if method == "classes.describe":
            return classes.describe(self._handles.get(params["handleId"])), b"", False
        if method == "arrays.describe":
            return arrays.describe(self._handles.get(params["handleId"]), self._objects.summarize), b"", False
        if method == "arrays.preview":
            metadata, binary = arrays.preview(
                self._handles.get(params["handleId"]),
                params.get("maxWidth", 1024),
                params.get("maxHeight", 1024),
                params.get("layout"),
                params.get("colorOrder", "RGB"),
                params.get("enabledChannels"),
                params.get("sliceAxis"),
                params.get("sliceIndex"),
                params.get("normalization", "AUTO"),
                params.get("percentileLow", 1.0),
                params.get("percentileHigh", 99.0),
            )
            return metadata, binary, False
        if method == "arrays.tile":
            metadata, binary = arrays.tile(
                self._handles.get(params["handleId"]),
                params["x"],
                params["y"],
                params["width"],
                params["height"],
                params.get("layout"),
                params.get("colorOrder", "RGB"),
                params.get("enabledChannels"),
                params.get("sliceAxis"),
                params.get("sliceIndex"),
                params.get("normalization", "AUTO"),
                params.get("percentileLow", 1.0),
                params.get("percentileHigh", 99.0),
            )
            return metadata, binary, False
        if method == "arrays.histogram":
            return arrays.histogram(
                self._handles.get(params["handleId"]),
                params.get("channel", 0),
                params.get("bins", 256),
                params.get("layout"),
                params.get("sliceAxis"),
                params.get("sliceIndex"),
            ), b"", False
        if method == "arrays.pixel":
            return arrays.pixel(
                self._handles.get(params["handleId"]),
                params["coordinates"],
                params.get("layout"),
                params.get("sliceAxis"),
                params.get("sliceIndex"),
            ), b"", False
        if method == "dataframes.describe":
            return dataframes.describe(self._handles.get(params["handleId"])), b"", False
        if method == "dataframes.preview":
            return dataframes.preview(
                self._handles.get(params["handleId"]),
                params.get("rowOffset", 0),
                params.get("rowCount", 50),
                params.get("columnOffset", 0),
                params.get("columnCount", 20),
            ), b"", False
        if method == "figures.describe":
            return matplotlib_figures.describe(self._handles.get(params["handleId"])), b"", False
        if method == "figures.preview":
            metadata, binary = matplotlib_figures.preview(
                self._handles.get(params["handleId"]),
                params.get("maxWidth", matplotlib_figures.MAX_PREVIEW_DIMENSION),
                params.get("maxHeight", matplotlib_figures.MAX_PREVIEW_DIMENSION),
            )
            return metadata, binary, False
        if method == "memory.status":
            return memory.status(), b"", False
        if method == "memory.start":
            return memory.start(params.get("tracebackDepth", 1)), b"", False
        if method == "memory.stop":
            return memory.stop(), b"", False
        if method == "memory.snapshot":
            return memory.take_snapshot(params.get("label")), b"", False
        if method == "memory.listSnapshots":
            return memory.list_snapshots(), b"", False
        if method == "memory.statistics":
            return memory.statistics(params.get("limit", 50), params.get("groupBy", "lineno")), b"", False
        if method == "memory.diff":
            return memory.diff(
                params["beforeSnapshotId"],
                params["afterSnapshotId"],
                params.get("limit", 50),
                params.get("groupBy", "lineno"),
            ), b"", False
        if method == "execution.status":
            return monitoring.status(), b"", False
        if method == "execution.start":
            return monitoring.start(
                params.get("eventNames"),
                params.get("bufferCapacity", 5000),
                params.get("includePathPrefix", ""),
            ), b"", False
        if method == "execution.stop":
            return monitoring.stop(), b"", False
        if method == "execution.list":
            return monitoring.list_events(params.get("afterSequence", 0), params.get("limit", 500)), b"", False
        if method == "execution.clear":
            return monitoring.clear(), b"", False
        raise ValueError(f"Unknown method: {method}")

    @staticmethod
    def _send_result(sock, request_id, result, binary=b""):
        bounded_result, truncated = _bound_result(result)
        if truncated and type(bounded_result) is dict:
            bounded_result = dict(bounded_result)
            bounded_result["responseTruncated"] = True
        write_frame(sock, {"protocolVersion": PROTOCOL_VERSION, "messageType": "response", "requestId": request_id, "ok": True, "result": bounded_result}, binary)

    @staticmethod
    def _send_error(sock, request_id, code, message):
        bounded_error, _ = _bound_result({"code": code, "message": message, "details": {}})
        safe_request_id = request_id if type(request_id) is str and len(request_id) <= _MAX_REQUEST_ID_LENGTH else None
        write_frame(sock, {"protocolVersion": PROTOCOL_VERSION, "messageType": "response", "requestId": safe_request_id, "ok": False, "error": bounded_error})


def _bound_result(value):
    state = {"remaining": _MAX_RESULT_TEXT_CHARS, "truncated": False}
    return _bound_result_value(value, state), state["truncated"]


def _bound_result_value(value, state):
    if type(value) is str:
        allowed = min(_MAX_RESULT_STRING_CHARS, state["remaining"])
        if len(value) <= allowed:
            state["remaining"] -= len(value)
            return value
        state["truncated"] = True
        if allowed <= 0:
            return ""
        clipped = value[:max(0, allowed - 1)] + "…"
        state["remaining"] -= len(clipped)
        return clipped
    if value is None or type(value) in (bool, int, float):
        return value
    if type(value) is dict:
        result = {}
        for index, (key, item) in enumerate(dict.items(value)):
            if index >= _MAX_RESULT_COLLECTION_ITEMS:
                state["truncated"] = True
                break
            if type(key) is not str:
                state["truncated"] = True
                continue
            bounded_key = key[:_MAX_RESULT_STRING_CHARS]
            if bounded_key != key:
                state["truncated"] = True
            result[bounded_key] = _bound_result_value(item, state)
        return result
    if type(value) in (list, tuple):
        if len(value) > _MAX_RESULT_COLLECTION_ITEMS:
            state["truncated"] = True
        return [
            _bound_result_value(item, state)
            for item in value[:_MAX_RESULT_COLLECTION_ITEMS]
        ]
    state["truncated"] = True
    return None


def start_inspector(host=None, port=None, token=None, attach_mode=None):
    global _active_agent
    selected_host = host or os.environ.get("PY_INSPECTOR_HOST", "127.0.0.1")
    raw_port = port if port is not None else os.environ.get("PY_INSPECTOR_PORT")
    selected_token = token or os.environ.get("PY_INSPECTOR_TOKEN")
    selected_attach_mode = attach_mode or os.environ.get("PY_INSPECTOR_ATTACH_MODE", "cooperative")
    if raw_port is None or selected_token is None:
        raise ValueError("PY_INSPECTOR_PORT and PY_INSPECTOR_TOKEN are required.")
    selected_port = int(raw_port)
    with _active_agent_lock:
        if _active_agent is not None and not _active_agent.is_stopped:
            if not _active_agent.matches_connection(
                selected_host,
                selected_port,
                selected_token,
                selected_attach_mode,
            ):
                raise ActiveAgentConflictError(
                    "A PyMonitor Agent is already active with different connection settings. "
                    "Detach its current PyMonitor session or fully restart the Python debuggee, then try again."
                )
            return _active_agent
        _active_agent = InspectorAgent(selected_host, selected_port, selected_token, selected_attach_mode).start()
        return _active_agent
