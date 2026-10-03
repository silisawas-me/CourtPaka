"""Draws the installable app's icons (PRD 8: installable as a PWA).

A court seen from above in white on the brand green, drawn rather than lettered: the app ships
no fonts for a raster, and a court reads at 72 px where a word does not. The court sits inside
the middle 60% so the icon survives a platform's round or squircle mask ("maskable").

    python scripts/gen_app_icons.py
"""

import pathlib

from PIL import Image, ImageDraw

GREEN = (45, 118, 67)  # #2d7643, the badPaka green (--mat-sys-primary)
WHITE = (255, 255, 255)
SIZES = [72, 96, 128, 144, 152, 192, 384, 512]
OUT = pathlib.Path(__file__).resolve().parents[1] / "venue" / "web" / "public" / "icons"


def court(size: int) -> Image.Image:
    # Drawn large and scaled down, so thin lines stay smooth at every size.
    scale = 4
    big = size * scale
    image = Image.new("RGB", (big, big), GREEN)
    draw = ImageDraw.Draw(image)

    # A badminton court is 13.4 m x 6.1 m; stood upright, inside the maskable safe zone.
    height = big * 0.60
    width = height * 6.1 / 13.4
    left = (big - width) / 2
    top = (big - height) / 2
    right = left + width
    bottom = top + height
    line = max(2, round(big * 0.018))

    draw.rectangle([left, top, right, bottom], outline=WHITE, width=line)
    # The net across the middle, heavier than the lines.
    middle = (top + bottom) / 2
    draw.line([left - line * 2, middle, right + line * 2, middle], fill=WHITE, width=line * 2)
    # Short service lines, 1.98 m either side of the net.
    service = height * 1.98 / 13.4
    for y in (middle - service, middle + service):
        draw.line([left, y, right, y], fill=WHITE, width=line)
    # The centre line between the service lines and the back.
    cx = (left + right) / 2
    draw.line([cx, top, cx, middle - service], fill=WHITE, width=line)
    draw.line([cx, middle + service, cx, bottom], fill=WHITE, width=line)

    return image.resize((size, size), Image.LANCZOS)


OUT.mkdir(parents=True, exist_ok=True)
for size in SIZES:
    court(size).save(OUT / f"icon-{size}x{size}.png", optimize=True)
    print(f"icon-{size}x{size}.png")

# The browser tab's icon, from the same drawing, so the tab and the home screen agree.
court(48).save(OUT.parent / "favicon.ico", sizes=[(16, 16), (32, 32), (48, 48)])
print("favicon.ico")
