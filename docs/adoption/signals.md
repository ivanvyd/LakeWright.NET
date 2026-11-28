# Adoption signals

The record [ADR 0037](../decisions/0037-public-fold-criterion-and-nomination-bar.md) decides
against. Append a row at each release and on the fold date, 2027-11-30. Never edit a past row.

Counted columns: external authors, public adopters, private adopter reports and acceptance reports.
Stars and forks are context only.

| Date | Release | External authors | Public adopters | Private reports | Acceptance reports (clouds) | Stars | Forks |
|---|---|---|---|---|---|---|---|
| 2026-11-28 | 3.1.0 | 0 | 0 | 0 | 0 (none) | 1 | 0 |

Thresholds: the fold floor is met at 5 external authors or 2 adopter reports (public plus private).
The nomination bar also needs 3 acceptance reports from 2 clouds.

## How to count

External authors are the distinct issue and pull request authors, excluding the maintainer and
bots. The issues endpoint includes pull requests:

```bash
gh api --paginate "repos/ivanvyd/LakeWright.NET/issues?state=all&per_page=100" \
  --jq '.[] | select(.user.type == "User" and .user.login != "ivanvyd") | .user.login' | sort -u
```

Stars and forks:

```bash
gh api repos/ivanvyd/LakeWright.NET --jq '[.stargazers_count, .forks_count] | @tsv'
```

Public adopters are the rows in [`ADOPTERS.md`](../../ADOPTERS.md). Private reports are counted by
the maintainer from email with `ADOPTER` in the subject. Acceptance reports are the files in
[`docs/release-evidence`](../release-evidence) that record an isolation run in a workspace the
maintainer does not control.
