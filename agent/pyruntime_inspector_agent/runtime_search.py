import time
import types
from collections import deque

from . import classes, console_namespaces, frames, modules
from .runtime_info import timestamp
from .safe_metadata import (
    bounded_text,
    exact_dict_value,
    is_class_object,
    is_dict_object,
    type_module,
    type_name,
    type_qualified_name,
)
from .safe_objects import _safe_instance_dict


DEFAULT_MAX_RESULTS = 200
DEFAULT_MAX_OBJECTS = 100_000
DEFAULT_MAX_DEPTH = 16
MAX_RESULTS = 500
MAX_OBJECTS = 200_000
MAX_DEPTH = 32
MAX_QUERY_LENGTH = 200
MAX_CHILDREN_PER_OBJECT = 5_000
MAX_CLASSES = 5_000
_ADDRESS_RAW_ENTRY_CHECK_INTERVAL = 64


def search_runtime(
    inspector,
    handles,
    agent_thread_id,
    query,
    max_results=DEFAULT_MAX_RESULTS,
    max_objects=DEFAULT_MAX_OBJECTS,
    max_depth=DEFAULT_MAX_DEPTH,
):
    _validate(query, max_results, max_objects, max_depth)
    started = time.perf_counter()
    console_discovery = console_namespaces.list_namespaces(handles)
    roots, root_metadata = _runtime_roots(handles, agent_thread_id, console_discovery)
    graph_budget = max(1, int(max_objects * 0.8))
    graph_result = search_roots(
        inspector,
        roots,
        query,
        max_results,
        graph_budget,
        max_depth,
    )
    remaining_results = max_results - len(graph_result["items"])
    remaining_objects = max_objects - graph_result["objectsScanned"]
    if remaining_results <= 0 or remaining_objects <= 0:
        graph_result["scanComplete"] = False
        graph_result["resultLimitReached"] = remaining_results <= 0
        graph_result["objectLimitReached"] = remaining_objects <= 0
        graph_result["maxResults"] = max_results
        graph_result["maxObjects"] = max_objects
        return _finalize_runtime_result(graph_result, root_metadata, started)

    gc_result = search_roots(
        inspector,
        [_gc_root(remaining_objects)],
        query,
        remaining_results,
        remaining_objects,
        max_depth,
    )
    combined = _combine_results(graph_result, gc_result, max_results, max_objects, max_depth)
    return _finalize_runtime_result(combined, root_metadata, started)


def search_roots(
    inspector,
    roots,
    query,
    max_results=DEFAULT_MAX_RESULTS,
    max_objects=DEFAULT_MAX_OBJECTS,
    max_depth=DEFAULT_MAX_DEPTH,
):
    terms = _validate(query, max_results, max_objects, max_depth)
    started = time.perf_counter()
    results = []
    result_keys = set()
    class_matches = {}
    classes_scanned = 0
    objects_scanned = 0
    roots_scanned = 0
    depth_limit_reached = False
    object_limit_reached = False
    result_limit_reached = False
    children_truncated = False

    def add_result(
        kind,
        name,
        location,
        match_fields,
        root,
        value=None,
        object_path=None,
        class_member=None,
        depth=0,
        root_name=None,
    ):
        nonlocal result_limit_reached
        key = (kind, name, location)
        if key in result_keys:
            return True
        if len(results) >= max_results:
            result_limit_reached = True
            return False
        row = {
            "kind": kind,
            "name": bounded_text(name, "<unnamed>", 512),
            "location": bounded_text(location, "<unknown>", 2_048),
            "objectPath": bounded_text(object_path, None, 2_048),
            "matchFields": sorted(match_fields),
            "depth": depth,
            "sourceKind": root.get("sourceKind"),
            "moduleName": root.get("moduleName"),
            "frameHandle": root.get("frameHandle"),
            "scopeType": root.get("scopeType"),
            "consoleHandle": root.get("consoleHandle"),
            "consoleAttributeName": root.get("consoleAttributeName"),
            "rootName": root_name if root_name is not None else root.get("rootName"),
            "classMember": class_member,
            "value": inspector.summarize(value) if value is not None else None,
        }
        results.append(row)
        result_keys.add(key)
        return True

    pending = deque()
    expanded_by_root = []
    root_iterators = deque()
    for root_index, root in enumerate(roots):
        if result_limit_reached:
            break
        roots_scanned += 1
        object_limit_reached = object_limit_reached or root.get("entriesTruncated", False)
        expanded_by_root.append(set())
        root_location = root["location"]
        root_value = root.get("value")
        root_fields = _matching_fields(terms, {
            "name": root.get("name"),
            "location": root_location,
            "source": root.get("sourceKind"),
        })
        if root_fields:
            root_kind = {
                "module": "module",
                "frame": "frame",
                "console": "console",
            }.get(root.get("sourceKind"), "source")
            if not add_result(root_kind, root.get("name", root_location), root_location, root_fields, root, root_value, root_location):
                break
        try:
            entries = iter(root["entries"]())
        except RuntimeError:
            children_truncated = True
            continue
        root_iterators.append((root_index, root, entries))

    while root_iterators and len(pending) < max_objects:
        root_index, root, entries = root_iterators.popleft()
        try:
            name, value = next(entries)
        except StopIteration:
            continue
        except RuntimeError:
            children_truncated = True
            continue
        else:
            path = _append_path(root["location"], name)
            pending.append((root_index, root, name, value, path, 0, (id(value),), name))
            root_iterators.append((root_index, root, entries))

    if root_iterators:
        object_limit_reached = True

    while pending and objects_scanned < max_objects and not result_limit_reached:
        root_index, root, name, value, path, depth, ancestry, root_name = pending.popleft()
        objects_scanned += 1
        metadata = _value_metadata(inspector, name, value, path)
        fields = _matching_fields(terms, metadata)
        if fields:
            kind = "variable" if depth == 0 else "object"
            if not add_result(kind, name, path, fields, root, value, path, depth=depth, root_name=root_name):
                break

        cls = value if is_class_object(value) else type(value)
        class_id = id(cls)
        matches = class_matches.get(class_id)
        if matches is None and classes_scanned < MAX_CLASSES:
            matches = _search_class(inspector, cls, terms)
            class_matches[class_id] = matches
            classes_scanned += 1
        if matches:
            class_name = f"{type_module(cls)}.{type_qualified_name(cls)}"
            for match in matches:
                if match["kind"] == "class":
                    location = f"{path} / Class {class_name}"
                    if not add_result(
                        "class", class_name, location, match["matchFields"], root, value, path,
                        depth=depth, root_name=root_name,
                    ):
                        break
                else:
                    member = match["member"]
                    location = f"{path} / Class {class_name} / {member['kind']} {member['name']}"
                    if not add_result(
                        _member_result_kind(member["kind"]),
                        member["name"],
                        location,
                        match["matchFields"],
                        root,
                        value,
                        path,
                        member,
                        depth,
                        root_name,
                    ):
                        break
        if result_limit_reached:
            break

        value_identity = id(value)
        expanded = expanded_by_root[root_index]
        if value_identity in expanded:
            continue
        expanded.add(value_identity)
        children, was_truncated = _static_children(inspector, value)
        children_truncated = children_truncated or was_truncated
        if not children:
            continue
        if depth >= max_depth:
            depth_limit_reached = True
            continue
        for child_name, child in children:
            child_identity = id(child)
            if child_identity in ancestry:
                continue
            if objects_scanned + len(pending) >= max_objects:
                object_limit_reached = True
                break
            child_path = _append_path(path, child_name)
            pending.append((
                root_index, root, child_name, child, child_path, depth + 1,
                ancestry + (child_identity,), root_name,
            ))

    if pending and objects_scanned >= max_objects:
        object_limit_reached = True

    scan_complete = not (
        object_limit_reached
        or result_limit_reached
        or depth_limit_reached
        or children_truncated
        or classes_scanned >= MAX_CLASSES
    )
    return {
        "query": query.strip(),
        "items": results,
        "total": len(results),
        "objectsScanned": objects_scanned,
        "rootsScanned": roots_scanned,
        "classesScanned": classes_scanned,
        "scanComplete": scan_complete,
        "objectLimitReached": object_limit_reached,
        "resultLimitReached": result_limit_reached,
        "depthLimitReached": depth_limit_reached,
        "childrenTruncated": children_truncated,
        "maxResults": max_results,
        "maxObjects": max_objects,
        "maxDepth": max_depth,
        "durationMilliseconds": round((time.perf_counter() - started) * 1000.0, 3),
        "snapshotTimestamp": timestamp(),
    }


def _validate(query, max_results, max_objects, max_depth):
    if type(query) is not str or not query.strip() or len(query) > MAX_QUERY_LENGTH:
        raise ValueError(f"query must be a non-empty string up to {MAX_QUERY_LENGTH} characters.")
    if type(max_results) is not int or not 1 <= max_results <= MAX_RESULTS:
        raise ValueError(f"maxResults must be between 1 and {MAX_RESULTS}.")
    if type(max_objects) is not int or not 1 <= max_objects <= MAX_OBJECTS:
        raise ValueError(f"maxObjects must be between 1 and {MAX_OBJECTS}.")
    if type(max_depth) is not int or not 0 <= max_depth <= MAX_DEPTH:
        raise ValueError(f"maxDepth must be between 0 and {MAX_DEPTH}.")
    return tuple(part.casefold() for part in query.split() if part)


def _runtime_roots(
    handles,
    agent_thread_id,
    console_discovery=None,
    *,
    module_root_limit=None,
    deadline=None,
    namespace_raw_limit=None,
):
    if console_discovery is None:
        console_discovery = console_namespaces.list_namespaces(handles)
    address_bounded = module_root_limit is not None
    metadata = {
        "consoleDiscovery": console_discovery,
        "frameRootsIncluded": 0,
        "frameRootLimitReached": False,
        "moduleRootLimitReached": False,
        "moduleRootDeadlineReached": False,
        "moduleRegistryMutationDetected": False,
        "moduleRootsIncluded": 0,
        "namespaceRawLimitReached": False,
        "namespaceMutationDetected": False,
        "namespaceDeadlineReached": False,
    }
    bounded_metadata = metadata if address_bounded else None
    roots = _console_roots(
        handles,
        console_discovery,
        namespace_raw_limit=namespace_raw_limit,
        deadline=deadline,
        metadata=bounded_metadata,
    )
    registry = modules._module_registry()
    if registry is not None and not address_bounded:
        for module_name, module in modules._module_entries(registry):
            roots.append(_module_root(module_name, module))

    snapshot = frames._current_frames_snapshot()
    thread_entries = list(dict.items(snapshot))
    frame_rows = 0
    frame_limit_reached = False
    for thread_index, (thread_id, top) in enumerate(thread_entries):
        if thread_id == agent_thread_id:
            continue
        frame = top
        frame_count = 0
        while (
            frame is not None
            and frame_count < frames._MAX_FRAMES_PER_THREAD
            and frame_rows < frames._MAX_FRAME_ROWS
        ):
            frame_count += 1
            frame_rows += 1
            code = frame.f_code
            frame_handle = handles.put(frame)
            frame_name = bounded_text(getattr(code, "co_qualname", code.co_name), "<unnamed>", 512)
            filename = bounded_text(code.co_filename, "<unknown>", 1_024)
            frame_location = f"Threads / {thread_id} / {frame_name} — {filename}:{frame.f_lineno}"
            for scope_type, mapping in (
                ("locals", frame.f_locals),
                ("globals", frame.f_globals),
                ("builtins", frame.f_builtins),
            ):
                roots.append({
                    "sourceKind": "frame",
                    "name": frame_name,
                    "location": f"{frame_location} / {scope_type.title()}",
                    "frameHandle": frame_handle,
                    "scopeType": scope_type,
                    "value": None,
                    "entries": lambda mapping=mapping: _namespace_entries(
                        mapping,
                        raw_limit=namespace_raw_limit,
                        deadline=deadline,
                        metadata=bounded_metadata,
                    ),
                })
            frame = frame.f_back
        if frame is not None:
            frame_limit_reached = True
        if frame_rows >= frames._MAX_FRAME_ROWS:
            if thread_index + 1 < len(thread_entries):
                frame_limit_reached = True
            break
    metadata["frameRootsIncluded"] = frame_rows
    metadata["frameRootLimitReached"] = frame_limit_reached
    if not address_bounded or registry is None:
        return roots, metadata

    main = _bounded_module_value(
        registry,
        "__main__",
        module_root_limit,
        deadline,
        metadata,
    )
    main_root = None
    if type(main) is types.ModuleType:
        main_root = _module_root(
            "__main__",
            main,
            namespace_raw_limit,
            deadline,
            metadata,
        )
        metadata["moduleRootsIncluded"] = 1
    normal_roots = _bounded_module_roots(
        registry,
        module_root_limit,
        deadline,
        namespace_raw_limit,
        metadata,
    )
    return _address_root_sequence(main_root, normal_roots, roots), metadata


def _console_roots(
    handles,
    discovered=None,
    *,
    namespace_raw_limit=None,
    deadline=None,
    metadata=None,
):
    if discovered is None:
        discovered = console_namespaces.list_namespaces(handles)
    roots = []
    for row in discovered["items"]:
        owner = handles.get(row["consoleHandle"])
        state = _safe_instance_dict(owner)
        namespace = exact_dict_value(state, row["attributeName"])
        if type(namespace) is not dict:
            continue
        roots.append({
            "sourceKind": "console",
            "name": row["displayName"],
            "location": (
                f"Console namespaces / {row['displayName']} "
                f"@{row['ownerAddressHex']}"
            ),
            "scopeType": "console",
            "consoleHandle": row["consoleHandle"],
            "consoleAttributeName": row["attributeName"],
            "value": None,
            "entries": lambda namespace=namespace: _namespace_entries(
                namespace,
                raw_limit=namespace_raw_limit,
                deadline=deadline,
                metadata=metadata,
            ),
        })
    return roots


def _module_root(
    module_name,
    module,
    namespace_raw_limit=None,
    deadline=None,
    metadata=None,
):
    namespace = types.ModuleType.__getattribute__(module, "__dict__")
    return {
        "sourceKind": "module",
        "name": module_name,
        "location": f"Modules / {module_name}",
        "moduleName": module_name,
        "scopeType": "module",
        "value": module,
        "entries": lambda namespace=namespace: _namespace_entries(
            namespace,
            raw_limit=namespace_raw_limit,
            deadline=deadline,
            metadata=metadata,
        ),
    }


def _bounded_module_roots(
    registry,
    raw_limit,
    deadline,
    namespace_raw_limit,
    metadata,
):
    seen_names = {"__main__"}
    raw_scanned = 0
    attempts = 0
    while attempts < modules._MAX_SCAN_ATTEMPTS:
        try:
            for name, module in dict.items(registry):
                if raw_scanned >= raw_limit:
                    metadata["moduleRootLimitReached"] = True
                    return
                if (
                    raw_scanned % _ADDRESS_RAW_ENTRY_CHECK_INTERVAL == 0
                    and time.perf_counter() >= deadline
                ):
                    metadata["moduleRootDeadlineReached"] = True
                    return
                raw_scanned += 1
                if raw_scanned >= raw_limit and dict.__len__(registry) > raw_scanned:
                    metadata["moduleRootLimitReached"] = True
                if (
                    type(name) is not str
                    or name in seen_names
                    or len(name) > 500
                    or type(module) is not types.ModuleType
                ):
                    continue
                seen_names.add(name)
                metadata["moduleRootsIncluded"] += 1
                yield _module_root(
                    name,
                    module,
                    namespace_raw_limit,
                    deadline,
                    metadata,
                )
            return
        except RuntimeError:
            metadata["moduleRegistryMutationDetected"] = True
            attempts += 1
    metadata["moduleRootLimitReached"] = True


def _bounded_module_value(registry, wanted_name, raw_limit, deadline, metadata):
    raw_scanned = 0
    attempts = 0
    while attempts < modules._MAX_SCAN_ATTEMPTS:
        try:
            for name, module in dict.items(registry):
                if raw_scanned >= raw_limit:
                    metadata["moduleRootLimitReached"] = True
                    return None
                if (
                    raw_scanned % _ADDRESS_RAW_ENTRY_CHECK_INTERVAL == 0
                    and time.perf_counter() >= deadline
                ):
                    metadata["moduleRootDeadlineReached"] = True
                    return None
                raw_scanned += 1
                if type(name) is str and str.__eq__(name, wanted_name):
                    return module
            return None
        except RuntimeError:
            metadata["moduleRegistryMutationDetected"] = True
            attempts += 1
    metadata["moduleRootLimitReached"] = True
    return None


def _address_root_sequence(main_root, normal_roots, priority_roots):
    if main_root is not None:
        yield main_root
    try:
        first_normal = next(normal_roots)
    except StopIteration:
        first_normal = None
    if first_normal is not None:
        yield first_normal
    yield from priority_roots
    yield from normal_roots


def _gc_root(limit=DEFAULT_MAX_OBJECTS):
    objects = console_namespaces._gc_objects_snapshot()
    tracked_total = len(objects)
    if tracked_total > limit:
        objects = objects[:limit]
    return {
        "sourceKind": "gc",
        "name": "GC-tracked objects",
        "location": "GC-tracked objects",
        "scopeType": "gc-tracked",
        "entriesTruncated": tracked_total > limit,
        "value": None,
        "entries": lambda: (
            (
                f"{type_module(type(value))}.{type_qualified_name(type(value))} @{hex(id(value))}",
                value,
            )
            for value in objects
        ),
    }


def _combine_results(first, second, max_results, max_objects, max_depth):
    combined = dict(first)
    combined["items"] = first["items"] + second["items"]
    combined["total"] = len(combined["items"])
    combined["objectsScanned"] = first["objectsScanned"] + second["objectsScanned"]
    combined["rootsScanned"] = first["rootsScanned"] + second["rootsScanned"]
    combined["classesScanned"] = first["classesScanned"] + second["classesScanned"]
    combined["scanComplete"] = first["scanComplete"] and second["scanComplete"]
    for flag in (
        "objectLimitReached",
        "resultLimitReached",
        "depthLimitReached",
        "childrenTruncated",
    ):
        combined[flag] = first[flag] or second[flag]
    combined["maxResults"] = max_results
    combined["maxObjects"] = max_objects
    combined["maxDepth"] = max_depth
    combined["durationMilliseconds"] = round(
        first["durationMilliseconds"] + second["durationMilliseconds"], 3)
    combined["snapshotTimestamp"] = timestamp()
    return combined


def _finalize_runtime_result(result, root_metadata, started):
    discovery = root_metadata["consoleDiscovery"]
    console_complete = discovery.get("scanComplete") is True
    frame_limit_reached = root_metadata["frameRootLimitReached"]
    result.update({
        "consoleDiscoveryComplete": console_complete,
        "consoleNamespacesReturned": len(discovery.get("items", ())),
        "consoleDiscoveryScannedCount": discovery.get("scannedCount", 0),
        "consoleDiscoveryTrackedTotal": discovery.get("trackedTotal", 0),
        "consoleDiscoveryTruncated": discovery.get("truncated") is True,
        "consoleNamespaceLimitReached": discovery.get("namespaceLimitReached") is True,
        "frameRootsIncluded": root_metadata["frameRootsIncluded"],
        "frameRootLimitReached": frame_limit_reached,
    })
    result["scanComplete"] = result["scanComplete"] and console_complete and not frame_limit_reached
    result["durationMilliseconds"] = round((time.perf_counter() - started) * 1000.0, 3)
    result["snapshotTimestamp"] = timestamp()
    return result


def _namespace_entries(mapping, raw_limit=None, deadline=None, metadata=None):
    if is_dict_object(mapping):
        entries = dict.items(mapping)
    else:
        entries = frames._frame_locals_proxy_items(mapping)
    if entries is None:
        return
    raw_scanned = 0
    try:
        for name, value in entries:
            if raw_limit is not None and raw_scanned >= raw_limit:
                if metadata is not None:
                    metadata["namespaceRawLimitReached"] = True
                return
            if (
                deadline is not None
                and raw_scanned % _ADDRESS_RAW_ENTRY_CHECK_INTERVAL == 0
                and time.perf_counter() >= deadline
            ):
                if metadata is not None:
                    metadata["namespaceDeadlineReached"] = True
                return
            raw_scanned += 1
            if type(name) is str:
                yield bounded_text(name, "<unnamed>", 512), value
    except RuntimeError:
        if metadata is None:
            raise
        metadata["namespaceMutationDetected"] = True


def _static_children(inspector, value):
    exact = type(value)
    rows = []
    truncated = False
    if exact in (list, tuple):
        limit = min(len(value), MAX_CHILDREN_PER_OBJECT)
        rows = [(f"[{index}]", value[index]) for index in range(limit)]
        truncated = len(value) > limit
    elif exact in (set, frozenset):
        for index, child in enumerate(value):
            if index >= MAX_CHILDREN_PER_OBJECT:
                truncated = True
                break
            rows.append((f"[{index}]", child))
    elif exact is dict:
        for index, (key, child) in enumerate(dict.items(value)):
            if index >= MAX_CHILDREN_PER_OBJECT:
                truncated = True
                break
            rows.append((inspector._key_name(key, index), child))
    else:
        namespace = _safe_instance_dict(value)
        if type(namespace) is dict:
            for index, (name, child) in enumerate(dict.items(namespace)):
                if index >= MAX_CHILDREN_PER_OBJECT:
                    truncated = True
                    break
                label = bounded_text(name, "<unnamed>", 512) if type(name) is str else inspector._key_name(name, index)
                rows.append((label, child))
    return rows, truncated


def _value_metadata(inspector, name, value, path):
    cls = type(value)
    module_name = type_module(cls)
    qualified_name = type_qualified_name(cls)
    return {
        "name": name,
        "path": path,
        "type": type_name(cls),
        "module": module_name,
        "qualifiedType": f"{module_name}.{qualified_name}",
        "preview": inspector._preview(value, 0, set()),
        "address": hex(id(value)),
    }


def _search_class(inspector, cls, terms):
    class_name = type_name(cls)
    module_name = type_module(cls)
    qualified_name = type_qualified_name(cls)
    complete_mro = type.__getattribute__(cls, "__mro__")
    bases = type.__getattribute__(cls, "__bases__")
    metaclass = type(cls)
    class_namespace = type.__getattribute__(cls, "__dict__")
    doc = class_namespace.get("__doc__")
    class_fields = _matching_fields(terms, {
        "class": class_name,
        "classModule": module_name,
        "qualifiedClass": f"{module_name}.{qualified_name}",
        "bases": " ".join(f"{type_module(base)}.{type_qualified_name(base)}" for base in bases),
        "mro": " ".join(f"{type_module(base)}.{type_qualified_name(base)}" for base in complete_mro),
        "metaclass": f"{type_module(metaclass)}.{type_qualified_name(metaclass)}",
        "classDoc": doc if type(doc) is str else None,
    })
    results = []
    if class_fields:
        results.append({"kind": "class", "matchFields": class_fields})

    seen = set()
    scanned = 0
    for base in type.__getattribute__(cls, "__mro__"):
        namespace = type.__getattribute__(base, "__dict__")
        declared_by = type_qualified_name(base)
        for name, raw in namespace.items():
            scanned += 1
            if scanned > classes.MAX_CLASS_MEMBER_SCAN:
                return results
            if name in seen:
                continue
            seen.add(name)
            kind, signature_target = classes._classification(raw)
            signature_details = classes._signature_details(signature_target) if signature_target is not None else None
            signature = signature_details["display"] if signature_details is not None else None
            source = classes._source(signature_target)
            source_text = None if source is None else f"{source['file']}:{source['line']}"
            raw_type = type(raw)
            match_fields = _matching_fields(terms, {
                "member": name,
                "memberKind": kind,
                "declaredBy": declared_by,
                "signature": signature,
                "source": source_text,
                "memberType": type_name(raw_type),
                "memberModule": type_module(raw_type),
                "memberQualifiedType": f"{type_module(raw_type)}.{type_qualified_name(raw_type)}",
                "memberPreview": inspector._preview(raw, 0, set()),
            })
            if not match_fields:
                continue
            results.append({
                "kind": "member",
                "matchFields": match_fields,
                "member": {
                    "name": bounded_text(name, "<unnamed>", 256),
                    "kind": kind,
                    "declaredBy": bounded_text(declared_by, "<unknown>", 500),
                    "signature": signature,
                    "source": source,
                },
            })
    return results


def _matching_fields(terms, fields):
    normalized = {
        name: str(value).casefold()
        for name, value in fields.items()
        if value is not None
    }
    combined = "\n".join(normalized.values())
    if not all(term in combined for term in terms):
        return set()
    return {name for name, value in normalized.items() if any(term in value for term in terms)}


def _append_path(parent, child):
    if child.startswith("["):
        return f"{parent}{child}"
    return f"{parent} / {child}"


def _member_result_kind(kind):
    if kind in ("instance method", "staticmethod", "classmethod", "function", "method descriptor"):
        return "method"
    if kind in ("property", "data descriptor", "unknown descriptor"):
        return "property"
    return "class attribute"
