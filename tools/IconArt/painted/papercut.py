"""Бумажная диорама: a shadow box of hand-cut paper layers - a dusk sky, a low moon, far and near
mountain ranges with cut snow caps, a forest edge, the sea with cut waves and a lighthouse on a
rock - every layer casting a soft shadow on the one behind it, all in a deep paper frame."""
import sys
import numpy as np
from scipy import ndimage
from PIL import Image, ImageDraw
from common import N, rng, fbm, hexrgb, layer, blank, shadow, save, yy_xx

out = sys.argv[1]
r = rng(5)
Y, X = yy_xx()
box = (96, 96, 928, 928)                  # inside of the frame
fiber = fbm(N, 31, octaves=5, base=24)    # paper fibres
fiber2 = ndimage.gaussian_filter(rng(32).random((N, N)).astype(np.float32), .7)


def jagged_line(xs, base, amp, seed, freq=1.0):
    """A hand-cut edge: a smooth profile with a slight scissor wobble."""
    rr = rng(seed)
    wob = ndimage.gaussian_filter1d(rr.normal(0, 1, len(xs)), 3) * 2.2
    return base + amp + wob


def cut(points):
    """A mask from a polygon, at 2x, with the tiny irregularity of a blade."""
    big = Image.new("L", (N * 2, N * 2), 0)
    ImageDraw.Draw(big).polygon([(x * 2, y * 2) for x, y in points], fill=255)
    m = np.asarray(big.resize((N, N), Image.LANCZOS), np.float32) / 255
    return m


def paper(color, seed, light=True):
    """Coloured paper: fibre texture and a gentle light from the top left."""
    tint = hexrgb(color)[None, None]
    f = fbm(N, seed, octaves=4, base=16)
    shade = 0.93 + 0.1 * (f - .5) + 0.05 * (fiber - .5)
    if light:
        shade = shade * (1.04 - 0.1 * (X + Y) / (2 * N))[...]
    return np.clip(tint * shade[..., None], 0, 1)


def range_profile(peaks, base_y, seed, rough=6):
    xs = np.arange(box[0] - 20, box[2] + 21, 4, dtype=np.float32)
    h = np.full_like(xs, base_y)
    for px, py, w in peaks:
        h = np.minimum(h, py + (np.abs(xs - px) / w) ** 1.15 * (base_y - py))
    h += ndimage.gaussian_filter1d(rng(seed).normal(0, rough, len(xs)), 2)
    return xs, h


canvas = blank()
inner = np.zeros((N, N), np.float32)
inner[box[1]:box[3], box[0]:box[2]] = 1

# sky: dusk, deep blue at the top to amber at the horizon
t = np.clip((Y - box[1]) / 520, 0, 1)[..., None]
sky = hexrgb("#24356B") * (1 - t) ** 1.2 + hexrgb("#FFB46A") * t ** 1.4 + hexrgb("#C2789A") * (4 * t * (1 - t)) * .55
sky = sky + hexrgb("#FFD9A0") * np.clip(1 - np.hypot(X - 640, Y - 520) / 380, 0, 1)[..., None] ** 2 * .45
sky = sky * (0.96 + 0.06 * (fiber[..., None] - .5))
canvas = layer(canvas, np.clip(sky, 0, 1), inner)
# stars: tiny cut holes
for _ in range(38):
    sx, sy = r.uniform(box[0] + 30, box[2] - 30), r.uniform(box[1] + 20, 380)
    rad = r.uniform(1.5, 3.4)
    canvas = layer(canvas, hexrgb("#FFF3D2"), np.clip(1.2 - np.hypot(X - sx, Y - sy) / rad, 0, 1))

layers = []
# moon
moon = np.clip(1 - np.hypot(X - 640, Y - 330) / 92, 0, 1) > 0
moon = ndimage.gaussian_filter(moon.astype(np.float32), .8)
layers.append((moon, paper("#FFF0C8", 41), 10))
# far range: lilac
xs, h = range_profile([(250, 360, 180), (470, 300, 200), (760, 340, 210)], 600, 2)
far = cut([(box[0] - 20, box[3])] + list(zip(xs, h)) + [(box[2] + 20, box[3])])
layers.append((far, paper("#C49BB0", 42), 14))
# near range: slate blue with cut snow caps
xs, h = range_profile([(330, 330, 170), (610, 260, 200), (860, 390, 150)], 660, 3, rough=8)
near = cut([(box[0] - 20, box[3])] + list(zip(xs, h)) + [(box[2] + 20, box[3])])
layers.append((near, paper("#5D739B", 43), 18))
snow = np.zeros((N, N), np.float32)
for px, py, w in [(330, 330, 170), (610, 260, 200), (860, 390, 150)]:
    line = py + 110 + 16 * np.sin((X - px) / 17.0) + 8 * np.sin((X - px) / 7.0)
    snow = np.maximum(snow, ((Y < line) & (np.abs(X - px) < w * .9)).astype(np.float32))
snow = ndimage.gaussian_filter(snow, .8) * near
layers.append((snow, paper("#F4F6FA", 44), 6))
# forest edge: dark green with a spiky top of fir trees
tops = []
x = box[0] - 20
while x < box[2] + 20:
    w = r.uniform(26, 40)
    hgt = r.uniform(70, 120)
    tops += [(x, 720), (x + w / 2, 720 - hgt), (x + w, 720)]
    x += w * .8
forest = cut([(box[0] - 20, box[3])] + tops + [(box[2] + 20, box[3])])
layers.append((forest, paper("#2E5A3A", 45), 22))
# hills: a lighter green band
hx = np.arange(box[0] - 20, box[2] + 21, 4, dtype=np.float32)
hy = 760 + 22 * np.sin(hx / 90.0) + 10 * np.sin(hx / 37.0)
hills = cut([(box[0] - 20, box[3])] + list(zip(hx, hy)) + [(box[2] + 20, box[3])])
layers.append((hills, paper("#6C9A45", 46), 22))
# sea with three cut wave lines
sy_ = 820 + 8 * np.sin(hx / 50.0)
sea = cut([(box[0] - 20, box[3])] + list(zip(hx, sy_)) + [(box[2] + 20, box[3])])
layers.append((sea, paper("#2C7FA0", 47), 24))
# lighthouse on a rock, lit window, light beam
rock = cut([(700, 860), (720, 820), (770, 806), (820, 826), (842, 862)])
tower = cut([(752, 812), (760, 690), (790, 690), (798, 812)])
cap = cut([(748, 694), (775, 664), (802, 694)])
layers.append((np.maximum(rock, 0), paper("#3B3029", 48), 12))
layers.append((tower, paper("#EFE6D6", 49), 10))
layers.append((cap, paper("#9C2F25", 50), 8))

for m, col, depth in layers:
    m = m * inner
    canvas = shadow(canvas, m, offset=(depth * .5, depth * .9), blur=depth * .7, alpha=.75)
    canvas = layer(canvas, col, m)
    # the cut edge catches the light: a thin bright lip along the top left of each piece
    lip = np.clip(m - ndimage.shift(m, (2.5, 2.5), order=1), 0, 1)
    canvas[..., :3] = np.clip(canvas[..., :3] + lip[..., None] * .18, 0, 1)

# the beam from the lantern: a soft warm wedge over everything
beam = cut([(776, 700), (box[0] + 40, 560), (box[0] + 40, 660)])
beam = ndimage.gaussian_filter(beam, 6) * inner
canvas[..., :3] = np.clip(canvas[..., :3] + beam[..., None] * hexrgb("#FFE2A0") * .22, 0, 1)
lamp = np.clip(1 - np.hypot(X - 775, Y - 712) / 34, 0, 1)
canvas[..., :3] = np.clip(canvas[..., :3] + (lamp ** 1.5)[..., None] * hexrgb("#FFE4A0") * 1.2, 0, 1)
win = cut([(768, 700), (782, 700), (782, 722), (768, 722)])
canvas = layer(canvas, hexrgb("#FFE7A6"), win)

# lift the scene: paper in a lit box is bright and saturated
rgb = canvas[..., :3]
grey = rgb.mean(2, keepdims=True)
rgb = np.clip((grey + (rgb - grey) * 1.35) * 1.28, 0, 1)
canvas[..., :3] = np.where(inner[..., None] > 0, rgb, canvas[..., :3])
# the box: inner walls in shadow (top/left darker), then a thick cream frame with a bevel
wall = np.zeros((N, N), np.float32)
dx = np.minimum(X - box[0], box[2] - X); dy = np.minimum(Y - box[1], box[3] - Y)
edge_d = np.minimum(dx, dy)
wall = np.clip(1 - edge_d / 46, 0, 1) * inner
canvas[..., :3] = canvas[..., :3] * (1 - .32 * wall[..., None])

frame_out = np.zeros((N, N), np.float32)
frame_out[40:984, 40:984] = 1
frame_out = ndimage.gaussian_filter(frame_out, 1)
frame = np.clip(frame_out - inner, 0, 1)
canvas = shadow(canvas, frame_out, offset=(10, 18), blur=18, alpha=.5)
grain = 0.5 + 0.5 * np.sin((Y * .9 + fbm(N, 52, octaves=4, base=6) * 120) / 5)
fr = hexrgb("#4A311D")[None, None] * (0.7 + 0.35 * grain[..., None])
# bevel: the outer frame faces light up/left and fall away down/right
fx = np.minimum(X - 40, 984 - X); fy = np.minimum(Y - 40, 984 - Y)
toward = np.where((X + Y) < N, 1.08, .82)
fr = fr * toward[..., None]
canvas = layer(canvas, fr, frame)
# a thin gold line around the opening
gl = np.zeros((N, N), np.float32)
gl[box[1] - 10:box[3] + 10, box[0] - 10:box[2] + 10] = 1
gl[box[1] - 4:box[3] + 4, box[0] - 4:box[2] + 4] = 0
canvas = layer(canvas, hexrgb("#E2B868"), ndimage.gaussian_filter(gl, .8))
ol = np.zeros((N, N), np.float32); ol[40:984, 40:984] = 1; ol[48:976, 48:976] = 0
canvas = layer(canvas, hexrgb("#C9A15A"), ndimage.gaussian_filter(ol, .8))

save(canvas, out)
print("ok", out)
