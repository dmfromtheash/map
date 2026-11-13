# Contact sheets for looking at every group: 30 groups per sheet, 4 samples each on paper and on dark.
#   look_sheets.py <scratch_dir>     -> H:\MapLibrary\look\sheets\<chunk>-NNN.jpg + index json
import json, os, sys, re
from PIL import Image, ImageDraw, ImageFont
S = sys.argv[1]
LIB = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))  # deployed: H:\MapLibrary\pipeline\look_sheets.py
if not os.path.exists(os.path.join(LIB, 'map-library.json')):  # repository copy: tools/library-pipeline/plan
    LIB = os.environ.get('MAP_LIBRARY_ROOT', r'H:\MapLibrary')
REFS = r'C:\Users\Leshe\Pictures\refs'
OUT = os.path.join(LIB, 'look', 'sheets'); os.makedirs(OUT, exist_ok=True)
inv = json.load(open(os.path.join(LIB, 'plan', 'inventory.json'), encoding='utf-8'))
files_by_group = {}
def shop_base(stem):
    base = re.split(r' \d|\s-\s|_', stem)[0].strip()
    base = re.sub(r'\b(Colorable [A-Z]|Black|White|Gray|Grey|Red|Blue|Green|Brown|Gold|Pink|Yellow|Purple|Orange|Teal|Dead|Empty)\b.*$', '', base).strip()
    return base or stem
for rel, size, w, h, mode, a in inv:
    p = rel.split('\\'); stem = os.path.splitext(p[-1])[0]
    if p[0].startswith('_Текстуры'): key = 'TEX:' + re.sub(r'_\d+$', '', stem)
    elif p[0].startswith('_Магазин'):
        if len(p) == 3: continue
        key = 'SHOP:' + shop_base(stem)
    elif p[-2] == '_Other': key = 'OTHER:' + re.sub(r'_\d+$', '', stem)
    else: key = 'OBJ:' + p[-2]
    files_by_group.setdefault(key, []).append(rel)

def load(rel):
    src = os.path.join(REFS, rel)
    if not os.path.exists(src):
        src = os.path.join(LIB, 'upscaled', os.path.splitext(rel)[0] + '.webp')
    im = Image.open(src); im.load(); return im.convert('RGBA')

font = ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf', 15)
T, PER = 150, 12
index = {}
for chunk in sorted(f[:-5] for f in os.listdir(os.path.join(S, 'chunks_out'))):
    recs = json.load(open(os.path.join(S, 'chunks_out', chunk + '.json'), encoding='utf-8'))
    for start in range(0, len(recs), PER):
        part = recs[start:start + PER]
        sheet = Image.new('RGB', (10 + 5 * (T + 8), len(part) * (T + 30) + 10), (40, 36, 32))
        d = ImageDraw.Draw(sheet)
        for row, rec in enumerate(part):
            y = 10 + row * (T + 30)
            files = files_by_group.get(rec['key'], [])
            pick = files[:: max(1, len(files) // 4)][:4]
            d.text((10, y), f"#{start + row}  {rec['key'][:70]}  ({len(files)} файлов)  → {rec['ru']}", fill=(240, 228, 200), font=font)
            for col, rel in enumerate(pick):
                try:
                    im = load(rel); im.thumbnail((T, T))
                    tile = Image.new('RGBA', (T, T), (236, 228, 208, 255)); tile.alpha_composite(im, ((T - im.width) // 2, (T - im.height) // 2))
                    sheet.paste(tile.convert('RGB'), (10 + col * (T + 8), y + 22))
                except Exception:
                    pass
            if pick:  # the first sample once more on dark ground: shows white parts and halos
                try:
                    im = load(pick[0]); im.thumbnail((T, T))
                    tile = Image.new('RGBA', (T, T), (30, 34, 30, 255)); tile.alpha_composite(im, ((T - im.width) // 2, (T - im.height) // 2))
                    sheet.paste(tile.convert('RGB'), (10 + 4 * (T + 8), y + 22))
                except Exception:
                    pass
        name = f'{chunk}-{start // PER:03d}.jpg'
        sheet.save(os.path.join(OUT, name), quality=80)
        index[name] = {'chunk': chunk, 'first': start, 'count': len(part)}
json.dump(index, open(os.path.join(LIB, 'look', 'index.json'), 'w', encoding='utf-8'), ensure_ascii=False, indent=0)
print('sheets', len(index))
