"""Painted relief: a height field seen from the south at an oblique angle, lit from the north-west,
split into a few soft paint bands, with ink on silhouettes and occluding ridges and hatching in
shadow. Used for mountains, hills, plateaus and the volcano so all relief shares one hand."""
import math
import numpy as np
from scipy import ndimage as ndi
from brush import SS, mix
from palette import *


class Relief:
    def __init__(self, cv, base, depth, tilt=.55, seed=0):
        """base: screen y (final px) of the front edge; depth: world depth in final px."""
        self.cv = cv; self.seed = seed; self.tilt = tilt
        self.D = int(depth * SS / tilt)  # rows so that depth maps to `depth` screen px
        self.Wc = cv.W
        self.base = base * SS
        self.X, self.J = np.meshgrid(np.arange(self.Wc, dtype=float) / SS, np.arange(self.D, dtype=float) * tilt / SS)
        self.Jmax = (self.D - 1) * tilt / SS  # front row in final px of depth
        self.Z = np.zeros((self.D, self.Wc))

    # world helpers (final px): x across, j = depth from the back (0) to the front (Jmax)
    def screen(self, x, j, z):
        return x, (self.base / SS) - (self.Jmax - j) - z

    def noise(self, cell, seed=0, octaves=4, ridged=False):
        r = np.random.default_rng(self.seed * 31 + seed)
        out = np.zeros_like(self.Z); amp = 1; tot = 0
        for o in range(octaves):
            c = max(2, int(cell * SS / 2 ** o))
            g = r.random((self.D // c + 4, self.Wc // c + 4))
            up = ndi.zoom(g, c, order=3)[c:c + self.D, c:c + self.Wc]
            if ridged:
                up = 1 - np.abs(np.clip(up, 0, 1) * 2 - 1)
            out += amp * up; tot += amp; amp *= .5
        return out / tot

    def cone(self, px, pj, rx, rj, h, power=1.2, ridge=.0):
        d = np.sqrt(((self.X - px) / rx) ** 2 + ((self.J - pj) / rj) ** 2)
        s = np.clip(1 - d, 0, 1) ** power
        return h * s

    def render(self, colour, alpha=None, ink=ROCK_INK, ink_width=2.2, hatch=.45, ridges=.7, silhouette=.85,
               z_ink_min=2.0, foot_ink=.1):
        cv = self.cv
        D, Wc, H = self.D, self.Wc, cv.H
        idx, valid = self.project()
        cols = np.broadcast_to(np.arange(Wc), (H, Wc))
        col = colour[idx, cols]
        a = (alpha if alpha is not None else np.clip(self.Z / 1.5, 0, 1))[idx, cols] * valid
        zpix = self.Z[idx, cols]
        # ink: silhouettes and ridges that hide terrain behind them
        jump = np.zeros((H, Wc))
        dj = np.abs(np.diff(idx.astype(float), axis=0))
        jump[1:] = np.clip((dj - 6 * SS) / (10 * SS), 0, 1)
        jump *= np.clip((zpix - z_ink_min) / 4, 0, 1)
        jump = ndi.maximum_filter(jump, size=max(1, int(ink_width * SS * .8)))
        sil = np.clip(a - ndi.minimum_filter(a, size=int(ink_width * SS * 2) + 1), 0, 1)
        zref = self.Z.max() * foot_ink
        sil *= np.clip((ndi.maximum_filter(zpix, 5) - zref) / (zref * .6 + 1e-6), 0, 1)
        n = cv.noise(7, self.seed + 3)
        g = cv.noise(15, self.seed + 4)
        pressure = np.clip(.55 + .7 * n, 0, 1) * np.clip((g - .1) / .12, 0, 1)
        line = np.clip(jump * ridges + np.clip(sil, 0, 1) * silhouette, 0, 1) * pressure
        line = ndi.gaussian_filter(line, .3 * SS)
        cv.over(a, np.clip(col, 0, 1))
        solid = (a > .5).astype(float)
        # pen along the light/shadow boundary on ridges (where an illustrator draws the crest)
        if ridges:
            tp = self.t[idx, cols] if hasattr(self, 't') else None
            if tp is not None:
                lit = (tp > .6).astype(float); dark = (tp < .4).astype(float)
                k = max(3, int(ink_width * 1.1 * SS))
                crest = ndi.maximum_filter(lit, k) * ndi.maximum_filter(dark, k) * solid
                crest *= np.clip((zpix - self.Z.max() * .12) / (self.Z.max() * .1 + 1e-6), 0, 1)
                crest = ndi.gaussian_filter(crest * (.4 + .9 * n) * np.clip((g - .2) / .15, 0, 1), .35 * SS)
                cv.over(np.clip(crest * 1.4, 0, 1) * .8 * ridges, ink)
        # pen hatching down the shadowed faces
        if hatch:
            lum = col @ np.array([.3, .59, .11])
            shadow = (self.t[idx, cols] < .5).astype(float) * solid if hasattr(self, 't') else np.clip((.5 - lum) / .06, 0, 1) * solid
            shadow = ndi.minimum_filter(shadow, max(1, int(1.5 * SS)))
            cv.hatch(shadow, ink, angle=72, spacing=7, width=.9, opacity=.5 * hatch / .45, length=(5, 12),
                     seed=self.seed, density=1.0)
        cv.over(line * a * .9, ink)
        self.idx = idx; self.a = a
        return a

    def project(self):
        """Screen pixel -> visible grid row per column (front-most surface wins)."""
        Z = self.Z * SS
        D, Wc, H = self.D, self.Wc, self.cv.H
        rows = np.arange(D)[:, None]
        s = self.base - (D - 1 - rows) * self.tilt - Z
        M = np.minimum.accumulate(s[::-1], axis=0)[::-1]
        ys = np.arange(H, dtype=float)
        idx = np.empty((H, Wc), dtype=np.int32)
        for c in range(Wc):
            idx[:, c] = np.searchsorted(M[:, c], ys, side='right') - 1
        valid = idx >= 0
        idx = np.clip(idx, 0, D - 1)
        valid &= ys[:, None] <= s[D - 1][None, :] + SS
        return idx, valid

    # ---- shading ----
    def light(self, L=(-.62, -.55, .56), relief=.4):
        gz, gx = np.gradient(self.Z * relief, self.tilt / SS * 1.0, 1 / SS)
        nx, ny, nz = -gx, -gz, np.ones_like(gx)
        nn = np.sqrt(nx * nx + ny * ny + nz * nz)
        L = np.array(L) / np.linalg.norm(L)
        return (nx * L[0] + ny * L[1] + nz * L[2]) / nn, nz / nn

    def bands(self, lam, levels=(.3, .7), soft=.02, jitter=.03, seed=0):
        """Posterisation into paint bands -> 0..len(levels); levels are percentiles of the lit relief."""
        idx, valid = self.project()
        cols = np.broadcast_to(np.arange(self.Wc), idx.shape)
        zp = self.Z[idx, cols]
        body = lam[idx, cols][valid & (zp > self.Z.max() * .08)]
        levels = [np.quantile(body, l) for l in levels] if len(body) else levels
        v = lam + (self.noise(14, seed + 9, 2) - .5) * 2 * jitter
        out = np.zeros_like(v)
        for l in levels:
            out += np.clip((v - l) / soft * .5 + .5, 0, 1)
        return out

    def palette(self, t, tones):
        """t in 0..len(tones)-1 -> interpolated colour."""
        tones = np.asarray(tones)
        k = np.clip(np.floor(t).astype(int), 0, len(tones) - 2)
        f = (t - k)[..., None]
        return tones[k] * (1 - f) + tones[k + 1] * f


ROCK_TONES = [ROCK_SHADE, ROCK, ROCK_LIGHT]
SNOW_TONES = [SNOW_SHADE, mix(SNOW, SNOW_SHADE, .35), SNOW]
GREEN_TONES = [mix(GROUND, LEAF_SHADE, .45), GROUND, mix(GROUND, LEAF_LIGHT, .35)]
OCHRE_TONES = [OCHRE_SHADE, OCHRE, mix(OCHRE, ROCK_LIGHT, .5)]


def paint_mountain(rf, snow_line=None, green_line=None, rock=ROCK_TONES, green=GREEN_TONES, seed=0, snow=SNOW_TONES):
    lam, nz = rf.light()
    t = rf.bands(lam, seed=seed)
    rf.t = t
    colour = rf.palette(t, rock)
    Zmax = rf.Z.max() + 1e-6
    n = rf.noise(10, seed + 1, 3)
    if snow_line is not None:
        flat = np.clip((nz - .45) / .25, 0, 1)
        w = np.clip((rf.Z - (snow_line + (n - .5) * .12 * Zmax)) / (.04 * Zmax), 0, 1) * (.35 + .65 * flat)
        colour = mix(colour, rf.palette(t, snow), w[..., None])
    if green_line is not None:
        w = np.clip(((green_line + (n - .5) * .2 * Zmax) - rf.Z) / (.06 * Zmax), 0, 1)
        colour = mix(colour, rf.palette(t, green), w[..., None])
    grain = (rf.noise(1.5, seed + 2, 2) - .5) * .06
    colour = colour * (1 + grain)[..., None]
    return colour, lam, t


def foot_alpha(rf, cut=.06, seed=0):
    """Cut the long low tails of the relief with a ragged, crisp edge (no translucent pad)."""
    n = rf.noise(8, seed + 21, 3)
    z0 = rf.Z.max() * cut * (.5 + n)
    return np.clip((rf.Z - z0) / 1.2, 0, 1)


def grow_grass(cv, rf, a, height=.13, seed=0):
    """Sponge dabs of the ground's own greens creeping up the lowest slopes."""
    zp = rf.Z[rf.idx, np.broadcast_to(np.arange(rf.Wc), rf.idx.shape)]
    zmax = rf.Z.max()
    n = cv.noise(9, seed + 61)
    low = np.clip(((height * zmax) * (.3 + 1.4 * n) - zp) / (height * zmax * .3), 0, 1) * (a > .5)
    area = int(low.sum() / SS / SS)
    cv.stipple(low, mix(GROUND, LEAF_SHADE, .35), area // 5, (1.4, 2.8), .9, seed)
    cv.stipple(low, GROUND, area // 5, (1.3, 2.6), .9, seed + 1)
    cv.stipple(low, mix(GROUND, LEAF_LIGHT, .45), area // 14, (1.0, 2.0), .85, seed + 2, where=low > .6)
    ys, xs = np.nonzero((low > .3) & (low < .8))
    r = np.random.default_rng(seed)
    pick = r.choice(len(xs), size=min(len(xs), area // 40 + 1), replace=False) if len(xs) else []
    blades = []
    for k in pick:
        x, y = xs[k] / SS, ys[k] / SS
        for q in range(3):
            blades.append([(x + q * 1.4, y), (x + q * 1.4 + r.uniform(-1.5, 1.5), y - r.uniform(2.5, 5))])
    cv.pens(blades, .9, LEAF_INK, .55, (0, .9))


def downhill(rf, x, j, steps=400, step=1.0, stop=.06, wander=0.0, seed=0):
    """Path of steepest descent from (x, j): for waterfalls and lava."""
    gz, gx = np.gradient(rf.Z, rf.tilt / SS, 1 / SS)
    pts = []
    zstop = rf.Z.max() * stop
    rng = np.random.default_rng(seed); drift = 0.0
    for _ in range(steps):
        c = int(np.clip(x * SS, 0, rf.Wc - 1)); r = int(np.clip(j * SS / rf.tilt, 0, rf.D - 1))
        z = rf.Z[r, c]
        pts.append((x, j, z))
        if z < zstop:
            break
        vx, vj = -gx[r, c], -gz[r, c]
        L = math.hypot(vx, vj) + 1e-9
        drift = drift * .9 + rng.normal(0, wander)
        x += (vx / L + drift) * step; j += vj / L * step
    return pts
