#!/bin/bash
# Runs the macOS-compatible Map test suites for one review commit; usage: mac_tests.sh <log>
source ~/MapWork/env.sh
cd ~/MapWork/map
for p in Map.Core.Tests Map.Persistence.Tests Map.Rendering.Tests; do
  echo "== $p $(date +%T)"
  nice -n 5 dotnet test tests/$p -c Release -p:RuntimeIdentifier=osx-arm64 --nologo --filter "FullyQualifiedName!~WorkingMemoryBudgetTests" 2>&1 | grep -E "(Не пройден|Failed) |всего|Total tests| error " | head -30
done
echo "DONE $(date +%T)"
