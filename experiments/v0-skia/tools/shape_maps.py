"""V0 shape maps for three owner mountains (Claude, 2026-10-24).

For each mountain: colour (downscaled), height, normal (OpenGL convention: R=+X right, G=+Y up,
B=+Z toward viewer), light mask (how strongly lighting may change the painting) and a soft
contact-shadow blob. Height is authored from a hand-placed form description (peaks, ridges,
bowls) on top of the silhouette, NOT derived from painting brightness; brightness adds only a
weak texture term so snow does not become a bump and ink does not become a trench.

Usage: python shape_maps.py <shared_root> [width]
"""
import json
import sys
from pathlib import Path

import numpy as np
from PIL import Image
from scipy import ndimage as ndi

ROOT = Path(sys.argv[1])
OUT_W = int(sys.argv[2]) if len(sys.argv) > 2 else 1024
RELIEF = 0.30

# Form description in 700-px preview coordinates (x right, y down). Heights 0..1.
# peaks: (x, y, height, radius); ridges: polyline [(x, y, h)...] tent width w;
# bowls: (x, y, depth, radius) concave cirques / saddles.
FORMS = {
    "glacier": {
        "preview_w": 700,
        "peaks": [(265, 18, 1.00, 70), (482, 78, 0.86, 60)],
        "ridges": [
            ([(265, 18, 1.0), (175, 120, 0.72), (70, 255, 0.32), (10, 300, 0.08)], 34),
            ([(265, 18, 1.0), (330, 70, 0.86), (410, 70, 0.80), (482, 78, 0.86)], 30),
            ([(482, 78, 0.86), (565, 185, 0.55), (650, 300, 0.18)], 34),
            ([(240, 150, 0.70), (300, 220, 0.62), (335, 330, 0.40), (350, 420, 0.16)], 40),
            ([(470, 170, 0.62), (520, 240, 0.48), (560, 350, 0.24)], 36),
            ([(120, 230, 0.40), (160, 330, 0.22)], 26),
        ],
        "bowls": [(395, 185, 0.30, 85), (430, 280, 0.12, 60)],
    },
    "twin": {
        "preview_w": 700,
        "peaks": [(240, 72, 0.88, 55), (455, 14, 1.00, 60)],
        "ridges": [
            ([(240, 72, 0.88), (150, 180, 0.55), (60, 290, 0.22), (10, 330, 0.06)], 32),
            ([(240, 72, 0.88), (300, 140, 0.66), (350, 152, 0.60), (400, 110, 0.74), (455, 14, 1.0)], 28),
            ([(455, 14, 1.0), (545, 130, 0.64), (610, 230, 0.36), (680, 305, 0.08)], 32),
            ([(455, 14, 1.0), (430, 170, 0.66), (395, 300, 0.40), (370, 420, 0.14)], 36),
            ([(240, 72, 0.88), (255, 210, 0.55), (300, 330, 0.30), (320, 430, 0.10)], 34),
            ([(545, 130, 0.64), (540, 260, 0.36)], 24),
        ],
        "bowls": [(350, 190, 0.22, 70)],
    },
    "peak": {
        "preview_w": 700,
        "peaks": [(345, 14, 1.00, 60)],
        "ridges": [
            ([(345, 14, 1.0), (300, 110, 0.78), (240, 215, 0.52), (130, 340, 0.20), (20, 385, 0.05)], 34),
            ([(345, 14, 1.0), (395, 110, 0.80), (460, 210, 0.52), (560, 310, 0.24), (650, 385, 0.06)], 34),
            ([(345, 14, 1.0), (335, 150, 0.80), (322, 300, 0.50), (310, 470, 0.14)], 36),
            ([(410, 120, 0.76), (470, 300, 0.42), (520, 460, 0.12)], 30),
            ([(300, 110, 0.78), (215, 300, 0.38), (190, 440, 0.12)], 28),
        ],
        "bowls": [(270, 300, 0.10, 50), (420, 330, 0.10, 50)],
    },
}


def seg_distance(px, py, ax, ay, bx, by):
    abx, aby = bx - ax, by - ay
    t = ((px - ax) * abx + (py - ay) * aby) / max(abx * abx + aby * aby, 1e-6)
    t = np.clip(t, 0.0, 1.0)
    dx, dy = px - (ax + t * abx), py - (ay + t * aby)
    return np.sqrt(dx * dx + dy * dy), t


def build(name, form):
    src = Image.open(ROOT / "art" / f"{name}.png").convert("RGBA")
    h_px = round(src.height * OUT_W / src.width)
    rgba = np.asarray(src.resize((OUT_W, h_px), Image.LANCZOS)).astype(np.float32) / 255.0
    alpha = rgba[..., 3]
    s = OUT_W / form["preview_w"]
    yy, xx = np.mgrid[0:h_px, 0:OUT_W].astype(np.float32)

    # Base: soft dome over the silhouette (interior distance), low so ridges dominate.
    inside = alpha > 0.5
    dist = ndi.distance_transform_edt(inside)
    dome = 0.45 * np.clip(dist / max(dist.max(), 1.0), 0, 1) ** 0.75

    form_h = np.zeros_like(dome)
    for (x, y, ph, r) in form["peaks"]:
        d2 = ((xx - x * s) ** 2 + (yy - y * s) ** 2) / (r * s) ** 2
        form_h = np.maximum(form_h, ph * np.exp(-d2 * 1.6))
    for pts, w in form["ridges"]:
        for (ax, ay, ah), (bx, by, bh) in zip(pts, pts[1:]):
            d, t = seg_distance(xx, yy, ax * s, ay * s, bx * s, by * s)
            hline = ah + (bh - ah) * t
            # Sharp crest with broad planar faces (the owner's rock is drawn as facets).
            tent = hline * np.clip(1.0 - d / (w * s * (1.6 + 3.4 * hline)), 0, 1)
            form_h = np.maximum(form_h, tent)
    for (x, y, depth, r) in form["bowls"]:
        d2 = ((xx - x * s) ** 2 + (yy - y * s) ** 2) / (r * s) ** 2
        form_h -= depth * np.exp(-d2 * 2.0)

    height = np.maximum(dome, form_h) * 0.85 + dome * 0.15
    # Round off the creases where ridge faces meet: hard creases read as pale stripes across
    # the painted rock under a moving light.
    height = ndi.gaussian_filter(height, 3.2 * s)

    # Soft fall-off to the silhouette instead of a hard cut (no cliff ring in the normals).
    soft_edge = np.clip(ndi.distance_transform_edt(alpha > 0.35) / (5.0 * s), 0, 1)
    soft_edge = soft_edge * soft_edge * (3 - 2 * soft_edge)
    height = height * (0.25 + 0.75 * soft_edge) * np.clip(alpha * 1.5, 0, 1)

    # Very weak painting texture: band-pass of luminance (strokes, not snow/ink blobs).
    lum = 0.299 * rgba[..., 0] + 0.587 * rgba[..., 1] + 0.114 * rgba[..., 2]
    detail = ndi.gaussian_filter(lum, 0.8 * s) - ndi.gaussian_filter(lum, 3.0 * s)
    height = height + 0.010 * detail * soft_edge
    height = ndi.gaussian_filter(np.clip(height, 0, None), 0.6 * s)
    height /= max(height.max(), 1e-6)

    # Normals from height: N = normalize(-k*dh/dx, +k*dh/dy (image y down -> GL y up), 1).
    # Relief scale: full height (1.0) equals RELIEF of the image width in pixels.
    relief_px = RELIEF * OUT_W
    gx = ndi.sobel(height, axis=1) / 8.0
    gy = ndi.sobel(height, axis=0) / 8.0
    nx, ny, nz = -gx * relief_px, gy * relief_px, np.ones_like(gx)
    ln = np.sqrt(nx * nx + ny * ny + nz * nz)
    nx, ny, nz = nx / ln, ny / ln, nz / ln

    # Extend normals past the silhouette (nearest inside pixel) so filtering gives no dark rim.
    _, (iy, ix) = ndi.distance_transform_edt(alpha < 0.5, return_indices=True)
    nx, ny, nz = nx[iy, ix], ny[iy, ix], nz[iy, ix]
    normal = np.stack([nx * 0.5 + 0.5, ny * 0.5 + 0.5, nz * 0.5 + 0.5, np.ones_like(nx)], -1)

    # Light mask: 1 = lighting may change the painting fully, lower on ink lines and the
    # flat snow apron at the foot (keeps the drawing readable).
    ink = np.clip((0.30 - lum) / 0.20, 0, 1)
    apron = np.clip(1.0 - height / 0.12, 0, 1)
    mask = np.clip(0.85 - 0.6 * ink - 0.35 * apron, 0.15, 1.0)
    mask = ndi.gaussian_filter(mask, 1.0)
    mask = mask[iy, ix]

    out = ROOT / "maps" / name
    out.mkdir(parents=True, exist_ok=True)
    Image.fromarray((rgba * 255 + 0.5).astype(np.uint8), "RGBA").save(out / "color.png")
    Image.fromarray((height * 65535 + 0.5).astype(np.uint16)).save(out / "height16.png")
    Image.fromarray((height * 255 + 0.5).astype(np.uint8), "L").save(out / "height.png")
    Image.fromarray((normal * 255 + 0.5).astype(np.uint8), "RGBA").save(out / "normal.png")
    Image.fromarray((mask * 255 + 0.5).astype(np.uint8), "L").save(out / "lightmask.png")

    # Base anchor: lowest silhouette row with real coverage (where the mountain sits on ground).
    rows = np.where(alpha.max(axis=1) > 0.5)[0]
    return {"size": [OUT_W, h_px], "baseY": int(rows.max()), "files": ["color.png", "normal.png", "height.png", "height16.png", "lightmask.png"]}


def main():
    meta = {}
    for name, form in FORMS.items():
        meta[name] = build(name, form)
        print(name, meta[name])
    (ROOT / "maps" / "maps.json").write_text(json.dumps({
        "normalConvention": "OpenGL: R=+X right, G=+Y up, B=+Z toward viewer; alpha 255",
        "heightRange": "0..1 (height.png 8-bit, height16.png 16-bit)",
        "lightmask": "0..1 strength the lighting may change the painting",
        "mountains": meta,
    }, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
