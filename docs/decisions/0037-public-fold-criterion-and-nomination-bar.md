# 0037. A public fold criterion and a nomination bar

Status: accepted

Date: 2026-11-28

## Context

LakeWright.NET has one maintainer. Nobody has measured the demand for it. On 2026-11-28 the
repository had 1 star, 0 forks and no issue or pull request authors other than the maintainer and
Dependabot. NuGet download counts exist, but CI mirrors and bots inflate them, so they do not show
that anyone adopted the library.

The project also asked whether to seek a Databricks programme, such as a Solution Accelerator,
Brickbuilder or Built-On. Those programmes are authored by Databricks or meant for partner
organizations. A reviewer would ask two questions first: whether anyone uses the library, and
whether its claims hold on the current release. The project cannot answer either from evidence
today.

`GOVERNANCE.md` already commits to saying so when the project is unmaintained. This record applies
the same honesty to demand, fixed in advance so the threshold cannot be moved after the fact.

## Decision

Two thresholds, on one scale of counted, outside evidence.

**Counted signals.** Only actions an outside party had to take:

- distinct issue or pull request authors who are not the maintainer and not bots;
- adopter reports: an entry in the opt-in [`ADOPTERS.md`](../../ADOPTERS.md), or a private report
  sent to the maintainer and counted without a name;
- independent acceptance reports: the cross-tenant isolation check run in a workspace the
  maintainer does not control, recorded in the `docs/release-evidence` format.

Stars, forks and package downloads are recorded for context only and are never counted.

**Fold floor.** The floor is met when the project has at least 5 distinct external issue or pull
request authors, or at least 2 adopter reports. If the floor is not met on 2027-11-30, the README
gets a maintenance-only banner. Maintenance-only means security fixes, dependency updates and
Databricks API compatibility patches. It means no new modules and no programme outreach.

**Nomination bar.** No Databricks programme is approached until all of these hold:

- the fold floor is met;
- at least 3 independent acceptance reports, from at least 2 clouds;
- every claim the README cites is live-verified on the current major version in the claim ledger
  (`docs/compatibility.md`, checked by `scripts/check-claim-ledger.py`).

**Record.** A dated row is appended to [`docs/adoption/signals.md`](../adoption/signals.md) at each
release and on the fold date, from the GitHub API. The decision on the fold date is made against
that record, not against an impression.

## Consequences

- The private adopters this library targets rarely open public issues. Counting anonymous private
  reports is the mitigation. Continued silence after cheap outreach, such as a Community article
  and the [When not to use LakeWright](../guides/when-not-to-use-lakewright.md) page, counts as an
  answer.
- The maintainer must not solicit drive-by issues to meet the floor. A report counts only if it
  describes real use.
- A private report can be counted but not checked by anyone else. The record shows those reports
  as a separate column, so a reader can weigh them.
- Changing either threshold or the date means a new record superseding this one.
