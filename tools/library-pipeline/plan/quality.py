# Data-quality report for the reference library before import.
#   py -3.13 quality.py --perfile <perfile.json> [--sets ...] [--manifest ...] [--out-dir ...]
# perfile rows: [relPath, sha256, w, h, alphaCoverage, [x0,y0,x1,y1], semiTransparentShare, meanRGB, sharpness, aHash64]
# Writes <out-dir>/quality.json and quality.md. Read-only on sources; the only side file is an aHash cache
# (<lib>/work/quality_ahash_cache.json, keyed by sha256) for rows whose stored aHash is broken (all zeros).
import argparse, collections, datetime, json, os, re
import numpy as np
from PIL import Image

LIB = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))  # deployed: H:\MapLibrary\pipeline\quality.py
if not os.path.exists(os.path.join(LIB, 'map-library.json')):  # repository copy: tools/library-pipeline/plan
    LIB = os.environ.get('MAP_LIBRARY_ROOT', r'H:\MapLibrary')
REFS = r'C:\Users\Leshe\Pictures\refs'

ap = argparse.ArgumentParser()
ap.add_argument('--perfile', required=True)
ap.add_argument('--sets', default=os.path.join(LIB, 'plan', 'sets.json'))
ap.add_argument('--manifest', default=os.path.join(LIB, 'upscaled', 'manifest.jsonl'))
ap.add_argument('--out-dir', default=os.path.join(LIB, 'plan'))
ap.add_argument('--refs', default=REFS)
ap.add_argument('--cache', default=os.path.join(LIB, 'work', 'quality_ahash_cache.json'))
A = ap.parse_args()

# thresholds (task spec)
NEAR_HAMMING, NEAR_SIZE = 3, 0.02
EMPTY_COV, TINY_BBOX = 0.002, 0.002
PAD_BBOX = 0.35
OPAQUE_COV = 0.999
UP_ALPHA_D, UP_COLOUR_D = 8.0, 10.0
SMALL_LONG = 64

P = json.load(open(A.perfile, encoding='utf-8'))
SETS = json.load(open(A.sets, encoding='utf-8'))
MAN = {}
if os.path.exists(A.manifest):
    for line in open(A.manifest, encoding='utf-8'):
        if line.strip():
            r = json.loads(line); MAN[r['rel']] = r  # last record wins
row = {r[0]: r for r in P}
f2s = {}
for s in SETS:
    for f in s['files']:
        f2s[f] = s
skey = lambda f: f2s[f]['key'] if f in f2s else None
cat_of = lambda f: f2s[f]['catalog'] if f in f2s else '(вне наборов)'

def src_path(rel):
    p = os.path.join(A.refs, rel)
    if os.path.exists(p): return p
    p = os.path.join(LIB, 'upscaled', os.path.splitext(rel)[0] + '.webp')
    return p if os.path.exists(p) else None

def bbox_area(r):
    b = r[5]
    return max(0, b[2] - b[0]) * max(0, b[3] - b[1])

# ---------- 1. exact duplicates ----------
def singular(word):
    w = word.lower()
    for suf in ('ies', 'es', 's'):
        if w.endswith(suf) and len(w) > len(suf) + 2:
            return w[:-len(suf)] + ('y' if suf == 'ies' else '')
    return w

def stem_of(rel):
    return re.sub(r'[_ ]\d+$', '', os.path.splitext(rel.split('\\')[-1])[0]).lower()

def tokens(text):
    return {singular(t) for t in re.findall(r'[a-zа-яё]+', text.lower()) if len(t) > 2}

def keep_score(rel):
    parts = rel.split('\\')
    folder, stem = parts[-2], stem_of(rel)
    sc = 0.0
    if rel in f2s: sc += 100                     # only planned files can keep
    if not parts[0].startswith('_'): sc += 4      # the pack itself over bonus/shop folders
    if folder == '_Other': sc -= 3
    else:                                         # folder name that describes the file best
        ft, st = tokens(folder), tokens(stem)
        if ft and st: sc += 5 * len(ft & st) / len(ft | st)
    return sc

by_sha = collections.defaultdict(list)
for r in P: by_sha[r[1]].append(r[0])
dup_groups, dup_drop = [], []
for sha, files in by_sha.items():
    if len(files) < 2: continue
    planned = [f for f in files if f in f2s]
    if not planned:
        dup_groups.append({'sha': sha[:12], 'keep': None, 'drop': [], 'unplanned': files}); continue
    best = max(planned, key=lambda f: (keep_score(f), -files.index(f)))
    drop = [[skey(f), f] for f in planned if f != best]
    dup_drop += drop
    g = {'sha': sha[:12], 'keep': [skey(best), best], 'drop': drop}
    un = [f for f in files if f not in f2s]
    if un: g['unplanned'] = un
    if len({skey(f) for f in planned}) == 1 and len(planned) > 1: g['sameSet'] = True
    # same bytes, different words in the names (e.g. Lit vs Unlit, Red vs Green): one label is wrong, check by eye
    if len({frozenset(tokens(stem_of(f))) for f in planned}) > 1: g['nameConflict'] = True
    if len({cat_of(f) for f in planned}) > 1: g['crossCatalog'] = True   # dropping removes it from a whole style
    dup_groups.append(g)
dropped = {f for _, f in dup_drop}

# ---------- 2. near duplicates inside one set ----------
cache = {}
if os.path.exists(A.cache):
    try: cache = json.load(open(A.cache, encoding='utf-8'))
    except Exception: cache = {}
cache_dirty = False

def own_ahash(rel, sha):
    """[aHash64, alpha-weighted mean RGB] computed here (stored values are broken for some rows)."""
    global cache_dirty
    if sha in cache and isinstance(cache[sha], list): return cache[sha]
    p = src_path(rel)
    if not p: return None
    im = Image.open(p).convert('RGBA')
    a = np.asarray(im, dtype=np.float32)
    al = a[..., 3:4] / 255.0
    rgb = a[..., :3] * al + 128.0 * (1 - al)           # over mid grey
    lum = (rgb @ np.array([0.299, 0.587, 0.114], dtype=np.float32)).astype(np.uint8)
    small = np.asarray(Image.fromarray(lum).resize((8, 8), Image.BOX), dtype=np.float32)
    h = ''.join('1' if v > small.mean() else '0' for v in small.ravel())
    w = al[..., 0]
    mean = [int(round(float((a[..., c] * w).sum() / max(w.sum(), 1e-6)))) for c in range(3)]
    cache[sha] = [h, mean]; cache_dirty = True
    return cache[sha]

broken = lambda r: r[9] == '0' * 64
near_groups, n_rehashed = [], 0
for s in SETS:
    files = [f for f in s['files'] if f in row and f not in dropped]
    if len(files) < 2: continue
    rehash = any(broken(row[f]) for f in files)   # mixing hash definitions inside a set is meaningless
    hashes, means = [], []
    for f in files:
        if rehash:
            hm = own_ahash(f, row[f][1]); n_rehashed += 1
            hashes.append(hm[0] if hm else None); means.append(hm[1] if hm else None)
        else:
            hashes.append(row[f][9]); means.append(row[f][7])
    ok = [i for i, h in enumerate(hashes) if h]
    if len(ok) < 2: continue
    bits = np.array([[c == '1' for c in hashes[i]] for i in ok], dtype=np.uint8)
    wh = np.array([[row[files[i]][2], row[files[i]][3]] for i in ok], dtype=np.float64)
    D = (bits[:, None, :] != bits[None, :, :]).sum(-1)
    sz = (np.abs(wh[:, None, :] - wh[None, :, :]) / np.maximum(wh[:, None, :], wh[None, :, :])).max(-1)
    shas = [row[files[i]][1] for i in ok]
    par = list(range(len(ok)))
    def find(x):
        while par[x] != x: par[x] = par[par[x]]; x = par[x]
        return x
    ii, jj = np.where(np.triu((D <= NEAR_HAMMING) & (sz <= NEAR_SIZE), 1))
    for i, j in zip(ii, jj):
        if shas[i] != shas[j]: par[find(i)] = find(j)
    comp = collections.defaultdict(list)
    for i in range(len(ok)): comp[find(i)].append(i)
    for members in comp.values():
        if len(members) < 2: continue
        m = np.array(members)
        pop = bits[m].sum(1)
        mc = np.array([means[ok[i]] for i in members], dtype=np.float64)
        colour_spread = float((mc.max(0) - mc.min(0)).max())
        gf = [files[ok[i]] for i in members]
        d = os.path.commonpath(gf) if len({os.path.dirname(f) for f in gf}) == 1 else ''
        g = {'set': s['key'], 'dir': d, 'names': [f[len(d) + 1:] if d else f for f in gf],
             'maxD': int(D[np.ix_(m, m)].max()),   # >3 possible: groups are chained pairs
             'kind': 'same-look' if colour_spread <= 6 else 'recolour'}
        if ((pop <= 4) | (pop >= 60)).any(): g['lowInfo'] = True
        if rehash: g['rehashed'] = True
        near_groups.append(g)
if cache_dirty:
    os.makedirs(os.path.dirname(A.cache), exist_ok=True)
    json.dump(cache, open(A.cache, 'w', encoding='utf-8'))

# ---------- 3. empty / opaque / padding ----------
empty, opaque, padding = [], [], []
def border_kind(rel, w, h):
    p = src_path(rel)
    if not p: return 'unread'
    a = np.asarray(Image.open(p).convert('RGB'), dtype=np.int16)
    b = np.concatenate([a[0], a[-1], a[:, 0], a[:, -1]])
    med = np.median(b, axis=0)
    flat = (np.abs(b - med).max(1) <= 10).mean()
    if max(w, h) >= 4 * min(w, h): return 'strip'      # wall/frame/barrier piece, opaque by design
    if flat >= 0.9: return 'flat-background'          # product shot / preview on a plain backdrop
    return 'full-frame'                              # tile, stairs, roof... check by eye
for r in P:
    rel, w, h = r[0], r[2], r[3]
    if rel not in f2s: continue
    area, ba = w * h, bbox_area(r)
    if r[4] < EMPTY_COV or ba < TINY_BBOX * area or min(r[5][2] - r[5][0], r[5][3] - r[5][1]) < 3:
        empty.append([skey(rel), rel, w, h, r[4], r[5]])
        continue
    if r[4] >= OPAQUE_COV and f2s[rel]['use'] == 'object':
        opaque.append([skey(rel), rel, w, h, border_kind(rel, w, h)])
    elif ba < PAD_BBOX * area:
        padding.append([skey(rel), rel, w, h, r[5], round(ba / area, 3)])

# ---------- 4. upscale outliers / small sources ----------
up_out, stale = [], []
for rel, m in MAN.items():
    if not m.get('ok'):
        up_out.append([skey(rel), rel, m.get('factor'), None, None, 'failed: ' + str(m.get('msg'))]); continue
    if (m.get('alphaD') or 0) > UP_ALPHA_D or (m.get('colourD') or 0) > UP_COLOUR_D:
        up_out.append([skey(rel), rel, m['factor'], m.get('alphaD'), m.get('colourD'), m.get('msg')])
    if rel in row and m.get('srcSha256') and m['srcSha256'] != row[rel][1]:
        stale.append(rel)
small = [[skey(r[0]), r[0], r[2], r[3], MAN[r[0]]['factor'] if r[0] in MAN else None]
         for r in P if r[0] in f2s and max(r[2], r[3]) < SMALL_LONG]

# ---------- 5. counts per catalog ----------
C = collections.defaultdict(collections.Counter)
for s in SETS:
    C[s['catalog']]['sets'] += 1; C[s['catalog']]['files'] += len(s['files'])
for rel in MAN: C[cat_of(rel)]['upscaled'] += 1
for _, f in dup_drop: C[cat_of(f)]['exactDupDrop'] += 1
for g in near_groups:
    c = g['set'].split('|')[0]; C[c]['nearDupGroups'] += 1; C[c]['nearDupExtra'] += len(g['names']) - 1
    if g['kind'] == 'same-look': C[c]['nearDupSameLook'] += 1
for x in empty: C[cat_of(x[1])]['empty'] += 1
for x in opaque: C[cat_of(x[1])]['opaque_' + x[4]] += 1
for x in padding: C[cat_of(x[1])]['padding'] += 1
for x in up_out: C[cat_of(x[1])]['upscaleOutlier'] += 1
for x in small: C[cat_of(x[1])]['smallSource'] += 1
unplanned = [r[0] for r in P if r[0] not in f2s]

out = {
    'generated': datetime.datetime.now().isoformat(timespec='seconds'),
    'inputs': {'perfileRows': len(P), 'sets': len(SETS), 'plannedFiles': len(f2s), 'manifestRows': len(MAN),
               'unplannedRows': len(unplanned), 'brokenAHashRows': sum(broken(r) for r in P),
               'rehashedForNearDup': n_rehashed, 'staleUpscales': stale},
    'thresholds': {'nearHamming': NEAR_HAMMING, 'nearSize': NEAR_SIZE, 'emptyCoverage': EMPTY_COV,
                   'tinyBBox': TINY_BBOX, 'paddingBBox': PAD_BBOX, 'opaqueCoverage': OPAQUE_COV,
                   'upAlphaD': UP_ALPHA_D, 'upColourD': UP_COLOUR_D, 'smallLongSide': SMALL_LONG},
    'counts': {k: dict(v) for k, v in sorted(C.items())},
    'exactDuplicates': {'groups': dup_groups, 'drop': dup_drop},
    'nearDuplicates': near_groups,
    'empty': empty,
    'opaque': opaque,
    'padding': padding,
    'upscaleOutliers': up_out,
    'smallSources': small,
}
os.makedirs(A.out_dir, exist_ok=True)
with open(os.path.join(A.out_dir, 'quality.json'), 'w', encoding='utf-8') as fh:
    json.dump(out, fh, ensure_ascii=False, separators=(',', ':'))

# ---------- Russian summary ----------
ok_count = collections.Counter(x[4] for x in opaque)
small_up = sum(1 for x in small if x[4])
L = []
L.append('# Качество библиотеки образцов перед импортом')
L.append('')
L.append(f'Создано {out["generated"]} скриптом `H:\\MapLibrary\\pipeline\\quality.py`. Полные списки — `quality.json` рядом.')
L.append(f'Файлов в описи: {len(P)}, в наборах: {len(f2s)} ({len(SETS)} наборов), увеличено: {len(MAN)}, '
         f'не попали в наборы: {len(unplanned)}.')
L.append('')
L.append('## Итог по проверкам')
L.append(f'- **Точные копии** (одинаковый SHA-256): {len(dup_groups)} групп; убрать из наборов {len(dup_drop)} файлов '
         f'(в {sum(1 for g in dup_groups if g.get("sameSet"))} группах копии в одном наборе; '
         f'в {sum(1 for g in dup_groups if g.get("nameConflict"))} группах у копий разные имена — '
         f'одно из имён неверно, проверить глазами; '
         f'{sum(1 for g in dup_groups if g.get("crossCatalog"))} групп между разными каталогами — там лучше '
         f'оставить файл в обоих стилях одной записью-ссылкой, а не удалять).')
L.append(f'- **Почти одинаковые внутри набора** (aHash ≤ {NEAR_HAMMING}, размер ±{int(NEAR_SIZE*100)} %): '
         f'{len(near_groups)} групп, {sum(len(g["names"]) for g in near_groups)} файлов — кандидаты слить в один вариант. '
         f'Внешне одинаковых (средний цвет различается ≤ 6): {sum(g["kind"] == "same-look" for g in near_groups)}; '
         f'перекраски одной формы: {sum(g["kind"] == "recolour" for g in near_groups)}; '
         f'с малоинформативным хешем (проверить глазами): {sum(1 for g in near_groups if g.get("lowInfo"))}.')
L.append(f'- **Пустые / почти пустые:** {len(empty)}.')
L.append(f'- **Непрозрачные прямоугольники в наборах объектов:** {len(opaque)} — на ровном фоне (похоже на превью) '
         f'{ok_count["flat-background"]}, полосы (стены, рамки — норма) {ok_count["strip"]}, '
         f'во весь кадр (плитка, лестницы, крыши — смотреть) {ok_count["full-frame"]}.')
L.append(f'- **Большие прозрачные поля** (рамка содержимого < {int(PAD_BBOX*100)} % холста): {len(padding)} — обрезать '
         f'по `bbox` перед импортом.')
L.append(f'- **Выбросы увеличения** (alphaD > {UP_ALPHA_D:g} или colourD > {UP_COLOUR_D:g}): {len(up_out)}; '
         f'увеличены из устаревшего исходника: {len(stale)}.')
L.append(f'- **Очень маленькие исходники** (длинная сторона < {SMALL_LONG} px): {len(small)}, из них уже увеличено '
         f'{small_up}; после ×4 останутся мягкими.')
if out['inputs']['brokenAHashRows']:
    L.append(f'- В описи {out["inputs"]["brokenAHashRows"]} строк с испорченным aHash (все нули); для их наборов хеш '
             f'пересчитан заново ({n_rehashed} файлов), кэш `work\\quality_ahash_cache.json`.')
L.append('')
L.append('## По каталогам')
cols = ['sets', 'files', 'upscaled', 'exactDupDrop', 'nearDupGroups', 'nearDupSameLook', 'empty',
        'opaque_flat-background', 'opaque_full-frame', 'padding', 'upscaleOutlier', 'smallSource']
names = ['наборов', 'файлов', 'увелич.', 'копии', 'похожие (групп)', 'из них одинаковые', 'пустые', 'превью',
         'во весь кадр', 'поля', 'выбросы', 'мелкие']
L.append('| каталог | ' + ' | '.join(names) + ' |')
L.append('|' + '---|' * (len(cols) + 1))
for k, v in sorted(C.items()):
    L.append(f'| {k} | ' + ' | '.join(str(v.get(c, 0)) for c in cols) + ' |')
L.append('')
L.append('## Что убрать из наборов (точные копии)')
for g in sorted(dup_groups, key=lambda g: (-bool(g.get('nameConflict')), -len(g['drop'])))[:15]:
    if g['keep']:
        L.append(f'- оставить `{g["keep"][1]}`; убрать: ' + ', '.join(f'`{f}`' for _, f in g['drop'])
                 + (' — **имена расходятся**' if g.get('nameConflict') else ''))
if len(dup_groups) > 15: L.append(f'- … ещё {len(dup_groups) - 15} групп в `quality.json`.')
L.append('')
L.append('## Самые заметные выбросы увеличения')
for x in sorted(up_out, key=lambda x: -max((x[3] or 0) / UP_ALPHA_D, (x[4] or 0) / UP_COLOUR_D))[:10]:
    L.append(f'- `{x[1]}` ×{x[2]}: alphaD={x[3]}, colourD={x[4]}')
with open(os.path.join(A.out_dir, 'quality.md'), 'w', encoding='utf-8') as fh:
    fh.write('\n'.join(L) + '\n')
print(json.dumps({'dupGroups': len(dup_groups), 'dupDrop': len(dup_drop), 'near': len(near_groups),
                  'empty': len(empty), 'opaque': dict(ok_count), 'padding': len(padding), 'upOut': len(up_out),
                  'stale': len(stale), 'small': len(small), 'rehashed': n_rehashed}, ensure_ascii=False))
