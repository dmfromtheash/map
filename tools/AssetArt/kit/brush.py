"""Hand-painted look for the Regional Atlas stamps: watercolour washes, pigment pooling at the
edges, broken brush outlines in the object's own dark hue, tapered brush strokes and a soft
contact shadow. Everything is procedural (numpy + scipy + pillow); no third-party artwork."""
import math
import numpy as np
from PIL import Image, ImageDraw
from scipy import ndimage as ndi

SS = 2  # supersampling factor; every coordinate passed in is in final pixels


def hexc(h):
    h = h.lstrip('#')
    return np.array([int(h[i:i + 2], 16) / 255 for i in (0, 2, 4)])


def mix(a, b, t):
    return np.asarray(a) * (1 - t) + np.asarray(b) * t


class Canvas:
    def __init__(self, w, h, seed=0):
        self.w, self.h = w, h
        self.W, self.H = w * SS, h * SS
        self.rgb = np.zeros((self.H, self.W, 3))  # premultiplied
        self.a = np.zeros((self.H, self.W))
        self.seed = seed
        self._n = 0
        self._noise = {}

    # ---------- noise ----------
    def rng(self):
        self._n += 1
        return np.random.default_rng(self.seed * 1000 + self._n)

    def noise(self, cell, seed=None):
        """Smooth value noise 0..1 over the whole canvas, one field per (cell, seed)."""
        key = (cell, seed)
        if key not in self._noise:
            r = np.random.default_rng(self.seed * 7919 + int(cell * 13) + (seed or 0) * 104729)
            c = max(2, int(cell * SS))
            g = r.random((self.H // c + 4, self.W // c + 4))
            up = ndi.zoom(g, c, order=3)[c:c + self.H, c:c + self.W]
            up = ((up - up.min()) / (np.ptp(up) + 1e-9)).astype(np.float32)
            if len(self._noise) > 24:  # low-RAM PC: keep only recent fields
                self._noise.pop(next(iter(self._noise)))
            self._noise[key] = up
        return self._noise[key]

    def fbm(self, cell, octaves=3, seed=0):
        out = np.zeros((self.H, self.W)); amp = 1; tot = 0
        for o in range(octaves):
            out += amp * self.noise(cell / 2 ** o, seed + o); tot += amp; amp *= .5
        return out / tot

    # ---------- masks ----------
    def _draw(self, fn):
        im = Image.new('L', (self.W, self.H), 0)
        fn(ImageDraw.Draw(im))
        return np.asarray(im, dtype=np.float64) / 255

    def warp(self, m, amp=2.0, cell=18, seed=0):
        if amp <= 0:
            return m
        ys, xs = np.nonzero(m > 0.001)
        if len(ys) == 0:
            return m
        pad = int(amp * SS * 2 + 4)
        y0, y1 = max(0, ys.min() - pad), min(self.H, ys.max() + pad + 1)
        x0, x1 = max(0, xs.min() - pad), min(self.W, xs.max() + pad + 1)
        dx = (self.noise(cell, 11 + seed)[y0:y1, x0:x1] - .5) * 2 * amp * SS
        dy = (self.noise(cell, 23 + seed)[y0:y1, x0:x1] - .5) * 2 * amp * SS
        yy, xx = np.mgrid[y0:y1, x0:x1]
        out = np.zeros_like(m)
        out[y0:y1, x0:x1] = ndi.map_coordinates(m, [yy + dy, xx + dx], order=1, mode='constant')
        return out

    def poly(self, pts, wobble=1.0, cell=16, seed=0, soft=0.35):
        p = [(x * SS, y * SS) for x, y in pts]
        m = self._draw(lambda d: d.polygon(p, fill=255))
        m = self.warp(m, wobble, cell, seed)
        return ndi.gaussian_filter(m, soft * SS) if soft else m

    def ellipse(self, cx, cy, rx, ry, wobble=1.0, cell=10, seed=0, soft=0.35):
        m = self._draw(lambda d: d.ellipse([(cx - rx) * SS, (cy - ry) * SS, (cx + rx) * SS, (cy + ry) * SS], fill=255))
        m = self.warp(m, wobble, cell, seed)
        return ndi.gaussian_filter(m, soft * SS) if soft else m

    def line_mask(self, pts, width, taper=(0.35, 0.35), jitter=0.0, seed=0, blur=.45):
        """Tapered brush stroke along a polyline; width in final px."""
        pts = np.asarray(pts, float)
        seg = np.hypot(*np.diff(pts, axis=0).T)
        L = seg.sum() + 1e-9
        t = np.concatenate([[0], np.cumsum(seg)]) / L
        # resample densely
        n = max(4, int(L * SS / 1.5))
        tt = np.linspace(0, 1, n)
        xs = np.interp(tt, t, pts[:, 0]); ys = np.interp(tt, t, pts[:, 1])
        if jitter:
            r = np.random.default_rng(seed)
            k = max(3, n // 12)
            jx = ndi.gaussian_filter1d(r.normal(0, jitter, n), k); jy = ndi.gaussian_filter1d(r.normal(0, jitter, n), k)
            xs, ys = xs + jx, ys + jy
        a, b = taper
        wv = np.ones(n)
        if a > 0: wv = np.minimum(wv, np.clip(tt / a, 0, 1) ** .7)
        if b > 0: wv = np.minimum(wv, np.clip((1 - tt) / b, 0, 1) ** .7)
        wv = 0.25 + 0.75 * wv

        def fn(d):
            for i in range(n):
                r_ = max(.5, width * wv[i] * SS / 2)
                x, y = xs[i] * SS, ys[i] * SS
                d.ellipse([x - r_, y - r_, x + r_, y + r_], fill=255)
        m = self._draw(fn)
        return ndi.gaussian_filter(m, blur * SS)

    # ---------- paint ----------
    def over(self, alpha, color):
        alpha = np.clip(alpha, 0, 1)
        col = np.asarray(color, float)
        if col.ndim == 1:
            col = col[None, None, :]
        self.rgb = col * alpha[..., None] + self.rgb * (1 - alpha[..., None])
        self.a = alpha + self.a * (1 - alpha)

    def multiply(self, alpha, color, strength=1.0):
        """Darken existing paint (glaze) without adding coverage."""
        alpha = np.clip(alpha * strength, 0, 1)[..., None]
        col = np.asarray(color, float).reshape(1, 1, 3) if np.ndim(color) == 1 else color
        self.rgb = self.rgb * (1 - alpha + alpha * col)

    def wash(self, mask, base, var=.10, edge=.22, gran=.05, cell=22, seed=0, opacity=1.0, light=None):
        """Watercolour fill: uneven pigment, darker pooled rim, paper granulation.
        light=(dx,dy,amount) brightens toward that direction inside the shape."""
        base = np.asarray(base, float)
        n1 = self.fbm(cell, 3, seed)
        n2 = self.noise(2.2, seed + 5)
        tone = 1 + var * (n1 - .5) * 2 + gran * (n2 - .5) * 2
        col = base[None, None, :] * tone[..., None]
        if light is not None:
            ys, xs = np.nonzero(mask > .3)
            if len(xs):
                dx, dy, amt = light
                cx, cy = xs.mean(), ys.mean()
                ext = max(np.ptp(xs), np.ptp(ys), 1) / 2
                yy, xx = np.mgrid[0:self.H, 0:self.W]
                g = np.clip(((xx - cx) * dx + (yy - cy) * dy) / ext, -1, 1)
                col = col * (1 + amt * g)[..., None]
        if edge:
            d = ndi.distance_transform_edt(mask > .5)
            rim = np.exp(-d / (3.2 * SS))
            col = col * (1 - edge * rim)[..., None]
        alpha = mask * opacity * (0.93 + 0.07 * n1)
        self.over(alpha, np.clip(col, 0, 1))

    def outline(self, mask, color, width=1.6, breaks=.18, cell=9, seed=0, keep=None, opacity=.9):
        """Broken brush contour along the inside of a shape; keep = 0..1 field that fades it out."""
        inside = mask > .5
        d = ndi.distance_transform_edt(inside)
        n = self.noise(cell, seed + 31)
        w = width * SS * (.55 + .9 * n)
        band = np.clip(w - d, 0, 1) * inside
        if breaks:
            gap = self.noise(cell * 2.3, seed + 47)
            band *= np.clip((gap - breaks) / .12, 0, 1)
        band = ndi.gaussian_filter(band, .3 * SS)
        if keep is not None:
            band *= keep
        self.over(band * opacity, color)

    def stroke(self, pts, width, color, opacity=.85, taper=(.35, .35), jitter=.0, seed=0):
        self.over(self.line_mask(pts, width, taper, jitter * SS, seed) * opacity, color)

    def fade_below(self, y0, y1):
        """Vertical keep-field: 1 above y0, fading to 0 at y1 (final px)."""
        yy = np.arange(self.H)[:, None] / SS
        return np.broadcast_to(np.clip((y1 - yy) / max(1e-6, y1 - y0), 0, 1), (self.H, self.W))

    def shadow(self, cx, cy, rx, ry, color, opacity=.32, blur=5):
        m = self.ellipse(cx, cy, rx, ry, wobble=2, cell=14, soft=blur)
        self.over(m * opacity, color)

    # ---------- brush kit ----------
    # Each brush leaves a different mark, so objects read clearly at map scale:
    #   pen      crisp tapered ink line (contours, timber, cracks)
    #   hatch    parallel pen strokes clipped to a shape (shadow sides)
    #   dry      bristle brush: streaky broken paint (bark, rock grain, thatch)
    #   flat     flat brush with a fixed nib angle (tiles, planks, cliff faces)
    #   stipple  sponge / round dabs inside a mask (foliage, gravel, moss)
    #   glaze    transparent wash over existing paint (shade, light)
    def pen(self, pts, width, color, opacity=.95, taper=(.2, .3), jitter=.0, seed=0, clip=None):
        m = self.line_mask(pts, width, taper, jitter * SS, seed, blur=.28)
        if clip is not None:
            m = m * clip
        self.over(m * opacity, color)

    def pens(self, lines, width, color, opacity=.9, taper=(.2, .5), blur=.28):
        """Many short pen strokes in one pass (grass blades, cracks, tile marks)."""
        im = Image.new('L', (self.W, self.H), 0); d = ImageDraw.Draw(im)
        for pts in lines:
            pts = np.asarray(pts, float)
            n = max(3, int(np.hypot(*np.diff(pts, axis=0).T).sum() * SS / 1.5))
            t = np.linspace(0, 1, n)
            seg = np.concatenate([[0], np.cumsum(np.hypot(*np.diff(pts, axis=0).T))]); seg /= seg[-1] + 1e-9
            xs = np.interp(t, seg, pts[:, 0]) * SS; ys = np.interp(t, seg, pts[:, 1]) * SS
            a, b = taper
            wv = np.minimum(np.clip(t / max(a, 1e-3), 0, 1) if a else 1, np.clip((1 - t) / max(b, 1e-3), 0, 1) if b else 1)
            wv = .3 + .7 * np.asarray(wv) * np.ones(n)
            for x, y, w in zip(xs, ys, wv):
                r_ = max(.5, width * w * SS / 2)
                d.ellipse([x - r_, y - r_, x + r_, y + r_], fill=255)
        m = ndi.gaussian_filter(np.asarray(im, float) / 255, blur * SS)
        self.over(m * opacity, color)

    def hatch(self, mask, color, angle=62, spacing=3.2, width=.8, opacity=.6, length=(4, 9), seed=0, density=1.0):
        """Short parallel pen strokes scattered over a mask."""
        r = np.random.default_rng(seed)
        ys, xs = np.nonzero(mask > .5)
        if len(xs) == 0:
            return
        area = len(xs) / SS / SS
        n = int(area / (spacing * 6) * density)
        pick = r.choice(len(xs), size=min(n, len(xs)), replace=False)
        a = math.radians(angle)
        acc = np.zeros((self.H, self.W))
        im = Image.new('L', (self.W, self.H), 0); d = ImageDraw.Draw(im)
        for k in pick:
            x, y = xs[k] / SS, ys[k] / SS
            L = r.uniform(*length)
            dx, dy = math.cos(a) * L / 2, math.sin(a) * L / 2
            d.line([((x - dx) * SS, (y - dy) * SS), ((x + dx) * SS, (y + dy) * SS)], fill=255, width=max(1, int(width * SS)))
        acc = ndi.gaussian_filter(np.asarray(im, float) / 255, .3 * SS)
        self.over(acc * np.clip(mask, 0, 1) * opacity, color)

    def dry(self, pts, width, color, opacity=.8, bristles=7, seed=0, clip=None, taper=(.15, .5)):
        """Bristle stroke: several thin hairs, each broken where the brush runs dry."""
        r = np.random.default_rng(seed)
        pts = np.asarray(pts, float)
        d = np.diff(pts, axis=0); n = np.stack([-d[:, 1], d[:, 0]], 1)
        n = np.vstack([n, n[-1:]]); n /= np.linalg.norm(n, axis=1, keepdims=True) + 1e-9
        acc = np.zeros((self.H, self.W))
        for b in range(bristles):
            off = (b / max(1, bristles - 1) - .5) * width
            p = pts + n * off
            m = self.line_mask(p, max(.5, width / bristles * 1.3), taper, .3 * SS, seed + b, blur=.25)
            gap = self.noise(r.uniform(2, 5), seed + 100 + b)
            acc = np.maximum(acc, m * np.clip((gap - r.uniform(.25, .45)) / .1, 0, 1))
        if clip is not None:
            acc *= clip
        self.over(acc * opacity, color)

    def flat(self, pts, width, color, opacity=.85, angle=35, thin=.28, clip=None, seed=0):
        """Flat nib: broad or thin depending on stroke direction against the nib angle."""
        pts = np.asarray(pts, float)
        seg = np.hypot(*np.diff(pts, axis=0).T); L = seg.sum() + 1e-9
        t = np.concatenate([[0], np.cumsum(seg)]) / L
        n = max(4, int(L * SS / 1.2)); tt = np.linspace(0, 1, n)
        xs = np.interp(tt, t, pts[:, 0]) * SS; ys = np.interp(tt, t, pts[:, 1]) * SS
        a = math.radians(angle); hx, hy = math.cos(a) * width * SS / 2, math.sin(a) * width * SS / 2
        tx, ty = -math.sin(a) * width * thin * SS / 2, math.cos(a) * width * thin * SS / 2
        im = Image.new('L', (self.W, self.H), 0); d = ImageDraw.Draw(im)
        for x, y in zip(xs, ys):
            d.polygon([(x - hx - tx, y - hy - ty), (x + hx - tx, y + hy - ty), (x + hx + tx, y + hy + ty), (x - hx + tx, y - hy + ty)], fill=255)
        m = ndi.gaussian_filter(np.asarray(im, float) / 255, .3 * SS)
        if clip is not None:
            m *= clip
        self.over(m * opacity, color)

    def stipple(self, mask, color, count, size=(1.2, 2.4), opacity=.8, seed=0, where=None, squash=.8):
        """Round sponge dabs scattered inside mask (optionally weighted by `where`)."""
        r = np.random.default_rng(seed)
        w = np.clip(mask, 0, 1) if where is None else np.clip(mask, 0, 1) * np.clip(where, 0, 1)
        ys, xs = np.nonzero(w > .35)
        if len(xs) == 0:
            return
        pick = r.choice(len(xs), size=min(count, len(xs)), replace=False)
        im = Image.new('L', (self.W, self.H), 0); d = ImageDraw.Draw(im)
        for k in pick:
            x, y = xs[k], ys[k]; rr = r.uniform(*size) * SS
            d.ellipse([x - rr, y - rr * squash, x + rr, y + rr * squash], fill=int(255 * r.uniform(.6, 1)))
        m = ndi.gaussian_filter(np.asarray(im, float) / 255, .3 * SS)
        self.over(m * np.clip(mask, 0, 1) * opacity, color)

    def glaze(self, mask, color, opacity=.5):
        self.over(np.clip(mask, 0, 1) * opacity, color)

    # ---------- output ----------
    def image(self):
        a = self.a
        # premultiplied downsample to avoid dark fringes
        pm = np.dstack([self.rgb, a])
        small = []
        for ch in range(4):
            c = Image.fromarray(np.clip(pm[..., ch], 0, 1).astype(np.float32), 'F')
            small.append(np.asarray(c.resize((self.w, self.h), Image.LANCZOS)))
        s = np.clip(np.dstack(small), 0, 1)
        al = s[..., 3]
        col = np.where(al[..., None] > 1e-4, s[..., :3] / np.maximum(al[..., None], 1e-4), 0)
        col = np.where(al[..., None] > 1e-3, col, 0)
        out = np.dstack([np.clip(col, 0, 1), al])
        return Image.fromarray((out * 255 + .5).astype(np.uint8), 'RGBA')


def trim(im, pad=8):
    bb = im.getchannel('A').point(lambda v: 255 if v > 2 else 0).getbbox()
    im = im.crop(bb)
    out = Image.new('RGBA', (im.width + 2 * pad, im.height + 2 * pad), (0, 0, 0, 0))
    out.paste(im, (pad, pad))
    return out
