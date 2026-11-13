#!/bin/bash
# run_agent.sh <name> <effort> : starts one Sol agent in ~/MapWork/agents/<name>.
# --ignore-user-config: no ChatGPT-app plugins (computer use, browser, MCP); auth from ~/.codex.
. ~/MapWork/env.sh
A="$HOME/MapWork/agents/$1"
cd "$A" || exit 1
mkdir -p tmp
mv -f last.txt "tmp/last.prev.txt" 2>/dev/null
cat brief/COMMON.md "brief/$1.md" > prompt.txt
printf '\n\nYour folder (-C): %s. Your report file: %s/SOL_REPORT.md\n' "$A" "$A" >> prompt.txt
nohup caffeinate -i codex exec --ignore-user-config -m gpt-6.1-sol -c model_reasoning_effort="$2" \
  -c features.apps=false -c features.memories=false -c features.chronicle=false \
  -s workspace-write --skip-git-repo-check -C "$A" -o "$A/last.txt" - < prompt.txt > "$A/codex.log" 2>&1 &
echo $! > "$A/pid"
echo "$1 started pid $(cat "$A/pid") effort $2"
