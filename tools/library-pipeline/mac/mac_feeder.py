"""PC side of the two-host upscale: sends reference files to the Mac worker (~/MapWork/upscale) and takes results.

The Mac takes files from the END of lists/all.txt (the PC pipeline goes from the start), skipping everything already
ok in upscaled/manifest.jsonl (PC) or upscaled/manifest-mac.jsonl (Mac). Stops when the next file is already done by
the PC (the two ends met). Originals are only read. For each result: the source SHA-256 computed here must equal the
one the Mac computed, the WebP must open with the exact upscaled size; then it is written to upscaled/<rel>.webp via a
temp file and recorded in manifest-mac.jsonl (failures also in failed-mac.jsonl).

usage: py -3.13 -B mac_feeder.py [--job 60] [--ahead 2] [--limit N]
"""
import argparse, hashlib, io, json, os, subprocess, sys, tarfile, time
from datetime import datetime, timezone
from pathlib import Path

from PIL import Image

LIB = Path(r'H:\MapLibrary')
REFS = Path(r'C:\Users\Leshe\Pictures\refs')
MAC = 'user@192.168.31.25'
SSH = ['ssh', '-o', 'BatchMode=yes', '-o', 'ServerAliveInterval=30', MAC]
JOBS = 'MapWork/upscale/jobs'
MAN_PC = LIB / 'upscaled/manifest.jsonl'
MAN_MAC = LIB / 'upscaled/manifest-mac.jsonl'
FAIL_MAC = LIB / 'upscaled/failed-mac.jsonl'
LOCK = LIB / 'work/mac_feeder.lock'


def key(rel):
    return os.path.normcase(os.path.normpath(rel))


def ok_set(path):
    done = set()
    if path.exists():
        for line in path.open(encoding='utf-8-sig'):
            try:
                r = json.loads(line)
            except ValueError:
                continue
            if isinstance(r, dict) and r.get('ok') is True:
                done.add(key(r['rel']))
    return done


def append(path, row):
    with path.open('a', encoding='utf-8') as f:
        f.write(json.dumps(row, ensure_ascii=False) + '\n')
        f.flush()
        os.fsync(f.fileno())


def log(msg):
    print(time.strftime('%H:%M:%S ') + msg, flush=True)


def ssh(cmd, **kw):
    # A Windows ssh sometimes never returns after the remote command ended (13.11: 16 min on `jobs.sh done`).
    kw.setdefault('timeout', 600)
    # Without input the console stdin is passed to ssh, which then may wait on it: give it an empty stdin.
    if 'input' not in kw:
        kw.setdefault('stdin', subprocess.DEVNULL)
    return subprocess.run(SSH + [cmd], check=True, **kw)


def upload(job_id, rels):
    buf = io.BytesIO()
    items = []
    with tarfile.open(fileobj=buf, mode='w') as tar:
        for i, rel in enumerate(rels):
            ext = Path(rel).suffix.lower()
            tar.add(str(REFS / rel), arcname=f'src/{i}{ext}')
            items.append({'i': i, 'rel': rel, 'ext': ext})
        data = json.dumps(items, ensure_ascii=False).encode('utf-8')
        info = tarfile.TarInfo('job.json')
        info.size = len(data)
        tar.addfile(info, io.BytesIO(data))
    ssh(f'mkdir -p {JOBS}/{job_id} && tar -xf - -C {JOBS}/{job_id} && touch {JOBS}/{job_id}/READY',
        input=buf.getvalue(), stdout=subprocess.DEVNULL)


def _jobs(kind):
    r = subprocess.run(SSH + [f'MapWork/upscale/jobs.sh {kind}'], capture_output=True, text=True, check=True, timeout=120,
                       stdin=subprocess.DEVNULL)
    return sorted(x for x in r.stdout.split() if x)


def done_jobs():
    return _jobs('done')


def pending_jobs():
    return _jobs('pending')


def fetch(job_id):
    r = subprocess.run(SSH + [f'tar -cf - -C {JOBS}/{job_id} out results.jsonl'], capture_output=True, check=True, timeout=900,
                       stdin=subprocess.DEVNULL)
    files, rows = {}, []
    with tarfile.open(fileobj=io.BytesIO(r.stdout), mode='r:') as tar:
        for m in tar.getmembers():
            if not m.isfile():
                continue
            data = tar.extractfile(m).read()
            if m.name == 'results.jsonl':
                rows = [json.loads(x) for x in data.decode('utf-8').splitlines() if x.strip()]
            elif m.name.startswith('out/') and m.name.endswith('.webp'):
                files[int(Path(m.name).stem)] = data
    ok = bad = 0
    for row in rows:
        rel = row['rel']
        row = dict(row, completedAt=datetime.now(timezone.utc).isoformat(), attempt=1)
        i = row.pop('i', None)
        if row.get('ok'):
            try:
                with (REFS / rel).open('rb') as s:
                    sha = hashlib.file_digest(s, 'sha256').hexdigest()
                if sha != row['srcSha256']:
                    raise ValueError('source SHA mismatch')
                data = files[i]
                im = Image.open(io.BytesIO(data))
                if im.size != (row['src'][0] * row['factor'], row['src'][1] * row['factor']) or im.mode not in ('RGBA', 'RGB'):
                    raise ValueError(f'bad output {im.size} {im.mode}')
                target = LIB / 'upscaled' / Path(rel).with_suffix('.webp')
                target.parent.mkdir(parents=True, exist_ok=True)
                tmp = target.with_name(target.name + '.mac.tmp')
                tmp.write_bytes(data)
                os.replace(tmp, target)
            except Exception as exc:
                row.update(ok=False, msg='PC check: ' + str(exc)[:200])
        append(MAN_MAC, row)
        if row['ok']:
            ok += 1
        else:
            bad += 1
            append(FAIL_MAC, row)
    ssh(f'touch {JOBS}/{job_id}/FETCHED')
    return ok, bad


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--job', type=int, default=60)
    p.add_argument('--ahead', type=int, default=2)
    p.add_argument('--limit', type=int)
    a = p.parse_args()
    import msvcrt
    LOCK.parent.mkdir(parents=True, exist_ok=True)
    lock = LOCK.open('a+b')
    lock.seek(0)
    msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)

    rels = [l.strip() for l in (LIB / 'lists/all.txt').open(encoding='utf-8-sig') if l.strip()]
    done = ok_set(MAN_PC) | ok_set(MAN_MAC)
    queue = [r for r in reversed(rels) if key(r) not in done and (REFS / r).is_file()]
    if a.limit is not None:
        queue = queue[:a.limit]
    log(f'mac queue {len(queue)}')
    in_flight = set(pending_jobs())
    total_ok = total_bad = 0
    t0 = time.time()
    while True:
        try:
            queue, total_ok, total_bad = step(a, queue, in_flight, total_ok, total_bad, t0)
        except (subprocess.CalledProcessError, subprocess.TimeoutExpired, OSError, tarfile.TarError) as exc:
            log(f'retry later: {exc!r}'[:300])
            time.sleep(30)
            continue
        if not queue and not in_flight:
            break
        time.sleep(10)
    log(f'finished: ok {total_ok} bad {total_bad}')


def step(a, queue, in_flight, total_ok, total_bad, t0):
    if True:
        for job_id in done_jobs():
            ok, bad = fetch(job_id)
            total_ok += ok
            total_bad += bad
            in_flight.discard(job_id)
            rate = (total_ok + total_bad) / max(time.time() - t0, 1) * 3600
            log(f'{job_id}: ok {ok} bad {bad}; total ok {total_ok} bad {total_bad}; {rate:.0f} files/h')
        while queue and len(in_flight) < a.ahead:
            pc_done = ok_set(MAN_PC)
            batch = []
            while queue and len(batch) < a.job:
                r = queue.pop(0)
                if key(r) in pc_done:
                    log('reached files the PC already did; stopping intake')
                    queue = []
                    break
                if not (REFS / r).is_file():  # removed after the queue was built (13.11: exact duplicates deleted)
                    log(f'skip missing source: {r}')
                    continue
                batch.append(r)
            if not batch:
                break
            job_id = f'j{time.time_ns() // 1_000_000}'
            try:
                upload(job_id, batch)
            except Exception:
                queue[:0] = batch  # nothing was marked READY; send these again later
                raise
            in_flight.add(job_id)
            log(f'{job_id}: sent {len(batch)}; left {len(queue)}')
    return queue, total_ok, total_bad


if __name__ == '__main__':
    sys.exit(main())
