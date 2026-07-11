import inspect
import sys
import types

from . import arrays
from .runtime_info import timestamp

_CONTAINER_TYPES = (list, tuple, set, frozenset, dict)
_KNOWN_SIZE_TYPES = (type(None), bool, int, float, complex, str, bytes, bytearray, list, tuple, set, frozenset, dict)


class SafeObjectInspector:
    def __init__(self, handles):
        self._handles = handles

    def summarize(self, value):
        cls = type(value)
        type_name = type.__getattribute__(cls, "__name__")
        module_name = type.__getattribute__(cls, "__module__")
        adapter = "numpy.ndarray" if arrays.is_exact_ndarray(value) else None
        return {
            "handleId": self._handles.put(value),
            "typeName": type_name,
            "moduleName": module_name,
            "qualifiedTypeName": f"{module_name}.{type_name}",
            "safePreview": self._preview(value, 0, set()),
            "addressHex": hex(id(value)),
            "shallowSizeBytes": self._shallow_size(value),
            "expandable": type(value) in _CONTAINER_TYPES or type(inspect.getattr_static(value, "__dict__", None)) is dict,
            "adapterKind": adapter,
            "changeToken": f"identity:{id(value):x}",
            "snapshotTimestamp": timestamp(),
        }

    def describe(self, handle):
        return self.summarize(self._handles.get(handle))

    def list_children(self, handle, offset=0, page_size=100):
        value = self._handles.get(handle)
        offset, page_size = _page(offset, page_size)
        entries = self._static_children(value)
        page = entries[offset:offset + page_size]
        items = []
        for name, origin, child in page:
            items.append({"name": name, "origin": origin, "value": self.summarize(child)})
        return {"items": items, "offset": offset, "pageSize": page_size, "total": len(entries)}

    def _static_children(self, value):
        exact = type(value)
        if exact in (list, tuple):
            return [(f"[{index}]", "item", item) for index, item in enumerate(value)]
        if exact in (set, frozenset):
            return [(f"[{index}]", "item", item) for index, item in enumerate(value)]
        if exact is dict:
            return [(self._key_name(key, index), "mapping", item) for index, (key, item) in enumerate(value.items())]
        entries = []
        instance_dict = inspect.getattr_static(value, "__dict__", None)
        if type(instance_dict) is dict:
            entries.extend((name, "instance", item) for name, item in instance_dict.items() if type(name) is str)
        cls = type(value)
        for base in type.__getattribute__(cls, "__mro__"):
            namespace = type.__getattribute__(base, "__dict__")
            base_name = type.__getattribute__(base, "__name__")
            entries.extend((name, f"class:{base_name}", item) for name, item in namespace.items() if type(name) is str)
        return entries

    def _preview(self, value, depth, seen):
        exact = type(value)
        if value is None:
            return "None"
        if exact is bool:
            return "True" if value else "False"
        if exact in (int, float, complex):
            return repr(value)
        if exact is str:
            clipped = value[:120]
            return repr(clipped) + ("…" if len(value) > 120 else "")
        if exact in (bytes, bytearray):
            raw = bytes(value[:60])
            return repr(raw) + ("…" if len(value) > 60 else "")
        if arrays.is_exact_ndarray(value):
            return f"ndarray(shape={tuple(value.shape)}, dtype={value.dtype})"
        if exact is types.ModuleType:
            name = types.ModuleType.__getattribute__(value, "__name__")
            return f"module {name}"
        if exact is types.FunctionType:
            return f"function {value.__qualname__}"
        if isinstance(value, type):
            return f"class {type.__getattribute__(value, '__qualname__')}"
        if exact in _CONTAINER_TYPES:
            if id(value) in seen:
                return f"{exact.__name__}(<cycle>)"
            if depth >= 2:
                return f"{exact.__name__}(len={len(value)})"
            seen.add(id(value))
            try:
                if exact is dict:
                    samples = list(value.items())[:4]
                    body = ", ".join(f"{self._preview(k, depth + 1, seen)}: {self._preview(v, depth + 1, seen)}" for k, v in samples)
                else:
                    samples = list(value)[:4]
                    body = ", ".join(self._preview(item, depth + 1, seen) for item in samples)
                suffix = ", …" if len(value) > 4 else ""
                return f"{exact.__name__}([{body}{suffix}])"
            finally:
                seen.remove(id(value))
        cls = type(value)
        return f"<{type.__getattribute__(cls, '__module__')}.{type.__getattribute__(cls, '__name__')} object>"

    @staticmethod
    def _shallow_size(value):
        if type(value) in _KNOWN_SIZE_TYPES or arrays.is_exact_ndarray(value):
            return sys.getsizeof(value)
        return object.__sizeof__(value)

    def _key_name(self, key, index):
        preview = self._preview(key, 0, set())
        return f"[{index}] {preview[:80]}"


def _page(offset, page_size):
    if type(offset) is not int or offset < 0:
        raise ValueError("offset must be a non-negative integer.")
    if type(page_size) is not int or not 1 <= page_size <= 1000:
        raise ValueError("pageSize must be between 1 and 1000.")
    return offset, page_size
