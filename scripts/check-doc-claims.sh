#!/usr/bin/env bash
# Fails when a tracked document makes a claim the repository contradicts.
#
# This is not a general drift detector — there is no such thing, and this file should not be
# mistaken for one. It is a list of claims that went stale once, each paired with the thing that
# falsifies it. A re-read on 2026-08-05 found six: the security workflow described as gated off
# while private, in two documents, when it had been public and running for five days;
# observability described as absent in two more, in a repository whose four instruments are
# asserted by TelemetryTests; secret scanning described as not yet enabled; and a control deferred
# to a milestone that had already shipped without it.
#
# Every rule is a phrase that must not appear, plus the reason. Two lessons are built in:
#
#   Match the subject, not the sentence. The first version of the CodeQL rule matched two exact
#   phrasings and missed a third document saying the same thing in different words. A rule that
#   only recognises wording it has already seen fail is barely a rule.
#
#   A rule that fires on true prose is worse than no rule. It gets deleted or routinely
#   overridden, and takes the working rules with it. Prefer missing a case to crying wolf.
#
# Add a rule when a claim goes stale, not in anticipation of one going stale.
set -uo pipefail

violations=0

if ! git rev-parse --is-inside-work-tree >/dev/null; then
  echo "ERROR: documentation claims must be checked from a Git worktree."
  exit 1
fi

mapfile -d '' documents < <(git ls-files -z --cached --others --exclude-standard -- '*.md')
if [ "${#documents[@]}" -eq 0 ]; then
  echo "ERROR: no repository Markdown files were found."
  exit 1
fi

# $1 = extended regex, $2 = why it is wrong now
#
# grep exits 0 on a match, 1 on none, and 2 or more on a real error — a malformed pattern, an
# unreadable file. Only 0 and 1 are expected here, so anything else fails the script rather than
# reading as "nothing found": a rule that silently stops running is worse than no rule, because
# the green check is then evidence of nothing.
forbid() {
  local pattern="$1" reason="$2" hits status
  # No pipe: after `hits=$(a | b)` the status of `a` is not recoverable, so `$?` means exactly
  # what it says. The file list includes tracked and non-ignored new documents, but not ignored
  # personal notes or build output.
  hits=$(grep -niE "$pattern" "${documents[@]}")
  status=$?
  if [ "$status" -gt 1 ]; then
    echo "ERROR: grep failed (exit $status) on rule: $reason"
    violations=$((violations + 1))
    return 0
  fi
  [ -z "$hits" ] && return 0
  echo "STALE: $reason"
  echo "$hits" | sed 's/^/  /'
  violations=$((violations + 1))
}

# The identifiers B1-B5 and M1-M5 named blockers and milestones in docs/planning/06-remaining-work.md,
# which is no longer tracked. A public document citing one points at something the reader cannot
# open, and two of them were already citing the wrong one.
#
# The word is required rather than optional. A bare `[BM][1-9]` also matches "Apple M1", "an
# App Service B1 plan" and "Section B3", and a check that fails on true prose gets deleted or
# routinely overridden, taking the part that worked with it. The cost is that a bare "in M2"
# slips through; that is the trade, and it is the right way round.
forbid '\b(blocker|milestone) [BM][1-9]\b' \
  'blocker and milestone identifiers no longer exist publicly; name the thing instead'

# LakeWrightTelemetry publishes four instruments and TelemetryTests asserts each. What is missing
# is an exporter, which is a different sentence.
forbid 'no (opentelemetry|observability) instrumentation|there is no instrumentation' \
  'LakeWrightTelemetry publishes instruments and TelemetryTests asserts them; say the export is missing'

# The repository went public on 2026-07-31. CodeQL, Scorecard and dependency review have run since.
#
# The first version of this rule matched two exact phrasings and missed a third in the threat
# model that said the same thing in different words. Matching on the *subject* — the tools —
# beside any word meaning "off" catches a rewording; matching the sentence never would.
forbid '(codeql|scorecard|dependency review)[^.]{0,80}(gated off|not enabled|disabled|until the repository)' \
  'the repository is public; CodeQL, Scorecard and dependency review all run'

forbid 'secret scanning( and push protection)? (are|is) \*{0,2}not yet enabled' \
  'secret scanning and push protection were enabled on 2026-08-01'

if [ "$violations" -ne 0 ]; then
  echo
  echo "$violations stale documentation claim(s). Fix the prose, or delete the rule in"
  echo "scripts/check-doc-claims.sh if the claim became true again."
  exit 1
fi

echo "documentation claims checked against the repository, none stale."
