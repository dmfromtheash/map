"""Paints the hex tile set into src/Map.Rendering/HexArt.

    python build.py <out-dir> [name ...]

Needs numpy, scipy and pillow (a `pip install --target` folder on PYTHONPATH is enough).
Pictures: <tile>_<variant>.webp (256 x 256 box), <tile>_<variant>_tall.webp (box with 128 px of
room above, standing parts only), <tile>_ground.webp, icon_<Kind>_<variant>.webp (128 x 128).
"""

from __future__ import annotations

import os
import sys
import time

import numpy as np
from PIL import Image
from scipy import ndimage as ndi

import owner
import tiles
from paint import INK, finish, paint

QUALITY = 90
FEATURE_ZOOM = 1.35


def save(img: Image.Image, path: str):
    img.save(path, "WEBP", quality=QUALITY, method=6, alpha_quality=100)


def build(out: str, only: set[str]):
    os.makedirs(out, exist_ok=True)
    S = tiles.SCALE
    box = int(tiles.u(tiles.BOX))
    room = int(tiles.u(tiles.TALL_ROOM))
    for name, count in tiles.TERRAINS.items():
        if only and name not in only:
            continue
        tall = name in tiles.TALL
        if name in owner.TILE and owner.available():
            # the owner's own mountains on a painted snow field (owner.py)
            g = tiles.paint_terrain(name, 0, ground_only=True)
            ground = paint(g, S)
            ground[..., 3] = 1.0
            ground = finish(ground, (tiles.BOX, tiles.BOX))
            for v in range(count):
                tile, over = owner.terrain(name, v, ground, tiles.TALL_ROOM)
                save(tile, os.path.join(out, f"{name}_{v}.webp"))
                if over is not None:
                    save(over, os.path.join(out, f"{name}_{v}_tall.webp"))
                print(f"{name} {v} (owner)", flush=True)
            save(ground, os.path.join(out, f"{name}_ground.webp"))
            continue
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
                # keep the hex part of the overlay only above the hex middle (the code draws that part again)
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
            if kind == "Mountains" and owner.available():
                save(owner.icon(v), os.path.join(out, f"icon_{kind}_{v}.webp"))
                continue
            cv = tiles.paint_icon(kind, v)
            save(finish(paint(cv, S, pen=5.0), (128, 128)), os.path.join(out, f"icon_{kind}_{v}.webp"))
            print(f"icon {kind} {v}", flush=True)


if __name__ == "__main__":
    build(sys.argv[1], set(sys.argv[2:]))
