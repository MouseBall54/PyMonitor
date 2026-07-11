import sys
import types

from .runtime_info import timestamp
from .safe_objects import _page


def list_modules(offset=0, page_size=100):
    offset, page_size = _page(offset, page_size)
    rows = []
    for name, module in list(sys.modules.items()):
        if type(name) is not str or type(module) is not types.ModuleType:
            continue
        namespace = types.ModuleType.__getattribute__(module, "__dict__")
        filename = namespace.get("__file__")
        rows.append({
            "name": name,
            "filename": filename if type(filename) is str else None,
            "entryCount": sum(type(key) is str for key in namespace),
            "isMain": name == "__main__",
        })
    rows.sort(key=lambda item: (not item["isMain"], item["name"].casefold(), item["name"]))
    return {
        "items": rows[offset:offset + page_size],
        "offset": offset,
        "pageSize": page_size,
        "total": len(rows),
        "snapshotTimestamp": timestamp(),
    }


def list_namespace(inspector, module_name, offset=0, page_size=100):
    if type(module_name) is not str or not module_name or len(module_name) > 500:
        raise ValueError("moduleName must be a non-empty string up to 500 characters.")
    module = sys.modules.get(module_name)
    if type(module) is not types.ModuleType:
        raise ValueError("The selected module is no longer loaded or is not a standard module.")

    namespace = types.ModuleType.__getattribute__(module, "__dict__")
    offset, page_size = _page(offset, page_size)
    entries = [(name, value) for name, value in list(namespace.items()) if type(name) is str]
    entries.sort(key=lambda item: (item[0].casefold(), item[0]))
    page = entries[offset:offset + page_size]
    return {
        "moduleName": module_name,
        "scopeType": "module",
        "items": [{"name": name, "value": inspector.summarize(value)} for name, value in page],
        "offset": offset,
        "pageSize": page_size,
        "total": len(entries),
        "snapshotTimestamp": timestamp(),
    }
