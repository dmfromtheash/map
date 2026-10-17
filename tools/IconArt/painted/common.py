"""Shared pixel tools for Map's painted icon studies: noise, masks, compositing, glow, saving.

Everything works on float RGB(A) numpy arrays in 0..1 at a working size (1024 px), so textures
(paper grain, glass streaks, ink wobble, watercolour edges) are real pixels, not flat vector fills.
"""
import numpy as np
from PIL import Image, ImageDraw, ImageFilter
from scipy import ndimage

N = 1024


def rng(seed):
    return np.random.default_rng(seed)


def fbm(size, seed, octaves=5, base=4, persistence=0.55):
    """Fractal value noise in 0..1, tileless, smooth (upsampled random grids)."""
    r = rng(seed)
    out = np.zeros((size, size), np.float32)
    amp, total = 1.0, 0.0
    cells = base
    for _ in range(octaves):
        grid = r.random((cells + 1, cells + 1)).astype(np.float32)
        layer = ndimage.zoom(grid, size / cells, order=3)[:size, :size]
        out += layer * amp
        total += amp
        amp *= persistence
        cells *= 2
    out /= total
    out -= out.min()
    out /= max(out.max(), 1e-6)
    return out


def mask_from(draw_fn, size=N, supersample=2, blur=0.0):
    """A soft 0..1 mask drawn with PIL at supersampled resolution."""
    big = Image.new("L", (size * supersample, size * supersample), 0)
    d = ImageDraw.Draw(big)
    draw_fn(d, supersample)
    m = big.resize((size, size), Image.LANCZOS)
    if blur:
        m = m.filter(ImageFilter.GaussianBlur(blur))
    return np.asarray(m, np.float32) / 255.0


def hexrgb(h):
    h = h.lstrip("#")
    return np.array([int(h[i:i + 2], 16) / 255 for i in (0, 2, 4)], np.float32)


def layer(canvas, color, mask):
    """Paints a colour (rgb or HxWx3 array) through a mask onto an RGBA canvas (straight alpha, 'over')."""
    m = mask[..., None]
    col = color if isinstance(color, np.ndarray) and color.ndim == 3 else np.broadcast_to(color, canvas[..., :3].shape)
    a = canvas[..., 3:4]
    out_a = m + a * (1 - m)
    canvas[..., :3] = np.where(out_a > 1e-6, (col * m + canvas[..., :3] * a * (1 - m)) / np.maximum(out_a, 1e-6), canvas[..., :3])
    canvas[..., 3:4] = out_a
    return canvas


def blank(size=N):
    return np.zeros((size, size, 4), np.float32)


def glow(canvas_rgb, mask, color, radius, strength):
    g = ndimage.gaussian_filter(mask, radius)
    return np.clip(canvas_rgb + g[..., None] * color * strength, 0, 1)


def shadow(canvas, mask, offset=(10, 16), blur=14, alpha=0.6):
    s = ndimage.shift(mask, offset, order=1)
    s = ndimage.gaussian_filter(s, blur) * alpha
    return layer(canvas, np.zeros(3, np.float32), s)


def save(canvas, path, sizes=(1024, 256, 64, 32, 16)):
    img = Image.fromarray((np.clip(canvas, 0, 1) * 255).astype(np.uint8), "RGBA")
    stem = path[:-4]
    for s in sizes:
        out = img if s == img.width else img.resize((s, s), Image.LANCZOS)
        if s <= 64:
            out = out.filter(ImageFilter.UnsharpMask(radius=1, percent=60, threshold=2))
        out.save(f"{stem}-{s}.png")


def circle(cx, cy, r):
    return lambda d, k: d.ellipse([(cx - r) * k, (cy - r) * k, (cx + r) * k, (cy + r) * k], fill=255)


def polygon(points):
    return lambda d, k: d.polygon([(x * k, y * k) for x, y in points], fill=255)


def yy_xx(size=N):
    return np.mgrid[0:size, 0:size].astype(np.float32)
