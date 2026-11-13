#!/bin/bash
# Runs watch_mac.sh and exits (=notifies Claude) on the first problem line or after $1 seconds.
limit=${1:-3600}
timeout "$limit" bash /h/MapLibrary/pipeline/mac/watch_mac.sh | while read -r line; do
  echo "$line"
  if echo "$line" | grep -qE 'DOWN|STALL|DIED|SILENT|refused|timed out|closed|memfree=([0-9]|1[0-9])%'; then
    echo "ALARM: $line"; exit 0
  fi
done
echo "watch window ended $(date +%H:%M)"
