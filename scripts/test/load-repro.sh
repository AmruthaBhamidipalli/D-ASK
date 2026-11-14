#!/usr/bin/env bash
# Reproduces the 2026-11-14 regression conditions: the full daemon suite, every TypeScript suite and the frontend build at the same time.
# Usage: bash scripts/test/load-repro.sh <evidence-dir>
set -u
D="$1"; mkdir -p "$D"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$ROOT"
( npm run build > "$D/frontend-build.log" 2>&1; echo "frontend exit $?" >> "$D/frontend-build.log" ) &
( for f in tests/agent-runtime/*.test.ts tests/agent-host/*.test.ts tests/hud-agent/*.test.ts; do
    r=$(timeout 280 npx tsx "$f" 2>&1 | grep -E "^[0-9]+ passed|passed, [0-9]+ failed|[0-9]+/[0-9]+ passed" | tail -1)
    echo "$(date +%T) $(basename "$f"): ${r:-NO SUMMARY}"
  done > "$D/ts-suites.log" 2>&1 ) &
( cd tests/DAsk.Daemon.Tests && DASK_7B1B_DIAG_DIR="$ROOT/$D/7b1b-diag" dotnet test -nologo --no-build --logger "console;verbosity=normal" > "$ROOT/$D/daemon-suite.log" 2>&1 ) &
( for i in $(seq 1 400); do echo "$(date +%T) llama=$(tasklist 2>/dev/null | grep -ci llama-server) freeMB=$(powershell -NoProfile -Command "[int]((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory/1024)")"; sleep 3; done > "$D/resources.log" 2>&1 ) &
MON=$!
wait %1 %2 %3 2>/dev/null
kill $MON 2>/dev/null
grep -E "Passed!|Failed!" "$D/daemon-suite.log"
grep -c "0 failed\|9/9" "$D/ts-suites.log"
