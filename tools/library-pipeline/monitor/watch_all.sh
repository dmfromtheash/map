#!/bin/bash
# watch_all.sh <minutes>: checks Mac, PC feeder/upscale and USER every 5 min (owner 13.11: «каждые 5-10 минут»); prints ANOMALY lines and exits 1 on the first
# problem, prints "OK <time>" and exits 0 after <minutes> without problems. Read-only everywhere.
end=$(( $(date +%s) + ${1:-20} * 60 ))
R='powershell.exe -NoProfile -Command'
while [ $(date +%s) -lt $end ]; do
  bad=""
  m=$(ssh -o BatchMode=yes -o ConnectTimeout=15 user@192.168.31.25 '
    setopt null_glob 2>/dev/null; now=$(date +%s)
    pgrep -f "^python mac_worker.py" >/dev/null || echo "MAC_WORKER_DOWN"
    a=$((now-$(stat -f %m ~/MapWork/upscale/worker.log))); [ $a -gt 900 ] && echo "MAC_WORKER_LOG_STALE ${a}s"
    for j in ~/MapWork/upscale/jobs/*/; do [ -f $j/DONE ] && [ ! -f $j/FETCHED ] && { d=$((now-$(stat -f %m $j/DONE))); [ $d -gt 420 ] && echo "MAC_JOB_NOT_FETCHED $(basename $j) ${d}s"; }; done
    for d in ~/MapWork/agents/*/; do n=$(basename $d); [ -f $d/pid ] || continue; [ -s $d/last.txt ] && continue
      if ps -p $(cat $d/pid) >/dev/null; then a=$((now-$(stat -f %m $d/codex.log))); [ $a -gt 1200 ] && echo "MAC_AGENT_SILENT $n ${a}s"; else echo "MAC_AGENT_DIED $n"; fi; done
    f=$(memory_pressure | tail -1 | grep -oE "[0-9]+"); [ "${f:-100}" -lt 20 ] && echo "MAC_LOW_MEMORY ${f}%"; true
  ' < /dev/null 2>&1) || m="MAC_SSH_FAILED $m"
  bad="$bad$m"
  fl=$(( $(date +%s) - $(stat -c %Y /h/MapLibrary/mac_feeder.log) )); [ $fl -gt 600 ] && bad="$bad
PC_FEEDER_LOG_STALE ${fl}s $(tail -1 /h/MapLibrary/mac_feeder.log)"
  [ "$(tail -3 /h/MapLibrary/mac_feeder.log | grep -c 'retry later')" -ge 3 ] && bad="$bad
PC_FEEDER_RETRY_LOOP $(tail -1 /h/MapLibrary/mac_feeder.log)"
  g=$(powershell.exe -NoProfile -Command "@(Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='nvlddmkm'; StartTime=(Get-Date).AddMinutes(-6)} -ErrorAction SilentlyContinue).Count" 2>/dev/null | tr -d '\r')
  [ "${g:-0}" -gt 0 ] && bad="$bad
PC_GPU_DRIVER_ERRORS ${g} in 6 min (nvlddmkm); $(tail -1 /h/MapLibrary/upscale.log)"
  ul=$(( $(date +%s) - $(stat -c %Y /h/MapLibrary/upscale.log) )); [ $ul -gt 900 ] && bad="$bad
PC_UPSCALE_LOG_STALE ${ul}s"
  u=$($R "& C:\Users\Leshe\.map-remote\r.ps1 '\$age=[int]((Get-Date)-(Get-Item D:\MapChecks\codex.log).LastWriteTime).TotalSeconds; \$st=(Get-ScheduledTask MapSolChecks -ErrorAction SilentlyContinue).State; \$done=Test-Path D:\MapChecks\last.txt; \$p=Get-Process Map.App,Map.Performance -ErrorAction SilentlyContinue | % { \$_.ProcessName+\"=\"+[int]((Get-Date)-\$_.StartTime).TotalMinutes+\"min\" }; \$fr=[int]((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory/1KB); \"USER age=\$age task=\$st done=\$done free=\${fr}MB procs=\$(\$p -join \",\")\"' 2>\$null" 2>/dev/null | tr -d '\r' | grep USER)
  [ -z "$u" ] && bad="$bad
USER_SSH_FAILED"
  if [ -n "$u" ]; then
    age=$(echo "$u" | grep -oE 'age=[0-9]+' | cut -d= -f2); done_=$(echo "$u" | grep -o 'done=True')
    [ -z "$done_" ] && [ "${age:-0}" -gt 1200 ] && bad="$bad
USER_SOL_SILENT $u"
    echo "$u" | grep -qE '(Map.App|Map.Performance)=([3-9][0-9]|[0-9]{3})min' && bad="$bad
USER_PROCESS_LONG $u"
    fr=$(echo "$u" | grep -oE 'free=[0-9]+' | cut -d= -f2); [ "${fr:-99999}" -lt 1500 ] && bad="$bad
USER_LOW_MEMORY $u"
  fi
  bad=$(echo "$bad" | grep -v '^$')
  if [ -n "$bad" ]; then echo "ANOMALY $(date +%H:%M)"; echo "$bad"; echo "context: $u"; exit 1; fi
  sleep 300
done
echo "OK $(date +%H:%M)"
