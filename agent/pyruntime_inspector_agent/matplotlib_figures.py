import ctypes
import hashlib
import sys
import types

from .safe_metadata import is_class_object, type_module, type_name


MAX_PREVIEW_DIMENSION = 1024
MAX_PREVIEW_BYTES = 4 * 1024 * 1024
_MAX_FINGERPRINT_PIXELS = 4096
_PY_TPFLAGS_HEAPTYPE = 1 << 9

_PYOBJECT_GENERIC_GET_DICT = ctypes.pythonapi.PyObject_GenericGetDict
_PYOBJECT_GENERIC_GET_DICT.argtypes = (ctypes.c_void_p, ctypes.c_void_p)
_PYOBJECT_GENERIC_GET_DICT.restype = ctypes.py_object

_UNAVAILABLE = {
    "not-rendered": (
        "The owning Figure does not have a completed Agg render.",
        "Call figure.canvas.draw() in the target code, then refresh the preview.",
    ),
    "stale": (
        "The owning Figure has pending changes or has not been drawn yet.",
        "Call figure.canvas.draw() in the target code after its final changes, then refresh the preview.",
    ),
    "non-agg": (
        "The owning Figure is not attached to an Agg-derived canvas.",
        "Render the Figure with an Agg-derived canvas in the target code.",
    ),
    "detached-axes": (
        "The selected Axes is not attached to an exact regular Figure.",
        "Select its owning Figure or attach the Axes to a Figure in the target code.",
    ),
    "invalid-renderer": (
        "The Agg renderer internals are unavailable or unsupported.",
        "Draw the Figure again in the target code, then refresh the preview.",
    ),
    "invalid-buffer": (
        "The Agg renderer does not expose the expected RGBA byte buffer.",
        "Use a standard Agg-derived Matplotlib canvas and draw the Figure again.",
    ),
    "buffer-changed": (
        "The rendered buffer changed while PyMonitor was copying the bounded preview.",
        "Refresh the preview after the target finishes drawing.",
    ),
}


def adapter_kind(value):
    """Identify exact, already-loaded regular Matplotlib Figure/Axes objects."""
    loaded = _loaded_source_types()
    if loaded is None:
        return None
    figure_type, axes_type = loaded
    exact = type(value)
    if exact is figure_type:
        return "matplotlib.Figure"
    if exact is axes_type:
        return "matplotlib.Axes"
    return None


def is_exact_figure_or_axes(value):
    return adapter_kind(value) is not None


def summary_metadata(value):
    return describe(value)


def safe_preview(value):
    metadata = describe(value)
    source = metadata["sourceKind"]
    if metadata["previewAvailable"]:
        dimensions = f"{metadata['sourceWidth']}x{metadata['sourceHeight']}"
        if source == "Axes":
            return f"Axes(owning Figure rendered={dimensions})"
        return f"Figure(rendered={dimensions})"
    reason = metadata["availability"]["reason"]
    return f"{source}(preview unavailable: {reason})"


def sample_fingerprint(value, maximum_pixels=_MAX_FINGERPRINT_PIXELS):
    if type(maximum_pixels) is not int or not 1 <= maximum_pixels <= _MAX_FINGERPRINT_PIXELS:
        raise ValueError(f"maximumPixels must be between 1 and {_MAX_FINGERPRINT_PIXELS}.")
    metadata, snapshot = _inspect(value)
    digest = hashlib.sha256()
    digest.update(metadata["adapterKind"].encode("ascii"))
    digest.update(metadata["availability"]["state"].encode("ascii"))
    reason = metadata["availability"]["reason"]
    if reason is not None:
        digest.update(reason.encode("ascii"))
    if snapshot is not None:
        height = snapshot["height"]
        width = snapshot["width"]
        digest.update(height.to_bytes(8, "big", signed=False))
        digest.update(width.to_bytes(8, "big", signed=False))
        pixels = height * width
        sample_count = min(pixels, maximum_pixels)
        raw = snapshot["view"].cast("B")
        pixel_step = max(1, pixels // sample_count)
        byte_step = pixel_step * 4
        for channel in range(4):
            stop = (sample_count - 1) * byte_step + channel + 1
            digest.update(raw[channel:stop:byte_step].tobytes())
        if not _snapshot_current(snapshot):
            digest.update(b"buffer-changed")
    return "matplotlib-render:v1:" + digest.hexdigest()


def describe(value):
    metadata, snapshot = _inspect(value)
    if snapshot is not None and not _snapshot_current(snapshot):
        return _unavailable_from(metadata, "buffer-changed")
    return metadata


def preview(value, max_width=MAX_PREVIEW_DIMENSION, max_height=MAX_PREVIEW_DIMENSION):
    max_width = _bounded_dimension(max_width, "maxWidth")
    max_height = _bounded_dimension(max_height, "maxHeight")
    metadata, snapshot = _inspect(value)
    if snapshot is None:
        return metadata, b""

    height = snapshot["height"]
    width = snapshot["width"]
    row_step = max(1, (height + max_height - 1) // max_height)
    column_step = max(1, (width + max_width - 1) // max_width)
    output_height = (height + row_step - 1) // row_step
    output_width = (width + column_step - 1) // column_step
    output_size = output_height * output_width * 4
    if output_size > MAX_PREVIEW_BYTES:
        return _unavailable_from(metadata, "invalid-buffer"), b""

    output = _copy_bgra(snapshot, row_step, column_step, output_width, output_height)
    if (
        not _snapshot_current(snapshot)
        or not _matches_bgra(snapshot, output, row_step, column_step, output_width, output_height)
        or not _snapshot_current(snapshot)
    ):
        return _unavailable_from(metadata, "buffer-changed"), b""

    result = dict(metadata)
    result.update({
        "width": output_width,
        "height": output_height,
        "stride": output_width * 4,
        "pixelFormat": "BGRA32",
        "rowStep": row_step,
        "columnStep": column_step,
        "originX": 0,
        "originY": 0,
        "snapshotConsistent": True,
    })
    return result, bytes(output)


def _inspect(value):
    kind = adapter_kind(value)
    if kind is None:
        raise ValueError("The selected object is not an exact loaded Matplotlib Figure or Axes.")

    source_kind = "Axes" if kind == "matplotlib.Axes" else "Figure"
    metadata = _base_metadata(value, kind, source_kind)
    figure = value
    if source_kind == "Axes":
        axes_state = _safe_instance_dict(value)
        figure = dict.get(axes_state, "_parent_figure") if type(axes_state) is dict else None
        loaded = _loaded_source_types()
        if loaded is None or type(figure) is not loaded[0]:
            return _unavailable_from(metadata, "detached-axes"), None

    metadata["figureAddressHex"] = hex(id(figure))
    figure_state = _safe_instance_dict(figure)
    if type(figure_state) is not dict:
        return _unavailable_from(metadata, "invalid-renderer"), None
    stale = dict.get(figure_state, "_stale")
    metadata["stale"] = stale if type(stale) is bool else None
    canvas = dict.get(figure_state, "canvas")
    metadata["canvasType"] = _qualified_type(canvas)

    render_types = _loaded_render_types()
    if render_types is None or not _is_agg_canvas(canvas, render_types[0]):
        return _unavailable_from(metadata, "non-agg"), None
    canvas_state = _safe_instance_dict(canvas)
    if type(canvas_state) is not dict or dict.get(canvas_state, "figure") is not figure:
        return _unavailable_from(metadata, "invalid-renderer"), None
    renderer = dict.get(canvas_state, "renderer")
    if stale is not False:
        reason = "not-rendered" if renderer is None else "stale"
        return _unavailable_from(metadata, reason), None

    _, renderer_type, core_renderer_type = render_types
    metadata["rendererType"] = _qualified_type(renderer)
    if type(renderer) is not renderer_type:
        return _unavailable_from(metadata, "not-rendered" if renderer is None else "invalid-renderer"), None
    renderer_state = _safe_instance_dict(renderer)
    core = dict.get(renderer_state, "_renderer") if type(renderer_state) is dict else None
    if type(core) is not core_renderer_type:
        return _unavailable_from(metadata, "invalid-renderer"), None
    try:
        view = memoryview(core)
    except (BufferError, TypeError, ValueError):
        return _unavailable_from(metadata, "invalid-buffer"), None
    shape = view.shape
    if (
        view.format != "B"
        or view.itemsize != 1
        or view.ndim != 3
        or type(shape) is not tuple
        or len(shape) != 3
        or shape[2] != 4
        or shape[0] < 1
        or shape[1] < 1
        or not view.c_contiguous
        or view.strides != (shape[1] * 4, 4, 1)
        or view.nbytes != shape[0] * shape[1] * 4
    ):
        return _unavailable_from(metadata, "invalid-buffer"), None

    height, width, _ = shape
    metadata.update({
        "previewAvailable": True,
        "availability": _availability("ready"),
        "sourceWidth": width,
        "sourceHeight": height,
        "sourceChannels": 4,
        "sourcePixelFormat": "RGBA32",
        "sourceBufferBytes": view.nbytes,
        "snapshotConsistent": True,
    })
    snapshot = {
        "figure": figure,
        "canvas": canvas,
        "renderer": renderer,
        "core": core,
        "view": view,
        "height": height,
        "width": width,
    }
    return metadata, snapshot


def _base_metadata(value, adapter, source_kind):
    return {
        "adapterKind": adapter,
        "sourceKind": source_kind,
        "renderedKind": "Figure",
        "axesUsesOwningFigure": source_kind == "Axes",
        "objectAddressHex": hex(id(value)),
        "figureAddressHex": None,
        "canvasType": None,
        "rendererType": None,
        "stale": None,
        "previewAvailable": False,
        "availability": _availability("unavailable", "not-rendered"),
        "sourceWidth": None,
        "sourceHeight": None,
        "sourceChannels": 4,
        "sourcePixelFormat": "RGBA32",
        "sourceBufferBytes": None,
        "maxPreviewWidth": MAX_PREVIEW_DIMENSION,
        "maxPreviewHeight": MAX_PREVIEW_DIMENSION,
        "maxPreviewBytes": MAX_PREVIEW_BYTES,
        "snapshotConsistent": False,
    }


def _unavailable_from(metadata, reason):
    result = dict(metadata)
    result.update({
        "previewAvailable": False,
        "availability": _availability("unavailable", reason),
        "snapshotConsistent": False,
    })
    return result


def _availability(state, reason=None):
    if state == "ready":
        return {
            "state": "ready",
            "reason": None,
            "message": "A current, completed Agg render is available.",
            "nextAction": None,
        }
    message, action = _UNAVAILABLE[reason]
    return {
        "state": "unavailable",
        "reason": reason,
        "message": message,
        "nextAction": action,
    }


def _snapshot_current(snapshot):
    figure_state = _safe_instance_dict(snapshot["figure"])
    canvas_state = _safe_instance_dict(snapshot["canvas"])
    renderer_state = _safe_instance_dict(snapshot["renderer"])
    if (
        type(figure_state) is not dict
        or type(canvas_state) is not dict
        or type(renderer_state) is not dict
        or dict.get(figure_state, "_stale") is not False
        or dict.get(figure_state, "canvas") is not snapshot["canvas"]
        or dict.get(canvas_state, "figure") is not snapshot["figure"]
        or dict.get(canvas_state, "renderer") is not snapshot["renderer"]
        or dict.get(renderer_state, "_renderer") is not snapshot["core"]
    ):
        return False
    try:
        view = memoryview(snapshot["core"])
    except (BufferError, TypeError, ValueError):
        return False
    return (
        view.format == "B"
        and view.itemsize == 1
        and view.ndim == 3
        and view.shape == (snapshot["height"], snapshot["width"], 4)
        and view.strides == (snapshot["width"] * 4, 4, 1)
        and view.c_contiguous
        and view.nbytes == snapshot["height"] * snapshot["width"] * 4
    )


def _copy_bgra(snapshot, row_step, column_step, output_width, output_height):
    output = bytearray(output_width * output_height * 4)
    target = memoryview(output)
    raw = snapshot["view"].cast("B")
    source_stride = snapshot["width"] * 4
    output_stride = output_width * 4
    for output_y, source_y in enumerate(range(0, snapshot["height"], row_step)):
        source_start = source_y * source_stride
        target_start = output_y * output_stride
        _copy_bgra_row(raw, target, source_start, target_start, column_step, output_width)
    if output_height != (snapshot["height"] + row_step - 1) // row_step:
        raise ValueError("The bounded output height is inconsistent.")
    return output


def _matches_bgra(snapshot, expected, row_step, column_step, output_width, output_height):
    raw = snapshot["view"].cast("B")
    expected_view = memoryview(expected)
    source_stride = snapshot["width"] * 4
    output_stride = output_width * 4
    row = bytearray(output_stride)
    row_view = memoryview(row)
    row_count = 0
    for output_y, source_y in enumerate(range(0, snapshot["height"], row_step)):
        source_start = source_y * source_stride
        _copy_bgra_row(raw, row_view, source_start, 0, column_step, output_width)
        target_start = output_y * output_stride
        if row_view != expected_view[target_start:target_start + output_stride]:
            return False
        row_count += 1
    return row_count == output_height


def _copy_bgra_row(raw, target, source_start, target_start, column_step, output_width):
    source_pixel_step = column_step * 4
    target_end = target_start + output_width * 4
    for target_channel, source_channel in ((0, 2), (1, 1), (2, 0), (3, 3)):
        first = source_start + source_channel
        last = first + (output_width - 1) * source_pixel_step
        target[target_start + target_channel:target_end:4] = raw[first:last + 1:source_pixel_step]


def _loaded_source_types():
    figure_module = _exact_module("matplotlib.figure")
    axes_package = _exact_module("matplotlib.axes")
    axes_module = _exact_module("matplotlib.axes._axes")
    if figure_module is None or axes_package is None or axes_module is None:
        return None
    figure_namespace = types.ModuleType.__getattribute__(figure_module, "__dict__")
    axes_package_namespace = types.ModuleType.__getattribute__(axes_package, "__dict__")
    axes_namespace = types.ModuleType.__getattribute__(axes_module, "__dict__")
    figure_type = dict.get(figure_namespace, "Figure")
    axes_type = dict.get(axes_namespace, "Axes")
    if (
        not _valid_python_type(figure_type, "matplotlib.figure", "Figure", "__init__", "/matplotlib/figure.py")
        or not _valid_python_type(axes_type, "matplotlib.axes._axes", "Axes", "plot", "/matplotlib/axes/_axes.py")
        or dict.get(axes_package_namespace, "Axes") is not axes_type
    ):
        return None
    return figure_type, axes_type


def _loaded_render_types():
    agg_module = _exact_module("matplotlib.backends.backend_agg")
    core_module = _exact_module("matplotlib.backends._backend_agg")
    if agg_module is None or core_module is None:
        return None
    agg_namespace = types.ModuleType.__getattribute__(agg_module, "__dict__")
    core_namespace = types.ModuleType.__getattribute__(core_module, "__dict__")
    canvas_type = dict.get(agg_namespace, "FigureCanvasAgg")
    renderer_type = dict.get(agg_namespace, "RendererAgg")
    core_renderer_type = dict.get(core_namespace, "RendererAgg")
    if (
        not _valid_python_type(
            canvas_type,
            "matplotlib.backends.backend_agg",
            "FigureCanvasAgg",
            "draw",
            "/matplotlib/backends/backend_agg.py",
        )
        or not _valid_python_type(
            renderer_type,
            "matplotlib.backends.backend_agg",
            "RendererAgg",
            "__init__",
            "/matplotlib/backends/backend_agg.py",
        )
        or not _valid_core_renderer_type(core_renderer_type)
    ):
        return None
    return canvas_type, renderer_type, core_renderer_type


def _is_agg_canvas(canvas, agg_canvas_type):
    canvas_type = type(canvas)
    try:
        mro = type.__getattribute__(canvas_type, "__mro__")
        module_name = type.__getattribute__(canvas_type, "__module__")
        class_name = type.__getattribute__(canvas_type, "__name__")
    except AttributeError:
        return False
    if (
        type(mro) is not tuple
        or type(module_name) is not str
        or not module_name.startswith("matplotlib.backends.")
        or type(class_name) is not str
        or not any(base is agg_canvas_type for base in mro)
    ):
        return False
    module = _exact_module(module_name)
    if module is None:
        return False
    namespace = types.ModuleType.__getattribute__(module, "__dict__")
    return dict.get(namespace, class_name) is canvas_type


def _valid_python_type(value, module_name, class_name, function_name, source_suffix):
    if not _valid_type_identity(value, module_name, class_name):
        return False
    try:
        flags = type.__getattribute__(value, "__flags__")
        namespace = type.__getattribute__(value, "__dict__")
    except AttributeError:
        return False
    function = namespace.get(function_name)
    if type(flags) is not int or not flags & _PY_TPFLAGS_HEAPTYPE or type(function) is not types.FunctionType:
        return False
    try:
        code = types.FunctionType.__getattribute__(function, "__code__")
        filename = types.CodeType.__getattribute__(code, "co_filename")
        code_name = types.CodeType.__getattribute__(code, "co_name")
    except AttributeError:
        return False
    normalized = filename.replace("\\", "/") if type(filename) is str else ""
    return type(code_name) is str and code_name == function_name and normalized.endswith(source_suffix)


def _valid_type_identity(value, module_name, class_name):
    if not is_class_object(value):
        return False
    try:
        actual_module = type.__getattribute__(value, "__module__")
        actual_name = type.__getattribute__(value, "__name__")
        qualified_name = type.__getattribute__(value, "__qualname__")
        mro = type.__getattribute__(value, "__mro__")
    except AttributeError:
        return False
    return (
        type(actual_module) is str
        and type(actual_name) is str
        and type(qualified_name) is str
        and actual_module == module_name
        and actual_name == class_name
        and qualified_name == class_name
        and type(mro) is tuple
        and mro
        and mro[-1] is object
    )


def _valid_core_renderer_type(value):
    if not _valid_type_identity(
        value,
        "matplotlib.backends._backend_agg",
        "RendererAgg",
    ):
        return False
    try:
        flags = type.__getattribute__(value, "__flags__")
        mro = type.__getattribute__(value, "__mro__")
        namespace = type.__getattribute__(value, "__dict__")
    except AttributeError:
        return False
    if type(flags) is not int or type(mro) is not tuple:
        return False

    initializer = namespace.get("__init__")
    draw_path = namespace.get("draw_path")
    descriptor_type = type(initializer)
    try:
        descriptor_module = type.__getattribute__(descriptor_type, "__module__")
        descriptor_name = type.__getattribute__(descriptor_type, "__name__")
        descriptor_flags = type.__getattribute__(descriptor_type, "__flags__")
    except AttributeError:
        return False
    descriptors_are_native = (
        initializer is not None
        and type(draw_path) is descriptor_type
        and type(descriptor_module) is str
        and type(descriptor_name) is str
        and type(descriptor_flags) is int
        and not descriptor_flags & _PY_TPFLAGS_HEAPTYPE
        and descriptor_module == "builtins"
        and descriptor_name in ("instancemethod", "method_descriptor")
    )
    if not descriptors_are_native:
        return False

    if not flags & _PY_TPFLAGS_HEAPTYPE:
        return type(value) is type

    metaclass = type(value)
    if len(mro) != 3 or mro[0] is not value or mro[2] is not object:
        return False
    base = mro[1]
    if (
        not _type_identity_is(metaclass, "pybind11_builtins", "pybind11_type")
        or type(base) is not metaclass
        or not _type_identity_is(base, "pybind11_builtins", "pybind11_object")
        or not _valid_pybind11_metaclass(metaclass)
        or not _valid_pybind11_base(base)
    ):
        return False

    for candidate in mro[:-1]:
        try:
            candidate_namespace = type.__getattribute__(candidate, "__dict__")
        except AttributeError:
            return False
        for slot_name in ("__buffer__", "__release_buffer__"):
            if slot_name in candidate_namespace and not _native_slot_owned_by(
                candidate,
                candidate_namespace,
                slot_name,
            ):
                return False
    return True


def _valid_pybind11_metaclass(metaclass):
    try:
        namespace = type.__getattribute__(metaclass, "__dict__")
    except AttributeError:
        return False
    return all(
        _native_slot_owned_by(metaclass, namespace, name)
        for name in ("__call__", "__getattribute__", "__setattr__", "__delattr__")
    )


def _valid_pybind11_base(base):
    try:
        namespace = type.__getattribute__(base, "__dict__")
    except AttributeError:
        return False
    initializer_is_native = _native_slot_owned_by(base, namespace, "__init__")
    constructor = namespace.get("__new__")
    if type(constructor) is not types.BuiltinFunctionType:
        return False
    try:
        constructor_owner = types.BuiltinFunctionType.__getattribute__(constructor, "__self__")
    except AttributeError:
        return False
    return initializer_is_native and constructor_owner is base


def _native_slot_owned_by(owner, namespace, name):
    descriptor = namespace.get(name)
    descriptor_type = type(descriptor)
    try:
        descriptor_module = type.__getattribute__(descriptor_type, "__module__")
        descriptor_name = type.__getattribute__(descriptor_type, "__name__")
        descriptor_flags = type.__getattribute__(descriptor_type, "__flags__")
    except AttributeError:
        return False
    if (
        type(descriptor_module) is not str
        or type(descriptor_name) is not str
        or type(descriptor_flags) is not int
        or descriptor_flags & _PY_TPFLAGS_HEAPTYPE
        or descriptor_module != "builtins"
        or descriptor_name not in ("wrapper_descriptor", "method_descriptor")
    ):
        return False
    try:
        descriptor_owner = descriptor_type.__getattribute__(descriptor, "__objclass__")
    except AttributeError:
        return False
    return descriptor_owner is owner


def _type_identity_is(value, module_name, class_name):
    if not is_class_object(value):
        return False
    try:
        actual_module = type.__getattribute__(value, "__module__")
        actual_name = type.__getattribute__(value, "__name__")
        qualified_name = type.__getattribute__(value, "__qualname__")
    except AttributeError:
        return False
    return (
        type(actual_module) is str
        and type(actual_name) is str
        and type(qualified_name) is str
        and actual_module == module_name
        and actual_name == class_name
        and qualified_name == class_name
    )


def _exact_module(name):
    try:
        module = dict.get(sys.modules, name)
    except TypeError:
        return None
    if type(module) is not types.ModuleType:
        return None
    namespace = types.ModuleType.__getattribute__(module, "__dict__")
    module_name = dict.get(namespace, "__name__")
    return module if type(module_name) is str and module_name == name else None


def _safe_instance_dict(value):
    try:
        mapping = _PYOBJECT_GENERIC_GET_DICT(id(value), None)
    except (AttributeError, TypeError):
        return None
    return mapping if type(mapping) is dict else None


def _qualified_type(value):
    if value is None:
        return None
    exact = type(value)
    return f"{type_module(exact)}.{type_name(exact)}"


def _bounded_dimension(value, name):
    if type(value) is not int or not 1 <= value <= MAX_PREVIEW_DIMENSION:
        raise ValueError(f"{name} must be between 1 and {MAX_PREVIEW_DIMENSION}.")
    return value
