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
