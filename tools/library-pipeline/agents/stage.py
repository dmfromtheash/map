"""Stage agent inputs into ~/MapWork/agents/<name>/in on the Mac (tar over ssh; originals only read)."""
import glob, io, json, os, random, subprocess, sys, tarfile
HERE = os.path.dirname(os.path.abspath(__file__))
LIB = r'H:\MapLibrary'
REFS = r'C:\Users\Leshe\Pictures\refs'
SSH = ['ssh', '-o', 'BatchMode=yes', 'user@192.168.31.25']
OLD = r'C:\Users\Leshe\AppData\Local\Temp\claude\5e61ffe7b5425a05\473c8591-4ef9-4178-a83a-ecec8c1a5c4b\scratchpad'


def send(agent, entries):
    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode='w') as t:
        for arc, src in entries:
            if isinstance(src, bytes):
                ti = tarfile.TarInfo(arc); ti.size = len(src); t.addfile(ti, io.BytesIO(src))
            else:
                t.add(src, arcname=arc)
        for name in ('COMMON.md', f'{agent}.md'):
            t.add(os.path.join(HERE, name), arcname=f'brief/{name}')
    subprocess.run(SSH + [f'mkdir -p MapWork/agents/{agent} && tar -xf - -C MapWork/agents/{agent}'],
                   input=buf.getvalue(), check=True)
    print(agent, len(entries), 'entries', round(len(buf.getvalue()) / 1e6, 1), 'MB')


def ok_rows(path):
    out = []
    for l in open(path, encoding='utf-8-sig'):
        try:
            out.append(json.loads(l))
        except ValueError:
            pass
    return out


look = [(f'in/look/{os.path.basename(p)}', p) for p in glob.glob(os.path.join(LIB, 'look', 'out', '*.jsonl'))]
sets = ('in/sets.json', os.path.join(LIB, 'plan', 'sets.json'))
which = sys.argv[1:] or ['esr-tune', 'names', 'taxonomy', 'textures', 'previews']

if 'esr-tune' in which:
    done = {os.path.normcase(r['rel']) for p in ('manifest.jsonl', 'manifest-mac.jsonl')
            for r in ok_rows(os.path.join(LIB, 'upscaled', p)) if r.get('ok')}
    rels = [l.strip() for l in open(os.path.join(LIB, 'lists', 'all.txt'), encoding='utf-8-sig') if l.strip()]
    rem = [r for r in rels if os.path.normcase(r) not in done]
    pick = random.Random(11).sample(rem, 180)
    ent = [(f'in/sample/{i}{os.path.splitext(r)[1].lower()}', os.path.join(REFS, r)) for i, r in enumerate(pick)]
    ent += [('in/sample.json', json.dumps([{'i': i, 'rel': r} for i, r in enumerate(pick)], ensure_ascii=False).encode()),
            ('in/mac_worker.py', os.path.join(LIB, 'pipeline', 'mac_worker.py')),
            ('in/finish_asset.py', os.path.join(LIB, 'pipeline', 'finish_asset.py'))]
    send('esr-tune', ent)
if 'names' in which:
    send('names', [sets] + look)
if 'taxonomy' in which:
    ent = [sets] + look
    if os.path.exists(os.path.join(OLD, 'TAXONOMY.md')):
        ent.append(('in/TAXONOMY.md', os.path.join(OLD, 'TAXONOMY.md')))
    send('taxonomy', ent)
if 'textures' in which:
    ent = [sets, ('in/catalog.json', os.path.join(LIB, 'textures', 'catalog.json')),
           ('in/sol5/tex_report.json', os.path.join(LIB, 'sol', 'sol5', 'tex_report.json')),
           ('in/sol5/tex_fix_plan.json', os.path.join(LIB, 'sol', 'sol5', 'tex_fix_plan.json')),
           ('in/texture_overrides.json', os.path.join(LIB, 'plan', 'texture_overrides.json'))]
    ent += [e for e in look if os.path.basename(e[1]).startswith('tex-')]
    send('textures', ent)
if 'previews' in which:
    fails = [r for r in ok_rows(os.path.join(LIB, 'upscaled', 'failed-mac.jsonl'))
             if 'превью' in r['rel'] and not str(r.get('msg', '')).startswith(('size/mode', 'ERR', 'PC check'))]
    seen, uniq = set(), []
    for r in fails:
        if r['rel'] not in seen:
            seen.add(r['rel']); uniq.append(r)
    uniq = uniq[:80]
    good = [r for r in ok_rows(os.path.join(LIB, 'upscaled', 'manifest-mac.jsonl')) if r.get('ok') and 'превью' in r['rel']]
    good = random.Random(2).sample(good, min(30, len(good)))
    ent = [(f'in/failed/{i}{os.path.splitext(r["rel"])[1].lower()}', os.path.join(REFS, r['rel'])) for i, r in enumerate(uniq)]
    ent += [(f'in/passed/{i}{os.path.splitext(r["rel"])[1].lower()}', os.path.join(REFS, r['rel'])) for i, r in enumerate(good)]
    ent += [('in/failed.json', json.dumps([dict(r, n=i) for i, r in enumerate(uniq)], ensure_ascii=False).encode()),
            ('in/passed.json', json.dumps([dict(r, n=i) for i, r in enumerate(good)], ensure_ascii=False).encode()),
            ('in/finish_asset.py', os.path.join(LIB, 'pipeline', 'finish_asset.py'))]
    send('previews', ent)
