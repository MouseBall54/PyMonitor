# Phase 5 advanced array and image viewer

Phase 5 extends the exact NumPy `ndarray` adapter without importing NumPy into a
target that did not already load it.

## Rendering

- Dtypes: bool, unsigned and signed integers, float16/32/64
- Layouts: 2D gray, HWC, CHW, and axis-selectable 3D volume slices
- Normalization: automatic, none/clamp, min-max, percentile, and deterministic
  integer label colors
- Output: bounded Gray8, RGB24, or BGRA32 `WriteableBitmap` payloads
- Non-finite values: NaN and negative infinity render as 0; positive infinity
  renders as 255, while exact raw pixel queries return structured values

## Bounded detail requests

The initial preview is sampled to at most 1024 by 1024. A source tile request
copies at most 1024 by 1024 values from the selected view and reports its source
origin so cursor coordinates remain exact. Histogram requests return 2 to 512
bins and sample at most one million values, with separate NaN and infinity
counts.

The WPF Array/Image tab exposes normalization and percentile controls, manual
source tile coordinates, histogram bin/channel controls, nearest-neighbor zoom,
pixel grid, volume slice selection, and separate raw/displayed pixel values.
