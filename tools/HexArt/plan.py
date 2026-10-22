"""Turns a picture built by tiles.py into a painting plan for Photoshop (photoshop/paint_plan.jsx).

The 2.5D buffers (material, light, objects) stay the underdrawing; the paint is put down with the
brushes loaded in Photoshop (the owner's Kyle Webster set): a light ground tone, darker tones laid
over it with oil and charcoal strokes clipped to their shapes, white chips on the snow, cool cast
shadows, then a thick-and-thin pen on the outlines and along the light/shadow edges.

Brushes are named by their place in the Brushes panel of this PC (see BRUSH).
"""

from __future__ import annotations

import json
import math

import cv2
import numpy as np
from scipy import ndimage as ndi

from paint import ID, NAMES, PALETTES, STYLE, noise, paint

# Places in the Brushes panel (Photoshop 2026 on the owner's PC, 2026-10-22).
BRUSH = {
    "pencil": 8,  # KYLE Ultimate - hard pencil
    "charcoal": 10,  # KYLE Ultimate - charcoal pencil 25 px
    "pastel": 12,  # Kyle Ultimate - pastels
    "pen": 14,  # KYLE Ultimate - Thick 'n Thin drafting
    "oil": 17,  # Kyle Real Oils - 01
    "oilwet": 18,  # Kyle Real Oils - round, wet edges
    "combine": 24,  # Kyle concept - stroke combination
    "foliage": 25,  # Kyle concept - foliage combination 2
    "spatter": 21,  # Kyle spatter - spatter and texture
}

INK = "1F2A36"

# How each material is brushed: stroke angle (0 = down the slope / vertical), length and size in
# 256-box pixels, the brush for the middle and the dark tones.
BRUSHING = {
    "ice": dict(angle=0, length=26, size=4.5, mid="oil", dark="charcoal", chips=True),
    "snowfield": dict(angle=80, length=30, size=6, mid="oilwet", dark="oil"),
    "grass": dict(angle=70, length=22, size=6, mid="oilwet", dark="oil"),
    "meadow": dict(angle=70, length=22, size=6, mid="oilwet", dark="oil"),
    "foliage": dict(angle=None, length=7, size=4, mid="oil", dark="charcoal"),
    "jungle": dict(angle=None, length=7, size=4, mid="oil", dark="charcoal"),
    "pine": dict(angle=60, length=6, size=3, mid="oil", dark="charcoal"),
    "trunk": dict(angle=0, length=8, size=2, mid="oil", dark="charcoal"),
    "sea": dict(angle=88, length=34, size=6, mid="oilwet", dark="oil"),
    "shallow": dict(angle=88, length=34, size=6, mid="oilwet", dark="oil"),
    "lake": dict(angle=88, length=34, size=6, mid="oilwet", dark="oil"),
    "plaster": dict(angle=0, length=10, size=3, mid="oil", dark="charcoal"),
    "roof": dict(angle=20, length=10, size=3, mid="oil", dark="charcoal"),
    "roofslate": dict(angle=20, length=10, size=3, mid="oil", dark="charcoal"),
    "stone": dict(angle=0, length=10, size=3, mid="oil", dark="charcoal"),
    "wood": dict(angle=90, length=10, size=2.5, mid="oil", dark="charcoal"),
}
DEFAULT_BRUSHING = dict(angle=60, length=14, size=4, mid="oil", dark="charcoal")


def hexstr(c) -> str:
    c = np.clip(np.asarray(c) * 255 + 0.5, 0, 255).astype(int)
    return "%02X%02X%02X" % tuple(c)


def ramp_at(name, t):
    pal = PALETTES[name]
    x = np.clip(t, 0, 0.999) * (len(pal) - 1)
    i = int(x)
    f = x - i
    return pal[i] * (1 - f) + pal[min(i + 1, len(pal) - 1)] * f


def polygons(mask, scale_down=1.0, min_area=30.0, eps=1.2):
    """Outer outlines and holes of a mask: [{"p": points, "hole": bool}, ...], outers first."""
    m = mask.astype(np.uint8) * 255
    contours, hierarchy = cv2.findContours(m, cv2.RETR_CCOMP, cv2.CHAIN_APPROX_NONE)
    outers, holes = [], []
    if hierarchy is None:
        return []
    for c, h in zip(contours, hierarchy[0]):
        if cv2.contourArea(c) < min_area:
            continue
        c = cv2.approxPolyDP(c, eps, True)
        if len(c) < 3:
            continue
        pts = [[float(p[0][0]) * scale_down, float(p[0][1]) * scale_down] for p in c]
        (holes if h[3] >= 0 else outers).append({"p": pts, "hole": bool(h[3] >= 0)})
    return outers + holes


def contour_lines(mask, keep=None, min_len=8, eps=1.0):
    """Outlines of a mask as open polylines; `keep(x, y)` cuts them where it is False."""
    m = mask.astype(np.uint8) * 255
    contours, _ = cv2.findContours(m, cv2.RETR_LIST, cv2.CHAIN_APPROX_NONE)
    lines = []
    for c in contours:
        pts = c[:, 0, :]
        if keep is not None:
            ok = keep[pts[:, 1], pts[:, 0]]
        else:
            ok = np.ones(len(pts), bool)
        run = []
        for p, k in zip(pts, ok):
            if k:
                run.append(p)
            elif run:
                if len(run) >= min_len:
                    lines.append(run)
                run = []
        if len(run) >= min_len:
            lines.append(run)
    out = []
    for run in lines:
        arr = np.array(run, np.int32).reshape(-1, 1, 2)
        arr = cv2.approxPolyDP(arr, eps, False)[:, 0, :]
        if len(arr) >= 2:
            out.append([[float(x), float(y)] for x, y in arr])
    return out


def split_lines(lines, rng, piece=(30, 90), keep=0.75):
    """Cuts long pen lines into pieces with gaps, so the line breaks like a hand-drawn one."""
    out = []
    for line in lines:
        pts = np.array(line)
        seg = np.linalg.norm(np.diff(pts, axis=0), axis=1)
        cum = np.concatenate([[0], np.cumsum(seg)])
        total = cum[-1]
        s = 0.0
        while s < total:
            length = rng.uniform(*piece)
            if rng.uniform() < keep:
                ts = np.linspace(s, min(total, s + length), max(2, int(length / 6)))
                xs = np.interp(ts, cum, pts[:, 0])
                ys = np.interp(ts, cum, pts[:, 1])
                out.append([[float(x), float(y)] for x, y in zip(xs, ys)])
            s += length + rng.uniform(4, 14)
    return out


def hatch(mask, rng, angle, length, spacing, jitter=0.35, curved=False):
    """Strokes covering a mask: lines along `angle` (degrees from vertical), staggered."""
    ys, xs = np.nonzero(mask)
    if len(xs) == 0:
        return []
    x0, x1, y0, y1 = xs.min(), xs.max(), ys.min(), ys.max()
    lines = []
    if angle is None:
        # dabs: short curved strokes in every direction (leaves)
        n = int(mask.sum() / (spacing * spacing) * 1.2)
        idx = rng.integers(0, len(xs), n)
        for i in idx:
            a = rng.uniform(0, math.pi)
            cx, cy = xs[i], ys[i]
            dx, dy = math.cos(a) * length / 2, math.sin(a) * length / 2
            bend = rng.uniform(-0.4, 0.4) * length
            lines.append([[cx - dx, cy - dy], [cx + bend * 0.3 - dy * 0.2, cy + bend * 0.3 + dx * 0.2], [cx + dx, cy + dy]])
        return lines
    a = math.radians(angle)
    ux, uy = math.sin(a), math.cos(a)  # along the stroke
    vx, vy = uy, -ux  # across
    cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
    half = math.hypot(x1 - x0, y1 - y0) / 2 + length
    v = -half
    while v < half:
        u = -half + rng.uniform(0, length)
        while u < half:
            l = length * rng.uniform(0.6, 1.2)
            px = cx + vx * (v + rng.uniform(-jitter, jitter) * spacing) + ux * u
            py = cy + vy * (v + rng.uniform(-jitter, jitter) * spacing) + uy * u
            qx, qy = px + ux * l, py + uy * l
            mx, my = int((px + qx) / 2), int((py + qy) / 2)
            if 0 <= mx < mask.shape[1] and 0 <= my < mask.shape[0] and mask[my, mx]:
                wob = rng.uniform(-0.15, 0.15) * l
                lines.append([[px, py], [(px + qx) / 2 + vx * wob, (py + qy) / 2 + vy * wob], [qx, qy]])
            u += l + rng.uniform(0.1, 0.5) * length
        v += spacing
    return lines


def trace_ink(cv, k):
    """The pen of the engine (paint.paint) as polylines with their widths, for the Photoshop pen."""
    from skimage.morphology import skeletonize

    ink = cv.ink_alpha > 0.45
    sk = skeletonize(ink)
    dt = ndi.distance_transform_edt(ink)
    h, w = sk.shape
    left = set(zip(*np.nonzero(sk)))
    nbrs = [(-1, -1), (-1, 0), (-1, 1), (0, -1), (0, 1), (1, -1), (1, 0), (1, 1)]

    def count(p):
        return sum((p[0] + dy, p[1] + dx) in left for dy, dx in nbrs)

    lines = []
    while left:
        ends = [p for p in left if count(p) <= 1]
        start = ends[0] if ends else next(iter(left))
        run = [start]
        left.discard(start)
        cur = start
        while True:
            nxt = None
            for dy, dx in nbrs:
                q = (cur[0] + dy, cur[1] + dx)
                if q in left:
                    nxt = q
                    break
            if nxt is None:
                break
            left.discard(nxt)
            run.append(nxt)
            cur = nxt
        if len(run) >= 4 * k:
            ys = np.array([p[0] for p in run])
            xs = np.array([p[1] for p in run])
            width = float(np.median(dt[ys, xs])) * 2
            arr = np.stack([xs, ys], 1).astype(np.int32).reshape(-1, 1, 2)
            arr = cv2.approxPolyDP(arr, 0.8, False)[:, 0, :]
            lines.append((width, [[float(x), float(y)] for x, y in arr]))
    return lines


def build_plan(cv, name, out, k, seed=1, opaque=True, pen=1.0, silhouettes=True, interior=True):
    """The painting plan of one picture. `k` is canvas pixels per 256-box pixel."""
    rng = np.random.default_rng(seed)
    shape = cv.mat.shape
    ops = []
    t_all = np.zeros(shape, np.float32)
    ragged = noise(rng, shape, 2.5 * k) * 0.07 + noise(rng, shape, 8 * k) * 0.05
    present = [n for n in NAMES if (cv.mat == ID[n]).any()]
    # the ground first, what stands on it after
    present.sort(key=lambda n: -float(cv.ground[cv.mat == ID[n]].mean()))
    for n in present:
        m = cv.mat == ID[n]
        st = STYLE[n]
        t = np.clip(1 - (cv.shade - st["lo"]) / (st["hi"] - st["lo"]), 0, 1) ** st.get("gamma", 1.0)
        t_all[m] = t[m]
    t_all = t_all + ragged
    # cast shadows darken the ground
    t_all = np.where(cv.ground, t_all + np.clip(cv.shadow, 0, 1) * 0.35, t_all)

    def P(mask, **kw):
        return polygons(mask, min_area=kw.get("min_area", 20 * k))

    for n in present:
        m = cv.mat == ID[n]
        br = BRUSHING.get(n, DEFAULT_BRUSHING)
        size = br["size"] * k
        length = br["length"] * k
        base = ramp_at(n, 0.12)
        ops.append({"op": "fill", "layer": "paint", "polys": P(m), "color": hexstr(base)})
        bands = [(0.3, 0.38, "oilwet", 45, 1.6), (0.48, 0.58, br["mid"], 85, 1.0), (0.68, 0.78, br["dark"], 85, 0.8),
                 (0.86, 0.95, br["dark"], 80, 0.6)]
        for (thr, tone, brush, opacity, sz) in bands:
            band = m & (t_all > thr)
            band = ndi.binary_opening(band, iterations=max(1, int(k)))
            if band.sum() < 30 * k * k:
                continue
            polys = P(band)
            if not polys:
                continue
            lines = hatch(band, rng, br["angle"], length * (0.8 + 0.4 * sz), size * sz * 0.7)
            if not lines:
                continue
            ops.append({"op": "paint", "layer": "paint", "polys": polys, "brush": BRUSH[brush], "size": round(size * sz, 1),
                        "color": hexstr(ramp_at(n, tone)), "opacity": opacity, "lines": lines})
        if br.get("chips"):
            # white chips: short bright strokes breaking into the shadowed faces
            edge = m & (t_all > 0.45) & (t_all < 0.62)
            lines = hatch(edge, rng, 0, 5 * k, 7 * k)
            lines = [l for l in lines if rng.uniform() < 0.35]
            if lines:
                ops.append({"op": "paint", "layer": "paint", "polys": P(m), "brush": BRUSH["pastel"], "size": round(2.2 * k, 1),
                            "color": "F6FAFD", "opacity": 90, "lines": lines})

    # The pen: the engine's own linework (silhouettes, ridges, creases, ticks) traced and drawn
    # again with the thick-and-thin pen, thin, middle and heavy lines each in one pass.
    paint(cv, k, pen=3.0 * pen)
    groups = {}
    for width, line in trace_ink(cv, k):
        bucket = 0 if width < 1.6 * k else (1 if width < 2.6 * k else 2)
        groups.setdefault(bucket, []).append(line)
    for bucket, lines in sorted(groups.items()):
        size = [1.1, 1.8, 2.6][bucket] * k * pen
        ops.append({"op": "paint", "layer": "ink", "brush": BRUSH["pen"], "size": round(size, 1), "color": INK,
                    "opacity": 100, "lines": lines})
    return {"name": name, "w": int(shape[1]), "h": int(shape[0]), "out": out, "ops": ops}


def write(plan, path):
    with open(path, "w", encoding="utf-8-sig") as f:
        f.write("(" + json.dumps(plan, ensure_ascii=False) + ")")
