"""Paints the hex tile set into src/Map.Rendering/HexArt.

    python build.py <out-dir> [name ...]

Needs numpy, scipy and pillow (a `pip install --target` folder on PYTHONPATH is enough).
Pictures: <tile>_<variant>.webp (256 x 256 box), <tile>_<variant>_tall.webp (box with 128 px of
room above, standing parts only), <tile>_ground.webp, icon_<Kind>_<variant>.webp (128 x 128).

The mountains and the peaks were painted in Photoshop with the owner's brushes from the same
underdrawing (photoshop_build.py, 2026-10-22); their 1024 px paintings are kept in painted/ and
only reduced here. No picture of the owner's own art is used.
"""

from __future__ import annotations

import os
import sys
import time

import numpy as np
from PIL import Image, ImageFilter
from scipy import ndimage as ndi

import tiles
from paint import INK, finish, paint

QUALITY = 90
FEATURE_ZOOM = 1.35
HERE = os.path.dirname(os.path.abspath(__file__))
PAINTED = os.path.join(HERE, "painted")
# Photoshop paintings per tile, and the ones the living-ridge icons are cut from.
PAINTED_TILES = {"mountains": 3, "peaks": 3}
ICON_SOURCES = [("peaks", 0), ("mountains", 0), ("peaks", 2), ("mountains", 2)]


def save(img: Image.Image, path: str):
    img.save(path, "WEBP", quality=QUALITY, method=6, alpha_quality=100)


def painted(name: str, v: int) -> Image.Image | None:
    path = os.path.join(PAINTED, f"{name}_{v}.webp")
    return Image.open(path).convert("RGBA") if os.path.exists(path) else None


def painted_icon(name: str, v: int) -> Image.Image:
    """A living-ridge icon cut from a painted mountain: the mountain's own shape (from the same
    underdrawing at the painting's scale) with its pen line, standing on (128, 244) of the box."""
    art = painted(name, v)
    old = tiles.SCALE
    tiles.SCALE = art.width // tiles.BOX
    try:
        cv = tiles.paint_terrain(name, v)
    finally:
        tiles.SCALE = old
    body = ndi.binary_fill_holes(cv.body)
    body = ndi.binary_dilation(body, iterations=3 * art.width // 512)
    alpha = Image.fromarray((body * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(1.2))
    art.putalpha(alpha)
    sprite = art.crop(art.getbbox())
    w = 236
    h = round(w * sprite.height / sprite.width)
    if h > 232:
        h, w = 232, round(232 * sprite.width / sprite.height)
    sprite = sprite.resize((w, h), Image.LANCZOS)
    box = Image.new("RGBA", (256, 256), (0, 0, 0, 0))
    # a soft cool shadow pool to the south-east of the foot
    yy, xx = np.mgrid[0:256, 0:256].astype(np.float32)
    d = ((xx - 150) / (w * 0.55)) ** 2 + ((yy - 242) / 9) ** 2
    pool = np.clip(1 - d, 0, 1) ** 1.5 * 0.3
    shadow = np.zeros((256, 256, 4), np.uint8)
    shadow[..., 0], shadow[..., 1], shadow[..., 2] = 0x41, 0x58, 0x73
    shadow[..., 3] = (pool * 255).astype(np.uint8)
    box.alpha_composite(Image.fromarray(shadow, "RGBA"))
    box.alpha_composite(sprite, (128 - w // 2, 246 - h))
    return box.resize((128, 128), Image.LANCZOS)


def build(out: str, only: set[str]):
    os.makedirs(out, exist_ok=True)
    S = tiles.SCALE
    box = int(tiles.u(tiles.BOX))
    room = int(tiles.u(tiles.TALL_ROOM))
    for name, count in tiles.TERRAINS.items():
        if only and name not in only:
            continue
        tall = name in tiles.TALL
        if name in PAINTED_TILES and painted(name, 0) is not None:
            for v in range(count):
                art = painted(name, v)
                art = art.resize((tiles.BOX, tiles.BOX), Image.LANCZOS)
                art.putalpha(255)
                save(art, os.path.join(out, f"{name}_{v}.webp"))
                print(f"{name} {v} (painted in Photoshop)", flush=True)
        else:
            for v in range(count):
                t0 = time.time()
                cv = tiles.paint_terrain(name, v, tall=tall)
                rgba = paint(cv, S)
                if tall:
                    tile = rgba[room:room + box]
                    standing = (cv.mat > 0) & ~cv.ground
                    # the pen line round a standing shape lies half on the ground: keep the inked
                    # pixels next to it, not the ground itself
                    near = ndi.binary_dilation(standing, iterations=int(3 * S))
                    inked = np.linalg.norm(rgba[..., :3] - INK, axis=-1) < 0.3
                    over = rgba.copy()
                    over[..., 3] *= standing | (near & inked)
                    save(finish(over, (tiles.BOX, tiles.BOX + tiles.TALL_ROOM)), os.path.join(out, f"{name}_{v}_tall.webp"))
                else:
                    tile = rgba
                tile = tile.copy()
                tile[..., 3] = 1.0
                save(finish(tile, (tiles.BOX, tiles.BOX)), os.path.join(out, f"{name}_{v}.webp"))
                print(f"{name} {v} {time.time() - t0:.1f}s", flush=True)
        cv = tiles.paint_terrain(name, 0, ground_only=True)
        rgba = paint(cv, S)
        rgba[..., 3] = 1.0
        save(finish(rgba, (tiles.BOX, tiles.BOX)), os.path.join(out, f"{name}_ground.webp"))
    for name, count in tiles.FEATURES.items():
        if only and name not in only:
            continue
        for v in range(count):
            cv = tiles.paint_feature(name, v)
            rgba = paint(cv, S, pen=2.4)
            # the buildings are drawn small and shown larger: the hex holds them like a stamp
            k = int(box * (1 - 1 / FEATURE_ZOOM) / 2)
            rgba = rgba[k:box - k, k:box - k]
            save(finish(rgba, (tiles.BOX, tiles.BOX)), os.path.join(out, f"{name}_{v}.webp"))
            print(f"{name} {v}", flush=True)
    for kind in tiles.ICONS:
        if only and ("icon_" + kind) not in only and "icons" not in only:
            continue
        for v in range(tiles.ICON_VARIANTS):
            if kind == "Mountains" and painted(*ICON_SOURCES[v]) is not None:
                save(painted_icon(*ICON_SOURCES[v]), os.path.join(out, f"icon_{kind}_{v}.webp"))
                print(f"icon {kind} {v} (painted)", flush=True)
                continue
            cv = tiles.paint_icon(kind, v)
            save(finish(paint(cv, S, pen=5.0), (128, 128)), os.path.join(out, f"icon_{kind}_{v}.webp"))
            print(f"icon {kind} {v}", flush=True)


if __name__ == "__main__":
    build(sys.argv[1], set(sys.argv[2:]))
