"""Reusable painted parts: canopies, pines, peaks, walls and roofs."""
import math
import numpy as np
from scipy import ndimage as ndi
from brush import SS, mix
from palette import *


def shifted(m, dx, dy):
    return ndi.shift(m, (dy * SS, dx * SS), order=1, mode='constant')


def tuft(cv, x, y, s=1.0, color=None, opacity=.55, seed=0):
    """A few grass blades at the foot of an object so it roots into the ground."""
    r = np.random.default_rng(seed)
    color = GROUND_SHADOW if color is None else color
    for _ in range(3):
        a = r.uniform(-.5, .5)
        L = r.uniform(4, 7) * s
        cv.stroke([(x, y), (x + math.sin(a) * L * .6, y - L)], 1.0 * s, color, opacity, (0, .9))
        x += r.uniform(1.5, 3) * s


# ---------------- vegetation ----------------

def canopy(cv, cx, cy, r, leaf=LEAF, light=LEAF_LIGHT, shade=LEAF_SHADE, ink=LEAF_INK, clumps=8,
           squash=.9, seed=0, outline=True, dabs=True):
    rng = np.random.default_rng(seed)
    blobs = [(cx - r * .05, cy + r * .02, r * .62)]
    for i in range(clumps):
        a = 2 * math.pi * i / clumps + rng.uniform(-.25, .25)
        d = r * rng.uniform(.5, .62)
        blobs.append((cx + math.cos(a) * d, cy + math.sin(a) * d * squash, r * rng.uniform(.36, .48)))
    m = np.zeros((cv.H, cv.W))
    for k, (bx, by, br) in enumerate(blobs):
        m = np.maximum(m, cv.ellipse(bx, by, br, br * squash, wobble=br * .08, cell=max(3, br * .4), seed=seed + k))
    cv.wash(m, leaf, var=.12, edge=.18, cell=r * .6, seed=seed, light=(-.6, -.8, .22))
    # lower-right body shade
    cres = m * (1 - np.clip(shifted(m, -r * .22, -r * .28) * 1.2, 0, 1))
    cv.wash(ndi.gaussian_filter(cres, 1.5 * SS), shade, var=.1, edge=0, seed=seed + 1, opacity=.75)
    # clump separations and highlights
    for k, (bx, by, br) in enumerate(blobs[1:], 1):
        if by < cy + r * .3:
            hl = cv.ellipse(bx - br * .28, by - br * .32, br * .42, br * .32 * squash, wobble=br * .1, cell=4, seed=seed + 50 + k)
            cv.wash(hl * m, light, var=.1, edge=0, seed=seed + 60 + k, opacity=.55)
        a0 = math.radians(20); a1 = math.radians(150)
        arc = [(bx + br * .82 * math.cos(t), by + br * .82 * squash * math.sin(t)) for t in np.linspace(a0, a1, 7)]
        cv.over(cv.line_mask(arc, max(1.0, r * .05), (.4, .4)) * m * .5, shade)
    if dabs:
        n = int(r * 1.4)
        ys, xs = np.nonzero(m > .8)
        if len(xs):
            pick = rng.choice(len(xs), size=min(n, len(xs)), replace=False)
            for j in pick:
                x, y = xs[j] / SS, ys[j] / SS
                up = ((x - cx) * -.6 + (y - cy) * -.8) / r
                col = light if up > .15 else shade
                L = r * rng.uniform(.06, .11); a = rng.uniform(-.8, .8)
                cv.stroke([(x, y), (x + math.cos(a) * L, y + math.sin(a) * L * .6)], max(.8, r * .045), col, .45, (.3, .5))
    if outline:
        cv.outline(m, ink, width=max(1.0, r * .05), breaks=.2, cell=max(3, r * .3), seed=seed, opacity=.8)
    return m


def broadleaf(cv, x, base, h, seed=0, leaf=LEAF, light=LEAF_LIGHT, shade=LEAF_SHADE, ink=LEAF_INK, lean=0.0,
              shadow=True):
    r = h * .36
    cy = base - h + r * 1.05
    if shadow:
        cv.shadow(x + r * .35, base - r * .05, r * .95, r * .32, GROUND_SHADOW, .34, blur=r * .12)
    tw = max(1.4, r * .16)
    cv.stroke([(x, base), (x + lean * r * .2, cy + r * .3)], tw, TIMBER, .95, (0, .2), seed=seed)
    cv.stroke([(x - tw * .25, base), (x + lean * r * .2 - tw * .2, cy + r * .4)], tw * .35, mix(TIMBER, INK, .5), .6, (0, .3))
    canopy(cv, x + lean * r * .3, cy, r, leaf, light, shade, ink, seed=seed)


def pine(cv, x, base, h, seed=0, shadow=True, col=PINE, light=PINE_LIGHT, shade=PINE_SHADE):
    w = h * .42
    if shadow:
        cv.shadow(x + w * .45, base, w * .7, w * .22, GROUND_SHADOW, .3, blur=max(1, w * .1))
    cv.stroke([(x, base), (x, base - h * .25)], max(1, w * .12), TIMBER, .9, (0, 0))
    tiers = 3
    m = np.zeros((cv.H, cv.W))
    for i in range(tiers):
        t0 = base - h * (.18 + .27 * i); top = base - h * (.52 + .24 * i) if i < tiers - 1 else base - h
        ww = w * (1 - .24 * i)
        m = np.maximum(m, cv.poly([(x - ww * .5, t0), (x, top), (x + ww * .5, t0), (x, t0 + h * .05)],
                                  wobble=max(.4, w * .03), cell=max(2, w * .15), seed=seed + i))
    cv.wash(m, col, var=.1, edge=.2, cell=w, seed=seed, light=(-.8, -.4, .2))
    right = np.zeros((cv.H, cv.W)); right[:, int(x * SS):] = 1
    cv.wash(m * ndi.gaussian_filter(right, SS * .8), shade, var=.05, edge=0, seed=seed + 3, opacity=.6)
    cv.outline(m, LEAF_INK, width=max(.8, w * .05), breaks=.25, cell=max(2, w * .2), seed=seed, opacity=.75)
    return m


# ---------------- mountains ----------------

def peak_polys(rng, px, py, left, right, base):
    """Silhouette and crest of one peak. left/right: base x extents."""
    h = base - py
    sil = [(left, base)]
    n = 4
    for i in range(1, n):
        t = i / n
        x = left + (px - left) * t
        y = base - h * (t ** 1.25) + rng.uniform(-.06, .06) * h * math.sin(t * math.pi)
        if i == n - 1 and rng.random() < .6:  # a shoulder
            sil.append((x - (px - left) * .05, y + h * .04))
        sil.append((x, y))
    sil.append((px, py))
    for i in range(1, n):
        t = i / n
        x = px + (right - px) * t
        y = py + h * (t ** .85) + rng.uniform(-.07, .07) * h * math.sin(t * math.pi)
        sil.append((x, y))
    sil.append((right, base))
    crest = [(px, py)]
    steps = 5
    for i in range(1, steps + 1):
        t = i / steps
        crest.append((px + (right - px) * (.08 + .22 * t) + rng.uniform(-.04, .04) * (right - left),
                      py + h * t))
    return sil, crest


def peak(cv, px, py, left, right, base, seed=0, snow=True, green=.3, ink_fade=None, rock=ROCK_LIGHT):
    rng = np.random.default_rng(seed)
    h = base - py; wdt = right - left
    sil, crest = peak_polys(rng, px, py, left, right, base)
    m = cv.poly(sil, wobble=max(1, wdt * .006), cell=max(6, wdt * .05), seed=seed)
    cv.wash(m, rock, var=.12, edge=.16, cell=wdt * .25, seed=seed, light=(-.7, -.7, .1))
    shade_poly = crest + [(right, base)] + [p for p in reversed(sil) if p[0] > px][::-1][::-1]
    face = cv.poly(crest + [(right + 2, base + 2)] + [p for p in sil[::-1] if p[0] > px and p[1] < base],
                   wobble=max(1, wdt * .006), cell=max(6, wdt * .05), seed=seed + 1)
    face *= m
    cv.wash(face, ROCK_SHADE, var=.12, edge=.1, cell=wdt * .2, seed=seed + 2, opacity=.85)
    # snow cap
    if snow:
        sl = py + h * rng.uniform(.26, .34)
        yy = np.arange(cv.H)[:, None] / SS
        edge = sl + (cv.noise(max(4, wdt * .04), seed + 9) - .5) * h * .22
        cap = m * np.clip((edge - yy) / 3, 0, 1)
        cv.wash(cap * (1 - face), SNOW, var=.04, edge=.08, seed=seed + 3, opacity=.95)
        cv.wash(cap * face, SNOW_SHADE, var=.05, edge=.05, seed=seed + 4, opacity=.9)
    # gullies on the lit face, hatching on the shadow face
    for i in range(int(3 + wdt / 60)):
        t = rng.uniform(.15, .8)
        sx = px + (crest[-1][0] - px) * t * .5; sy = py + h * t * .7
        L = h * rng.uniform(.15, .3)
        pts = [(sx, sy), (sx - L * .35, sy + L * .5), (sx - L * .45, sy + L)]
        cv.over(cv.line_mask(pts, max(1, wdt * .006), (.2, .6), .8 * SS, seed + i) * m * (1 - face) * .45, ROCK_SHADE)
    for i in range(int(8 + wdt / 12)):
        t = rng.uniform(.05, .95)
        cx_ = crest[0][0] + (crest[-1][0] - crest[0][0]) * t + rng.uniform(0, 1) * (right - crest[-1][0]) * .9 * t
        cy_ = py + h * t * rng.uniform(.6, 1.0)
        L = h * rng.uniform(.06, .13)
        cv.over(cv.line_mask([(cx_, cy_), (cx_ + L * .35, cy_ + L)], max(.9, wdt * .004), (.3, .5)) * face * .5, ROCK_DEEP)
    # vegetated foot
    if green:
        yy = np.arange(cv.H)[:, None] / SS
        gl = base - h * green + (cv.noise(max(4, wdt * .05), seed + 12) - .5) * h * .18
        foot = m * np.clip((yy - gl) / (h * .12), 0, 1)
        cv.wash(foot, SLOPE_GREEN, var=.14, edge=0, cell=wdt * .15, seed=seed + 5, opacity=.78)
    keep = ink_fade if ink_fade is not None else cv.fade_below(base - h * .3, base - h * .05)
    cv.outline(m, ROCK_INK, width=max(1.1, wdt * .006), breaks=.16, cell=max(5, wdt * .03), seed=seed, keep=keep)
    cv.stroke(crest[:4], max(1.0, wdt * .005), ROCK_INK, .55, (.05, .6), .6, seed)
    return m


def melt_base(cv, y0, y1, amount=1.0):
    """Let the lowest band of a stamp dissolve into the ground (no hard cut-out edge)."""
    yy = np.arange(cv.H)[:, None] / SS
    t = np.clip((yy - y0) / max(1e-6, y1 - y0), 0, 1)
    n = cv.noise(6, 77)
    f = 1 - amount * np.clip(t * (1.15 - .3 * n), 0, 1) ** 1.3
    cv.rgb *= f[..., None]; cv.a *= f


# ---------------- buildings ----------------

def box_house(cv, x0, x1, top, base, depth=(60, -26), gable=40, roof=ROOF, roof_light=ROOF_LIGHT,
              roof_shade=ROOF_SHADE, wall=WALL, timber=True, seed=0, door=True, windows=2, chimney=True,
              shadow=True, overhang=5):
    """Front gable facing the viewer, right side wall and right roof slope visible."""
    dx, dy = depth
    mid = (x0 + x1) / 2
    apex = (mid, top - gable)
    if shadow:
        sh = cv.poly([(x0 + 6, base), (x1 + dx + 18, base + dy + 6), (x1 + dx + 30, base + dy + 18), (x1 + 16, base + 10), (x0 + 14, base + 8)],
                     wobble=2, cell=12, seed=seed + 90, soft=4)
        cv.over(sh * .33, GROUND_SHADOW)
    front = cv.poly([(x0, base), (x0, top), apex, (x1, top), (x1, base)], wobble=.8, cell=10, seed=seed)
    side = cv.poly([(x1, top), (x1 + dx, top + dy), (x1 + dx, base + dy), (x1, base)], wobble=.8, cell=10, seed=seed + 1)
    cv.wash(front, wall, var=.08, edge=.2, cell=30, seed=seed, light=(-.8, -.6, .1))
    cv.wash(side, WALL_SHADE, var=.08, edge=.18, cell=30, seed=seed + 2)
    if timber:
        cv.stroke([(x0 + 2, top + 1), (x1 - 2, top + 1)], 2.2, TIMBER, .85, (0, 0))
        for i in range(1, 3):
            xx = x1 + dx * i / 3
            cv.stroke([(xx, top + dy * i / 3 + 2), (xx, base + dy * i / 3 - 2)], 2, TIMBER, .8, (0, 0))
        cv.stroke([(x1 + 3, (top + base) / 2), (x1 + dx - 3, (top + base) / 2 + dy)], 1.8, TIMBER, .75, (0, 0))
        cv.stroke([(mid, top + 2), (mid, apex[1] + 8)], 1.8, TIMBER, .7, (0, 0))
    # openings
    if door:
        dw = (x1 - x0) * .2; dh = (base - top) * .62
        dm = cv.poly([(mid - dw / 2, base), (mid - dw / 2, base - dh), (mid + dw / 2, base - dh), (mid + dw / 2, base)], wobble=.4, cell=5, seed=seed + 3)
        cv.wash(dm, WOOD, var=.1, edge=.3, seed=seed + 3)
        cv.outline(dm, INK, 1.0, .1, seed=seed + 3, opacity=.7)
    for i in range(windows):
        t = (i + .5) / windows
        wx = x1 + dx * t; wy = top + dy * t + (base - top) * .35
        wm = cv.poly([(wx - 5, wy - 6 + dy * .05), (wx + 5, wy - 6 - dy * .05), (wx + 5, wy + 6 - dy * .05), (wx - 5, wy + 6 + dy * .05)], wobble=.3, cell=4, seed=seed + 10 + i)
        cv.wash(wm, WINDOW, var=.05, edge=0, seed=seed + 10)
    if door and windows:
        for sx in (x0 + (x1 - x0) * .2, x1 - (x1 - x0) * .2):
            wm = cv.ellipse(sx, top + (base - top) * .38, 4.5, 5.5, wobble=.3, cell=4, seed=seed + 20)
            cv.wash(wm, WINDOW, var=.05, edge=0, seed=seed + 20)
    # roof: right slope quad + front bargeboard
    o = overhang
    ridge_back = (apex[0] + dx, apex[1] + dy)
    slope = cv.poly([(apex[0], apex[1] - 2), (ridge_back[0], ridge_back[1] - 2), (x1 + dx + o, top + dy + o * .6),
                     (x1 + o, top + o * .6)], wobble=.9, cell=12, seed=seed + 4)
    cv.wash(slope, roof, var=.12, edge=.2, cell=25, seed=seed + 4, light=(-.5, -.8, .16))
    r = np.random.default_rng(seed)
    rows = 5
    for i in range(1, rows):
        t = i / rows
        a = (apex[0] + (x1 + o - apex[0]) * t, apex[1] + (top + o * .6 - apex[1]) * t)
        b = (a[0] + dx, a[1] + dy)
        cv.over(cv.line_mask([a, b], 1.1, (.1, .1), .5 * SS, seed + i) * slope * .5, roof_shade)
    for i in range(14):
        t = r.uniform(.1, .95); u = r.uniform(.05, .95)
        a = (apex[0] + (x1 + o - apex[0]) * t + dx * u, apex[1] + (top + o * .6 - apex[1]) * t + dy * u)
        cv.over(cv.line_mask([a, (a[0] + 1.5, a[1] + 5)], 1.0, (.3, .3)) * slope * .45, roof_shade)
    barge = cv.line_mask([(x0 - o, top + o * .6), apex, (x1 + o, top + o * .6)], 4.2, (0, 0))
    cv.wash(barge, roof_shade, var=.08, edge=0, seed=seed + 6)
    cv.outline(slope, INK, 1.1, .2, seed=seed + 4, opacity=.7)
    if chimney:
        cxp = apex[0] + dx * .62; cyp = apex[1] + dy * .62 + 4
        ch = cv.poly([(cxp - 5, cyp + 4), (cxp - 5, cyp - 16), (cxp + 5, cyp - 18), (cxp + 5, cyp + 2)], wobble=.4, cell=5, seed=seed + 7)
        cv.wash(ch, STONE, var=.1, edge=.25, seed=seed + 7, light=(-1, 0, .2))
        cv.outline(ch, INK, 1.0, .1, seed=seed + 7, opacity=.7)
    cv.outline(front, INK, 1.2, .22, seed=seed, keep=cv.fade_below(base - 6, base + 1), opacity=.7)
    cv.outline(side, INK, 1.2, .22, seed=seed + 1, keep=cv.fade_below(base + dy - 6, base + dy + 1), opacity=.7)
    for k, (tx, ty) in enumerate([(x0 - 2, base + 1), (x1 + dx - 4, base + dy + 1), (mid + 12, base + 1)]):
        tuft(cv, tx, ty, 1.0, seed=seed + 30 + k)
