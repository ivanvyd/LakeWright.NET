# Post-ship review: 2026-08-30

Three PRs shipped between `v0.1.2-preview.1` and now: #85, #86, #88. This is the
maintainer's self-review at the time of the deploy, on a repo where every PR has
historically been opened and merged by the same person (the ROADMAP records this
explicitly). The lenses are: correctness, security/tenancy, performance, and
requirements-completeness against the original goals in the threat model and ROADMAP.

## What shipped

| PR | What it does |
|---|---|
| [#85](https://github.com/ivanvyd/LakeWright.NET/pull/85) | Cost attribution (elapsed-time proxy), OTel exporter as adopter choice, reference Bicep + deploy workflow, SSH.NET advisory bump. |
| [#86](https://github.com/ivanvyd/Lakewright.NET/pull/86) | Adds `Lakewright.LoadHarness`, the load test the ROADMAP said was missing. |
| [#88](https://github.com/ivanvyd/Lakewright.NET/pull/88) | Sizes Postgres for production (ADR 0015: 200/12), wires the harness to the sample's `X-Demo-User` scheme, seeds harness memberships as `Member` rather than `Viewer`. |

## What the review found

### Correctness

**Harness smoke was producing 80% errors until the `Member` role fix.** #88's last
shipped commit was the result of running the smoke end-to-end and reading the logs
(see the post-merge conversation on the PR). The header rename and the role fix are
both real bugs that the harness's diagnostic output exposed. Without the smoke, the
PR would have looked green and the harness would have shipped broken.

**`audit_events` will need a one-time data migration in production.** #88's
partitioning is not in #88 (it is the in-flight PR #90). A database that already
has rows in `audit_events` would have those rows dropped on first start with the
new `EnsurePartitionedAuditAsync`. The shipped manager documents this and the
tests run against fresh databases, so the harness smoke does not exercise the
copy path. The right follow-up is a separate migration that copies existing rows
into the partitioned parent before the swap, on a release that has both the
schema change and the data migration in the same transaction.

**`LakewrightDbContext` no longer declares `(OrganizationId, OccurredAt)` as an
index.** The move is correct (the per-partition index covers the same query
path), but it is a behaviour change for an adopter who has built their own index
on the assumption that the parent has it. The ADR records the change; a
contributor who has not read the ADR would not know to look.

**The Bicep still does not size for production.** #89 (in flight) addresses
`max_connections=200` and the per-process `Maximum Pool Size=12`. The Bicep's
default SKU is `Standard_B1ms` (the smallest Postgres tier) and the storage is
32 GB, which is the right call for a reference template and the wrong call for
production. The deploying-azure guide is explicit about this, but the Bicep's
defaults are still the small ones. A contributor who runs the Bicep without
reading the guide gets a dev-tier database.

### Security/tenancy

**The harness sends `X-Demo-User: harness-user-1` and gets a real `Member` row
back.** This is correct: the harness's seed and the resolver's lookup both go
through the same `LakeWrightDbContext`, so a `Member` membership the seed wrote
is the one the resolver finds. But the harness is sending a request as
`harness-user-1` — if the harness ever drives a request as a different principal
without seeding a corresponding membership, the resolver returns null and the
endpoint 403s. The harness is single-tenant by construction, so this is not a
current bug, but the comment on the seed loop in `HarnessEnvironment` should
call out the relationship explicitly so a future contributor changing the seed
does not break the resolver.

**The harness's seeding bypasses `DatabaseHardening`.** Tests run as the schema
owner, and the harness seeds as the schema owner. Both are correct for what
they do (the schema owner can write rows; the application role cannot amend
them), but a contributor who reads only the harness code might think the
harness's principal could `UPDATE` an `audit_events` row through the
`LakeWrightDbContext`. It cannot: the append-only guard and the database
`REVOKE` are layered defenses, and the harness exercises neither path because
neither is in scope for a load test of /operations and /cost.

**The Bicep's `postgresAdminPassword` is a parameter, not a Key Vault reference.**
The Bicep parameter is `@secure()`, and the deploying-azure guide explicitly
warns about passing it inline, but the parameter is still a parameter. A
contributor who copies the Bicep into a non-Bicep deploy path loses the
secure-string semantics. The fix is a Key Vault reference at the parameter
level, which is on the production-readiness list and not on this PR.

### Performance

**The harness's SLO gate has four checks, all of which pass on the smoke.** A
30-second 50-RPS run against testcontainers Postgres: p99 5.1ms on /operations,
3.9ms on /cost, 0% error rate, 5% pool utilisation. The gates are not
production-tight (`--p99-operations=500`, `--p99-cost=200`, `--error-rate=0.1`,
`--pool=0.8`) and the smoke passes them with a wide margin. The next run
should tighten the gates to the ADR-0015-anchored values
(`--p99-operations=200`, `--p99-cost=100`, `--error-rate=0.01`, `--pool=0.6`)
and rerun the smoke. If those fail, the deploy plan needs more headroom; if
they pass, the gates are recorded in the ADR and the smoke becomes a release
gate.

**The harness measures 10 peak connections against a 200-connection ceiling.**
With 12 per-process × 15 processes = 180, the gate at 80% is 144. The smoke
hits 10, which is what one process under steady-state load looks like. The
SLO gate is correct for a single-process smoke; it would be wrong to read it
as a 15-process measurement. The next step is running the harness against
the Bicep-provisioned environment (which #89 enables) and verifying the
gate still passes with the Bicep's `max_connections=200` and the application's
`Maximum Pool Size=12`.

**The Bicep's `Standard_B1ms` SKU burstable baseline is 0.5 vCPUs.** The
Bicep comment does not say "this is a dev SKU"; the deploying-azure guide
does, but a contributor who reads only the Bicep gets the wrong impression.
The comment is on the list to add.

### Requirements completeness

**Cost attribution in currency is still blocked on the metastore-admin grant.**
ADR 0012 documents the blocker, the interface is shipped (#85), and the proxy
implementation is the default. A product that gets the grant wires its own
`ICostAttribution` against the interface and returns `CostSource.Billing`. The
work to write that wiring exists as soon as the grant does; the goal of
shipping the seam is met.

**Reference deployment is the Bicep + workflow.** #85 ships both, the
deploying-azure guide documents the parameters and the OIDC handshake, and
the Bicep compiles. The first deploy is the promotion step (the ROADMAP says
"billable resources someone has to decide to create"). The goal of "the
template exists" is met; the goal of "the template has been applied" is not.

**Load testing is now possible but has not happened against production.**
#86 ships the harness, #88 wires it to the sample's auth, and the CI smoke
runs on every PR. A 5-minute 500-RPS cycle against a real environment is a
human-promoted run, not a CI run. The next milestone should add a
manually-approved `load-harness-production.yml` workflow that runs against
the Bicep-provisioned environment and writes the verdict to the compatibility
matrix.

**Synthetic events and per-tenant cost attribution in Signalboard are
still open.** The ROADMAP records them as Week 7. The cost endpoint answers
against real operations today; the synthetic-events half would let the cost
endpoint answer against a steady-state workload, which is what a demo
recording (also Week 7, dropped) would need. Out of scope for the v0.1
milestone; on the v0.2 list.

## What this review is not

**Not an independent human review.** Every PR has been opened and merged by
the maintainer with zero approvals, per the ROADMAP. The findings above are
the maintainer's self-review, which is the one the skill calls out as the
worst possible judge of its own work. A second pair of eyes on #88 would
have caught the `Member` role bug and the harness-headers bug earlier than
the smoke did.

**Not a full security review.** The lenses above are the maintainer's; the
project's threat model and SOC 2 mapping are the source of truth for what a
proper review should look at. The harness's test-against-real-Postgres
pattern, the application role's grants, and the append-only enforcement are
covered by `AuditLockdownTests` and `DatabaseHardening`, which pass; the
review did not re-examine them.

**Not a coverage re-measurement.** The ROADMAP records the CI coverage at
85.4% as of 2026-08-06, and notes that the new tests from this milestone
(`CostAttributionTests`, `TelemetryTenantGuardTests`) are excluded from that
figure. The coverage report has not been re-run after this merge. The next
maintenance task is to rerun it and update the badge in the README.
