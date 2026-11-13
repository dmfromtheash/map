"""Picks the next N hundreds per catalog exactly as the collector does (DriveLibraryBatch.RunCatalog) and lists the files
USER needs to pour them: plan, state, map-library.json and the upscaled pictures. Read-only on H:."""
import json, os, sys
LIB = r'H:\MapLibrary'
CATALOGS = sys.argv[2:] or ['fantasy-battlemaps-3', 'fantasy-battlemaps-4', 'scifi-battlemaps', 'modern-interiors-1', 'watercolor-battlemaps', 'fantasy-regional', 'fantasy-world', 'parchment-world', 'watercolor-cities-1']
N = int(sys.argv[1]) if len(sys.argv) > 1 else 2

def load(p, default):
    return json.load(open(p, encoding='utf-8-sig')) if os.path.exists(p) else default

plan = load(os.path.join(LIB, 'plan', 'sets.json'), [])
excl = {x['rel'] if isinstance(x, dict) else x for x in load(os.path.join(LIB, 'plan', 'exclusions.json'), [])}
files, summary = [], {}
for cat in CATALOGS:
    state = load(os.path.join(LIB, 'checks', f'state-{cat}.json'), {})
    done = set(state.get('Checked', state.get('checked', [])))
    sets = [dict(s, files=[f for f in s['files'] if f not in excl]) for s in plan if s['catalog'] == cat]
    sets = [s for s in sets if s['files']]
    batches = []
    for _ in range(N):
        batch, count = [], 0
        for s in sets:
            if s['key'] in done or not s.get('ready') or not s.get('named') or 'Не разобрано' in s['tags']: continue
            batch.append(s); count += len(s['files'])
            if count >= 100: break
        if not batch: break
        done |= {s['key'] for s in batch}
        batches.append(sum(len(s['files']) for s in batch))
        for s in batch:
            files += [os.path.join('upscaled', os.path.splitext(f)[0] + '.webp') for f in s['files']]
    summary[cat] = batches
missing = [f for f in files if not os.path.exists(os.path.join(LIB, f))]
size = sum(os.path.getsize(os.path.join(LIB, f)) for f in files if os.path.exists(os.path.join(LIB, f)))
print(json.dumps(summary, ensure_ascii=False), 'files', len(files), 'missing', len(missing), 'MB', round(size / 1e6))
base = ['plan/sets.json', 'plan/exclusions.json', 'plan/trims.json', 'map-library.json'] + [f'checks/state-{c}.json' for c in CATALOGS]
with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'pour_files.txt'), 'w', encoding='utf-8') as out:
    for f in base + files:
        if os.path.exists(os.path.join(LIB, f)): out.write(f.replace('/', os.sep) + '\n')
