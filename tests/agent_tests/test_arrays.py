import unittest

import numpy as np

from pyruntime_inspector_agent import arrays
from pyruntime_inspector_agent.handles import HandleStore
from pyruntime_inspector_agent.safe_objects import SafeObjectInspector


class ArrayTests(unittest.TestCase):
    def setUp(self):
        self.inspector = SafeObjectInspector(HandleStore())

    def test_describe_separates_object_and_buffer_addresses(self):
        image = np.zeros((6, 5, 3), dtype=np.uint8)
        description = arrays.describe(image, self.inspector.summarize)
        self.assertEqual([6, 5, 3], description["shape"])
        self.assertEqual("uint8", description["dtype"])
        self.assertEqual(90, description["nbytes"])
        self.assertEqual("HWC", description["layoutGuess"])
        self.assertNotEqual(description["objectAddressHex"], description["dataAddressHex"])

    def test_grayscale_preview_and_pixel_match_source(self):
        image = np.arange(20, dtype=np.uint8).reshape(4, 5)
        metadata, binary = arrays.preview(image, 5, 4, "GRAY")
        self.assertEqual("Gray8", metadata["pixelFormat"])
        self.assertEqual(image.tobytes(), binary)
        self.assertEqual(13, arrays.pixel(image, [2, 3], "GRAY")["value"])

    def test_hwc_and_chw_rgb_previews_match(self):
        hwc = np.zeros((2, 3, 3), dtype=np.uint8)
        hwc[1, 2] = [10, 20, 30]
        chw = hwc.transpose(2, 0, 1).copy()
        hwc_meta, hwc_binary = arrays.preview(hwc, 3, 2, "HWC")
        chw_meta, chw_binary = arrays.preview(chw, 3, 2, "CHW")
        self.assertEqual("RGB24", hwc_meta["pixelFormat"])
        self.assertEqual(hwc_binary, chw_binary)
        self.assertEqual([10, 20, 30], arrays.pixel(chw, [1, 2], "CHW")["value"])

    def test_uncertain_layout_requires_override(self):
        image = np.zeros((3, 3, 3), dtype=np.uint8)
        self.assertEqual(("uncertain", "low"), arrays.guess_layout(image))
        with self.assertRaises(ValueError):
            arrays.preview(image)

    def test_bgr_channel_selection_and_volume_slice(self):
        color = np.array([[[10, 20, 30]]], dtype=np.uint8)
        metadata, binary = arrays.preview(color, 1, 1, "HWC", "BGR", [True, False, True])
        self.assertEqual("BGR", metadata["colorOrder"])
        self.assertEqual(bytes([30, 0, 10]), binary)

        volume = np.arange(24, dtype=np.uint8).reshape(2, 3, 4)
        volume_metadata, volume_binary = arrays.preview(volume, 4, 3, "VOLUME", slice_axis=0, slice_index=1)
        self.assertEqual("Gray8", volume_metadata["pixelFormat"])
        self.assertEqual(volume[1].tobytes(), volume_binary)
        self.assertEqual(int(volume[1, 2, 3]), arrays.pixel(volume, [2, 3], "VOLUME", 0, 1)["value"])

    def test_uint16_float_bool_and_label_normalization(self):
        uint16_image = np.array([[0, 32768, 65535]], dtype=np.uint16)
        metadata, binary = arrays.preview(uint16_image, 3, 1, "GRAY", normalization="MINMAX")
        self.assertEqual(bytes([0, 127, 255]), binary)
        self.assertEqual("MINMAX", metadata["normalization"]["mode"])

        float_image = np.array([[np.nan, -np.inf, 0.0, 1.0, np.inf]], dtype=np.float32)
        metadata, binary = arrays.preview(float_image, 5, 1, "GRAY", normalization="MINMAX")
        self.assertEqual(bytes([0, 0, 0, 255, 255]), binary)
        self.assertEqual(1, metadata["normalization"]["nanCount"])
        self.assertEqual({"kind": "NaN"}, arrays.pixel(float_image, [0, 0], "GRAY")["value"])
        self.assertEqual({"kind": "+Infinity"}, arrays.pixel(float_image, [0, 4], "GRAY")["value"])

        bool_image = np.array([[False, True]], dtype=np.bool_)
        self.assertEqual(bytes([0, 255]), arrays.preview(bool_image, 2, 1, "GRAY")[1])

        labels = np.array([[0, 1], [2, 3]], dtype=np.int32)
        label_metadata, label_binary = arrays.preview(labels, 2, 2, "GRAY", normalization="LABEL")
        self.assertEqual("RGB24", label_metadata["pixelFormat"])
        self.assertEqual(bytes([0, 0, 0]), label_binary[:3])
        self.assertNotEqual(bytes([0, 0, 0]), label_binary[3:6])

    def test_source_tile_and_histogram_are_bounded_and_exact(self):
        image = np.arange(100, dtype=np.uint8).reshape(10, 10)
        metadata, binary = arrays.tile(image, 3, 4, 4, 3, "GRAY")
        self.assertEqual((3, 4), (metadata["originX"], metadata["originY"]))
        self.assertEqual((4, 3), (metadata["width"], metadata["height"]))
        self.assertEqual(image[4:7, 3:7].tobytes(), binary)

        histogram = arrays.histogram(image, bins=10, layout="GRAY")
        self.assertEqual(100, sum(histogram["counts"]))
        self.assertEqual(11, len(histogram["binEdges"]))
        self.assertEqual(0.0, histogram["minimum"])
        self.assertEqual(99.0, histogram["maximum"])

        large = np.broadcast_to(np.zeros(1, dtype=np.uint16), (4096, 4096, 4))
        metadata, binary = arrays.preview(large, 1024, 1024, "HWC", normalization="MINMAX")
        self.assertLessEqual(len(binary), 1024 * 1024 * 4)
        self.assertEqual(4, metadata["rowStep"])
