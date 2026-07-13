import ctypes
import hashlib
import sys
import types

from . import arrays
from .safe_metadata import bounded_text, type_module, type_name


MAX_PREVIEW_ROWS = 200
MAX_PREVIEW_COLUMNS = 100
MAX_PREVIEW_CELLS = 2000
MAX_DESCRIBE_COLUMNS = 100
_MAX_CELL_TEXT = 96
_MAX_LABEL_ITEMS = 8
_MAX_LABEL_DEPTH = 3
_MAX_DIRECT_INT_BITS = 4096
_MAX_FINGERPRINT_SAMPLES = 64
_PY_TPFLAGS_HEAPTYPE = 1 << 9
_MISSING = object()

_PYOBJECT_GENERIC_GET_DICT = ctypes.pythonapi.PyObject_GenericGetDict
_PYOBJECT_GENERIC_GET_DICT.argtypes = (ctypes.c_void_p, ctypes.c_void_p)
_PYOBJECT_GENERIC_GET_DICT.restype = ctypes.py_object


def is_exact_dataframe(value):
    """Identify an exact, already-loaded pandas DataFrame without importing pandas."""
    dataframe_type = _loaded_dataframe_type()
    if dataframe_type is None or type(value) is not dataframe_type:
        return False
    return _frame_snapshot(value) is not None


def summary_metadata(value):
    snapshot = _require_snapshot(value)
    return {
        "rows": snapshot["totalRows"],
        "columns": snapshot["totalColumns"],
    }


def sample_fingerprint(value, maximum_samples=_MAX_FINGERPRINT_SAMPLES):
    """Return a bounded sampled token; it is intentionally not a full-frame checksum."""
    if type(maximum_samples) is not int or not 1 <= maximum_samples <= _MAX_FINGERPRINT_SAMPLES:
        raise ValueError(f"maximumSamples must be between 1 and {_MAX_FINGERPRINT_SAMPLES}.")
    snapshot = _require_snapshot(value)
    total_rows = snapshot["totalRows"]
    total_columns = snapshot["totalColumns"]
    total_cells = total_rows * total_columns
    digest = hashlib.sha256()
    digest.update(f"{total_rows}:{total_columns}".encode("ascii"))
    if total_cells:
        sample_count = min(total_cells, maximum_samples)
        flat_positions = [
            0 if sample_count == 1 else sample_index * (total_cells - 1) // (sample_count - 1)
            for sample_index in range(sample_count)
        ]
        column_positions = sorted({flat_position % total_columns for flat_position in flat_positions})
        readers = _column_readers(snapshot, column_positions)
        for flat_position in flat_positions:
            row_position, column_position = divmod(flat_position, total_columns)
            reader = readers.get(column_position)
            cell = _read_cell(reader, row_position) if reader is not None else _MISSING
            text = "<unavailable>" if cell is _MISSING else _safe_scalar_text(cell)
            payload = text.encode("utf-8", "surrogatepass")
            digest.update(row_position.to_bytes(8, "big", signed=False))
            digest.update(column_position.to_bytes(8, "big", signed=False))
            digest.update(len(payload).to_bytes(4, "big", signed=False))
            digest.update(payload)
    digest.update(b"stable" if _snapshot_still_current(value, snapshot) else b"mutated")
    return "dataframe-sample:v1:" + digest.hexdigest()


def describe(value):
    snapshot = _require_snapshot(value)
    column_count = min(snapshot["totalColumns"], MAX_DESCRIBE_COLUMNS)
    positions = list(range(column_count))
    columns = _column_metadata(snapshot, positions)
    mutation_detected = not _snapshot_still_current(value, snapshot)
    return {
        "adapterKind": "pandas.DataFrame",
        "totalRows": snapshot["totalRows"],
        "totalColumns": snapshot["totalColumns"],
        "columns": columns,
        "columnsTruncated": column_count < snapshot["totalColumns"],
        "mutationDetected": mutation_detected,
        "snapshotConsistent": not mutation_detected,
        "maxPreviewRows": MAX_PREVIEW_ROWS,
        "maxPreviewColumns": MAX_PREVIEW_COLUMNS,
    }


def preview(value, row_offset=0, row_count=50, column_offset=0, column_count=20):
    row_offset = _non_negative_integer(row_offset, "rowOffset")
    column_offset = _non_negative_integer(column_offset, "columnOffset")
    row_count = _bounded_count(row_count, "rowCount", MAX_PREVIEW_ROWS)
    column_count = _bounded_count(column_count, "columnCount", MAX_PREVIEW_COLUMNS)
    snapshot = _require_snapshot(value)

    column_end = min(snapshot["totalColumns"], column_offset + column_count)
    column_positions = list(range(column_offset, column_end))
    maximum_rows_for_cells = MAX_PREVIEW_CELLS // max(1, len(column_positions))
    effective_row_count = min(row_count, maximum_rows_for_cells)
    row_end = min(snapshot["totalRows"], row_offset + effective_row_count)
    row_positions = list(range(row_offset, row_end))
    readers = _column_readers(snapshot, column_positions)
    columns = _column_metadata(snapshot, column_positions, readers)

    rows = []
    index_labels = []
    read_failed = False
    for row_position in row_positions:
        label = _axis_label(snapshot["indexAxis"], row_position)
        if label is _MISSING:
            index_labels.append("<unavailable>")
            read_failed = True
        else:
            index_labels.append(_safe_scalar_text(label))
        row = []
        for column_position in column_positions:
            reader = readers.get(column_position)
            cell = _read_cell(reader, row_position) if reader is not None else _MISSING
            if cell is _MISSING:
                row.append("<unavailable>")
                read_failed = True
            else:
                row.append(_safe_scalar_text(cell))
        rows.append(row)

    mutation_detected = read_failed or not _snapshot_still_current(value, snapshot)
    has_more_rows = row_end < snapshot["totalRows"]
    has_more_columns = column_end < snapshot["totalColumns"]
    return {
        "adapterKind": "pandas.DataFrame",
        "columns": columns,
        "indexLabels": index_labels,
        "rows": rows,
        "rowOffset": row_offset,
        "rowCount": len(rows),
        "totalRows": snapshot["totalRows"],
        "columnOffset": column_offset,
        "columnCount": len(columns),
        "totalColumns": snapshot["totalColumns"],
        "hasMoreRows": has_more_rows,
        "hasMoreColumns": has_more_columns,
        "rowsTruncated": row_offset > 0 or has_more_rows,
        "columnsTruncated": column_offset > 0 or has_more_columns,
        "cellLimitApplied": effective_row_count < row_count,
        "mutationDetected": mutation_detected,
        "snapshotConsistent": not mutation_detected,
    }


def _loaded_dataframe_type():
    pandas_module = _exact_module("pandas")
    frame_module = _exact_module("pandas.core.frame")
    if pandas_module is None or frame_module is None:
        return None
    pandas_namespace = types.ModuleType.__getattribute__(pandas_module, "__dict__")
    frame_namespace = types.ModuleType.__getattribute__(frame_module, "__dict__")
    dataframe_type = dict.get(frame_namespace, "DataFrame")
    if type(dataframe_type) is not type or dict.get(pandas_namespace, "DataFrame") is not dataframe_type:
        return None
    try:
        flags = type.__getattribute__(dataframe_type, "__flags__")
        module_name = type.__getattribute__(dataframe_type, "__module__")
        class_name = type.__getattribute__(dataframe_type, "__name__")
        qualified_name = type.__getattribute__(dataframe_type, "__qualname__")
        class_namespace = type.__getattribute__(dataframe_type, "__dict__")
        mro = type.__getattribute__(dataframe_type, "__mro__")
    except AttributeError:
        return None
    if (
        type(flags) is not int
        or not flags & _PY_TPFLAGS_HEAPTYPE
        or module_name not in ("pandas", "pandas.core.frame")
        or class_name != "DataFrame"
        or qualified_name != "DataFrame"
        or type(mro) is not tuple
        or not mro
        or mro[-1] is not object
    ):
        return None
    base_signatures = {
        (type_module(base), type_name(base))
        for base in mro[1:]
        if type(base) is type
    }
    if ("pandas.core.generic", "NDFrame") not in base_signatures:
        return None
    initializer = class_namespace.get("__init__")
    if type(initializer) is not types.FunctionType:
        return None
    try:
        code = types.FunctionType.__getattribute__(initializer, "__code__")
        filename = types.CodeType.__getattribute__(code, "co_filename")
        code_name = types.CodeType.__getattribute__(code, "co_name")
    except AttributeError:
        return None
    normalized_filename = filename.replace("\\", "/") if type(filename) is str else ""
    if code_name != "__init__" or not normalized_filename.endswith("/pandas/core/frame.py"):
        return None
    return dataframe_type


def _exact_module(name):
    try:
        module = dict.get(sys.modules, name)
    except TypeError:
        return None
    if type(module) is not types.ModuleType:
        return None
    namespace = types.ModuleType.__getattribute__(module, "__dict__")
    return module if dict.get(namespace, "__name__") == name else None


def _require_snapshot(value):
    dataframe_type = _loaded_dataframe_type()
    if dataframe_type is None or type(value) is not dataframe_type:
        raise ValueError("The selected object is not an exact loaded pandas DataFrame.")
    snapshot = _frame_snapshot(value)
    if snapshot is None:
        raise ValueError("The selected pandas DataFrame does not expose a supported safe block manager.")
    return snapshot


def _frame_snapshot(value):
    instance_dict = _safe_instance_dict(value)
    if type(instance_dict) is not dict:
        return None
    manager = dict.get(instance_dict, "_mgr")
    manager_state = _manager_state(manager)
    if manager_state is None:
        return None
    axes, blocks, manager_base = manager_state
    if len(axes) != 2:
        return None
    columns_axis, index_axis = axes
    total_columns = _axis_length(columns_axis)
    total_rows = _axis_length(index_axis)
    if total_columns is None or total_rows is None:
        return None
    return {
        "manager": manager,
        "managerBase": manager_base,
        "axes": axes,
        "blocks": blocks,
        "columnsAxis": columns_axis,
        "indexAxis": index_axis,
        "totalRows": total_rows,
        "totalColumns": total_columns,
    }


def _manager_state(manager):
    manager_type = type(manager)
    if type(manager_type) is not type:
        return None
    try:
        mro = type.__getattribute__(manager_type, "__mro__")
    except AttributeError:
        return None
    manager_base = _extension_descriptor_base(
        mro, "pandas._libs.internals", "BlockManager")
    if manager_base is None:
        return None
    namespace = type.__getattribute__(manager_base, "__dict__")
    axes_descriptor = namespace.get("axes")
    blocks_descriptor = namespace.get("blocks")
    if type(axes_descriptor) is not types.GetSetDescriptorType or type(blocks_descriptor) is not types.GetSetDescriptorType:
        return None
    try:
        axes = axes_descriptor.__get__(manager, manager_type)
        blocks = blocks_descriptor.__get__(manager, manager_type)
    except (AttributeError, RuntimeError, TypeError, ValueError):
        return None
    if type(axes) is not list or type(blocks) is not tuple:
        return None
    return axes, blocks, manager_base


def _snapshot_still_current(value, snapshot):
    instance_dict = _safe_instance_dict(value)
    if type(instance_dict) is not dict or dict.get(instance_dict, "_mgr") is not snapshot["manager"]:
        return False
    state = _manager_state(snapshot["manager"])
    if state is None:
        return False
    axes, blocks, _ = state
    if axes is not snapshot["axes"] or blocks is not snapshot["blocks"]:
        return False
    if len(axes) != 2 or axes[0] is not snapshot["columnsAxis"] or axes[1] is not snapshot["indexAxis"]:
        return False
    return (
        _axis_length(axes[0]) == snapshot["totalColumns"]
        and _axis_length(axes[1]) == snapshot["totalRows"]
    )


def _axis_length(axis):
    state = _axis_state(axis)
    if state is None:
        return None
    kind, payload = state
    if kind == "range":
        return len(payload)
    if kind == "array":
        array, _ = payload
        return int(array.shape[0]) if array.ndim >= 1 else 0
    if kind == "multi":
        _, codes = payload
        if not codes:
            return 0
        first = codes[0]
        return int(first.shape[0]) if arrays.is_exact_ndarray(first) and first.ndim == 1 else None
    return None


def _axis_label(axis, position):
    state = _axis_state(axis)
    if state is None:
        return _MISSING
    kind, payload = state
    if kind == "range":
        try:
            return payload[position]
        except IndexError:
            return _MISSING
    if kind == "array":
        array, mask = payload
        return _array_scalar(array, mask, position, 0, 1)
    if kind == "multi":
        levels, codes = payload
        parts = []
        for level, code_array in zip(levels, codes):
            if not arrays.is_exact_ndarray(code_array) or code_array.ndim != 1 or position >= int(code_array.shape[0]):
                return _MISSING
            code = code_array[position]
            try:
                code_value = int(code)
            except (OverflowError, TypeError, ValueError):
                return _MISSING
            if code_value < 0:
                parts.append("<NA>")
                continue
            level_value = _axis_label(level, code_value)
            parts.append("<unavailable>" if level_value is _MISSING else _safe_scalar_text(level_value))
        return "(" + ", ".join(parts) + ")"
    return _MISSING


def _axis_state(axis):
    instance_dict = _safe_instance_dict(axis)
    if type(instance_dict) is not dict:
        return None
    raw_range = dict.get(instance_dict, "_range")
    if type(raw_range) is range:
        return "range", raw_range
    levels = dict.get(instance_dict, "_levels")
    codes = dict.get(instance_dict, "_codes")
    if levels is not None and codes is not None:
        level_items = _direct_list_items(levels)
        code_items = _direct_list_items(codes)
        if level_items is not None and code_items is not None and len(level_items) == len(code_items):
            return "multi", (level_items, code_items)
    data = dict.get(instance_dict, "_data")
    storage = _array_storage(data)
    if storage is not None:
        return "array", storage
    return None


def _direct_list_items(value):
    value_type = type(value)
    try:
        mro = type.__getattribute__(value_type, "__mro__")
    except AttributeError:
        return None
    if list not in mro:
        return None
    try:
        count = list.__len__(value)
        return [list.__getitem__(value, index) for index in range(count)]
    except (IndexError, TypeError):
        return None


def _column_metadata(snapshot, positions, readers=None):
    if readers is None:
        readers = _column_readers(snapshot, positions)
    result = []
    for position in positions:
        name = _axis_label(snapshot["columnsAxis"], position)
        reader = readers.get(position)
        result.append({
            "position": position,
            "name": "<unavailable>" if name is _MISSING else _safe_scalar_text(name),
            "dtype": _reader_dtype(reader),
        })
    return result


def _column_readers(snapshot, positions):
    wanted = set(positions)
    readers = {}
    if not wanted:
        return readers
    for block in snapshot["blocks"]:
        block_state = _block_state(block, snapshot["totalColumns"], wanted)
        if block_state is None:
            continue
        storage, placements, placement_count, value_kind = block_state
        array, mask = storage if storage is not None else (None, None)
        for local_position, column_position in placements:
            readers[column_position] = (
                array,
                mask,
                local_position,
                placement_count,
                type_name(type(block)),
                value_kind,
            )
        if len(readers) == len(wanted):
            break
    return readers


def _block_state(block, total_columns, wanted):
    block_type = type(block)
    try:
        mro = type.__getattribute__(block_type, "__mro__")
    except AttributeError:
        return None
    block_base = _extension_descriptor_base(mro, "pandas._libs.internals", "Block")
    if block_base is None:
        return None
    namespace = type.__getattribute__(block_base, "__dict__")
    values_descriptor = namespace.get("values")
    placement_descriptor = namespace.get("_mgr_locs")
    if type(values_descriptor) is not types.GetSetDescriptorType or type(placement_descriptor) is not types.GetSetDescriptorType:
        return None
    try:
        values = values_descriptor.__get__(block, block_type)
        placement = placement_descriptor.__get__(block, block_type)
    except (AttributeError, RuntimeError, TypeError, ValueError):
        return None
    storage = _array_storage(values)
    placement_result = _selected_placements(placement, total_columns, wanted)
    if placement_result is None:
        return None
    placements, placement_count = placement_result
    return storage, placements, placement_count, type_name(type(values))


def _selected_placements(placement, total_columns, wanted):
    placement_type = type(placement)
    try:
        flags = type.__getattribute__(placement_type, "__flags__")
        module_name = type.__getattribute__(placement_type, "__module__")
        class_name = type.__getattribute__(placement_type, "__name__")
        namespace = type.__getattribute__(placement_type, "__dict__")
    except AttributeError:
        return None
    if (
        type(flags) is not int
        or module_name != "pandas._libs.internals"
        or class_name != "BlockPlacement"
    ):
        return None
    descriptor = namespace.get("indexer")
    if type(descriptor) is not types.GetSetDescriptorType:
        return None
    try:
        indexer = descriptor.__get__(placement, placement_type)
    except (AttributeError, RuntimeError, TypeError, ValueError):
        return None
    selected = []
    if type(indexer) is slice:
        start, stop, step = indexer.indices(total_columns)
        for column_position in wanted:
            if step > 0 and start <= column_position < stop and (column_position - start) % step == 0:
                selected.append(((column_position - start) // step, column_position))
        selected.sort()
        return selected, len(range(start, stop, step))
    if arrays.is_exact_ndarray(indexer) and indexer.ndim == 1 and indexer.dtype.kind in ("u", "i"):
        for local_position in range(int(indexer.shape[0])):
            column_position = int(indexer[local_position])
            if column_position in wanted:
                selected.append((local_position, column_position))
                if len(selected) == len(wanted):
                    break
        return selected, int(indexer.shape[0])
    return None


def _array_storage(value):
    if arrays.is_exact_ndarray(value):
        return value, None
    value_type = type(value)
    try:
        mro = type.__getattribute__(value_type, "__mro__")
    except AttributeError:
        return None
    base = _extension_descriptor_base(mro, "pandas._libs.arrays", "NDArrayBacked")
    if base is not None:
        namespace = type.__getattribute__(base, "__dict__")
        descriptor = namespace.get("_ndarray")
        if type(descriptor) is types.GetSetDescriptorType:
            try:
                backing = descriptor.__get__(value, value_type)
            except (AttributeError, RuntimeError, TypeError, ValueError):
                return None
            if arrays.is_exact_ndarray(backing):
                return backing, None
    instance_dict = _safe_instance_dict(value)
    if type(instance_dict) is dict:
        backing = dict.get(instance_dict, "_data")
        mask = dict.get(instance_dict, "_mask")
        if arrays.is_exact_ndarray(backing) and (mask is None or arrays.is_exact_ndarray(mask)):
            return backing, mask
    return None


def _read_cell(reader, row_position):
    if reader is None:
        return _MISSING
    array, mask, local_position, placement_count, _, _ = reader
    if array is None:
        return _MISSING
    return _array_scalar(array, mask, row_position, local_position, placement_count)


def _array_scalar(array, mask, row_position, local_position, placement_count):
    try:
        if array.ndim == 1:
            if local_position != 0 or placement_count != 1 or row_position >= int(array.shape[0]):
                return _MISSING
            coordinates = (row_position,)
        elif array.ndim == 2 and int(array.shape[0]) == placement_count:
            if row_position >= int(array.shape[1]):
                return _MISSING
            coordinates = (local_position, row_position)
        elif array.ndim == 2 and int(array.shape[1]) == placement_count:
            if row_position >= int(array.shape[0]):
                return _MISSING
            coordinates = (row_position, local_position)
        else:
            return _MISSING
        if mask is not None:
            mask_value = mask[coordinates] if mask.shape == array.shape else mask[row_position]
            if type(mask_value) is bool or _is_immutable_numpy_scalar(mask_value):
                if bool(mask_value):
                    return "<NA>"
            else:
                return _MISSING
        return array[coordinates]
    except (IndexError, OverflowError, TypeError, ValueError):
        return _MISSING


def _reader_dtype(reader):
    if reader is None:
        return "<unavailable>"
    array, _, _, _, block_kind, value_kind = reader
    known_extension_dtypes = {
        "StringArray": "string",
        "ArrowStringArray": "string[pyarrow]",
        "BooleanArray": "boolean",
        "IntegerArray": "nullable integer",
        "FloatingArray": "nullable float",
        "DatetimeArray": "datetime",
        "TimedeltaArray": "timedelta",
        "Categorical": "category",
        "PeriodArray": "period",
        "IntervalArray": "interval",
        "SparseArray": "sparse",
    }
    extension_dtype = known_extension_dtypes.get(value_kind)
    if array is None:
        return extension_dtype or bounded_text(value_kind, "<unavailable>", 128)
    try:
        dtype_text = bounded_text(str(array.dtype), "<unknown>", 128)
    except (AttributeError, TypeError, ValueError):
        return "<unknown>"
    if block_kind == "ExtensionBlock" and extension_dtype is not None:
        return extension_dtype
    return dtype_text


def _safe_scalar_text(value, depth=0):
    exact = type(value)
    if value is None:
        return "None"
    if exact is bool:
        return "True" if value else "False"
    if exact is int:
        bit_length = int.bit_length(value)
        return f"<int bits={bit_length}>" if bit_length > _MAX_DIRECT_INT_BITS else repr(value)
    if exact is float:
        if value != value:
            return "NaN"
        if value == float("inf"):
            return "+Infinity"
        if value == float("-inf"):
            return "-Infinity"
        return repr(value)
    if exact is complex:
        return bounded_text(repr(value), "<complex>", _MAX_CELL_TEXT)
    if exact is str:
        return bounded_text(value, "", _MAX_CELL_TEXT)
    if exact in (bytes, bytearray):
        raw = bytes(value[:64])
        suffix = "…" if len(value) > 64 else ""
        return bounded_text(repr(raw) + suffix, "<bytes>", _MAX_CELL_TEXT)
    if exact is tuple:
        if depth >= _MAX_LABEL_DEPTH:
            return "(…)"
        count = len(value)
        body = ", ".join(
            _safe_scalar_text(value[index], depth + 1)
            for index in range(min(count, _MAX_LABEL_ITEMS))
        )
        if count > _MAX_LABEL_ITEMS:
            body += ", …"
        if count == 1:
            body += ","
        return bounded_text("(" + body + ")", "(…)", _MAX_CELL_TEXT)
    if _is_immutable_numpy_scalar(value):
        try:
            return bounded_text(str(value), f"<{type_name(exact)}>", _MAX_CELL_TEXT)
        except (TypeError, ValueError):
            return f"<{type_name(exact)}>"
    module_name = type_module(exact)
    class_name = type_name(exact)
    if module_name.startswith("pandas") and class_name in ("NAType", "NaTType"):
        return "<NA>" if class_name == "NAType" else "NaT"
    return bounded_text(f"<{module_name}.{class_name}>", "<object>", _MAX_CELL_TEXT)


def _is_immutable_numpy_scalar(value):
    value_type = type(value)
    try:
        flags = type.__getattribute__(value_type, "__flags__")
        mro = type.__getattribute__(value_type, "__mro__")
    except AttributeError:
        return False
    if type(flags) is not int or flags & _PY_TPFLAGS_HEAPTYPE:
        return False
    return any(
        type(base) is type
        and type_module(base) == "numpy"
        and type_name(base) == "generic"
        and not type.__getattribute__(base, "__flags__") & _PY_TPFLAGS_HEAPTYPE
        for base in mro
    )


def _extension_descriptor_base(mro, module_name, class_name):
    for base in mro:
        if type(base) is not type:
            continue
        try:
            flags = type.__getattribute__(base, "__flags__")
            base_module = type.__getattribute__(base, "__module__")
            base_name = type.__getattribute__(base, "__name__")
        except AttributeError:
            continue
        if (
            type(flags) is int
            and base_module == module_name
            and base_name == class_name
        ):
            return base
    return None


def _safe_instance_dict(value):
    try:
        mapping = _PYOBJECT_GENERIC_GET_DICT(id(value), None)
    except (AttributeError, TypeError):
        return None
    return mapping if type(mapping) is dict else None


def _non_negative_integer(value, name):
    if type(value) is not int or value < 0:
        raise ValueError(f"{name} must be a non-negative integer.")
    return value


def _bounded_count(value, name, maximum):
    if type(value) is not int or not 1 <= value <= maximum:
        raise ValueError(f"{name} must be between 1 and {maximum}.")
    return value
