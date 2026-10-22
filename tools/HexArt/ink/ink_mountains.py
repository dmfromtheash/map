"""Ink mountains for hex tiles, adapted (not copied) from the owner's reference sheets 3-5.

Technique read from the references:
  * one confident contour with pressure: thick on the shadow-side ridge, thin and broken at the tips,
    open at the base where it flicks out into a short ground line;
  * light from the right: the left faces carry the tone - a wash plus fall-line hatching that starts
    dense under the crest and thins out toward the foot; the lit faces stay paper with a few ticks;
  * solid ink wedges in the crevices right under the crest and where spurs split off;
  * small mountains are the same drawing with fewer marks.
Palette is the hex set's snow/ice (ground #f1f7fa, mid #e4f0f7, shade #90a4b2) with slate ink.

    python ink_mountains.py <out-dir>
"""
import math
import random
import sys
import os

from PIL import Image, ImageDraw, ImageFilter, ImageChops

K = 4                      # canvas px per tile px
N = 256 * K
INK = (30, 40, 52, 255)
SHADE = (144, 164, 178)
MID = (196, 212, 223)
SNOW = (241, 247, 250)
GROUND_DARK = (228, 240, 247)


def lerp(a, b, t):
    return a + (b - a) * t


def smooth(points, steps=8):
    """Catmull-Rom through the points."""
    if len(points) < 3:
        out = []
        for i in range(len(points) - 1):
            for s in range(steps):
                t = s / steps
                out.append((lerp(points[i][0], points[i + 1][0], t), lerp(points[i][1], points[i + 1][1], t)))
        out.append(points[-1])
        return out
    p = [points[0]] + list(points) + [points[-1]]
    out = []
    for i in range(1, len(p) - 2):
        p0, p1, p2, p3 = p[i - 1], p[i], p[i + 1], p[i + 2]
        for s in range(steps):
            t = s / steps
            t2, t3 = t * t, t * t * t
            out.append(tuple(0.5 * ((2 * p1[k]) + (-p0[k] + p2[k]) * t + (2 * p0[k] - 5 * p1[k] + 4 * p2[k] - p3[k]) * t2
                                    + (-p0[k] + 3 * p1[k] - 3 * p2[k] + p3[k]) * t3) for k in (0, 1)))
    out.append(points[-1])
    return out


def stroke(draw, pts, w0, w1, color=INK, swell=0.0, curve=True, jitter=None):
    """A pen stroke with pressure: width goes w0 -> w1 with a swell in the middle."""
    if curve and len(pts) > 2:
        path = smooth(pts)
    else:
        # straight pen segments: corners stay sharp
        path = []
        for i in range(len(pts) - 1):
            seg = max(2, int(math.dist(pts[i], pts[i + 1]) / 3))
            path += [(lerp(pts[i][0], pts[i + 1][0], s / seg), lerp(pts[i][1], pts[i + 1][1], s / seg)) for s in range(seg)]
        path.append(pts[-1])
    n = len(path)
    if n < 2:
        return
    left, right = [], []
    for i, (x, y) in enumerate(path):
        a = path[max(0, i - 1)]
        b = path[min(n - 1, i + 1)]
        dx, dy = b[0] - a[0], b[1] - a[1]
        ln = math.hypot(dx, dy) or 1
        nx, ny = -dy / ln, dx / ln
        t = i / (n - 1)
        w = lerp(w0, w1, t) + swell * math.sin(math.pi * t)
        if jitter:
            w *= 1 + jitter.uniform(-0.12, 0.12)
        w = max(w, 0.3) / 2
        left.append((x + nx * w, y + ny * w))
        right.append((x - nx * w, y - ny * w))
    draw.polygon(left + right[::-1], fill=color)


def jag(a, b, rng, depth, rough):
    """Jagged ridge between two points (midpoint displacement, perpendicular)."""
    if depth == 0:
        return [a, b]
    mx, my = (a[0] + b[0]) / 2, (a[1] + b[1]) / 2
    dx, dy = b[0] - a[0], b[1] - a[1]
    ln = math.hypot(dx, dy)
    off = rng.uniform(-rough, rough) * ln
    m = (mx - dy / ln * off, my + dx / ln * off)
    return jag(a, m, rng, depth - 1, rough * 0.6)[:-1] + jag(m, b, rng, depth - 1, rough * 0.6)


def point_on(path, t):
    """Point at fraction t of the polyline's length."""
    lens = [math.dist(path[i], path[i + 1]) for i in range(len(path) - 1)]
    total = sum(lens)
    d = t * total
    for i, l in enumerate(lens):
        if d <= l:
            f = d / l if l else 0
            return (lerp(path[i][0], path[i + 1][0], f), lerp(path[i][1], path[i + 1][1], f))
        d -= l
    return path[-1]


class Mountain:
    def __init__(self, cx, base, height, half, rng, kind="rocky", lean=0.0):
        self.rng = rng
        self.kind = kind
        self.base = base
        self.height = height
        sx = cx + lean * half
        top = (sx, base - height)
        lb = (cx - half, base)
        rb = (cx + half * rng.uniform(0.95, 1.15), base)
        rough = 0.07 if kind == "rocky" else 0.035
        # The shadow (left) ridge: concave, with a shoulder; the lit (right) ridge longer and broken by a second summit.
        s1 = (lerp(lb[0], sx, 0.55) - half * 0.08, lerp(base, top[1], 0.45))
        left = jag(lb, s1, rng, 2, rough)[:-1] + jag(s1, top, rng, 3, rough * 1.4)
        if kind == "rocky" and height > 60 * K / 4:
            sub = (lerp(sx, rb[0], 0.38), top[1] + height * rng.uniform(0.18, 0.3))
            notch = (lerp(sx, sub[0], 0.5), top[1] + height * rng.uniform(0.22, 0.32))
            right = [top] + jag(top, notch, rng, 1, rough)[1:-1] + [notch] + jag(notch, sub, rng, 1, rough)[1:-1] + [sub] \
                + jag(sub, rb, rng, 3, rough)[1:]
        else:
            right = jag(top, rb, rng, 3, rough)
        self.left, self.right, self.top, self.lb, self.rb = left, right, top, lb, rb
        self.outline = left + right[1:]
        # The crest: from the summit down into the body, splitting the shadowed and lit faces.
        foot = (lerp(lb[0], rb[0], rng.uniform(0.40, 0.5)), base - height * 0.02)
        c1 = (lerp(sx, foot[0], 0.35) + half * 0.06, lerp(top[1], base, 0.3))
        c2 = (lerp(sx, foot[0], 0.7) - half * 0.04, lerp(top[1], base, 0.66))
        self.crest = [top, c1, c2, foot]

    def shadow_poly(self):
        return self.left + self.crest[::-1][:-1]

    def paint(self, base_img, rng):
        rng = self.rng
        h = self.height
        # 1) silhouette in snow, so it hides what stands behind; a cast shadow on the ground to the left.
        d = ImageDraw.Draw(base_img, "RGBA")
        cast = Image.new("L", base_img.size, 0)
        cd = ImageDraw.Draw(cast)
        cd.ellipse([self.lb[0] - h * 0.25, self.base - h * 0.07, self.rb[0] - h * 0.2, self.base + h * 0.08], fill=110)
        cast = cast.filter(ImageFilter.GaussianBlur(h * 0.06))
        base_img.paste(Image.new("RGB", base_img.size, SHADE), (0, 0), cast.point(lambda v: int(v * 0.55)))
        d.polygon(self.outline + [(self.rb[0], self.base + 2), (self.lb[0], self.base + 2)], fill=SNOW + (255,))

        # 2) tone of the shadowed face: a wash, darker under the crest, fading toward the foot.
        mask = Image.new("L", base_img.size, 0)
        ImageDraw.Draw(mask).polygon(self.shadow_poly(), fill=255)
        grad = Image.new("L", base_img.size, 0)
        gd = ImageDraw.Draw(grad)
        for i in range(24):
            t = i / 23
            y0 = lerp(self.top[1], self.base, t)
            gd.rectangle([0, y0, base_img.size[0], y0 + h / 23 + 2], fill=int(lerp(200, 70, t)))
        wash = ImageChops.multiply(mask, grad).filter(ImageFilter.GaussianBlur(K * 1.2))
        base_img.paste(Image.new("RGB", base_img.size, MID), (0, 0), wash)
        dark = ImageChops.multiply(mask, grad.point(lambda v: max(0, v - 90) * 2)).filter(ImageFilter.GaussianBlur(K * 2))
        base_img.paste(Image.new("RGB", base_img.size, SHADE), (0, 0), dark)

        # 3) fall-line hatching on the shadowed face, clipped to it.
        hatch = Image.new("RGBA", base_img.size, (0, 0, 0, 0))
        hd = ImageDraw.Draw(hatch)
        count = int(h / K * (0.55 if self.kind == "rocky" else 0.4)) + 3
        for i in range(count):
            t = (i + rng.uniform(0, 0.8)) / count
            if t > 0.92:
                continue
            p = point_on(self.crest, t * 0.9)
            # strokes run down-left, steeper near the summit; shorter and lighter low down
            ang = math.radians(lerp(112, 140, t) + rng.uniform(-6, 6))
            length = h * lerp(0.42, 0.18, t) * rng.uniform(0.6, 1.1)
            q = (p[0] + math.cos(ang) * length, p[1] + math.sin(ang) * length)
            m = (lerp(p[0], q[0], 0.5) + rng.uniform(-1, 1) * K, lerp(p[1], q[1], 0.5))
            w = K * lerp(1.15, 0.7, t)
            stroke(hd, [(p[0] - K, p[1]), m, q], w, 0.2, color=INK[:3] + (int(lerp(235, 150, t)),), jitter=rng)
        # a second, shorter row under the left ridge, so the face reads as rounded
        for i in range(count // 2):
            t = (i + rng.uniform(0, 1)) / max(1, count // 2)
            if t < 0.15 or t > 0.85:
                continue
            p = point_on(self.left, 1 - t * 0.85)
            ang = math.radians(rng.uniform(70, 95))
            length = h * rng.uniform(0.08, 0.16)
            q = (p[0] + math.cos(ang) * length + K * 2, p[1] + math.sin(ang) * length)
            stroke(hd, [(p[0] + K * 2, p[1] + K * 2), q], K * 0.8, 0.2, color=INK[:3] + (170,))
        base_img.paste(hatch, (0, 0), ImageChops.multiply(hatch.getchannel("A"), mask))

        d = ImageDraw.Draw(base_img, "RGBA")
        # 4) ink wedges in the crevices under the crest and at a spur.
        wedges = 2 if self.kind == "rocky" else 1
        for i in range(wedges + (1 if h > 70 * K else 0)):
            t = rng.uniform(0.08, 0.45) if i == 0 else rng.uniform(0.3, 0.7)
            p = point_on(self.crest, t)
            size = h * rng.uniform(0.07, 0.12) * (1.0 if i == 0 else 0.7)
            a = (p[0] - size * 0.15, p[1] - size * 0.1)
            b = (p[0] - size * 0.95, p[1] + size * rng.uniform(0.25, 0.5))
            c = (p[0] - size * 0.25, p[1] + size * rng.uniform(0.9, 1.3))
            d.polygon([a, b, (lerp(b[0], c[0], 0.5) + size * 0.12, lerp(b[1], c[1], 0.5)), c], fill=INK)
        # 5) a spur on the lit face: a broken line with a small wedge
        if self.kind == "rocky" and h > 50 * K:
            s = point_on(self.right, rng.uniform(0.25, 0.4))
            e = (s[0] - h * 0.05, s[1] + h * rng.uniform(0.28, 0.38))
            stroke(d, [s, (lerp(s[0], e[0], 0.5) + K, lerp(s[1], e[1], 0.5)), e], K * 1.3, K * 0.3)
            d.polygon([(s[0] - K, s[1] + K * 2), (s[0] - h * 0.07, s[1] + h * 0.1), (s[0] - K * 1.5, s[1] + h * 0.12)], fill=INK)

        # 6) the crest itself: a broken line, firm at the top, gone before the foot.
        crest = smooth(self.crest)
        cut = int(len(crest) * rng.uniform(0.62, 0.78))
        stroke(d, crest[:cut // 2], K * 1.6, K * 1.1, curve=False)
        stroke(d, crest[cut // 2 + 2:cut], K * 1.0, K * 0.2, curve=False)

        # 7) lit face: a few short ticks along the right ridge.
        for i in range(2 if self.kind == "rocky" else 1):
            p = point_on(self.right, rng.uniform(0.45, 0.8))
            q = (p[0] - h * 0.04, p[1] + h * rng.uniform(0.05, 0.1))
            stroke(d, [(p[0] - K * 2, p[1] + K), q], K * 0.9, 0.2, color=INK[:3] + (200,))

        # 8) the contour: heavy on the shadow ridge, lighter on the lit one, tapering to the base.
        stroke(d, self.left, K * 1.0, K * 2.6, swell=K * 0.6, curve=False, jitter=rng)
        stroke(d, self.right, K * 2.2, K * 0.5, curve=False, jitter=rng)
        # 9) the base: no closed bottom - a short ground flick out of each foot and a few dashes.
        lx, rx = self.lb[0], self.rb[0]
        stroke(d, [(lx + K * 2, self.base), (lx - h * 0.18, self.base + K * 0.5)], K * 1.0, 0.2)
        stroke(d, [(rx - K * 2, self.base), (rx + h * 0.12, self.base + K)], K * 0.9, 0.2)
        for i in range(3):
            x = lerp(lx, rx, rng.uniform(0.15, 0.85))
            y = self.base + rng.uniform(1, 3) * K
            stroke(d, [(x, y), (x + rng.uniform(4, 9) * K, y + K * 0.3)], K * 0.7, 0.2, color=INK[:3] + (150,))


def ground(rng):
    """The hex set's snow ground: pale, with soft blotches and a paper grain."""
    img = Image.new("RGB", (N, N), SNOW)
    blot = Image.new("L", (N, N), 0)
    bd = ImageDraw.Draw(blot)
    for _ in range(60):
        x, y = rng.uniform(0, N), rng.uniform(0, N)
        w, h = rng.uniform(20, 70) * K / 4, rng.uniform(6, 18) * K / 4
        bd.ellipse([x - w, y - h, x + w, y + h], fill=int(rng.uniform(60, 160)))
    blot = blot.filter(ImageFilter.GaussianBlur(K * 3))
    img.paste(Image.new("RGB", (N, N), GROUND_DARK), (0, 0), blot)
    grain = Image.effect_noise((N // 2, N // 2), 22).resize((N, N), Image.BILINEAR)
    img = ImageChops.multiply(img, Image.merge("RGB", [grain.point(lambda v: 235 + (v - 128) // 10)] * 3).point(lambda v: min(255, v + 12)))
    return img


def compose(variant):
    """One big mountain with two smaller ones before it, all inside the hex."""
    rng = random.Random(1000 + variant)
    img = ground(rng)
    c = N / 2
    layouts = [
        # (cx, base, height, half, kind, lean) in tile px, drawn back to front
        [(128, 176, 118, 66, "rocky", 0.05), (78, 208, 52, 34, "rocky", -0.1), (176, 200, 44, 30, "smooth", 0.1)],
        [(136, 172, 110, 70, "rocky", -0.12), (182, 206, 56, 34, "rocky", 0.05), (86, 214, 36, 26, "smooth", 0.0)],
        [(120, 178, 124, 62, "rocky", 0.12), (170, 196, 62, 38, "rocky", -0.05), (74, 210, 40, 28, "smooth", 0.05), (136, 222, 26, 22, "smooth", 0.0)],
    ][variant % 3]
    for cx, base, h, half, kind, lean in layouts:
        m = Mountain(cx * K, base * K, h * K, half * K, rng, kind, lean)
        m.paint(img, rng)
    return img


def hex_mask(size, flat=True):
    m = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(m)
    r = size / 2
    pts = []
    for i in range(6):
        a = math.radians(60 * i + (0 if flat else 30))
        pts.append((size / 2 + r * math.cos(a), size / 2 + r * math.sin(a)))
    d.polygon(pts, fill=255)
    return m


if __name__ == "__main__":
    out = sys.argv[1]
    os.makedirs(out, exist_ok=True)
    tiles = []
    for v in range(3):
        big = compose(v)
        tile = big.resize((256, 256), Image.LANCZOS)
        tile.save(os.path.join(out, f"ink_mountains_{v}.png"))
        big.resize((512, 512), Image.LANCZOS).save(os.path.join(out, f"ink_mountains_{v}_x2.png"))
        tiles.append(tile)
    # sheet: the three tiles at 2x, clipped to hexes, on a dark backdrop
    sheet = Image.new("RGB", (3 * 520 + 20, 540), (60, 64, 70))
    mask = hex_mask(512)
    for i in range(3):
        big = Image.open(os.path.join(out, f"ink_mountains_{i}_x2.png"))
        sheet.paste(big, (20 + i * 520, 14), mask)
    sheet.save(os.path.join(out, "sheet.png"))
