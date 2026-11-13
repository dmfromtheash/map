#!/bin/bash
# One full check of the Mac: worker, jobs, Real-ESRGAN, Sol agents, memory, load, disk; plus PC feeder/upscale log tails.
ssh -o BatchMode=yes -o ConnectTimeout=10 user@192.168.31.25 '
now=$(date +%s); echo "MAC $(date +%H:%M) $(uptime | sed "s/.*load/load/")"
pgrep -f "^python mac_worker.py" >/dev/null && echo "worker up pid $(pgrep -f "^python mac_worker.py")" || echo "WORKER DOWN"
echo "worker.log age $((now-$(stat -f %m ~/MapWork/upscale/worker.log)))s: $(tail -1 ~/MapWork/upscale/worker.log)"
echo "jobs: $(for j in ~/MapWork/upscale/jobs/*/; do n=$(basename $j); s=R; [ -f $j/TAKEN ] && s=T; [ -f $j/DONE ] && s=D; [ -f $j/FETCHED ] && s=F; printf "%s:%s " ${n: -4} $s; done)"
echo "esrgan procs: $(pgrep -f realesrgan | wc -l | tr -d " ")"
for d in ~/MapWork/agents/*/; do n=$(basename $d); [ -f $d/pid ] || continue
  if ps -p $(cat $d/pid) >/dev/null; then a=$((now-$(stat -f %m $d/codex.log))); echo "agent $n RUN (log ${a}s)"; elif [ -s $d/last.txt ]; then echo "agent $n done"; else echo "agent $n DIED"; fi; done
echo "$(memory_pressure | tail -1)"; df -h ~ | tail -1 | awk "{print \"disk free \" \$4}"
' < /dev/null 2>&1 || echo "SSH FAILED"
echo "PC feeder: $(tail -1 /h/MapLibrary/mac_feeder.log)"
echo "PC upscale: $(tail -1 /h/MapLibrary/upscale.log)"
echo "USER (Sol live checks):"
powershell.exe -NoProfile -Command "& C:\Users\Leshe\.map-remote\r.ps1 'powershell -NoProfile -ExecutionPolicy Bypass -File C:\Users\user1\map_status.ps1' 2>\$null | Select-Object -Skip 4" 2>/dev/null | tr -d '\r' | grep -v '^$' | cut -c1-200 || echo "USER SSH FAILED"
