"""Акварель и тушь: an island painted wet-in-wet on cold-press paper - pigment pooling dark at the
edges of each wash, granulation in the blues, soft blooms - then drawn over with a dip pen: a
wobbly coast line, hatched peaks, a compass rose, a dotted route ending in a red mark."""
import sys
import numpy as np
from scipy import ndimage
from PIL import Image, ImageDraw
from common import N, rng, fbm, hexrgb, layer, blank, shadow, save, yy_xx

out = sys.argv[1]
r = rng(23)
Y, X = yy_xx()
cx, cy = 512, 512

# ---------------------------------------------------------------- paper: a round sheet with a deckled edge
ang = np.arctan2(Y - cy, X - cx)
deckle = 452 + 10 * fbm(N, 2, octaves=5, base=12)[(((ang + np.pi) / (2 * np.pi)) * (N - 1)).astype(int), 300]
dist = np.hypot(X - cx, Y - cy)
sheet = np.clip((deckle - dist) / 2.5, 0, 1)
grain = fbm(N, 3, octaves=6, base=40)
tooth = ndimage.gaussian_filter(rng(4).random((N, N)).astype(np.float32), .9)
paper = hexrgb("#F6EDD9")[None, None] * (0.95 + 0.06 * (grain[..., None] - .5) + 0.05 * (tooth[..., None] - .5))
paper = paper * (1 - .10 * np.clip((dist - 300) / 160, 0, 1)[..., None])  # a little age towards the edge


def wash(canvas_rgb, mask, pigment, density, seed, granulate=0.0, edge=0.55, bloom=0.0):
    """One watercolour wash: subtractive pigment, darker where it pooled at the edge of the wet area."""
    noise = fbm(N, seed, octaves=5, base=10)
    m = mask * (0.75 + 0.5 * (noise - .5))
    inside = ndimage.distance_transform_edt(mask > .5).astype(np.float32)
    rim = np.exp(-inside / 7.0) * (mask > .5)
    amount = np.clip(m * density + rim * edge * density, 0, 1)
    if granulate:
        g = ndimage.gaussian_filter(rng(seed + 1).random((N, N)).astype(np.float32), 1.1)
        amount = amount * (1 + granulate * (g - .5) * 2)
    if bloom:
        b = fbm(N, seed + 2, octaves=3, base=5)
        amount = amount * (1 - bloom * np.clip((b - .62) * 4, 0, 1))
    pig = hexrgb(pigment)
    # subtractive mixing: paper * (pigment ^ amount)
    return canvas_rgb * np.power(pig[None, None], np.clip(amount, 0, 1.4)[..., None])


def blob(seed, scale, thresh, centre, radius, warp=60):
    n = fbm(N, seed, octaves=5, base=scale)
    wx = (fbm(N, seed + 10, octaves=3, base=4) - .5) * warp
    wy = (fbm(N, seed + 11, octaves=3, base=4) - .5) * warp
    d = np.hypot(X + wx - centre[0], Y + wy - centre[1]) / radius
    v = n * 0.55 + (1 - d) * 0.9
    return ndimage.gaussian_filter((v > thresh).astype(np.float32), 1.2)


rgb = paper.copy()
# the sea: a pale cerulean wash fading into the paper, granulating
sea = np.clip(1 - np.clip((dist - 250) / 190, 0, 1), 0, 1) * (sheet > .5)
sea = ndimage.gaussian_filter((sea > .02).astype(np.float32) * (0.35 + 0.65 * sea), 2)
rgb = wash(rgb, sea, "#5B9CCB", .8, 11, granulate=.6, edge=.0, bloom=.3)
island = blob(21, 6, 0.5, (500, 500), 330, warp=90)
coastal = np.clip(ndimage.gaussian_filter(island, 18) - island, 0, 1)
rgb = wash(rgb, np.clip(coastal * 3, 0, 1), "#2F78AE", .75, 12, granulate=.7, edge=.2)  # deeper water near the shore
rgb = wash(rgb, island, "#E3C779", .75, 13, edge=.55)                                     # sand/ochre land
inland = blob(31, 7, 0.66, (490, 500), 320) * island
rgb = wash(rgb, inland, "#6E9E3E", .85, 14, edge=.6, bloom=.25)                          # green
woods = blob(41, 9, 0.8, (420, 560), 260) * inland
rgb = wash(rgb, woods, "#2F5E35", .9, 15, edge=.7)
hills = blob(51, 8, 0.8, (570, 420), 230) * inland
rgb = wash(rgb, hills, "#B8955E", .55, 16, edge=.5)
lake = blob(61, 10, 0.9, (600, 610), 150) * inland
rgb = wash(rgb, lake, "#4B92BF", .75, 17, granulate=.5, edge=.8)

# ---------------------------------------------------------------- ink: a dip pen with pressure and wobble
ink = np.zeros((N, N), np.float32)
img = Image.new("L", (N * 2, N * 2), 0)
pen = ImageDraw.Draw(img)


def stroke(points, width, seed=0):
    rr = rng(seed)
    pts = np.array(points, np.float32)
    if len(pts) > 2:
        jitter = ndimage.gaussian_filter1d(rr.normal(0, 1.2, pts.shape), 2, axis=0)
        pts = pts + jitter
    for i in range(len(pts) - 1):
        t = i / max(len(pts) - 1, 1)
        w = width * (0.6 + 0.6 * np.sin(np.pi * t) + 0.2 * rr.random())
        a, b = pts[i] * 2, pts[i + 1] * 2
        pen.line([tuple(a), tuple(b)], fill=255, width=max(1, int(w * 2)))
        pen.ellipse([a[0] - w, a[1] - w, a[0] + w, a[1] + w], fill=255)


# coast and the old-map shore lines are drawn after the pen strokes, from the island's edge
# peaks: little hatched mountains on the hills
peaks = [(505, 430, 44), (590, 395, 54), (650, 440, 38), (560, 470, 34)]
shade = np.zeros((N, N), np.float32)
for k, (mx, my, s) in enumerate(peaks):
    # the shaded right face as a grey-violet wash under the pen lines
    face = Image.new("L", (N, N), 0)
    ImageDraw.Draw(face).polygon([(mx, my - s * .9), (mx + s, my + s * .55), (mx + s * .15, my + s * .55)], fill=255)
    shade = np.maximum(shade, np.asarray(face, np.float32) / 255)
    stroke([(mx - s, my + s * .55), (mx - s * .4, my - s * .2), (mx, my - s * .9), (mx + s * .5, my - s * .1), (mx + s, my + s * .55)], 2.6, 10 + k)
    for h in range(5):
        stroke([(mx + 3 + h * s * .14, my - s * .6 + h * s * .22), (mx + 8 + h * s * .14, my + s * .5)], 1.0, 20 + k * 5 + h)
# trees: small rounded marks in the woods
ys3, xs3 = np.nonzero(woods > .6)
pick = r.choice(len(xs3), 26, replace=False)
for i, j in enumerate(pick):
    x0, y0 = xs3[j], ys3[j]
    stroke([(x0 - 6, y0 + 4), (x0, y0 - 8), (x0 + 6, y0 + 4)], 1.3, 40 + i)
# the route: dots across the island to the red mark
route = [(380, 640), (420, 610), (455, 600), (490, 585), (520, 560), (548, 540), (575, 525)]
for i, (x0, y0) in enumerate(route):
    pen.ellipse([x0 * 2 - 6, y0 * 2 - 6, x0 * 2 + 6, y0 * 2 + 6], fill=255)
# compass rose, lower left, on the sea
rx, ry, rs = 250, 770, 96
for i in range(8):
    a = -np.pi / 2 + i * np.pi / 4
    L = rs if i % 2 == 0 else rs * .55
    tip = (rx + np.cos(a) * L, ry + np.sin(a) * L)
    l = (rx + np.cos(a - .5) * 14, ry + np.sin(a - .5) * 14)
    rr_ = (rx + np.cos(a + .5) * 14, ry + np.sin(a + .5) * 14)
    stroke([l, tip, rr_], 1.6, 60 + i)
pen.ellipse([(rx - rs * .32) * 2, (ry - rs * .32) * 2, (rx + rs * .32) * 2, (ry + rs * .32) * 2], outline=255, width=4)
rgb = wash(rgb, ndimage.gaussian_filter(shade, 1.5), "#6E6390", .6, 18, edge=.3)
ink = np.asarray(img.resize((N, N), Image.LANCZOS), np.float32) / 255
land_d = ndimage.distance_transform_edt(island < .5).astype(np.float32)   # distance outside the coast
pressure = 1.6 + 1.6 * fbm(N, 90, octaves=3, base=8)
coast_ink = np.clip(pressure - land_d, 0, 1) * (island < .6)
# two broken shore lines offshore, as on old charts
breaks = fbm(N, 91, octaves=3, base=14) > .45
for off in (16, 30):
    coast_ink = np.maximum(coast_ink, np.clip(1.1 - np.abs(land_d - off), 0, 1) * breaks * .8)
ink = np.maximum(ink, ndimage.gaussian_filter(coast_ink, .6))
ink = ink * (0.85 + 0.3 * (ndimage.gaussian_filter(rng(70).random((N, N)).astype(np.float32), 2) - .5))  # ink density varies
rgb = rgb * (1 - ink[..., None] * (1 - hexrgb("#2B1D14")[None, None]))
# north half of the rose in red ink, and the mark at the end of the route
red = np.zeros((N, N), np.float32)
redimg = Image.new("L", (N * 2, N * 2), 0)
rp = ImageDraw.Draw(redimg)
rp.polygon([((rx - 10) * 2, ry * 2), (rx * 2, (ry - rs) * 2), ((rx + 10) * 2, ry * 2)], fill=255)
mx, my = 600, 512
for d in (-1, 1):
    rp.line([((mx - 18) * 2, (my - 18 * d) * 2), ((mx + 18) * 2, (my + 18 * d) * 2)], fill=255, width=14)
red = np.asarray(redimg.resize((N, N), Image.LANCZOS), np.float32) / 255
rgb = wash(rgb, red, "#B8322A", .95, 80, edge=.4)

canvas = blank()
canvas = shadow(canvas, sheet, offset=(10, 16), blur=16, alpha=.45)
canvas = layer(canvas, np.clip(rgb, 0, 1), sheet)
save(canvas, out)
print("ok", out)
