"""Third pass after the owner's notes: upright mountains, a centred pyramid composition, a rich snow ground.

    py -3 ink3.py <out-dir> <hexart-dir>
"""
import math
import os
import random
import sys

from PIL import Image, ImageChops, ImageDraw, ImageFilter

from ink_mountains import INK, K, MID, N, SHADE, SNOW, hex_mask, jag, lerp, point_on, smooth, stroke
import ink2
from ink2 import Mountain


class Upright(Mountain):
    """Summit straight above the middle of the foot; teeth on both sides of it; a near-vertical crest."""

    def __init__(self, cx, base, height, half, rng, kind="rocky"):
        self.rng, self.kind, self.base, self.height, self.half = rng, kind, base, height, half
        top = (cx, base - height)
        lb = (cx - half, base)
        rb = (cx + half, base)
        rough = 0.05 if kind == "rocky" else 0.025
        # flanks: steep shoulders high up, a gentle foot (both sides alike, so nothing leans)
        def flank(side):
            k2 = (cx + side * half * 0.28, lerp(top[1], base, 0.42))
            k1 = (cx + side * half * 0.62, lerp(top[1], base, 0.82))
            foot = (cx + side * half, base)
            return [top] + jag(top, k2, rng, 2, rough * 1.3)[1:-1] + [k2] + jag(k2, k1, rng, 2, rough)[1:-1] + [k1] \
                + jag(k1, foot, rng, 1, rough * 0.5)[1:]
        lflank, rflank = flank(-1), flank(1)
        # a toothed summit: one lower tooth each side of the main peak
        teeth_l, teeth_r = [], []
        if kind == "rocky":
            for side, store in ((-1, teeth_l), (1, teeth_r)):
                if rng.random() < 0.85:
                    a = point_on(lflank if side < 0 else rflank, rng.uniform(0.12, 0.18))
                    tip = (a[0] + side * half * 0.02, a[1] - height * rng.uniform(0.07, 0.11))
                    notch = point_on(lflank if side < 0 else rflank, rng.uniform(0.06, 0.09))
                    store += [notch, tip, a]
        left = lflank[::-1]
        if teeth_l:
            n, t, a = teeth_l
            ia = min(range(len(left)), key=lambda i: math.dist(left[i], a))
            left = left[:ia] + [a, t, n, top]
        right = rflank
        if teeth_r:
            n, t, a = teeth_r
            ia = min(range(len(right)), key=lambda i: math.dist(right[i], a))
            right = [top, n, t, a] + right[ia + 1:]
        self.left, self.right, self.top, self.lb, self.rb = left, right, top, lb, rb
        self.teeth = [top] + ([teeth_r[0], teeth_r[1]] if teeth_r else [])
        self.outline = left + right[1:]
        # the crest falls almost straight down, a little left of the middle, with ledges
        foot = (cx - half * rng.uniform(0.05, 0.15), base)
        self.crest = [top]
        for i, f in enumerate((0.2, 0.42, 0.64, 0.84)):
            wob = half * rng.uniform(0.03, 0.07) * (1 if i % 2 == 0 else -1)
            self.crest.append((lerp(top[0], foot[0], f) + wob, lerp(top[1], base, f)))
        self.crest.append(foot)
        start = teeth_r[1] if teeth_r else point_on(right, 0.15)
        self.crest2 = [start, (lerp(start[0], rb[0], 0.18), lerp(start[1], base, 0.45)),
                       (lerp(start[0], rb[0], 0.3), lerp(start[1], base, 0.8))]


def snow_ground(hexart, variant, rng):
    """The hex set's own snow, then drawn detail: drifts with blue shadows, buried rocks, wind lines."""
    g = Image.open(os.path.join(hexart, "mountains_ground.webp")).convert("RGB").resize((N, N), Image.Resampling.BICUBIC)
    d = ImageDraw.Draw(g, "RGBA")
    # drifts: a soft blue shadow under a lit crescent edge
    for _ in range(9):
        x, y = rng.uniform(0, N), rng.uniform(0, N)
        w, h = rng.uniform(26, 60) * K, rng.uniform(5, 10) * K
        shade = Image.new("L", (N, N), 0)
        ImageDraw.Draw(shade).ellipse([x - w, y - h * 0.2, x + w, y + h], fill=110)
        g.paste(Image.new("RGB", (N, N), (206, 222, 233)), (0, 0), shade.filter(ImageFilter.GaussianBlur(K * 4)))
        stroke(d, [(x - w * 0.7, y + h * 0.1), (x, y - h * 0.15), (x + w * 0.6, y + h * 0.1)], K * 0.5, K * 0.2,
               color=(120, 142, 160, 110))
    # wind lines
    for _ in range(26):
        x, y = rng.uniform(0, N), rng.uniform(0, N)
        ln = rng.uniform(8, 22) * K
        stroke(d, [(x, y), (x + ln * 0.5, y - K * 0.6), (x + ln, y + K * 0.2)], K * 0.45, 0.2, color=(130, 152, 170, 90))
    # rocks breaking through the snow: only the shadowed underside shows, the top stays under snow
    for _ in range(rng.choice([2, 3])):
        x, y = rng.uniform(40, 216) * K, rng.uniform(36, 220) * K
        s = rng.uniform(3, 5.5) * K
        under = [(x - s, y), (x - s * 0.5, y - s * rng.uniform(0.25, 0.4)), (x + s * 0.2, y - s * rng.uniform(0.1, 0.3)),
                 (x + s * 0.7, y - s * 0.2), (x + s, y)]
        d.polygon(under + [(x + s * 0.6, y + s * 0.15), (x - s * 0.6, y + s * 0.12)], fill=SHADE + (255,))
        stroke(d, under, K * 0.4, K * 0.9, color=INK[:3] + (230,), curve=False)
        for j in range(3):
            xx = lerp(x - s * 0.8, x + s * 0.6, j / 2)
            stroke(d, [(xx, y - s * 0.15), (xx - s * 0.2, y + s * 0.1)], K * 0.5, 0.2, color=INK[:3] + (170,))
    # pebbles and dots
    for _ in range(30):
        x, y = rng.uniform(0, N), rng.uniform(0, N)
        r = rng.uniform(0.4, 0.9) * K
        d.ellipse([x - r, y - r * 0.7, x + r, y + r * 0.7], fill=INK[:3] + (int(rng.uniform(70, 150)),))
    return g


# (dx, dy-of-foot, height, half, kind) relative to the group; the first is the main mountain, drawn after the
# ones behind it. Every group is a pyramid: the big one centred, smaller ones lower and to the sides.
GROUPS = [
    [("back", 0, 0, 82, 72, "rocky"), ("front", -54, 30, 42, 34, "rocky"), ("front", 54, 26, 36, 30, "smooth")],
    [("back", 0, 0, 86, 70, "rocky"), ("front", 52, 28, 46, 36, "rocky"), ("front", -52, 32, 32, 30, "smooth")],
    [("back", 6, 0, 80, 70, "rocky"), ("side", -50, 12, 52, 40, "rocky"), ("front", 54, 32, 32, 30, "smooth")],
]


def compose(hexart, variant):
    rng = random.Random(3000 + variant)
    img = snow_ground(hexart, variant, rng)
    group = GROUPS[variant % 3]
    # bounding box of the group, then shift so it sits in the optical centre of the hex
    xs, ys = [], []
    for _, dx, dy, h, half, _k in group:
        xs += [dx - half * 1.1, dx + half * 1.1]
        ys += [dy - h, dy + 4]
    ox = 128 - (min(xs) + max(xs)) / 2
    # the owner (2026-10-22): the main group sits higher, toward the top of the hex
    oy = 112 - (min(ys) + max(ys)) / 2
    # a cool shadow pool the whole group stands in, so it sits on the ground
    pool = Image.new("L", (N, N), 0)
    ImageDraw.Draw(pool).ellipse([(min(xs) + ox - 6) * K, (max(ys) + oy - 22) * K, (max(xs) + ox + 6) * K, (max(ys) + oy + 6) * K], fill=120)
    img.paste(Image.new("RGB", (N, N), (200, 216, 228)), (0, 0), pool.filter(ImageFilter.GaussianBlur(K * 6)))
    placed = [(dx + ox, dy + oy, h, half, kind) for _, dx, dy, h, half, kind in group]
    # ...and more small mountains over the rest of the hex, the edges too (each wholly inside the hex)
    def inside_hex(x, y, margin):
        dy = abs(y - 128)
        return dy <= 111 - margin and abs(x - 128) <= 128 - dy / math.sqrt(3) - margin

    def clear(x, foot, h, half):
        for px, pfoot, ph, phalf, _ in placed:
            if abs(x - px) < (half + phalf) * 0.8 and foot > pfoot - ph * 0.9 and foot - h < pfoot + 4:
                return False
        return True

    def fits(x, foot, h, half):
        return inside_hex(x - half, foot, 6) and inside_hex(x + half, foot, 6) and inside_hex(x, foot - h, 6)

    # A range runs from the main massif's foothill down toward one lower corner: each step smaller,
    # a little lower and tucked partly behind the previous one, like the spurs of one ridge.
    side = (-1, 1, -1)[variant % 3]
    start = max((m for m in placed[1:] if (m[0] - placed[0][0]) * side > 0), key=lambda m: abs(m[0] - placed[0][0]), default=placed[0])
    x, foot, h, half = start[0], start[1], start[2], start[3]
    for _ in range(rng.choice([2, 3])):
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
        placed.append((nx, nfoot, nh, nhalf, "rocky" if nh > 22 else "smooth"))
        x, foot, h, half = nx, nfoot, nh, nhalf
    # one small hill on the other side, lower down, to balance the range (not on the same line)
    for _ in range(200):
        bh = rng.uniform(15, 20)
        bhalf = bh * rng.uniform(1.0, 1.2)
        bx = 128 - side * rng.uniform(30, 70)
        bfoot = rng.uniform(186, 214)
        if fits(bx, bfoot, bh, bhalf) and clear(bx, bfoot, bh, bhalf) and all(abs(bfoot - m[1]) > 6 for m in placed):
            placed.append((bx, bfoot, bh, bhalf, "smooth"))
            break
    # back to front: the lower the foot, the nearer the mountain
    for x, foot, h, half, kind in sorted(placed, key=lambda m: m[1]):
        Upright(x * K, foot * K, h * K, half * K, rng, kind).paint(img)
    return img


if __name__ == "__main__":
    out, hexart = sys.argv[1], sys.argv[2]
    os.makedirs(out, exist_ok=True)
    sheet = Image.new("RGB", (3 * 520 + 20, 540), (60, 64, 70))
    mask = hex_mask(512)
    for v in range(3):
        big = compose(hexart, v)
        big.resize((256, 256), Image.Resampling.LANCZOS).save(os.path.join(out, f"ink_mountains_{v}.png"))
        sheet.paste(big.resize((512, 512), Image.Resampling.LANCZOS), (20 + v * 520, 14), mask)
        # guide check: the hex centre
    sheet.save(os.path.join(out, "sheet2.png"))
    ink2_dir = os.path.dirname(os.path.abspath(ink2.__file__))
    # the map patch from ink2's code
    tiles = {f"m{v}": Image.open(os.path.join(out, f"ink_mountains_{v}.png")).convert("RGB") for v in range(3)}
    for name in ("grass_0", "forest_0", "mountains_0"):
        tiles[name] = Image.open(os.path.join(ink2_dir, name + ".png")).convert("RGB")
    size = 128
    grid = [["grass_0", "m0", "m1", "forest_0"], ["m2", "m1", "m0", "grass_0"], ["forest_0", "m2", "grass_0", "mountains_0"]]
    w = size * 0.75
    hh = size * math.sqrt(3) / 2
    patch = Image.new("RGB", (int(w * 4 + size * 0.25) + 20, int(hh * 3.5) + 20), (60, 64, 70))
    m = hex_mask(size)
    for c in range(4):
        for r in range(3):
            t = tiles[grid[r][c]].resize((size, size), Image.Resampling.LANCZOS)
            patch.paste(t, (int(10 + c * w), int(10 + r * hh + (hh / 2 if c % 2 else 0) - (size - hh) / 2)), m)
    patch.save(os.path.join(out, "patch.png"))
