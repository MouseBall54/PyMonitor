#!/usr/bin/env python3
"""Build the Windows multi-resolution icon from the approved PyMonitor master."""

from __future__ import annotations

import argparse
import io
import struct
from pathlib import Path

from PIL import Image, ImageFilter


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_SOURCE = ROOT / "src" / "PyRuntimeInspector.App" / "Assets" / "app-icon.png"
DEFAULT_OUTPUT = ROOT / "src" / "PyRuntimeInspector.App" / "Assets" / "app-icon.ico"
ICON_SIZES = (16, 20, 24, 32, 40, 48, 64, 80, 96, 128, 256)
PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"


def normalized_master(source: Image.Image) -> Image.Image:
    rgba = source.convert("RGBA")
    if rgba.width != rgba.height or min(rgba.size) < 1024:
        raise ValueError("The icon master must be square and at least 1024 x 1024.")

    bounds = rgba.getchannel("A").getbbox()
    if bounds is None:
        raise ValueError("The icon master is fully transparent.")

    left, top, right, bottom = bounds
    content_size = max(right - left, bottom - top)
    optical_padding = max(2, round(content_size * 0.08))
    side = content_size + optical_padding * 2
    center_x = (left + right) / 2
    center_y = (top + bottom) / 2
    crop_left = round(center_x - side / 2)
    crop_top = round(center_y - side / 2)

    normalized = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    source_left = max(0, crop_left)
    source_top = max(0, crop_top)
    source_right = min(rgba.width, crop_left + side)
    source_bottom = min(rgba.height, crop_top + side)
    destination = (source_left - crop_left, source_top - crop_top)
    normalized.alpha_composite(
        rgba.crop((source_left, source_top, source_right, source_bottom)),
        destination,
    )
    return normalized


def render_frame(master: Image.Image, size: int) -> Image.Image:
    # Resize premultiplied RGBA to avoid dark fringes around transparent edges.
    frame = master.convert("RGBa").resize(
        (size, size),
        Image.Resampling.LANCZOS,
    ).convert("RGBA")

    if size <= 24:
        radius, percent = 0.35, 175
    elif size <= 48:
        radius, percent = 0.55, 145
    else:
        return frame

    alpha = frame.getchannel("A")
    sharpened = frame.convert("RGB").filter(
        ImageFilter.UnsharpMask(radius=radius, percent=percent, threshold=2)
    )
    return Image.merge("RGBA", (*sharpened.split(), alpha))


def encode_png(frame: Image.Image) -> bytes:
    payload = io.BytesIO()
    frame.save(payload, format="PNG", optimize=True, compress_level=9)
    data = payload.getvalue()
    if not data.startswith(PNG_SIGNATURE):
        raise ValueError("An icon frame was not encoded as PNG.")
    return data


def write_ico(output: Path, frames: list[tuple[int, bytes]]) -> None:
    directory_size = 6 + len(frames) * 16
    offset = directory_size
    entries: list[bytes] = []
    payloads: list[bytes] = []

    for size, payload in frames:
        dimension = 0 if size == 256 else size
        entries.append(
            struct.pack(
                "<BBBBHHII",
                dimension,
                dimension,
                0,
                0,
                1,
                32,
                len(payload),
                offset,
            )
        )
        payloads.append(payload)
        offset += len(payload)

    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_name(f".{output.name}.tmp")
    try:
        temporary.write_bytes(
            struct.pack("<HHH", 0, 1, len(frames))
            + b"".join(entries)
            + b"".join(payloads)
        )
        temporary.replace(output)
    finally:
        temporary.unlink(missing_ok=True)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, default=DEFAULT_SOURCE)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument(
        "--preview-dir",
        type=Path,
        help="Optionally write each rendered PNG frame for visual review.",
    )
    args = parser.parse_args()

    with Image.open(args.source) as source:
        master = normalized_master(source)
    rendered = [(size, render_frame(master, size)) for size in ICON_SIZES]

    if args.preview_dir is not None:
        args.preview_dir.mkdir(parents=True, exist_ok=True)
        for size, frame in rendered:
            frame.save(args.preview_dir / f"app-icon-{size}.png")

    write_ico(args.output, [(size, encode_png(frame)) for size, frame in rendered])

    with Image.open(args.output) as icon:
        actual_sizes = tuple(sorted(size[0] for size in icon.ico.sizes()))
    if actual_sizes != ICON_SIZES:
        raise ValueError(f"ICO frame validation failed: {actual_sizes!r}")

    print(f"Wrote {args.output} with {len(ICON_SIZES)} frames: {ICON_SIZES}")


if __name__ == "__main__":
    main()
