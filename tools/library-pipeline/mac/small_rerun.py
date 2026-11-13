#!/usr/bin/env python3
"""DEC-108: sequential x4 cycle_rgb rerun of a worker job (long side <=256).

Usage: python small_rerun.py JOB
JOB contains job.json [{i, rel, ext}] and src/<i><ext>. The caller selects shop
previews and supplies an idle job; this program does not coordinate with the
live worker. Writes out/<i>.webp, results.jsonl and DONE, including failed rows.
Exit 0 = every row passed; 2 = at least one failed. Inputs are never modified.
sec, as in mac_worker.finish_one, measures finishing/verification only;
gpuSec and totalSec additionally expose both inference and whole-item time.
The finish/prepare/verify implementation is loaded read-only from --recipe.
"""
import argparse
import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time

sys.dont_write_bytecode = True
import numpy as np
from PIL import Image

WORK = Path.home() / 'MapWork'
MODELS = ('realesrgan-x4plus-anime', 'realesrgan-x4plus')
BLOCK_LIMIT = 60.0


def load_recipe(path):
    spec = importlib.util.spec_from_file_location('small_finish_asset', path)
    recipe = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(recipe)
    return recipe


def digest(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def resize_float(x, size):
    if x.ndim == 2:
        return np.asarray(Image.fromarray(np.ascontiguousarray(x, dtype=np.float32))
                          .resize(size, Image.Resampling.LANCZOS), np.float32)
    return np.stack([resize_float(x[..., c], size) for c in range(x.shape[2])], axis=2)


def save_rgba(x, path):
    Image.fromarray(np.round(np.clip(x, 0, 1) * 255).astype(np.uint8)).save(
        path, 'WEBP', lossless=True, quality=80, method=4, exact=True)


def preserve_near_opaque_alpha(src, target):
    alpha = np.asarray(src)[..., 3]
    if 200 < int(alpha.min()) < 255:
        with Image.open(target) as image:
            out = np.asarray(image.convert('RGBA'), np.float32) / 255
        out[..., 3] = np.clip(resize_float(alpha.astype(np.float32) / 255,
                                         (src.width * 4, src.height * 4)), 0, 1)
        save_rgba(out, target)


def cycle_rgb(recipe, src, prepared, a, b, target):
    """Match previews2.apply_recipe('cycle_rgb'), including WebP round trips."""
    size = (src.width * 4, src.height * 4)
    for path in (a, b):
        with Image.open(path) as image:
            image.load()
            if image.size != size or image.mode != 'RGB':
                raise ValueError(f'Model output: expected RGB {size}, got {image.mode} {image.size}')
    recipe.finish(src, a, b, target, 4, wa=.6, grain_k=0)
    out = np.asarray(recipe.load_rgba(target), np.float32) / 255
    with Image.open(prepared) as image:
        p = np.asarray(image.convert('RGB'), np.float32) / 255
    back = np.asarray(Image.fromarray(np.round(out[..., :3] * 255).astype(np.uint8))
                      .resize(src.size, Image.Resampling.BOX), np.float32) / 255
    out[..., :3] += resize_float(p - back, size)
    save_rgba(out, target)
    preserve_near_opaque_alpha(src, target)
    return size


def block_max(src_rgba, out_path):
    # Same arithmetic, alpha mask, block grid and minimum count as mac_worker.
    with Image.open(out_path) as out:
        out.load()
        b = np.asarray(out.convert('RGBA').resize(src_rgba.size, Image.Resampling.BOX), np.float32)
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


def run_model(esr, models, prepared, target, model):
    command = [str(esr), '-i', str(prepared), '-o', str(target), '-n', model,
               '-s', '4', '-f', 'png', '-m', str(models), '-t', '128', '-j', '1:1:1']
    start = time.perf_counter()
    result = subprocess.run(command, capture_output=True, cwd=str(prepared.parent))
    if result.returncode:
        tail = (result.stdout + result.stderr)[-800:].decode('utf-8', 'replace')
        raise RuntimeError(f'{model} exit {result.returncode}: {tail}')
    return time.perf_counter() - start


def finish_one(recipe, job, item, src, prepared, a, b):
    start = time.perf_counter()
    target = prepared.parent / 'cycle.webp'
    size = cycle_rgb(recipe, src, prepared, a, b, target)
    ok, msg, da, dc = recipe.verify(src, target, 4)
    if not (math.isfinite(da) and math.isfinite(dc)):
        raise ValueError('non-finite verification metrics')
    bm = block_max(src, target)
    if not math.isfinite(bm):
        raise ValueError('non-finite block metric')
    if bm > BLOCK_LIMIT:
        ok, msg = False, msg + f' blockMax={bm:.1f}'
    source = job / 'src' / f"{item['i']}{item['ext']}"
    sha = digest(source)
    output = job / 'out' / f"{item['i']}.webp"
    os.replace(target, output)
    return {'i': item['i'], 'rel': item['rel'], 'ok': bool(ok), 'factor': 4,
            'src': list(src.size), 'out': list(size), 'srcSha256': sha,
            'alphaD': round(da, 2), 'colourD': round(dc, 2), 'blockMax': round(bm, 1),
            'bytes': output.stat().st_size, 'sec': round(time.perf_counter() - start, 2),
            'msg': msg, 'animeWeight': .6, 'grain': 0, 'host': 'mac', 'recipe': 'cycle_rgb'}


def read_items(job):
    items = json.loads((job / 'job.json').read_text(encoding='utf-8'))
    if not isinstance(items, list):
        raise ValueError('job.json must be a list')
    seen = set()
    for item in items:
        i = item['i']
        if type(i) is not int or i < 0 or i in seen:
            raise ValueError(f'Duplicate or invalid i: {i}')
        seen.add(i)
        ext = item['ext']
        if not isinstance(ext, str) or not ext.startswith('.') or any(c in ext for c in '/\\\0'):
            raise ValueError(f'Invalid ext: {ext!r}')
        if not isinstance(item['rel'], str):
            raise ValueError('rel must be a string')
        source = job / 'src' / f'{i}{ext}'
        if not source.resolve().is_relative_to((job / 'src').resolve()):
            raise ValueError('Source escapes src/')
    return items


def run_job(job, recipe, esr, models):
    job = Path(job).resolve()
    items = read_items(job)
    # Refuse concurrent invocations of this script; no live-worker markers touched.
    lock = job / '.small_rerun.lock'
    with lock.open('x', encoding='utf-8') as stream:
        stream.write(str(os.getpid()))
    try:
        (job / 'DONE').unlink(missing_ok=True)
        (job / 'out').mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(prefix='.small-rerun-', dir=job) as temporary:
            scratch = Path(temporary)
            results = scratch / 'results.jsonl'
            rows = []
            with results.open('w', encoding='utf-8') as stream:
                for index, item in enumerate(items):
                    started = time.perf_counter()
                    gpu_seconds = 0.0
                    try:
                        src = recipe.load_rgba(job / 'src' / f"{item['i']}{item['ext']}")
                        if max(src.size) > 256:
                            raise ValueError(f'Outside cycle_rgb domain <=256: {src.size}')
                        with tempfile.TemporaryDirectory(prefix='item-', dir=scratch) as folder:
                            work = Path(folder)
                            prepared, a, b = (work / n for n in ('prepared.png', 'a.png', 'b.png'))
                            recipe.prepare_rgb(src, prepared)
                            for model, target in zip(MODELS, (a, b)):
                                gpu_seconds += run_model(esr, models, prepared, target, model)
                            row = finish_one(recipe, job, item, src, prepared, a, b)
                    except Exception as exc:
                        row = {'i': item['i'], 'rel': item['rel'], 'ok': False,
                               'msg': 'ERR ' + str(exc)[:300], 'host': 'mac', 'recipe': 'cycle_rgb'}
                    row.update(gpuSec=round(gpu_seconds, 4),
                               totalSec=round(time.perf_counter() - started, 4))
                    rows.append(row)
                    stream.write(json.dumps(row, ensure_ascii=False, allow_nan=False) + '\n')
                    stream.flush()
                    print(f"{index + 1}/{len(items)} i={item['i']} ok={row['ok']} {row['msg']}", flush=True)
            os.replace(results, job / 'results.jsonl')
        (job / 'DONE').write_text('done', encoding='utf-8')
        return rows
    finally:
        lock.unlink()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('job', type=Path)
    parser.add_argument('--recipe', type=Path, default=WORK / 'upscale/finish_asset.py')
    parser.add_argument('--esr', type=Path, default=WORK / 'esr/realesrgan-ncnn-vulkan')
    parser.add_argument('--models', type=Path, default=WORK / 'esr/models')
    args = parser.parse_args()
    rows = run_job(args.job, load_recipe(args.recipe.expanduser()),
                   args.esr.expanduser().resolve(), args.models.expanduser().resolve())
    return 0 if all(row['ok'] for row in rows) else 2


if __name__ == '__main__':
    raise SystemExit(main())
