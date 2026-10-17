"""Buildings in one oblique projection (x east, y north/away, z up) with a single north-west
light, painted face by face with material brushes and a crisp pen contour."""
import math
import numpy as np
from scipy import ndimage as ndi
from brush import SS, mix
from palette import *

LIGHT = np.array([-.62, .3, .75]); LIGHT = LIGHT / np.linalg.norm(LIGHT)

# material -> (shade, base, light)
MAT = {
    'plaster': (WALL_SHADE, mix(WALL, WALL_SHADE, .25), WALL),
    'stone': (STONE_SHADE, mix(STONE, STONE_SHADE, .3), STONE),
    'tile': (ROOF_SHADE, ROOF, ROOF_LIGHT),
    'thatch': (THATCH_SHADE, THATCH, mix(THATCH, WALL, .35)),
    'slate': (SLATE_SHADE, SLATE, mix(SLATE, WALL, .3)),
    'wood': (mix(WOOD, INK, .35), WOOD, mix(WOOD, WALL, .3)),
    'rock': (ROCK_SHADE, ROCK, ROCK_LIGHT),
}


class Proj:
    def __init__(self, cv, ox, oy, kx=.5, ky=.42, seed=0):
        self.cv, self.ox, self.oy, self.kx, self.ky = cv, ox, oy, kx, ky
        self.seed = seed; self.k = 0

    def p(self, x, y, z):
        return (self.ox + x + y * self.kx, self.oy - z - y * self.ky)

    def next(self):
        self.k += 1
        return self.seed * 100 + self.k

    # ---- faces ----
    def tone(self, normal, mat):
        n = np.asarray(normal, float); n = n / np.linalg.norm(n)
        lam = float(n @ LIGHT)
        sh, base, li = MAT[mat]
        if lam < -.05: return sh, lam
        if lam < .45: return base, lam
        return li, lam

    def face(self, pts3, normal, mat, texture=None, ink=1.8, wobble=.5, outline=True):
        cv = self.cv
        pts = [self.p(*q) for q in pts3]
        s = self.next()
        m = cv.poly(pts, wobble=wobble, cell=8, seed=s, soft=.3)
        col, lam = self.tone(normal, mat)
        cv.wash(m, col, var=.07, edge=.16, gran=.05, cell=18, seed=s)
        if texture:
            texture(self, m, pts3, normal, mat, lam)
        if outline:
            cv.outline(m, INK, width=ink, breaks=.1, cell=6, seed=s, opacity=.92)
        return m

    # ---- solids ----
    def box(self, x0, x1, y0, y1, z0, z1, mat='plaster', tex=None, top=False):
        """Front (south) and east faces; top only if nothing covers it."""
        e = self.face([(x1, y0, z0), (x1, y1, z0), (x1, y1, z1), (x1, y0, z1)], (1, 0, 0), mat, tex)
        f = self.face([(x0, y0, z0), (x1, y0, z0), (x1, y0, z1), (x0, y0, z1)], (0, -1, 0), mat, tex)
        if top:
            self.face([(x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1)], (0, 0, 1), mat, tex)
        return f, e

    def gable_x(self, x0, x1, y0, y1, z, h, mat='tile', o=3, wall='plaster', wtex=None):
        """Ridge along x (long side faces the viewer): back slope, east gable, front slope."""
        ym = (y0 + y1) / 2
        run = (y1 - y0) / 2
        self.face([(x0 - o, ym, z + h), (x1 + o, ym, z + h), (x1 + o, y1 + o, z - o * h / run), (x0 - o, y1 + o, z - o * h / run)],
                  (0, run, h), mat, ROOF_TEX.get(mat))
        self.face([(x1, y0, z), (x1, y1, z), (x1, ym, z + h)], (1, 0, 0), wall, wtex)
        self.face([(x0 - o, ym, z + h), (x1 + o, ym, z + h), (x1 + o, y0 - o, z - o * h / run), (x0 - o, y0 - o, z - o * h / run)],
                  (0, -run, h), mat, ROOF_TEX.get(mat))

    def gable_y(self, x0, x1, y0, y1, z, h, mat='tile', o=3, wall='plaster', wtex=None):
        """Ridge along y (gable end faces the viewer): west slope, front gable, east slope."""
        xm = (x0 + x1) / 2; run = (x1 - x0) / 2
        self.face([(xm, y0 - o, z + h), (xm, y1 + o, z + h), (x0 - o, y1 + o, z - o * h / run), (x0 - o, y0 - o, z - o * h / run)],
                  (-h, 0, run), mat, ROOF_TEX.get(mat))
        self.face([(x0, y0, z), (x1, y0, z), (xm, y0, z + h)], (0, -1, 0), wall, wtex)
        self.face([(xm, y0 - o, z + h), (xm, y1 + o, z + h), (x1 + o, y1 + o, z - o * h / run), (x1 + o, y0 - o, z - o * h / run)],
                  (h, 0, run), mat, ROOF_TEX.get(mat))

    def lean(self, x0, x1, y0, y1, z_low, z_high, mat='tile', o=3):
        """Single slope falling toward the viewer."""
        self.face([(x0 - o, y1, z_high), (x1 + o, y1, z_high), (x1 + o, y0 - o, z_low), (x0 - o, y0 - o, z_low)],
                  (0, -(z_high - z_low), y1 - y0), mat, ROOF_TEX.get(mat))

    def _hull(self, pts):
        from scipy.spatial import ConvexHull
        pts = np.asarray(pts)
        return [tuple(pts[i]) for i in ConvexHull(pts).vertices]

    def _banded(self, m, x_l, x_r, mat, cuts=(.3, .64)):
        cv = self.cv
        sh, bs, li = MAT[mat]
        xs = np.arange(cv.W) / SS
        u = np.clip((xs - x_l) / (x_r - x_l + 1e-9), 0, 1)
        cols = np.where(u[:, None] < cuts[0], li[None], np.where(u[:, None] < cuts[1], bs[None], sh[None]))
        field = np.broadcast_to(cols[None], (cv.H, cv.W, 3))
        n1 = cv.fbm(16, 3, self.seed)
        cv.over(m, np.clip(field * (1 + .07 * (n1 - .5) * 2)[..., None], 0, 1))

    def cylinder(self, cx, cy, r, z0, z1, mat='stone', tex=None, n=48):
        cv = self.cv
        ring = lambda z: [self.p(cx + r * math.cos(a), cy + r * math.sin(a), z) for a in np.linspace(0, 2 * math.pi, n, endpoint=False)]
        s = self.next()
        m = cv.poly(self._hull(ring(z0) + ring(z1)), wobble=.4, cell=8, seed=s, soft=.3)
        self._banded(m, self.p(cx - r, cy, 0)[0], self.p(cx + r, cy, 0)[0], mat)
        if tex:
            tex(self, m, None, (0, -1, 0), mat, 0)
        cv.outline(m, INK, width=1.8, breaks=.1, cell=6, seed=s, opacity=.92)
        return m

    def disc(self, cx, cy, r, z, mat='stone', n=48):
        cv = self.cv
        pts = [self.p(cx + r * math.cos(a), cy + r * math.sin(a), z) for a in np.linspace(0, 2 * math.pi, n, endpoint=False)]
        m = cv.poly(pts, wobble=.3, cell=8, seed=self.next(), soft=.3)
        cv.wash(m, MAT[mat][2], var=.06, edge=.2)
        cv.outline(m, INK, width=1.5, breaks=.1, opacity=.9)
        return m

    def cone(self, cx, cy, r, z0, h, mat='tile', n=48):
        cv = self.cv
        apex = self.p(cx, cy, z0 + h)
        rim = [self.p(cx + r * math.cos(a), cy + r * math.sin(a), z0) for a in np.linspace(0, 2 * math.pi, n, endpoint=False)]
        s = self.next()
        m = cv.poly(self._hull(rim + [apex]), wobble=.4, cell=8, seed=s, soft=.3)
        self._banded(m, self.p(cx - r, cy, 0)[0], self.p(cx + r, cy, 0)[0], mat, (.35, .66))
        sh = MAT[mat][0]
        lines = []
        for a in np.linspace(math.pi * 1.08, math.pi * 1.92, 9):
            q = self.p(cx + r * math.cos(a), cy + r * math.sin(a), z0)
            lines.append([(apex[0] + (q[0] - apex[0]) * .15, apex[1] + (q[1] - apex[1]) * .15), q])
        cv.over(_line_mask(cv, lines, .9) * m * .5, mix(sh, INK, .4))
        cv.outline(m, INK, width=1.8, breaks=.1, cell=6, seed=s, opacity=.92)
        return m

    def crenels(self, x0, x1, y0, z, h, w=6, mat='stone'):
        x = x0
        while x + w <= x1 + .1:
            self.box(x, x + w, y0, y0 + w, z, z + h, mat, top=True)
            x += w * 2

    def ring_crenels(self, cx, cy, r, z, h, count=9, mat='stone'):
        for a in np.linspace(math.pi * 1.05, math.pi * 1.95, count):
            x = cx + r * math.cos(a) - 2.5; y = cy + r * math.sin(a)
            self.box(x, x + 5, y, y + 4, z, z + h, mat, top=True)

    # ---- openings ----
    def window(self, x, y, z, w, h, face='front', glow=False, shutters=False):
        cv = self.cv
        if face == 'front':
            q = [(x, y, z), (x + w, y, z), (x + w, y, z + h), (x, y, z + h)]
        else:
            q = [(x, y, z), (x, y + w, z), (x, y + w, z + h), (x, y, z + h)]
        pts = [self.p(*v) for v in q]
        m = cv.poly(pts, wobble=.2, cell=4, seed=self.next(), soft=.25)
        cv.wash(m, GLOW if glow else WINDOW, var=.05, edge=0)
        cv.outline(m, INK, width=1.1, breaks=0, opacity=.9)
        if shutters:
            for side in (-1, 1):
                sx = x - w * .45 if side < 0 else x + w
                if face == 'front':
                    sq = [(sx, y, z), (sx + w * .45, y, z), (sx + w * .45, y, z + h), (sx, y, z + h)]
                else:
                    sy = y - w * .45 if side < 0 else y + w
                    sq = [(x, sy, z), (x, sy + w * .45, z), (x, sy + w * .45, z + h), (x, sy, z + h)]
                sm = cv.poly([self.p(*v) for v in sq], wobble=.2, cell=4, seed=self.next(), soft=.25)
                cv.wash(sm, mix(LEAF_SHADE, WOOD, .4), var=.05, edge=0)

    def door(self, x, y, w, h, face='front', arch=False):
        cv = self.cv
        if face == 'front':
            q = [(x, y, 0), (x + w, y, 0), (x + w, y, h), (x + w / 2, y, h + (w * .45 if arch else 0)), (x, y, h)]
        else:
            q = [(x, y, 0), (x, y + w, 0), (x, y + w, h), (x, y + w / 2, h + (w * .45 if arch else 0)), (x, y, h)]
        m = cv.poly([self.p(*v) for v in q], wobble=.25, cell=4, seed=self.next(), soft=.25)
        cv.wash(m, mix(WOOD, INK, .25), var=.08, edge=.2)
        a, b = self.p(*q[0]), self.p(*q[1])
        for t in (.33, .66):
            xa = a[0] + (b[0] - a[0]) * t; ya = a[1] + (b[1] - a[1]) * t
            cv.pen([(xa, ya), (xa, ya - h * .9)], .8, INK, .5, (0, 0), clip=m)
        cv.outline(m, INK, width=1.2, breaks=0, opacity=.95)

    def ground_shadow(self, x0, x1, y0, y1, height, opacity=.34):
        """Cast shadow falling to the south-east on the grass."""
        cv = self.cv
        d = height * .55
        pts = [self.p(x0, y0, 0), self.p(x1, y0, 0), self.p(x1 + d, y0 - d * .5, 0), self.p(x1 + d, y1 - d * .5, 0), self.p(x1, y1, 0)]
        m = cv.poly(pts, wobble=1, cell=10, seed=self.next(), soft=2.2)
        cv.over(m * opacity, GROUND_SHADOW)


# ---------------- material brushes ----------------

def _face_frame(pr, pts3):
    """Screen-space u (along first edge) and v (up) vectors for a face."""
    a, b, c = (np.array(pr.p(*q)) for q in pts3[:3])
    return a, b - a, c - b


def tile_tex(pr, m, pts3, normal, mat, lam):
    """Flat brush rows of roof tiles, each row a slightly darker lower edge."""
    cv = pr.cv
    a, u, v = _face_frame(pr, pts3)
    rows = max(3, int(np.linalg.norm(v) / 5))
    sh = MAT[mat][0]
    rng = np.random.default_rng(pr.next())
    lines = []
    for i in range(1, rows):
        t = i / rows
        p0 = a + v * t; p1 = p0 + u
        lines.append([tuple(p0), tuple(p1)])
    cv.pens(lines, 1.2, mix(sh, INK, .3), .55, (0, 0))
    ticks = []
    for i in range(rows):
        for j in range(int(np.linalg.norm(u) / 5)):
            t = (i + rng.uniform(.2, .8)) / rows; s = (j + rng.uniform(0, 1)) / max(1, np.linalg.norm(u) / 5)
            p = a + v * t + u * s
            ticks.append([tuple(p), tuple(p + v / rows * .8)])
    cv.pens(ticks, .9, mix(sh, INK, .2), .4, (0, 0))


def thatch_tex(pr, m, pts3, normal, mat, lam):
    cv = pr.cv
    a, u, v = _face_frame(pr, pts3)
    rng = np.random.default_rng(pr.next())
    n = int(np.linalg.norm(u) * np.linalg.norm(v) / 14)
    lines = []
    for _ in range(n):
        p = a + u * rng.uniform(0, 1) + v * rng.uniform(0, 1)
        lines.append([tuple(p), tuple(p - v / max(1, np.linalg.norm(v)) * rng.uniform(4, 8))])
    cv.pens(lines, .9, THATCH_SHADE, .6, (.2, .4))
    cv.pens([[tuple(a + v * t), tuple(a + v * t + u)] for t in (.34, .67)], 1.3, mix(THATCH_SHADE, INK, .3), .5, (0, 0))


def slate_tex(pr, m, pts3, normal, mat, lam):
    cv = pr.cv
    a, u, v = _face_frame(pr, pts3)
    rows = max(3, int(np.linalg.norm(v) / 4.5))
    cv.pens([[tuple(a + v * (i / rows)), tuple(a + v * (i / rows) + u)] for i in range(1, rows)], 1.0, SLATE_SHADE, .6, (0, 0))


def stone_tex(pr, m, pts3, normal, mat, lam):
    """Pen masonry: irregular courses of blocks."""
    cv = pr.cv
    rng = np.random.default_rng(pr.next())
    ys, xs = np.nonzero(m > .6)
    if len(xs) == 0: return
    x0, x1, y0, y1 = xs.min() / SS, xs.max() / SS, ys.min() / SS, ys.max() / SS
    sh = MAT[mat][0]
    lines = []
    y = y0 + rng.uniform(2, 5)
    slope = 0
    if pts3 is not None:
        a, u, v = _face_frame(pr, pts3)
        slope = u[1] / (u[0] + 1e-9)
    while y < y1:
        x = x0 + rng.uniform(0, 5)
        while x < x1:
            L = rng.uniform(5, 10)
            if rng.random() < .8:
                lines.append([(x, y + slope * (x - x0)), (x + L, y + slope * (x + L - x0))])
            lines.append([(x + L, y + slope * (x + L - x0)), (x + L, y + slope * (x + L - x0) + 4.5)])
            x += L
        y += rng.uniform(4, 5.5)
    lm = _line_mask(cv, lines, .8)
    cv.over(lm * m * .5, mix(sh, INK, .45))


def _line_mask(cv, lines, width):
    from PIL import Image, ImageDraw
    im = Image.new('L', (cv.W, cv.H), 0); d = ImageDraw.Draw(im)
    for l in lines:
        d.line([(x * SS, y * SS) for x, y in l], fill=255, width=max(1, int(width * SS)))
    return ndi.gaussian_filter(np.asarray(im, float) / 255, .3 * SS)


def plaster_tex(pr, m, pts3, normal, mat, lam):
    cv = pr.cv
    area = int(m.sum() / SS / SS)
    cv.stipple(m, MAT[mat][0], area // 90, (.6, 1.2), .35, pr.next())


def timber_tex(pr, m, pts3, normal, mat, lam):
    """Plaster with a half-timber frame drawn with a broad pen."""
    plaster_tex(pr, m, pts3, normal, mat, lam)
    cv = pr.cv
    a, u, v = _face_frame(pr, pts3)
    if len(pts3) == 3:
        return
    lines = [[tuple(a + v * .5), tuple(a + v * .5 + u)], [tuple(a + v), tuple(a + v + u)]]
    n = max(2, int(np.linalg.norm(u) / 14))
    for i in range(n + 1):
        t = i / n
        lines.append([tuple(a + u * t), tuple(a + u * t + v)])
    for i in range(n):
        t0, t1 = i / n, (i + 1) / n
        if i % 2 == 0:
            lines.append([tuple(a + u * t0 + v * .5), tuple(a + u * t1 + v)])
    lm = _line_mask(cv, lines, 2.2)
    cv.over(lm * m * .92, TIMBER)


def plank_tex(pr, m, pts3, normal, mat, lam):
    cv = pr.cv
    a, u, v = _face_frame(pr, pts3)
    n = max(3, int(np.linalg.norm(u) / 4))
    lines = [[tuple(a + u * (i / n)), tuple(a + u * (i / n) + v)] for i in range(1, n)]
    cv.over(_line_mask(cv, lines, .8) * m * .55, mix(WOOD, INK, .5))


ROOF_TEX = {'tile': tile_tex, 'thatch': thatch_tex, 'slate': slate_tex}
