"""Reviewed corrections to the plan (names, details, tags, use), applied by plan.py right before sets.json is written.

Inputs in H:\\MapLibrary\\plan\\fixes\\ (all optional):
  name_fixes.json  [{"key", "field": "name"|"detail"|"one", "old", "new"}]   (Sol «names», 12.11)
  tag_fixes.json   [{"key", "remove": [...], "add": [...]}]                  (Sol «taxonomy»)
  field_fixes.json [{"key", "before": {...}, "after": {...}}]                (Sol «taxonomy»: use/collection)
  vocab_merge.json {"from": "to"} for plain lower-case search tags            (Sol «taxonomy»)
A fix is applied only when the plan still holds its "old"/"before" value, so a re-classified set is never overwritten
blindly. Writes fixes/applied.json: counts, skipped fixes, and the keys whose set changed (to re-name imported sets).
"""
import json
import os


def _load(path, default):
    if not os.path.exists(path):
        return default
    with open(path, encoding='utf-8-sig') as f:
        return json.load(f)


def apply(sets, lib):
    d = os.path.join(lib, 'plan', 'fixes')
    by_key = {s['key']: s for s in sets}
    changed, skipped = set(), []
    counts = {'name': 0, 'tags': 0, 'field': 0, 'vocab': 0}

    for fx in _load(os.path.join(d, 'name_fixes.json'), []):
        s, field = by_key.get(fx.get('key')), fx.get('field')
        if s is None or field not in ('name', 'detail', 'one') or (s.get(field) or '') != (fx.get('old') or ''):
            skipped.append({'kind': 'name', 'key': fx.get('key'), 'field': field})
            continue
        s[field] = fx['new']
        counts['name'] += 1
        changed.add(s['key'])

    for fx in _load(os.path.join(d, 'tag_fixes.json'), []):
        s = by_key.get(fx.get('key'))
        remove, add = fx.get('remove') or [], fx.get('add') or []
        if s is None or any(t not in s['tags'] for t in remove):
            skipped.append({'kind': 'tags', 'key': fx.get('key')})
            continue
        s['tags'] = list(dict.fromkeys([t for t in s['tags'] if t not in remove] + add))
        counts['tags'] += 1
        changed.add(s['key'])

    for fx in _load(os.path.join(d, 'field_fixes.json'), []):
        s = by_key.get(fx.get('key'))
        before, after = fx.get('before') or {}, fx.get('after') or {}
        if s is None or any(s.get(k) != v for k, v in before.items()) or not set(after) <= {'use', 'collection'}:
            skipped.append({'kind': 'field', 'key': fx.get('key')})
            continue
        s.update(after)
        counts['field'] += 1
        changed.add(s['key'])

    # Exact duplicate sets the owner removed (13.11: «идентичные копии удаляй вообще»). Dropped after the catalog split,
    # so the boundaries of already imported catalogs do not move.
    drop = {x['key'] for x in _load(os.path.join(d, 'drop_sets.json'), [])}
    if drop:
        kept = [s for s in sets if s['key'] not in drop]
        counts['dropped'] = len(sets) - len(kept)
        sets[:] = kept
        by_key = {s['key']: s for s in sets}
    # Exact duplicate PICTURES (visible pixels equal, 13.11 fingerprint of all refs): the copy leaves its set; a set left
    # without files leaves the plan. Also after the catalog split.
    drop_files = {x['rel'] for x in _load(os.path.join(d, 'drop_files.json'), [])}
    if drop_files:
        n = 0
        for s in sets:
            before = len(s['files'])
            s['files'] = [f for f in s['files'] if f not in drop_files]
            if len(s['files']) != before: n += before - len(s['files']); changed.add(s['key'])
        kept = [s for s in sets if s['files']]
        counts['dropped_files'] = n
        counts['dropped'] = counts.get('dropped', 0) + len(sets) - len(kept)
        sets[:] = kept
        by_key = {s['key']: s for s in sets}

    merge = _load(os.path.join(d, 'vocab_merge.json'), {})
    if merge:
        for s in sets:
            new = list(dict.fromkeys(merge.get(t, t) if '/' not in t else t for t in s['tags']))
            if new != s['tags']:
                s['tags'] = new
                counts['vocab'] += 1
                changed.add(s['key'])

    if os.path.isdir(d):
        with open(os.path.join(d, 'applied.json'), 'w', encoding='utf-8') as f:
            json.dump({'counts': counts, 'skipped': skipped, 'changedKeys': sorted(changed)}, f, ensure_ascii=False, indent=1)
    return counts, len(skipped)
