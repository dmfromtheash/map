"""Resumable two-model upscale, with one GPU producer and a bounded CPU pool.

Same positional arguments/flags as run_upscale.py; adds --workers and --batch.
Defaults target H:\\MapLibrary. Do not run concurrently with the legacy writer.
--library-root redirects ALL writes for an isolated run; models/finish stay read-only.
"""
import argparse
from concurrent.futures import ProcessPoolExecutor, ThreadPoolExecutor, as_completed
from concurrent.futures.process import BrokenProcessPool
from dataclasses import dataclass
from datetime import datetime, timezone
import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time

# Import the existing recipe without creating __pycache__ outside this folder.
sys.dont_write_bytecode = True
LIB = Path(r'H:\MapLibrary')
ESR = LIB / 'tools/realesrgan/realesrgan-ncnn-vulkan.exe'
# Recipe: a finish_asset.py next to this file (repository tools/library-pipeline/upscale) wins; the deployed copy in
# H:\MapLibrary\sol\sol1 has none there and uses H:\MapLibrary\pipeline\finish_asset.py as before.
_RECIPE = Path(__file__).resolve().parent / 'finish_asset.py'
if not _RECIPE.is_file():
    _RECIPE = LIB / 'pipeline/finish_asset.py'
spec = importlib.util.spec_from_file_location('finish_asset', _RECIPE)
recipe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(recipe)


@dataclass(frozen=True)
class Config:
    library: Path = LIB
    workers: int = 6
    batch: int = 120
    delete: bool = False
    anime_weight: float = 0.6
    grain: float | None = None
    rerun: bool = False

    @property
    def manifest(self):
        return self.library / 'upscaled/manifest.jsonl'

    @property
    def failed(self):
        return self.library / 'upscaled/failed.jsonl'


def factor_for(side):
    return 4 if side <= 512 else 2


def key(rel):
    return os.path.normcase(os.path.normpath(rel))


def relative_path(rel):
    path = Path(rel)
    if not rel or path.is_absolute() or path.drive or '..' in path.parts:
        raise ValueError(f'unsafe relative path: {rel!r}')
    return path


def records(path):
    """Ignore interrupted JSONL records, as does the original pipeline."""
    if path.exists():
        with path.open(encoding='utf-8-sig') as stream:
            for line in stream:
                try:
                    row = json.loads(line)
                    if isinstance(row, dict) and isinstance(row.get('rel'), str):
                        yield row
                except (ValueError, UnicodeError):
                    continue


def done_set(path):
    return {key(row['rel']) for row in records(path) if row.get('ok') is True}


def append_record(path, row):
    path.parent.mkdir(parents=True, exist_ok=True)
    # Repair a partial last line before appending, so the next record stays readable.
    with path.open('a+b') as stream:
        stream.seek(0, os.SEEK_END)
        if stream.tell():
            stream.seek(-1, os.SEEK_END)
            if stream.read(1) != b'\n':
                stream.write(b'\n')
        stream.write((json.dumps(row, ensure_ascii=False, allow_nan=False) + '\n').encode('utf-8'))
        stream.flush()
        os.fsync(stream.fileno())


def error_record(rel, exc):
    return {'rel': rel, 'ok': False, 'msg': 'ERR ' + str(exc)[:400]}


MIN_GPU_SIDE = int(os.environ.get('MAP_MIN_GPU_SIDE', '16'))


def append_line(path, line):
    with path.open('a', encoding='utf-8') as stream:
        stream.write(line + '\n')
        stream.flush()
        os.fsync(stream.fileno())


def prepare_gpu(root, rels, bdir):
    """Only this single producer thread ever launches a GPU command."""
    bdir = Path(bdir)
    for sub in ('in', 'a', 'b'):
        (bdir / sub).mkdir(parents=True, exist_ok=True)
    ready, errors = [], []
    for i, rel in enumerate(rels):
        try:
            src = recipe.load_rgba(Path(root) / relative_path(rel))
            # 13.11: a picture 8 px wide hangs the PC's NVIDIA GPU inside Real-ESRGAN (nvlddmkm 153 / LiveKernelEvent 141,
            # reproduced 2/2; 14 px passed). The Mac upscales such pictures correctly, so the PC leaves them unrecorded
            # for the Mac node, which works through the list from its end.
            if min(src.size) < MIN_GPU_SIDE:
                # The Mac feeder sends lists/mac_priority.txt first (owner 13.11: «то, что не сделает ПК, сразу на Мак»).
                append_line(LIB / 'lists/mac_priority.txt', rel)
                continue
            recipe.prepare_rgb(src, bdir / 'in' / f'{i}.png')
            ready.append((i, rel))
        except Exception as exc:
            errors.append(error_record(rel, exc))
    if ready:
        try:
            for model, sub in (('realesrgan-x4plus-anime', 'a'), ('realesrgan-x4plus', 'b')):
                log = bdir / f'{sub}.log'
                with log.open('wb') as stream:
                    result = subprocess.run(
                        [str(ESR), '-i', str(bdir / 'in'), '-o', str(bdir / sub),
                         '-n', model, '-s', '4', '-f', 'png'],
                        stdout=stream, stderr=subprocess.STDOUT, check=False,
                        cwd=ESR.parent)
                if result.returncode:
                    detail = log.read_bytes()[-1500:].decode('utf-8', errors='replace')
                    raise RuntimeError(f'{model}: exit {result.returncode}; {detail}')
                # Owner 13.11: the laptop GPU reached 86 °C and reset twice (nvlddmkm 153) - it rests after every model
                # run. Pixels do not change; only the pace does.
                time.sleep(float(os.environ.get('MAP_GPU_COOLDOWN', '0')))
        except Exception as exc:
            errors.extend(error_record(rel, exc) for _, rel in ready)
            ready = []
    return ready, errors


def finish_one(root, rel, i, bdir, config):
    t0 = time.perf_counter()
    temp_path = None
    try:
        source = Path(root) / relative_path(rel)
        src = recipe.load_rgba(source)
        factor = factor_for(max(src.size))
        target = config.library / 'upscaled' / relative_path(rel).with_suffix('.webp')
        target.parent.mkdir(parents=True, exist_ok=True)
        # An interrupted encode must not leave a half-written final output.
        fd, temp_path = tempfile.mkstemp(prefix=target.stem + '-', suffix='.webp.tmp', dir=target.parent)
        os.close(fd)
        normal_grain = 0.15 if rel.startswith('Battlemaps Classic') else 0.6
        grain = normal_grain if config.grain is None else min(config.grain, normal_grain)
        size = recipe.finish(src, Path(bdir) / 'a' / f'{i}.png', Path(bdir) / 'b' / f'{i}.png',
                             temp_path, factor, wa=config.anime_weight, grain_k=grain)
        ok, msg, da, dc = recipe.verify(src, temp_path, factor)
        if not math.isfinite(da) or not math.isfinite(dc):
            raise ValueError('non-finite verification metrics')
        with source.open('rb') as stream:
            src_sha = hashlib.file_digest(stream, 'sha256').hexdigest()
        size_bytes = os.path.getsize(temp_path)
        row = {'rel': rel, 'ok': bool(ok), 'factor': factor, 'src': list(src.size), 'out': list(size),
                'srcSha256': src_sha, 'alphaD': round(da, 2), 'colourD': round(dc, 2),
                'bytes': size_bytes, 'sec': round(time.perf_counter() - t0, 2), 'msg': msg,
                'animeWeight': config.anime_weight, 'grain': grain}
        if config.rerun and target.exists() and not ok:
            # Compare against the actual installed output, not a rejected attempt
            # in the manifest. Failed reruns must not silently degrade this file.
            old_ok, old_msg, old_da, old_dc = recipe.verify(src, target, factor)
            if not (da < old_da and dc < old_dc):
                candidate = target.with_suffix('.rerun.webp')
                os.replace(temp_path, candidate)
                row['rerunCandidate'] = {
                    'path': candidate.relative_to(config.library / 'upscaled').as_posix(),
                    'ok': bool(ok), 'alphaD': row['alphaD'], 'colourD': row['colourD'],
                    'bytes': size_bytes, 'msg': msg,
                    'animeWeight': config.anime_weight, 'grain': grain}
                # Top-level metrics still describe the output at the usual path.
                row.update(ok=bool(old_ok), alphaD=round(old_da, 2), colourD=round(old_dc, 2),
                           bytes=target.stat().st_size, msg='rerun retained previous: ' + old_msg)
                row.pop('animeWeight')
                row.pop('grain')
                return row
        os.replace(temp_path, target)
        return row
    except Exception as exc:
        return error_record(rel, exc)
    finally:
        if temp_path and os.path.exists(temp_path):
            os.remove(temp_path)


def persist(row, attempt, config, root):
    row = dict(row, attempt=attempt, completedAt=datetime.now(timezone.utc).isoformat())
    append_record(config.manifest, row)
    if not row['ok'] and (attempt == 2 or not str(row.get('msg', '')).startswith('ERR')):
        append_record(config.failed, row)
    if row['ok'] and config.delete:
        # A durable success must exist before an irreplaceable source is removed.
        try:
            (Path(root) / relative_path(row['rel'])).unlink()
        except OSError as exc:
            print(f"source retained: {row['rel']}: {exc}", flush=True)
        else:
            # Keep a single success record for consumers that do not deduplicate.
            print(f"source deleted: {row['rel']}", flush=True)
    return row


class WriterLock:
    """A process lock shared by both new scripts (automatically released on crash)."""
    def __init__(self, library):
        self.path = library / 'work/upscale2.lock'

    def __enter__(self):
        import msvcrt
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.stream = self.path.open('a+b')
        if self.stream.seek(0, os.SEEK_END) == 0:
            self.stream.write(b'0')
            self.stream.flush()
        self.stream.seek(0)
        try:
            msvcrt.locking(self.stream.fileno(), msvcrt.LK_NBLCK, 1)
        except OSError:
            self.stream.close()
            raise RuntimeError('another upscale2/rerun_outliers writer is running') from None
        return self

    def __exit__(self, *args):
        self.stream.close()


def run(root, rels, config):
    """At most one next GPU batch overlaps the current CPU batch; retries use the same GPU queue."""
    started = time.perf_counter()
    if not rels:
        return {'files': 0, 'ok': 0, 'bad': 0, 'seconds': 0.0}
    work = config.library / 'work'
    work.mkdir(parents=True, exist_ok=True)
    counts = {'files': 0, 'ok': 0, 'bad': 0}
    batches = [rels[i:i + config.batch] for i in range(0, len(rels), config.batch)]
    with tempfile.TemporaryDirectory(prefix='upscale2-', dir=work) as session:
        # Exit pools before deleting the session, including when Ctrl+C is pressed.
        with ThreadPoolExecutor(1) as gpu, ProcessPoolExecutor(config.workers) as cpu:
            mac = config.library / 'upscaled/manifest-mac.jsonl'

            def schedule(index):
                bdir = Path(session) / f'b{index:06d}'
                # The Mac node works from the end of the list (13.11): files it finished meanwhile are not done again.
                batch = batches[index]
                if mac.exists():
                    mac_done = done_set(mac)
                    batch = [rel for rel in batch if key(rel) not in mac_done]
                return bdir, gpu.submit(prepare_gpu, root, batch, bdir)

            bdir, pending = schedule(0)
            for index in range(len(batches)):
                ready, errors = pending.result()
                # Launch k+1 BEFORE waiting for any CPU work for k.
                following = schedule(index + 1) if index + 1 < len(batches) else None
                futures = {cpu.submit(finish_one, root, rel, i, bdir, config): rel for i, rel in ready}
                failures = []
                for row in errors:
                    persist(row, 1, config, root)
                    failures.append(row['rel'])
                for future in as_completed(futures):
                    try:
                        row = future.result()
                    except BrokenProcessPool:
                        raise
                    except Exception as exc:
                        row = error_record(futures[future], exc)
                    persist(row, 1, config, root)
                    if row['ok']:
                        counts['ok'] += 1
                    elif str(row.get('msg', '')).startswith('ERR'):
                        failures.append(row['rel'])
                    else:
                        counts['bad'] += 1
                # Retry transient ERRs ALONE, once; measured rejections are final.
                for retry_index, rel in enumerate(failures):
                    retry_dir = Path(session) / f'r{index:06d}-{retry_index:06d}'
                    retry_ready, retry_errors = gpu.submit(prepare_gpu, root, [rel], retry_dir).result()
                    if retry_errors:
                        row = retry_errors[0]
                    else:
                        try:
                            row = cpu.submit(finish_one, root, rel, retry_ready[0][0], retry_dir, config).result()
                        except BrokenProcessPool:
                            raise
                        except Exception as exc:
                            row = error_record(rel, exc)
                    persist(row, 2, config, root)
                    counts['ok' if row['ok'] else 'bad'] += 1
                    shutil.rmtree(retry_dir)
                shutil.rmtree(bdir)
                counts['files'] += len(batches[index])
                elapsed = time.perf_counter() - started
                eta = elapsed / counts['files'] * (len(rels) - counts['files']) / 3600
                print(f"{counts['files']}/{len(rels)} bad={counts['bad']} elapsed={elapsed/3600:.2f}h eta={eta:.1f}h", flush=True)
                if following:
                    bdir, pending = following
    return dict(counts, seconds=round(time.perf_counter() - started, 4))


def parser(description=__doc__):
    p = argparse.ArgumentParser(description=description)
    p.add_argument('refs_root', type=Path)
    p.add_argument('list', type=Path)
    p.add_argument('--delete-source', action='store_true')
    p.add_argument('--limit', type=int)
    p.add_argument('--workers', type=int, default=6, help='CPU finish processes (default: 6)')
    p.add_argument('--batch', type=int, default=120, help='files per GPU batch (default: 120)')
    p.add_argument('--library-root', type=Path, default=LIB, help='output/work root, for isolated runs')
    return p


def options(p, argv=None):
    args = p.parse_args(argv)
    if args.workers < 1 or args.batch < 1 or (args.limit is not None and args.limit < 0):
        p.error('--workers/--batch must be positive; --limit must be nonnegative')
    args.library_root = args.library_root.resolve()
    args.refs_root = args.refs_root.resolve()
    if not args.refs_root.is_dir():
        p.error('refs_root is not a directory')
    return args


def select_rels(args, done):
    result, seen = [], set()
    with args.list.open(encoding='utf-8-sig') as stream:
        for line in stream:
            rel = line.strip()
            if not rel:
                continue
            relative_path(rel)
            identity = key(rel)
            if identity not in seen and identity not in done:
                seen.add(identity)
                if (args.refs_root / rel).is_file():
                    result.append(rel)
    return result[:args.limit] if args.limit is not None else result


def main(argv=None):
    args = options(parser(), argv)
    config = Config(args.library_root, args.workers, args.batch, args.delete_source)
    with WriterLock(config.library):
        done = done_set(config.manifest)
        mac = config.library / 'upscaled/manifest-mac.jsonl'
        if mac.exists():
            done |= done_set(mac)
        todo = select_rels(args, done)
        print(f'pipeline=v2 startedAt={time.time():.6f}', flush=True)
        print(f'todo {len(todo)} (done {len(done)})', flush=True)
        result = run(args.refs_root, todo, config)
    print(json.dumps(result), flush=True)
    return 1 if result['bad'] else 0


if __name__ == '__main__':
    raise SystemExit(main())

