"""V0 shared scene (Claude, 2026-10-24): ground plateau, cliff, water + layout of mountains/lights.

Writes scene/ground_color.png, ground_normal.png, ground_height.png, water_mask.png, shore_dist.png
and scene/scene.json (positions, lights, time-of-day keys). Same inputs for variant A and B.
Palette taken from the owner's mountains: slate blue-grey rock, ice white, ink contour, warm paper.
Usage: python scene.py <shared_root>
"""
import json
import sys
from pathlib import Path

import numpy as np
from PIL import Image
from scipy import ndimage as ndi

ROOT = Path(sys.argv[1])
W, H = 2560, 1440
rng = np.random.default_rng(20261024)
yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)


def noise(scale, octaves=4, seed=0):
    r = np.random.default_rng(seed)
    out = np.zeros((H, W), np.float32)
    amp, total = 1.0, 0.0
    for o in range(octaves):
        s = max(2, int(scale / (2 ** o)))
        g = r.standard_normal((H // s + 3, W // s + 3)).astype(np.float32)
        up = ndi.zoom(g, s, order=3)[:H, :W]
        out += amp * up
        total += amp
        amp *= 0.5
    out /= total
    return (out - out.min()) / (out.max() - out.min())


def hexc(h):
    h = h.lstrip('#')
    return np.array([int(h[i:i + 2], 16) for i in (0, 2, 4)], np.float32) / 255


# --- Cliff edge line (top of the cliff) and cliff foot (waterline): oblique view, face visible.
edge_y = 930 + 70 * np.sin(xx[0] / 420.0 + 0.6) + 35 * np.sin(xx[0] / 160.0 + 2.0) - 0.05 * (xx[0] - 1280)
edge_y += (noise(60, 3, 7)[0] - 0.5) * 40
face_h = 95 + 30 * np.sin(xx[0] / 300.0 + 1.3) + (noise(40, 3, 8)[0] - 0.5) * 30
foot_y = edge_y + face_h
E = edge_y[None, :].repeat(H, 0)
F = foot_y[None, :].repeat(H, 0)
plateau = yy < E
face = (yy >= E) & (yy < F)
water = yy >= F

# --- Heights: plateau 0.30 (+ gentle undulation), face drops to 0, water 0.
und = noise(380, 4, 1)
height = np.where(plateau, 0.30 + 0.05 * (und - 0.5), 0.0)
t_face = np.clip((yy - E) / np.maximum(F - E, 1), 0, 1)
height = np.where(face, 0.30 * (1 - t_face) ** 0.8, height)
height = ndi.gaussian_filter(height, 1.0)

# --- Normals. Plateau/water from height; the face is a near-vertical wall seen obliquely:
# it faces the viewer/south (GL -Y) with rock-column variation.
relief_px = 220.0
gx = ndi.sobel(height, axis=1) / 8.0
gy = ndi.sobel(height, axis=0) / 8.0
nx, ny, nz = -gx * relief_px, gy * relief_px, np.ones_like(gx)
cols = noise(18, 2, 3)  # vertical rock columns: x-varying facets
col_x = ndi.gaussian_filter1d(noise(14, 2, 4), 1.0, axis=1)
fx = (np.gradient(ndi.gaussian_filter(col_x, (12, 2)), axis=1)) * 40
nx = np.where(face, np.clip(fx, -0.7, 0.7), nx)
ny = np.where(face, -0.75 + 0.25 * t_face, ny)
nz = np.where(face, 0.55 + 0.2 * cols, nz)
ln = np.sqrt(nx ** 2 + ny ** 2 + nz ** 2)
normal = np.stack([nx / ln * 0.5 + 0.5, ny / ln * 0.5 + 0.5, nz / ln * 0.5 + 0.5], -1)

# --- Colour.
paper = hexc('#e9dfc6')
g1, g2, g3 = hexc('#b6ae86'), hexc('#9aa07a'), hexc('#c9b98e')  # muted ochre/olive meadow
n1, n2 = noise(300, 4, 11), noise(90, 4, 12)
stroke = ndi.gaussian_filter(noise(6, 2, 13), (0.6, 5.0))  # horizontal dry-brush strokes
g4, g5 = hexc('#7f8c6a'), hexc('#a99a72')  # deeper olive patches, warm earth
ground = g1 * (1 - n1[..., None]) + g2 * n1[..., None]
ground = ground * (1 - 0.35 * n2[..., None]) + g3 * 0.35 * n2[..., None]
patch = np.clip((noise(160, 4, 16) - 0.55) * 4, 0, 1)[..., None]  # painted darker meadow pools
ground = ground * (1 - 0.55 * patch) + g4 * 0.55 * patch
earth = np.clip((noise(220, 3, 17) - 0.6) * 4, 0, 1)[..., None]
ground = ground * (1 - 0.4 * earth) + g5 * 0.4 * earth
# Dry-brush strokes at a slight angle (hand-painted look), two scales.
s1 = ndi.gaussian_filter(noise(4, 1, 18), (0.7, 7.0))
s2 = ndi.gaussian_filter(noise(10, 2, 19), (1.2, 14.0))
ground = ground * (0.88 + 0.14 * stroke[..., None] + 0.10 * s1[..., None] + 0.06 * s2[..., None])
# Darker band along the cliff top (ground shadowed by its own edge, like the owner's aprons).
near_edge = np.clip(1 - (E - yy) / 70.0, 0, 1) * plateau
ground = ground * (1 - 0.18 * near_edge[..., None])
ground = ground * 0.88 + paper * 0.12

rock_dark, rock_mid, ice = hexc('#3b4c63'), hexc('#6f8199'), hexc('#dde6ee')
colstreak = ndi.gaussian_filter(noise(5, 2, 14), (6.0, 0.7))
colstreak = np.clip((colstreak - 0.5) * 3 + 0.5, 0, 1)
# Rock facets: column bands lit on one side, dark on the other (as in the owner's cliffs).
facet = np.clip((ndi.gaussian_filter(noise(26, 2, 22), (10, 1.5)) - 0.5) * 5 + 0.5, 0, 1)
rock = rock_mid * (1 - colstreak[..., None]) + rock_dark * colstreak[..., None]
rock = rock * (0.75 + 0.45 * facet[..., None])
inkcols = np.clip((np.abs(ndi.sobel(ndi.gaussian_filter(facet, (6, 0.8)), axis=1)) - 0.25) * 2.5, 0, 1)
rock = rock * (1 - 0.6 * inkcols[..., None]) + hexc('#1d2735') * 0.6 * inkcols[..., None]
rock = rock * (1 - 0.35 * t_face[..., None]) + rock_dark * 0.35 * t_face[..., None]
snowcap = np.clip(1 - t_face / 0.18, 0, 1)[..., None] * 0.55
rock = rock * (1 - snowcap) + ice * snowcap

shore = ndi.distance_transform_edt(water).astype(np.float32)  # px from cliff foot into water
shore_n = np.clip(shore / 520.0, 0, 1)
w_shallow, w_deep = hexc('#7d9cad'), hexc('#3d5c74')
wcol = w_shallow * (1 - shore_n[..., None]) + w_deep * shore_n[..., None]
wstroke = ndi.gaussian_filter(noise(5, 2, 15), (0.8, 9.0))
wstroke2 = ndi.gaussian_filter(noise(12, 2, 23), (1.5, 22.0))
wcol = wcol * (0.90 + 0.14 * wstroke[..., None] + 0.10 * wstroke2[..., None])
# Painted lighter ripple dashes near the shore, fading out to open water.
dash = np.clip((ndi.gaussian_filter(noise(6, 1, 24), (0.9, 12.0)) - 0.62) * 8, 0, 1)
dash *= np.clip(1 - shore / 260.0, 0, 1) * (shore > 14)
wcol = wcol * (1 - 0.35 * dash[..., None]) + ice * 0.35 * dash[..., None]
foam = np.clip(1 - shore / 10.0, 0, 1)[..., None] * 0.6
wcol = wcol * (1 - foam) + ice * foam
wcol = wcol * 0.9 + paper * 0.1

color = np.where(plateau[..., None], ground, np.where(face[..., None], rock, wcol))

# Ink contour along the cliff edge and the waterline (owner's style: dark navy line, varying width).
ink = hexc('#1d2735')
def line_mask(ycurve, width):
    d = np.abs(yy - ycurve[None, :])
    wv = width * (0.7 + 0.6 * noise(120, 2, 21))
    return np.clip(1.2 - d / wv, 0, 1)
inkm = np.maximum(line_mask(edge_y, 2.6), 0.6 * line_mask(foot_y, 1.8))
color = color * (1 - inkm[..., None]) + ink * inkm[..., None]

# Paper grain everywhere (fine, fixed seed).
grain = noise(2, 1, 31) - 0.5
color = np.clip(color * (1 + 0.05 * grain[..., None]), 0, 1)

out = ROOT / "scene"
out.mkdir(parents=True, exist_ok=True)
Image.fromarray((color * 255 + 0.5).astype(np.uint8), "RGB").save(out / "ground_color.png")
Image.fromarray((normal * 255 + 0.5).astype(np.uint8), "RGB").save(out / "ground_normal.png")
Image.fromarray((np.clip(height / 0.35, 0, 1) * 255 + 0.5).astype(np.uint8), "L").save(out / "ground_height.png")
Image.fromarray((water * 255).astype(np.uint8), "L").save(out / "water_mask.png")
Image.fromarray((np.clip(shore / 520.0, 0, 1) * 255 + 0.5).astype(np.uint8), "L").save(out / "shore_dist.png")

maps = json.loads((ROOT / "maps" / "maps.json").read_text(encoding="utf-8"))["mountains"]


def mountain(name, cx, base_y, scale):
    w, h = maps[name]["size"]
    by = maps[name]["baseY"]
    # Top-left placement so that the sprite base (baseY) sits on base_y, centred on cx.
    return {"name": name, "x": round(cx - w * scale / 2), "y": round(base_y - by * scale), "scale": scale,
            "groundHeight": 0.30, "baseScreenY": base_y}


scene = {
    "canvas": [W, H],
    "units": "pixels of the 2560x1440 frame; y down; normals OpenGL (G = up/north)",
    "ground": {"color": "scene/ground_color.png", "normal": "scene/ground_normal.png",
               "height": "scene/ground_height.png (0..1 maps to 0..0.35)", "waterMask": "scene/water_mask.png",
               "shoreDistance": "scene/shore_dist.png (0..1 = 0..520 px from the cliff foot)"},
    "mountains": [  # draw order: back (smaller y) first
        mountain("peak", 1300, 760, 0.80),
        mountain("glacier", 560, 880, 0.76),
        mountain("twin", 2040, 900, 0.74),
    ],
    "mountainMaps": "maps/<name>/color.png, normal.png, height.png, lightmask.png (1024 px wide)",
    # Practical lights placed by composition rules: the torch on the left third line at the cliff
    # edge (accent + reflection in water), the village as an irregular cluster around the right
    # third point with one hero light (square/tavern) and dimmer, smaller windows; no straight rows.
    "lights": [
        {"id": "torch", "x": 860, "y": int(edge_y[860]) - 18, "color": "#ff9a42", "intensity": 1.15, "radius": 300, "height": 40,
         "flicker": {"amplitude": 0.12, "hz": [1.7, 4.3, 9.1], "seed": 7}},
        {"id": "village_hero", "x": 1640, "y": int(edge_y[1640]) - 75, "color": "#ffb866", "intensity": 1.3, "radius": 230, "height": 30},
        {"id": "village2", "x": 1565, "y": int(edge_y[1565]) - 52, "color": "#ffc27a", "intensity": 0.85, "radius": 150, "height": 22},
        {"id": "village3", "x": 1712, "y": int(edge_y[1712]) - 44, "color": "#ffc888", "intensity": 0.7, "radius": 130, "height": 20},
        {"id": "village4", "x": 1590, "y": int(edge_y[1590]) - 118, "color": "#ffc27a", "intensity": 0.6, "radius": 120, "height": 20},
        {"id": "village5", "x": 1735, "y": int(edge_y[1735]) - 112, "color": "#ffd29a", "intensity": 0.5, "radius": 110, "height": 18},
    ],
    "lightingDesign": [
        "Day: sun from the upper left (azimuth ~315 deg, elevation ~40 deg) - the cartographic (Imhof) convention, relief never reads inverted; key:fill about 2.5:1.",
        "Evening (golden hour): sun low (~18 deg) from the west/left, warm; cool lavender sky fill as the complementary colour; long soft shadows; practical lights start to glow.",
        "Night: moon kept on the same upper-left side (no relief flip between keys), cool and weak; practical lights become the key lights and the focal points.",
        "Composition: torch on the left third, village cluster at the right third point, tallest peak between them - a triangle that leads the eye; brightest value at the village.",
        "Grade: whiteBalance is a linear-RGB multiplier applied to the whole lit image before saturation (night = cool day-for-night balance, so warm ground does not turn olive); practical lights' halos are added after it so they stay warm.",
        "Shadows: cast from the base of each painted mountain along the light (as Happy Harvest's projected sprite shadows), penumbra widening with distance, plus a contact shadow under the base.",
    ],
    "timeOfDay": {
        "parameter": "t in 0..1; keys interpolated with smoothstep between neighbours; colours mixed in linear RGB",
        "keys": [
            {"t": 0.0, "name": "day", "sunDir": [-0.55, 0.55, 0.63], "sunColor": "#fff3dc", "sunIntensity": 1.0,
             "ambientColor": "#c3cddc", "ambientIntensity": 0.48, "fogColor": "#dfe4ea", "fogDensity": 0.0,
             "lightsIntensity": 0.0, "shadowLength": 0.35, "grade": {"exposure": 0.0, "saturation": 1.0, "whiteBalance": [1.0, 1.0, 1.0]}},
            {"t": 0.5, "name": "evening", "sunDir": [-0.86, 0.18, 0.33], "sunColor": "#ffad6b", "sunIntensity": 0.9,
             "ambientColor": "#8e7ca6", "ambientIntensity": 0.42, "fogColor": "#c99c8f", "fogDensity": 0.18,
             "lightsIntensity": 0.55, "shadowLength": 1.2, "grade": {"exposure": -0.1, "saturation": 1.0, "whiteBalance": [1.0, 0.97, 1.02]}},
            {"t": 1.0, "name": "night", "sunDir": [-0.38, 0.55, 0.74], "sunColor": "#a7bbe6", "sunIntensity": 0.38,
             "ambientColor": "#2f3a58", "ambientIntensity": 0.26, "fogColor": "#38456a", "fogDensity": 0.22,
             "lightsIntensity": 1.0, "shadowLength": 0.6, "grade": {"exposure": -0.3, "saturation": 0.75, "whiteBalance": [0.80, 0.90, 1.18]}},
        ],
    },
    "rules": [
        "Same inputs, same camera (1:1 pixels, 2560x1440), same light keys for A and B.",
        "Mountains: final = colour * (ambient + sun*N.L*shadow + local lights) mixed with the original painting by lightmask.",
        "Shadows: finite soft cast shadow of each mountain silhouette on the ground, opposite the sun, length = shadowLength * sprite height; plus contact blob. No infinite projection shadows (Unity Happy Harvest observation).",
        "Fog: low-lying, density grows with (1 - height); does not cover mountain tops.",
        "Water: calm stylised ripple (no glossy specular), shore band from shoreDistance; reflects sky/ambient colour; moving slowly.",
        "Lights: soft falloff, warm, lift the ground and the mountain faces turned to them; halo sprite allowed; flicker reproducible from seed.",
        "Export of a still: fixed t and fixed time for ripple/flicker.",
    ],
}
(out / "scene.json").write_text(json.dumps(scene, indent=2, ensure_ascii=False), encoding="utf-8")
print("ok", scene["mountains"])
