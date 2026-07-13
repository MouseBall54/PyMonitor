import sys
import types

from .runtime_info import timestamp
from .safe_metadata import bounded_text, exact_dict_value, is_dict_object
from .safe_objects import MAX_VALUE_PAGE_SIZE, _page

_MAX_SCAN_ATTEMPTS = 2


def list_modules(offset=0, page_size=100):
    offset, page_size = _page(offset, page_size)
    registry = _module_registry()
    if registry is None:
        raise ValueError("The module registry is unavailable or is not a dictionary.")
    page, total, registry_scan_complete, registry_mutation = _bounded_scan(
        lambda: _module_entries(registry),
        offset,
        page_size,
    )
    rows = []
    rows_complete = True
    mutation_detected = registry_mutation
    for name, module in page:
        row, row_complete, row_mutation = _module_row(name, module)
        rows.append(row)
        rows_complete = rows_complete and row_complete
        mutation_detected = mutation_detected or row_mutation
    return {
        "items": rows,
        "offset": offset,
        "pageSize": page_size,
        "total": total,
        "ordering": "main-then-insertion",
        "scanComplete": registry_scan_complete and rows_complete,
        "totalIsExact": registry_scan_complete,
        "mutationDetected": mutation_detected,
        "snapshotTimestamp": timestamp(),
    }


def _module_registry():
    try:
        registry = types.ModuleType.__getattribute__(sys, "modules")
    except AttributeError:
        return None
    return registry if is_dict_object(registry) else None


def _module_entries(registry):
    main = exact_dict_value(registry, "__main__")
    if type(main) is types.ModuleType:
        yield "__main__", main
    for name, module in dict.items(registry):
        if (
            type(name) is not str
            or name == "__main__"
            or len(name) > 500
            or type(module) is not types.ModuleType
        ):
            continue
        yield name, module


def _module_row(name, module):
    namespace = types.ModuleType.__getattribute__(module, "__dict__")
    filename = exact_dict_value(namespace, "__file__")
    try:
        _, entry_count, scan_complete, mutation_detected = _bounded_scan(
            lambda: (key for key in dict.keys(namespace) if type(key) is str),
            0,
            0,
        )
    except ValueError:
        entry_count = dict.__len__(namespace)
        scan_complete = False
        mutation_detected = True
    return {
        "name": name,
        "filename": bounded_text(filename, None, 1024),
        "entryCount": entry_count,
        "entryCountIsExact": scan_complete,
        "isMain": name == "__main__",
    }, scan_complete, mutation_detected


def list_namespace(inspector, module_name, offset=0, page_size=100):
    if type(module_name) is not str or not module_name or len(module_name) > 500:
        raise ValueError("moduleName must be a non-empty string up to 500 characters.")
    registry = _module_registry()
    if registry is None:
        raise ValueError("The module registry is unavailable or is not a dictionary.")
    module = exact_dict_value(registry, module_name)
    if type(module) is not types.ModuleType:
        raise ValueError("The selected module is no longer loaded or is not a standard module.")

    namespace = types.ModuleType.__getattribute__(module, "__dict__")
    offset, page_size = _page(offset, page_size, MAX_VALUE_PAGE_SIZE)
    # Preserve direct dictionary insertion order so memory stays O(pageSize)
    # instead of copying and sorting the complete module namespace.
    page, total, scan_complete, mutation_detected = _bounded_scan(
        lambda: (
            (bounded_text(name, "<unnamed>", 512), value)
            for name, value in dict.items(namespace)
            if type(name) is str
        ),
        offset,
        page_size,
    )
    return {
        "moduleName": module_name,
        "scopeType": "module",
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
