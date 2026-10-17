import sys
path = sys.argv[1]
frames = []  # (now, gap, scene, tier, fast, rev, fields)
inputs = []  # (now, kind, handlerMs, rev)
for line in open(path, encoding='utf-8'):
    if line.startswith('#') or not line.strip():
        continue
    parts = line.rstrip('\n').split('\t')
    if parts[0] == 'input':
        try:
            inputs.append((int(parts[1]), parts[2], float(parts[3].replace(',', '.')), int(parts[4].split('=')[1])))
        except Exception:
            pass
        continue
    try:
        now = int(parts[0]); gap = int(parts[1]); scene = float(parts[2].replace(',', '.'))
        rev = int(parts[5].split('=')[1]) if parts[5].startswith('rev=') else -1
    except Exception:
        continue
    frames.append((now, gap, scene, parts[3], parts[4], rev, parts[6:] if rev >= 0 else parts[5:]))

def pct(v, p):
    v = sorted(v)
    return v[min(len(v) - 1, int(len(v) * p))] if v else float('nan')

# gesture segments from inputs: split where the pause between inputs exceeds 400 ms
segs = []; cur = []
for inp in inputs:
    if cur and (inp[0] - cur[-1][0] > 400 or inp[1] != cur[-1][1]):
        segs.append(cur); cur = []
    cur.append(inp)
if cur: segs.append(cur)
for k, seg in enumerate(segs):
    if len(seg) < 3: continue
    t0, t1 = seg[0][0], seg[-1][0]
    kinds = set(i[1] for i in seg)
    handler = [i[2] for i in seg]
    # frames completed during the burst (plus the one that answers the last input)
    lat = []
    for inp in seg:
        answer = next((f for f in frames if f[5] >= inp[3] and f[0] >= inp[0]), None)
        if answer: lat.append(answer[0] - inp[0])
    last = next((f for f in frames if f[5] >= seg[-1][3] and f[0] >= t1), None); end = last[0] if last else t1
    burst_frames = [f for f in frames if t0 <= f[0] <= end]
    dur = (end - t0) / 1000 if end > t0 else 0
    scenes = [f[2] for f in burst_frames]
    print(f"gesture {k} {kinds}: inputs={len(seg)} over {(t1 - t0)} ms; frames={len(burst_frames)} fps={len(burst_frames) / dur if dur else 0:.1f}; "
          f"handler P50/P95/max={pct(handler, .5):.1f}/{pct(handler, .95):.1f}/{max(handler):.1f} ms; "
          f"input->frame P50/P95/max={pct(lat, .5)}/{pct(lat, .95)}/{max(lat) if lat else 'nan'} ms; scene P50/P95/max={pct(scenes, .5):.1f}/{pct(scenes, .95):.1f}/{max(scenes) if scenes else 0:.1f}; "
          f"tiers={set(f[3] for f in burst_frames)} fast={sum(1 for f in burst_frames if f[4] == 'fast')}")
    for f in sorted(burst_frames, key=lambda f: -f[2])[:3]:
        print("   slow frame: scene", f[2], f[3], f[4], ' '.join(x for x in f[6] if not x.startswith('objCache')))
