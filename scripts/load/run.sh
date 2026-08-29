#!/usr/bin/env bash
# Orchestrator: build the harness, run a 5-minute cycle at 500 RPS, and exit with the
# harness's verdict. The SLO gates inside the harness are what matter; this script is
# a thin wrapper that gives you one command.
#
# The harness's exit code is the build verdict: 0 = all gates pass, 1 = at least one failed.

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
PROJECT="$ROOT/scripts/load/Lakewright.LoadHarness/Lakewright.LoadHarness.csproj"

echo "== Building harness =="
dotnet build "$PROJECT" -c Release

echo
echo "== Running 5-minute, 500 RPS cycle =="
echo "(adjust --rps/--duration/--p99-* to match your SLOs; see README)"
echo

dotnet run --project "$PROJECT" -c Release --no-build -- \
    --rps=500 \
    --duration=300 \
    --p99-operations=500 \
    --p99-cost=200 \
    --error-rate=0.001 \
    --pool=0.8
