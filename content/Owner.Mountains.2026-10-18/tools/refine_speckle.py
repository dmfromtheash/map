# For grounds whose texture IS fine speckle (snow dust, 1-3 px dots): the AI 4x result gives the
# large shapes and crisp flake edges; the detail band of the SOURCE (everything finer than ~3 source
# pixels) is put back exactly where it was, each source pixel drawn as a round dot at 4x, and only
# what the AI lost is added, so the detail energy matches the source at every scale.
import sys, numpy as np
from PIL import Image
from scipy import ndimage as nd
src_p, a_p, b_p, out_p = sys.argv[1:5]
wa = float(sys.argv[5]) if len(sys.argv) > 5 else 0.3
gain = float(sys.argv[6]) if len(sys.argv) > 6 else 1.0
S, R = 4, 3.0
src = np.asarray(Image.open(src_p).convert('RGB'), np.float32) / 255
A = np.asarray(Image.open(a_p).convert('RGB'), np.float32) / 255
B = np.asarray(Image.open(b_p).convert('RGB'), np.float32) / 255
ai = wa * A + (1 - wa) * B
g = lambda a, s: np.stack([nd.gaussian_filter(a[..., c], s) for c in range(3)], -1)
band_src = src - g(src, R)
blk = np.repeat(np.repeat(band_src, S, 0), S, 1)
dots = g(blk, 1.4)
dots = dots + 0.7 * (dots - g(dots, 2.0))          # crisp dot edges
band_ai = ai - g(ai, R * S)
out = np.clip(ai + gain * (dots - band_ai), 0, 1)
H, W = out.shape[:2]
Image.fromarray((np.dstack([out, np.ones((H, W, 1), np.float32)]) * 255 + .5).astype(np.uint8), 'RGBA').save(out_p, compress_level=6)
