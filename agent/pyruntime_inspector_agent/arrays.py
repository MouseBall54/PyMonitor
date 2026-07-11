import sys
import types


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


def _require_uint8_array(value):
    if not is_exact_ndarray(value):
        raise ValueError("The selected object is not an exact loaded NumPy ndarray.")
    if value.dtype.str not in ("|u1", "<u1", ">u1"):
        raise ValueError("Only uint8 array previews are supported in Phase 0.")


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
        "supportedPreviewModes": ["GRAY", "HWC", "CHW", "VOLUME"] if value.dtype.kind == "u" and value.itemsize == 1 else [],
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
):
    _require_uint8_array(value)
    max_width = int(max_width)
    max_height = int(max_height)
    if not 1 <= max_width <= 1024 or not 1 <= max_height <= 1024:
        raise ValueError("Preview dimensions must be between 1 and 1024.")
    selected_layout = layout or guess_layout(value)[0]
    if color_order not in ("RGB", "BGR"):
        raise ValueError("colorOrder must be RGB or BGR.")
    if value.ndim == 2 and selected_layout in ("GRAY", "gray"):
        image = value
        channels = 1
    elif value.ndim == 3 and selected_layout == "HWC" and value.shape[2] in (1, 3, 4):
        image = value
        channels = value.shape[2]
    elif value.ndim == 3 and selected_layout == "CHW" and value.shape[0] in (1, 3, 4):
        image = value.transpose(1, 2, 0)
        channels = value.shape[0]
    elif value.ndim == 3 and selected_layout == "VOLUME":
        axis = 0 if slice_axis is None else int(slice_axis)
        if axis not in (0, 1, 2):
            raise ValueError("sliceAxis must be 0, 1, or 2.")
        index = value.shape[axis] // 2 if slice_index is None else int(slice_index)
        if not 0 <= index < value.shape[axis]:
            raise ValueError("sliceIndex is out of range.")
        image = value.take(index, axis=axis)
        channels = 1
    else:
        raise ValueError("The array shape and requested layout are not supported.")
    height, width = image.shape[:2]
    row_step = max(1, (height + max_height - 1) // max_height)
    column_step = max(1, (width + max_width - 1) // max_width)
    sampled = image[::row_step, ::column_step]
    if enabled_channels is not None:
        if type(enabled_channels) is not list or len(enabled_channels) != channels or any(type(item) is not bool for item in enabled_channels):
            raise ValueError("enabledChannels must contain one boolean per channel.")
        if not all(enabled_channels):
            sampled = sampled.copy(order="C")
            if channels == 1:
                if not enabled_channels[0]:
                    sampled[:, :] = 0
            else:
                for channel, enabled in enumerate(enabled_channels):
                    if not enabled:
                        sampled[:, :, channel] = 0
    if channels == 1:
        sampled = sampled.reshape(sampled.shape[0], sampled.shape[1])
        pixel_format = "Gray8"
    elif channels == 3:
        if color_order == "BGR":
            sampled = sampled[:, :, ::-1]
        pixel_format = "RGB24"
    else:
        if color_order == "RGB":
            sampled = sampled[:, :, [2, 1, 0, 3]]
        pixel_format = "BGRA32"
    snapshot = sampled.copy(order="C")
    binary = snapshot.tobytes(order="C")
    output_height, output_width = snapshot.shape[:2]
    stride = output_width * channels
    selected_slice_axis = None
    selected_slice_index = None
    if selected_layout == "VOLUME":
        selected_slice_axis = 0 if slice_axis is None else int(slice_axis)
        selected_slice_index = value.shape[selected_slice_axis] // 2 if slice_index is None else int(slice_index)
    return {
        "width": int(output_width),
        "height": int(output_height),
        "stride": int(stride),
        "pixelFormat": pixel_format,
        "layout": selected_layout,
        "colorOrder": color_order,
        "sliceAxis": selected_slice_axis,
        "sliceIndex": selected_slice_index,
        "rowStep": row_step,
        "columnStep": column_step,
        "normalization": None,
    }, binary


def pixel(value, coordinates, layout=None, slice_axis=None, slice_index=None):
    _require_uint8_array(value)
    if type(coordinates) is not list or len(coordinates) != 2 or any(type(item) is not int for item in coordinates):
        raise ValueError("coordinates must be [y, x].")
    y, x = coordinates
    selected_layout = layout or guess_layout(value)[0]
    if value.ndim == 2:
        if not (0 <= y < value.shape[0] and 0 <= x < value.shape[1]):
            raise ValueError("Pixel coordinates are out of range.")
        raw = int(value[y, x])
    elif selected_layout == "HWC" and value.ndim == 3:
        if not (0 <= y < value.shape[0] and 0 <= x < value.shape[1]):
            raise ValueError("Pixel coordinates are out of range.")
        raw = [int(item) for item in value[y, x].tolist()]
    elif selected_layout == "CHW" and value.ndim == 3:
        if not (0 <= y < value.shape[1] and 0 <= x < value.shape[2]):
            raise ValueError("Pixel coordinates are out of range.")
        raw = [int(item) for item in value[:, y, x].tolist()]
    elif selected_layout == "VOLUME" and value.ndim == 3:
        axis = 0 if slice_axis is None else int(slice_axis)
        if axis not in (0, 1, 2):
            raise ValueError("sliceAxis must be 0, 1, or 2.")
        index = value.shape[axis] // 2 if slice_index is None else int(slice_index)
        plane = value.take(index, axis=axis)
        if not (0 <= y < plane.shape[0] and 0 <= x < plane.shape[1]):
            raise ValueError("Pixel coordinates are out of range.")
        raw = int(plane[y, x])
    else:
        raise ValueError("The requested layout is not supported.")
    result = {"coordinates": [y, x], "dtype": str(value.dtype), "layout": selected_layout, "value": raw}
    if selected_layout == "VOLUME":
        result.update({"sliceAxis": axis, "sliceIndex": index})
    return result
