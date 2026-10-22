"""Second pass: wide massifs with toothed summits, full-face fall-line hatching, ink slivers.

    python ink2.py <out-dir>
"""
import math
import os
import random
import sys

from PIL import Image, ImageChops, ImageDraw, ImageFilter

from ink_mountains import INK, K, MID, N, SNOW, SHADE, ground, hex_mask, jag, lerp, point_on, smooth, stroke


def inside_poly(poly, x, y):
    c = False
    j = len(poly) - 1
    for i in range(len(poly)):
        xi, yi = poly[i]
        xj, yj = poly[j]
        if (yi > y) != (yj > y) and x < (xj - xi) * (y - yi) / (yj - yi) + xi:
            c = not c
        j = i
    return c


def sliver(draw, path, t0, t1, thick, side, color=INK):
    """A solid ink sliver hugging a line between fractions t0..t1, widest in the middle."""
    pts = [point_on(path, lerp(t0, t1, k / 10)) for k in range(11)]
    inner = []
    for k, (x, y) in enumerate(pts):
        a = pts[max(0, k - 1)]
        b = pts[min(10, k + 1)]
        dx, dy = b[0] - a[0], b[1] - a[1]
        ln = math.hypot(dx, dy) or 1
        w = thick * math.sin(math.pi * k / 10) ** 0.8
        inner.append((x - dy / ln * w * side, y + dx / ln * w * side))
    draw.polygon(pts + inner[::-1], fill=color)


class Mountain:
    """A massif: wide foot, concave shadow ridge on the left, a toothed summit, a shouldered lit ridge."""

    def __init__(self, cx, base, height, half, rng, kind="rocky", lean=0.0):
        self.rng, self.kind, self.base, self.height, self.half = rng, kind, base, height, half
        top = (cx + lean * half, base - height)
        lb = (cx - half, base)
        rb = (cx + half * rng.uniform(0.95, 1.1), base)
        rough = 0.06 if kind == "rocky" else 0.03
        k1 = (lerp(lb[0], top[0], 0.35), lerp(base, top[1], 0.24))
        k2 = (lerp(lb[0], top[0], 0.78), lerp(base, top[1], 0.62))
        left = jag(lb, k1, rng, 2, rough * 0.6)[:-1] + jag(k1, k2, rng, 2, rough)[:-1] + jag(k2, top, rng, 2, rough * 1.5)
        teeth = [top]
        x, y = top
        n = rng.choice([1, 2, 2, 3]) if kind == "rocky" else rng.choice([0, 1])
        for _ in range(n):
            nx = x + half * rng.uniform(0.07, 0.13)
            teeth.append((nx - half * 0.03, y + height * rng.uniform(0.07, 0.13)))
            y = y + height * rng.uniform(-0.02, 0.12)
            x = nx + half * rng.uniform(0.02, 0.06)
            teeth.append((x, y))
        sh = (lerp(x, rb[0], 0.45), lerp(y, base, 0.42) - height * 0.05)
        right = teeth + jag(teeth[-1], sh, rng, 2, rough)[1:-1] + [sh] + jag(sh, rb, rng, 2, rough * 0.7)[1:]
        self.left, self.right, self.top, self.lb, self.rb, self.teeth = left, right, top, lb, rb, teeth
        self.outline = left + right[1:]
        foot = (lerp(lb[0], rb[0], rng.uniform(0.36, 0.46)), base)
        # the crest wanders: it bends at ledges, so the shadow is not cut by a ruler
        self.crest = [top]
        for i, f in enumerate((0.2, 0.4, 0.6, 0.8)):
            wob = half * rng.uniform(0.06, 0.12) * (1 if i % 2 == 0 else -1)
            self.crest.append((lerp(top[0], foot[0], f) + wob, lerp(top[1], base, f)))
        self.crest.append(foot)
        # a second crest falls from the last tooth across the lit face
        last = teeth[-1]
        self.crest2 = [last, (lerp(last[0], rb[0], 0.15) - half * 0.05, lerp(last[1], base, 0.4)),
                       (lerp(last[0], rb[0], 0.25), lerp(last[1], base, 0.75))]

    def paint(self, img):
        rng = self.rng
        h = self.height
        d = ImageDraw.Draw(img, "RGBA")
        cast = Image.new("L", img.size, 0)
        ImageDraw.Draw(cast).ellipse([self.lb[0] - h * 0.1, self.base - h * 0.06, self.rb[0] + h * 0.05, self.base + h * 0.06], fill=90)
        img.paste(Image.new("RGB", img.size, MID), (0, 0), cast.filter(ImageFilter.GaussianBlur(h * 0.05)))
        d.polygon(self.outline, fill=SNOW + (255,))

        crest = smooth(self.crest)
        shadow = self.left + crest[1:]
        mask = Image.new("L", img.size, 0)
        ImageDraw.Draw(mask).polygon(shadow, fill=255)
        grad = Image.new("L", img.size, 0)
        gd = ImageDraw.Draw(grad)
        for i in range(30):
            t = i / 29
            y0 = lerp(self.top[1], self.base, t)
            gd.rectangle([0, y0, img.size[0], y0 + h / 29 + 2], fill=int(lerp(190, 60, t)))
        img.paste(Image.new("RGB", img.size, MID), (0, 0), ImageChops.multiply(mask, grad).filter(ImageFilter.GaussianBlur(K)))
        img.paste(Image.new("RGB", img.size, SHADE), (0, 0),
                  ImageChops.multiply(mask, grad.point(lambda v: int(v * 0.55))).filter(ImageFilter.GaussianBlur(K * 2)))
        body = Image.new("L", img.size, 0)
        ImageDraw.Draw(body).polygon(self.outline, fill=255)
        low = Image.new("L", img.size, 0)
        ImageDraw.Draw(low).rectangle([0, self.base - h * 0.25, img.size[0], self.base], fill=70)
        img.paste(Image.new("RGB", img.size, MID), (0, 0), ImageChops.multiply(body, low.filter(ImageFilter.GaussianBlur(h * 0.1))))

        hatch = Image.new("RGBA", img.size, (0, 0, 0, 0))
        hd = ImageDraw.Draw(hatch)
        gap = K * (2.3 if self.kind == "rocky" else 3.2)
        total = sum(math.dist(crest[i], crest[i + 1]) for i in range(len(crest) - 1))
        steps = max(4, int(total / gap))
        for i in range(steps):
            t = (i + rng.uniform(-0.25, 0.25)) / steps
            if t > 0.93:
                break
            p = point_on(crest, max(0.0, t))
            ang = math.radians(lerp(118, 150, t) + rng.uniform(-4, 4))
            reach = 0
            while reach < h and inside_poly(shadow, p[0] + math.cos(ang) * (reach + K), p[1] + math.sin(ang) * (reach + K)):
                reach += K
            if reach < K * 3:
                continue
            share = rng.uniform(0.45, 0.95) * (1 - 0.35 * t)
            q = (p[0] + math.cos(ang) * reach * share, p[1] + math.sin(ang) * reach * share)
            stroke(hd, [(p[0] + K * 0.5, p[1]), q], K * lerp(1.1, 0.75, t), 0.25,
                   color=INK[:3] + (int(lerp(230, 140, t)),), jitter=rng)
        left = smooth(self.left)
        for _ in range(int(h / K / 5)):
            t = rng.uniform(0.15, 0.9)
            p = point_on(left, t)
            ang = math.radians(rng.uniform(55, 80))
            ln = h * rng.uniform(0.06, 0.13)
            stroke(hd, [(p[0] + K, p[1] + K * 1.5), (p[0] + math.cos(ang) * ln + K, p[1] + math.sin(ang) * ln)], K * 0.8, 0.2,
                   color=INK[:3] + (150,))
        img.paste(hatch, (0, 0), ImageChops.multiply(hatch.getchannel("A"), mask))

        d = ImageDraw.Draw(img, "RGBA")
        sliver(d, left, rng.uniform(0.62, 0.7), rng.uniform(0.86, 0.95), h * 0.055, 1)
        sliver(d, crest, 0.04, rng.uniform(0.22, 0.32), h * 0.05, 1)
        # each tooth casts a thin ink sliver down its left flank, and a short ridge falls from it
        for k in range(2, len(self.teeth), 2):
            tip, notch = self.teeth[k], self.teeth[k - 1]
            fall = (tip[0] - h * rng.uniform(0.02, 0.06), tip[1] + h * rng.uniform(0.18, 0.3))
            sliver(d, [tip, (lerp(tip[0], fall[0], 0.5) - K, lerp(tip[1], fall[1], 0.5)), fall], 0.0, 0.75, h * 0.03, 1)
            stroke(d, [tip, fall], K * 1.3, K * 0.2)
            for j in range(3):
                a = point_on([tip, fall], 0.25 + j * 0.2)
                stroke(d, [(a[0] - K, a[1]), (a[0] - h * 0.05, a[1] + h * 0.05)], K * 0.7, 0.2, color=INK[:3] + (170,))
        right = smooth(self.right)
        if self.kind == "rocky" and h > 50 * K:
            # spurs down the lit face, each with a sliver and a few short strokes on its shadow side
            for t0 in (rng.uniform(0.28, 0.38), rng.uniform(0.5, 0.62)):
                s = point_on(right, t0)
                e = (s[0] - h * rng.uniform(0.04, 0.1), s[1] + h * rng.uniform(0.2, 0.3) * (1.1 - t0))
                mid = (lerp(s[0], e[0], 0.5) + rng.uniform(-1, 1) * K, lerp(s[1], e[1], 0.5))
                stroke(d, [s, mid, e], K * 1.1, K * 0.2, curve=False)
                sliver(d, [s, mid, e], 0.05, 0.5, h * 0.025, 1)
                for j in range(4):
                    a = point_on([s, mid, e], 0.2 + j * 0.18)
                    stroke(d, [(a[0] - K, a[1] + K * 0.5), (a[0] - h * rng.uniform(0.04, 0.07), a[1] + h * 0.06)], K * 0.7, 0.2,
                           color=INK[:3] + (150,))
        if self.kind == "rocky" and len(self.teeth) > 1:
            c2 = smooth(self.crest2)
            stroke(d, c2[: int(len(c2) * 0.8)], K * 1.2, K * 0.2, curve=False)
            sliver(d, c2, 0.05, 0.35, h * 0.03, 1)
            for j in range(7):
                a = point_on(c2, 0.08 + j * 0.11)
                ln = h * rng.uniform(0.06, 0.12) * (1 - j * 0.07)
                stroke(d, [(a[0] - K, a[1] + K * 0.5), (a[0] - ln * 0.7, a[1] + ln)], K * 0.75, 0.2, color=INK[:3] + (int(lerp(200, 120, j / 6)),))
        cut = int(len(crest) * rng.uniform(0.55, 0.7))
        stroke(d, crest[:cut], K * 1.5, K * 0.3, curve=False)
        for _ in range(3 if self.kind == "rocky" else 1):
            p = point_on(right, rng.uniform(0.35, 0.85))
            stroke(d, [(p[0] - K * 1.5, p[1] + K), (p[0] - h * 0.05, p[1] + h * rng.uniform(0.05, 0.1))], K * 0.8, 0.2,
                   color=INK[:3] + (190,))
        stroke(d, self.left, K * 0.5, K * 2.5, swell=K * 0.7, curve=False, jitter=rng)
        stroke(d, self.right, K * 2.1, K * 0.4, curve=False, jitter=rng)
        for _ in range(rng.choice([2, 3, 4])):
            x = lerp(self.lb[0], self.rb[0], rng.uniform(0.05, 0.95))
            y = self.base + rng.uniform(0.5, 2.5) * K
            stroke(d, [(x, y), (x + rng.uniform(3, 8) * K, y + K * 0.2)], K * 0.7, 0.2, color=INK[:3] + (140,))


LAYOUTS = [
    # (cx, base, height, half, kind, lean) in tile px, drawn back to front
    [(132, 170, 96, 78, "rocky", 0.0), (82, 204, 44, 40, "rocky", 0.05), (180, 198, 34, 32, "smooth", 0.0)],
    [(124, 168, 100, 74, "rocky", 0.1), (178, 202, 48, 40, "rocky", -0.05), (84, 210, 30, 30, "smooth", 0.0)],
    [(128, 172, 104, 72, "rocky", -0.05), (176, 196, 50, 40, "rocky", 0.0), (80, 206, 38, 34, "rocky", 0.0), (132, 218, 22, 24, "smooth", 0.0)],
]


def compose(variant):
    rng = random.Random(2000 + variant)
    img = ground(rng)
    for cx, base, h, half, kind, lean in LAYOUTS[variant % 3]:
        Mountain(cx * K, base * K, h * K, half * K, rng, kind, lean).paint(img)
    return img


if __name__ == "__main__":
    out = sys.argv[1]
    os.makedirs(out, exist_ok=True)
    sheet = Image.new("RGB", (3 * 520 + 20, 540), (60, 64, 70))
    mask = hex_mask(512)
    for v in range(3):
        big = compose(v)
        big.resize((256, 256), Image.Resampling.LANCZOS).save(os.path.join(out, f"ink_mountains_{v}.png"))
        x2 = big.resize((512, 512), Image.Resampling.LANCZOS)
        sheet.paste(x2, (20 + v * 520, 14), mask)
    sheet.save(os.path.join(out, "sheet2.png"))
    # a patch of the map at in-app sizes: flat hexes 128 px wide, ours among the current grass/forest
    here = os.path.dirname(os.path.abspath(__file__))
    tiles = {f"m{v}": Image.open(os.path.join(out, f"ink_mountains_{v}.png")).convert("RGB") for v in range(3)}
    for name in ("grass_0", "forest_0", "mountains_0"):
        tiles[name] = Image.open(os.path.join(here, name + ".png")).convert("RGB")
    size = 128
    grid = [["grass_0", "m0", "m1", "forest_0"], ["m2", "m1", "m0", "grass_0"], ["forest_0", "m2", "grass_0", "mountains_0"]]
    w = size * 0.75
    hh = size * math.sqrt(3) / 2
    patch = Image.new("RGB", (int(w * 4 + size * 0.25) + 20, int(hh * 3.5) + 20), (60, 64, 70))
    m = hex_mask(size)
    for c in range(4):
        for r in range(3):
            t = tiles[grid[r][c]].resize((size, size), Image.Resampling.LANCZOS)
            x = int(10 + c * w)
            y = int(10 + r * hh + (hh / 2 if c % 2 else 0) - (size - hh) / 2)
            patch.paste(t, (x, y), m)
    patch.save(os.path.join(out, "patch.png"))
