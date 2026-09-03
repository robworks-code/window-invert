"""Renders assets/WindowInvert.ico.

The icon is a window silhouette split along its diagonal: the upper-left part is
a light-themed window (light body, dark title bar), the lower-right part is the
same window inverted. Every size in the .ico is drawn independently at that
size, supersampled and downscaled, so the 16 px tray icon is a deliberate
drawing and not a blurred copy of the 256 px one.

Run from the repository root:

    python assets/make_icon.py

Requires Pillow. Also writes assets/WindowInvert-256.png for use anywhere an
.ico is not accepted.
"""

from __future__ import annotations

from pathlib import Path

from PIL import Image, ImageChops, ImageDraw

SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]

LIGHT = (246, 246, 246, 255)
DARK = (28, 28, 30, 255)
LIGHT_BAR = (214, 214, 214, 255)
DARK_BAR = (66, 66, 70, 255)
OUTLINE = (128, 128, 128, 255)


def render(size: int) -> Image.Image:
    # Heavier supersampling for the small sizes, where a single edge pixel is a
    # large fraction of the drawing.
    scale = 32 if size <= 24 else 16 if size <= 48 else 8
    s = size * scale

    # Geometry as fractions of the canvas, so every size shares one drawing.
    inset = round(s * 0.06)
    radius = round(s * 0.10)
    outline = max(round(s * 0.03), scale)  # never thinner than 1 device pixel
    bar_h = round(s * 0.22)

    box = (inset, inset, s - inset - 1, s - inset - 1)
    left, top, right, bottom = box

    light = _window(s, box, radius, bar_h, LIGHT, LIGHT_BAR)
    _content_lines(ImageDraw.Draw(light), size, s, left, top + bar_h, right, bottom, DARK)

    # Inverted window: the same drawing with every tone flipped.
    dark = _window(s, box, radius, bar_h, DARK, DARK_BAR)
    _content_lines(ImageDraw.Draw(dark), size, s, left, top + bar_h, right, bottom, LIGHT)

    # Split along the diagonal from bottom-left to top-right. The inverted half
    # is everything below and right of that line.
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).polygon([(left, bottom), (right, top), (right, bottom)], fill=255)
    img = Image.composite(dark, light, mask)

    # A mid-grey outline keeps the shape visible on both a white and a black
    # taskbar, where one of the two halves would otherwise vanish.
    d = ImageDraw.Draw(img)
    d.rounded_rectangle(box, radius=radius, outline=OUTLINE, width=outline)

    return img.resize((size, size), Image.Resampling.LANCZOS)


def _window(s: int, box, radius: int, bar_h: int, body, bar) -> Image.Image:
    """A rounded window: a title-bar band across the top, body below."""
    left, top, right, bottom = box
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle(box, radius=radius, fill=body)
    band = Image.new("RGBA", (s, s), bar)
    band_mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(band_mask).rectangle((left, top, right, top + bar_h), fill=255)
    shape = Image.new("L", (s, s), 0)
    ImageDraw.Draw(shape).rounded_rectangle(box, radius=radius, fill=255)
    return Image.composite(band, img, ImageChops.multiply(band_mask, shape))


def _content_lines(d: ImageDraw.ImageDraw, size: int, s: int,
                   left: int, top: int, right: int, bottom: int, color) -> None:
    """Two short 'text' lines inside the body. Omitted at tray sizes, where they
    would only be noise."""
    if size < 32:
        return
    h = round(s * 0.055)
    gap = round(s * 0.11)
    x0 = left + round(s * 0.14)
    y = top + round(s * 0.18)
    for width_frac in (0.40, 0.28):
        d.rounded_rectangle((x0, y, x0 + round(s * width_frac), y + h),
                            radius=h // 2, fill=color)
        y += gap


def main() -> None:
    here = Path(__file__).resolve().parent
    frames = [render(n) for n in SIZES]
    largest = frames[-1]
    largest.save(here / "WindowInvert.ico", format="ICO",
                 sizes=[(n, n) for n in SIZES], append_images=frames[:-1])
    largest.save(here / "WindowInvert-256.png", format="PNG")
    print("wrote", here / "WindowInvert.ico", "sizes", SIZES)


if __name__ == "__main__":
    main()
