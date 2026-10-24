"""Contact sheet: colour | height | normal | relit from left | relit from right (quick check)."""
import sys
from pathlib import Path

import numpy as np
from PIL import Image

ROOT = Path(sys.argv[1])
rows = []
for name in ["glacier", "twin", "peak"]:
    d = ROOT / "maps" / name
    col = np.asarray(Image.open(d / "color.png")).astype(np.float32) / 255
    nrm = np.asarray(Image.open(d / "normal.png")).astype(np.float32)[..., :3] / 255 * 2 - 1
    hgt = np.asarray(Image.open(d / "height.png")).astype(np.float32) / 255
    msk = np.asarray(Image.open(d / "lightmask.png")).astype(np.float32) / 255
    a = col[..., 3:4]
    tiles = [col[..., :3], np.repeat(hgt[..., None], 3, -1), nrm * 0.5 + 0.5]
    for lx, ly in [(-0.7, 0.45), (0.7, 0.45)]:
        L = np.array([lx, ly, 0.55]); L /= np.linalg.norm(L)
        ndl = np.clip((nrm * L).sum(-1), 0, 1)[..., None]
        flat = L[2]
        rel = 0.55 + 0.75 * ndl / max(flat, 1e-3) * 0.6
        lit = col[..., :3] * (1 + (rel - 1) * msk[..., None])
        tiles.append(np.clip(lit, 0, 1))
    paper = np.array([0.92, 0.88, 0.78])
    tiles = [t * (a if i != 1 and i != 2 else 1) + paper * (1 - a) * (1 if i not in (1, 2) else 0) for i, t in enumerate(tiles)]
    row = np.concatenate(tiles, 1)
    rows.append(row)
w = max(r.shape[1] for r in rows)
sheet = np.concatenate([np.pad(r, ((0, 0), (0, w - r.shape[1]), (0, 0))) for r in rows], 0)
img = Image.fromarray((np.clip(sheet, 0, 1) * 255).astype(np.uint8))
img.thumbnail((2400, 2400))
img.save(ROOT / "maps" / "_sheet.jpg", quality=88)
print(img.size)
