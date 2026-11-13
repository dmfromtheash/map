"""Retry unresolved colourD > 10 OR alphaD > 8 with anime weight .8, grain <= .3.

Usage: rerun_outliers.py <refs_root> [--limit N] [--workers N] [--batch N]
Never reprocess a rel that has any successful manifest record.
"""
import argparse
import json
from pathlib import Path
import sys
import time
sys.dont_write_bytecode = True
from run_upscale2 import Config, LIB, WriterLock, done_set, key, options, records, relative_path, run


def select_outliers(manifest):
    latest, done = {}, done_set(manifest)
    for row in records(manifest):
        # A later ERR without metrics must not hide an earlier measured outlier.
        if row.get('ok') is False and ('colourD' in row or 'alphaD' in row):
            latest[key(row['rel'])] = row
    result = []
    for identity, row in latest.items():
        if identity in done or row.get('ok') is not False:
            continue
        try:
            outlier = float(row.get('colourD', 0)) > 10 or float(row.get('alphaD', 0)) > 8
        except (ValueError, TypeError):
            continue
        if outlier:
            relative_path(row['rel'])
            result.append(row['rel'])
    return result


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('refs_root', type=Path)
    p.add_argument('--limit', type=int)
    p.add_argument('--workers', type=int, default=6)
    p.add_argument('--batch', type=int, default=120)
    p.add_argument('--library-root', type=Path, default=LIB)
    args = options(p, argv)
    config = Config(args.library_root, args.workers, args.batch, False, 0.8, 0.3, rerun=True)
    with WriterLock(config.library):
        candidates = select_outliers(config.manifest)
        missing = [rel for rel in candidates if not (args.refs_root / rel).is_file()]
        todo = [rel for rel in candidates if (args.refs_root / rel).is_file()]
        if args.limit is not None:
            todo = todo[:args.limit]
        print(f'pipeline=v2 startedAt={time.time():.6f}', flush=True)
        print(f'outliers {len(candidates)}, missing sources {len(missing)}, todo {len(todo)}', flush=True)
        result = run(args.refs_root, todo, config)
    print(json.dumps(result), flush=True)
    return 1 if result['bad'] or missing else 0


if __name__ == '__main__':
    raise SystemExit(main())
