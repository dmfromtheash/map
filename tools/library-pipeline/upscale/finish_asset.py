# One asset: colour-only ESRGAN inputs are prepared here, the two x4 results are blended,
# the silhouette comes from the source alpha, lost fine grain is restored, final factor 4 or 2.
import numpy as np
from PIL import Image
from scipy import ndimage as nd


def load_rgba(path):
    im = Image.open(path)
    im.load()
    return im.convert('RGBA')


def prepare_rgb(src_rgba, out_path):
    """Colour under transparent pixels = nearest visible colour, so the model sees no black fringe."""
    arr = np.asarray(src_rgba)
    a = arr[..., 3]
    rgb = arr[..., :3].copy()
    hidden = a < 8
    if hidden.any() and (~hidden).any():
        _, (iy, ix) = nd.distance_transform_edt(hidden, return_indices=True)
        rgb = rgb[iy, ix]
    Image.fromarray(np.ascontiguousarray(rgb), 'RGB').save(out_path, compress_level=1)


def _resize_f(channel, size, method):
    return np.asarray(Image.fromarray(np.ascontiguousarray(channel, dtype=np.float32), 'F').resize(size, method), np.float32)


def finish(src_rgba, a_path, b_path, out_path, factor, wa=0.6, grain_k=0.6, cycle_rgb=False):
    src = np.asarray(src_rgba, np.float32) / 255
    A = np.asarray(Image.open(a_path).convert('RGB'), np.float32) / 255
    B = np.asarray(Image.open(b_path).convert('RGB'), np.float32) / 255
    H, W = A.shape[:2]
    rgb = wa * A + (1 - wa) * B
    # silhouette 1:1 from the source: smooth x4, slight edge tightening
    if src[..., 3].min() >= 0.999:
        al = np.ones((H, W), np.float32)
    else:
        al = np.clip(_resize_f(src[..., 3], (W, H), Image.LANCZOS), 0, 1)
        sa = src[..., 3]
        visible = sa > 0.03
        soft = ((sa > 0.03) & (sa < 0.97)).sum() / max(visible.sum(), 1)
        if soft < 0.12:  # crisp cut-out: tighten the resampled edge; soft art (clouds, washes) keeps its alpha
            al = np.clip((al - 0.5) * 1.5 + 0.5, 0, 1)
    # grain the network smoothed away, at its local source strength, crisp at full resolution
    lum = src[..., :3].mean(2)
    hp = lum - nd.gaussian_filter(lum, 1.2)
    amp = np.sqrt(nd.gaussian_filter(hp * hp, 2.0))
    amp_up = _resize_f(amp, (W, H), Image.BICUBIC)
    lumu = rgb.mean(2)
    hpu = lumu - nd.gaussian_filter(lumu, 4.8)
    ampu = np.sqrt(nd.gaussian_filter(hpu * hpu, 8.0))
    missing = np.sqrt(np.clip(amp_up ** 2 - ampu ** 2, 0, None))
    n = nd.gaussian_filter(np.random.default_rng(1408).standard_normal((H, W)).astype(np.float32), 0.9)
    n /= n.std() + 1e-6
    rgb = np.clip(rgb + (grain_k * missing * n)[..., None], 0, 1)
    if cycle_rgb:
        # DEC-108: one colour-cycle correction of the usual mix0; alpha is handled separately.
        w, h = src_rgba.size
        back = np.dstack([_resize_f(rgb[..., c], (w, h), Image.BOX) for c in range(3)])
        delta = src[..., :3] - back
        rgb = np.clip(rgb + np.dstack([_resize_f(delta[..., c], (W, H), Image.LANCZOS)
                                      for c in range(3)]), 0, 1)
    out = np.dstack([rgb, al]).astype(np.float32)
    if factor != 4:
        w, h = src_rgba.size
        size = (w * factor, h * factor)
        pm = out.copy()
        pm[..., :3] *= pm[..., 3:4]
        small = np.clip(np.dstack([_resize_f(pm[..., c], size, Image.LANCZOS) for c in range(4)]), 0, 1)
        a = small[..., 3:4]
        small[..., :3] = np.where(a > 1e-4, small[..., :3] / np.maximum(a, 1e-4), 0)
        out = np.clip(small, 0, 1)
    img = Image.fromarray((out * 255 + 0.5).astype(np.uint8), 'RGBA')
    img.save(out_path, 'WEBP', lossless=True, quality=80, method=4)
    return img.size


def refs2_cycle(rel, size):
    return rel.replace('/', '\\').startswith('refs2\\_Магазин') and max(size) <= 256


def verify(src_rgba, out_path, factor):
    """The result opens, has the exact size, and keeps the source silhouette and colour."""
    out = Image.open(out_path)
    out.load()
    w, h = src_rgba.size
    if out.mode == 'RGB' and np.asarray(src_rgba)[..., 3].min() == 255:
        out = out.convert('RGBA')  # libwebp stores no alpha chunk when every pixel is opaque
    if out.size != (w * factor, h * factor) or out.mode != 'RGBA':
        return False, f'size/mode {out.size} {out.mode}', 0, 0
    back = np.asarray(out.resize((w, h), Image.BOX), np.float32)
    s = np.asarray(src_rgba, np.float32)
    da = float(np.abs(back[..., 3] - s[..., 3]).mean())
    m = s[..., 3] > 200
    dc = float(np.abs(back[m][:, :3] - s[m][:, :3]).mean()) if m.any() else 0.0
    ok = da < 12 and dc < 14
    return ok, f'alphaD={da:.1f} colourD={dc:.1f}', da, dc
