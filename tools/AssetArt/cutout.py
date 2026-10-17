"""Cut a generated asset out of a flat chroma background (magenta by default) into a trimmed RGBA PNG.

The background colour is estimated from the image border. Alpha comes from how close each pixel is
to that colour; colour is then un-mixed (F = (C - (1 - a) * B) / a), which removes the magenta fringe
that a plain colour key leaves on soft and anti-aliased edges.

Run:  py -3.13 cutout.py <in.jpg|png> <out.png> [--max 1024] [--pad 12]
"""
import argparse
import numpy as np
from PIL import Image
from scipy import ndimage as ndi


def cutout(src, dst, max_edge=1024, pad=12):
    im = np.asarray(Image.open(src).convert('RGB')).astype(np.float64) / 255
    h, w, _ = im.shape
    border = np.concatenate([im[:8].reshape(-1, 3), im[-8:].reshape(-1, 3), im[:, :8].reshape(-1, 3), im[:, -8:].reshape(-1, 3)])
    bg = np.median(border, axis=0)
    # "Magenta-ness" relative to the background: min(R, B) - G is high on the key and low on rock or leaves.
    key = np.minimum(im[..., 0], im[..., 2]) - im[..., 1]
    key_bg = min(bg[0], bg[2]) - bg[1]
    a = np.clip((key_bg * .78 - key) / (key_bg * .5), 0, 1)
    # Soften the 1-px jagged JPEG edge and drop isolated specks.
    a = ndi.gaussian_filter(a, .6)
    solid = ndi.binary_opening(a > .5, iterations=2)
    labels, n = ndi.label(solid)
    if n > 1:
        sizes = ndi.sum(solid, labels, range(1, n + 1))
        keep = np.isin(labels, 1 + np.flatnonzero(sizes >= sizes.max() * .02))
        a *= ndi.binary_dilation(keep, iterations=3)
    a = np.where(a < .02, 0, a)
    fg = np.where(a[..., None] > .05, (im - (1 - a[..., None]) * bg) / np.maximum(a[..., None], .05), 0)
    # Any magenta left in the un-mixed colour is spill: pull R and B down toward G on partial pixels.
    near_edge = ndi.binary_dilation(a < .5, iterations=3)
    spill = np.clip(np.minimum(fg[..., 0], fg[..., 2]) - fg[..., 1], 0, None) * np.maximum(np.clip((1 - a) * 4, 0, 1), near_edge)
    fg[..., 0] -= spill; fg[..., 2] -= spill
    # JPEG blends the key into light paint along the silhouette: opaque pixels with a magenta tint.
    # Within a few px of the edge, take their colour from clean neighbours and fade them by the tint.
    fg = np.clip(fg, 0, 1)
    tint = np.clip(np.minimum(fg[..., 0], fg[..., 2]) - fg[..., 1], 0, None)
    band = ndi.binary_dilation(a < .5, iterations=5)
    clean = ((a > .9) & (tint < .02)).astype(np.float64)
    weight = ndi.uniform_filter(clean, 7)
    mean = np.dstack([ndi.uniform_filter(fg[..., c] * clean, 7) for c in range(3)]) / np.maximum(weight[..., None], 1e-6)
    weight = ndi.uniform_filter(clean, 11)
    mean = np.dstack([ndi.uniform_filter(fg[..., c] * clean, 11) for c in range(3)]) / np.maximum(weight[..., None], 1e-6)
    dirty = band & ((tint > .03) | (a < .95))
    grey = np.repeat(fg.mean(axis=2, keepdims=True), 3, axis=2)
    fg = np.where((dirty & (weight > .03))[..., None], mean, np.where(dirty[..., None], grey, fg))
    a = np.where(band, a * np.clip(1 - tint / .25, 0, 1), a)
    out = np.dstack([np.clip(fg, 0, 1), a])
    img = Image.fromarray((out * 255 + .5).astype(np.uint8), 'RGBA')
    bb = img.getchannel('A').point(lambda v: 255 if v > 8 else 0).getbbox()
    img = img.crop(bb)
    scale = min(1.0, max_edge / max(img.size))
    if scale < 1:
        img = img.resize((round(img.width * scale), round(img.height * scale)), Image.LANCZOS)
    canvas = Image.new('RGBA', (img.width + 2 * pad, img.height + 2 * pad), (0, 0, 0, 0))
    canvas.paste(img, (pad, pad))
    canvas.save(dst)
    return canvas.size, bg


if __name__ == '__main__':
    p = argparse.ArgumentParser()
    p.add_argument('src'); p.add_argument('dst')
    p.add_argument('--max', type=int, default=1024); p.add_argument('--pad', type=int, default=12)
    o = p.parse_args()
    print(cutout(o.src, o.dst, o.max, o.pad))
