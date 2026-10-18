# Final masters: mountains -> trimmed RGBA, long side 4096 (dims multiple of 16) + full 4x archive;
# textures -> 2048 seamless tiles.
import sys, numpy as np
from PIL import Image
from scipy import ndimage as nd
kind, src, dst, archive = sys.argv[1:5]
im = Image.open(src).convert('RGBA')
if kind == 'mountain':
    a = np.asarray(im)[..., 3]
    ys, xs = np.where(a > 8)
    pad = 48
    x0, x1 = max(0, xs.min() - pad), min(im.width, xs.max() + 1 + pad)
    y0, y1 = max(0, ys.min() - pad), min(im.height, ys.max() + 1 + pad)
    im = im.crop((x0, y0, x1, y1))
    im.save(archive, compress_level=6)
    s = 4096 / max(im.size)
    w, h = (round(im.width * s / 16) * 16, round(im.height * s / 16) * 16)
    # resample in premultiplied space so transparent pixels do not bleed dark fringes
    arr = np.asarray(im, np.float32) / 255
    pm = np.dstack([arr[..., :3] * arr[..., 3:], arr[..., 3:]])
    chans = [Image.fromarray(pm[..., c]).resize((w, h), Image.LANCZOS) for c in range(4)]
    out = np.dstack([np.asarray(c, np.float32) for c in chans]).clip(0, 1)
    al = out[..., 3:]
    rgb = np.where(al > 1e-4, out[..., :3] / np.maximum(al, 1e-4), 0).clip(0, 1)
    Image.fromarray((np.dstack([rgb, al]) * 255 + .5).astype(np.uint8), 'RGBA').save(dst, compress_level=9)
    print(dst, (w, h), 'archive', im.size)
else:
    im = im.convert('RGB')
    im.save(archive, compress_level=6)
    side = min(im.size)
    im = im.crop(((im.width - side) // 2, (im.height - side) // 2, (im.width - side) // 2 + side, (im.height - side) // 2 + side))
    N = 2048
    t = np.asarray(im.resize((N, N), Image.LANCZOS), np.float32) / 255
    if len(sys.argv) > 5 and sys.argv[5] == 'flatten':
        # even out the painting's overall light (brighter middle, darker corners): repeated,
        # it would show as a grid of light and dark squares across the map
        low = np.stack([nd.gaussian_filter(t[..., c], N / 7, mode='reflect') for c in range(3)], -1)
        t = np.clip(t - low + low.reshape(-1, 3).mean(0), 0, 1)
    # Seamless in two passes. Each pass blends the tile with itself shifted by half along ONE
    # axis: the copy's own seam sits in the middle, where the original keeps full weight, so
    # no cross remains; the second pass keeps the first pass's wrap intact.
    rng = np.random.default_rng(7)
    mean = t.reshape(-1, 3).mean(0)
    for axis in (1, 0):
        r = np.roll(t, N // 2, axis)
        d = np.minimum(np.arange(N), N - 1 - np.arange(N)) / (N / 2)
        ramp = np.clip((d - 0.02) / 0.28, 0, 1); ramp = ramp * ramp * (3 - 2 * ramp)
        w = ramp[None, :] if axis == 1 else ramp[:, None]
        w = np.broadcast_to(w, (N, N)).copy()
        n = nd.gaussian_filter(rng.standard_normal((N, N)), 50, mode='wrap'); n /= np.abs(n).max()
        w = np.clip(w + 0.35 * n * (w * (1 - w)) * 4, 0, 1)
        # keep the seam-free edges exact: at the border only the shifted copy, at the centre only the original
        w = w[..., None]
        mix = w * t + (1 - w) * r
        norm = np.sqrt(w * w + (1 - w) ** 2)
        t = (mean + (mix - mean) / norm).clip(0, 1)
    mix = t
    Image.fromarray((mix.clip(0, 1) * 255 + .5).astype(np.uint8), 'RGB').save(dst, compress_level=9)
    print(dst, (N, N))
