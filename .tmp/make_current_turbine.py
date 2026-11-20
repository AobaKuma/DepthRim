"""Generate the 3x3 ocean current turbine textures (4 rotations).

Flat-shaded, dark-outlined style matching the other DepthRim building art
(128 px per cell, 3x3 -> 384x384). Run from anywhere:
    python .tmp/make_current_turbine.py
Writes Textures/Thing/DR_CurrentTurbine/CurrentTurbine_{north,east,south,west}.png
"""
import math
import os

from PIL import Image, ImageDraw

SIZE = 384
OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                   "Textures", "Thing", "DR_CurrentTurbine")

OUTLINE = (20, 23, 27, 255)
STEEL_DARK = (57, 65, 74, 255)
STEEL = (78, 88, 99, 255)
STEEL_LIGHT = (110, 122, 134, 255)
STEEL_HI = (146, 158, 169, 255)
HOLE = (14, 17, 21, 255)
BLADE = (126, 138, 150, 255)
BLADE_DARK = (86, 96, 106, 255)
ACCENT = (86, 150, 166, 255)


def rounded(draw, box, radius, fill, outline=OUTLINE, width=4):
    draw.rounded_rectangle(box, radius=radius, fill=fill, outline=outline, width=width)


def ring(draw, cx, cy, rx, ry, width, fill, outline=None, outline_width=0):
    # draw an annulus by stroking an ellipse
    for i in range(width):
        draw.ellipse([cx - rx + i, cy - ry + i, cx + rx - i, cy + ry - i],
                     outline=fill)
    if outline and outline_width:
        for i in range(outline_width):
            draw.ellipse([cx - rx + i, cy - ry + i, cx + rx - i, cy + ry - i],
                         outline=outline)


def blade(draw, cx, cy, angle, inner, outer, half_inner, half_outer, fill):
    a = math.radians(angle)
    nx, ny = math.cos(a), math.sin(a)
    px, py = -ny, nx
    pts = [
        (cx + nx * inner + px * half_inner, cy + ny * inner + py * half_inner),
        (cx + nx * outer + px * half_outer, cy + ny * outer + py * half_outer),
        (cx + nx * outer - px * half_outer, cy + ny * outer - py * half_outer),
        (cx + nx * inner - px * half_inner, cy + ny * inner - py * half_inner),
    ]
    draw.polygon(pts, fill=fill)


def make():
    img = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    # base sled on the sea floor
    rounded(d, [38, 300, 346, 360], 10, STEEL_DARK)
    d.rectangle([62, 318, 322, 330], fill=(40, 46, 53, 255))
    for x in (70, 300):
        d.rectangle([x, 306, x + 14, 314], fill=STEEL_LIGHT)
    # two mounting posts up to the duct
    for x in (64, 276):
        rounded(d, [x, 168, x + 44, 318], 6, STEEL)
        d.rectangle([x + 8, 182, x + 12, 300], fill=STEEL_LIGHT)
        d.rectangle([x + 32, 182, x + 36, 300], fill=STEEL_DARK)

    # duct ring, seen face on
    cx, cy = 192, 172
    d.ellipse([cx - 138, cy - 132, cx + 138, cy + 132], fill=STEEL_DARK, outline=OUTLINE, width=6)
    d.ellipse([cx - 126, cy - 120, cx + 126, cy + 120], fill=STEEL, outline=OUTLINE, width=4)
    d.ellipse([cx - 116, cy - 110, cx + 116, cy + 110], fill=STEEL_DARK)
    # inner lip highlight (top left)
    d.arc([cx - 114, cy - 108, cx + 114, cy + 108], 155, 295, fill=STEEL_HI, width=6)
    d.arc([cx - 114, cy - 108, cx + 114, cy + 108], 155, 295, fill=STEEL_LIGHT, width=2)
    d.ellipse([cx - 104, cy - 98, cx + 104, cy + 98], fill=HOLE, outline=OUTLINE, width=4)

    # blades inside the duct
    for k in range(5):
        angle = -90 + k * 72 + 18
        blade(d, cx, cy, angle, 22, 100, 12, 26, BLADE)
        blade(d, cx, cy, angle + 6, 24, 96, 6, 16, BLADE_DARK)
    # hub
    d.ellipse([cx - 30, cy - 30, cx + 30, cy + 30], fill=STEEL_LIGHT, outline=OUTLINE, width=4)
    d.ellipse([cx - 14, cy - 14, cx + 14, cy + 14], fill=STEEL_DARK, outline=OUTLINE, width=3)
    d.ellipse([cx - 6, cy - 6, cx + 6, cy + 6], fill=STEEL_HI)

    # status light on the frame
    d.rounded_rectangle([cx - 26, 336 - 14, cx + 26, 336], radius=6, fill=ACCENT, outline=OUTLINE, width=3)

    return img


def main():
    img = make()
    os.makedirs(OUT, exist_ok=True)
    for rot in ("north", "east", "south", "west"):
        path = os.path.join(OUT, f"CurrentTurbine_{rot}.png")
        img.save(path)
        print("wrote", path, img.size)


if __name__ == "__main__":
    main()
