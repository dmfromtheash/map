"""Optional refs2 input. Never mutates refs classifications, fixes or catalog boundaries."""
import collections
import hashlib
import json
from pathlib import Path, PureWindowsPath
import re


def load(path):
    with open(path, encoding='utf-8-sig') as stream:
        return json.load(stream)


def add_arguments(parser, classifications=False):
    parser.add_argument('--refs2-inventory', type=Path)
    parser.add_argument('--refs2-files', type=Path, help='files.json with SHA and reviewed duplicate links')
    parser.add_argument('--refs2-prefix', default='refs2')
    parser.add_argument('--refs2-root', type=Path, default=Path(r'C:\Users\Leshe\Pictures'),
                        help='root before the rel prefix (originals, read-only)')
    parser.add_argument('--refs2-pixel-duplicates', type=Path,
                        help='explicit reviewed list of pixel-equivalent rels; never infer from a path collision')
    parser.add_argument('--check-refs2-sources', action='store_true', help='read originals and verify inventory SHA')
    if classifications:
        parser.add_argument('--refs2-classification', type=Path, action='append', default=[])


def safe_rel(rel):
    path = PureWindowsPath(rel)
    if not rel or path.is_absolute() or path.drive or '..' in path.parts:
        raise ValueError(f'unsafe relative path: {rel!r}')
    return str(path)


def prefixed(args, rel):
    return safe_rel(args.refs2_prefix) + '\\' + safe_rel(rel)


def selected(args, refs_shas=()):
    if not args.refs2_files:
        raise ValueError('--refs2-files is required with --refs2-inventory')
    rows = load(args.refs2_inventory)
    files = load(args.refs2_files)
    metadata = {safe_rel(f['rel']): f for f in files}
    inventory_rels = {safe_rel(r[0]) for r in rows}
    if len(metadata) != len(files) or len(inventory_rels) != len(rows):
        raise ValueError('duplicate inventory rel')
    if inventory_rels != metadata.keys():
        raise ValueError('inventory and files.json rels differ')
    reviewed = set(load(args.refs2_pixel_duplicates)) if args.refs2_pixel_duplicates else set()
    if not reviewed <= metadata.keys():
        raise ValueError('reviewed duplicate missing from files.json')
    shas, stats, result = set(refs_shas), collections.Counter(), []
    seen = set()
    for row in rows:
        rel = safe_rel(row[0])
        f = metadata[rel]
        sha = f['sha256']
        if not re.fullmatch('[0-9a-f]{64}', sha):
            raise ValueError(f'invalid SHA: {rel}')
        if args.check_refs2_sources:
            source = args.refs2_root.joinpath(*PureWindowsPath(prefixed(args, rel)).parts)
            with source.open('rb') as stream:
                if hashlib.file_digest(stream, 'sha256').hexdigest() != sha:
                    raise ValueError(f'source SHA mismatch: {rel}')
        if f.get('kind') == 'shop-cover' or (rel.startswith('_Магазин') and rel.count('\\') == 2):
            stats['covers'] += 1
        elif f.get('dupKeep', rel) != rel or sha in seen:
            stats['duplicate_refs2'] += 1
        elif f.get('inRefsBySha') or sha in shas:
            stats['duplicate_refs_sha'] += 1
        elif rel in reviewed:
            stats['duplicate_refs_pixels'] += 1
        else:
            result.append(row)
            stats['kept'] += 1
        seen.add(sha)
    return result, dict(stats)


def build(args, refs_cls, taxonomy, done):
    rows, stats = selected(args, (r['srcSha256'] for rel, r in done.items()
                                 if not rel.startswith(args.refs2_prefix + '\\') and r.get('srcSha256')))
    # Known names are reused only as a fallback. refs2 overrides never flow back into refs.
    cls = dict(refs_cls)
    own_keys = set()
    for path in args.refs2_classification:
        records = load(path)
        cls.update((r['key'], r) for r in records)
        own_keys.update(r['key'] for r in records)
    sets = collections.OrderedDict()
    packs = {'_Магазин — поверхности (превью)': ('shop-surfaces', 'Поверхности'),
             '_Магазин — эффекты (превью)': ('shop-effects', 'Эффекты')}
    for row in rows:
        rel = safe_rel(row[0])
        parts = rel.split('\\')
        stem = PureWindowsPath(rel).stem
        texture = parts[0].startswith('_Текстуры')
        if texture:
            catalog, title = 'textures-2', 'Текстуры'
            key = 'TEX:' + re.sub(r'_\d+$', '', stem)
        else:
            catalog, title = packs[parts[0]]  # reject unexpected packs instead of mixing with refs
            key = 'SHOP:' + taxonomy['shop_base'](stem)
        c = cls.get(key)
        if c is None:
            raise ValueError(f'unclassified refs2 group: {key} ({rel})')
        if c['use'] == 'skip':
            continue
        if key not in own_keys:
            seen = taxonomy['look'].get(key, {})
            c = dict(c, detail=seen.get('detail'), view=seen.get('view'))
            if seen.get('name'):
                c['ru'] = seen['name']
        use = c['use'] if texture else 'object'  # DEC-108: all shop previews are stamps
        tags = ['Тестовое'] + c.get('search', []) + [f'Набор/{parts[1]}']
        for field, tag, vocabulary in (('places', 'Место', 'PLACES'), ('setting', 'Сеттинг', 'SETTINGS'),
                                        ('state', 'Состояние', 'STATES')):
            tags += [f'{tag}/{taxonomy[vocabulary][v]}' for v in c.get(field, []) if v in taxonomy[vocabulary]]
        if c.get('season') in taxonomy['SEASONS']:
            tags.append('Сезон/' + taxonomy['SEASONS'][c['season']])
        if c.get('view') in taxonomy['VIEW']:
            tags.append('Вид/' + taxonomy['VIEW'][c['view']])
        if texture:
            kind = {'ground': 'Земля', 'water': 'Вода', 'floor': 'Пол', 'brush': 'Краска', 'overlay': 'Наложение'}.get(use)
            tags.append('Текстура/' + kind if kind else 'Текстура')
            collection = ['Тестовое', 'Текстуры', {'ground': 'Земля', 'water': 'Вода', 'floor': 'Полы',
                          'brush': 'Краски', 'overlay': 'Наложения'}.get(use, 'Земля')]
        else:
            sub = c['sub']
            section = c.get('section') or taxonomy['SUB_SECTION'].get(sub, 'items')
            section_title, subs = taxonomy['SECTIONS'][section]
            collection = ['Тестовое', section_title, subs.get(sub, sub)]
            tags += [f'Набор/{title}', 'Масштаб/Сцена']
        set_key = f'{catalog}|{key}' + ('' if texture else f'|{parts[1]}')
        item = sets.setdefault(set_key, {'key': set_key, 'catalog': catalog, 'name': c['ru'], 'one': c['ruOne'],
            'use': use, 'detail': c.get('detail'), 'named': bool(c.get('ru')), 'source': 'refs2',
            'collection': collection, 'tags': list(dict.fromkeys(tags)), 'files': []})
        item['files'].append(prefixed(args, rel))
    out = list(sets.values())
    totals = collections.Counter()
    running = collections.Counter()
    for item in out:
        totals[item['catalog']] += len(item['files'])
    for item in out:
        base = item['catalog']
        if totals[base] > 4000:
            item['catalog'] = f'{base}-{running[base] // 4000 + 1}'
        running[base] += len(item['files'])
        texture = base == 'textures-2'
        item['ready'] = texture or all(rel in done and done[rel].get('factor') == 4 for rel in item['files'])
        item['factor'] = [] if texture else sorted({done[rel]['factor'] for rel in item['files'] if rel in done})
    return out, stats
