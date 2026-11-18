# 用法：python gen_implosion_textures.py <模組>/Textures/DepthRim/Implosion
# 水中內爆貼圖 v2：比較像真的水下氣泡，而不是發光的圓圈。
# - 邊緣外側一圈細的暗邊（折射造成的暗輪廓），內側是銀白的反光（全反射），往中心很快淡掉
# - 中心幾乎透明，只有一點點霧
# - 左上一道月牙形高光、右下一點小反光
# - 邊緣用低頻角度雜訊擾動，不是完美的圓；反光帶上有細的斑駁（爆炸氣體不均勻）
# 顏色存在 RGB 裡（暗邊是暗的），材質 _Color 再整體染色。
import math, random, sys
from PIL import Image

out = sys.argv[1]


def g(x, mu, s):
    return math.exp(-((x - mu) / s) ** 2)


class Noise:
    """可平鋪的 2D value noise（雙線性內插）。"""
    def __init__(self, seed, n):
        rnd = random.Random(seed)
        self.n = n
        self.v = [[rnd.random() for _ in range(n)] for _ in range(n)]

    def at(self, x, y):
        n = self.n
        x *= n; y *= n
        x0 = int(math.floor(x)); y0 = int(math.floor(y))
        fx = x - x0; fy = y - y0
        fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy)
        def v(i, j):
            return self.v[j % n][i % n]
        a = v(x0, y0) * (1 - fx) + v(x0 + 1, y0) * fx
        b = v(x0, y0 + 1) * (1 - fx) + v(x0 + 1, y0 + 1) * fx
        return a * (1 - fy) + b * fy


def save(size, fn, name):
    img = Image.new("RGBA", (size, size))
    px = img.load()
    for y in range(size):
        for x in range(size):
            u = (x + 0.5) / size * 2 - 1
            v = (y + 0.5) / size * 2 - 1
            r, gg, b, a = fn(u, v)
            c = lambda t: int(round(255 * max(0.0, min(1.0, t))))
            px[x, y] = (c(r), c(gg), c(b), c(a))
    img.save(f"{out}/{name}.png")


def bubble_fn(seed, edge_wobble, mottle, rim_w, size_hint):
    rnd = random.Random(seed)
    harmonics = [(k, rnd.uniform(0, 2 * math.pi), edge_wobble / k) for k in (2, 3, 5, 7)]
    noise = Noise(seed * 7 + 1, 10)
    hl_angle = math.radians(225)   # 影像 y 向下：左上

    def fn(u, v):
        r = math.hypot(u, v)
        ang = math.atan2(v, u)
        edge = 0.9 + sum(a * math.sin(k * ang + p) for k, p, a in harmonics)
        rn = r / edge                     # 以擾動後的邊為 1
        if rn > 1.12:
            return (0, 0, 0, 0)
        # 外側暗輪廓（稍微跨出邊外）
        dark = 0.55 * g(rn, 1.0, 0.045)
        # 內側銀白反光：靠邊最強、往內快速淡掉；帶斑駁
        m = 1.0 - mottle + mottle * noise.at((u + 1) / 2, (v + 1) / 2)
        rim = 0.55 * math.exp(-((1.0 - rn) / rim_w)) * m if rn <= 1.0 else 0.0
        # 中心淡霧
        fog = 0.05 * (1 - rn) if rn <= 1.0 else 0.0
        # 月牙高光：左上、貼著邊、沿角度展開
        da = math.atan2(math.sin(ang - hl_angle), math.cos(ang - hl_angle))
        crescent = 0.75 * g(rn, 0.8, 0.07) * g(da, 0, 0.5) if rn <= 1.0 else 0.0
        # 右下小反光
        spot = 0.35 * g(math.hypot(u - 0.42 * edge, v - 0.42 * edge), 0, 0.07 * (256 / size_hint) ** 0.3)
        a = dark + rim + fog + crescent + spot
        if a <= 0.001:
            return (0, 0, 0, 0)
        # 亮度：暗輪廓是暗的，其他是銀白
        bright = (rim + fog + crescent + spot) / a
        lum = 0.08 + 0.92 * bright
        return (lum, lum, lum, min(1.0, a))
    return fn


def ring_fn(u, v):
    # 壓力波：外側細亮線＋緊貼內側的暗線（折射的明暗對），都很淡
    r = math.hypot(u, v)
    if r > 1.0:
        return (0, 0, 0, 0)
    light = 0.5 * g(r, 0.94, 0.022)
    dark = 0.35 * g(r, 0.89, 0.03)
    a = light + dark
    if a <= 0.001:
        return (0, 0, 0, 0)
    lum = light / a
    fade = min(1.0, (1.0 - r) / 0.02)
    return (lum, lum, lum, a * fade)


def fbm(noises, x, y):
    """多層 value noise 疊加（0–1）。"""
    total = 0.0
    amp = 0.5
    norm = 0.0
    for n in noises:
        total += amp * n.at(x, y)
        norm += amp
        amp *= 0.55
    return total / norm


def gas_bubble_fn(seed):
    """
    爆炸氣泡（參考：慢動作水下爆炸影片 1:00–1:17）：
    幾乎透明的球，裡面充滿斑駁、雲狀的爆炸氣體；邊緣柔和、帶銀白光澤，沒有清楚的輪廓線；左上一道淡高光。
    """
    rnd = random.Random(seed)
    harmonics = [(k, rnd.uniform(0, 2 * math.pi), 0.012 / k) for k in (2, 3, 5)]
    noises = [Noise(seed * 13 + i, n) for i, n in enumerate((5, 10, 20, 40))]
    hl_angle = math.radians(225)

    def fn(u, v):
        r = math.hypot(u, v)
        ang = math.atan2(v, u)
        edge = 0.92 + sum(a * math.sin(k * ang + p) for k, p, a in harmonics)
        rn = r / edge
        if rn > 1.06:
            return (0, 0, 0, 0)
        soft = max(0.0, min(1.0, (1.06 - rn) / 0.1))          # 柔和的邊
        cloud = fbm(noises, (u + 1) / 2, (v + 1) / 2)          # 0–1
        cloud = max(0.0, (cloud - 0.35) / 0.65)                 # 拉開對比：一團一團
        # 球面投影：邊緣看穿的氣體比較厚，中心比較薄
        depth = math.sqrt(max(0.0, 1.0 - min(1.0, rn) ** 2))
        thick = 1.0 - 0.55 * depth
        gas = 0.38 * cloud * thick
        sheen = 0.32 * math.exp(-((1.0 - min(rn, 1.0)) / 0.12))  # 邊緣銀白光澤
        da = math.atan2(math.sin(ang - hl_angle), math.cos(ang - hl_angle))
        highlight = 0.35 * g(rn, 0.72, 0.12) * g(da, 0, 0.6)
        haze = 0.06
        a = (gas + sheen + highlight + haze) * soft
        lum = 0.78 + 0.22 * min(1.0, (sheen + highlight) * 2 + cloud * 0.5)
        return (lum, lum, lum, min(1.0, a))
    return fn


def foam_fn(seed):
    """
    崩塌後的泡沫團：起伏很大、像花椰菜的白色團塊，中間不透明，凹處暗一點，邊緣柔和。
    """
    rnd = random.Random(seed)
    harmonics = [(k, rnd.uniform(0, 2 * math.pi), rnd.uniform(0.04, 0.09) * (3.0 / k) ** 0.5) for k in (3, 4, 5, 7, 9, 12)]
    noises = [Noise(seed * 17 + i, n) for i, n in enumerate((6, 12, 24, 48))]

    def fn(u, v):
        r = math.hypot(u, v)
        ang = math.atan2(v, u)
        lumps = fbm(noises, (u + 1) / 2, (v + 1) / 2)
        edge = 0.72 + sum(a * math.sin(k * ang + p) for k, p, a in harmonics) + 0.12 * (lumps - 0.5)
        rn = r / edge
        if rn > 1.15:
            return (0, 0, 0, 0)
        soft = max(0.0, min(1.0, (1.15 - rn) / 0.3))
        body = 0.85 * soft
        # 光從上方（影像 y 向下）：上緣亮、凹處暗
        shade = 0.72 + 0.28 * lumps - 0.08 * v
        lum = max(0.45, min(1.0, shade))
        # 邊緣碎成小泡
        if rn > 0.85:
            speck = fbm(noises[2:], (u + 1) / 2 * 1.7, (v + 1) / 2 * 1.7)
            body *= max(0.0, min(1.0, (speck - 0.35) * 3.0)) * 0.6 + 0.4 * max(0.0, (1.15 - rn) / 0.3)
        return (lum, lum, lum, min(1.0, body))
    return fn


def puff_foam_fn(seed, count=80):
    """
    崩塌後的泡沫團（花椰菜狀）：很多小圓泡疊在一起，每顆各自有立體明暗（左上亮、右下暗），
    外圍的泡比較小、比較稀，整團邊緣是碎的。
    """
    rnd = random.Random(seed)
    puffs = []
    for _ in range(count):
        # 越靠外越小
        ang = rnd.uniform(0, 2 * math.pi)
        dist = abs(rnd.gauss(0, 0.2))
        if dist > 0.5:
            continue
        rad = rnd.uniform(0.1, 0.22) * (1.1 - dist * 0.8)
        puffs.append((dist * math.cos(ang), dist * math.sin(ang), rad))
    noise = Noise(seed * 31 + 3, 24)
    light = (-0.55, -0.65)                      # 影像 y 向下：左上
    ln = math.hypot(*light)
    light = (light[0] / ln, light[1] / ln)

    def fn(u, v):
        alpha = 0.0
        lum_acc = 0.0
        w_acc = 0.0
        for px, py, pr in puffs:
            d = math.hypot(u - px, v - py) / pr
            if d >= 1.35:
                continue
            a = max(0.0, min(1.0, (1.35 - d) / 0.6))
            # 球面法線估明暗
            dd = min(d, 1.0)
            nz = math.sqrt(max(0.0, 1.0 - dd * dd))
            nx = (u - px) / pr
            ny = (v - py) / pr
            lit = 0.55 + 0.45 * max(-1.0, min(1.0, (nx * light[0] + ny * light[1]) * 0.8 + nz * 0.5))
            alpha = 1.0 - (1.0 - alpha) * (1.0 - a * 0.85)
            lum_acc += lit * a
            w_acc += a
        if alpha <= 0.002:
            return (0, 0, 0, 0)
        lum = lum_acc / w_acc
        lum *= 0.9 + 0.1 * noise.at((u + 1) / 2, (v + 1) / 2)
        lum = max(0.4, min(1.0, lum))
        return (lum, lum, lum, min(1.0, alpha))
    return fn


# 主氣泡改用原版 Things/Mote/Smoke（2026-11-18），不再產生 Bubble.png
save(256, puff_foam_fn(4), "Foam")
save(256, ring_fn, "ShockRing")
for name, seed in (("BubbleSmall_A", 3), ("BubbleSmall_B", 5), ("BubbleSmall_C", 8)):
    save(48, bubble_fn(seed, 0.03, 0.2, 0.18, 48), name)
