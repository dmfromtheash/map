import json, sys, collections
data = json.load(open(sys.argv[1], encoding="utf-8"))
frames = [f["name"] for f in data["shared"]["frames"]]
excl = collections.Counter(); incl = collections.Counter(); thread_total = collections.Counter()
skip = ("UNMANAGED_CODE_TIME", "CPU_TIME")
total = 0.0
for prof in data["profiles"]:
    name = prof.get("name", "?")
    stack = []; last = None
    for ev in prof["events"]:
        t = ev["at"]
        if stack and last is not None:
            dt = t - last
            if dt > 0:
                names = [frames[f] for f in stack]
                top = names[-1]
                # Idle waits are not CPU: skip time whose leaf is a wait/sleep.
                if not any(k in top for k in ("WaitForSingleObject", "WaitOne", "Monitor.Wait", "SleepEx", "NtWait", "Thread.Sleep", "WaitHandle", "SpinWait", "GetQueuedCompletionStatus", "ZwWait", "RtlSleep")):
                    total += dt; thread_total[name] += dt; excl[top] += dt
                    for n in set(names): incl[n] += dt
        last = t
        if ev["type"] == "O": stack.append(ev["frame"])
        else:
            if stack: stack.pop()
def short(n): return n if len(n) < 160 else n[:157] + "..."
print(f"total non-wait time (ms, all threads): {total:.0f}")
print("\n== threads")
for t, w in thread_total.most_common(14): print(f"{100*w/total:6.2f}%  {w/1000:8.1f}s  {short(t)}")
print("\n== top self time")
for n, w in excl.most_common(30): print(f"{100*w/total:6.2f}%  {w/1000:8.1f}s  {short(n)}")
print("\n== top inclusive Map.* frames")
for n, w in [x for x in incl.most_common(6000) if x[0].startswith("Map.")][:50]: print(f"{100*w/total:6.2f}%  {w/1000:8.1f}s  {short(n)}")
