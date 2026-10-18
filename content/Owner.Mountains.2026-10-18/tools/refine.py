# Combine two Real-ESRGAN 4x results and re-synthesise the source's paper grain at full resolution.
import sys, numpy as np
from PIL import Image
from scipy import ndimage as nd
src_p, a_p, b_p, out_p = sys.argv[1:5]
wa = float(sys.argv[5]) if len(sys.argv) > 5 else 0.6
grain_k = float(sys.argv[6]) if len(sys.argv) > 6 else 1.0
S = 4
src = np.asarray(Image.open(src_p).convert('RGBA'), np.float32) / 255
A = np.asarray(Image.open(a_p).convert('RGBA'), np.float32) / 255
B = np.asarray(Image.open(b_p).convert('RGBA'), np.float32) / 255
rgb = wa * A[..., :3] + (1 - wa) * B[..., :3]
# alpha: upscale source alpha smoothly, then sharpen the edge a little (keeps 1:1 silhouette)
al = np.asarray(Image.fromarray((src[..., 3] * 255).astype(np.uint8)).resize((rgb.shape[1], rgb.shape[0]), Image.LANCZOS), np.float32) / 255
al = np.clip((al - 0.5) * 1.6 + 0.5, 0, 1)
al = np.minimum(al, np.maximum(A[..., 3], B[..., 3]) + 0.02) if A.shape[2] == 4 else al
# grain: local amplitude of the source's finest detail (what the AI smoothed away)
lum = src[..., :3].mean(2)
hp = lum - nd.gaussian_filter(lum, 1.2)
amp = np.sqrt(nd.gaussian_filter(hp * hp, 2.0))
amp_up = np.asarray(Image.fromarray(amp).resize((rgb.shape[1], rgb.shape[0]), Image.BICUBIC), np.float32)
# what's left in the AI result at the same (source-pixel) scale
lumu = rgb.mean(2)
hpu = lumu - nd.gaussian_filter(lumu, 1.2 * S)
ampu = np.sqrt(nd.gaussian_filter(hpu * hpu, 2.0 * S))
missing = np.sqrt(np.clip(amp_up ** 2 - ampu ** 2, 0, None))
rng = np.random.default_rng(1408)
n = rng.standard_normal(lumu.shape).astype(np.float32)
n = nd.gaussian_filter(n, 0.9)  # grain dot ~2px at 4x, crisp when zoomed
n /= n.std() + 1e-6
rgb = np.clip(rgb + (grain_k * missing * n)[..., None], 0, 1)
out = np.dstack([rgb, al])
out[..., :3] *= 1  # straight alpha kept
Image.fromarray((out * 255 + 0.5).astype(np.uint8), 'RGBA').save(out_p, optimize=False, compress_level=6)
