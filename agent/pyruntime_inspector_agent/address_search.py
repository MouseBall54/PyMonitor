import collections
import re
import struct
import time
import types
import weakref
from collections import deque

from . import console_namespaces, runtime_search
from .runtime_info import timestamp
from .safe_metadata import bounded_text, is_class_object, type_module, type_qualified_name
from .safe_objects import _safe_instance_dict


DEFAULT_MAX_RESULTS = runtime_search.DEFAULT_MAX_RESULTS
DEFAULT_MAX_OBJECTS = runtime_search.DEFAULT_MAX_OBJECTS
DEFAULT_MAX_DEPTH = runtime_search.DEFAULT_MAX_DEPTH
MAX_RESULTS = runtime_search.MAX_RESULTS
MAX_OBJECTS = runtime_search.MAX_OBJECTS
MAX_DEPTH = runtime_search.MAX_DEPTH
MAX_CHILDREN_PER_OBJECT = runtime_search.MAX_CHILDREN_PER_OBJECT
DEFAULT_MAX_EDGES = 250_000
MAX_EDGES = 2_000_000
DEFAULT_MAX_DURATION_MS = 1_500
MAX_DURATION_MS = 10_000
_DEADLINE_CHECK_INTERVAL = 256
_ROOT_PRIORITY_QUOTA = 3
_ROOT_PREFETCH_LIMIT = 64
_ROOT_PHASE_FRACTION = 0.75
_POINTER_BITS = struct.calcsize("P") * 8
_POINTER_MAX = (1 << _POINTER_BITS) - 1
_MAX_POINTER_DIGITS = _POINTER_BITS // 4
_ADDRESS_PATTERN = re.compile(r"0[xX][0-9a-fA-F]+\Z")


def find_address(
    inspector,
    handles,
    agent_thread_id,
    address,
    max_results=DEFAULT_MAX_RESULTS,
    max_objects=DEFAULT_MAX_OBJECTS,
    max_depth=DEFAULT_MAX_DEPTH,
    max_edges=DEFAULT_MAX_EDGES,
    max_duration_ms=DEFAULT_MAX_DURATION_MS,
):
    """Find live objects and safe owner edges for an exact CPython identity address."""
    target_address, address_hex = _validate(
        address,
        max_results,
        max_objects,
        max_depth,
        max_edges,
        max_duration_ms,
    )
    started = time.perf_counter()
    discovery_deadline = started + (max_duration_ms / 1_000.0)

    # Capture the genuine CPython GC snapshot before building roots or result
    # structures so the scan does not primarily rediscover its own bookkeeping.
    gc_snapshot = console_namespaces._gc_objects_snapshot()
    if type(gc_snapshot) is not list:
        raise ValueError("The CPython GC snapshot is invalid.")
    gc_tracked_total = len(gc_snapshot)
    discovery_deadline_reached = time.perf_counter() >= discovery_deadline

    state = {
        "items": [],
        "resultKeys": set(),
        "targetFound": False,
        "targetSummary": None,
        "objectsScanned": 0,
        "rootObjectsScanned": 0,
        "gcObjectsScanned": 0,
        "rootsScanned": 0,
        "edgesScanned": 0,
        "maxEdges": max_edges,
        "deadline": discovery_deadline,
        "rootDeadline": None,
        "rootEdgeLimit": None,
        "objectLimitReached": False,
        "resultLimitReached": False,
        "depthLimitReached": False,
        "childrenTruncated": False,
        "edgeLimitReached": False,
        "deadlineReached": False,
        "rootBudgetReached": False,
    }

    if discovery_deadline_reached:
        console_discovery = _incomplete_console_discovery(gc_tracked_total, max_objects)
    else:
        console_discovery = console_namespaces.list_namespaces(
            handles,
            max_objects,
            _objects_snapshot=gc_snapshot,
            _raw_entry_limit=MAX_CHILDREN_PER_OBJECT,
            _deadline=discovery_deadline,
        )
        discovery_deadline_reached = (
            console_discovery.get("deadlineReached") is True
            or time.perf_counter() >= discovery_deadline
        )

    gc_budget = min(gc_tracked_total, max_objects // 2)
    graph_budget = max(1, max_objects - gc_budget)
    scan_started = time.perf_counter()
    state["deadline"] = scan_started + (max_duration_ms / 1_000.0)
    state["rootDeadline"] = (
        scan_started + (max_duration_ms / 1_000.0) * _ROOT_PHASE_FRACTION
        if gc_tracked_total
        else state["deadline"]
    )
    gc_edge_reserve = max_edges // 4 if gc_tracked_total and max_edges > 1 else 0
    state["rootEdgeLimit"] = max_edges - gc_edge_reserve
    roots, root_metadata = runtime_search._runtime_roots(
        handles,
        agent_thread_id,
        console_discovery,
        module_root_limit=graph_budget,
        deadline=state["deadline"],
        namespace_raw_limit=MAX_CHILDREN_PER_OBJECT,
    )
    root_metadata["consoleDiscovery"] = console_discovery
    excluded_gc_identities = _handle_store_internal_ids(handles)
    excluded_gc_identities.update((
        id(gc_snapshot),
        id(state),
        id(state["items"]),
        id(state["resultKeys"]),
    ))

    root_scan_complete = _search_runtime_roots(
        inspector,
        roots,
        target_address,
        max_results,
        graph_budget,
        max_depth,
        state,
    )
    state["rootDeadline"] = None
    state["rootEdgeLimit"] = None
    _check_deadline(state, force=True)

    gc_scan_complete = False
    if not state["resultLimitReached"] and not state["deadlineReached"]:
        gc_scan_complete = _search_gc_snapshot(
            inspector,
            gc_snapshot,
            excluded_gc_identities,
            target_address,
            max_results,
            max_objects,
            max_depth,
            state,
        )
        _check_deadline(state, force=True)

    discovery = root_metadata["consoleDiscovery"]
    console_complete = discovery.get("scanComplete") is True
    console_raw_limit_reached = discovery.get("rawEntryLimitReached") is True
    console_mutation_detected = discovery.get("mutationDetected") is True
    frame_limit_reached = root_metadata["frameRootLimitReached"]
    module_root_limit_reached = root_metadata.get("moduleRootLimitReached") is True
    module_root_deadline_reached = root_metadata.get("moduleRootDeadlineReached") is True
    module_mutation_detected = root_metadata.get("moduleRegistryMutationDetected") is True
    namespace_raw_limit_reached = root_metadata.get("namespaceRawLimitReached") is True
    namespace_mutation_detected = root_metadata.get("namespaceMutationDetected") is True
    namespace_deadline_reached = root_metadata.get("namespaceDeadlineReached") is True
    deadline_reached = (
        discovery_deadline_reached
        or module_root_deadline_reached
        or namespace_deadline_reached
        or state["deadlineReached"]
    )
    scan_complete = (
        root_scan_complete
        and gc_scan_complete
        and console_complete
        and not console_raw_limit_reached
        and not console_mutation_detected
        and not frame_limit_reached
        and not module_root_limit_reached
        and not module_root_deadline_reached
        and not module_mutation_detected
        and not namespace_raw_limit_reached
        and not namespace_mutation_detected
        and not state["rootBudgetReached"]
        and not state["objectLimitReached"]
        and not state["resultLimitReached"]
        and not state["depthLimitReached"]
        and not state["childrenTruncated"]
        and not state["edgeLimitReached"]
        and not deadline_reached
    )
    return {
        "mode": "address",
        "addressHex": address_hex,
        "targetFound": state["targetFound"],
        "items": state["items"],
        "total": len(state["items"]),
        "objectsScanned": state["objectsScanned"],
        "rootObjectsScanned": state["rootObjectsScanned"],
        "gcObjectsScanned": state["gcObjectsScanned"],
        "rootsScanned": state["rootsScanned"],
        "edgesScanned": state["edgesScanned"],
        "gcTrackedTotal": gc_tracked_total,
        "scanComplete": scan_complete,
        "gcScanComplete": gc_scan_complete,
        "gcSnapshotTruncated": not gc_scan_complete,
        "objectLimitReached": state["objectLimitReached"],
        "resultLimitReached": state["resultLimitReached"],
        "depthLimitReached": state["depthLimitReached"],
        "childrenTruncated": state["childrenTruncated"],
        "edgeLimitReached": state["edgeLimitReached"],
        "deadlineReached": deadline_reached,
        "rootBudgetReached": state["rootBudgetReached"],
        "maxResults": max_results,
        "maxObjects": max_objects,
        "maxDepth": max_depth,
        "maxEdges": max_edges,
        "maxDurationMilliseconds": max_duration_ms,
        "snapshotAllocationBounded": False,
        "consoleDiscoveryComplete": console_complete,
        "consoleNamespacesReturned": len(discovery.get("items", ())),
        "consoleDiscoveryScannedCount": discovery.get("scannedCount", 0),
        "consoleDiscoveryTrackedTotal": discovery.get("trackedTotal", 0),
        "consoleDiscoveryTruncated": discovery.get("truncated") is True,
        "consoleNamespaceLimitReached": discovery.get("namespaceLimitReached") is True,
        "consoleRawEntryLimitReached": console_raw_limit_reached,
        "consoleMutationDetected": console_mutation_detected,
        "frameRootsIncluded": root_metadata["frameRootsIncluded"],
        "frameRootLimitReached": frame_limit_reached,
        "moduleRootsIncluded": root_metadata.get("moduleRootsIncluded", 0),
        "moduleRootLimitReached": module_root_limit_reached,
        "moduleRootDeadlineReached": module_root_deadline_reached,
        "moduleRegistryMutationDetected": module_mutation_detected,
        "namespaceRawLimitReached": namespace_raw_limit_reached,
        "namespaceMutationDetected": namespace_mutation_detected,
        "namespaceDeadlineReached": namespace_deadline_reached,
        "consoleDeadlineReached": discovery.get("deadlineReached") is True,
        "durationMilliseconds": round((time.perf_counter() - started) * 1000.0, 3),
        "snapshotTimestamp": timestamp(),
    }


def _validate(address, max_results, max_objects, max_depth, max_edges, max_duration_ms):
    if type(address) is not str:
        raise ValueError("address must be a hexadecimal CPython object address.")
    candidate = address.strip()
    if (
        not candidate
        or _ADDRESS_PATTERN.fullmatch(candidate) is None
    ):
        raise ValueError("address must use the form 0x followed by hexadecimal digits.")
    significant_digits = candidate[2:].lstrip("0")
    if not significant_digits:
        raise ValueError(f"address must be between 0x1 and {hex(_POINTER_MAX)}.")
    if len(significant_digits) > _MAX_POINTER_DIGITS:
        raise ValueError(f"address must be between 0x1 and {hex(_POINTER_MAX)}.")
    value = int(significant_digits, 16)
    if value > _POINTER_MAX:
        raise ValueError(f"address must be between 0x1 and {hex(_POINTER_MAX)}.")
    if type(max_results) is not int or not 1 <= max_results <= MAX_RESULTS:
        raise ValueError(f"maxResults must be between 1 and {MAX_RESULTS}.")
    if type(max_objects) is not int or not 1 <= max_objects <= MAX_OBJECTS:
        raise ValueError(f"maxObjects must be between 1 and {MAX_OBJECTS}.")
    if type(max_depth) is not int or not 0 <= max_depth <= MAX_DEPTH:
        raise ValueError(f"maxDepth must be between 0 and {MAX_DEPTH}.")
    if type(max_edges) is not int or not 1 <= max_edges <= MAX_EDGES:
        raise ValueError(f"maxEdges must be between 1 and {MAX_EDGES}.")
    if type(max_duration_ms) is not int or not 1 <= max_duration_ms <= MAX_DURATION_MS:
        raise ValueError(f"maxDurationMilliseconds must be between 1 and {MAX_DURATION_MS}.")
    return value, hex(value)


def _search_runtime_roots(
    inspector,
    roots,
    target_address,
    max_results,
    max_objects,
    max_depth,
    state,
):
    pending = deque()
    priority_root_iterators = deque()
    root_iterators = deque()
    root_source = iter(roots)
    root_source_exhausted = False
    root_entries_incomplete = False
    root_pulls_blocked = False
    priority_streak = 0

    def pull_root():
        nonlocal root_source_exhausted, root_entries_incomplete, root_pulls_blocked
        if root_source_exhausted or root_pulls_blocked:
            return False
        if _check_root_budget(
            state,
            force=state["rootsScanned"] % _DEADLINE_CHECK_INTERVAL == 0,
        ):
            root_entries_incomplete = True
            root_pulls_blocked = True
            return False
        try:
            root = next(root_source)
        except StopIteration:
            root_source_exhausted = True
            return False
        except RuntimeError:
            state["childrenTruncated"] = True
            root_entries_incomplete = True
            root_source_exhausted = True
            return False
        state["rootsScanned"] += 1
        if root.get("entriesTruncated") is True:
            state["objectLimitReached"] = True
            root_entries_incomplete = True
        try:
            entries = iter(root["entries"]())
        except RuntimeError:
            state["childrenTruncated"] = True
            root_entries_incomplete = True
            return True
        target = priority_root_iterators if _is_priority_root(root) else root_iterators
        target.append((root, entries))
        return True

    for _ in range(_ROOT_PREFETCH_LIMIT):
        if priority_root_iterators:
            break
        if not pull_root():
            break

    def process_node(node):
        nonlocal root_entries_incomplete
        if _check_root_budget(
            state,
            force=state["rootObjectsScanned"] % _DEADLINE_CHECK_INTERVAL == 0,
        ):
            root_entries_incomplete = True
            return False
        (
            root,
            name,
            value,
            path,
            depth,
            ancestry,
            root_name,
            relation,
            target_kind,
            owner,
            cycle_edge,
        ) = node
        state["rootObjectsScanned"] += 1
        state["objectsScanned"] += 1

        if id(value) == target_address:
            if not _add_result(
                inspector,
                state,
                max_results,
                value,
                name,
                path,
                depth,
                root,
                root_name,
                relation,
                target_kind,
                owner,
            ):
                return False

        if cycle_edge:
            return True

        if depth >= max_depth:
            if _has_static_edges(value):
                state["depthLimitReached"] = True
            return True
        children, was_truncated = _static_edges(inspector, value, state)
        state["childrenTruncated"] = state["childrenTruncated"] or was_truncated
        if state["deadlineReached"] or state["rootBudgetReached"]:
            root_entries_incomplete = True
            return False
        if not children:
            return True

        for child_name, child, child_relation, child_kind in children:
            child_identity = id(child)
            is_cycle = child_identity in ancestry
            if is_cycle and child_identity != target_address:
                continue
            child_path = _append_path(path, child_name)
            if child_identity == target_address:
                if not _add_result(
                    inspector,
                    state,
                    max_results,
                    child,
                    child_name,
                    child_path,
                    depth + 1,
                    root,
                    root_name,
                    child_relation,
                    child_kind,
                    value,
                ):
                    return False
            if is_cycle:
                continue
            if state["rootObjectsScanned"] + len(pending) >= max_objects:
                state["objectLimitReached"] = True
                root_entries_incomplete = True
                break
            pending.append((
                root,
                child_name,
                child,
                child_path,
                depth + 1,
                ancestry if is_cycle else ancestry + (child_identity,),
                root_name,
                child_relation,
                child_kind,
                value,
                is_cycle,
            ))
        return True

    while (
        priority_root_iterators
        or root_iterators
        or pending
        or (not root_source_exhausted and not root_pulls_blocked)
    ) and state["rootObjectsScanned"] < max_objects:
        made_progress = False

        if not priority_root_iterators and not root_iterators:
            made_progress = pull_root() or made_progress

        if (priority_root_iterators or root_iterators) and not root_pulls_blocked:
            root_node = None
            while priority_root_iterators or root_iterators:
                if _check_root_budget(
                    state,
                    force=state["rootObjectsScanned"] % _DEADLINE_CHECK_INTERVAL == 0,
                ):
                    root_entries_incomplete = True
                    root_pulls_blocked = True
                    break
                if priority_root_iterators and root_iterators:
                    if priority_streak >= _ROOT_PRIORITY_QUOTA:
                        selected = root_iterators
                    else:
                        selected = priority_root_iterators
                elif priority_root_iterators:
                    selected = priority_root_iterators
                else:
                    selected = root_iterators
                root, entries = selected.popleft()
                try:
                    name, value = next(entries)
                except StopIteration:
                    continue
                except RuntimeError:
                    state["childrenTruncated"] = True
                    root_entries_incomplete = True
                    continue
                selected.append((root, entries))
                if selected is priority_root_iterators:
                    priority_streak += 1
                else:
                    priority_streak = 0
                if not _allow_edge(state):
                    root_entries_incomplete = True
                    root_pulls_blocked = True
                    break
                path = _append_path(root["location"], name)
                root_node = (
                    root,
                    name,
                    value,
                    path,
                    0,
                    (id(value),),
                    name,
                    _root_relation(root),
                    "variable",
                    root.get("value"),
                    False,
                )
                break

            if root_node is not None:
                made_progress = True
                if not process_node(root_node):
                    break

        if pending and state["rootObjectsScanned"] < max_objects:
            made_progress = True
            if not process_node(pending.popleft()):
                break

        if (
            not root_source_exhausted
            and not root_pulls_blocked
            and state["rootObjectsScanned"] < max_objects
        ):
            made_progress = pull_root() or made_progress

        if not made_progress:
            break

    if (
        (
            priority_root_iterators
            or root_iterators
            or pending
            or not root_source_exhausted
        )
        and not state["resultLimitReached"]
        and not state["deadlineReached"]
        and not state["rootBudgetReached"]
        and state["rootObjectsScanned"] >= max_objects
    ):
        state["objectLimitReached"] = True
        root_entries_incomplete = True
    if root_pulls_blocked:
        root_entries_incomplete = True
    return (
        not root_entries_incomplete
        and root_source_exhausted
        and not priority_root_iterators
        and not root_iterators
        and not pending
        and not state["resultLimitReached"]
    )


def _search_gc_snapshot(
    inspector,
    objects,
    excluded_identities,
    target_address,
    max_results,
    max_objects,
    max_depth,
    state,
):
    complete = True
    for owner in objects:
        if id(owner) in excluded_identities:
            continue
        if _check_deadline(
            state,
            force=state["gcObjectsScanned"] % _DEADLINE_CHECK_INTERVAL == 0,
        ):
            complete = False
            break
        if state["objectsScanned"] >= max_objects:
            state["objectLimitReached"] = True
            complete = False
            break
        state["objectsScanned"] += 1
        state["gcObjectsScanned"] += 1
        owner_type = _owner_type_name(owner)
        owner_location = f"GC objects / {owner_type} @{hex(id(owner))}"
        gc_root = {
            "sourceKind": "gc",
            "moduleName": None,
            "frameHandle": None,
            "scopeType": "gc-tracked",
            "consoleHandle": None,
            "consoleAttributeName": None,
        }

        if id(owner) == target_address:
            state["targetFound"] = True
            if not _add_result(
                inspector,
                state,
                max_results,
                owner,
                f"{owner_type} @{hex(id(owner))}",
                owner_location,
                0,
                gc_root,
                f"{owner_type} @{hex(id(owner))}",
                "gcObject",
                "object",
                owner,
            ):
                complete = False
                break

        if state["edgeLimitReached"]:
            complete = False
            continue
        if max_depth <= 0:
            if _has_static_edges(owner):
                state["depthLimitReached"] = True
                complete = False
            continue
        children, was_truncated = _static_edges(inspector, owner, state)
        state["childrenTruncated"] = state["childrenTruncated"] or was_truncated
        if state["deadlineReached"]:
            complete = False
            break
        for name, child, relation, target_kind in children:
            if id(child) != target_address:
                continue
            location = _append_path(owner_location, name)
            if not _add_result(
                inspector,
                state,
                max_results,
                child,
                name,
                location,
                1,
                gc_root,
                f"{owner_type} @{hex(id(owner))}",
                relation,
                target_kind,
                owner,
            ):
                complete = False
                break
        if state["resultLimitReached"]:
            complete = False
            break
    return complete and not state["edgeLimitReached"] and not state["deadlineReached"]


def _add_result(
    inspector,
    state,
    max_results,
    target,
    name,
    location,
    depth,
    root,
    root_name,
    relation,
    target_kind,
    owner,
):
    state["targetFound"] = True
    result_key = (
        root.get("sourceKind"),
        root.get("moduleName"),
        root.get("frameHandle"),
        root.get("scopeType"),
        root.get("consoleHandle"),
        root.get("consoleAttributeName"),
        relation,
        location,
    )
    if result_key in state["resultKeys"]:
        return True
    if len(state["items"]) >= max_results:
        state["resultLimitReached"] = True
        return False
    if state["targetSummary"] is None:
        state["targetSummary"] = inspector.summarize(target)
    owner_type_name = _owner_type_name(owner) if owner is not None else None
    row = {
        "kind": "variable" if target_kind == "variable" and depth == 0 else "object",
        "name": bounded_text(name, "<unnamed>", 512),
        "location": bounded_text(location, "<unknown>", 2_048),
        "objectPath": bounded_text(location, None, 2_048),
        "matchFields": ["address"],
        "depth": depth,
        "sourceKind": root.get("sourceKind"),
        "moduleName": root.get("moduleName"),
        "frameHandle": root.get("frameHandle"),
        "scopeType": root.get("scopeType"),
        "consoleHandle": root.get("consoleHandle"),
        "consoleAttributeName": root.get("consoleAttributeName"),
        "rootName": bounded_text(root_name, "<unnamed>", 512),
        "classMember": None,
        "value": state["targetSummary"],
        "relation": relation,
        "targetKind": target_kind,
        "ownerTypeName": bounded_text(owner_type_name, None, 1_024),
        "ownerAddressHex": hex(id(owner)) if owner is not None else None,
    }
    state["items"].append(row)
    state["resultKeys"].add(result_key)
    return True


def _root_relation(root):
    return {
        "module": "moduleVariable",
        "frame": "frameVariable",
        "console": "consoleVariable",
    }.get(root.get("sourceKind"), "variable")


def _is_priority_root(root):
    source_kind = root.get("sourceKind")
    if source_kind == "console":
        return True
    if source_kind == "module":
        return root.get("moduleName") == "__main__"
    if source_kind == "frame":
        return root.get("scopeType") in ("locals", "globals")
    return False


def _append_path(parent, child):
    return f"{parent} / {child}"


def _allow_edge(state):
    if _check_root_budget(
        state,
        force=state["edgesScanned"] % _DEADLINE_CHECK_INTERVAL == 0,
    ):
        return False
    if state["edgesScanned"] >= state["maxEdges"]:
        state["edgeLimitReached"] = True
        return False
    root_edge_limit = state.get("rootEdgeLimit")
    if root_edge_limit is not None and state["edgesScanned"] >= root_edge_limit:
        state["rootBudgetReached"] = True
        return False
    state["edgesScanned"] += 1
    return True


def _check_deadline(state, force=False):
    if state["deadlineReached"]:
        return True
    if force and time.perf_counter() >= state["deadline"]:
        state["deadlineReached"] = True
    return state["deadlineReached"]


def _check_root_budget(state, force=False):
    if _check_deadline(state, force=force):
        return True
    root_deadline = state.get("rootDeadline")
    if root_deadline is None and state.get("rootEdgeLimit") is None:
        return False
    if (
        not state.get("rootBudgetReached", False)
        and force
        and root_deadline is not None
        and time.perf_counter() >= root_deadline
    ):
        state["rootBudgetReached"] = True
    return state.get("rootBudgetReached", False)


def _has_static_edges(value):
    exact = type(value)
    if exact in (list, tuple, set, frozenset, dict):
        return len(value) > 0
    if exact is types.ModuleType:
        namespace = types.ModuleType.__getattribute__(value, "__dict__")
        return dict.__len__(namespace) > 0
    if is_class_object(value):
        namespace = type.__getattribute__(value, "__dict__")
        return len(namespace) > 0
    namespace = _safe_instance_dict(value)
    return type(namespace) is dict and dict.__len__(namespace) > 0


def _static_edges(inspector, value, state):
    exact = type(value)
    rows = []
    truncated = False

    if exact in (list, tuple):
        relation = "listItem" if exact is list else "tupleItem"
        count = len(value)
        limit = min(count, MAX_CHILDREN_PER_OBJECT)
        for index in range(limit):
            if not _allow_edge(state):
                break
            try:
                rows.append((f"[{index}]", value[index], relation, "collectionItem"))
            except IndexError:
                truncated = True
                break
        return rows, truncated or count > limit

    if exact in (set, frozenset):
        relation = "setItem" if exact is set else "frozensetItem"
        try:
            for index, child in enumerate(value):
                if index >= MAX_CHILDREN_PER_OBJECT:
                    truncated = True
                    break
                if not _allow_edge(state):
                    break
                rows.append((f"[{index}]", child, relation, "collectionItem"))
        except RuntimeError:
            truncated = True
        return rows, truncated

    if exact is dict:
        try:
            for index, (key, child) in enumerate(dict.items(value)):
                if len(rows) + 2 > MAX_CHILDREN_PER_OBJECT:
                    truncated = True
                    break
                if not _allow_edge(state):
                    break
                key_name = inspector._key_name(key, index)
                rows.append((f"{key_name} / key", key, "dictKey", "mappingKey"))
                if not _allow_edge(state):
                    break
                rows.append((f"{key_name} / value", child, "dictValue", "mappingValue"))
        except RuntimeError:
            truncated = True
        return rows, truncated

    if exact is types.ModuleType:
        namespace = types.ModuleType.__getattribute__(value, "__dict__")
        try:
            for index, (name, child) in enumerate(dict.items(namespace)):
                if index >= MAX_CHILDREN_PER_OBJECT:
                    truncated = True
                    break
                if type(name) is str:
                    if not _allow_edge(state):
                        break
                    rows.append((bounded_text(name, "<unnamed>", 512), child, "moduleVariable", "variable"))
        except RuntimeError:
            truncated = True
        return rows, truncated

    if is_class_object(value):
        namespace = type.__getattribute__(value, "__dict__")
        try:
            for index, (name, child) in enumerate(namespace.items()):
                if index >= MAX_CHILDREN_PER_OBJECT:
                    truncated = True
                    break
                if type(name) is str:
                    if not _allow_edge(state):
                        break
                    rows.append((bounded_text(name, "<unnamed>", 512), child, "classAttribute", "classAttribute"))
        except RuntimeError:
            truncated = True
        return rows, truncated

    namespace = _safe_instance_dict(value)
    if type(namespace) is dict:
        try:
            for index, (name, child) in enumerate(dict.items(namespace)):
                if index >= MAX_CHILDREN_PER_OBJECT:
                    truncated = True
                    break
                if not _allow_edge(state):
                    break
                if type(name) is str:
                    label = bounded_text(name, "<unnamed>", 512)
                    relation = "instanceField"
                    target_kind = "instanceField"
                else:
                    label = inspector._key_name(name, index)
                    relation = "instanceDictionaryEntry"
                    target_kind = "instanceDictionaryEntry"
                rows.append((label, child, relation, target_kind))
        except RuntimeError:
            truncated = True
    return rows, truncated


def _owner_type_name(owner):
    if is_class_object(owner):
        cls = owner
    else:
        cls = type(owner)
    return f"{type_module(cls)}.{type_qualified_name(cls)}"


def _incomplete_console_discovery(tracked_total, max_objects):
    return {
        "items": [],
        "total": 0,
        "trackedTotal": tracked_total,
        "scannedCount": 0,
        "maxObjects": max_objects,
        "truncated": tracked_total > 0,
        "namespaceLimitReached": False,
        "scanComplete": False,
        "durationMilliseconds": 0.0,
        "snapshotTimestamp": timestamp(),
    }


def _handle_store_internal_ids(handles):
    identities = {id(handles)}
    for name in ("_entries", "_handles_by_identity", "_lock"):
        try:
            value = object.__getattribute__(handles, name)
        except AttributeError:
            continue
        identities.add(id(value))

    try:
        entries = object.__getattribute__(handles, "_entries")
    except AttributeError:
        return identities
    if type(entries) is not collections.OrderedDict:
        return identities
    for record in collections.OrderedDict.values(entries):
        identities.add(id(record))
        if type(record) is tuple and len(record) == 4:
            stored = record[3]
            if type(stored) is weakref.ReferenceType:
                identities.add(id(stored))
    return identities
