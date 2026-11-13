"""Read-only progress, including a last-hour estimate for the timestamp-free legacy log."""
import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import re
import sys
import time


def load_state(path):
    latest, successes, completed, malformed = {}, set(), {}, 0
    if not path.exists():
        return latest, successes, completed, malformed
    with path.open(encoding='utf-8-sig') as stream:
        for line in stream:
            try:
                row = json.loads(line)
                rel = os.path.normcase(os.path.normpath(row['rel']))
                latest[rel] = row
                if row.get('ok') is True:
                    successes.add(rel)
                    if row.get('completedAt') and rel not in completed:
                        completed[rel] = datetime.fromisoformat(row['completedAt']).timestamp()
            except (ValueError, KeyError, TypeError):
                malformed += 1
    return latest, successes, completed, malformed


def legacy_hourly(log, now):
    """Interpolate rounded cumulative counters, anchoring the last line to log mtime.

    Includes idle time since the last write. This is explicitly an estimate, not
    individual completion timestamps. Reset points start a new run.
    """
    if not log.exists():
        return None
    points = [(0.0, 0)]
    anchor = log.stat().st_mtime
    with log.open(encoding='utf-8-sig') as stream:
        for line in stream:
            marker = re.match(r'^pipeline=v2 startedAt=([\d.]+)', line)
            if marker:
                # v2 completions have exact timestamps in the manifest. Do not
                # count its stdout counters again. For an appended mixed log,
                # approximate the old endpoint by the time of this transition.
                anchor = float(marker[1])
                break
            if line.startswith('todo '):
                points = [(0.0, 0)]
            match = re.search(r'^(\d+)/(\d+) bad=\d+ elapsed=([\d.]+)h', line)
            if match:
                count, elapsed = int(match[1]), float(match[3]) * 3600
                if count < points[-1][1] or elapsed < points[-1][0]:
                    points = [(0.0, 0)]
                points.append((elapsed, count))
    if len(points) == 1:
        return None
    last_time, last_count = points[-1]
    cutoff = last_time + max(0, now - anchor) - 3600
    if cutoff >= last_time:
        return 0.0
    if cutoff <= 0:
        return float(last_count)
    for (t0, n0), (t1, n1) in zip(points, points[1:]):
        if t0 <= cutoff <= t1 and t1 > t0:
            return last_count - (n0 + (n1 - n0) * (cutoff - t0) / (t1 - t0))
    return None


def disk_bytes(root):
    total, errors = 0, 0
    pending = [root]
    while pending:
        folder = pending.pop()
        try:
            with os.scandir(folder) as entries:
                for entry in entries:
                    try:
                        if entry.is_dir(follow_symlinks=False):
                            pending.append(entry.path)
                        elif entry.is_file(follow_symlinks=False):
                            total += entry.stat(follow_symlinks=False).st_size
                    except OSError:
                        errors += 1
        except FileNotFoundError:
            continue
        except OSError:
            errors += 1
    return total, errors


def snapshot(library, list_path, now=None):
    now = time.time() if now is None else now
    with list_path.open(encoding='utf-8-sig') as stream:
        wanted = {os.path.normcase(os.path.normpath(line.strip())) for line in stream if line.strip()}
    latest, successes, completed, malformed = load_state(library / 'upscaled/manifest.jsonl')
    done = len(wanted & successes)
    bad = sum(rel in wanted and rel not in successes and row.get('ok') is False for rel, row in latest.items())
    timestamped = sum(now - 3600 <= at <= now for rel, at in completed.items() if rel in wanted)
    estimate = legacy_hourly(library / 'upscale.log', now)
    # When switching to v2, the legacy mtime expires naturally within an hour.
    rate = timestamped + (estimate or 0)
    seconds = (len(wanted) - done) / rate * 3600 if rate > 0 else None
    size, disk_errors = disk_bytes(library / 'upscaled')
    return {'done': done, 'total': len(wanted), 'filesPerHour': round(rate, 2),
            'rateEstimated': estimate is not None and estimate > 0,
            'etaHours': round(seconds / 3600, 2) if seconds is not None else None,
            'bad': bad, 'diskBytes': size, 'diskErrors': disk_errors, 'malformedRecords': malformed,
            'asOf': datetime.fromtimestamp(now, timezone.utc).isoformat()}


def main():
    sys.stdout.reconfigure(encoding='utf-8')
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--library-root', type=Path, default=Path(r'H:\MapLibrary'))
    p.add_argument('--list', type=Path)
    p.add_argument('--json', action='store_true')
    args = p.parse_args()
    state = snapshot(args.library_root, args.list or args.library_root / 'lists/all.txt')
    if args.json:
        print(json.dumps(state))
    else:
        eta = f"{state['etaHours']:.1f} ч" if state['etaHours'] is not None else 'нет данных'
        qualifier = ' (оценка по старому журналу)' if state['rateEstimated'] else ''
        print(f"Готово: {state['done']}/{state['total']}")
        print(f"За последний час: {state['filesPerHour']:.1f} файлов/ч{qualifier}")
        print(f"Осталось: {eta}; ошибок: {state['bad']}")
        print(f"upscaled/: {state['diskBytes']} байт ({state['diskBytes']/1024**3:.3f} ГиБ)")
        print(f"Неполных/невалидных записей: {state['malformedRecords']}; ошибок чтения диска: {state['diskErrors']}")


if __name__ == '__main__':
    main()
