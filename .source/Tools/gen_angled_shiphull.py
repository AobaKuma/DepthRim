"""
產生抗壓水密牆（Textures/Thing/ShipHull.png）風格的斜角貼圖，給 DepthRim.SectionLayer_ShipHullCorners 用。

做法比照原版重力船斜角船體（SectionLayer_GravshipHull）：每張圖覆蓋 2×2 格，貼在缺口格旁邊，蓋過相鄰兩段船體的端點。
貼圖不是手繪，而是用 ShipHull 圖集的「偽 3D」量測重畫：
  - 牆的佔地（footprint）：格內 u 0.1–0.9、v 0.1–0.6（v 向上）
  - 頂面 = 佔地往上平移 H（0.3 格）；正面 = 佔地往上掃過的部分扣掉頂面（只有朝南的邊會有正面）
  - 外框黑線 4px、頂面邊緣一圈 5px 框（114）、中間深色板（49）與橫肋（127）、正面淺灰（231，帶雜訊）
斜接的兩段牆之間用兩個佔地方塊的凸包連起來，渲染整個聯集，再只輸出缺口格與兩段牆面向接縫的邊條
（那裡原本是牆的端蓋與外框），所以接上之後外框、頂面框、正面會連續。

用法：python gen_angled_shiphull.py [--preview <png>]
"""
import argparse
import os
import random

import numpy as np
from PIL import Image, ImageDraw

CELL = 80            # 輸出每格 80px（2×2 格 = 160px）
SS = 4               # 超取樣倍數
# 圖集每格 80px，但原版連接貼圖只取中間 60px（MaterialAtlasPool：內縮 1/32、取 3/16），
# 所以下列量測都換算成「遊戲裡的格」：圖集像素 p → (p - 10) / 60
H = 0.40             # 頂面相對佔地往上平移（＝正面高度）
FOOT = (0.0, 1.0, 0.0, 0.6)   # 佔地 u0, u1, v0, v1（格內）：牆填滿整格寬
OUTLINE = 2.5 / 60
FRAME = 5.0 / 60
RIB_PERIOD = 31.0 / 60
RIB_WIDTH = 7.0 / 60
STRIP = 0.5          # 相鄰牆面向接縫的邊條深度：半格，純斜接的中間格由前後兩塊各蓋一半，整格都換掉
FACE_STRIP = 0.5

BEVEL = 10.0 / 60    # 南北向牆兩側的斜面寬（圖集直牆 13–22px）
C_BEVEL = 160
C_OUT = 0
C_FRAME = 116
C_PANEL = 49
C_RIB = 124
C_FACE = 231
C_FACE_TOP = 180
C_FACE_BOT = 220

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT_DIR = os.path.join(ROOT, "Textures", "Thing", "ShipHullAngled")
ATLAS = os.path.join(ROOT, "Textures", "Thing", "ShipHull.png")

PAD = 1              # 畫布四周多留一格：區塊外的牆（往下延伸、斜接下一段）平移上來會進到區塊裡
N = (2 + 2 * PAD) * CELL * SS


def to_px(u, v):
    """區塊座標（u 向右、v 向上，0–2，可超出 ±PAD）→ 超取樣影像座標。"""
    return ((u + PAD) * CELL * SS, (2 + PAD - v) * CELL * SS)


def poly_mask(polys):
    img = Image.new("L", (N, N), 0)
    d = ImageDraw.Draw(img)
    for p in polys:
        d.polygon([to_px(u, v) for u, v in p], fill=255)
    return np.array(img) > 0


def rect(u0, u1, v0, v1):
    return [(u0, v0), (u0, v1), (u1, v1), (u1, v0)]


def cell_foot(cx, cz, ext_left=0.0, ext_down=0.0, ext_right=0.0, ext_up=0.0):
    u0, u1, v0, v1 = FOOT
    return rect(cx + u0 - ext_left, cx + u1 + ext_right, cz + v0 - ext_down, cz + v1 + ext_up)


def shift_up(mask, amount):
    px = int(round(amount * CELL * SS))
    out = np.zeros_like(mask)
    out[:-px] = mask[px:]
    return out


def dist_to(mask_outside):
    """到 mask_outside 為真的像素的近似距離（格），8 鄰 chamfer。"""
    inf = 1e9
    d = np.where(mask_outside, 0.0, inf)
    h, w = d.shape
    a, b = 1.0, 1.41421356
    # 兩趟掃描（numpy 逐列）
    for y in range(h):
        row = d[y]
        if y > 0:
            prev = d[y - 1]
            row = np.minimum(row, prev + a)
            row[1:] = np.minimum(row[1:], prev[:-1] + b)
            row[:-1] = np.minimum(row[:-1], prev[1:] + b)
        for x in range(1, w):
            if row[x - 1] + a < row[x]:
                row[x] = row[x - 1] + a
        d[y] = row
    for y in range(h - 1, -1, -1):
        row = d[y]
        if y < h - 1:
            nxt = d[y + 1]
            row = np.minimum(row, nxt + a)
            row[1:] = np.minimum(row[1:], nxt[:-1] + b)
            row[:-1] = np.minimum(row[:-1], nxt[1:] + b)
        for x in range(w - 2, -1, -1):
            if row[x + 1] + a < row[x]:
                row[x] = row[x + 1] + a
        d[y] = row
    return d / (CELL * SS)


def render(foot, cells):
    """把佔地聯集畫成 ShipHull 風格：回傳 (灰階, alpha) 超取樣陣列。"""
    top = shift_up(foot, H)
    swept = foot.copy()
    steps = int(round(H * CELL * SS))
    for i in range(1, steps + 1):
        swept |= shift_up(foot, i / (CELL * SS))
    face = swept & ~top
    d_out = dist_to(~swept)
    d_top = dist_to(~top)

    ys, xs = np.mgrid[0:N, 0:N]
    u = (xs + 0.5) / (CELL * SS) - PAD
    v = 2 + PAD - (ys + 0.5) / (CELL * SS)

    rng = random.Random(7)
    noise = np.array([[rng.randint(-5, 3) for _ in range(N // SS)] for _ in range(N // SS)])
    noise = np.kron(noise, np.ones((SS, SS)))

    g = np.zeros((N, N))
    # 頂面：外圈框、中間深色板，板上每 0.39 格一條橫肋（斜牆上肋垂直於牆的走向：u+v 等值線）
    g[top] = C_PANEL
    framed = top & (d_top < FRAME + OUTLINE)
    g[framed] = C_FRAME
    rib_period = RIB_PERIOD * 1.41421356
    # 南北向的邊（左右兩側是垂直邊）：外框內側先有一條淺色側斜面，比照圖集直牆
    bpx = int(round((OUTLINE + BEVEL) * CELL * SS))
    vpx = int(round(0.12 * CELL * SS))
    out_l = np.zeros_like(top)
    out_r = np.zeros_like(top)
    for k in range(1, bpx + 1):
        out_l[:, k:] |= ~top[:, :-k]
        out_r[:, :-k] |= ~top[:, k:]
    vert_ok = np.zeros_like(top)
    vert_ok[vpx:-vpx] = top[:-2 * vpx] & top[2 * vpx:]
    bevel = top & vert_ok & (out_l | out_r)
    framed &= ~bevel
    g[bevel] = C_BEVEL
    rib = top & ~framed & ~bevel & (((u + v) % rib_period) < RIB_WIDTH * 1.41421356)
    g[rib] = C_RIB
    # 正面
    g[face] = C_FACE + noise[face]
    k = int(1.5 * SS)
    top_down = np.zeros_like(top)
    top_down[k:] = top[:-k]
    face_top = face & top_down
    g[face_top] = C_FACE_TOP
    face_bot = face & (d_out < OUTLINE + 1.0 / 60)
    g[face_bot] = C_FACE_BOT
    # 外框
    outline = swept & (d_out < OUTLINE)
    g[outline] = C_OUT
    return g, swept


def downsample(g, a, mask):
    lo, hi = PAD * CELL * SS, (PAD + 2) * CELL * SS
    g, a, mask = g[lo:hi, lo:hi], a[lo:hi, lo:hi], mask[lo:hi, lo:hi]
    gi = Image.fromarray(np.clip(g * a, 0, 255).astype(np.uint8))
    ai = Image.fromarray((a * 255).astype(np.uint8))
    size = (2 * CELL, 2 * CELL)
    ga = np.array(gi.resize(size, Image.BOX)).astype(float)
    aa = np.array(ai.resize(size, Image.BOX)).astype(float)
    m = np.array(Image.fromarray((mask * 255).astype(np.uint8)).resize(size, Image.BOX)).astype(float) / 255
    alpha = np.where((aa / 255 > 0.5) & (m > 0.5), 255, 0)
    col = np.where(aa > 0, ga * 255 / np.maximum(aa, 1), 0)
    out = np.zeros((2 * CELL, 2 * CELL, 4), np.uint8)
    out[..., 0] = out[..., 1] = out[..., 2] = np.clip(col, 0, 255).astype(np.uint8)
    out[..., 3] = alpha
    return Image.fromarray(out, "RGBA")


CONNECTOR = [(0.0, 0.0), (1.0, 0.0), (2.0, 1.0), (2.0, 1.6), (1.0, 1.6), (0.0, 0.6)]


def build(kind, lower=""):
    """kind: 'Partial_NW'、'Partial_SE'、'Corner_NW'、'Corner_SE'（NE/SW 由左右鏡像得到）。
    NW：缺口在右下格，牆在左下（西）與右上（北），Corner 時左上也是牆。
    SE：缺口在左上格，牆在左下（南）與右上（東），Corner 時右下也是牆。
    lower：左下牆在區塊外往下還接著什麼（只有 NW 類有差：它的東側邊條一路畫到牆腳，
    下面接著牆時頂面會往下延伸、正面也不同）。"" = 沒有、"S" = 正南接直牆、"SW" = 西南斜接下一段。
    其他方向的延伸只影響邊條以外的外框（邊條留了邊距），或影響不到邊條（頂面只會往上平移）。"""
    big = 1.0
    shapes = [
        cell_foot(0, 0, ext_left=big, ext_down=big if lower == "S" else 0.0),   # 左下牆
        cell_foot(1, 1, ext_up=big),                                            # 右上牆
        CONNECTOR,                                                              # 兩塊佔地的凸包
    ]
    if lower == "SW":
        shapes.append(cell_foot(-1, -1))
        shapes.append([(u - 1, v - 1) for u, v in CONNECTOR])
    if kind == "Corner_NW":
        shapes.append(cell_foot(0, 1, ext_left=big, ext_up=big))
        shapes.append(rect(0.0, 1.0, 0.0, 1.6))
        shapes.append(rect(0.0, 2.0, 1.0, 1.6))
    if kind == "Corner_SE":
        shapes.append(cell_foot(1, 0, ext_right=big, ext_down=big))
        shapes.append(rect(0.0, 2.0, 0.0, 0.6))
        shapes.append(rect(1.0, 2.0, 0.0, 1.6))
    foot = poly_mask(shapes)
    cell_rects = [rect(-1, 1, -1, 1), rect(1, 3, 1, 3)]
    if kind == "Corner_NW":
        cell_rects.append(rect(-1, 1, 1, 3))
    if kind == "Corner_SE":
        cell_rects.append(rect(1, 3, -1, 1))
    g, a = render(foot, poly_mask(cell_rects))

    if kind.endswith("NW"):
        regions = [
            rect(1, 2, 0, 1),                    # 缺口格（右下）
            rect(1 - STRIP, 1, 0, 1),            # 左下牆的東側邊條
            rect(1, 1.92, 1, 1 + FACE_STRIP),    # 右上牆的南側邊條（右端留邊距：右上牆往東連不連都不碰）
        ]
        if kind.startswith("Corner"):
            regions.append(rect(1 - STRIP, 1, 1, 1 + FACE_STRIP))   # 左上牆的內角（原本有一小塊正面）
    else:
        regions = [
            rect(0, 1, 1, 2),                    # 缺口格（左上）
            rect(0.08, 1, 1 - STRIP, 1),         # 左下牆的北側邊條（左端留邊距）
            rect(1, 1 + STRIP, 1, 1.92),         # 右上牆的西側邊條（上端留邊距）
        ]
        if kind.startswith("Corner"):
            regions.append(rect(1, 1 + STRIP, 1 - STRIP, 1))        # 右下牆的內角
    mask = poly_mask(regions)
    return downsample(g, a.astype(float), mask.astype(float))


NAMES = {
    "Partial_NW": "ShipHullAngled_Partial_northwest",
    "Partial_SE": "ShipHullAngled_Partial_southeast",
    "Corner_NW": "ShipHullAngled_northwest",
    "Corner_SE": "ShipHullAngled_southeast",
}
MIRRORS = {
    "Partial_NW": "ShipHullAngled_Partial_northeast",
    "Partial_SE": "ShipHullAngled_Partial_southwest",
    "Corner_NW": "ShipHullAngled_northeast",
    "Corner_SE": "ShipHullAngled_southwest",
}


# NW 類（與鏡像的 NE 類）依左下牆（鏡像後是右下牆）往下接著什麼分三種：_Linked = 正南接直牆、_Diag = 斜接下一段
LOWER_VARIANTS = [("", ""), ("S", "_Linked"), ("SW", "_Diag")]


def generate():
    os.makedirs(OUT_DIR, exist_ok=True)
    result = {}
    for kind, name in NAMES.items():
        variants = LOWER_VARIANTS if kind.endswith("NW") else [("", "")]
        for lower, suffix in variants:
            img = build(kind, lower)
            img.save(os.path.join(OUT_DIR, name + suffix + ".png"))
            mir = img.transpose(Image.FLIP_LEFT_RIGHT)
            mir.save(os.path.join(OUT_DIR, MIRRORS[kind] + suffix + ".png"))
            result[name + suffix] = img
            result[MIRRORS[kind] + suffix] = mir
    return result


# ---------- 預覽：用圖集與斜角貼圖拼出一小塊船體，照 SectionLayer_ShipHullCorners 的規則放斜角 ----------

def atlas_tile(atlas, n, e, s, w):
    idx = (1 if n else 0) | (2 if e else 0) | (4 if s else 0) | (8 if w else 0)
    col, row = idx % 4, 3 - idx // 4
    return atlas.crop((col * 80 + 10, row * 80 + 10, col * 80 + 70, row * 80 + 70)).resize((CELL, CELL), Image.LANCZOS)


def preview(path, tex):
    layout = [
        "..................",
        ".########.........",
        ".#.......#........",
        ".#.......#........",
        ".#.......#........",
        ".#........#.......",
        ".#........#.......",
        ".#.........#......",
        ".##........#......",
        "..##......#.......",
        "...#.....#........",
        "...#######........",
        "..................",
    ]
    rows, cols = len(layout), len(layout[0])

    def hull(x, z):
        r = rows - 1 - z
        return 0 <= x < cols and 0 <= r < rows and layout[r][x] == "#"

    inside = set()
    # 內部：從 (4, 5) 洪水填充
    stack = [(5, 6)]
    while stack:
        x, z = stack.pop()
        if (x, z) in inside or hull(x, z) or not (0 <= x < cols and 0 <= z < rows):
            continue
        inside.add((x, z))
        stack += [(x + 1, z), (x - 1, z), (x, z + 1), (x, z - 1)]

    W, Hh = cols * CELL, rows * CELL
    canvas = Image.new("RGBA", (W, Hh), (14, 30, 48, 255))
    floor = Image.new("RGBA", (CELL, CELL), (58, 64, 70, 255))
    for (x, z) in inside:
        canvas.alpha_composite(floor, (x * CELL, (rows - 1 - z) * CELL))
    atlas = Image.open(ATLAS).convert("RGBA")
    tint = np.array([205, 210, 216, 255]) / 255.0

    def tinted(img):
        a = np.array(img).astype(float) * tint
        return Image.fromarray(a.astype(np.uint8), "RGBA")

    for z in range(rows):
        for x in range(cols):
            if hull(x, z):
                t = atlas_tile(atlas, hull(x, z + 1), hull(x + 1, z), hull(x, z - 1), hull(x - 1, z))
                canvas.alpha_composite(tinted(t), (x * CELL, (rows - 1 - z) * CELL))
    D = [(0, 1), (1, 0), (0, -1), (-1, 0), (-1, 1), (1, 1), (1, -1), (-1, -1)]
    for z in range(rows):
        for x in range(cols):
            if hull(x, z):
                continue
            c = [hull(x + dx, z + dz) for dx, dz in D]
            kind, off = None, None
            if c[0] and c[3] and not c[2] and not c[1]:
                kind, off = ("ShipHullAngled_northwest" if c[4] else "ShipHullAngled_Partial_northwest"), (-1, 0)
                if hull(x - 1, z - 1):
                    kind += "_Linked"
                elif hull(x - 2, z - 1):
                    kind += "_Diag"
            elif c[0] and c[1] and not c[2] and not c[3]:
                kind, off = ("ShipHullAngled_northeast" if c[5] else "ShipHullAngled_Partial_northeast"), (0, 0)
                if hull(x + 1, z - 1):
                    kind += "_Linked"
                elif hull(x + 2, z - 1):
                    kind += "_Diag"
            elif c[2] and c[1] and not c[0] and not c[3]:
                kind, off = ("ShipHullAngled_southeast" if c[6] else "ShipHullAngled_Partial_southeast"), (0, -1)
            elif c[2] and c[3] and not c[0] and not c[1]:
                kind, off = ("ShipHullAngled_southwest" if c[7] else "ShipHullAngled_Partial_southwest"), (-1, -1)
            if kind is None:
                continue
            if "Partial" not in kind and (x, z) in inside:
                continue  # 室內（船體平台上）的直角轉角不補
            bx, bz = x + off[0], z + off[1]
            canvas.alpha_composite(tinted(tex[kind]), (bx * CELL, (rows - 1 - (bz + 1)) * CELL))
    canvas.save(path)


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--preview")
    args = ap.parse_args()
    tex = generate()
    if args.preview:
        preview(args.preview, tex)
