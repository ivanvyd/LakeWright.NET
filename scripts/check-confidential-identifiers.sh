#!/usr/bin/env bash
set -euo pipefail

encoded_terms=${OSS_CONFIDENTIAL_TERMS_B64:-}
if [[ -z "$encoded_terms" ]]; then
  echo "No private confidentiality denylist is available; skipping identifier scan."
  exit 0
fi

work_dir=$(mktemp -d)
terms_file="$work_dir/terms"
trap 'rm -rf "$work_dir"' EXIT

if ! printf '%s' "$encoded_terms" | base64 --decode > "$terms_file"; then
  echo "::error title=Invalid confidentiality denylist::OSS_CONFIDENTIAL_TERMS_B64 is not valid base64."
  exit 1
fi

artifact_root="$work_dir/artifacts"
mkdir -p "$artifact_root"
archive_index=0
for target in "$@"; do
  while IFS= read -r -d '' archive; do
    archive_index=$((archive_index + 1))
    destination="$artifact_root/$archive_index"
    mkdir -p "$destination"
    unzip -qq "$archive" -d "$destination"
  done < <(find "$target" -type f \( -name '*.nupkg' -o -name '*.snupkg' \) -print0)
done

failed=0
commit_message=$(git log -1 --pretty=%B)
while IFS= read -r term || [[ -n "$term" ]]; do
  term=${term%$'\r'}
  [[ -z "$term" ]] && continue

  mapfile -t tracked_matches < <(git grep -I -l -i -F -e "$term" -- || true)
  if (( ${#tracked_matches[@]} > 0 )); then
    failed=1
    for path in "${tracked_matches[@]}"; do
      echo "::error file=$path,title=Confidential identifier::A private denylist term appears in this tracked file."
    done
  fi

  if grep -i -F -q -- "$term" <<< "$commit_message"; then
    failed=1
    echo "::error title=Confidential identifier::A private denylist term appears in the HEAD commit message."
  fi

  if (( archive_index > 0 )); then
    mapfile -t artifact_matches < <(grep -rI -l -i -F -- "$term" "$artifact_root" || true)
    if (( ${#artifact_matches[@]} > 0 )); then
      failed=1
      for path in "${artifact_matches[@]}"; do
        relative=${path#"$artifact_root/"}
        echo "::error title=Confidential identifier in package::A private denylist term appears in extracted package content: $relative"
      done
    fi
  fi
done < "$terms_file"

if (( failed != 0 )); then
  exit 1
fi

echo "Confidential identifier scan passed."
