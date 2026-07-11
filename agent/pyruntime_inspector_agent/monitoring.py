import collections
import os
import sys
import threading
import time

from .runtime_info import timestamp


_TOOL_NAME = "PyRuntimeInspector"
_CANDIDATE_TOOL_IDS = (3, 4)
_SUPPORTED_EVENTS = (
    "PY_START",
    "PY_RETURN",
    "PY_YIELD",
    "PY_UNWIND",
    "RAISE",
    "LINE",
    "CALL",
)
_lock = threading.RLock()
_events = collections.deque(maxlen=5000)
_active = False
_tool_id = None
_event_names = ()
_event_mask = 0
_started_at = None
_path_prefix = ""
_sequence = 0
_dropped = 0


class MonitoringError(Exception):
    def __init__(self, code, message):
        super().__init__(message)
        self.code = code


def status():
    with _lock:
        available = hasattr(sys, "monitoring")
        return {
            "available": available,
            "minimumPythonVersion": "3.12",
            "active": _active,
            "toolId": _tool_id,
            "eventNames": list(_event_names),
            "bufferedCount": len(_events),
            "bufferCapacity": _events.maxlen,
            "droppedCount": _dropped,
            "startedAt": _started_at,
            "includePathPrefix": _path_prefix,
            "snapshotTimestamp": timestamp(),
        }


def start(event_names=None, buffer_capacity=5000, include_path_prefix=""):
    global _active, _tool_id, _event_names, _event_mask, _started_at
    global _path_prefix, _events, _sequence, _dropped
    monitoring = _require_monitoring()
    if event_names is None:
        event_names = ["PY_START", "PY_RETURN", "RAISE"]
    if type(event_names) is not list or not event_names or any(type(item) is not str for item in event_names):
        raise ValueError("eventNames must be a non-empty string array.")
    unique_names = tuple(dict.fromkeys(event_names))
    unsupported = [name for name in unique_names if name not in _SUPPORTED_EVENTS]
    if unsupported:
        raise ValueError("Unsupported monitoring events: " + ", ".join(unsupported))
    if type(buffer_capacity) is not int or not 100 <= buffer_capacity <= 10000:
        raise ValueError("bufferCapacity must be between 100 and 10000.")
    if type(include_path_prefix) is not str:
        raise ValueError("includePathPrefix must be a string.")

    with _lock:
        if _active:
            raise MonitoringError("MONITORING_ALREADY_ACTIVE", "Execution monitoring is already active.")
        selected_tool_id = _claim_tool_id(monitoring)
        try:
            event_mask = 0
            for name in unique_names:
                event = getattr(monitoring.events, name)
                monitoring.register_callback(selected_tool_id, event, _callback_for(name))
                event_mask |= event
            _events = collections.deque(maxlen=buffer_capacity)
            _sequence = 0
            _dropped = 0
            _path_prefix = os.path.normcase(os.path.abspath(include_path_prefix)) if include_path_prefix else ""
            _tool_id = selected_tool_id
            _event_names = unique_names
            _event_mask = event_mask
            _started_at = timestamp()
            _active = True
            monitoring.set_events(selected_tool_id, event_mask)
        except Exception:
            monitoring.free_tool_id(selected_tool_id)
            _reset_state(clear_events=True)
            raise
        return status()


def stop():
    with _lock:
        was_active = _active
        _release_tool_id()
        result = status()
        result["wasActive"] = was_active
        return result


def list_events(after_sequence=0, limit=500):
    if type(after_sequence) is not int or after_sequence < 0:
        raise ValueError("afterSequence must be a non-negative integer.")
    if type(limit) is not int or not 1 <= limit <= 1000:
        raise ValueError("limit must be between 1 and 1000.")
    with _lock:
        items = [item.copy() for item in _events if item["sequence"] > after_sequence][:limit]
        next_sequence = items[-1]["sequence"] if items else after_sequence
        return {
            "items": items,
            "nextSequence": next_sequence,
            "bufferedCount": len(_events),
            "bufferCapacity": _events.maxlen,
            "droppedCount": _dropped,
            "active": _active,
            "snapshotTimestamp": timestamp(),
        }


def clear():
    global _dropped
    with _lock:
        _events.clear()
        _dropped = 0
        result = status()
        result["cleared"] = True
        return result


def cleanup():
    with _lock:
        _release_tool_id()
        _reset_state(clear_events=True)


def _require_monitoring():
    monitoring = getattr(sys, "monitoring", None)
    if monitoring is None:
        raise MonitoringError("MONITORING_UNAVAILABLE", "Execution monitoring requires Python 3.12 or newer.")
    return monitoring


def _claim_tool_id(monitoring):
    for candidate in _CANDIDATE_TOOL_IDS:
        if monitoring.get_tool(candidate) is None:
            monitoring.use_tool_id(candidate, _TOOL_NAME)
            return candidate
    raise MonitoringError("TOOL_ID_CONFLICT", "Monitoring tool IDs 3 and 4 are already in use.")


def _release_tool_id():
    global _active, _tool_id, _event_names, _event_mask, _started_at, _path_prefix
    if _tool_id is not None and hasattr(sys, "monitoring"):
        monitoring = sys.monitoring
        try:
            monitoring.set_events(_tool_id, monitoring.events.NO_EVENTS)
        finally:
            monitoring.free_tool_id(_tool_id)
    _active = False
    _tool_id = None
    _event_names = ()
    _event_mask = 0
    _started_at = None
    _path_prefix = ""


def _reset_state(clear_events):
    global _active, _tool_id, _event_names, _event_mask, _started_at
    global _path_prefix, _sequence, _dropped
    _active = False
    _tool_id = None
    _event_names = ()
    _event_mask = 0
    _started_at = None
    _path_prefix = ""
    if clear_events:
        _events.clear()
        _sequence = 0
        _dropped = 0


def _callback_for(name):
    if name in ("PY_START",):
        return lambda code, instruction_offset: _record(name, code, instruction_offset, None)
    if name in ("PY_RETURN", "PY_YIELD"):
        return lambda code, instruction_offset, value: _record(name, code, instruction_offset, None)
    if name in ("PY_UNWIND", "RAISE"):
        return lambda code, instruction_offset, exception: _record(
            name, code, instruction_offset, _safe_exception_type(exception))
    if name == "LINE":
        return lambda code, line_number: _record(name, code, None, line_number)
    if name == "CALL":
        return lambda code, instruction_offset, callable_value, arg0: _record(
            name, code, instruction_offset, None)
    raise ValueError(f"Unsupported monitoring event: {name}")


def _record(event_name, code, instruction_offset, line_number, detail=None):
    global _sequence, _dropped
    filename = code.co_filename
    normalized = filename.replace("\\", "/")
    if "/pyruntime_inspector_agent/" in normalized:
        return
    if _path_prefix and not os.path.normcase(filename).startswith(_path_prefix):
        return
    with _lock:
        if not _active:
            return
        _sequence += 1
        if len(_events) == _events.maxlen:
            _dropped += 1
        _events.append({
            "sequence": _sequence,
            "timestampUnixNanoseconds": time.time_ns(),
            "threadId": threading.get_ident(),
            "eventName": event_name,
            "functionName": code.co_name,
            "qualifiedName": getattr(code, "co_qualname", code.co_name),
            "filename": filename,
            "lineNumber": line_number if line_number is not None else code.co_firstlineno,
            "instructionOffset": instruction_offset,
            "detail": detail,
        })


def _safe_exception_type(exception):
    exception_type = type(exception)
    return type.__getattribute__(exception_type, "__name__")
