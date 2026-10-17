"""Ксилография: a woodblock print in three inks on warm paper - an island mountain, a curling
wave in front of it, a vermilion sun behind marked with compass points; every tone is carved
lines (sky stripes thinning upward, wave lines following the swell, hatching on the shaded
slope), and the print has the slight starvation and spread of a hand-pulled impression."""
import sys
import numpy as np
from scipy import ndimage
from PIL import Image, ImageDraw
from common import N, rng, fbm, hexrgb, layer, blank, shadow, save, yy_xx

out = sys.argv[1]
Y, X = yy_xx()
cx, cy, R = 512, 512, 440
dist = np.hypot(X - cx, Y - cy)
disc = np.clip((R - dist) / 1.5, 0, 1)

paper = hexrgb("#F1E4C6")[None, None] * (0.94 + 0.08 * (fbm(N, 1, octaves=6, base=30)[..., None] - .5))
grain = fbm(N, 2, octaves=5, base=60)                       # wood grain of the block, shows in solid areas
grain_lines = 0.5 + 0.5 * np.sin(Y / 3.2 + 6 * fbm(N, 3, octaves=3, base=5))


def poly(points):
    im = Image.new("L", (N * 2, N * 2), 0)
    ImageDraw.Draw(im).polygon([(x * 2, y * 2) for x, y in points], fill=255)
    return np.asarray(im.resize((N, N), Image.LANCZOS), np.float32) / 255


def print_ink(mask, seed):
    """A hand-pulled impression: ink spreads a little, starves where the block was dry."""
    starve = fbm(N, seed, octaves=4, base=18)
    m = ndimage.gaussian_filter(mask, .7)
    return np.clip(m * (1.05 - .5 * np.clip((starve - .62) * 3, 0, 1)) * (0.9 + 0.12 * grain_lines), 0, 1)


horizon = 600
# ---------------------------------------------------------------- shapes
mountain = poly([(150, horizon + 10), (330, 420), (420, 330), (470, 300), (520, 330), (640, 450), (860, horizon + 10)])
snow = poly([(400, 350), (470, 300), (530, 342), (505, 360), (480, 345), (455, 365), (430, 350)])
sun_c, sun_r = (650, 300), 120
sun = np.clip((sun_r - np.hypot(X - sun_c[0], Y - sun_c[1])) / 1.5, 0, 1)

# the wave: a crest curling left over a trough, its foam fingers
wave = np.zeros((N, N), np.float32)
crest = poly([(90, 900), (120, 760), (200, 660), (300, 610), (410, 620), (470, 670), (440, 700), (400, 676), (350, 668), (300, 690),
              (270, 740), (280, 800), (330, 860), (420, 900)])
curl_hole = poly([(330, 700), (380, 690), (410, 705), (380, 735), (340, 745)])
wave = np.clip(crest - curl_hole, 0, 1)
fingers = np.zeros((N, N), np.float32)
for i, (fx, fy) in enumerate([(300, 610), (340, 608), (380, 612), (420, 624), (455, 645)]):
    fingers = np.maximum(fingers, poly([(fx - 16, fy + 12), (fx + 4, fy - 34 - 6 * (i % 2)), (fx + 18, fy + 10)]))
sea = np.clip((Y - horizon) / 2, 0, 1)

# ---------------------------------------------------------------- carved line patterns (black ink)
ink = np.zeros((N, N), np.float32)
# sky: horizontal stripes that thin out upward
sky_t = np.clip((horizon - Y) / 420, 0, 1)
stripe = np.abs(((Y + 4 * np.sin(X / 70)) % 22) - 11) / 11
sky_lines = (stripe < (0.32 - 0.26 * sky_t)) & (Y < horizon) & (sun < .5) & (mountain < .5)
ink = np.maximum(ink, sky_lines.astype(np.float32) * (sky_t < .92))
# sea: lines following the swell
swell = (Y + 18 * np.sin(X / 46.0 + Y / 90.0)) % 26
sea_lines = (np.abs(swell - 13) < 3.4) & (sea > .5) & (wave < .5)
ink = np.maximum(ink, sea_lines.astype(np.float32))
# mountain: outline + hatching on the right (shaded) slope
mount_edge = np.clip(ndimage.binary_dilation(mountain > .5, iterations=5).astype(np.float32) - (mountain > .5), 0, 1)
ink = np.maximum(ink, mount_edge)
slope = ((X - Y * .55) % 16 < 4.2) & (mountain > .5) & (X > 470 + (Y - 300) * .15) & (snow < .5)
ink = np.maximum(ink, slope.astype(np.float32))
ridges = (np.abs(np.sin((X - 470) / 26.0 + (Y - 300) / 60.0)) < .08) & (mountain > .5) & (snow < .5) & (X < 470)
ink = np.maximum(ink, ridges.astype(np.float32) * .9)
snow_edge = np.clip(ndimage.binary_dilation(snow > .5, iterations=3).astype(np.float32) - (snow > .5), 0, 1) * (mountain > .5)
ink = np.maximum(ink, snow_edge)
# wave: outline, inner lines following the crest, foam dots
wave_edge = np.clip(ndimage.binary_dilation(wave > .5, iterations=6).astype(np.float32) - (wave > .5), 0, 1)
ink = np.maximum(ink, wave_edge)
inside = ndimage.distance_transform_edt(wave > .5).astype(np.float32)
wave_lines = (np.abs((inside % 18) - 9) < 2.6) & (wave > .5) & (inside > 24)
ink = np.maximum(ink, wave_lines.astype(np.float32))
fing_edge = np.clip(ndimage.binary_dilation(fingers > .5, iterations=4).astype(np.float32) - (fingers > .5), 0, 1)
ink = np.maximum(ink, fing_edge)
r = rng(9)
for _ in range(70):  # spray
    a = r.uniform(np.pi * 1.05, np.pi * 1.95); d = r.uniform(8, 70)
    sx, sy = 380 + np.cos(a) * d * 2.2, 610 + np.sin(a) * d
    ink = np.maximum(ink, np.clip(1 - np.hypot(X - sx, Y - sy) / r.uniform(3, 6), 0, 1))
# horizon line
ink = np.maximum(ink, ((np.abs(Y - horizon) < 3) & (wave < .5) & (mountain < .5)).astype(np.float32))

# ---------------------------------------------------------------- colour blocks
indigo = (sea > .5) & (wave < .5) | (wave > .5) & (inside > 40) & ((inside % 36) > 18)
indigo = indigo.astype(np.float32)
sky_wash = (Y < horizon) & (sky_t > .45) & (sun < .5) & (mountain < .5)

rgb = paper.copy()
blue = hexrgb("#2F4F86")
rgb = rgb * (1 - print_ink(sky_wash.astype(np.float32), 20)[..., None] * .18) + blue * print_ink(sky_wash.astype(np.float32), 20)[..., None] * .18
rgb = rgb * (1 - print_ink(indigo, 21)[..., None] * .75) + blue * print_ink(indigo, 21)[..., None] * .75
red = print_ink(sun, 22)
rgb = rgb * (1 - red[..., None]) + hexrgb("#C8452C") * red[..., None]
# compass points carved out of the sun: four pale lines and a ring
ang = np.degrees(np.arctan2(Y - sun_c[1], X - sun_c[0])) % 90
sd = np.hypot(X - sun_c[0], Y - sun_c[1])
carve = ((np.abs(ang - 45) > 42) & (sd > 20) & (sd < sun_r - 14)) | (np.abs(sd - (sun_r - 26)) < 2.5)
carve = carve & (sun > .5)
rgb[carve] = paper[carve] * .98
mount_fill = print_ink(mountain * (snow < .5), 23)
rgb = rgb * (1 - mount_fill[..., None] * .35) + hexrgb("#6F7F6A") * mount_fill[..., None] * .35
key = print_ink(ink, 24)
rgb = rgb * (1 - key[..., None]) + hexrgb("#1B1611") * key[..., None]

# ---------------------------------------------------------------- the print's border: a black ring and a pale margin
ring = np.clip(1 - np.abs(dist - (R - 16)) / 7, 0, 1)
rgb = rgb * (1 - print_ink(ring, 25)[..., None]) + hexrgb("#1B1611") * print_ink(ring, 25)[..., None]
thin = np.clip(1 - np.abs(dist - (R - 34)) / 2, 0, 1)
rgb = rgb * (1 - thin[..., None]) + hexrgb("#1B1611") * thin[..., None]
outside_art = dist > R - 34
rgb[outside_art & (dist < R - 22)] = paper[outside_art & (dist < R - 22)]

canvas = blank()
canvas = shadow(canvas, disc, offset=(10, 16), blur=16, alpha=.5)
canvas = layer(canvas, np.clip(rgb, 0, 1), disc)
save(canvas, out)
print("ok", out)
