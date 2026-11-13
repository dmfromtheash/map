"""Mac side of the two-host upscale. Runs inside ~/MapWork only.

Watches ~/MapWork/upscale/jobs/<id>/ for a READY marker (written last by the PC feeder). A job holds
src/<i><ext> (original reference files, unchanged) and job.json [{"i", "rel", "ext"}]. The recipe is the same
finish_asset.py as on the PC (copied next to this file): prepare_rgb -> Real-ESRGAN x4plus-anime + x4plus ->
finish (60/40, grain) -> verify, plus a block check that rejects local GPU garbage the mean check can miss.
Writes out/<i>.webp, results.jsonl and DONE. The feeder writes FETCHED after it has taken the results; the
worker then deletes that job folder. One GPU stage at a time; the GPU stage of the next job overlaps the CPU
finish of the current one.
"""
import hashlib, json, math, os, shutil, subprocess, sys, time, traceback
from concurrent.futures import ProcessPoolExecutor, ThreadPoolExecutor
from pathlib import Path

import numpy as np
from PIL import Image

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import finish_asset as recipe  # noqa: E402

WORK = Path(os.environ.get('MAPWORK', Path.home() / 'MapWork'))
JOBS = WORK / 'upscale' / 'jobs'
ESR = WORK / 'esr' / 'realesrgan-ncnn-vulkan'
MODELS = WORK / 'esr' / 'models'
LOG = WORK / 'upscale' / 'worker.log'
BLOCK_LIMIT = 60.0  # 8x8 block mean colour error (0..255); clean outputs stay below ~40


def log(msg):
    with LOG.open('a', encoding='utf-8') as f:
        f.write(time.strftime('%Y-%m-%d %H:%M:%S ') + msg + '\n')


def factor_for(side):
    return 4 if side <= 512 else 2


def block_max(src_rgba, out_path):
    out = Image.open(out_path)
    out.load()
    b = np.asarray(out.convert('RGBA').resize(src_rgba.size, Image.BOX), np.float32)
    a = np.asarray(src_rgba, np.float32)
    d = np.abs(b[..., :3] - a[..., :3]).mean(2)
    m = a[..., 3] > 200
    h, w = d.shape
    k = 8
    H, W = h // k * k, w // k * k
    if not H or not W:
        return 0.0
    d = d[:H, :W].reshape(H // k, k, W // k, k).swapaxes(1, 2).reshape(H // k, W // k, -1)
    m = m[:H, :W].reshape(H // k, k, W // k, k).swapaxes(1, 2).reshape(H // k, W // k, -1)
    cnt = m.sum(2)
    s = (d * m).sum(2)
    ok = cnt >= 16
    return float((s[ok] / cnt[ok]).max()) if ok.any() else 0.0


def gpu(job, items):
    for sub in ('in', 'a', 'b', 'out'):
        (job / sub).mkdir(exist_ok=True)
    ready, errors = [], []
    for it in items:
        try:
            recipe.prepare_rgb(recipe.load_rgba(job / 'src' / f"{it['i']}{it['ext']}"), job / 'in' / f"{it['i']}.png")
            ready.append(it)
        except Exception as exc:
            errors.append({'rel': it['rel'], 'ok': False, 'msg': 'ERR ' + str(exc)[:300]})
    if ready:
        for model, sub in (('realesrgan-x4plus-anime', 'a'), ('realesrgan-x4plus', 'b')):
            r = subprocess.run([str(ESR), '-i', str(job / 'in'), '-o', str(job / sub), '-n', model, '-s', '4',
                                '-f', 'png', '-m', str(MODELS)], capture_output=True, cwd=str(ESR.parent))
            if r.returncode:
                tail = (r.stdout + r.stderr)[-800:].decode('utf-8', 'replace')
                return [], errors + [{'rel': it['rel'], 'ok': False, 'msg': f'ERR {model} exit {r.returncode}: {tail}'}
                                     for it in ready]
    return ready, errors


def finish_one(job, it):
    t0 = time.perf_counter()
    i, rel = it['i'], it['rel']
    try:
        source = job / 'src' / f"{i}{it['ext']}"
        src = recipe.load_rgba(source)
        factor = factor_for(max(src.size))
        grain = 0.15 if rel.startswith('Battlemaps Classic') else 0.6
        tmp = job / 'out' / f'{i}.webp.tmp'
        cycle = recipe.refs2_cycle(rel, src.size)
        finish_options = {'cycle_rgb': True} if cycle else {}
        size = recipe.finish(src, job / 'a' / f'{i}.png', job / 'b' / f'{i}.png', str(tmp), factor,
                             wa=0.6, grain_k=grain, **finish_options)
        ok, msg, da, dc = recipe.verify(src, str(tmp), factor)
        if not (math.isfinite(da) and math.isfinite(dc)):
            raise ValueError('non-finite verification metrics')
        bm = block_max(src, str(tmp))
        if bm > BLOCK_LIMIT:
            ok, msg = False, msg + f' blockMax={bm:.1f}'
        with source.open('rb') as s:
            sha = hashlib.file_digest(s, 'sha256').hexdigest()
        os.replace(tmp, job / 'out' / f'{i}.webp')
        row = {'i': i, 'rel': rel, 'ok': bool(ok), 'factor': factor, 'src': list(src.size), 'out': list(size),
                'srcSha256': sha, 'alphaD': round(da, 2), 'colourD': round(dc, 2), 'blockMax': round(bm, 1),
                'bytes': (job / 'out' / f'{i}.webp').stat().st_size, 'sec': round(time.perf_counter() - t0, 2),
                'msg': msg, 'animeWeight': 0.6, 'grain': grain, 'host': 'mac'}
        if cycle:
            row['recipe'] = 'cycle_rgb'
        return row
    except Exception as exc:
        return {'i': i, 'rel': rel, 'ok': False, 'msg': 'ERR ' + str(exc)[:300], 'host': 'mac'}


def finish_job(job, ready, errors, cpu):
    rows = list(errors) + list(cpu.map(finish_one, [job] * len(ready), ready))
    with (job / 'results.jsonl').open('w', encoding='utf-8') as f:
        for r in rows:
            f.write(json.dumps(r, ensure_ascii=False) + '\n')
    for sub in ('in', 'a', 'b'):
        shutil.rmtree(job / sub, ignore_errors=True)
    (job / 'DONE').write_text('done')
    log(f'{job.name}: {sum(r["ok"] for r in rows)}/{len(rows)} ok')


def next_job():
    for job in sorted(JOBS.iterdir()) if JOBS.exists() else []:
        if (job / 'READY').exists() and not (job / 'TAKEN').exists():
            (job / 'TAKEN').write_text('taken')
            return job
    return None


def cleanup():
    for job in JOBS.iterdir() if JOBS.exists() else []:
        if (job / 'FETCHED').exists():
            shutil.rmtree(job, ignore_errors=True)


def main():
    workers = int(sys.argv[1]) if len(sys.argv) > 1 else 6
    JOBS.mkdir(parents=True, exist_ok=True)
    log(f'worker start pid={os.getpid()} workers={workers}')
    with ThreadPoolExecutor(1) as gpu_pool, ThreadPoolExecutor(1) as fin_pool, ProcessPoolExecutor(workers) as cpu:
        fin = None
        while not (WORK / 'upscale' / 'STOP').exists():
            cleanup()
            job = next_job()
            if not job:
                time.sleep(3)
                continue
            try:
                items = json.loads((job / 'job.json').read_text(encoding='utf-8'))
                ready, errors = gpu_pool.submit(gpu, job, items).result()
                if fin:
                    fin.result()
                fin = fin_pool.submit(finish_job, job, ready, errors, cpu)
            except Exception:
                log(f'{job.name}: ' + traceback.format_exc()[-1500:])
        if fin:
            fin.result()
    (WORK / 'upscale' / 'STOP').unlink(missing_ok=True)  # the stop request is consumed
    log('worker stop')


if __name__ == '__main__':
    main()
