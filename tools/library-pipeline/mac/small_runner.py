"""PC side of the «сверка цвета» rerun (DEC-108): small shop previews (long side <= 256 px) that are not ok in either
upscale journal go to the Mac in jobs of 60, are processed there by ~/MapWork/upscale/small_rerun.py (cycle_rgb) and are
taken back by the feeder's own checked fetch (source SHA, exact size) into manifest-mac.jsonl with "recipe": "cycle_rgb".
The live Mac worker must be stopped (it would otherwise take READY jobs with the normal recipe).

usage: py -3.13 -B small_runner.py [--limit N] [--dry-run]
"""
import argparse, json, sys, time
from pathlib import Path

from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent))
import mac_feeder as f  # noqa: E402  same LIB/REFS/ssh/upload/fetch as the feeder

LIMIT_SIDE = 256


def candidates():
    done = f.ok_set(f.MAN_PC) | f.ok_set(f.MAN_MAC)
    seen, out = set(), []
    for name in ('failed.jsonl', 'failed-mac.jsonl'):
        path = f.LIB / 'upscaled' / name
        if not path.exists():
            continue
        for line in path.open(encoding='utf-8-sig'):
            try:
                rel = json.loads(line)['rel']
            except (ValueError, KeyError):
                continue
            k = f.key(rel)
            if k in done or k in seen or not (f.REFS / rel).is_file():
                continue
            seen.add(k)
            with Image.open(f.REFS / rel) as im:
                if max(im.size) <= LIMIT_SIDE:
                    out.append(rel)
    return out


def run_job(job_id):
    """Runs cycle_rgb on an uploaded job (paths from the home folder) and takes the results back."""
    job = f'~/{f.JOBS}/{job_id}'
    f.ssh(f'source ~/MapWork/env.sh; cd ~/MapWork/upscale && python small_rerun.py {job} > {job}/small.log 2>&1; '
          f'test -f {job}/DONE', timeout=3600)
    return f.fetch(job_id)


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--limit', type=int)
    p.add_argument('--job', type=int, default=60)
    p.add_argument('--dry-run', action='store_true')
    p.add_argument('--resume', nargs='*', default=[], help='already uploaded job ids to process first')
    a = p.parse_args()
    for job_id in a.resume:
        ok, bad = run_job(job_id)
        f.log(f'{job_id} (resumed): cycle_rgb ok {ok} bad {bad}')
    if f.ssh('pgrep -f "^python mac_worker.py" || true', capture_output=True, text=True).stdout.strip():
        sys.exit('the live Mac worker is running: stop it first (touch ~/MapWork/upscale/STOP)')
    rels = candidates()[:a.limit] if a.limit else candidates()
    f.log(f'small previews to redo: {len(rels)}')
    if a.dry_run:
        return
    total_ok = total_bad = 0
    for start in range(0, len(rels), a.job):
        batch = rels[start:start + a.job]
        job_id = f'j{time.time_ns() // 1_000_000}'
        f.upload(job_id, batch)
        ok, bad = run_job(job_id)
        total_ok += ok
        total_bad += bad
        f.log(f'{job_id}: cycle_rgb ok {ok} bad {bad}; total ok {total_ok} bad {total_bad}; left {len(rels) - start - len(batch)}')
    f.log(f'small rerun finished: ok {total_ok} bad {total_bad}')


if __name__ == '__main__':
    main()
