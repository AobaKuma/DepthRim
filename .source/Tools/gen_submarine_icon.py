"""World-map icon for the DepthRim submarine: a white top-down hull pointing north (+y up),
tinted by the world object's expandingIconColor. Usage: python -I gen_submarine_icon.py <out_png>"""
import sys
import numpy as np
from PIL import Image, ImageFilter

N = 128
SS = 4
y, x = np.mgrid[0:N * SS, 0:N * SS].astype(np.float64)
x = (x + 0.5) / (N * SS) - 0.5
y = 0.5 - (y + 0.5) / (N * SS)  # +y = north
# hull: a long rounded spindle, bow at the top
t = (y + 0.42) / 0.84  # 0 at stern, 1 at bow
half = np.where(t > 0.75, 0.13 * np.sqrt(np.clip(1 - ((t - 0.75) / 0.25) ** 2, 0, 1)),
                0.13 * np.clip(t / 0.25, 0, 1) ** 0.5)
hull = (np.abs(x) <= half) & (t >= 0) & (t <= 1)
# sail (conning tower) and stern planes / rudder
sail = ((x / 0.055) ** 2 + ((y - 0.10) / 0.12) ** 2) <= 1
planes = (np.abs(y + 0.34) <= 0.025) & (np.abs(x) <= 0.2)
rudder = (np.abs(x) <= 0.02) & (y < -0.38) & (y > -0.47)
alpha = (hull | planes | rudder).astype(np.float64)
# the sail is drawn slightly darker so it reads on top of the hull
shade = np.where(sail & hull, 0.78, 1.0)
img = np.zeros((N * SS, N * SS, 4))
img[..., 0] = shade
img[..., 1] = shade
img[..., 2] = shade
img[..., 3] = alpha
im = Image.fromarray((img * 255).astype(np.uint8), "RGBA").resize((N, N), Image.LANCZOS)
im.save(sys.argv[1])
