import gc
import time

from .runtime_info import timestamp
from .safe_metadata import type_module, type_name
from .safe_objects import _page


DEFAULT_MAX_OBJECTS = 100_000
MAX_OBJECTS = 1_000_000
MAX_PAGE_SIZE = 200


def list_objects(inspector, query="", offset=0, page_size=100, max_objects=DEFAULT_MAX_OBJECTS):
    query = _query(query)
    offset, page_size = _page(offset, page_size)
    if page_size > MAX_PAGE_SIZE:
        raise ValueError(f"GC pageSize must be between 1 and {MAX_PAGE_SIZE}.")
    if type(max_objects) is not int or not 1 <= max_objects <= MAX_OBJECTS:
        raise ValueError(f"maxObjects must be between 1 and {MAX_OBJECTS}.")

    started = time.perf_counter()
    objects = gc.get_objects()
    tracked_total = len(objects)
    scanned_count = min(tracked_total, max_objects)
    folded_query = query.casefold()
    matches = []

    for index, value in enumerate(objects):
        if index >= scanned_count:
            break
        cls = type(value)
        value_type_name = type_name(cls)
        module_name = type_module(cls)
        qualified_name = f"{module_name}.{value_type_name}"
        address = hex(id(value))
        if folded_query and not any(
            folded_query in candidate.casefold()
            for candidate in (value_type_name, module_name, qualified_name, address)
        ):
            continue
        matches.append((module_name.casefold(), value_type_name.casefold(), id(value), qualified_name, value))

    matches.sort(key=lambda item: item[:3])
    page = matches[offset:offset + page_size]
    items = [
        {"name": qualified_name, "value": inspector.summarize(value)}
        for _, _, _, qualified_name, value in page
    ]
    return {
        "scopeType": "gc-tracked",
        "query": query,
        "items": items,
        "offset": offset,
        "pageSize": page_size,
        "total": len(matches),
        "trackedTotal": tracked_total,
        "scannedCount": scanned_count,
        "truncated": scanned_count < tracked_total,
        "durationMilliseconds": round((time.perf_counter() - started) * 1000, 3),
        "snapshotTimestamp": timestamp(),
    }


def _query(value):
    if type(value) is not str:
        raise ValueError("query must be a string.")
    value = value.strip()
    if len(value) > 200:
        raise ValueError("query must contain at most 200 characters.")
    return value
