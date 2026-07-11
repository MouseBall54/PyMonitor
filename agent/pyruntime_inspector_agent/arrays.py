import math
import sys
import types


_MAX_IMAGE_DIMENSION = 1024
_MAX_HISTOGRAM_SAMPLES = 1_000_000


def _numpy_module():
    module = sys.modules.get("numpy")
    if type(module) is not types.ModuleType:
        return None
    namespace = types.ModuleType.__getattribute__(module, "__dict__")
    ndarray = namespace.get("ndarray")
    return module if type(ndarray) is type else None


def is_exact_ndarray(value):
    module = _numpy_module()
    if module is None:
        return False
    ndarray = types.ModuleType.__getattribute__(module, "__dict__")["ndarray"]
    return type(value) is ndarray


def _require_supported_array(value):
    if not is_exact_ndarray(value):
        raise ValueError("The selected object is not an exact loaded NumPy ndarray.")
    if value.dtype.kind not in ("b", "u", "i", "f"):
        raise ValueError("Array preview supports bool, integer, and floating-point dtypes.")


def guess_layout(value):
    if value.ndim == 2:
        return "GRAY", "certain"
    if value.ndim != 3:
        return "unsupported", "none"
    first = value.shape[0] in (1, 3, 4)
    last = value.shape[-1] in (1, 3, 4)
    if first and last:
        return "uncertain", "low"
    if last:
        return "HWC", "high"
    if first:
        return "CHW", "high"
    return "volume", "medium"


def describe(value, summarize):
    if not is_exact_ndarray(value):
        raise ValueError("The selected object is not an exact loaded NumPy ndarray.")
    interface = value.__array_interface__
    layout, confidence = guess_layout(value)
    supported = value.dtype.kind in ("b", "u", "i", "f")
    preview_modes = ["GRAY", "HWC", "CHW", "VOLUME"] if supported else []
    normalization_modes = ["AUTO", "NONE", "MINMAX", "PERCENTILE"] if supported else []
    if value.dtype.kind in ("b", "u", "i"):
        normalization_modes.append("LABEL")
    return {
        "ndim": int(value.ndim),
        "shape": [int(part) for part in value.shape],
        "dtype": str(value.dtype),
        "dtypeKind": value.dtype.kind,
        "byteOrder": value.dtype.byteorder,
        "itemSize": int(value.itemsize),
        "strides": [int(part) for part in value.strides],
        "size": int(value.size),
        "nbytes": int(value.nbytes),
        "cContiguous": bool(value.flags.c_contiguous),
        "fContiguous": bool(value.flags.f_contiguous),
        "writeable": bool(value.flags.writeable),
        "aligned": bool(value.flags.aligned),
        "ownsData": bool(value.flags.owndata),
        "base": summarize(value.base) if value.base is not None else None,
        "objectAddressHex": hex(id(value)),
        "dataAddressHex": hex(int(interface["data"][0])),
        "layoutGuess": layout,
        "layoutConfidence": confidence,
        "supportedPreviewModes": preview_modes,
        "normalizationModes": normalization_modes,
    }


def preview(
    value,
    max_width=1024,
    max_height=1024,
    layout=None,
    color_order="RGB",
    enabled_channels=None,
    slice_axis=None,
    slice_index=None,
    normalization="AUTO",
    percentile_low=1.0,
    percentile_high=99.0,
):
    _require_supported_array(value)
    max_width = _bounded_dimension(max_width, "maxWidth")
    max_height = _bounded_dimension(max_height, "maxHeight")
    image, channels, selected_layout, selected_axis, selected_index = _select_image(
        value, layout, slice_axis, slice_index)
    height, width = image.shape[:2]
    row_step = max(1, (height + max_height - 1) // max_height)
    column_step = max(1, (width + max_width - 1) // max_width)
    sampled = image[::row_step, ::column_step]
    metadata, binary = _render(
        sampled, channels, color_order, enabled_channels, normalization,
        percentile_low, percentile_high)
    metadata.update({
        "layout": selected_layout,
        "sliceAxis": selected_axis,
        "sliceIndex": selected_index,
        "rowStep": row_step,
        "columnStep": column_step,
        "sourceWidth": int(width),
        "sourceHeight": int(height),
        "originX": 0,
        "originY": 0,
    })
    return metadata, binary


def tile(
    value,
    x,
    y,
    width,
    height,
    layout=None,
    color_order="RGB",
    enabled_channels=None,
    slice_axis=None,
    slice_index=None,
    normalization="AUTO",
    percentile_low=1.0,
    percentile_high=99.0,
):
    _require_supported_array(value)
    x = _non_negative_integer(x, "x")
    y = _non_negative_integer(y, "y")
    width = _bounded_dimension(width, "width")
    height = _bounded_dimension(height, "height")
    image, channels, selected_layout, selected_axis, selected_index = _select_image(
        value, layout, slice_axis, slice_index)
    source_height, source_width = image.shape[:2]
    if x >= source_width or y >= source_height:
        raise ValueError("Tile origin is outside the selected array view.")
    output_width = min(width, source_width - x)
    output_height = min(height, source_height - y)
    selected = image[y:y + output_height, x:x + output_width]
    metadata, binary = _render(
        selected, channels, color_order, enabled_channels, normalization,
        percentile_low, percentile_high)
    metadata.update({
        "layout": selected_layout,
        "sliceAxis": selected_axis,
        "sliceIndex": selected_index,
        "rowStep": 1,
        "columnStep": 1,
        "sourceWidth": int(source_width),
        "sourceHeight": int(source_height),
        "originX": x,
        "originY": y,
    })
    return metadata, binary


def histogram(
    value,
    channel=0,
    bins=256,
    layout=None,
    slice_axis=None,
    slice_index=None,
):
    _require_supported_array(value)
    if type(bins) is not int or not 2 <= bins <= 512:
        raise ValueError("bins must be between 2 and 512.")
    image, channels, selected_layout, selected_axis, selected_index = _select_image(
        value, layout, slice_axis, slice_index)
    if type(channel) is not int or not 0 <= channel < channels:
        raise ValueError("channel is outside the selected array view.")
    selected = image if channels == 1 else image[:, :, channel]
    flat = selected.reshape(-1)
    sample_step = max(1, (flat.size + _MAX_HISTOGRAM_SAMPLES - 1) // _MAX_HISTOGRAM_SAMPLES)
    sample = flat[::sample_step].copy(order="C")
    numpy = _numpy_module()
    if value.dtype.kind == "f":
        nan_count = int(numpy.isnan(sample).sum())
        positive_inf_count = int(numpy.isposinf(sample).sum())
        negative_inf_count = int(numpy.isneginf(sample).sum())
        finite = sample[numpy.isfinite(sample)]
    else:
        nan_count = positive_inf_count = negative_inf_count = 0
        finite = sample
    if finite.size:
        counts, edges = numpy.histogram(finite, bins=bins)
        minimum = float(finite.min())
        maximum = float(finite.max())
    else:
        counts = numpy.zeros(bins, dtype=numpy.int64)
        edges = numpy.linspace(0.0, 1.0, bins + 1)
        minimum = maximum = None
    return {
        "layout": selected_layout,
        "sliceAxis": selected_axis,
        "sliceIndex": selected_index,
        "channel": channel,
        "bins": bins,
        "counts": [int(item) for item in counts.tolist()],
        "binEdges": [float(item) for item in edges.tolist()],
        "sampleCount": int(sample.size),
        "sampleStep": int(sample_step),
        "finiteCount": int(finite.size),
        "nanCount": nan_count,
        "positiveInfinityCount": positive_inf_count,
        "negativeInfinityCount": negative_inf_count,
        "minimum": minimum,
        "maximum": maximum,
        "dtype": str(value.dtype),
    }


def pixel(value, coordinates, layout=None, slice_axis=None, slice_index=None):
    _require_supported_array(value)
    if type(coordinates) is not list or len(coordinates) != 2 or any(type(item) is not int for item in coordinates):
        raise ValueError("coordinates must be [y, x].")
    y, x = coordinates
    image, channels, selected_layout, selected_axis, selected_index = _select_image(
        value, layout, slice_axis, slice_index)
    if not (0 <= y < image.shape[0] and 0 <= x < image.shape[1]):
        raise ValueError("Pixel coordinates are out of range.")
    if channels == 1:
        raw = _json_scalar(image[y, x].item())
    else:
        raw = [_json_scalar(item) for item in image[y, x].tolist()]
    result = {
        "coordinates": [y, x],
        "dtype": str(value.dtype),
        "layout": selected_layout,
        "value": raw,
    }
    if selected_layout == "VOLUME":
        result.update({"sliceAxis": selected_axis, "sliceIndex": selected_index})
    return result


def _select_image(value, layout, slice_axis, slice_index):
    selected_layout = layout or guess_layout(value)[0]
    selected_axis = None
    selected_index = None
    if value.ndim == 2 and selected_layout in ("GRAY", "gray"):
        image = value
        channels = 1
        selected_layout = "GRAY"
    elif value.ndim == 3 and selected_layout == "HWC" and value.shape[2] in (1, 3, 4):
        image = value
        channels = int(value.shape[2])
    elif value.ndim == 3 and selected_layout == "CHW" and value.shape[0] in (1, 3, 4):
        image = value.transpose(1, 2, 0)
        channels = int(value.shape[0])
    elif value.ndim == 3 and selected_layout == "VOLUME":
        selected_axis = 0 if slice_axis is None else int(slice_axis)
        if selected_axis not in (0, 1, 2):
            raise ValueError("sliceAxis must be 0, 1, or 2.")
        selected_index = value.shape[selected_axis] // 2 if slice_index is None else int(slice_index)
        if not 0 <= selected_index < value.shape[selected_axis]:
            raise ValueError("sliceIndex is out of range.")
        image = value.take(selected_index, axis=selected_axis)
        channels = 1
    else:
        raise ValueError("The array shape and requested layout are not supported.")
    return image, channels, selected_layout, selected_axis, selected_index


def _render(image, channels, color_order, enabled_channels, normalization, percentile_low, percentile_high):
    if color_order not in ("RGB", "BGR"):
        raise ValueError("colorOrder must be RGB or BGR.")
    mode = str(normalization or "AUTO").upper()
    if mode not in ("AUTO", "NONE", "MINMAX", "PERCENTILE", "LABEL"):
        raise ValueError("normalization must be AUTO, NONE, MINMAX, PERCENTILE, or LABEL.")
    if not 0 <= float(percentile_low) < float(percentile_high) <= 100:
        raise ValueError("Percentile bounds must satisfy 0 <= low < high <= 100.")

    snapshot = image.copy(order="C")
    if channels == 1:
        snapshot = snapshot.reshape(snapshot.shape[0], snapshot.shape[1])
    if mode == "LABEL":
        if channels != 1 or snapshot.dtype.kind not in ("b", "u", "i"):
            raise ValueError("LABEL normalization requires a single-channel integer or bool view.")
        rendered, normalization_metadata = _label_map(snapshot)
        channels = 3
        pixel_format = "RGB24"
    else:
        rendered, normalization_metadata = _normalize(snapshot, mode, percentile_low, percentile_high)
        rendered = _apply_channels(rendered, channels, enabled_channels)
        if channels == 1:
            pixel_format = "Gray8"
        elif channels == 3:
            if color_order == "BGR":
                rendered = rendered[:, :, ::-1]
            pixel_format = "RGB24"
        else:
            if color_order == "RGB":
                rendered = rendered[:, :, [2, 1, 0, 3]]
            pixel_format = "BGRA32"
    output = rendered.copy(order="C")
    output_height, output_width = output.shape[:2]
    output_channels = 1 if output.ndim == 2 else output.shape[2]
    return {
        "width": int(output_width),
        "height": int(output_height),
        "stride": int(output_width * output_channels),
        "pixelFormat": pixel_format,
        "colorOrder": color_order,
        "normalization": normalization_metadata,
    }, output.tobytes(order="C")


def _normalize(snapshot, mode, percentile_low, percentile_high):
    numpy = _numpy_module()
    kind = snapshot.dtype.kind
    if kind == "u" and snapshot.dtype.itemsize == 1 and mode in ("AUTO", "NONE"):
        return snapshot, {
            "mode": "NONE",
            "displayMinimum": 0.0,
            "displayMaximum": 255.0,
            "nanCount": 0,
            "positiveInfinityCount": 0,
            "negativeInfinityCount": 0,
        }
    if kind == "b":
        return snapshot.astype(numpy.uint8) * 255, {
            "mode": "BOOL",
            "displayMinimum": 0.0,
            "displayMaximum": 1.0,
            "nanCount": 0,
            "positiveInfinityCount": 0,
            "negativeInfinityCount": 0,
        }
    if mode == "NONE":
        minimum, maximum = 0.0, 255.0
    else:
        selected_mode = "PERCENTILE" if mode == "AUTO" else mode
        finite_mask = numpy.isfinite(snapshot)
        finite = snapshot[finite_mask]
        if finite.size == 0:
            minimum, maximum = 0.0, 1.0
        elif selected_mode == "PERCENTILE":
            minimum, maximum = [float(item) for item in numpy.percentile(finite, [float(percentile_low), float(percentile_high)])]
        else:
            minimum, maximum = float(finite.min()), float(finite.max())
        mode = selected_mode
    source = snapshot.astype(numpy.float64, copy=False)
    if maximum <= minimum:
        rendered = numpy.zeros(snapshot.shape, dtype=numpy.uint8)
    else:
        scaled = (source - minimum) * (255.0 / (maximum - minimum))
        scaled = numpy.nan_to_num(scaled, nan=0.0, posinf=255.0, neginf=0.0)
        rendered = numpy.clip(scaled, 0.0, 255.0).astype(numpy.uint8)
    if kind == "f":
        nan_count = int(numpy.isnan(snapshot).sum())
        positive_inf_count = int(numpy.isposinf(snapshot).sum())
        negative_inf_count = int(numpy.isneginf(snapshot).sum())
    else:
        nan_count = positive_inf_count = negative_inf_count = 0
    return rendered, {
        "mode": mode,
        "displayMinimum": minimum,
        "displayMaximum": maximum,
        "percentileLow": float(percentile_low) if mode == "PERCENTILE" else None,
        "percentileHigh": float(percentile_high) if mode == "PERCENTILE" else None,
        "nanCount": nan_count,
        "positiveInfinityCount": positive_inf_count,
        "negativeInfinityCount": negative_inf_count,
    }


def _label_map(snapshot):
    numpy = _numpy_module()
    labels = snapshot.astype(numpy.int64, copy=False)
    red = ((labels * 37 + 17) % 256).astype(numpy.uint8)
    green = ((labels * 73 + 29) % 256).astype(numpy.uint8)
    blue = ((labels * 109 + 47) % 256).astype(numpy.uint8)
    rendered = numpy.stack((red, green, blue), axis=2)
    rendered[labels == 0] = 0
    return rendered, {
        "mode": "LABEL",
        "displayMinimum": None,
        "displayMaximum": None,
        "nanCount": 0,
        "positiveInfinityCount": 0,
        "negativeInfinityCount": 0,
    }


def _apply_channels(rendered, channels, enabled_channels):
    if enabled_channels is None:
        return rendered
    if type(enabled_channels) is not list or len(enabled_channels) != channels or any(type(item) is not bool for item in enabled_channels):
        raise ValueError("enabledChannels must contain one boolean per channel.")
    if all(enabled_channels):
        return rendered
    rendered = rendered.copy(order="C")
    if channels == 1:
        if not enabled_channels[0]:
            rendered[:, :] = 0
    else:
        for channel, enabled in enumerate(enabled_channels):
            if not enabled:
                rendered[:, :, channel] = 0
    return rendered


def _json_scalar(value):
    if type(value) is bool:
        return value
    if type(value) is int:
        return value
    if type(value) is float:
        if math.isnan(value):
            return {"kind": "NaN"}
        if math.isinf(value):
            return {"kind": "+Infinity" if value > 0 else "-Infinity"}
        return value
    return value


def _bounded_dimension(value, name):
    if type(value) is not int:
        try:
            value = int(value)
        except (TypeError, ValueError):
            raise ValueError(f"{name} must be an integer.") from None
    if not 1 <= value <= _MAX_IMAGE_DIMENSION:
        raise ValueError(f"{name} must be between 1 and {_MAX_IMAGE_DIMENSION}.")
    return value


def _non_negative_integer(value, name):
    if type(value) is not int or value < 0:
        raise ValueError(f"{name} must be a non-negative integer.")
    return value
