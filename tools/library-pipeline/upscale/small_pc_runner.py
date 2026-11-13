"""Sequential PC queue for shop previews <=256 px, DEC-108; uses the normal two-model producer.

No source deletion. Defaults match the existing refs queue. refs2 rels require the Pictures root.
--dry-run reads the local list and image headers only, without locks, GPU or writes.
"""
import argparse
from dataclasses import replace
import json
from pathlib import Path

from PIL import Image
import run_upscale2 as pipeline


def parser():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--refs-root', type=Path, default=Path(r'C:\Users\Leshe\Pictures\refs'))
    p.add_argument('--list', type=Path, default=pipeline.LIB / 'lists/all.txt')
    p.add_argument('--library-root', type=Path, default=pipeline.LIB)
    p.add_argument('--limit', type=int)
    p.add_argument('--workers', type=int, default=6)
    p.add_argument('--batch', type=int, default=60)
    p.add_argument('--dry-run', action='store_true')
    return p


def main(argv=None):
    args = pipeline.options(parser(), argv)
    config = replace(pipeline.Config(args.library_root, args.workers, args.batch), cycle_small=True)

    def select():
        done = pipeline.done_set(config.manifest) | pipeline.done_set(config.library / 'upscaled/manifest-mac.jsonl')
        # Apply the limit after filtering to small shop previews.
        candidates = pipeline.select_rels(replace_args(args, limit=None), done)
        todo = []
        for rel in candidates:
            if not any(part.startswith('_Магазин') for part in rel.replace('/', '\\').split('\\')):
                continue
            with Image.open(args.refs_root / rel) as im:
                if max(im.size) <= 256:
                    todo.append(rel)
        return todo[:args.limit] if args.limit is not None else todo

    if args.dry_run:
        print(json.dumps({'dryRun': True, 'todo': len(select()), 'recipe': 'cycle_rgb'}))
        return 0
    with pipeline.WriterLock(config.library):
        result = pipeline.run(args.refs_root, select(), config)
    print(json.dumps(result))
    return 1 if result['bad'] else 0


def replace_args(args, **changes):
    return argparse.Namespace(**(vars(args) | changes))


if __name__ == '__main__':
    raise SystemExit(main())
