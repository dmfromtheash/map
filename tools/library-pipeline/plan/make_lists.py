# Writes lists/all.txt (every object + shop image; textures are already high-res and not upscaled)
# and lists/trial.txt (a stratified sample: every pack x size class).
import json, sys, os, random, collections
inv = json.load(open(sys.argv[1], encoding='utf-8'))
here = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))  # deployed: H:\MapLibrary\pipeline\make_lists.py
if not os.path.exists(os.path.join(here, 'map-library.json')):  # repository copy: tools/library-pipeline/plan
    here = os.environ.get('MAP_LIBRARY_ROOT', r'H:\MapLibrary')
os.makedirs(os.path.join(here, 'lists'), exist_ok=True)
rows = [r for r in inv if not r[0].startswith('_Текстуры') and not (r[0].startswith('_Магазин') and r[0].count('\\') == 2)]
with open(os.path.join(here, 'lists', 'all.txt'), 'w', encoding='utf-8') as f:
    for r in rows: f.write(r[0] + '\n')
strata = collections.defaultdict(list)
for r in rows:
    side = max(r[2], r[3])
    cls = 's<256' if side < 256 else 's<512' if side <= 512 else 's>512'
    strata[(r[0].split('\\')[0], cls)].append(r[0])
rnd = random.Random(12)
trial = []
for k in sorted(strata): trial += rnd.sample(strata[k], min(3, len(strata[k])))
with open(os.path.join(here, 'lists', 'trial.txt'), 'w', encoding='utf-8') as f:
    for r in trial: f.write(r + '\n')
print('all', len(rows), 'trial', len(trial))
