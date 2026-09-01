#!/usr/bin/env bash
# Fails if any relative markdown link or image points at a file that does not exist.
#
# Documentation rots by link before it rots by content, and a compliance document referencing an
# evidence artifact that was never written is the specific failure this catches: the SOC 2 mapping
# pointed at docs/compliance/permissions.md for a while, and nothing noticed.
#
# Images are checked for the same reason and were not, until the README grew screenshots. A broken
# image renders as alt text, which reads as a deliberate caption rather than as a mistake.
set -uo pipefail

missing=0
checked=0

if ! git rev-parse --is-inside-work-tree >/dev/null; then
  echo "ERROR: documentation links must be checked from a Git worktree."
  exit 1
fi

while IFS= read -r -d '' file; do
  dir=$(dirname "$file")

  # grep exits 1 on no matches, which is normal for a file with no links. Any larger status is a
  # real read or pattern failure; treating that as an empty document would make the gate green on
  # evidence it never inspected.
  targets=$(grep -oE '\]\([^)#]+\.(md|png|jpg|jpeg|svg|gif)[^)]*\)' "$file" \
    | sed -E 's/^\]\(//; s/\)$//; s/#.*$//')
  status=$?
  if [ "$status" -gt 1 ]; then
    echo "ERROR: failed to read Markdown links from $file (exit $status)."
    exit 1
  fi
  [ -z "$targets" ] && continue

  while IFS= read -r target; do
    case "$target" in
      http*|'') continue ;;
    esac
    checked=$((checked + 1))
    if [ ! -f "$dir/$target" ]; then
      echo "MISSING: $target  (linked from $file)"
      missing=$((missing + 1))
    fi
  done <<< "$targets"
done < <(git ls-files -z --cached --others --exclude-standard -- '*.md')

if [ "$missing" -ne 0 ]; then
  echo "$missing broken documentation link(s)."
  exit 1
fi

echo "$checked relative documentation links and images, all resolve."
