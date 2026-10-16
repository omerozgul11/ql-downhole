#!/usr/bin/env python3
"""Generates the Downhole Studio branding from the QuakeLogic mark.

The QuakeLogic "Q" mark (six white rings around six orange dots on charcoal) is redrawn here as
vector geometry measured from the official 200 x 200 logo, so every size is sharp:
  src/DownholeStudio.App/Assets/QuakeLogic.ico           application, window and file icon
  src/DownholeStudio.App/Assets/quakelogic-mark.png      in-app logo (256 px)
  packaging/windows/QuakeLogic.ico                        installer icon
  packaging/windows/wizard*.bmp, wizard-small*.bmp        installer side and header images
  docs/images/quakelogic-mark.png                         README logo
Requires Pillow. Run from anywhere: python3 scripts/generate-branding.py
"""
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent.parent
CHARCOAL = (65, 65, 66)
ORANGE = (241, 96, 34)
WHITE = (255, 255, 255)
SUPERSAMPLE = 8

# Geometry in the official logo's 200-unit coordinate system, centred on (100, 100).
RINGS = [(0, -62.2), (-59.4, -31.0), (59.4, -31.0), (-59.4, 31.0), (59.4, 31.0), (0, 62.2)]
RING_OUTER, RING_INNER = 22.4, 13.4
DOT_ORBIT, DOT_RADIUS = 21.4, 6.9
DOTS = [(-DOT_ORBIT / 2, -DOT_ORBIT * 0.866), (DOT_ORBIT / 2, -DOT_ORBIT * 0.866), (-DOT_ORBIT, 0), (DOT_ORBIT, 0),
        (-DOT_ORBIT / 2, DOT_ORBIT * 0.866), (DOT_ORBIT / 2, DOT_ORBIT * 0.866)]


def draw_mark(draw: ImageDraw.ImageDraw, cx: float, cy: float, size: float, background: bool) -> None:
    """Draws the mark centred at (cx, cy) with an overall width of `size` pixels."""
    s = size / 200
    if background:
        draw.rectangle([cx - 100 * s, cy - 100 * s, cx + 100 * s, cy + 100 * s], fill=CHARCOAL)
    for dx, dy in RINGS:
        x, y = cx + dx * s, cy + dy * s
        draw.ellipse([x - RING_OUTER * s, y - RING_OUTER * s, x + RING_OUTER * s, y + RING_OUTER * s], fill=WHITE)
        draw.ellipse([x - RING_INNER * s, y - RING_INNER * s, x + RING_INNER * s, y + RING_INNER * s], fill=CHARCOAL)
    for dx, dy in DOTS:
        x, y = cx + dx * s, cy + dy * s
        draw.ellipse([x - DOT_RADIUS * s, y - DOT_RADIUS * s, x + DOT_RADIUS * s, y + DOT_RADIUS * s], fill=ORANGE)


def mark(size: int) -> Image.Image:
    big = Image.new("RGBA", (size * SUPERSAMPLE, size * SUPERSAMPLE), (0, 0, 0, 0))
    draw_mark(ImageDraw.Draw(big), big.width / 2, big.height / 2, big.width, background=True)
    return big.resize((size, size), Image.LANCZOS)


def wizard(width: int, height: int) -> Image.Image:
    """Tall installer side image: the mark over the product name on charcoal."""
    W, H = width * SUPERSAMPLE, height * SUPERSAMPLE
    img = Image.new("RGB", (W, H), CHARCOAL)
    draw = ImageDraw.Draw(img)
    draw_mark(draw, W / 2, H * 0.30, W * 0.78, background=False)
    fonts = ROOT / "src" / "DownholeStudio.Visualization" / "Fonts"
    title = ImageFont.truetype(str(fonts / "Inter-SemiBold.ttf"), int(W * 0.125))
    brand = ImageFont.truetype(str(fonts / "Inter-Medium.ttf"), int(W * 0.085))
    y = H * 0.56
    for line in ("Downhole", "Studio"):
        draw.text((W / 2, y), line, font=title, fill=WHITE, anchor="mt")
        y += W * 0.16
    draw.rectangle([W * 0.38, y + W * 0.05, W * 0.62, y + W * 0.065], fill=ORANGE)
    draw.text((W / 2, y + W * 0.13), "QuakeLogic", font=brand, fill=ORANGE, anchor="mt")
    return img.resize((width, height), Image.LANCZOS)


def main() -> None:
    assets = ROOT / "src" / "DownholeStudio.App" / "Assets"
    windows = ROOT / "packaging" / "windows"
    images = ROOT / "docs" / "images"
    images.mkdir(parents=True, exist_ok=True)

    icon_sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    master = mark(256)
    for target in (assets / "QuakeLogic.ico", windows / "QuakeLogic.ico"):
        # Each size is rendered separately so small icons stay crisp.
        master.save(target, format="ICO", sizes=[(s, s) for s in icon_sizes], append_images=[mark(s) for s in icon_sizes[:-1]])
    master.save(assets / "quakelogic-mark.png")
    master.save(images / "quakelogic-mark.png")

    for name, (w, h) in {"wizard.bmp": (164, 314), "wizard-150.bmp": (246, 471), "wizard-200.bmp": (328, 628)}.items():
        wizard(w, h).save(windows / name)
    for name, s in {"wizard-small.bmp": 55, "wizard-small-150.bmp": 83, "wizard-small-200.bmp": 110}.items():
        mark(s).convert("RGB").save(windows / name)
    print("Branding written to", assets, windows, images, sep="\n  ")


if __name__ == "__main__":
    main()
