## What this changes

<!-- The behaviour difference, not a restatement of the diff. -->

Closes #

## How it was verified

<!-- Commands run and what they produced. "Tests pass" without output is not verification. -->

## Checklist

- [ ] Tests cover the changed behaviour, and fail without the change
- [ ] Touches tenant resolution, the query layer, or auth? A case was added to the cross-tenant
      isolation suite
- [ ] Changes architecture or public API? An ADR is included in this pull request
- [ ] Breaking change? Listed in `CHANGELOG.md` with a migration note
- [ ] No unapproved client/customer names, project codenames, environment identifiers, private
      paths, or other confidential context appears in the change, metadata, logs, or artifacts
- [ ] Commits signed off (`git commit -s`)
