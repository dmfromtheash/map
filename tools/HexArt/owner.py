"""The owner's own mountains (Standard category, «Мои горы») cut into hex tiles and icons.

The owner allowed his mountain art to be used directly for the hex mountains (2026-10-22), so the
mountains, the tall peaks and the living-ridge icons are his pictures reduced to the hex box,
standing in a snow field painted by paint.py with a cool cast shadow to the south-east.
The source is %LOCALAPPDATA%\\Map\\Assets\\originals (six 4096 px RGBA pictures); without it the
build keeps the procedural mountains.
"""

from __future__ import annotations

import glob
import os

import numpy as np
from PIL import Image, ImageFilter

# The six originals in name order: 0 broad massif with an icefall, 1 crown of crystal spires,
# 2 a long diagonal ridge, 3 a single tall peak, 4 twin peaks, 5 a leaning spire.
TILE = {"mountains": [0, 4, 2], "peaks": [3, 1, 5]}
ICONS = [3, 4, 1, 0]


def folder() -> str:
    return os.path.join(os.environ.get("LOCALAPPDATA", ""), "Map", "Assets", "originals")


def available() -> bool:
    return len(glob.glob(os.path.join(folder(), "*.png"))) >= 6


_cache: dict[int, Image.Image] = {}


def source(index: int) -> Image.Image:
    """One original cropped to its picture, kept at 1024 px wide (enough for every use here)."""
    if index not in _cache:
        path = sorted(glob.glob(os.path.join(folder(), "*.png")))[index]
        im = Image.open(path).convert("RGBA")
        im = im.crop(im.getchannel("A").point(lambda a: 255 if a > 8 else 0).getbbox())
        _cache[index] = im.resize((1024, round(1024 * im.height / im.width)), Image.LANCZOS)
    return _cache[index]


def sprite(index: int, width: int, max_height: int | None = None) -> Image.Image:
    """The original reduced to `width` (or less, to keep within max_height); the pen is kept
    readable by a slight darkening of the thinnest lines after the reduction."""
    im = source(index)
    w = width
    if max_height is not None and round(w * im.height / im.width) > max_height:
        w = round(max_height * im.width / im.height)
    h = round(w * im.height / im.width)
    # two steps: a reduction straight from 1024 to ~200 px loses the pen to grey
    mid = im.resize((w * 2, h * 2), Image.LANCZOS)
    small = mid.resize((w, h), Image.LANCZOS)
    small = small.filter(ImageFilter.UnsharpMask(radius=1.0, percent=60, threshold=2))
    return small


def shadow(size, foot_x, foot_y, width, strength=0.32) -> Image.Image:
    """A soft cool shadow pool to the south-east of a foot."""
    w, h = size
    y, x = np.mgrid[0:h, 0:w].astype(np.float32)
    rx, ry = width * 0.55, width * 0.13
    d = ((x - (foot_x + width * 0.12)) / rx) ** 2 + ((y - (foot_y - ry * 0.2)) / ry) ** 2
    a = np.clip(1 - d, 0, 1) ** 1.5 * strength
    rgba = np.zeros((h, w, 4), np.float32)
    rgba[..., 0], rgba[..., 1], rgba[..., 2] = 0x41 / 255, 0x58 / 255, 0x73 / 255
    rgba[..., 3] = a
    img = Image.fromarray((rgba * 255 + 0.5).astype(np.uint8), "RGBA")
    return img.filter(ImageFilter.GaussianBlur(3))


def place(canvas: Image.Image, sp: Image.Image, centre_x: float, foot_y: float):
    """Stands the sprite with the middle of its bottom on (centre_x, foot_y), its shadow first."""
    canvas.alpha_composite(shadow(canvas.size, centre_x, foot_y, sp.width))
    canvas.alpha_composite(sp, (round(centre_x - sp.width / 2), round(foot_y - sp.height)))


def terrain(name: str, variant: int, ground: Image.Image, room: int):
    """(tile, tall-or-None): the 256 box with the owner's mountain on its snow field and, for the
    tall peaks, the picture of what rises over the row behind (box plus `room` px above)."""
    index = TILE[name][variant % 3]
    if name == "mountains":
        sp = sprite(index, 206, max_height=176)
        tile = ground.copy()
        place(tile, sp, 128, 214)
        return tile, None
    # The owner (2026-10-22): peaks were too big for a hex map. They are now a little larger than
    # the plain mountains and rise only a short way over the row behind.
    sp = sprite(index, 224, max_height=206)
    full = Image.new("RGBA", (256, 256 + room), (0, 0, 0, 0))
    full.paste(ground, (0, room))
    place(full, sp, 128, room + 218)
    tile = full.crop((0, room, 256, room + 256))
    over = Image.new("RGBA", (256, 256 + room), (0, 0, 0, 0))
    over.alpha_composite(sp, (round(128 - sp.width / 2), round(room + 218 - sp.height)))
    return tile, over


def icon(variant: int) -> Image.Image:
    """A living-ridge icon: one of the owner's mountains standing on (128, 244) of the 256 box."""
    sp = sprite(ICONS[variant % len(ICONS)], 240, max_height=236)
    box = Image.new("RGBA", (256, 256), (0, 0, 0, 0))
    place(box, sp, 128, 246)
    return box.resize((128, 128), Image.LANCZOS)
