"""Unlit composite of the shared scene (layout check only)."""
import json
import sys
from pathlib import Path

from PIL import Image

ROOT = Path(sys.argv[1])
scene = json.loads((ROOT / "scene" / "scene.json").read_text(encoding="utf-8"))
img = Image.open(ROOT / "scene" / "ground_color.png").convert("RGBA")
for m in scene["mountains"]:
    sp = Image.open(ROOT / "maps" / m["name"] / "color.png")
    sp = sp.resize((round(sp.width * m["scale"]), round(sp.height * m["scale"])), Image.LANCZOS)
    img.alpha_composite(sp, (m["x"], m["y"]))
img = img.convert("RGB")
img.thumbnail((1600, 1600))
img.save(ROOT / "scene" / "_unlit.jpg", quality=88)
