"""The hex tile set in the owner's mountain manner: every tile, ground and icon of HexStarterTiles.

Coordinates are those of the 256 x 256 box the C# code centres on a hex (`u` = box pixels),
painted at SCALE times that size and reduced at the end. Terrain pictures are square and opaque
(the hex clips them); feature, icon and tall pictures are transparent.
"""

from __future__ import annotations

import math

import numpy as np
from scipy import ndimage as ndi

from paint import ID, Canvas, fbm, finish, noise, paint, smoothstep

SCALE = 2
BOX = 256
RIDGES = True  # mountains drawn as a ridge and its spurs (False: the faceted cones)
TALL_ROOM = 128  # box pixels of the tall picture above the box (the hex's top is at 17 or 0)
MARGIN = 48  # ground rows beyond the box so heights never uncover the paper


def u(v: float) -> float:
    return v * SCALE


# ---------------------------------------------------------------------------- ground fields


class Field:
    """A ground height field in box coordinates, a little larger than the box."""

    def __init__(self, cv: Canvas, top: float = 0.0):
        self.cv = cv
        self.rows = int(u(BOX + 2 * MARGIN))
        self.y0 = u(top - MARGIN)
        y, x = np.mgrid[0:self.rows, 0:cv.w].astype(np.float32)
        self.X = x / SCALE
        self.Y = y / SCALE - MARGIN
        self.h = np.zeros((self.rows, cv.w), np.float32)
        self.mat = np.zeros((self.rows, cv.w), np.int16)
        self.body = np.zeros((self.rows, cv.w), bool)
        self.lift = np.zeros((self.rows, cv.w), np.float32)
        self.crease = np.zeros((self.rows, cv.w), np.float32)  # weight of the light pen on ridges

    def rng(self):
        return self.cv.rng

    def fill(self, material: str, relief: float = 1.5, sigma: float = 30):
        self.mat[:] = ID[material]
        self.h += fbm(self.rng(), self.h.shape, u(sigma)) * u(relief)

    def render(self, ground=True):
        hl = self.h
        oid = self.cv.terrain(hl, self.mat, y0=self.y0, ground=ground, creases=self.crease, body=self.body)
        return oid


def pyramid(f: Field, ax, ay, height, radius, facets, rng, power=1.5, squash=1.0):
    th0 = rng.uniform(0, 6.28)
    angs = th0 + np.arange(facets) * 6.2832 / facets + rng.uniform(-0.3, 0.3, facets)
    out = np.full(f.X.shape, -1e9, np.float32)
    for a in angs:
        k = 1 / (radius * rng.uniform(0.7, 1.3))
        out = np.maximum(out, k * ((f.X - ax) * np.cos(a) + (f.Y - ay) * np.sin(a) / squash))
    t = np.clip(1 - out, 0, None)
    return u(height) * t ** power


def massif(f: Field, peaks, rng, material="ice", power=1.25, ledges=False, rough=0.6, squash=0.6, spires=2, teeth_amount=0.09):
    """Faceted ice mountains like the owner's: sharp ridges, flat lit and shadowed faces."""
    mount = np.zeros_like(f.h)
    for (px, py, height, radius, facets) in peaks:
        mount = np.maximum(mount, pyramid(f, px, py, height, radius, facets, rng, power, squash))
        # spires on the shoulders: the owner's peaks are crowns of sharp points, not one cone
        for _ in range(spires):
            a = rng.uniform(-2.6, -0.5)
            d = rng.uniform(0.35, 0.6) * radius
            sx, sy = px + math.cos(a) * d, py + math.sin(a) * d * 0.5
            mount = np.maximum(mount, pyramid(f, sx, sy, height * rng.uniform(0.45, 0.7), radius * rng.uniform(0.25, 0.4),
                                              int(rng.integers(4, 6)), rng, power, squash))
    # serrated ridges: a ridged noise grows with the height, so crests break into teeth
    teeth = (1 - np.abs(noise(rng, f.h.shape, u(9)))) ** 2
    mount = mount * (0.9 + teeth_amount * teeth * np.clip(mount / u(30), 0, 1))
    gullies = noise(rng, f.h.shape, u(2.0), stretch=5, angle=0) * u(rough)
    f.h += mount + gullies * np.clip(mount / u(25), 0, 1)
    body = mount > np.clip(5 + 4 * noise(rng, f.h.shape, u(3)), 2, 10) * SCALE
    f.mat[body] = ID[material]
    f.body |= body
    f.crease[body] = 0.5
    if ledges:
        # Snow caught on ledges across the shadowed faces (the white bands of the owner's rock).
        band = np.sin(mount / u(7) + noise(rng, f.h.shape, u(3), stretch=3, angle=90) * 0.9)
        dash = noise(rng, f.h.shape, u(4), stretch=3, angle=90) > 0.3
        f.lift += np.where(body & (band > 0.9) & dash, 0.45, 0)
    return mount


def ridge_range(f: Field, rng, cx, by, height, half_width, material="ice", steep=1.9, seed_peaks=3):
    """A mountain built as artists draw one (tutorials of Jonathan Roberts, Clip Studio): a main
    ridge along the summits with saddles between them, spurs running down from the ridge towards
    the viewer and branching, each spur a sharp crest between a lit west face and a shadowed east
    face; the faces fall steeply; scree and snow lie at the foot."""
    segs = []  # (x0, y0, h0, x1, y1, h1, steepness)

    def chain(points, s):
        for (x0, y0, h0), (x1, y1, h1) in zip(points, points[1:]):
            segs.append((x0, y0, h0, x1, y1, h1, s))

    H = height
    # the main ridge, west to east, with the summit off the middle
    sx = cx + rng.uniform(-0.18, 0.18) * half_width
    ry = by - 0.32 * half_width
    peaks = [(sx, ry, H)]
    if seed_peaks >= 2:
        peaks.append((cx - rng.uniform(0.5, 0.65) * half_width, ry + rng.uniform(4, 10), H * rng.uniform(0.55, 0.72)))
    if seed_peaks >= 3:
        peaks.append((cx + rng.uniform(0.5, 0.65) * half_width, ry + rng.uniform(2, 9), H * rng.uniform(0.48, 0.66)))
    peaks.sort(key=lambda p: p[0])
    ridge = [(cx - half_width * 1.05, by + 2, 0.0)]
    for i, p in enumerate(peaks):
        if i > 0:
            q = peaks[i - 1]
            ridge.append(((p[0] + q[0]) / 2 + rng.uniform(-6, 6), (p[1] + q[1]) / 2 - 3, min(p[2], q[2]) * rng.uniform(0.5, 0.68)))
        ridge.append(p)
    ridge.append((cx + half_width * 1.05, by + 4, 0.0))
    chain(ridge, steep)
    # spurs down towards the viewer from the summits and the saddles, branching once
    for (px, py, ph) in ridge[1:-1]:
        for side in (-1, 1):
            if ph < H * 0.3:
                continue
            ang = math.radians(90 + side * rng.uniform(25, 55))  # 90 = straight south (down the screen)
            length = ph / steep * rng.uniform(1.4, 1.8)
            pts = [(px, py, ph)]
            x, y, h = px, py, ph
            n = 3
            for k in range(1, n + 1):
                a = ang + rng.uniform(-0.25, 0.25)
                x += math.cos(a) * length / n
                y += math.sin(a) * length / n * 0.8
                h = ph * (1 - k / n) ** 1.15
                pts.append((x, y, h))
            chain(pts, steep * 1.05)
            # a branch from the middle of the spur
            mx, my, mh = pts[1]
            b = ang + side * rng.uniform(0.4, 0.7)
            bl = mh / steep * 1.4
            chain([(mx, my, mh), (mx + math.cos(b) * bl * 0.6, my + math.sin(b) * bl * 0.5, mh * 0.45),
                   (mx + math.cos(b) * bl, my + math.sin(b) * bl * 0.8, 0.0)], steep * 1.1)
    X, Y = f.X, f.Y
    field = np.zeros_like(f.h)
    for (x0, y0, h0, x1, y1, h1, s) in segs:
        dx, dy = x1 - x0, y1 - y0
        L2 = dx * dx + dy * dy + 1e-6
        t = np.clip(((X - x0) * dx + (Y - y0) * dy) / L2, 0, 1)
        d = np.hypot(X - (x0 + t * dx), Y - (y0 + t * dy))
        hs = h0 + (h1 - h0) * t
        field = np.maximum(field, hs - s * d)
    field = np.clip(field, 0, None)
    # crests break into small teeth; the faces carry gullies
    teeth = (1 - np.abs(noise(rng, f.h.shape, u(3.5)))) ** 3
    field = field * (0.95 + 0.1 * teeth * np.clip(field / 30, 0, 1))
    gullies = noise(rng, f.h.shape, u(1.6), stretch=6, angle=0)
    field = field + gullies * 0.05 * np.clip(field / 25, 0, 1)
    mount = u(1) * field
    # scree: a few small rocks at the foot, in front
    for _ in range(int(rng.integers(5, 9))):
        rx = cx + rng.uniform(-0.9, 0.9) * half_width
        ry2 = by + rng.uniform(4, 14)
        r = rng.uniform(3, 6)
        rock = np.clip(r * 1.1 - 1.6 * np.hypot(X - rx, (Y - ry2) * 1.4), 0, None)
        mount = np.maximum(mount, u(1) * rock)
    f.h += mount
    body = mount > u(2.5)
    f.mat[body] = ID[material]
    f.body |= body
    f.crease[body] = 0.3
    return mount


def bumps(f: Field, centres, rng, material, body=True, sharp=1.0):
    """Rounded hills: soft domes with a ridge line where their two faces meet."""
    hill = np.zeros_like(f.h)
    for (cx, cy, height, rx, ry) in centres:
        d = ((f.X - cx) / rx) ** 2 + ((f.Y - cy) / ry) ** 2
        dome = np.clip(1 - d, 0, None) ** (0.55 * sharp)
        hill = np.maximum(hill, u(height) * dome)
    f.h += hill
    if body:
        m = hill > u(2.5)
        f.mat[m] = ID[material]
        f.body |= m
    return hill


# ---------------------------------------------------------------------------- standing shapes


def tree(cv: Canvas, x, foot, size, rng, palette="foliage", lobes=None, trunk=True, lift=0.78, stretch=1.0):
    """A broadleaf tree: a crown of a few lumpy spheres on a short trunk."""
    oid = cv.new_id()
    cv.cast(x + size * 0.32, foot + size * 0.02, size * 0.55, size * 0.22, strength=0.6, soft=u(2.5))
    if trunk:
        cv.cylinder(x, foot - size * (lift - 0.23), foot, max(u(1.3), size * 0.07), "trunk", oid, part=1)
    zbuf = np.full((cv.h, cv.w), -1e9, np.float32)
    cy = foot - size * lift
    n = lobes or int(rng.integers(3, 6))
    for i in range(n):
        a = rng.uniform(0, 6.28)
        rr = size * rng.uniform(0.28, 0.38)
        ox = math.cos(a) * size * 0.2
        oy = math.sin(a) * size * 0.14 * stretch
        cv.blob(x + ox, cy + oy, rr, palette, oid, part=2 + i, lumps=0.1, zbuf=zbuf, seed=int(rng.integers(1e9)), flat=stretch)
    cv.blob(x, cy - size * 0.08 * stretch, size * 0.4, palette, oid, part=2 + n, lumps=0.12, zbuf=zbuf, seed=int(rng.integers(1e9)), flat=stretch)
    return oid


def pine(cv: Canvas, x, foot, size, rng, palette="pine", tiers=None, snow=False):
    """A fir: stacked jagged cones, the lit side to the west."""
    oid = cv.new_id()
    cv.cast(x + size * 0.28, foot + size * 0.01, size * 0.42, size * 0.16, strength=0.6, soft=u(2.0))
    cv.cylinder(x, foot - size * 0.2, foot, max(u(1.1), size * 0.05), "trunk", oid, part=1)
    n = tiers or int(rng.integers(3, 5))
    top = foot - size
    for i in range(n):
        t0 = i / n
        t1 = (i + 1) / n
        tier_top = top + (foot - size * 0.12 - top) * (t0 * 0.85)
        tier_base = top + (foot - size * 0.12 - top) * t1
        half = size * (0.14 + 0.24 * t1)
        cv.cone(x, tier_base, tier_top, half, palette, oid, part=2 + i, jag=u(1.5), seed=int(rng.integers(1e9)), droop=u(1.0))
    return oid


def house(cv: Canvas, x, foot, w, hgt, depth, rng, roof="roof", wall="plaster", oid=None, door=True):
    """A gable-fronted house seen from the south: lit west roof plane, shadowed east plane."""
    oid = oid or cv.new_id()
    cv.cast(x + w * 0.45, foot - depth * 0.15, w * 0.75, depth * 0.45, strength=0.6, soft=u(2))
    rh = w * 0.42
    o = w * 0.08
    eave = foot - hgt
    apex = (x, eave - rh)
    back = (x, eave - rh - depth)
    cv.face([(x - w / 2 - o, eave + o * 0.6), apex, back, (x - w / 2 - o, eave - depth + o * 0.6)], roof, (-0.8, -0.1, 1), oid, part=1)
    cv.face([apex, (x + w / 2 + o, eave + o * 0.6), (x + w / 2 + o, eave - depth + o * 0.6), back], roof, (0.8, -0.1, 1), oid, part=2)
    cv.face([(x - w / 2, foot), (x - w / 2, eave), apex, (x + w / 2, eave), (x + w / 2, foot)], wall, (0, 1, 0.35), oid, part=3)
    if door:
        dw = w * 0.22
        cv.face([(x - dw / 2, foot), (x - dw / 2, foot - hgt * 0.62), (x + dw / 2, foot - hgt * 0.62), (x + dw / 2, foot)],
                "dark", (0, 1, 0.3), oid, part=4)
    return oid


def tower(cv: Canvas, x, foot, half, hgt, rng, roof=True, material="stone", oid=None):
    oid = oid or cv.new_id()
    cv.cast(x + half * 1.1, foot - half * 0.2, half * 1.6, half * 0.7, strength=0.6, soft=u(2))
    cv.cylinder(x, foot - hgt, foot, half, material, oid, part=1)
    if roof:
        cv.cone(x, foot - hgt + u(1), foot - hgt - half * 2.3, half * 1.3, "roofslate", oid, part=2, jag=0, seed=int(rng.integers(1e9)))
    else:
        tw = half * 2 / 5
        for i in range(3):
            left = x - half + i * 2 * tw
            cv.face([(left, foot - hgt), (left, foot - hgt - tw), (left + tw, foot - hgt - tw), (left + tw, foot - hgt)],
                    material, (-0.2 + 0.2 * i, 1, 0.3), oid, part=3 + i)
    win = half * 0.28
    cv.face([(x - win, foot - hgt * 0.62), (x - win, foot - hgt * 0.62 - win * 2.4), (x + win, foot - hgt * 0.62 - win * 2.4),
             (x + win, foot - hgt * 0.62)], "dark", (0, 1, 0.3), oid, part=9)
    return oid


def wall_box(cv: Canvas, x0, x1, foot, hgt, depth, material, oid=None, crenels=True):
    """A block (a keep, a wall): the lit top and the south face."""
    oid = oid or cv.new_id()
    cv.cast((x0 + x1) / 2 + (x1 - x0) * 0.25, foot - depth * 0.3, (x1 - x0) * 0.6, depth * 0.6, strength=0.6, soft=u(2))
    cv.face([(x0, foot - hgt), (x0, foot - hgt - depth), (x1, foot - hgt - depth), (x1, foot - hgt)], material, (-0.1, -0.2, 1), oid, part=1)
    cv.face([(x0, foot), (x0, foot - hgt), (x1, foot - hgt), (x1, foot)], material, (0, 1, 0.3), oid, part=2)
    if crenels:
        step = u(5)
        k = 0
        xx = x0
        while xx + step * 0.6 < x1:
            cv.face([(xx, foot - hgt), (xx, foot - hgt - u(3.5)), (xx + step * 0.6, foot - hgt - u(3.5)), (xx + step * 0.6, foot - hgt)],
                    material, (0, 1, 0.3), oid, part=3 + k)
            xx += step
            k += 1
    return oid


def tufts(cv: Canvas, rng, count, area, material_shift=-0.35, ink=0.0, spread=96):
    """Small brush ticks of grass: a few darker strokes, here and there a pen tick."""
    for _ in range(count):
        a = rng.uniform(0, 6.28)
        r = spread * math.sqrt(rng.uniform(0, 1))
        x, y = u(128 + r * math.cos(a)), u(128 + r * math.sin(a) * 0.9)
        if not area(x, y):
            continue
        hgt = u(rng.uniform(3, 5.5))
        for k in (-1, 0, 1):
            pts = [(x + k * u(1.4), y), (x + k * u(2.2) + u(0.4), y - hgt * (1 - 0.25 * abs(k)))]
            m = cv.polygon_mask([(pts[0][0] - u(0.7), pts[0][1]), (pts[1][0], pts[1][1]), (pts[0][0] + u(0.7), pts[0][1])])
            cv.shade[m] += material_shift
        if ink and rng.uniform() < ink:
            cv.line([(x - u(1.6), y), (x - u(2.4), y - hgt * 0.9)], 1.0)
            cv.line([(x + u(0.6), y), (x + u(1.6), y - hgt)], 1.0)


def waves(cv: Canvas, rng, count, spread=100, length=(8, 14), ink=0.4, lift=0.6):
    for _ in range(count):
        a = rng.uniform(0, 6.28)
        r = spread * math.sqrt(rng.uniform(0, 1))
        x, y = u(128 + r * math.cos(a)), u(128 + r * math.sin(a) * 0.9)
        L = u(rng.uniform(*length))
        pts = [(x - L, y + u(1)), (x - L / 2, y - u(1.6)), (x, y), (x + L / 2, y - u(1.6)), (x + L, y + u(1))]
        band = cv.polygon_mask([(p[0], p[1] - u(0.9)) for p in pts] + [(p[0], p[1] + u(0.9)) for p in reversed(pts)])
        cv.shade[band] += lift
        if rng.uniform() < ink:
            cv.line(pts[1:4], 1.1)


# ---------------------------------------------------------------------------- tiles


def canvas_for(kind: str, seed: int, tall=False, icon=False) -> tuple[Canvas, float]:
    top = TALL_ROOM if tall else 0
    cv = Canvas(int(u(BOX)), int(u(BOX + top)), seed)
    return cv, top


def ground_of(name: str, f: Field, rng, variant: int):
    """The bare ground of every terrain (its transition band shows the same)."""
    if name in ("grass", "live-forest", "live-hills"):
        f.fill("grass", 1.4)
    elif name in ("forest", "old-forest"):
        f.fill("grass", 1.2)
        f.lift -= 0.25
    elif name in ("pines", "live-pines"):
        f.fill("meadow", 1.2)
        f.lift -= 0.3
    elif name == "jungle":
        f.fill("jungle", 1.6)
        f.lift -= 0.35
    elif name == "farmland":
        f.fill("meadow", 1.0)
    elif name == "hills":
        f.fill("grass", 1.4)
    elif name in ("mountains", "peaks", "live-mountains"):
        f.fill("snowfield", 1.6)
    elif name == "desert":
        f.fill("sand", 1.0)
    elif name == "swamp":
        f.fill("swamp", 1.0)
    elif name == "snow":
        f.fill("snowfield", 1.2)
    elif name == "canyon":
        f.fill("canyon", 1.2)
    elif name == "cliffs":
        f.fill("stone", 1.4)
    elif name == "volcano":
        f.fill("basalt", 1.6)
    elif name == "sea":
        f.fill("sea", 0.6, 40)
    elif name == "shallows":
        f.fill("shallow", 0.6, 40)
    elif name == "lake":
        f.fill("lake", 0.6, 40)
    else:
        f.fill("grass", 1.4)


def paint_terrain(name: str, variant: int, ground_only=False, tall=False):
    seed = 7919 * (variant + 1) + sum(map(ord, name)) * 131 + (17 if ground_only else 0)
    cv, top = canvas_for(name, seed, tall=tall)
    rng = cv.rng
    f = Field(cv, top)
    ground_of(name, f, rng, variant)
    oy = u(top)  # screen offset of the box
    standing = []  # (foot_y, draw)

    if ground_only:
        pass
    elif name == "hills":
        n = 2 + variant % 2
        centres = []
        for i in range(n):
            centres.append((rng.uniform(72, 184) if n > 2 else 96 + 64 * i + rng.uniform(-10, 10), rng.uniform(110, 170),
                            rng.uniform(28, 36), rng.uniform(38, 48), rng.uniform(22, 28)))
        bumps(f, centres, rng, "grass")
    elif name == "mountains":
        peaks = [(128 + rng.uniform(-8, 8), 176, rng.uniform(112, 124), 74, 7)]
        if variant != 1:
            peaks.append((76 + rng.uniform(-6, 6), 168, rng.uniform(66, 80), 50, 6))
        if variant != 2:
            peaks.append((182 + rng.uniform(-6, 6), 172, rng.uniform(70, 86), 52, 6))
        peaks.append((128 + rng.uniform(-30, 30), 148, rng.uniform(56, 70), 40, 5))
        if RIDGES:
            ridge_range(f, rng, 128, 178, rng.uniform(92, 102), 84, steep=1.4, seed_peaks=3 - variant % 2)
        else:
            massif(f, peaks, rng)
    elif name == "peaks":
        if RIDGES:
            # tall and narrow, but inside the hex like the mountains (the owner, 2026-10-22)
            ridge_range(f, rng, 128, 180, rng.uniform(128, 138), 74, steep=2.0, seed_peaks=2 + variant % 2)
        else:
            peaks = [(128 + rng.uniform(-6, 6), 206, rng.uniform(268, 290), 84, 7),
                     (74 + rng.uniform(-6, 6), 200, rng.uniform(170, 200), 56, 6),
                     (184 + rng.uniform(-6, 6), 204, rng.uniform(180, 215), 58, 6),
                     (104 + rng.uniform(-10, 10), 176, rng.uniform(130, 160), 44, 5)]
            massif(f, peaks, rng, power=1.15, squash=0.55)
    elif name == "volcano":
        cone = pyramid(f, 128, 206, 214, 100, 9, rng, power=1.2, squash=0.7)
        crater = np.clip(1 - (((f.X - 128) / 24) ** 2 + ((f.Y - 206) / 14) ** 2), 0, None)
        cone = np.maximum(cone - u(22) * crater ** 0.6, 0)
        f.h += cone
        body = cone > u(4)
        f.mat[body] = ID["basalt"]
        f.body |= body
        f.crease[body] = 0.4
        # lava runs: narrow bright streaks down the south face
        for i in range(3 if variant == 0 else 2):
            ang = rng.uniform(0.35, 2.8)
            along = (f.X - 128) * math.cos(ang) + (f.Y - 206) * math.sin(ang)
            across = -(f.X - 128) * math.sin(ang) + (f.Y - 206) * math.cos(ang)
            wig = noise(rng, f.h.shape, u(4)) * 3
            run = (np.abs(across + wig) < 2.4 + along * 0.02) & (along > 16) & (along < 70) & body
            f.mat[run] = ID["lava"]
        f.mat[(crater > 0.2) & body] = ID["lava"]
    elif name == "canyon":
        # A gorge from the top edge to the bottom edge (pointy pictures are turned by the code).
        wig = np.sin(f.Y / 34 + rng.uniform(0, 6)) * 10 + noise(rng, f.h.shape, u(10)) * 4
        d = np.abs(f.X - 128 - wig)
        plateau = smoothstep(14, 34, d)
        f.h += u(26) * plateau
        bed = d < 13
        f.mat[bed] = ID["sand"]
        f.body |= plateau > 0.5
        f.crease[plateau > 0.3] = 0.4
        strata = (np.sin(f.h / u(3.2)) > 0.6) & (plateau > 0.08) & (plateau < 0.92)
        f.lift += np.where(strata, -0.25, 0)
    elif name == "cliffs":
        wig = noise(rng, (1, f.h.shape[1]), u(8))[0] * 5
        edge = 112 + wig[None, :]
        high = smoothstep(edge + 6, edge - 6, f.Y)
        f.h += u(34) * high
        f.body |= high > 0.5
        f.crease[high > 0.3] = 0.4
        strata = (np.sin(f.h / u(4)) > 0.55) & (high > 0.06) & (high < 0.94)
        f.lift += np.where(strata, -0.3, 0)
        low = f.Y > edge + 4
        f.mat[low] = ID["grass"]
    elif name == "desert":
        phase = rng.uniform(0, 6)
        k = rng.uniform(0.07, 0.09)
        warp = noise(rng, f.h.shape, u(14)) * 6
        s = (f.Y + 0.35 * f.X + warp) * k + phase
        dune = (1 - np.abs(np.sin(s))) ** 2
        f.h += u(5) * dune
        f.crease[dune > 0.3] = 0.22
    elif name == "snow":
        warp = noise(rng, f.h.shape, u(14)) * 8
        s = (f.Y - 0.25 * f.X + warp) * 0.07 + rng.uniform(0, 6)
        drift = (1 - np.abs(np.sin(s))) ** 3
        f.h += u(4) * drift
        f.crease[drift > 0.3] = 0.2
    elif name == "swamp":
        pools = fbm(rng, f.h.shape, u(16)) + 0.25
        water = pools > 0.35
        f.mat[water] = ID["bog"]
        f.h[water] = f.h[water] * 0.2 - u(1)
    elif name in ("sea", "shallows", "lake"):
        pass

    oid = f.render()
    lift_screen = np.zeros_like(cv.shade)
    # lift to screen: re-project the ground lift through the row buffer
    rows = np.clip(cv.row.astype(int), 0, f.rows - 1)
    cols = np.broadcast_to(np.arange(cv.w), cv.shade.shape)
    lift_screen = f.lift[rows, cols]
    cv.shade += np.where(cv.row > -1e5, lift_screen, 0)
    cv.ground[cv.body] = False

    def inside(x, y, radius=96):
        dx, dy = x / SCALE - 128, (y - oy) / SCALE - 128
        return dx * dx + (dy / 0.9) ** 2 < radius * radius

    def scatter(count, radius, min_dist, ymin=None):
        pts = []
        for _ in range(count * 40):
            if len(pts) >= count:
                break
            a = rng.uniform(0, 6.28)
            r = radius * math.sqrt(rng.uniform(0, 1))
            x, y = 128 + r * math.cos(a), 128 + r * math.sin(a) * 0.86
            if ymin is not None and y < ymin:
                continue
            if all((x - p[0]) ** 2 + (y - p[1]) ** 2 >= min_dist ** 2 for p in pts):
                pts.append((x, y))
        return sorted(pts, key=lambda p: p[1])

    if not ground_only:
        if name in ("grass", "live-forest", "live-pines", "live-hills", "farmland", "hills", "cliffs"):
            tufts(cv, rng, 30 if name == "grass" else 14, lambda x, y: cv.mat[int(min(y, cv.h - 1)), int(min(x, cv.w - 1))] in (ID["grass"], ID["meadow"]),
                  ink=0.25 if name == "grass" else 0.1)
        if name == "farmland":
            farmland(cv, rng, variant, oy)
        if name == "forest":
            for (x, y) in scatter(12, 92, 28, ymin=66):
                tree(cv, u(x), oy + u(y + 10), u(rng.uniform(42, 52)), rng)
        if name == "pines":
            for (x, y) in scatter(14, 94, 22, ymin=70):
                pine(cv, u(x), oy + u(y + 10), u(rng.uniform(44, 54)), rng)
        if name == "jungle":
            for (x, y) in scatter(17, 94, 22, ymin=64):
                if rng.uniform() < 0.25:
                    fern(cv, u(x), oy + u(y + 6), u(rng.uniform(18, 24)), rng)
                else:
                    tree(cv, u(x), oy + u(y + 10), u(rng.uniform(42, 52)), rng, palette="jungle", lobes=6)
        if name == "swamp":
            for (x, y) in scatter(16, 96, 14):
                xs, ys = u(x), oy + u(y)
                if cv.mat[int(ys), int(xs)] == ID["bog"]:
                    continue
                reeds(cv, xs, ys, rng)
        if name == "old-forest":
            # The owner (2026-10-22): the old forest was too big for a hex map: old trees are now
            # only somewhat taller than the forest's and stay within the hex.
            for (x, y) in scatter(8, 84, 34, ymin=96):
                tree(cv, u(x), oy + u(y + 12), u(rng.uniform(60, 72)), rng, lobes=6, lift=0.9, stretch=1.2)
        if name in ("sea", "shallows", "lake"):
            waves(cv, rng, {"sea": 12, "shallows": 8, "lake": 7}[name], ink={"sea": 0.85, "shallows": 0.6, "lake": 0.6}[name])
        if name == "volcano":
            smoke(cv, u(128), oy + u(206 - 214 + 4), rng)
    return cv


def farmland(cv: Canvas, rng, variant, oy):
    """Strips of wheat, young green and turned soil, furrowed, with hedges between them."""
    ang = math.radians(rng.uniform(-35, 35))
    ca, sa = math.cos(ang), math.sin(ang)
    X, Y = cv.X / SCALE - 128, (cv.Y - oy) / SCALE - 128
    v = -X * sa + Y * ca
    along = X * ca + Y * sa
    edges = [-160]
    while edges[-1] < 160:
        edges.append(edges[-1] + rng.uniform(30, 48))
    kinds = ["wheat", "meadow", "soil", "wheat", "grass"]
    start = int(rng.integers(0, len(kinds)))
    for i in range(len(edges) - 1):
        m = (v >= edges[i]) & (v < edges[i + 1]) & (cv.row > -1e5)
        kind = kinds[(start + i) % len(kinds)]
        cv.mat[m] = ID[kind]
        if kind in ("wheat", "soil"):
            furrow = np.sin(along * 0.9) > 0.55
            cv.shade[m & furrow] -= 0.35
    # hedges along the field edges
    for e in edges[1:-1]:
        for t in np.arange(-150, 150, 9):
            x = 128 + t * ca - e * sa
            y = 128 + t * sa + e * ca
            if (x - 128) ** 2 + ((y - 128) / 0.9) ** 2 > 104 ** 2 or rng.uniform() < 0.55:
                continue
            oid = cv.new_id()
            cv.cast(u(x + 3), oy + u(y + 1), u(4), u(2), strength=0.4, soft=u(1.5))
            cv.blob(u(x), oy + u(y - 2), u(rng.uniform(2.8, 3.8)), "foliage", oid, lumps=0.15, seed=int(rng.integers(1e9)), weight=0.45)


def fern(cv: Canvas, x, foot, size, rng):
    oid = cv.new_id()
    cv.cast(x + size * 0.2, foot, size * 0.5, size * 0.2, strength=0.5, soft=u(1.5))
    for k in range(5):
        a = -math.pi / 2 + (k - 2) * 0.55 + rng.uniform(-0.12, 0.12)
        tipx, tipy = x + math.cos(a) * size, foot + math.sin(a) * size * 0.9
        nx, ny = -math.sin(a) * size * 0.2, math.cos(a) * size * 0.2
        mx, my = (x + tipx) / 2, (foot + tipy) / 2
        cv.face([(x, foot), (mx + nx, my + ny), (tipx, tipy), (mx - nx, my - ny)], "jungle", (math.cos(a), -0.3, 1), oid, part=1 + k, weight=0.7)


def reeds(cv: Canvas, x, foot, rng):
    for k in range(rng.integers(3, 6)):
        dx = rng.uniform(-u(3), u(3))
        hgt = u(rng.uniform(7, 12))
        cv.line([(x + dx, foot), (x + dx + rng.uniform(-u(1.2), u(1.2)), foot - hgt)], 0.9)
    oid = cv.new_id()
    cv.cast(x + u(2), foot, u(4), u(1.6), strength=0.4, soft=u(1))


def cactus(cv: Canvas, x, foot, rng):
    oid = cv.new_id()
    cv.cast(x + u(5), foot, u(8), u(3), strength=0.5, soft=u(1.5))
    cv.cylinder(x, foot - u(16), foot, u(2.6), "pine", oid, part=1)
    cv.cylinder(x - u(5), foot - u(11), foot - u(5), u(1.6), "pine", oid, part=2)
    cv.cylinder(x + u(5), foot - u(13), foot - u(7), u(1.6), "pine", oid, part=3)


def smoke(cv: Canvas, x, y, rng):
    oid = cv.new_id()
    zbuf = np.full((cv.h, cv.w), -1e9, np.float32)
    for i in range(5):
        cv.blob(x + u(4 * i) + rng.uniform(-u(3), u(3)), y - u(14 * i), u(9 + 3 * i), "smoke", oid, part=1 + i,
                lumps=0.18, zbuf=zbuf, seed=int(rng.integers(1e9)), weight=0.6)


# ---------------------------------------------------------------------------- features


def paint_feature(name: str, variant: int):
    seed = 104729 * (variant + 1) + sum(map(ord, name)) * 17
    cv = Canvas(int(u(BOX)), int(u(BOX)), seed)
    rng = cv.rng
    C = 128
    if name == "village":
        spots = [(C - 40, C + 12, 42), (C + 38, C + 6, 44), (C - 2, C + 44, 46)]
        if variant == 1:
            spots = [(C - 44, C + 22, 40), (C + 4, C + 2, 38), (C + 44, C + 30, 42), (C - 4, C + 52, 36)]
        for (x, y, w) in sorted(spots, key=lambda p: p[1]):
            house(cv, u(x), u(y), u(w), u(w * 0.55), u(w * 0.45), rng)
    elif name == "city":
        wall_box(cv, u(C - 74), u(C + 74), u(C - 6), u(9), u(6), "stone", crenels=True)
        for (x, y, w) in [(C - 46, C + 12, 26), (C - 14, C + 4, 24), (C + 40, C + 12, 26)]:
            house(cv, u(x), u(y), u(w), u(w * 0.6), u(w * 0.5), rng)
        tower(cv, u(C + 14 + rng.uniform(-4, 4)), u(C + 10), u(9), u(52), rng, roof=True)
        for (x, y, w) in [(C - 34, C + 40, 28), (C + 4, C + 46, 30), (C + 42, C + 40, 26)]:
            house(cv, u(x), u(y), u(w), u(w * 0.6), u(w * 0.5), rng, roof="roof" if rng.uniform() < 0.7 else "roofslate")
        wall_box(cv, u(C - 78), u(C + 78), u(C + 58), u(10), u(4), "stone", crenels=True)
    elif name == "castle":
        wall_box(cv, u(C - 44), u(C + 44), u(C + 34), u(36), u(30), "stone", crenels=True)
        tower(cv, u(C - 46), u(C + 44), u(12), u(62), rng, roof=variant == 0)
        tower(cv, u(C + 46), u(C + 44), u(12), u(62), rng, roof=True)
        oid = cv.new_id()
        cv.line([(u(C), u(C - 32)), (u(C), u(C - 62))], 1.4)
        cv.face([(u(C + 1), u(C - 62)), (u(C + 22), u(C - 56)), (u(C + 1), u(C - 50))], "flag", (-0.3, 0.4, 1), oid, part=1, weight=0.6)
    elif name == "tower":
        tower(cv, u(C + rng.uniform(-6, 6)), u(C + 44), u(15), u(100), rng, roof=variant == 0)
    elif name == "port":
        oid = cv.new_id()
        water = cv.ellipse_mask(u(C + 40), u(C + 34), u(56), u(24))
        cv._put(water, ID["sea"], 0.5 + 0.0 * cv.X, oid, 0, weight=0.6)
        waves_local(cv, rng, u(C + 40), u(C + 34))
        pid = cv.new_id()
        cv.face([(u(C + 2), u(C + 14)), (u(C + 2), u(C + 4)), (u(C + 78), u(C + 4)), (u(C + 78), u(C + 14))], "wood", (0, -0.2, 1), pid, part=1)
        for k in range(6):
            x = C + 8 + k * 13
            cv.face([(u(x), u(C + 22)), (u(x), u(C + 14)), (u(x + 2.5), u(C + 14)), (u(x + 2.5), u(C + 22))], "wood", (0, 1, 0.3), pid, part=2 + k)
        bid = cv.new_id()
        cv.face([(u(C + 30), u(C + 34)), (u(C + 76), u(C + 34)), (u(C + 66), u(C + 46)), (u(C + 40), u(C + 46))], "wood", (0, 1, 0.4), bid, part=1)
        cv.line([(u(C + 53), u(C + 34)), (u(C + 53), u(C - 6))], 1.3)
        cv.face([(u(C + 54), u(C - 4)), (u(C + 74), u(C + 28)), (u(C + 54), u(C + 28))], "cloth", (0.3, 0.6, 1), bid, part=2)
        house(cv, u(C - 40), u(C + 18), u(30), u(17), u(13), rng)
        house(cv, u(C - 8), u(C - 4), u(26), u(15), u(12), rng)
    elif name == "camp":
        for (x, y, w) in [(C - 34, C + 26, 46), (C + 30, C + 18, 42)]:
            oid = cv.new_id()
            cv.cast(u(x + w * 0.4), u(y - 2), u(w * 0.6), u(w * 0.25), strength=0.55, soft=u(2))
            cv.face([(u(x - w / 2), u(y)), (u(x), u(y - w * 0.8)), (u(x), u(y))], "cloth", (-0.8, 0.4, 0.6), oid, part=1)
            cv.face([(u(x), u(y)), (u(x), u(y - w * 0.8)), (u(x + w / 2), u(y))], "cloth", (0.8, 0.4, 0.6), oid, part=2)
            cv.face([(u(x - 6), u(y)), (u(x), u(y - w * 0.42)), (u(x + 6), u(y))], "dark", (0, 1, 0.3), oid, part=3)
        oid = cv.new_id()
        cv.line([(u(C - 10), u(C + 48)), (u(C + 10), u(C + 42))], 2.0)
        cv.line([(u(C - 10), u(C + 42)), (u(C + 10), u(C + 48))], 2.0)
        cv.face([(u(C - 7), u(C + 44)), (u(C - 5), u(C + 32)), (u(C), u(C + 24)), (u(C + 5), u(C + 32)), (u(C + 7), u(C + 44))],
                "fire", (-0.2, 0.3, 1), oid, part=1, weight=0.5)
    elif name == "ruins":
        for i in range(5):
            x = C - 60 + i * 30 + rng.uniform(-4, 4)
            hgt = rng.uniform(14, 52)
            oid = cv.new_id()
            cv.cast(u(x + 9), u(C + 38), u(12), u(4), strength=0.55, soft=u(1.5))
            cv.face([(u(x - 7), u(C + 38)), (u(x - 7), u(C + 38 - hgt)), (u(x - 2), u(C + 38 - hgt - rng.uniform(2, 8))),
                     (u(x + 7), u(C + 38 - hgt + rng.uniform(0, 6))), (u(x + 7), u(C + 38))], "stone", (-0.3, 1, 0.35), oid, part=1)
        for i in range(7):
            oid = cv.new_id()
            x, y = C + rng.uniform(-70, 60), C + rng.uniform(40, 56)
            cv.blob(u(x), u(y), u(rng.uniform(3.5, 6)), "stone", oid, lumps=0.25, flat=0.7, seed=int(rng.integers(1e9)))
    elif name == "cave":
        f = Field(cv, 0)
        mound = np.clip(1 - (((f.X - C) / 70) ** 2 + ((f.Y - (C + 40)) / 40) ** 2), 0, None) ** 0.7
        mound = mound * (1 + 0.2 * noise(rng, f.h.shape, u(6)))
        f.h += u(64) * mound
        body = mound > 0.04
        f.mat[body] = ID["stone"]
        f.body |= body
        f.crease[body] = 0.35
        cv.terrain(f.h, f.mat, y0=f.y0, ground=False, creases=f.crease, body=f.body)
        cv.cast(u(C + 22), u(C + 52), u(76), u(16), strength=0.5)
        oid = cv.new_id()
        cv.face([(u(C - 20), u(C + 44)), (u(C - 18), u(C + 18)), (u(C - 8), u(C + 6)), (u(C + 8), u(C + 6)), (u(C + 18), u(C + 18)),
                 (u(C + 20), u(C + 44))], "dark", (0, 1, 0.3), oid, part=1)
    elif name == "temple":
        wall_box(cv, u(C - 64), u(C + 64), u(C + 44), u(8), u(14), "stone", crenels=False)
        for i in range(5):
            x = C - 50 + i * 25
            cv.cylinder(u(x), u(C - 16), u(C + 30), u(5), "plaster", cv.new_id(), part=1)
        oid = cv.new_id()
        cv.face([(u(C - 62), u(C - 14)), (u(C - 62), u(C - 26)), (u(C + 62), u(C - 26)), (u(C + 62), u(C - 14))], "plaster", (0, 1, 0.35), oid, part=1)
        cv.face([(u(C - 68), u(C - 26)), (u(C), u(C - 58)), (u(C + 68), u(C - 26))], "plaster", (0, 1, 0.6), oid, part=2)
    elif name == "mine":
        f = Field(cv, 0)
        heap = np.clip(1 - (((f.X - C) / 72) ** 2 + ((f.Y - (C + 40)) / 40) ** 2), 0, None) ** 0.8
        f.h += u(52) * heap * (1 + 0.12 * noise(rng, f.h.shape, u(5)))
        body = heap > 0.04
        f.mat[body] = ID["soil"]
        f.body |= body
        cv.terrain(f.h, f.mat, y0=f.y0, ground=False, creases=f.crease, body=f.body)
        cv.cast(u(C + 22), u(C + 52), u(78), u(16), strength=0.5)
        oid = cv.new_id()
        cv.face([(u(C - 18), u(C + 40)), (u(C - 18), u(C + 6)), (u(C + 18), u(C + 6)), (u(C + 18), u(C + 40))], "dark", (0, 1, 0.3), oid, part=1)
        for (x0, y0, x1, y1) in [(C - 23, C + 42, C - 17, C + 2), (C + 17, C + 42, C + 23, C + 2), (C - 28, C + 8, C + 28, C + 2)]:
            cv.face([(u(x0), u(y0)), (u(x0), u(y1)), (u(x1), u(y1)), (u(x1), u(y0))], "wood", (-0.3, 1, 0.4), oid, part=2)
        cv.line([(u(C - 8), u(C + 42)), (u(C - 30), u(C + 72))], 1.2)
        cv.line([(u(C + 8), u(C + 42)), (u(C + 30), u(C + 72))], 1.2)
        cid = cv.new_id()
        cv.cast(u(C + 30), u(C + 66), u(18), u(5), strength=0.5)
        cv.face([(u(C + 8), u(C + 64)), (u(C + 8), u(C + 52)), (u(C + 36), u(C + 52)), (u(C + 36), u(C + 64))], "wood", (0, 1, 0.35), cid, part=1)
    return cv


def waves_local(cv, rng, cx, cy):
    for _ in range(3):
        x = cx + rng.uniform(-u(30), u(30))
        y = cy + rng.uniform(-u(8), u(10))
        cv.line([(x - u(5), y), (x, y - u(1.5)), (x + u(5), y)], 0.9)


# ---------------------------------------------------------------------------- icons


def paint_icon(kind: str, variant: int):
    """One living-terrain icon standing on (128, 244) of its box."""
    seed = 15485863 * (variant + 1) + sum(map(ord, kind))
    cv = Canvas(int(u(BOX)), int(u(BOX)), seed)
    rng = cv.rng
    foot = u(240)
    if kind == "Trees":
        tree(cv, u(128), foot, u(rng.uniform(160, 176)), rng, lobes=int(rng.integers(4, 6)))
    elif kind == "Pines":
        pine(cv, u(128), foot, u(rng.uniform(214, 236)), rng, tiers=int(rng.integers(3, 5)))
    elif kind == "Hills":
        f = Field(cv, 0)
        hill = bumps(f, [(128, 206, rng.uniform(96, 116), rng.uniform(96, 108), rng.uniform(30, 34))], rng, "grass")
        f.mat[hill <= u(2.5)] = 0
        cv.terrain(f.h, f.mat, y0=f.y0, ground=False, creases=f.crease, body=f.body)
        cv.cast(u(150), u(240), u(100), u(10), strength=0.45, soft=u(3))
    elif kind == "Mountains":
        f = Field(cv, 0)
        m = massif(f, [(128 + rng.uniform(-6, 6), 200, rng.uniform(170, 190), 78, 7),
                       (74, 210, rng.uniform(80, 110), 50, 6), (184, 212, rng.uniform(90, 120), 52, 6)], rng, squash=0.45, power=1.05)
        f.mat[m <= u(3)] = 0
        cv.terrain(f.h, f.mat, y0=f.y0, ground=False, creases=f.crease, body=f.body)
        cv.cast(u(150), u(238), u(110), u(10), strength=0.45, soft=u(3))
    return cv


# ---------------------------------------------------------------------------- the set

TERRAINS = {
    "grass": 3, "farmland": 3, "forest": 3, "pines": 3, "jungle": 3, "hills": 3, "mountains": 3, "desert": 3, "swamp": 3,
    "snow": 3, "canyon": 3, "cliffs": 3, "shallows": 3, "sea": 3, "lake": 3, "peaks": 3, "old-forest": 3, "volcano": 2,
    "live-forest": 3, "live-pines": 3, "live-hills": 3, "live-mountains": 3,
}
TALL = {"peaks", "old-forest", "volcano"}
FEATURES = {"village": 2, "city": 2, "castle": 2, "port": 2, "camp": 2, "tower": 2, "ruins": 2, "cave": 2, "temple": 2, "mine": 2}
ICONS = ["Trees", "Pines", "Hills", "Mountains"]
ICON_VARIANTS = 4


def render(cv: Canvas, size, pen=3.0, scale=SCALE):
    return finish(paint(cv, scale, pen=pen), size)
