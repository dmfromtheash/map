"""GPU throughput variants for Real-ESRGAN on the Mac (run by Claude outside the agent sandbox).
Usage: python bench.py <workdir> ; uses ~/MapWork/agents/esr-tune/in/sample as sources."""
import glob, os, subprocess, sys, time
import numpy as np
from PIL import Image
sys.path.insert(0, os.path.expanduser('~/MapWork/upscale'))
import finish_asset as R

W = sys.argv[1]
ESR = os.path.expanduser('~/MapWork/esr/realesrgan-ncnn-vulkan')
M = os.path.expanduser('~/MapWork/esr/models')
os.makedirs(f'{W}/in', exist_ok=True)
src = sorted(glob.glob(os.path.expanduser('~/MapWork/agents/esr-tune/in/sample/*')))
if not os.listdir(f'{W}/in'):
    for p in src:
        R.prepare_rgb(R.load_rgba(p), f'{W}/in/{os.path.splitext(os.path.basename(p))[0]}.png')
src = glob.glob(f'{W}/in/*.png')  # a pre-filled in/ (a subset) sets the count
px = sum(np.prod(Image.open(p).size) for p in src)
print('files', len(src), 'input Mpx', round(px / 1e6, 1), flush=True)


def run(model, out, extra=()):
    os.makedirs(out, exist_ok=True)
    return subprocess.Popen([ESR, '-i', f'{W}/in', '-o', out, '-n', model, '-s', '4', '-f', 'png', '-m', M, *extra],
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def variant(name, concurrent, extra=()):
    t = time.time()
    if concurrent:
        ps = [run('realesrgan-x4plus-anime', f'{W}/{name}/a', extra), run('realesrgan-x4plus', f'{W}/{name}/b', extra)]
        codes = [p.wait() for p in ps]
    else:
        codes = [run('realesrgan-x4plus-anime', f'{W}/{name}/a', extra).wait(),
                 run('realesrgan-x4plus', f'{W}/{name}/b', extra).wait()]
    dt = time.time() - t
    diff = 0.0
    if name != 'v0':
        for sub in 'ab':
            for p in glob.glob(f'{W}/v0/{sub}/*.png'):
                a = np.asarray(Image.open(p), np.int16)
                b = np.asarray(Image.open(p.replace('/v0/', f'/{name}/')), np.int16)
                diff = max(diff, float(np.percentile(np.abs(a - b), 99.9)))
    print(f'{name} concurrent={concurrent} extra={" ".join(extra)} codes={codes} sec={dt:.1f} '
          f'files/h={len(src) / dt * 3600:.0f} p99.9diff={diff:.0f}', flush=True)


variant('v0', False)
variant('v1', True)
variant('v2', False, ('-j', '2:4:4'))
variant('v3', True, ('-j', '2:4:4'))
