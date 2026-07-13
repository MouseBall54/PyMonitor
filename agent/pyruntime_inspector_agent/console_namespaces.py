import collections
import gc
import threading
import time
import types
import uuid

from . import frames, modules
from .runtime_info import timestamp
from .safe_metadata import (
    bounded_text,
    exact_dict_string_values,
    exact_dict_value,
    is_class_object,
    type_module,
    type_name,
    type_qualified_name,
)
from .safe_objects import MAX_VALUE_PAGE_SIZE, _page, _safe_instance_dict


DEFAULT_MAX_OBJECTS = 100_000
MAX_OBJECTS = 1_000_000
MAX_NAMESPACES = 100
MAX_REGISTERED_NAMESPACES = 100
_OWNER_MARKERS = ("console", "interactive", "repl", "shell", "terminal")
_NAMESPACE_FIELDS = (
    "locals",
    "globals",
    "namespace",
    "user_ns",
    "local_ns",
    "global_ns",
    "_locals",
    "_globals",
    "_namespace",
    "_user_ns",
)
_registered = collections.OrderedDict()
_registered_lock = threading.RLock()


class _RegisteredConsoleNamespace:
    def __init__(self, registration_id, display_name, namespace):
        self.registration_id = registration_id
        self.display_name = display_name
        self.namespace = namespace
        self.active = True


def register_namespace(display_name, namespace):
    if type(display_name) is not str or not display_name.strip() or len(display_name) > 256:
        raise ValueError("display_name must be a non-empty string up to 256 characters.")
    if type(namespace) is not dict:
        raise ValueError("namespace must be an exact dictionary.")
    with _registered_lock:
        if any(owner.namespace is namespace for owner in _registered.values()):
            raise ValueError("This namespace is already registered.")
        if len(_registered) >= MAX_REGISTERED_NAMESPACES:
            raise ValueError(f"At most {MAX_REGISTERED_NAMESPACES} console namespaces can be registered.")
        registration_id = uuid.uuid4().hex
        _registered[registration_id] = _RegisteredConsoleNamespace(
            registration_id,
            display_name.strip(),
            namespace,
        )
        return registration_id


def unregister_namespace(registration_id):
    if type(registration_id) is not str or not registration_id:
        return False
    with _registered_lock:
        owner = _registered.pop(registration_id, None)
        if owner is None:
            return False
        owner.active = False
        return True


def list_namespaces(handles, max_objects=DEFAULT_MAX_OBJECTS):
    if type(max_objects) is not int or not 1 <= max_objects <= MAX_OBJECTS:
        raise ValueError(f"maxObjects must be between 1 and {MAX_OBJECTS}.")

    started = time.perf_counter()
    objects = _gc_objects_snapshot()
    tracked_total = len(objects)
    scanned_count = min(tracked_total, max_objects)
    known_types = _known_console_types()
    class_cache = {}
    candidates = {}
    namespace_limit_reached = False

    for owner in _registered_snapshot():
        namespace = owner.namespace
        candidates[id(namespace)] = (
            -1,
            owner,
            "registered",
            "namespace",
            "namespace",
            owner.display_name,
            "registered",
            namespace,
        )

    for index, owner in enumerate(objects):
        if index >= scanned_count:
            break
        if type(owner) is _RegisteredConsoleNamespace:
            continue
        cls = type(owner)
        class_id = id(cls)
        if class_id not in class_cache:
            classification = _classify_owner(cls, known_types)
            if classification is None:
                classification = False
            class_cache[class_id] = classification
        else:
            classification = class_cache[class_id]
        if classification is False:
            continue

        kind, fields, priority = classification
        state = _safe_instance_dict(owner)
        if type(state) is not dict:
            continue
        owner_type = f"{type_module(cls)}.{type_qualified_name(cls)}"
        field_values = exact_dict_string_values(state, fields)
        for attribute_name in fields:
            namespace = field_values.get(attribute_name)
            if type(namespace) is not dict:
                continue
            display_attribute_name = "user_ns" if kind == "ipython" and attribute_name == "_user_ns" else attribute_name
            namespace_id = id(namespace)
            existing = candidates.get(namespace_id)
            candidate = (
                priority,
                owner,
                owner_type,
                attribute_name,
                display_attribute_name,
                f"{owner_type}.{display_attribute_name}",
                kind,
                namespace,
            )
            if existing is not None:
                if priority < existing[0]:
                    candidates[namespace_id] = candidate
                continue
            if len(candidates) >= MAX_NAMESPACES:
                namespace_limit_reached = True
                worst_key, worst = max(candidates.items(), key=lambda item: item[1][0])
                if priority >= worst[0]:
                    continue
                del candidates[worst_key]
            candidates[namespace_id] = candidate

    rows = []
    for (
        _priority,
        owner,
        owner_type,
        attribute_name,
        display_attribute_name,
        display_name,
        kind,
        namespace,
    ) in candidates.values():
        rows.append({
            "consoleHandle": handles.put(owner),
            "displayName": bounded_text(display_name, "Console namespace", 1_024),
            "ownerType": bounded_text(owner_type, "<unknown>", 1_024),
            "ownerAddressHex": hex(id(owner)),
            "attributeName": attribute_name,
            "namespaceName": display_attribute_name,
            "kind": kind,
            "entryCount": dict.__len__(namespace),
        })

    rows.sort(key=lambda row: (
        row["kind"],
        row["ownerType"].casefold(),
        row["attributeName"],
        row["ownerAddressHex"],
    ))
    scan_complete = scanned_count == tracked_total and not namespace_limit_reached
    return {
        "items": rows,
        "total": len(rows),
        "trackedTotal": tracked_total,
        "scannedCount": scanned_count,
        "maxObjects": max_objects,
        "truncated": scanned_count < tracked_total,
        "namespaceLimitReached": namespace_limit_reached,
        "scanComplete": scan_complete,
        "durationMilliseconds": round((time.perf_counter() - started) * 1000.0, 3),
        "snapshotTimestamp": timestamp(),
    }


def list_namespace(
    handles,
    inspector,
    console_handle,
    attribute_name,
    offset=0,
    page_size=100,
):
    offset, page_size = _page(offset, page_size, MAX_VALUE_PAGE_SIZE)
    if type(attribute_name) is not str or attribute_name not in _NAMESPACE_FIELDS:
        raise ValueError("attributeName does not identify a supported console namespace.")
    owner = handles.get(console_handle)
    state = _safe_instance_dict(owner)
    if type(owner) is _RegisteredConsoleNamespace and exact_dict_value(state, "active") is not True:
        raise ValueError("The selected registered console namespace is no longer active.")
    namespace = exact_dict_value(state, attribute_name)
    if type(namespace) is not dict:
        raise ValueError("The selected console namespace is no longer available.")

    page, total, scan_complete, mutation_detected = frames._bounded_scan(
        lambda: frames._namespace_entries(namespace),
        offset,
        page_size,
    )
    return {
        "consoleHandle": console_handle,
        "attributeName": attribute_name,
        "scopeType": "console",
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


def _known_console_types():
    registry = modules._module_registry()
    if registry is None:
        return {"python": (), "ipython": ()}
    return {
        "python": _module_types(registry, "code", ("InteractiveInterpreter", "InteractiveConsole")),
        "ipython": _module_types(registry, "IPython.core.interactiveshell", ("InteractiveShell",)),
    }


def _gc_objects_snapshot():
    namespace = types.ModuleType.__getattribute__(gc, "__dict__")
    function = exact_dict_value(namespace, "get_objects")
    if (
        type(function) is not types.BuiltinFunctionType
        or getattr(function, "__self__", None) is not gc
        or getattr(function, "__name__", None) != "get_objects"
    ):
        raise ValueError("The CPython GC snapshot function is unavailable.")
    return function()


def _registered_snapshot():
    with _registered_lock:
        return [owner for owner in _registered.values() if owner.active]


def _module_types(registry, module_name, names):
    module = exact_dict_value(registry, module_name)
    if type(module) is not types.ModuleType:
        return ()
    namespace = types.ModuleType.__getattribute__(module, "__dict__")
    values = exact_dict_string_values(namespace, names)
    return tuple(candidate for name in names if is_class_object(candidate := values.get(name)))


def _classify_owner(cls, known_types):
    try:
        mro = type.__getattribute__(cls, "__mro__")
    except (AttributeError, TypeError):
        return None
    if any(base is candidate for candidate in known_types["python"] for base in mro):
        return "python", ("locals",), 0
    if any(base is candidate for candidate in known_types["ipython"] for base in mro):
        return "ipython", ("_user_ns", "user_ns"), 0

    class_name = type_name(cls).casefold()
    if any(marker in class_name for marker in _OWNER_MARKERS):
        return "custom", _NAMESPACE_FIELDS, 1
    return None
