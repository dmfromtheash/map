"""Paints hex assets in Photoshop (the owner's brushes) from the tiles.py underdrawing.

    python photoshop_build.py <out-dir> <asset> [<asset> ...]

An asset is <tile>[:<variant>] or icon_<Kind>[:<variant>]. Photoshop is driven through COM
(Photoshop.Application, DoJavaScriptFile): each picture is a new document closed without saving
after a PNG copy is written to <out-dir>/ps. The 1024 px painting is reduced to the tile size.
"""

from __future__ import annotations

import os
import subprocess
import sys
import time

import numpy as np
from PIL import Image

import tiles

K = 4  # canvas pixels per 256-box pixel
tiles.SCALE = K

import plan as planning  # noqa: E402  (after SCALE)

HERE = os.path.dirname(os.path.abspath(__file__))
JSX = os.path.join(HERE, "photoshop", "paint_plan.jsx")


def run_photoshop(plan_path: str, work: str):
    wrapper = os.path.join(work, "run.jsx")
    with open(wrapper, "w", encoding="utf-8") as f:
        f.write('var HEXART_PLAN = "%s";\n$.evalFile(new File("%s"));\n' % (plan_path.replace("\\", "/"), JSX.replace("\\", "/")))
    ps = (
        "$ps = New-Object -ComObject Photoshop.Application; $old = $ps.DisplayDialogs; $ps.DisplayDialogs = 3; "
        f"try {{ [void]$ps.DoJavaScriptFile('{wrapper}') }} finally {{ $ps.DisplayDialogs = $old }}"
    )
    subprocess.run(["powershell", "-NoProfile", "-Command", ps], check=True)


def canvas_for(asset: str):
    name, _, v = asset.partition(":")
    v = int(v or 0)
    if name.startswith("icon_"):
        return tiles.paint_icon(name[5:], v), name, v, "icon"
    if name in tiles.FEATURES:
        return tiles.paint_feature(name, v), name, v, "feature"
    return tiles.paint_terrain(name, v, tall=name in tiles.TALL), name, v, "terrain"


def main(out: str, assets: list[str]):
    work = os.path.join(out, "ps")
    os.makedirs(work, exist_ok=True)
    for asset in assets:
        t0 = time.time()
        cv, name, v, kind = canvas_for(asset)
        png = os.path.join(work, f"{name}_{v}.png").replace("\\", "/")
        p = planning.build_plan(cv, f"{name}_{v}", png, K, seed=v + 1, opaque=kind == "terrain",
                                pen=1.4 if kind == "icon" else 1.0)
        plan_path = os.path.join(work, f"{name}_{v}.plan.js")
        planning.write(p, plan_path)
        if os.path.exists(png):
            os.remove(png)
        run_photoshop(plan_path, work)
        log = open(png + ".log", encoding="utf-8", errors="replace").read() if os.path.exists(png + ".log") else "no log"
        if not os.path.exists(png):
            print(f"{asset}: FAILED {log}", flush=True)
            continue
        im = Image.open(png).convert("RGBA")
        size = (128, 128) if kind == "icon" else (256, im.height * 256 // im.width)
        im.resize(size, Image.LANCZOS).save(os.path.join(out, f"{name}_{v}.png"))
        print(f"{asset}: {len(p['ops'])} ops, {time.time() - t0:.0f} s, {log.strip()}", flush=True)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2:])
