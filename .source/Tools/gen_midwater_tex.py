"""Generate top-down silhouettes of creatures seen through the abyss (DepthRim midwater layer).

Every texture faces +x (head on the right), so the C# side only has to rotate the quad to the heading.
RGB is the creature's faint body shading; alpha is the shape with soft edges. Bioluminescent spots
are written as bright RGB with high alpha. Original procedural shapes, no reference art.
Usage: python -I gen_midwater_tex.py <out_dir>
"""
import sys
import os
import numpy as np
from PIL import Image, ImageFilter

SS = 2  # supersampling factor


def grid(w, h):
    y, x = np.mgrid[0:h * SS, 0:w * SS].astype(np.float64)
    return (x + 0.5) / (w * SS), (y + 0.5) / (h * SS)


def ellipse(x, y, cx, cy, rx, ry):
    return ((x - cx) / rx) ** 2 + ((y - cy) / ry) ** 2 <= 1.0


def finish(alpha, rgb, w, h, blur, path):
    a = Image.fromarray((np.clip(alpha, 0, 1) * 255).astype(np.uint8), "L")
    a = a.resize((w, h), Image.LANCZOS).filter(ImageFilter.GaussianBlur(blur))
    c = Image.fromarray((np.clip(rgb, 0, 1) * 255).astype(np.uint8), "RGB").resize((w, h), Image.LANCZOS)
    img = c.copy()
    img.putalpha(a)
    img.save(path)


def shade(alpha, base, belly, y, cy):
    """Lighter along the spine, darker at the flanks: a hint of volume."""
    t = np.clip(1.0 - np.abs(y - cy) * 6.0, 0, 1)
    rgb = np.zeros(alpha.shape + (3,))
    for i in range(3):
        rgb[..., i] = base[i] + (belly[i] - base[i]) * t
    return rgb


def whale(path):
    w, h = 1024, 384
    x, y = grid(w, h)
    cy = 0.5
    # body: a long tapered spindle, widest a third from the head
    u = x
    half = np.where(u > 0.62, 0.17 * np.sqrt(np.clip(1 - ((u - 0.62) / 0.36) ** 2, 0, 1)),
                    0.17 * np.clip((u - 0.10) / 0.52, 0, 1) ** 0.75)
    body = (np.abs(y - cy) <= half) & (u > 0.10) & (u < 0.98)
    # tail stock and flukes
    stock = (np.abs(y - cy) <= 0.025) & (u > 0.03) & (u <= 0.12)
    fluke_l = ellipse(x, y, 0.065, cy - 0.085, 0.04, 0.085) & (x < 0.10)
    fluke_r = ellipse(x, y, 0.065, cy + 0.085, 0.04, 0.085) & (x < 0.10)
    notch = ellipse(x, y, 0.02, cy, 0.035, 0.035)
    # long pectoral fins swept back towards the tail
    def fin(sign):
        dx, dy = -0.6, 0.8 * sign
        ox, oy = x - 0.57, y - (cy + sign * 0.19)
        px = ox * dx + oy * dy
        py = -ox * dy + oy * dx
        return ellipse(px, py, 0.0, 0.0, 0.085, 0.026)
    alpha = (body | stock | ((fluke_l | fluke_r) & ~notch) | fin(1) | fin(-1)).astype(np.float64)
    rgb = shade(alpha, (0.30, 0.38, 0.46), (0.52, 0.62, 0.70), y, cy)
    finish(alpha * 0.95, rgb, w, h, 2.0, path)


def giant_squid(path):
    w, h = 1024, 384
    x, y = grid(w, h)
    cy = 0.5
    # mantle leads (squid swim mantle-first): a long cone from the head at x=0.48 to the tip at 0.98
    mx = np.clip((x - 0.48) / 0.50, 0, 1)
    mantle = (np.abs(y - cy) <= 0.11 * (1 - mx ** 1.6)) & (x >= 0.48) & (x <= 0.98)
    fins = (np.abs(y - cy) <= 0.15 * (1 - np.abs(x - 0.89) / 0.08)) & (x > 0.81) & (x < 0.97)
    head = ellipse(x, y, 0.445, cy, 0.07, 0.09)
    eyes = ellipse(x, y, 0.45, cy - 0.075, 0.018, 0.022) | ellipse(x, y, 0.45, cy + 0.075, 0.018, 0.022)
    # eight arms and two long feeding tentacles trailing behind, each a slightly wavy strip
    arms = np.zeros_like(x, dtype=bool)
    rng = np.random.default_rng(7)
    for i in range(8):
        spread = (i - 3.5) / 3.5 * 0.08
        phase = rng.uniform(0, 6.28)
        length = 0.22 + rng.uniform(0, 0.06)
        t = np.clip((0.41 - x) / length, 0, 1)
        centre = cy + spread * (1 + t * 0.6) + 0.012 * np.sin(t * 9 + phase)
        width = 0.016 * (1 - t) + 0.003
        arms |= (np.abs(y - centre) <= width) & (x <= 0.43) & (x >= 0.41 - length)
    for sign in (-1, 1):
        t = np.clip((0.41 - x) / 0.39, 0, 1)
        centre = cy + sign * (0.02 + 0.05 * t) + 0.015 * np.sin(t * 7 + sign)
        club = ellipse(x, y, 0.035, cy + sign * 0.07, 0.03, 0.022)
        arms |= ((np.abs(y - centre) <= 0.006) & (x <= 0.41) & (x >= 0.02)) | club
    alpha = (mantle | fins | head | arms).astype(np.float64)
    rgb = shade(alpha, (0.34, 0.26, 0.30), (0.56, 0.42, 0.46), y, cy)
    # the eyes catch whatever light there is
    rgb[eyes] = (0.85, 0.95, 1.0)
    finish(alpha * 0.95, rgb, w, h, 1.6, path)


def fish_school(path):
    w, h = 512, 512
    x, y = grid(w, h)
    rng = np.random.default_rng(11)
    alpha = np.zeros_like(x)
    # a loose ball of small fish all heading +x, denser at the centre
    for _ in range(140):
        r = abs(rng.normal(0, 0.18))
        a = rng.uniform(0, 6.28)
        cx, cyy = 0.5 + np.cos(a) * r, 0.5 + np.sin(a) * r * 0.75
        if not (0.06 < cx < 0.94 and 0.06 < cyy < 0.94):
            continue
        s = rng.uniform(0.6, 1.0)
        body = ellipse(x, y, cx, cyy, 0.022 * s, 0.007 * s)
        tail = (x < cx - 0.016 * s) & (x > cx - 0.032 * s) & (np.abs(y - cyy) <= (cx - 0.016 * s - x) * 0.6)
        alpha = np.maximum(alpha, (body | tail).astype(np.float64))
    rgb = np.ones(x.shape + (3,)) * np.array((0.55, 0.66, 0.74))
    finish(alpha * 0.9, rgb, w, h, 0.8, path)


def jellyfish(path):
    w, h = 256, 256
    x, y = grid(w, h)
    cx, cy = 0.5, 0.5
    d = np.sqrt((x - cx) ** 2 + (y - cy) ** 2)
    ang = np.arctan2(y - cy, x - cx)
    bell = np.clip(1 - d / 0.22, 0, 1) ** 0.6
    # radial canals and a frilled rim
    canals = (np.abs(np.sin(ang * 4)) < 0.12) & (d < 0.2)
    rim = (np.abs(d - 0.21 - 0.012 * np.sin(ang * 16)) < 0.012)
    # tentacles drifting out behind (-x) with a gentle wave
    tent = np.zeros_like(x, dtype=bool)
    for i in range(7):
        off = (i - 3) * 0.045
        t = np.clip((cx - 0.15 - x) / 0.3, 0, 1)
        centre = cy + off * (1 + t) + 0.02 * np.sin(t * 8 + i)
        tent |= (np.abs(y - centre) <= 0.004) & (x < cx - 0.12) & (x > 0.04)
    alpha = np.maximum(bell * 0.55, np.maximum(rim * 0.9, tent * 0.5))
    alpha = np.maximum(alpha, canals * 0.85)
    rgb = np.ones(x.shape + (3,)) * np.array((0.45, 0.85, 0.95))
    glow = canals | rim
    rgb[glow] = (0.75, 1.0, 1.0)
    finish(alpha, rgb, w, h, 0.8, path)


def main():
    out = sys.argv[1]
    os.makedirs(out, exist_ok=True)
    whale(os.path.join(out, "Whale.png"))
    giant_squid(os.path.join(out, "GiantSquid.png"))
    fish_school(os.path.join(out, "FishSchool.png"))
    jellyfish(os.path.join(out, "Jellyfish.png"))


if __name__ == "__main__":
    main()
