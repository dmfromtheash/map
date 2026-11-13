# Side-by-side close-up: source enlarged (bicubic) | upscaled result, on paper and on dark ground.
#   sheet.py <refs_root> <manifest.jsonl> <out.jpg> [first] [count]
import sys, os, json
from PIL import Image, ImageDraw
root, man, out = sys.argv[1:4]
first = int(sys.argv[4]) if len(sys.argv) > 4 else 0
count = int(sys.argv[5]) if len(sys.argv) > 5 else 24
lib = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))  # deployed: H:\MapLibrary\pipeline\sheet.py
if not os.path.exists(os.path.join(lib, 'map-library.json')):  # repository copy: tools/library-pipeline/upscale
    lib = os.environ.get('MAP_LIBRARY_ROOT', r'H:\MapLibrary')
recs = [json.loads(l) for l in open(man, encoding='utf-8')][first:first + count]
T = 300
rows = []
for r in recs:
    src = Image.open(os.path.join(root, r['rel'])).convert('RGBA')
    up = Image.open(os.path.join(lib, 'upscaled', os.path.splitext(r['rel'])[0] + '.webp')).convert('RGBA')
    k = min(src.width, src.height, 75)
    cx, cy = src.width // 2, src.height // 2
    box = (cx - k // 2, cy - k // 2, cx - k // 2 + k, cy - k // 2 + k)
    a = src.crop(box).resize((T, T), Image.BICUBIC)
    f = r['factor']
    b = up.crop(tuple(v * f for v in box)).resize((T, T), Image.LANCZOS)
    whole = up.copy(); whole.thumbnail((T, T))
    row = Image.new('RGB', (T * 3 + 40, T + 22), (50, 50, 50))
    for i, (img, bg) in enumerate([(a, (236, 228, 208)), (b, (236, 228, 208)), (whole, (40, 44, 40))]):
        tile = Image.new('RGBA', (T, T), bg + (255,)); tile.alpha_composite(img, ((T - img.width) // 2, (T - img.height) // 2))
        row.paste(tile.convert('RGB'), (i * (T + 20), 22))
    ImageDraw.Draw(row).text((4, 4), f"{r['rel'][-70:]}  {r['src']} x{f}  aD={r.get('alphaD')} cD={r.get('colourD')}", fill=(255, 255, 255))
    rows.append(row)
sheet = Image.new('RGB', (rows[0].width, sum(x.height for x in rows)), (30, 30, 30))
y = 0
for x in rows: sheet.paste(x, (0, y)); y += x.height
sheet.save(out, quality=88)
print(sheet.size)
