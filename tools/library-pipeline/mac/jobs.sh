#!/bin/bash
# jobs.sh pending|done : lists job ids in ~/MapWork/upscale/jobs
cd ~/MapWork/upscale/jobs 2>/dev/null || exit 0
for d in */; do d=${d%/}; [ -d "$d" ] || continue
  if [ "$1" = pending ]; then [ -f "$d/READY" ] && [ ! -f "$d/DONE" ] && echo "$d"; fi
  if [ "$1" = done ]; then [ -f "$d/DONE" ] && [ ! -f "$d/FETCHED" ] && echo "$d"; fi
done
exit 0
