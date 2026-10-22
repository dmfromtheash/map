"""Painterly 2.5D engine of the hex tile set, in the manner of the owner's mountains.

Everything is built as shapes with a light side and a shadow side (height fields seen from the
south-west sky, spheres, cones and flat faces), then painted: a few flat tones per material with
ragged, streaky edges (visible brush), a navy pen line on silhouettes that tapers and breaks, a
cool cast shadow and paper grain. Light comes from the north-west, as on the owner's art.
"""

from __future__ import annotations

import math

import numpy as np
from PIL import Image, ImageDraw
from scipy import ndimage as ndi

INK = np.array([0x1F, 0x2A, 0x36], np.float32) / 255
# Ground coordinates: x east (right), y south (towards the viewer, down the screen), z up.
LIGHT = np.array([-0.75, -0.16, 0.64], np.float32)
LIGHT /= np.linalg.norm(LIGHT)


def rgb(value: str) -> np.ndarray:
    value = value.lstrip("#")
    return np.array([int(value[i:i + 2], 16) for i in (0, 2, 4)], np.float32) / 255


def ramp(*colours: str) -> np.ndarray:
    return np.stack([rgb(c) for c in colours])


# Every palette runs from the lit tone to the deepest shadow. The owner's ice palette is the
# reference; the other materials are kept as quiet as it is.
PALETTES: dict[str, np.ndarray] = {
    "ice": ramp("F6FAFD", "E2EFF5", "BACFDC", "90ACC0", "7C99B0", "5D7891", "415873", "253651"),
    "snowfield": ramp("F8FBFC", "EAF2F6", "D2E1EA", "B6CBD8", "93AEC1"),
    "grass": ramp("CBD0A2", "B4BD8C", "9AA877", "7F9165", "637A55", "4A6248"),
    "meadow": ramp("D3D4A6", "BDC28F", "A4AE7B", "88986A", "6C8159"),
    "foliage": ramp("B9C58E", "93A971", "6F8A5C", "526F4E", "3B5644", "283F38"),
    "pine": ramp("A3B79A", "7D9A82", "5B7C6A", "425F54", "2E4642", "1F3134"),
    "jungle": ramp("B0BF86", "8DA26C", "6C8758", "506C4B", "38533F", "253D33"),
    "trunk": ramp("A08A70", "806A54", "604E40", "45392F"),
    "wheat": ramp("E6D9A6", "D6C28A", "C1A871", "A38C5C", "7E6C4B"),
    "soil": ramp("C9B48E", "AE9672", "8E785B", "6D5C47"),
    "sand": ramp("EFE3C2", "E2D0A6", "CDB689", "B09670", "8C7658"),
    "swamp": ramp("B4B48C", "999C74", "7C845F", "61694D", "48503D"),
    "bog": ramp("8FA3A0", "73898A", "5A7074", "44585F", "2F404A"),
    "canyon": ramp("E6C7A4", "D3AA86", "B98A6A", "986A54", "744E43", "523935"),
    "stone": ramp("E1DCCF", "C8C1B1", "A9A193", "888175", "676259", "4A4641"),
    "slate": ramp("D9E2E8", "B5C3CD", "8FA1AF", "6E8191", "4F6273", "34465A"),
    "basalt": ramp("A39D9B", "86807F", "6A6466", "504B4F", "3A363C", "28262C"),
    "lava": ramp("EFC48E", "DE9A68", "C67650", "A2573F"),
    "sea": ramp("A9C1D0", "8FAABE", "7894AB", "627F98", "4E6B86", "3D5874"),
    "shallow": ramp("CFE0E2", "B6CED4", "9DBAC4", "85A6B4", "6F92A4"),
    "lake": ramp("B8CCD8", "9CB5C6", "829EB3", "6A88A0", "56738D"),
    "plaster": ramp("F1EADA", "DED3BC", "C4B79D", "A3967E", "7F735F"),
    "roof": ramp("CB8A6C", "B26E55", "955746", "77443A", "57342E"),
    "roofslate": ramp("A9B9C9", "8699AE", "687D95", "506279", "3A4A5E"),
    "wood": ramp("B89A76", "9A7B5B", "7B5F46", "5D4737", "433429"),
    "cloth": ramp("F2E8D0", "E0D1B0", "C7B48F", "A49272"),
    "dark": ramp("4A4B52", "36383F", "272A31", "1C1F26"),
    "flag": ramp("C9665A", "AD4C44", "8C3A37"),
    "fire": ramp("F7D58E", "EFAE62", "DD8446"),
    "smoke": ramp("C9C8CB", "AFAEB3", "95949B", "7C7B84"),
}

NAMES = list(PALETTES)
EDGE_MATERIALS = ("bog",)
ID = {name: i + 1 for i, name in enumerate(NAMES)}

# How a material is painted: posterise (0 smooth .. 1 hard flat tones), the brush direction of
# its streaks (0 vertical, 90 horizontal), the streak strength and the shade range it spans.
STYLE: dict[str, dict] = {
    "ice": dict(post=0.75, angle=0, streak=0.18, lo=-0.38, hi=0.92, gamma=2.5),
    "snowfield": dict(post=0.55, angle=80, streak=0.1, lo=-0.4, hi=1.0),
    "grass": dict(post=0.55, angle=70, streak=0.1, lo=-0.3, hi=1.0),
    "meadow": dict(post=0.55, angle=70, streak=0.1, lo=-0.3, hi=1.0),
    "foliage": dict(post=0.7, angle=20, streak=0.12, lo=-0.55, hi=0.95, gamma=1.25),
    "pine": dict(post=0.7, angle=0, streak=0.12, lo=-0.6, hi=0.95, gamma=1.2),
    "jungle": dict(post=0.7, angle=20, streak=0.12, lo=-0.55, hi=0.95),
    "trunk": dict(post=0.6, angle=0, streak=0.08, lo=-0.5, hi=0.9),
    "wheat": dict(post=0.5, angle=90, streak=0.14, lo=-0.3, hi=1.0),
    "soil": dict(post=0.5, angle=90, streak=0.14, lo=-0.3, hi=1.0),
    "sand": dict(post=0.65, angle=75, streak=0.08, lo=-0.45, hi=1.0),
    "swamp": dict(post=0.55, angle=80, streak=0.1, lo=-0.3, hi=1.0),
    "bog": dict(post=0.6, angle=90, streak=0.14, lo=-0.3, hi=1.0),
    "canyon": dict(post=0.7, angle=90, streak=0.12, lo=-0.55, hi=0.95),
    "stone": dict(post=0.7, angle=0, streak=0.1, lo=-0.55, hi=0.95),
    "slate": dict(post=0.7, angle=0, streak=0.14, lo=-0.55, hi=0.95),
    "basalt": dict(post=0.7, angle=0, streak=0.14, lo=-0.55, hi=0.95),
    "lava": dict(post=0.5, angle=0, streak=0.1, lo=-0.4, hi=1.0),
    "sea": dict(post=0.6, angle=90, streak=0.16, lo=-0.4, hi=1.0),
    "shallow": dict(post=0.6, angle=90, streak=0.14, lo=-0.4, hi=1.0),
    "lake": dict(post=0.6, angle=90, streak=0.14, lo=-0.4, hi=1.0),
    "plaster": dict(post=0.75, angle=0, streak=0.06, lo=-0.55, hi=0.95),
    "roof": dict(post=0.75, angle=0, streak=0.1, lo=-0.55, hi=0.95),
    "roofslate": dict(post=0.75, angle=0, streak=0.1, lo=-0.55, hi=0.95),
    "wood": dict(post=0.7, angle=90, streak=0.1, lo=-0.55, hi=0.95),
    "cloth": dict(post=0.7, angle=0, streak=0.06, lo=-0.55, hi=0.95),
    "dark": dict(post=0.5, angle=0, streak=0.05, lo=-0.5, hi=0.9),
    "flag": dict(post=0.6, angle=0, streak=0.05, lo=-0.5, hi=0.9),
    "fire": dict(post=0.4, angle=0, streak=0.05, lo=-0.5, hi=0.9),
    "smoke": dict(post=0.6, angle=20, streak=0.12, lo=-0.5, hi=0.9),
}


def noise(rng: np.random.Generator, shape, sigma: float, stretch: float = 1.0, angle: float = 0.0) -> np.ndarray:
    """Smooth noise with unit spread; stretch > 1 draws streaks along `angle` (0 = vertical)."""
    pad = int(sigma * stretch * 3) + 2
    big = (shape[0] + 2 * pad, shape[1] + 2 * pad)
    n = rng.standard_normal(big).astype(np.float32)
    if stretch != 1.0 and angle % 180 != 0:
        n = ndi.gaussian_filter(n, sigma)
        n = ndi.rotate(n, angle, reshape=False, order=1, mode="wrap")
        n = ndi.gaussian_filter(n, (sigma * stretch, 1e-3))
        n = ndi.rotate(n, -angle, reshape=False, order=1, mode="wrap")
    else:
        n = ndi.gaussian_filter(n, (sigma * stretch, sigma))
    n = n[pad:pad + shape[0], pad:pad + shape[1]]
    return (n - n.mean()) / (n.std() + 1e-6)


def fbm(rng, shape, sigma, octaves=4, gain=0.5):
    total = np.zeros(shape, np.float32)
    amp = 1.0
    for _ in range(octaves):
        total += amp * noise(rng, shape, max(sigma, 0.6))
        sigma /= 2
        amp *= gain
    return total / np.abs(total).max()


def smoothstep(a, b, x):
    t = np.clip((x - a) / (b - a), 0, 1)
    return t * t * (3 - 2 * t)


class Canvas:
    """Buffers of one picture: what is where (material, shade, object), before it is painted."""

    def __init__(self, width: int, height: int, seed: int):
        self.w, self.h = width, height
        self.rng = np.random.default_rng(seed)
        self.mat = np.zeros((height, width), np.int16)
        self.shade = np.zeros((height, width), np.float32)
        self.oid = np.zeros((height, width), np.int32)
        self.part = np.zeros((height, width), np.int32)
        self.row = np.full((height, width), -1e6, np.float32)
        self.ground = np.zeros((height, width), bool)
        self.pen = np.zeros((height, width), np.float32)  # widths of pen seeds (explicit lines)
        self.shadow = np.zeros((height, width), np.float32)
        self.solid = np.zeros((height, width), bool)  # the object may be inked against others
        self.weight = np.ones((height, width), np.float32)  # pen weight of an object's outline
        self.elev = np.zeros((height, width), np.float32)  # height of the surface seen there
        self.body = np.zeros((height, width), bool)  # a standing shape (a mountain) of a height field
        self.creases = np.zeros((height, width), np.float32)  # weight of the crease pen on a face
        self.nrm = np.zeros((height, width, 3), np.float32)
        self.convex = np.zeros((height, width), bool)  # faces of this object get the crease pen
        self.next = 1
        self.Y, self.X = np.mgrid[0:height, 0:width].astype(np.float32)

    def new_id(self) -> int:
        self.next += 1
        return self.next

    # ------------------------------------------------------------------ height fields

    def terrain(self, h, mat, y0: float = 0.0, ground: bool = True, oid: int | None = None, crest=None, weight=1.0, creases=None, body=None):
        """A height field seen from the south: ground row r shows at screen y0 + r - h, the nearer rows over the farther."""
        hg, wg = h.shape
        assert wg == self.w
        gy, gx = np.gradient(h)
        n = np.stack([-gx, -gy, np.ones_like(h)], -1)
        n /= np.linalg.norm(n, axis=-1, keepdims=True)
        lam = n @ LIGHT
        hs = ndi.gaussian_filter(h, 3.5)
        sy, sx = np.gradient(hs)
        ns = np.stack([-sx, -sy, np.ones_like(hs)], -1)
        ns /= np.linalg.norm(ns, axis=-1, keepdims=True)
        conv = ndi.gaussian_filter(ndi.laplace(ndi.gaussian_filter(h, 3.0)), 4.0) < 0
        oid = oid or self.new_id()
        ybuf = np.full(wg, self.h, np.int32)
        cols = np.arange(wg)
        for r in range(hg - 1, -1, -1):
            top = y0 + r - h[r]
            t = np.ceil(top).astype(np.int32)
            t = np.maximum(t, 0)
            # a surface never reaches below its own foot (matters over the empty paper)
            bottom = ybuf if ground else np.minimum(ybuf, np.maximum(int(math.ceil(y0 + r + 1)), t + 1))
            lo, hi = int(t.min()), int(min(self.h, bottom.max()))
            if hi > lo:
                ys = np.arange(lo, hi, dtype=np.int32)[:, None]
                m = (ys >= t[None, :]) & (ys < bottom[None, :]) & (mat[r][None, :] > 0)
                yy, xx = np.nonzero(m)
                if yy.size:
                    yy = yy + lo
                    self.mat[yy, xx] = mat[r, xx]
                    self.shade[yy, xx] = lam[r, xx]
                    self.oid[yy, xx] = oid
                    self.part[yy, xx] = 0
                    self.row[yy, xx] = r
                    self.ground[yy, xx] = ground
                    self.solid[yy, xx] = not ground
                    self.weight[yy, xx] = weight
                    self.creases[yy, xx] = 0 if creases is None else creases[r, xx]
                    self.elev[yy, xx] = h[r, xx]
                    self.nrm[yy, xx] = ns[r, xx]
                    self.convex[yy, xx] = conv[r, xx]
                    self.body[yy, xx] = False if body is None else body[r, xx]
                    if crest is not None:
                        c = crest[r, xx]
                        sel = c > 0
                        self.pen[yy[sel], xx[sel]] = np.maximum(self.pen[yy[sel], xx[sel]], c[sel])
            ybuf = np.where(mat[r] > 0, np.minimum(ybuf, t), ybuf)
        return oid

    # ------------------------------------------------------------------ sprites

    def _put(self, mask, mat, shade, oid, part, weight=1.0, zbuf=None, z=None, creases=False):
        if zbuf is not None:
            mask = mask & (z > zbuf)
            zbuf[mask] = z[mask]
        if np.isscalar(mat):
            mat = np.full(mask.shape, mat, np.int16)
        self.mat[mask] = mat[mask]
        self.shade[mask] = shade[mask] if not np.isscalar(shade) else shade
        self.oid[mask] = oid
        self.part[mask] = part
        self.row[mask] = -1e6
        self.ground[mask] = False
        self.solid[mask] = True
        self.weight[mask] = weight
        self.creases[mask] = creases
        return mask

    def polygon_mask(self, points) -> np.ndarray:
        img = Image.new("L", (self.w, self.h), 0)
        ImageDraw.Draw(img).polygon([(float(x), float(y)) for x, y in points], fill=255)
        return np.asarray(img) > 127

    def face(self, points, material: str, normal, oid, part=0, tone=0.0, weight=1.0):
        """A flat face (a wall, a roof plane) lit by its normal."""
        n = np.array(normal, np.float32)
        n /= np.linalg.norm(n)
        mask = self.polygon_mask(points)
        self._put(mask, ID[material], float(n @ LIGHT) + tone, oid, part, weight)
        return mask

    def blob(self, cx, cy, r, material, oid, part=0, lumps=0.12, zbuf=None, flat=1.0, tone=0.0, seed=0, weight=1.0, creases=False):
        """A sphere (a crown of leaves, a boulder): lit on its north-west, with a lumpy edge."""
        x0, x1 = int(max(0, cx - r * 1.4)), int(min(self.w, cx + r * 1.4 + 1))
        y0, y1 = int(max(0, cy - r * 1.4)), int(min(self.h, cy + r * 1.4 + 1))
        if x1 <= x0 or y1 <= y0:
            return
        Y, X = self.Y[y0:y1, x0:x1], self.X[y0:y1, x0:x1]
        dx, dy = (X - cx) / r, (Y - cy) / (r * flat)
        ang = np.arctan2(dy, dx)
        rng = np.random.default_rng(seed)
        k = rng.integers(5, 9)
        ph = rng.uniform(0, 6.28, 3)
        edge = 1 + lumps * (0.6 * np.sin(k * ang + ph[0]) + 0.3 * np.sin((k + 3) * ang + ph[1]) + 0.2 * np.sin(2 * ang + ph[2]))
        d2 = (dx * dx + dy * dy) / (edge * edge)
        inside = d2 < 1
        nz = np.sqrt(np.clip(1 - d2, 0, 1))
        # A sphere seen from above-south: its top faces up, its lower rim faces the viewer (south).
        nx, ny = dx / edge, dy / edge
        n = np.stack([nx, ny * 0.6, nz], -1)
        n /= np.linalg.norm(n, axis=-1, keepdims=True) + 1e-6
        lam = n[..., 0] * LIGHT[0] + n[..., 1] * LIGHT[1] + n[..., 2] * LIGHT[2] + tone
        z = cy + nz * r
        full = np.zeros((self.h, self.w), bool)
        full[y0:y1, x0:x1] = inside
        shade = np.zeros((self.h, self.w), np.float32)
        shade[y0:y1, x0:x1] = lam
        zz = None
        if zbuf is not None:
            zz = np.full((self.h, self.w), -1e9, np.float32)
            zz[y0:y1, x0:x1] = np.where(inside, z, -1e9)
        return self._put(full, ID[material], shade, oid, part, weight, zbuf, zz, creases)

    def cone(self, cx, base_y, top_y, half, material, oid, part=0, jag=0.0, seed=0, droop=0.0, tone=0.0, weight=1.0):
        """A cone or a pine tier: the apex at top_y, a jagged base `half` wide either side."""
        rng = np.random.default_rng(seed)
        pts = [(cx, top_y)]
        teeth = max(3, int(half / 5))
        for i in range(teeth + 1):
            t = i / teeth
            x = cx + half - 2 * half * t
            y = base_y + droop * (1 - (2 * t - 1) ** 2) * -1 + (jag * (rng.uniform(0.3, 1.0)) if i % 2 else 0)
            pts.append((x, y))
        mask = self.polygon_mask(pts)
        height = max(base_y - top_y, 1)
        rel = (self.X - cx) / (half * np.clip((self.Y - top_y) / height, 0.05, 1.0))
        rel = np.clip(rel, -1, 1)
        nz = np.sqrt(np.clip(1 - rel * rel, 0, 1))
        n = np.stack([rel, 0.35 * np.ones_like(rel), nz * 0.8 + 0.25], -1)
        n /= np.linalg.norm(n, axis=-1, keepdims=True)
        lam = n @ LIGHT + tone
        return self._put(mask, ID[material], lam, oid, part, weight)

    def cylinder(self, cx, top_y, bottom_y, half, material, oid, part=0, tone=0.0, weight=1.0):
        mask = (np.abs(self.X - cx) <= half) & (self.Y >= top_y) & (self.Y <= bottom_y)
        rel = np.clip((self.X - cx) / half, -1, 1)
        n = np.stack([rel, np.sqrt(np.clip(1 - rel * rel, 0, 1)), np.zeros_like(rel) + 0.15], -1)
        lam = n @ LIGHT + tone
        return self._put(mask, ID[material], lam, oid, part, weight)

    def ellipse_mask(self, cx, cy, rx, ry):
        return ((self.X - cx) / rx) ** 2 + ((self.Y - cy) / ry) ** 2 < 1

    def cast(self, cx, cy, rx, ry, strength=0.55, soft=6.0):
        """A cool cast shadow on the ground, to the south-east of what stands at (cx, cy)."""
        m = self.ellipse_mask(cx, cy, rx, ry).astype(np.float32)
        m = ndi.gaussian_filter(m, soft)
        self.shadow = np.maximum(self.shadow, m * strength)

    def line(self, points, width: float, jitter: float = 0.0, seed: int = 0):
        """An explicit pen line through points (a ridge, a door frame); tapered at both ends."""
        rng = np.random.default_rng(seed)
        pts = np.array(points, np.float32)
        seg = np.linalg.norm(np.diff(pts, axis=0), axis=1)
        total = seg.sum()
        if total <= 0:
            return
        steps = max(2, int(total / 1.2))
        s = np.linspace(0, total, steps)
        cum = np.concatenate([[0], np.cumsum(seg)])
        xs = np.interp(s, cum, pts[:, 0]) + jitter * rng.standard_normal(steps).cumsum() * 0.05
        ys = np.interp(s, cum, pts[:, 1]) + jitter * rng.standard_normal(steps).cumsum() * 0.05
        t = s / total
        taper = np.clip(np.minimum(t, 1 - t) * 5, 0.25, 1)
        xi = np.clip(np.round(xs).astype(int), 0, self.w - 1)
        yi = np.clip(np.round(ys).astype(int), 0, self.h - 1)
        np.maximum.at(self.pen, (yi, xi), (width * taper).astype(np.float32))


def paint(cv: Canvas, scale: float, pen: float = 3.0, grain: float = 1.0, crease: float = 0.035, texture: float = 0.5,
          shadow_tint=(0.6, 0.68, 0.82), opaque_ground=True) -> np.ndarray:
    """Paints the buffers: flat tones with brushy edges, the pen, the cast shadows and the paper."""
    rng = cv.rng
    shape = (cv.h, cv.w)
    colour = np.zeros(shape + (3,), np.float32)
    wash = noise(rng, shape, 18 * scale) * 0.03 + noise(rng, shape, 6 * scale) * 0.015
    dab = noise(rng, shape, 2.2 * scale)
    for name in NAMES:
        mid = ID[name]
        m = cv.mat == mid
        if not m.any():
            continue
        st = STYLE[name]
        pal = PALETTES[name]
        k = len(pal)
        streak = noise(rng, shape, 1.4 * scale, stretch=9.0, angle=st["angle"]) if st["streak"] > 0 else 0
        t = np.clip(1 - (cv.shade - st["lo"]) / (st["hi"] - st["lo"]), 0, 1) ** st.get("gamma", 1.0)
        t = t + st["streak"] * streak * texture + wash + 0.02 * dab
        t = np.clip(t, 0, 0.999)
        q = (np.floor(t * k) + 0.5) / k
        tt = np.clip(t * (1 - st["post"]) + q * st["post"], 0, 1) * (k - 1)
        i0 = np.floor(tt).astype(int)
        i1 = np.minimum(i0 + 1, k - 1)
        f = (tt - i0)[..., None]
        c = pal[i0] * (1 - f) + pal[i1] * f
        colour[m] = c[m]

    # Cast shadows fall on the ground only, cool and transparent.
    tint = np.array(shadow_tint, np.float32)
    s = np.clip(cv.shadow, 0, 1)[..., None] * cv.ground[..., None]
    s = s * (0.85 + 0.15 * noise(rng, shape, 3 * scale)[..., None])
    colour = colour * (1 - s * (1 - tint))

    # The pen: outlines of objects, depth breaks of height fields and the explicit lines.
    seeds = np.zeros(shape, np.float32)
    o = cv.oid
    solid = cv.solid
    w_obj = pen * scale * cv.weight
    for axis in (0, 1):
        a = np.diff(o, axis=axis) != 0
        if axis == 0:
            sa, sb = solid[:-1, :], solid[1:, :]
            wa, wb = w_obj[:-1, :], w_obj[1:, :]
            # the outline belongs to the object in front: the one lower on the screen,
            # or the solid one against the ground or the empty paper
            e = a & (sa | sb)
            seeds[:-1, :] = np.maximum(seeds[:-1, :], np.where(e & sa, wa, 0))
            seeds[1:, :] = np.maximum(seeds[1:, :], np.where(e & sb, wb, 0))
        else:
            sa, sb = solid[:, :-1], solid[:, 1:]
            wa, wb = w_obj[:, :-1], w_obj[:, 1:]
            e = a & (sa | sb)
            seeds[:, :-1] = np.maximum(seeds[:, :-1], np.where(e & sa, wa, 0))
            seeds[:, 1:] = np.maximum(seeds[:, 1:], np.where(e & sb, wb, 0))
    # Parts of one object (tiers of a pine, planes of a roof): a thin line.
    p = cv.part
    for axis in (0, 1):
        a = (np.diff(p, axis=axis) != 0) & (np.diff(o, axis=axis) == 0)
        if axis == 0:
            seeds[1:, :] = np.maximum(seeds[1:, :], np.where(a & solid[1:, :], 0.5 * w_obj[1:, :], 0))
        else:
            seeds[:, 1:] = np.maximum(seeds[:, 1:], np.where(a & solid[:, 1:], 0.5 * w_obj[:, 1:], 0))
    # Height fields: where a near ridge hides far ground the silhouette gets the pen.
    r = cv.row
    jump_v = np.zeros(shape, np.float32)
    jump_v[1:, :] = r[1:, :] - r[:-1, :]
    jump_h = np.zeros(shape, np.float32)
    jump_h[:, 1:] = np.abs(r[:, 1:] - r[:, :-1])
    jump_h[:, :-1] = np.maximum(jump_h[:, :-1], np.abs(r[:, :-1] - r[:, 1:]))
    terr = (r > -1e5)
    thr = 7 * scale
    jump = np.where(terr, np.maximum(jump_v, jump_h), 0)
    jump = np.where(jump > 1e5, 60 * scale, jump)
    strength = smoothstep(thr, thr * 5, jump)
    seeds = np.maximum(seeds, strength * pen * scale * cv.weight * (cv.oid > 0))
    seeds = np.maximum(seeds, cv.pen * scale)
    # Water in the land (bog pools, a harbour): a light pen along the shore.
    for name in EDGE_MATERIALS:
        m = cv.mat == ID[name]
        if not m.any():
            continue
        shore = m & ndi.binary_dilation((cv.mat != ID[name]) & (cv.mat > 0))
        seeds = np.maximum(seeds, np.where(shore, 0.45 * pen * scale, 0))
    # The outline of a mountain against the land behind and beside it, fading out at its foot.
    bd = cv.body
    out_edge = np.zeros(shape, bool)
    out_edge[1:, :] |= bd[1:, :] & ~bd[:-1, :]
    out_edge[:, 1:] |= bd[:, 1:] & ~bd[:, :-1]
    out_edge[:, :-1] |= bd[:, :-1] & ~bd[:, 1:]
    below = np.zeros(shape, np.float32)
    for y in range(cv.h - 2, -1, -1):
        below[y] = np.where(bd[y], below[y + 1] + 1, 0)
    fade = smoothstep(3 * scale, 20 * scale, below)
    seeds = np.maximum(seeds, np.where(out_edge, pen * scale * cv.weight * fade, 0))
    # Creases inside a height field: where two faces meet at a ridge (the owner's lighter pen).
    if crease > 0:
        # Screen rows repeat ground rows in steps: the normals are smoothed first, or the crease
        # would come out as a row of dots.
        nm = np.dstack([ndi.gaussian_filter(cv.nrm[..., i], 1.6 * scale) for i in range(3)])
        nd = np.zeros(shape, np.float32)
        nd[:, 1:] = np.linalg.norm(nm[:, 1:] - nm[:, :-1], axis=-1)
        nd[1:, :] = np.maximum(nd[1:, :], np.linalg.norm(nm[1:, :] - nm[:-1, :], axis=-1))
        nd = ndi.gaussian_filter(nd, 1.0 * scale) * 3
        # the crest of the ridge of nd (a plain 3 x 3 maximum test breaks a slanted crest into dots)
        line = (nd > crease) & (nd >= 0.92 * ndi.maximum_filter(nd, int(3 * scale) | 1)) & (cv.creases > 0) & cv.convex
        seeds = np.maximum(seeds, np.where(line, cv.creases * pen * scale * cv.weight, 0))

    # Weight and breaks: heavier on the shadow side, thinning out and breaking now and then.
    lf = noise(rng, shape, 9 * scale)
    lf2 = noise(rng, shape, 5 * scale)
    shade_side = 1 + 0.45 * np.clip(-cv.shade, 0, 1)
    width = seeds * (0.78 + 0.3 * lf) * shade_side * smoothstep(-2.0, -1.2, lf2)
    has = width > 0.3
    if has.any():
        dist, (iy, ix) = ndi.distance_transform_edt(~has, return_indices=True)
        wnear = width[iy, ix]
        ink = np.clip(wnear / 2 - dist + 0.5, 0, 1)
    else:
        ink = np.zeros(shape, np.float32)
    cv.ink_alpha = ink.copy()  # the pen as painted here: plan.py traces it for Photoshop
    cv.ink_width = width
    ink *= 0.94
    colour = colour * (1 - ink[..., None]) + INK * ink[..., None]

    # Paper: fine tooth and a few fibres.
    if grain > 0:
        tooth = noise(rng, shape, 0.7 * scale) * 0.022 + noise(rng, shape, 2.5 * scale) * 0.018
        fibre = noise(rng, shape, 1.0 * scale, stretch=6.0, angle=35) * 0.012
        colour = colour * (1 + grain * (tooth + fibre))[..., None]
    colour = np.clip(colour, 0, 1)

    alpha = ((cv.mat > 0) | (ink > 0)).astype(np.float32)
    alpha = np.maximum(alpha, ink)
    # Cast shadows on empty paper (standing pictures): a soft transparent pool.
    free = (cv.mat == 0)
    pool = np.clip(cv.shadow, 0, 1) * free
    if pool.any():
        shadow_rgb = np.array([0.36, 0.44, 0.55], np.float32)
        colour[free] = colour[free] * 0 + shadow_rgb
        alpha = np.where(free, np.maximum(alpha, pool * 0.42), alpha)
        colour = np.where(free[..., None] & (ink[..., None] > 0), INK, colour)
    return np.dstack([colour, alpha])


def finish(rgba: np.ndarray, size: tuple[int, int]) -> Image.Image:
    """Premultiplied down-sampling to the final picture size."""
    a = rgba[..., 3:4]
    pm = np.dstack([rgba[..., :3] * a, a])
    img = Image.fromarray(np.clip(pm * 255 + 0.5, 0, 255).astype(np.uint8), "RGBA")
    small = np.asarray(img.resize(size, Image.LANCZOS)).astype(np.float32) / 255
    a = small[..., 3:4]
    col = np.where(a > 1e-3, small[..., :3] / np.maximum(a, 1e-3), 0)
    out = np.dstack([np.clip(col, 0, 1), a])
    return Image.fromarray((out * 255 + 0.5).astype(np.uint8), "RGBA")
