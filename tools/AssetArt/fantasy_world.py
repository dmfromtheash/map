"""Map's own "Фэнтези-мир" sample sets, painted with the procedural brush kit in kit/.

Genre conventions of painted fantasy-world maps (oblique top-down view, light from the north-west,
soft painterly bands, broken dark-brown ink on ridges and silhouettes, a foot that dissolves into
the ground instead of a hard base line, several variants per set). All drawing is generated here;
no third-party artwork is used or traced.

Run:  py -3.13 fantasy_world.py <out-dir>
Writes <out-dir>/stamps/<id>.png and <out-dir>/manifest.json (RegionalArtPack format: groups become
asset sets with their variants, the pack name becomes the collection, tags categorise the sets).
"""
import hashlib, json, sys
from pathlib import Path
import numpy as np

sys.path.insert(0, str(Path(__file__).with_name('kit')))
from brush import Canvas, SS, mix, hexc  # noqa: E402
from palette import INK, GROUND_SHADOW  # noqa: E402
from terrain import Relief, paint_mountain, SNOW_TONES  # noqa: E402

PACK = {"id": "map-fantasy-world-samples", "name": "Фэнтези-мир · образцы"}

# Each group is one asset set; tags give the category tree: Стиль / Набор / Тема.
GROUPS = [
    {"id": "mountains", "name": "Одинокая вершина",
     "tags": ["Стиль/Фэнтези-мир", "Набор/Основа", "Тема/Рельеф/Горы"]},
    {"id": "trees", "name": "Лиственное дерево",
     "tags": ["Стиль/Фэнтези-мир", "Набор/Растительность", "Тема/Растительность/Деревья"]},
]

# Warm earth rock in three bands (shade, body, light) - the classic painted-peak palette.
EARTH = [hexc('#6E3F26'), hexc('#B06B3A'), hexc('#E8B27A')]
OLIVE = [hexc('#5B5426'), hexc('#9C8F3E'), hexc('#D7C878')]
SLATE = [hexc('#4A4C52'), hexc('#8B8D92'), hexc('#D9D9D6')]
PEAK_INK = hexc('#3B2516')


def finish(cv, base_y, pad=10):
    from PIL import Image
    im = cv.image()
    bb = im.getchannel('A').point(lambda v: 255 if v > 3 else 0).getbbox()
    im = im.crop(bb)
    out = Image.new('RGBA', (im.width + 2 * pad, im.height + 2 * pad), (0, 0, 0, 0))
    out.paste(im, (pad, pad))
    anchor = (base_y - bb[1] + pad) / out.height
    return out, round(min(.99, max(.5, anchor)), 3)


def lone_peak(seed, tones, snow=False, lean=0.0):
    """One dominant steep peak with two lower shoulders; the foot fades softly into the ground."""
    cv = Canvas(560, 440, seed)
    rf = Relief(cv, base=420, depth=120, seed=seed)
    rng = np.random.default_rng(seed)
    cx = 280 + lean * 40
    Z = rf.cone(cx, 60, 150, 62, 330, power=1.05)
    Z = np.maximum(Z, rf.cone(cx - 120 + rng.uniform(-15, 15), 78, 120, 45, rng.uniform(130, 170), power=1.15))
    Z = np.maximum(Z, rf.cone(cx + 115 + rng.uniform(-15, 15), 82, 115, 42, rng.uniform(110, 160), power=1.15))
    # a few big radiating ridges (faceted faces, not mottle): angular ribs around the summit plus
    # one broad ridged-noise pass for irregularity
    ang = np.arctan2((rf.J - 60) * 2.2, rf.X - cx)
    ribs = .5 + .5 * np.cos(ang * rng.integers(5, 8) + rng.uniform(0, 6.28))
    rid = rf.noise(70, seed, 2, ridged=True)
    Z = Z * (.78 + .14 * ribs ** 2 + .12 * rid)
    Z = np.clip(Z, 0, None) * np.clip((rf.Jmax - rf.J) / 10, 0, 1) * np.clip(rf.J / 8, 0, 1)
    rf.Z = Z
    colour, lam, t = paint_mountain(rf, rock=tones, snow=SNOW_TONES,
                                    snow_line=(.66 * Z.max()) if snow else None, seed=seed)
    zmax = Z.max()
    n = rf.noise(9, seed + 7, 3)
    # soft foot: alpha climbs from 0 over the lowest fifth of the height (dissolves into the map)
    alpha = np.clip((Z - zmax * .03 * (.5 + n)) / (zmax * .2), 0, 1) ** 1.4
    a = rf.render(colour, alpha, ink=PEAK_INK, ink_width=2.2, hatch=.18, ridges=.9, silhouette=.75, foot_ink=.25)
    # a faint dark haze under the foot, the way painted maps seat a peak on the ground
    return finish(cv, 412)


def broadleaf(seed, leaf, light, shade, ink, h=230, crown=.9, squash=.88):
    sys.path.insert(0, str(Path(__file__).with_name('kit')))
    from parts import canopy  # noqa: F401  (kept for parity with the kit)
    cv = Canvas(300, 300, seed)
    x, base = 150, 278
    r = h * crown / 2 / squash * .95
    cy = base - h + r * squash
    cv.shadow(x + r * .35, base - r * .05, r * .9, r * .22, GROUND_SHADOW, .4, blur=r * .1)
    # trunk
    w = max(5, r * .2)
    pts = [(x - w * .6, base), (x - w * .4, (base + cy) / 2), (x - w * .3, cy + r * .2),
           (x + w * .3, cy + r * .2), (x + w * .45, (base + cy) / 2), (x + w * .65, base)]
    m = cv.poly(pts, wobble=.4, cell=6, seed=seed, soft=.3)
    bark = hexc('#5E4330')
    cv.wash(m, bark, var=.08, edge=.2, seed=seed)
    cv.outline(m, ink, 1.3, .05, seed=seed, opacity=.85)
    # crown: soft clumps - body, south-east shade, north-west light, broken contour
    rng = np.random.default_rng(seed)
    crown_mask = np.zeros((cv.H, cv.W))
    blobs = [(x - r * .05, cy - r * .06, r * .62)]
    for i in range(9):
        ang = 2 * np.pi * i / 9 + rng.uniform(-.25, .25)
        d = r * rng.uniform(.45, .58)
        blobs.append((x + np.cos(ang) * d, cy + np.sin(ang) * d * squash, r * rng.uniform(.38, .5)))
    for bx, by, br in blobs:
        crown_mask = np.maximum(crown_mask, cv.ellipse(bx, by, br, br * squash, wobble=1.4, cell=7, seed=seed + int(bx), soft=.4))
    cv.wash(crown_mask, leaf, var=.12, edge=.3, seed=seed + 3)
    # shade = where the crown, shifted toward the light, no longer covers (south-east rim and the
    # lower half), softened; light = the inverse toward the north-west. Follows the clump outline.
    from scipy import ndimage as ndi
    off = r * .28 * SS
    lit_part = ndi.shift(crown_mask, (-off * .9, -off), order=1)
    shade_m = ndi.gaussian_filter(np.clip(crown_mask - lit_part, 0, 1), r * .08 * SS) * crown_mask
    hi = ndi.shift(crown_mask, (off * .8, off * .8), order=1)
    light_m = ndi.gaussian_filter(np.clip(crown_mask - hi, 0, 1), r * .06 * SS) * crown_mask
    cv.glaze(np.clip(shade_m * 1.6, 0, 1) * (.7 + .3 * cv.noise(6, seed + 5)), shade, .8)
    cv.stipple(light_m > .3, light, int(r * r * .06), (1.6, 3.6), .9, seed + 6)
    cv.stipple(crown_mask > .6, mix(leaf, light, .35), int(r * r * .03), (1.4, 2.8), .6, seed + 7)
    cv.outline(crown_mask, ink, 1.5, .2, seed=seed + 8, opacity=.85)
    return finish(cv, base)


def build(out: Path):
    (out / 'stamps').mkdir(parents=True, exist_ok=True)
    assets = []

    def put(gid, sid, name, im_anchor, width):
        im, anchor = im_anchor
        f = out / 'stamps' / f'{sid}.png'
        im.save(f)
        sha = hashlib.sha256(f.read_bytes()).hexdigest()
        assets.append({"id": sid, "group": gid, "name": name, "file": f"stamps/{sid}.png", "sha256": sha,
                       "width": width, "anchorY": anchor, "pixelSize": list(im.size)})
        print(sid, im.size, anchor)

    put("mountains", "peak-earth", "Вершина · охра", lone_peak(101, EARTH), 300)
    put("mountains", "peak-olive", "Вершина · олива", lone_peak(102, OLIVE, lean=-.4), 300)
    put("mountains", "peak-snow", "Вершина · снег", lone_peak(103, SLATE, snow=True, lean=.4), 300)

    put("trees", "oak-green", "Дерево · зелёное",
        broadleaf(201, hexc('#5E8A3A'), hexc('#B4CF68'), hexc('#2E5638'), hexc('#1F3A26')), 110)
    put("trees", "oak-olive", "Дерево · оливковое",
        broadleaf(202, hexc('#7C8434'), hexc('#C9C770'), hexc('#454A22'), hexc('#2C2E17'), h=215), 104)
    put("trees", "oak-autumn", "Дерево · осеннее",
        broadleaf(203, hexc('#B8693A'), hexc('#E8A75C'), hexc('#7A3A28'), hexc('#4A2418'), h=220, squash=.92), 106)

    manifest = {**PACK, "groups": GROUPS, "assets": assets}
    (out / 'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding='utf-8')

    # contact sheet for review
    from PIL import Image
    ims = [Image.open(out / a['file']) for a in assets]
    W = sum(i.width for i in ims) + 20 * (len(ims) + 1); H = max(i.height for i in ims) + 40
    sheet = Image.new('RGBA', (W, H), (44, 56, 40, 255))
    x = 20
    for i in ims:
        sheet.alpha_composite(i, (x, H - 20 - i.height)); x += i.width + 20
    sheet.save(out / 'contact.png')


if __name__ == '__main__':
    build(Path(sys.argv[1] if len(sys.argv) > 1 else 'out'))
