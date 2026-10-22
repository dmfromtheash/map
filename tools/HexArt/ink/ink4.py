"""Fourth pass: a vocabulary of mountain shapes read from the owner's sheets, six tile variants.

Shapes (sheet 4: peaks, twins, crowned massifs, low mounds; sheet 5: needle spires; sheet 3: flat-topped
craters). Each is an outline of control points (u across the foot -1..1, v up 0..1), roughened per mountain.

    py -3 tools/HexArt/ink/ink4.py <out-dir> src/Map.Rendering/HexArt
    then: py -3 tools/HexArt/build.py ... mountains (copies painted/mountains_<v>.webp into the tile set)

The owner chose this look on 2026-10-22 (sheet v4): upright mountains, the main group high in the hex,
foothills and a falling range, one balancing hill, six main shapes. Not copied from his sheets.
"""
import math
import os
import random
import sys

from PIL import Image, ImageDraw, ImageFilter

from ink_mountains import INK, K, N, SHADE, hex_mask, jag, lerp, point_on, smooth, stroke
from ink2 import Mountain, sliver
from ink3 import snow_ground

SHAPES = {
    "peak":  [(-1, 0), (-0.62, 0.18), (-0.3, 0.56), (-0.12, 0.84), (0, 1), (0.14, 0.86), (0.32, 0.54), (0.62, 0.2), (1, 0)],
    "twin":  [(-1, 0), (-0.62, 0.24), (-0.36, 0.78), (-0.2, 1), (0.0, 0.7), (0.2, 0.84), (0.42, 0.46), (0.68, 0.16), (1, 0)],
    "crown": [(-1, 0), (-0.62, 0.3), (-0.38, 0.78), (-0.28, 0.9), (-0.18, 0.8), (-0.05, 1), (0.08, 0.83), (0.2, 0.93),
              (0.32, 0.76), (0.6, 0.3), (1, 0)],
    "mesa":  [(-1, 0), (-0.66, 0.3), (-0.44, 0.84), (-0.34, 0.97), (-0.1, 1), (0.16, 0.95), (0.34, 0.97), (0.46, 0.82),
              (0.7, 0.28), (1, 0)],
    "spire": [(-1, 0), (-0.58, 0.16), (-0.3, 0.52), (-0.14, 0.86), (-0.04, 1), (0.08, 0.72), (0.2, 0.8), (0.34, 0.44),
              (0.62, 0.14), (1, 0)],
    "mound": [(-1, 0), (-0.6, 0.45), (-0.25, 0.9), (0, 1), (0.3, 0.86), (0.62, 0.4), (1, 0)],
}


class Shaped(Mountain):
    def __init__(self, cx, base, height, half, rng, shape="peak"):
        self.rng, self.base, self.height, self.half, self.shape = rng, base, height, half, shape
        self.kind = "smooth" if shape == "mound" or height < 22 * K else "rocky"
        rough = {"mound": 0.02, "mesa": 0.035}.get(shape, 0.05)
        ctrl = []
        for i, (u, v) in enumerate(SHAPES[shape]):
            if 0 < i < len(SHAPES[shape]) - 1:
                u += rng.uniform(-0.04, 0.04)
                v = min(1.0, max(0.05, v + rng.uniform(-0.05, 0.05))) if v < 1 else 1.0
            ctrl.append((cx + u * half, base - v * height))
        pts = [ctrl[0]]
        for a, b in zip(ctrl, ctrl[1:]):
            pts += jag(a, b, rng, 2 if math.dist(a, b) > 14 * K else 1, rough)[1:]
        si = min(range(len(pts)), key=lambda i: pts[i][1])
        top = pts[si]
        self.left, self.right, self.top = pts[: si + 1], pts[si:], top
        self.lb, self.rb = ctrl[0], ctrl[-1]
        self.outline = pts
        # teeth: the local summits right of the top (pairs notch, tip) for the slivers in paint
        tips = [c for c in ctrl[1:-1] if c[1] < base - height * 0.7 and c is not ctrl[0]]
        self.teeth = [top]
        for j in range(1, len(ctrl) - 1):
            c = ctrl[j]
            if c[0] > top[0] and c[1] < ctrl[j - 1][1] and c[1] < ctrl[j + 1][1] and c[1] < base - height * 0.5:
                self.teeth += [ctrl[j - 1], c]
        del tips
        foot = (cx - half * rng.uniform(0.05, 0.18), base)
        self.crest = [top]
        for i, f in enumerate((0.2, 0.42, 0.64, 0.84)):
            wob = half * rng.uniform(0.03, 0.07) * (1 if i % 2 == 0 else -1)
            self.crest.append((lerp(top[0], foot[0], f) + wob, lerp(top[1], base, f)))
        self.crest.append(foot)
        start = self.teeth[-1] if len(self.teeth) > 1 else point_on(self.right, 0.15)
        self.crest2 = [start, (lerp(start[0], self.rb[0], 0.18), lerp(start[1], base, 0.45)),
                       (lerp(start[0], self.rb[0], 0.3), lerp(start[1], base, 0.8))]
        self._ctrl = ctrl

    def paint(self, img):
        super().paint(img)
        if self.shape == "mesa":
            # a crater: a dark hollow just under the plateau rim, broken by the far rim's teeth (sheet 3)
            d = ImageDraw.Draw(img, "RGBA")
            rim = [c for c in self._ctrl if c[1] < self.base - self.height * 0.9]
            if len(rim) >= 2:
                x0, x1 = rim[0][0] + self.half * 0.04, rim[-1][0] - self.half * 0.04
                y = max(c[1] for c in rim) + self.height * 0.03
                depth = self.height * 0.09
                hollow = [(x0, y)]
                for k in range(1, 8):
                    hollow.append((lerp(x0, x1, k / 8), y - self.height * (0.03 if k % 2 else -0.01)))
                hollow.append((x1, y))
                hollow += [(lerp(x1, x0, k / 6), y + depth * math.sin(math.pi * k / 6) * self.rng.uniform(0.7, 1.1)) for k in range(7)]
                d.polygon(hollow, fill=INK)
                stroke(d, [(x0 - self.half * 0.02, y + depth * 0.3), (lerp(x0, x1, 0.5), y + depth * 1.4),
                           (x1 + self.half * 0.02, y + depth * 0.3)], K * 0.8, K * 0.8, color=INK[:3] + (180,))


def inside_hex(x, y, margin):
    """Inside both the flat and the pointy hex: one picture serves both orientations."""
    dx, dy = abs(x - 128), abs(y - 128)
    flat = dy <= 111 - margin and dx <= 128 - dy / math.sqrt(3) - margin
    pointy = dx <= 111 - margin and dy <= 128 - dx / math.sqrt(3) - margin
    return flat and pointy


def fits(x, foot, h, half):
    return inside_hex(x - half, foot, 6) and inside_hex(x + half, foot, 6) and inside_hex(x, foot - h, 6)


# Six tiles. main = shape, height, half; foot = foothills beside it (dx, dy, h, half, shape);
# range = which way a falling chain runs (-1 left, 1 right, 0 none); balance = a lone small hill.
VARIANTS = [
    dict(main=("peak", 84, 70), foot=[(-54, 30, 40, 34, "twin")], range=1, chain=["peak", "mound"], balance="mound"),
    dict(main=("crown", 80, 74), foot=[(52, 28, 42, 36, "peak")], range=-1, chain=["spire", "mound"], balance="peak"),
    dict(main=("twin", 86, 70), foot=[(-56, 26, 36, 32, "crown")], range=1, chain=["mound"], balance="spire"),
    dict(main=("mesa", 66, 76), foot=[(56, 24, 40, 32, "peak"), (-58, 30, 30, 30, "mound")], range=0, chain=[], balance="twin"),
    dict(main=("spire", 92, 52), foot=[(-44, 22, 54, 36, "spire"), (46, 30, 40, 30, "peak")], range=0, chain=[], balance="mound"),
    dict(main=("peak", 78, 74), foot=[], range=-1, chain=["crown", "peak", "mound"], balance="mound"),
]


def compose(hexart, variant):
    rng = random.Random(4000 + variant)
    img = snow_ground(hexart, variant, rng)
    spec = VARIANTS[variant]
    shape, mh, mhalf = spec["main"]
    group = [(0, 0, mh, mhalf, shape)] + list(spec["foot"])
    xs = [dx - half * 1.05 for dx, _, _, half, _ in group] + [dx + half * 1.05 for dx, _, _, half, _ in group]
    ys = [dy - h for dx, dy, h, _, _ in group] + [dy for _, dy, _, _, _ in group]
    ox = 128 - (min(xs) + max(xs)) / 2
    oy = 112 - (min(ys) + max(ys)) / 2
    placed = [(dx + ox, dy + oy, h, half, s) for dx, dy, h, half, s in group]
    pool = Image.new("L", (N, N), 0)
    ImageDraw.Draw(pool).ellipse([(min(xs) + ox - 6) * K, (max(ys) + oy - 22) * K, (max(xs) + ox + 6) * K, (max(ys) + oy + 6) * K], fill=120)
    img.paste(Image.new("RGB", (N, N), (200, 216, 228)), (0, 0), pool.filter(ImageFilter.GaussianBlur(K * 6)))

    def clear(x, foot, h, half):
        return all(not (abs(x - px) < (half + phalf) * 0.8 and foot > pf - ph * 0.9 and foot - h < pf + 4)
                   for px, pf, ph, phalf, _ in placed)

    side = spec["range"]
    if side:
        start = max((m for m in placed[1:] if (m[0] - placed[0][0]) * side > 0), key=lambda m: abs(m[0] - placed[0][0]), default=placed[0])
        x, foot, h, half = start[0], start[1], start[2], start[3]
        for s in spec["chain"]:
            nh = h * rng.uniform(0.62, 0.74)
            nhalf = nh * rng.uniform(0.95, 1.15)
            nx = x + side * (half * 0.55 + nhalf * 0.6)
            nfoot = foot + nh * rng.uniform(0.35, 0.55)
            while nh > 10 and not fits(nx, nfoot, nh, nhalf):
                nh *= 0.85
                nhalf *= 0.85
                nx = x + side * (half * 0.55 + nhalf * 0.6)
            if nh <= 10:
                break
            placed.append((nx, nfoot, nh, nhalf, s))
            x, foot, h, half = nx, nfoot, nh, nhalf
    other = -side if side else rng.choice([-1, 1])
    for _ in range(300):
        bh = rng.uniform(15, 21)
        bhalf = bh * rng.uniform(1.0, 1.25)
        bx = 128 + other * rng.uniform(25, 70)
        bfoot = rng.uniform(184, 214)
        if fits(bx, bfoot, bh, bhalf) and clear(bx, bfoot, bh, bhalf) and all(abs(bfoot - m[1]) > 6 for m in placed):
            placed.append((bx, bfoot, bh, bhalf, spec["balance"]))
            break
    for x, foot, h, half, s in sorted(placed, key=lambda m: m[1]):
        Shaped(x * K, foot * K, h * K, half * K, rng, s).paint(img)
    return img


if __name__ == "__main__":
    out, hexart = sys.argv[1], sys.argv[2]
    os.makedirs(out, exist_ok=True)
    mask = hex_mask(400)
    sheet = Image.new("RGB", (3 * 410 + 20, 2 * 410 + 20), (60, 64, 70))
    for v in range(len(VARIANTS)):
        big = compose(hexart, v)
        # the 1024 px painting goes to the tool's painted-art folder; build.py reduces it into the tile set
        big.save(os.path.join(os.path.dirname(os.path.abspath(sys.argv[0])), "..", "painted", f"mountains_{v}.webp"),
                 "WEBP", quality=90, method=6)
        big.resize((256, 256), Image.Resampling.LANCZOS).save(os.path.join(out, f"v4_mountains_{v}.png"))
        sheet.paste(big.resize((400, 400), Image.Resampling.LANCZOS), (15 + (v % 3) * 410, 10 + (v // 3) * 410), mask)
    sheet.save(os.path.join(out, "sheet_v4.png"))

    # map patch at in-app size with the six variants
    here = os.path.dirname(os.path.abspath(sys.argv[0]))
    tiles = {f"m{v}": Image.open(os.path.join(out, f"v4_mountains_{v}.png")).convert("RGB") for v in range(len(VARIANTS))}
    for name in ("grass_0", "forest_0"):
        tiles[name] = Image.open(os.path.join(hexart, name + ".webp")).convert("RGB")
    size = 128
    grid = [["grass_0", "m0", "m1", "forest_0", "grass_0"], ["m2", "m3", "m4", "m5", "grass_0"], ["forest_0", "m1", "m5", "grass_0", "forest_0"]]
    w = size * 0.75
    hh = size * math.sqrt(3) / 2
    patch = Image.new("RGB", (int(w * 5 + size * 0.25) + 20, int(hh * 3.5) + 20), (60, 64, 70))
    m = hex_mask(size)
    for c in range(5):
        for r in range(3):
            t = tiles[grid[r][c]].resize((size, size), Image.Resampling.LANCZOS)
            patch.paste(t, (int(10 + c * w), int(10 + r * hh + (hh / 2 if c % 2 else 0) - (size - hh) / 2)), m)
    patch.save(os.path.join(out, "patch_v4.png"))
