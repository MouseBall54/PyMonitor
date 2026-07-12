import ctypes
import hashlib
import itertools
import sys
import types

from . import arrays, dataframes, matplotlib_figures
from .runtime_info import timestamp
from .safe_metadata import bounded_text, type_module, type_name, type_qualified_name

_CONTAINER_TYPES = (list, tuple, set, frozenset, dict)
_KNOWN_SIZE_TYPES = (type(None), bool, int, float, complex, str, bytes, bytearray, list, tuple, set, frozenset, dict)
MAX_VALUE_PAGE_SIZE = 200
MAX_NAVIGATION_DEPTH = 32
_MAX_PREVIEW_ITEMS = 4
_MAX_ARRAY_TOKEN_SAMPLES = 256
_MAX_ARRAY_TOKEN_FULL_BYTES = 1024 * 1024
_MAX_ARRAY_TOKEN_WINDOW_BYTES = 4096
_MAX_ARRAY_TOKEN_WINDOWS = 16
_MAX_DIRECT_INT_BITS = 4096

_PYOBJECT_GENERIC_GET_DICT = ctypes.pythonapi.PyObject_GenericGetDict
_PYOBJECT_GENERIC_GET_DICT.argtypes = (ctypes.py_object, ctypes.c_void_p)
_PYOBJECT_GENERIC_GET_DICT.restype = ctypes.py_object


class SafeObjectInspector:
    def __init__(self, handles):
        self._handles = handles

    def summarize(self, value):
        cls = type(value)
        value_type_name = type_name(cls)
        module_name = type_module(cls)
        qualified_type_name = type_qualified_name(cls)
        if arrays.is_exact_ndarray(value):
            adapter = "numpy.ndarray"
        elif dataframes.is_exact_dataframe(value):
            adapter = "pandas.DataFrame"
        else:
            adapter = matplotlib_figures.adapter_kind(value)
        handle = self._handles.put(value)
        identity_token = _identity_token(value)
        preview = self._preview(value, 0, set())
        metadata_token = self._metadata_token(value, preview, adapter)
        instance_dict = _safe_instance_dict(value)
        summary = {
            "handleId": handle,
            "typeName": value_type_name,
            "moduleName": module_name,
            "qualifiedTypeName": f"{module_name}.{qualified_type_name}",
            "safePreview": preview,
            "addressHex": hex(id(value)),
            "shallowSizeBytes": self._shallow_size(value),
            "expandable": type(value) in _CONTAINER_TYPES or type(instance_dict) is dict,
            "adapterKind": adapter,
            "identityToken": identity_token,
            "metadataToken": metadata_token,
            "changeToken": f"{identity_token}:{metadata_token}",
            "snapshotTimestamp": timestamp(),
        }
        if adapter == "numpy.ndarray":
            dtype_text = bounded_text(str(value.dtype), "<unknown>")
            summary.update({
                "shape": [int(part) for part in value.shape],
                "dtype": dtype_text,
                "payloadSizeBytes": int(value.nbytes),
            })
        elif adapter == "pandas.DataFrame":
            frame_metadata = dataframes.summary_metadata(value)
            summary.update({
                "shape": [frame_metadata["rows"], frame_metadata["columns"]],
                "rowCount": frame_metadata["rows"],
                "columnCount": frame_metadata["columns"],
            })
        elif adapter in ("matplotlib.Figure", "matplotlib.Axes"):
            figure_metadata = matplotlib_figures.summary_metadata(value)
            summary.update({
                "renderState": figure_metadata["availability"]["state"],
                "renderUnavailableReason": figure_metadata["availability"]["reason"],
            })
            if figure_metadata["previewAvailable"]:
                summary.update({
                    "shape": [figure_metadata["sourceHeight"], figure_metadata["sourceWidth"], 4],
                    "dtype": "uint8",
                    "payloadSizeBytes": figure_metadata["sourceBufferBytes"],
                })
        return summary

    def describe(self, handle):
        return self.summarize(self._handles.get(handle))

    def list_children(self, handle, offset=0, page_size=100, depth=0, ancestor_identity_tokens=None):
        value = self._handles.get(handle)
        offset, page_size = _page(offset, page_size, MAX_VALUE_PAGE_SIZE)
        depth, ancestry = _navigation(depth, ancestor_identity_tokens)
        page, total = self._static_children(value, offset, page_size)
        items = []
        for name, origin, child in page:
            child_summary = self.summarize(child)
            child_identity = child_summary["identityToken"]
            cycle_to_depth = _cycle_depth(ancestry, child_identity)
            if child is value and cycle_to_depth is None:
                cycle_to_depth = depth
            child_depth = depth + 1
            is_cycle = cycle_to_depth is not None
            items.append({
                "name": name,
                "origin": origin,
                "relationKind": _relation_kind(origin),
                "pathSegment": name,
                "value": child_summary,
                "depth": child_depth,
                "isCycle": is_cycle,
                "cycleToDepth": cycle_to_depth,
                "canExpand": child_summary["expandable"] and not is_cycle and child_depth < MAX_NAVIGATION_DEPTH,
            })
        return {
            "items": items,
            "offset": offset,
            "pageSize": page_size,
            "total": total,
            "hasMore": offset + len(page) < total,
            "depth": depth,
            "depthLimitReached": depth + 1 >= MAX_NAVIGATION_DEPTH,
        }

    def _static_children(self, value, offset, page_size):
        exact = type(value)
        if exact in (list, tuple):
            end = min(len(value), offset + page_size)
            return [(f"[{index}]", "item", value[index]) for index in range(offset, end)], len(value)
        if exact in (set, frozenset):
            page = itertools.islice(iter(value), offset, offset + page_size)
            return [(f"[{index}]", "item", item) for index, item in enumerate(page, offset)], len(value)
        if exact is dict:
            page = itertools.islice(value.items(), offset, offset + page_size)
            return [
                (self._key_name(key, index), "mapping", item)
                for index, (key, item) in enumerate(page, offset)
            ], len(value)
        instance_dict = _safe_instance_dict(value)
        if type(instance_dict) is dict:
            page = itertools.islice(instance_dict.items(), offset, offset + page_size)
            return [
                (
                    bounded_text(name, "<unnamed>", 512) if type(name) is str else self._key_name(name, index),
                    "instance" if type(name) is str else "instance-dict",
                    item,
                )
                for index, (name, item) in enumerate(page, offset)
            ], len(instance_dict)
        return [], 0

    def _preview(self, value, depth, seen):
        exact = type(value)
        if value is None:
            return "None"
        if exact is bool:
            return "True" if value else "False"
        if exact is int:
            bit_length = int.bit_length(value)
            if bit_length > _MAX_DIRECT_INT_BITS:
                return f"<int bits={bit_length}>"
            return repr(value)
        if exact in (float, complex):
            return repr(value)
        if exact is str:
            clipped = value[:120]
            return repr(clipped) + ("…" if len(value) > 120 else "")
        if exact in (bytes, bytearray):
            raw = bytes(value[:60])
            return repr(raw) + ("…" if len(value) > 60 else "")
        if arrays.is_exact_ndarray(value):
            dtype_text = bounded_text(str(value.dtype), "<unknown>")
            return f"ndarray(shape={tuple(value.shape)}, dtype={dtype_text})"
        if dataframes.is_exact_dataframe(value):
            metadata = dataframes.summary_metadata(value)
            return f"DataFrame(rows={metadata['rows']}, columns={metadata['columns']})"
        if matplotlib_figures.adapter_kind(value) is not None:
            return matplotlib_figures.safe_preview(value)
        if exact is types.ModuleType:
            name = types.ModuleType.__getattribute__(value, "__name__")
            name = bounded_text(name, "<unnamed>")
            return f"module {name}"
        if exact is types.FunctionType:
            name = types.FunctionType.__getattribute__(value, "__qualname__")
            return f"function {bounded_text(name, '<unnamed>')}"
        if isinstance(value, type):
            return f"class {type_qualified_name(value)}"
        if exact in _CONTAINER_TYPES:
            if id(value) in seen:
                return f"{type_name(exact)}(<cycle>)"
            if depth >= 2:
                return f"{type_name(exact)}(len={len(value)})"
            seen.add(id(value))
            try:
                if exact is dict:
                    samples = itertools.islice(value.items(), _MAX_PREVIEW_ITEMS)
                    body = ", ".join(f"{self._preview(k, depth + 1, seen)}: {self._preview(v, depth + 1, seen)}" for k, v in samples)
                else:
                    samples = itertools.islice(iter(value), _MAX_PREVIEW_ITEMS)
                    body = ", ".join(self._preview(item, depth + 1, seen) for item in samples)
                suffix = ", …" if len(value) > _MAX_PREVIEW_ITEMS else ""
                return f"{type_name(exact)}([{body}{suffix}])"
            finally:
                seen.remove(id(value))
        cls = type(value)
        return f"<{type_module(cls)}.{type_name(cls)} object>"

    @staticmethod
    def _shallow_size(value):
        if type(value) in _KNOWN_SIZE_TYPES or arrays.is_exact_ndarray(value):
            return sys.getsizeof(value)
        return object.__sizeof__(value)

    def _key_name(self, key, index):
        preview = self._preview(key, 0, set())
        return f"[{index}] {preview[:80]}"

    def _metadata_token(self, value, preview, adapter):
        exact = type(value)
        cls = exact
        parts = [
            type_module(cls),
            type_name(cls),
        ]
        if exact in _CONTAINER_TYPES:
            parts.extend((
                f"length:{len(value)}",
                f"preview:{preview}",
            ))
        elif exact is bytearray:
            parts.extend((
                f"length:{len(value)}",
                "sample:" + bytes(value[:32] + value[-32:]).hex(),
            ))
        elif exact in (type(None), bool, int, float, complex, str, bytes):
            parts.append(f"value:{preview}")
        elif adapter == "numpy.ndarray":
            dtype_text = bounded_text(str(value.dtype), "<unknown>")
            parts.extend((
                "shape:" + ",".join(str(int(part)) for part in value.shape),
                f"dtype:{dtype_text}",
                f"nbytes:{int(value.nbytes)}",
            ))
            sample = _array_sample_digest(value)
            if sample is not None:
                parts.append(f"sample:{sample}")
        elif adapter == "pandas.DataFrame":
            frame_metadata = dataframes.summary_metadata(value)
            parts.extend((
                f"rows:{frame_metadata['rows']}",
                f"columns:{frame_metadata['columns']}",
                f"sample:{dataframes.sample_fingerprint(value)}",
            ))
        elif adapter in ("matplotlib.Figure", "matplotlib.Axes"):
            figure_metadata = matplotlib_figures.summary_metadata(value)
            availability = figure_metadata["availability"]
            parts.extend((
                f"render-state:{availability['state']}",
                f"render-reason:{availability['reason']}",
                f"source-width:{figure_metadata['sourceWidth']}",
                f"source-height:{figure_metadata['sourceHeight']}",
                f"sample:{matplotlib_figures.sample_fingerprint(value)}",
            ))
        else:
            instance_dict = _safe_instance_dict(value)
            if type(instance_dict) is dict:
                parts.append(f"field-count:{len(instance_dict)}")
                for index, (name, field_value) in enumerate(itertools.islice(instance_dict.items(), _MAX_PREVIEW_ITEMS)):
                    safe_name = name if type(name) is str else self._preview(name, 0, set())
                    parts.append(f"field:{index}:{safe_name}:{self._preview(field_value, 0, set())}")
        encoded = "\x1f".join(parts).encode("utf-8", "surrogatepass")
        return "metadata:v1:" + hashlib.sha256(encoded).hexdigest()


def _page(offset, page_size, max_page_size=1000):
    if type(offset) is not int or offset < 0:
        raise ValueError("offset must be a non-negative integer.")
    if type(max_page_size) is not int or max_page_size < 1:
        raise ValueError("max_page_size must be a positive integer.")
    if type(page_size) is not int or not 1 <= page_size <= max_page_size:
        raise ValueError(f"pageSize must be between 1 and {max_page_size}.")
    return offset, page_size


def _bounded_page(entries, offset, page_size):
    """Scan deterministic entries while retaining only the requested page."""
    page = []
    total = 0
    page_end = offset + page_size
    for entry in entries:
        if offset <= total < page_end:
            page.append(entry)
        total += 1
    return page, total


def _identity_token(value):
    return f"identity:{id(value):x}"


def _navigation(depth, ancestor_identity_tokens):
    if type(depth) is not int or not 0 <= depth < MAX_NAVIGATION_DEPTH:
        raise ValueError(f"depth must be between 0 and {MAX_NAVIGATION_DEPTH - 1}.")
    if ancestor_identity_tokens is None:
        return depth, ()
    if type(ancestor_identity_tokens) not in (list, tuple) or len(ancestor_identity_tokens) > MAX_NAVIGATION_DEPTH:
        raise ValueError(f"ancestorIdentityTokens must contain at most {MAX_NAVIGATION_DEPTH} items.")
    ancestry = []
    for token in ancestor_identity_tokens:
        if type(token) is not str or not token.startswith("identity:") or len(token) > 64:
            raise ValueError("ancestorIdentityTokens contains an invalid token.")
        ancestry.append(token)
    return depth, tuple(ancestry)


def _cycle_depth(ancestry, identity_token):
    for index, token in enumerate(ancestry):
        if token == identity_token:
            return index
    return None


def _relation_kind(origin):
    return {
        "item": "collectionItem",
        "mapping": "mappingValue",
        "instance": "instanceField",
        "instance-dict": "instanceDictionaryEntry",
    }.get(origin, "unknown")


def _safe_instance_dict(value):
    try:
        mapping = _PYOBJECT_GENERIC_GET_DICT(value, None)
    except (AttributeError, TypeError):
        return None
    return mapping if type(mapping) is dict else None


def _array_sample_digest(value):
    try:
        dtype = value.dtype
        if dtype.kind not in ("b", "u", "i", "f", "c") or bool(dtype.hasobject):
            return None
        shape = tuple(int(part) for part in value.shape)
        size = int(value.size)
        if size == 0:
            return hashlib.sha256(b"").hexdigest()
        contiguous = _contiguous_array_digest(value)
        if contiguous is not None:
            return contiguous
        sample_count = min(size, _MAX_ARRAY_TOKEN_SAMPLES)
        digest = hashlib.sha256()
        for sample_index in range(sample_count):
            flat_index = 0 if sample_count == 1 else sample_index * (size - 1) // (sample_count - 1)
            coordinates = []
            remainder = flat_index
            for dimension in reversed(shape):
                coordinates.append(remainder % dimension)
                remainder //= dimension
            coordinates.reverse()
            scalar = value[tuple(coordinates)] if shape else value[()]
            payload = scalar.tobytes()
            digest.update(len(payload).to_bytes(4, "big"))
            digest.update(payload)
        return digest.hexdigest()
    except (AttributeError, IndexError, OverflowError, TypeError, ValueError):
        return None


def _contiguous_array_digest(value):
    """Hash small image-like arrays completely and bound work for large arrays."""
    try:
        view = memoryview(value)
        if not view.c_contiguous:
            return None
        raw = view.cast("B")
        byte_count = len(raw)
        digest = hashlib.sha256()
        digest.update(byte_count.to_bytes(8, "big"))
        if byte_count <= _MAX_ARRAY_TOKEN_FULL_BYTES:
            digest.update(raw)
            return digest.hexdigest()

        window_count = min(_MAX_ARRAY_TOKEN_WINDOWS, byte_count)
        window_size = min(_MAX_ARRAY_TOKEN_WINDOW_BYTES, byte_count)
        last_start = byte_count - window_size
        for index in range(window_count):
            start = 0 if window_count == 1 else index * last_start // (window_count - 1)
            digest.update(start.to_bytes(8, "big"))
            digest.update(raw[start:start + window_size])
        return digest.hexdigest()
    except (BufferError, OverflowError, TypeError, ValueError):
        return None
