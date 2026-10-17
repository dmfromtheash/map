"""Витраж: a rose window. The lead follows the shapes of the scene - a range of snowy peaks under a
setting sun, a forest, a winding river, the sea - and only then are the regions cut into glass
pieces; each piece has its own tint, cathedral-glass streaks and painted grisaille detail, and the
whole window is lit from behind. A gold compass star sits over it like a jewel."""
import sys
import numpy as np
from scipy import ndimage
from scipy.spatial import cKDTree
from common import N, rng, fbm, mask_from, hexrgb, layer, blank, glow, shadow, save, circle, polygon, yy_xx

out = sys.argv[1]
r = rng(11)
Y, X = yy_xx()
cx, cy, R = 512, 512, 418
dist = np.hypot(X - cx, Y - cy)
glass_area = mask_from(circle(cx, cy, R)) > .5
border_in = R - 64

# ---------------------------------------------------------------- scene regions (ids)
region = np.zeros((N, N), np.int32)          # 0 sky
SKY, SUN, SNOW, ROCK, ROCK_DARK, FOREST, FIELD, RIVER, SEA, SEA_DEEP, BORDER = range(11)
horizon = 560

def ridge(x):  # the mountain line: three peaks, the middle one highest
    peaks = [(330, 300, 190), (540, 210, 230), (760, 330, 170)]
    h = np.full_like(x, horizon, dtype=np.float32)
    for px, py, w in peaks:
        h = np.minimum(h, py + np.abs(x - px) * (horizon - py) / w)
    return h

rid = ridge(X)
sun_m = (np.hypot(X - 650, Y - 330) < 70)
region[sun_m] = SUN
mount = (Y >= rid) & (Y < horizon + 20)
region[mount] = ROCK
# shaded right faces
for px, py, w in [(330, 300, 190), (540, 210, 230), (760, 330, 170)]:
    face = mount & (X > px) & (Y < horizon + 20) & (Y >= py + np.abs(X - px) * (horizon - py) / w)
    region[face & (X - px < (Y - py) * .55)] = ROCK_DARK
# snow caps: the top third of each peak, with a jagged lower edge
snowline = 0.38
for px, py, w in [(330, 300, 190), (540, 210, 230), (760, 330, 170)]:
    lim = py + (horizon - py) * snowline + 18 * np.sin(X / 23.0)
    region[mount & (Y < lim) & (np.abs(X - px) < w * snowline * 1.15)] = SNOW
# land below the range: forest then fields, a river winding to the sea
land = (Y >= horizon) & (Y < 760)
region[land] = FIELD
region[land & (Y < 640) | (land & (np.sin(X / 40.0) * 30 + 640 > Y))] = FOREST
river_x = 470 + 80 * np.sin((Y - horizon) / 55.0)
region[land & (np.abs(X - river_x) < 10 + (Y - horizon) / 9)] = RIVER
sea = Y >= 760 + 14 * np.sin(X / 47.0)
region[sea] = SEA
region[sea & (Y > 850)] = SEA_DEEP
region[dist > border_in] = BORDER

# ---------------------------------------------------------------- cut each region into pieces
piece_size = {SKY: 64, SUN: 40, SNOW: 34, ROCK: 40, ROCK_DARK: 40, FOREST: 36, FIELD: 46, RIVER: 22, SEA: 48, SEA_DEEP: 52}
labels = np.zeros((N, N), np.int32)
next_id = 1
for rid_ in piece_size:
    m = (region == rid_) & glass_area
    if not m.any():
        continue
    ys, xs = np.nonzero(m)
    step = piece_size[rid_]
    count = max(1, int(m.sum() / (step * step)))
    pick = r.choice(len(xs), size=count, replace=len(xs) < count)
    sites = np.stack([xs[pick], ys[pick]], 1).astype(np.float32)
    _, near = cKDTree(sites).query(np.stack([xs, ys], 1))
    labels[ys, xs] = near + next_id
    next_id += count + 1
# border: 36 alternating segments
ang = (np.degrees(np.arctan2(Y - cy, X - cx)) + 360) % 360
bm = (region == BORDER) & glass_area
seg = (ang // 10).astype(np.int32)
labels[bm] = 50000 + seg[bm]

# ---------------------------------------------------------------- colour per piece
base = {
    SKY: ("#2C5FA8", "#86BDE6"), SUN: ("#FFD36E", "#FFE9A8"), SNOW: ("#EEF4FA", "#CFDCEA"),
    ROCK: ("#8E9BB0", "#A8B3C4"), ROCK_DARK: ("#4F5D74", "#5F6E86"), FOREST: ("#25563A", "#3E7A44"),
    FIELD: ("#6FA548", "#93BE5A"), RIVER: ("#5BC2DA", "#8FDCEB"), SEA: ("#1F7E96", "#2B97AE"), SEA_DEEP: ("#0E4D6B", "#185E80"),
}
img = np.zeros((N, N, 3), np.float32)
ids = np.unique(labels[labels > 0])
for pid in ids:
    m = labels == pid
    if pid >= 50000:
        col = hexrgb("#8E1F25") if (pid % 2) else hexrgb("#E8A238")
    else:
        reg = np.bincount(region[m]).argmax()
        a, b = base[reg]
        t = r.random()
        if reg == SKY:  # the sky darkens towards the top of the window
            yy = Y[m].mean()
            t = np.clip((yy - 120) / 440, 0, 1) * .8 + r.random() * .2
        col = hexrgb(a) * (1 - t) + hexrgb(b) * t
        col = np.clip(col * (0.92 + r.random() * .16), 0, 1)
    img[m] = col

# cathedral glass: thicker (darker) near the lead, streaks and seeds, bright in the middle
edges = np.zeros((N, N), bool)
edges[:-1] |= labels[:-1] != labels[1:]
edges[:, :-1] |= labels[:, :-1] != labels[:, 1:]
edges &= glass_area
to_lead = ndimage.distance_transform_edt(~edges).astype(np.float32)
centre = np.clip(to_lead / 22, 0, 1)
streak = ndimage.gaussian_filter(rng(5).random((N, N)).astype(np.float32), (2, 14))
streak = (streak - streak.mean()) / (streak.std() + 1e-6)
cloud = fbm(N, 9, octaves=4, base=10)
tex = 0.70 + 0.36 * centre + 0.05 * streak + 0.16 * (cloud - .5)
img = np.clip(img * tex[..., None], 0, 1)

# grisaille: dark paint inside pieces - ridge lines on the rock, ripples on the sea, tree marks
paint = np.zeros((N, N), np.float32)
for px, py, w in [(330, 300, 190), (540, 210, 230), (760, 330, 170)]:
    for k in range(4):
        x0 = px + (k - 1.5) * 34
        line = (np.abs(X - (x0 + (Y - py) * .25 * (1 if k % 2 else -1))) < 2.2) & (region == ROCK) & (Y > py + 40 + k * 20) & (Y < horizon)
        paint += line
paint += ((np.abs(np.sin(X / 18.0 + Y / 9.0)) < .06) & (region >= SEA) & (region <= SEA_DEEP)) * .8
paint += ((np.abs(np.sin(X / 9.0)) < .12) & (np.abs(np.sin(Y / 11.0)) < .25) & (region == FOREST)) * .9
paint += ((np.abs(dist - (border_in + 32)) < 2) & (region == BORDER)) * .6
paint = ndimage.gaussian_filter(np.clip(paint, 0, 1), .8)
img = img * (1 - .55 * paint[..., None])
# sun rays painted in pale yellow on the sky
ray_a = np.degrees(np.arctan2(Y - 330, X - 650)) % 30
rays = (np.abs(ray_a - 15) < 2.5) & (region == SKY) & (np.hypot(X - 650, Y - 330) < 210)
img[rays] = np.clip(img[rays] * .6 + hexrgb("#FFE6A0") * .45, 0, 1)

# backlight: warm from the lower right, as a candle behind the window
light = np.clip(1 - np.hypot(X - 700, Y - 760) / 760, 0, 1)
img = np.clip(img * (0.72 + 0.6 * light[..., None]) + hexrgb("#FFB04A") * (light ** 4)[..., None] * .22, 0, 1)

# ---------------------------------------------------------------- the gold compass star over the glass
def star(cx, cy, long, short, waist):
    pts = []
    for i in range(16):
        a = -np.pi / 2 + i * np.pi / 8
        pts.append((cx + np.cos(a) * (long if i % 4 == 0 else short if i % 4 == 2 else waist),
                    cy + np.sin(a) * (long if i % 4 == 0 else short if i % 4 == 2 else waist)))
    return pts

star_m = mask_from(polygon(star(cx, cy, 250, 120, 44)))
half = ((np.degrees(np.arctan2(Y - cy, X - cx)) + 90) % 45) < 22.5
gold = np.where(half[..., None], hexrgb("#FFE1A0"), hexrgb("#C7811E"))
gold = gold * (0.85 + 0.3 * light[..., None])

# ---------------------------------------------------------------- compose
canvas = blank()
outer = mask_from(circle(cx, cy, R + 70))
canvas = shadow(canvas, outer, offset=(12, 20), blur=20, alpha=.65)
canvas = layer(canvas, img, glass_area.astype(np.float32))
# lead: rounded came, dark with a dull top highlight
lead = ndimage.gaussian_filter(ndimage.binary_dilation(edges, iterations=4).astype(np.float32), 1.0)
lead_hi = ndimage.gaussian_filter(ndimage.binary_dilation(edges, iterations=1).astype(np.float32), .8)
canvas[..., :3] = canvas[..., :3] * (1 - lead[..., None]) + hexrgb("#24211E") * lead[..., None]
canvas[..., :3] = np.clip(canvas[..., :3] + (lead_hi * lead * .14)[..., None], 0, 1)
# light bleeding across the lead from the brightest glass
bright = np.clip((img.max(2) - .7) * 3.2, 0, 1) * glass_area
canvas[..., :3] = glow(canvas[..., :3], bright, hexrgb("#FFF1D0"), 7, .45)
# the star, with its own lead outline and a soft glow
star_edge = ndimage.binary_dilation(star_m > .5, iterations=5) & ~(star_m > .5)
canvas[..., :3] = glow(canvas[..., :3], star_m, hexrgb("#FFD27A"), 18, .35)
canvas = layer(canvas, hexrgb("#24211E"), ndimage.gaussian_filter(star_edge.astype(np.float32), 1))
canvas = layer(canvas, gold, star_m)
seams = (np.abs(((np.degrees(np.arctan2(Y - cy, X - cx)) + 90) % 45) - 22.5) < .9) & (star_m > .5)
canvas = layer(canvas, hexrgb("#7A4A10"), seams.astype(np.float32) * .8)
hub = mask_from(circle(cx, cy, 34))
hd = np.hypot(X - cx + 10, Y - cy + 10)
canvas = layer(canvas, hexrgb("#FFF4D8")[None, None] * np.clip(1.2 - hd / 60, .6, 1.1)[..., None], hub)
canvas = layer(canvas, hexrgb("#24211E"), np.clip(1 - np.abs(dist - 34) / 3, 0, 1))

# frame: dark carved wood with a brass bead on both edges and eight brass bosses
ring_m = np.clip(outer - glass_area, 0, 1)
wood = fbm(N, 21, octaves=4, base=6)
grain = 0.5 + 0.5 * np.sin((ang * 2.2 + wood * 70) / 4)
lightdir = np.clip(((cx - X) + (cy - Y)) / (dist + 1e-6) * .5 + .5, 0, 1)
bevel = np.clip(1 - np.abs(dist - (R + 35)) / 35, 0, 1)
wood_col = hexrgb("#3C2716")[None, None] * (0.6 + 0.4 * grain[..., None])
wood_col = wood_col * (0.55 + 0.75 * (bevel ** .7 * lightdir)[..., None])
canvas = layer(canvas, wood_col, ring_m)
for rr, w in [(R + 3, 6), (R + 66, 5)]:
    band = np.clip(1 - np.abs(dist - rr) / w, 0, 1)
    canvas = layer(canvas, hexrgb("#E2B868")[None, None] * (0.5 + 0.7 * lightdir)[..., None], band)
for a in range(22, 360, 45):
    bx, by = cx + np.cos(np.radians(a)) * (R + 35), cy + np.sin(np.radians(a)) * (R + 35)
    bd = np.hypot(X - bx + 5, Y - by + 5)
    canvas = layer(canvas, hexrgb("#F5DB9C")[None, None] * np.clip(1.3 - bd / 15, .45, 1.2)[..., None], mask_from(circle(bx, by, 14)))

save(canvas, out)
print("ok", out)
