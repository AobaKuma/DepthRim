"""Generate seamless world-map textures for DepthRim's seven ocean biomes.

Vanilla world terrain shaders sample biome textures by world position (the mesh has no UV0),
so each texture only has to tile seamlessly. Periodic noise is built in the frequency domain.
Usage: python -I gen_ocean_biome_tex.py <out_dir>
"""
import sys
import os
import numpy as np
from PIL import Image

N = 512


def periodic_noise(rng, falloff, lo=1, hi=None):
    """Seamless fractal noise in [0,1]: random-phase spectrum with 1/f^falloff amplitude."""
    fx = np.fft.fftfreq(N) * N
    fy = np.fft.fftfreq(N) * N
    f = np.sqrt(fx[None, :] ** 2 + fy[:, None] ** 2)
    amp = np.zeros_like(f)
    mask = f >= lo
    if hi is not None:
        mask &= f <= hi
    amp[mask] = 1.0 / f[mask] ** falloff
    phase = rng.uniform(0, 2 * np.pi, (N, N))
    img = np.real(np.fft.ifft2(amp * np.exp(1j * phase)))
    img -= img.min()
    img /= img.max()
    return img


def ridged(n):
    return 1.0 - np.abs(n * 2.0 - 1.0)


def lerp_color(a, b, t):
    a = np.array(a, dtype=np.float64)
    b = np.array(b, dtype=np.float64)
    return a[None, None, :] + (b - a)[None, None, :] * t[..., None]


def blend(base, color, mask):
    c = np.array(color, dtype=np.float64)[None, None, :]
    return base * (1 - mask[..., None]) + c * mask[..., None]


def smooth(x, a, b):
    t = np.clip((x - a) / (b - a), 0, 1)
    return t * t * (3 - 2 * t)


def polar_ice_cap(rng):
    n = periodic_noise(rng, 1.6)
    cracks = smooth(ridged(periodic_noise(rng, 1.3, 2)), 0.93, 0.99)
    img = lerp_color((122, 150, 172), (196, 214, 226), n)
    img = blend(img, (70, 98, 126), cracks * 0.8)
    return img


def kelp_forest(rng):
    n = periodic_noise(rng, 1.8)
    kelp = smooth(periodic_noise(rng, 1.2, 3), 0.55, 0.72)
    img = lerp_color((44, 92, 96), (70, 118, 104), n)
    img = blend(img, (38, 78, 44), kelp * 0.85)
    return img


def shallow_reef(rng):
    n = periodic_noise(rng, 1.7)
    coral = smooth(periodic_noise(rng, 1.1, 4), 0.6, 0.75)
    sand = smooth(periodic_noise(rng, 2.0), 0.55, 0.8)
    img = lerp_color((46, 150, 160), (92, 190, 186), n)
    img = blend(img, (214, 206, 168), sand * 0.6)
    img = blend(img, (222, 140, 120), coral * 0.85)
    return img


def deep_reef(rng):
    n = periodic_noise(rng, 1.8)
    mounds = smooth(periodic_noise(rng, 1.3, 3), 0.58, 0.74)
    img = lerp_color((26, 44, 84), (44, 66, 108), n)
    img = blend(img, (104, 74, 108), mounds * 0.8)
    return img


def seabed_plain(rng):
    n = periodic_noise(rng, 2.0)
    fine = periodic_noise(rng, 0.9, 20)
    img = lerp_color((34, 50, 72), (58, 74, 94), n * 0.8 + fine * 0.2)
    return img


def hydrothermal_field(rng):
    n = periodic_noise(rng, 1.7)
    fissures = smooth(ridged(periodic_noise(rng, 1.4, 2)), 0.9, 0.985)
    vents = smooth(periodic_noise(rng, 1.0, 6), 0.78, 0.9)
    img = lerp_color((34, 34, 44), (62, 58, 66), n)
    img = blend(img, (196, 92, 38), fissures * 0.7)
    img = blend(img, (236, 160, 70), vents * 0.9)
    return img


def abyssal_trench(rng):
    n = periodic_noise(rng, 1.9)
    streaks = smooth(ridged(periodic_noise(rng, 1.5, 2)), 0.88, 0.98)
    img = lerp_color((6, 9, 20), (18, 26, 44), n)
    img = blend(img, (34, 46, 70), streaks * 0.5)
    return img


def mid_ocean_ridge(rng):
    # 年輕的黑色玄武岩，裂谷與熔岩紋
    n = periodic_noise(rng, 1.6)
    rifts = smooth(ridged(periodic_noise(rng, 1.4, 2)), 0.9, 0.98)
    img = lerp_color((30, 34, 46), (64, 70, 86), n)
    img = blend(img, (12, 14, 22), rifts * 0.8)
    return img


def landmass(rng):
    # 退路貼圖：正常情況下陸塊會被畫成正上方的地表生態區
    n = periodic_noise(rng, 1.8)
    img = lerp_color((96, 86, 70), (140, 128, 104), n)
    return img


BIOMES = {
    "PolarIceCap": polar_ice_cap,
    "KelpForest": kelp_forest,
    "ShallowReef": shallow_reef,
    "DeepReef": deep_reef,
    "SeabedPlain": seabed_plain,
    "HydrothermalField": hydrothermal_field,
    "AbyssalTrench": abyssal_trench,
    "Landmass": landmass,
    "MidOceanRidge": mid_ocean_ridge,
}


def main():
    out = sys.argv[1]
    os.makedirs(out, exist_ok=True)
    for i, (name, fn) in enumerate(BIOMES.items()):
        rng = np.random.default_rng(73415 + i)
        img = np.clip(fn(rng), 0, 255).astype(np.uint8)
        Image.fromarray(img, "RGB").save(os.path.join(out, name + ".png"))
        print("wrote", name)


if __name__ == "__main__":
    main()
