# Prepares the naming input for refs2 (owner 13.11: refs2 into the library): groups in the cls/chunks format plus
# thumbnails (<=384 px, transparency on grey) for an agent with sight.
#   refs2_groups.py <out_dir>   (reads H:\MapLibrary\plan\refs2\files.json, plan\cls\chunks_out, C:\Users\Leshe\Pictures\refs2)
import collections, json, os, re, sys
from PIL import Image

LIB = os.environ.get('MAP_LIBRARY_ROOT', r'H:\MapLibrary')
SRC = r'C:\Users\Leshe\Pictures\refs2'
OUT = sys.argv[1]


def shop_base(stem):  # same rule as plan.py
    base = re.split(r' \d|\s-\s|_', stem)[0].strip()
    base = re.sub(r'\b(Colorable [A-Z]|Black|White|Gray|Grey|Red|Blue|Green|Brown|Gold|Pink|Yellow|Purple|Orange|Teal|Dead|Empty)\b.*$', '', base).strip()
    return base or stem


known = set()
for name in os.listdir(os.path.join(LIB, 'plan', 'cls', 'chunks_out')):
    for rec in json.load(open(os.path.join(LIB, 'plan', 'cls', 'chunks_out', name), encoding='utf-8')):
        known.add(rec['key'])
files = json.load(open(os.path.join(LIB, 'plan', 'refs2', 'files.json'), encoding='utf-8'))
groups = collections.OrderedDict()
for f in files:
    parts = f['rel'].split('\\')
    if f.get('kind') == 'shop-cover' or len(parts) < 3 and parts[0].startswith('_Магазин'):
        continue
    stem = os.path.splitext(parts[-1])[0]
    if parts[0].startswith('_Текстуры'):
        key = 'TEX:' + re.sub(r'_\d+$', '', stem)
    else:
        key = 'SHOP:' + shop_base(stem)
    if key in known:
        continue
    g = groups.setdefault(key, {'name': key, 'packs': collections.Counter(), 'files': 0, 'maxSide': 0, 'samples': [], 'rels': []})
    g['packs'][parts[0] + '/' + (parts[1] if len(parts) > 2 else '')] += 1
    g['files'] += 1
    g['maxSide'] = max(g['maxSide'], f['w'], f['h'])
    if len(g['samples']) < 6:
        g['samples'].append(parts[-1])
    g['rels'].append(f['rel'])
os.makedirs(os.path.join(OUT, 'thumbs'), exist_ok=True)
thumbs = []
for n, g in enumerate(groups.values()):
    g['packs'] = dict(g['packs'])
    for k, rel in enumerate(g['rels'][:2]):
        with Image.open(os.path.join(SRC, rel)) as im:
            im = im.convert('RGBA')
            im.thumbnail((384, 384))
            bg = Image.new('RGBA', im.size, (128, 128, 128, 255))
            bg.alpha_composite(im)
            name = f'{n:04d}_{k}.jpg'
            bg.convert('RGB').save(os.path.join(OUT, 'thumbs', name), quality=85)
            thumbs.append({'thumb': name, 'key': g['name'], 'rel': rel})
out = [{k: v for k, v in g.items() if k != 'rels'} for g in groups.values()]
json.dump(out, open(os.path.join(OUT, 'groups.json'), 'w', encoding='utf-8'), ensure_ascii=False, indent=0)
json.dump(thumbs, open(os.path.join(OUT, 'thumbs.json'), 'w', encoding='utf-8'), ensure_ascii=False, indent=0)
print('groups', len(out), 'thumbs', len(thumbs), 'by kind', collections.Counter(g['name'].split(':')[0] for g in out))
