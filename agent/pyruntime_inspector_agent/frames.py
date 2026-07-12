import sys
import threading
import types

from .runtime_info import timestamp
from .safe_metadata import bounded_text
from .safe_objects import MAX_VALUE_PAGE_SIZE, _page, _safe_instance_dict

_MAX_SCAN_ATTEMPTS = 2
_MAX_THREAD_ROWS = 1000
_MAX_FRAME_ROWS = 200
_MAX_FRAMES_PER_THREAD = 100
_PY_TPFLAGS_HEAPTYPE = 1 << 9


def list_threads(agent_thread_id):
    current = _current_frames_snapshot()
    result = []
    threads, threads_truncated = _thread_snapshot()
    for thread in threads:
        state = _safe_instance_dict(thread)
        if type(state) is not dict:
            continue
        thread_id = dict.get(state, "_ident")
        if type(thread_id) is not int or thread_id == agent_thread_id:
            continue
        name = dict.get(state, "_name")
        daemon = dict.get(state, "_daemonic")
        stopped = dict.get(state, "_is_stopped")
        started_state = _safe_instance_dict(dict.get(state, "_started"))
        started = dict.get(started_state, "_flag") if type(started_state) is dict else None
        alive = started and not stopped if type(started) is bool and type(stopped) is bool else thread_id in current
        result.append({
            "threadId": thread_id,
            "name": bounded_text(name, "<unnamed>"),
            "daemon": daemon if type(daemon) is bool else None,
            "alive": alive,
            "hasTopFrame": thread_id in current,
        })
    return {
        "items": result,
        "truncated": threads_truncated,
        "limit": _MAX_THREAD_ROWS,
        "snapshotTimestamp": timestamp(),
    }


def list_frames(handles, agent_thread_id):
    snapshot = _current_frames_snapshot()
    rows = []
    truncated = False
    thread_entries = list(dict.items(snapshot))
    for thread_index, (thread_id, top) in enumerate(thread_entries):
        if thread_id == agent_thread_id:
            continue
        frames = []
        frame = top
        remaining = _MAX_FRAME_ROWS - len(rows)
        per_thread_limit = min(_MAX_FRAMES_PER_THREAD, remaining)
        while frame is not None and len(frames) < per_thread_limit:
            frames.append(frame)
            frame = frame.f_back
        if frame is not None:
            truncated = True
        frame_handles = {id(frame): handles.put(frame) for frame in frames}
        for frame in frames:
            code = frame.f_code
            raw_filename = code.co_filename
            filename = bounded_text(raw_filename, "<unknown>", 1024)
            module_name = dict.get(frame.f_globals, "__name__")
            rows.append({
                "frameHandle": frame_handles[id(frame)],
                "threadId": thread_id,
                "functionName": bounded_text(code.co_name, "<unnamed>"),
                "qualifiedName": bounded_text(getattr(code, "co_qualname", code.co_name), "<unnamed>"),
                "filename": filename,
                "lineNumber": frame.f_lineno,
                "firstLineNumber": code.co_firstlineno,
                "moduleName": bounded_text(module_name, None),
                "isAgentFrame": "pyruntime_inspector_agent" in raw_filename,
                "callerFrameHandle": frame_handles.get(id(frame.f_back)) if frame.f_back is not None else None,
                "snapshotTimestamp": timestamp(),
            })
        if len(rows) >= _MAX_FRAME_ROWS:
            if thread_index + 1 < len(thread_entries):
                truncated = True
            break
    return {
        "items": rows,
        "truncated": truncated,
        "limit": _MAX_FRAME_ROWS,
        "perThreadLimit": _MAX_FRAMES_PER_THREAD,
        "snapshotTimestamp": timestamp(),
    }


def list_scope(handles, inspector, frame_handle, scope_type, offset=0, page_size=100):
    frame = handles.get(frame_handle)
    if type(frame) is not types.FrameType:
        raise ValueError("The handle does not identify a frame.")
    if scope_type == "locals":
        mapping = frame.f_locals
    elif scope_type == "globals":
        mapping = frame.f_globals
    elif scope_type == "builtins":
        mapping = frame.f_builtins
    else:
        raise ValueError("scopeType must be locals, globals, or builtins.")
    offset, page_size = _page(offset, page_size, MAX_VALUE_PAGE_SIZE)
    # Dict insertion order is deterministic while a namespace is unchanged and
    # avoids an O(N) copy plus sort. Concurrent target changes can shift offsets.
    page, total, scan_complete, mutation_detected = _bounded_scan(
        lambda: _namespace_entries(mapping),
        offset,
        page_size,
    )
    return {
        "frameHandle": frame_handle,
        "scopeType": scope_type,
        "items": [{"name": name, "value": inspector.summarize(value)} for name, value in page],
        "offset": offset,
        "pageSize": page_size,
        "total": total,
        "ordering": "insertion",
        "scanComplete": scan_complete,
        "totalIsExact": scan_complete,
        "mutationDetected": mutation_detected,
        "snapshotTimestamp": timestamp(),
    }


def _namespace_entries(mapping):
    if isinstance(mapping, dict):
        entries = dict.items(mapping)
    else:
        entries = _frame_locals_proxy_items(mapping)
    if entries is None:
        raise ValueError("The selected frame namespace is not a dictionary.")
    for name, value in entries:
        if type(name) is str:
            yield bounded_text(name, "<unnamed>", 512), value


def _frame_locals_proxy_items(mapping):
    """Use only CPython's immutable FrameLocalsProxy C descriptor on 3.13+."""
    proxy_type = type(mapping)
    try:
        flags = type.__getattribute__(proxy_type, "__flags__")
        module_name = type.__getattribute__(proxy_type, "__module__")
        type_name = type.__getattribute__(proxy_type, "__name__")
        qualified_name = type.__getattribute__(proxy_type, "__qualname__")
        namespace = type.__getattribute__(proxy_type, "__dict__")
    except AttributeError:
        return None
    if (
        type(flags) is not int
        or flags & _PY_TPFLAGS_HEAPTYPE
        or module_name != "builtins"
        or type_name != "FrameLocalsProxy"
        or qualified_name != "FrameLocalsProxy"
    ):
        return None
    items_descriptor = namespace.get("items")
    if type(items_descriptor) is not types.MethodDescriptorType:
        return None
    try:
        return items_descriptor(mapping)
    except (TypeError, ValueError):
        return None


def _current_frames_snapshot():
    namespace = types.ModuleType.__getattribute__(sys, "__dict__")
    function = dict.get(namespace, "_current_frames")
    if (
        type(function) is not types.BuiltinFunctionType
        or getattr(function, "__self__", None) is not sys
        or getattr(function, "__name__", None) != "_current_frames"
    ):
        raise ValueError("The CPython frame snapshot function is unavailable.")
    return function()


def _thread_snapshot():
    namespace = types.ModuleType.__getattribute__(threading, "__dict__")
    registries = (dict.get(namespace, "_active"), dict.get(namespace, "_limbo"))
    threads = []
    seen = set()
    truncated = False
    for registry in registries:
        if type(registry) is not dict:
            continue
        try:
            for thread in dict.values(registry):
                identity = id(thread)
                if identity in seen:
                    continue
                if len(threads) >= _MAX_THREAD_ROWS:
                    truncated = True
                    break
                seen.add(identity)
                threads.append(thread)
        except RuntimeError:
            truncated = True
        if truncated and len(threads) >= _MAX_THREAD_ROWS:
            break
    return threads, truncated


def _bounded_scan(entries_factory, offset, page_size):
    page_end = offset + page_size
    mutation_detected = False
    for attempt_index in range(_MAX_SCAN_ATTEMPTS):
        page = []
        total = 0
        try:
            for entry in entries_factory():
                if offset <= total < page_end:
                    page.append(entry)
                total += 1
        except RuntimeError:
            mutation_detected = True
            continue
        return page, total, True, mutation_detected
    raise ValueError("The namespace changed during inspection; retry the request.")
