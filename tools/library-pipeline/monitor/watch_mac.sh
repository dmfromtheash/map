#!/bin/bash
# Emits a line when something on the Mac changes or looks stuck: upscale stalls (no job result for 5 min),
# agents silent for 10 min, worker down, low memory. Also reports feeder problems from the PC log.
prev=""
while true; do
  s=$(ssh -o BatchMode=yes -o ConnectTimeout=10 user@192.168.31.25 '
    now=$(date +%s)
    w=$(pgrep -f "^python mac_worker.py" >/dev/null && echo up || echo DOWN)
    last=$(stat -f %m ~/MapWork/upscale/worker.log); age=$((now-last)); st=""; [ $age -gt 300 ] && st=" UPSCALE-STALL-${age}s"
    mem=$(memory_pressure | tail -1 | grep -o "[0-9]*%")
    ag=""; for d in ~/MapWork/agents/*/; do n=$(basename $d); [ -f $d/pid ] || continue
      if [ -s $d/last.txt ]; then x=done; elif ps -p $(cat $d/pid) >/dev/null; then
        f=$(ls -t ~/.codex/sessions/*/*/*/rollout-*.jsonl 2>/dev/null | xargs grep -l "agents/$n\"" 2>/dev/null | head -1)
        a=$(( now - $(stat -f %m "${f:-$d/codex.log}") )); x=run; [ $a -gt 600 ] && x="SILENT-${a}s"; else x=DIED; fi
      ag="$ag $n:$x"; done
    echo "worker=$w$st memfree=$mem agents:$ag"' 2>&1 | tail -1)
  fl=$(( $(date +%s) - $(stat -c %Y /h/MapLibrary/mac_feeder.log) )); [ $fl -gt 360 ] && s="$s FEEDER-STALL-${fl}s"
  key=$(echo "$s" | sed 's/memfree=[0-9]*%//; s/STALL-[0-9]*s/STALL/g; s/SILENT-[0-9]*s/SILENT/g')
  low=$(echo "$s" | grep -oE "memfree=[0-9]+" | grep -oE "[0-9]+")
  if [ "$key" != "$prev" ] || { [ -n "$low" ] && [ "$low" -lt 20 ]; }; then echo "$(date +%H:%M) $s"; prev="$key"; fi
  sleep 60
done
