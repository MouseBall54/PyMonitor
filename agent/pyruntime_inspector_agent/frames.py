import sys
import threading
import types

from .runtime_info import timestamp
from .safe_objects import _page


def list_threads(agent_thread_id):
    current = sys._current_frames()
    result = []
    for thread in threading.enumerate():
        if thread.ident == agent_thread_id:
            continue
        result.append({
            "threadId": thread.ident,
            "name": thread.name,
            "daemon": thread.daemon,
            "alive": thread.is_alive(),
            "hasTopFrame": thread.ident in current,
        })
    return {"items": result, "snapshotTimestamp": timestamp()}


def list_frames(handles, agent_thread_id):
    snapshot = sys._current_frames()
    rows = []
    for thread_id, top in snapshot.items():
        if thread_id == agent_thread_id:
            continue
        frames = []
        frame = top
        while frame is not None:
            frames.append(frame)
            frame = frame.f_back
        frame_handles = {id(frame): handles.put(frame) for frame in frames}
        for frame in frames:
            code = frame.f_code
            filename = code.co_filename
            rows.append({
                "frameHandle": frame_handles[id(frame)],
                "threadId": thread_id,
                "functionName": code.co_name,
                "qualifiedName": getattr(code, "co_qualname", code.co_name),
                "filename": filename,
                "lineNumber": frame.f_lineno,
                "firstLineNumber": code.co_firstlineno,
                "moduleName": frame.f_globals.get("__name__"),
                "isAgentFrame": "pyruntime_inspector_agent" in filename.replace("\\", "/"),
                "callerFrameHandle": frame_handles.get(id(frame.f_back)) if frame.f_back is not None else None,
                "snapshotTimestamp": timestamp(),
            })
    return {"items": rows, "snapshotTimestamp": timestamp()}


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
    offset, page_size = _page(offset, page_size)
    entries = [(name, value) for name, value in list(mapping.items()) if type(name) is str]
    entries.sort(key=lambda item: item[0])
    page = entries[offset:offset + page_size]
    return {
        "frameHandle": frame_handle,
        "scopeType": scope_type,
        "items": [{"name": name, "value": inspector.summarize(value)} for name, value in page],
        "offset": offset,
        "pageSize": page_size,
        "total": len(entries),
        "snapshotTimestamp": timestamp(),
    }
