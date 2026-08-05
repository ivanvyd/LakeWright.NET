#!/usr/bin/env bash
# Fails when a tracked document makes a claim the repository contradicts.
#
# This is not a general drift detector — there is no such thing. It is a list of claims that went
# stale once, each paired with the thing that falsifies it. A re-read on 2026-08-05 found four:
# the security workflow described as gated off while private, when it had been public and running
# for five days; observability described as absent in two documents, in a repository whose four
# instruments are asserted by TelemetryTests; secret scanning described as not yet enabled; and a
# control deferred to a milestone that had already shipped without it.
#
# Every rule here is a phrase that must not appear, plus the reason it must not. Add a rule when a
# claim goes stale, not in anticipation of one going stale — a rule written for a hypothetical
# fires on prose that was never wrong and trains people to ignore the check.
set -uo pipefail

violations=0

# $1 = extended regex, $2 = why it is wrong now
forbid() {
  local pattern="$1" reason="$2" hits
  hits=$(grep -rniE --include='*.md' --exclude='*.local.md' "$pattern" . 2>/dev/null \
    | grep -v '^\./\.git/' | grep -v '^\./scripts/') || true
  [ -z "$hits" ] && return 0
  echo "STALE: $reason"
  echo "$hits" | sed 's/^/  /'
  violations=$((violations + 1))
}

# The identifiers B1-B5 and M1-M5 named blockers and milestones in docs/planning/06-remaining-work.md,
# which is no longer tracked. A public document citing one points at something the reader cannot
# open, and two of them were already citing the wrong one.
forbid '\b(blocker |milestone )?[BM][1-9]\b' \
  'blocker and milestone identifiers no longer exist publicly; name the thing instead'

# LakeWrightTelemetry publishes four instruments and TelemetryTests asserts each. What is missing
# is an exporter, which is a different sentence.
forbid 'no (opentelemetry|observability) instrumentation|there is no instrumentation' \
  'LakeWrightTelemetry publishes instruments and TelemetryTests asserts them; say the export is missing'

# The repository went public on 2026-07-31. CodeQL, Scorecard and dependency review have run since.
forbid 'gated off while the repository is private|need(s)? the repository to be public' \
  'the repository is public; CodeQL and Scorecard run'

forbid 'secret scanning( and push protection)? (are|is) \*{0,2}not yet enabled' \
  'secret scanning and push protection were enabled on 2026-08-01'

if [ "$violations" -ne 0 ]; then
  echo
  echo "$violations stale documentation claim(s). Fix the prose, or delete the rule in"
  echo "scripts/check-doc-claims.sh if the claim became true again."
  exit 1
fi

echo "documentation claims checked against the repository, none stale."
