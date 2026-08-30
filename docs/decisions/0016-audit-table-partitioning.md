# ADR 0016: Partition audit_events by month

Status: accepted
Date: 2026-08-30

## Context

The audit table is the most widely read table in the system and the only one that is
guaranteed to grow without bound. A row per state transition, every claim, every external
statement id, every denied access: it does not shrink. The append-only guarantee in
`LakeWrightDbContext` keeps the rows honest; `REVOKE UPDATE, DELETE` in `DatabaseHardening`
keeps the application from amending them. Neither keeps the table small.

Retention is the next thing. A 12-month retention policy on a single `audit_events` table
becomes a `DELETE` the size of the table. The `DELETE` takes a lock, blocks every other
writer, and the auditor is the person who notices. The same problem hits any maintenance
query that scans a non-trivial fraction of the table: index rebuilds, `VACUUM`, backups.

ADR 0015 anchored production Postgres at `max_connections=200` and the per-process pool at
12. The audit table is the largest single thing on the server, and the per-month fan-out is
the natural cut.

## Decision

**`audit_events` becomes a Postgres-native partitioned table, partitioned by `RANGE (OccurredAt)`, with one child per calendar month.** The `Id` column is no longer unique on its own; the
primary key becomes `(Id, OccurredAt)`, because Postgres requires every unique constraint
on a partitioned table to include the partition key. The composite is fine: a single
partition is exactly one month, and `Id` alone is still unique within that partition.

**`DatabasePartitioning.EnsurePartitionedAuditAsync` is the one place that knows the
shape.** EF's `EnsureCreatedAsync` still runs first; it builds a non-partitioned
`audit_events`, and the partition manager drops it and recreates it as the partitioned
parent. A fresh database ends up partitioned; a database that already has a partitioned
`audit_events` is left alone, because the harness reuses containers and the production
migration re-runs the same DDL on every deploy.

**The current month and the next month are pre-created on every call.** A row whose
`OccurredAt` lands in a month that has no partition fails with `no partition of relation
"audit_events" found for row`. The window between "month rolled over" and "no partition
exists" is the time between deploys; pre-creating next month covers the boundary case where
a row's `OccurredAt` is just past midnight UTC.

**The application role's grants stay the same: `SELECT, INSERT` on the parent.** Postgres
native partitioning makes the parent and its children look like one table to SQL clients,
so a `SELECT FROM audit_events WHERE ...` reads the right partition(s) without the
application having to know. The `REVOKE UPDATE, DELETE` in `DatabaseHardening` continues
to enforce append-only, and the per-partition `(OrganizationId, OccurredAt)` index
preserves the read path the application has always used.

## Consequences

**A retention sweep becomes `DROP TABLE audit_events_2026_01`**, which is a metadata
operation that does not scan rows. The auditor gets a fast, lock-free removal of last
month's data. A scheduled job that runs on the first of the month drops the partition
two months behind (so the current and next month's partitions are never touched).

**Migration is a one-time cost.** A production deploy that runs against a database with
data in `audit_events` has to copy rows into the partitioned parent before the swap. The
shipped `EnsurePartitionedAuditAsync` does not do this copy: tests run against fresh
databases, and the only place an existing audit table exists today is the harness's
ephemeral container. A production migration is a different change, deliberately separated
so the schema swap is reviewable on its own evidence.

**The composite primary key changes a property the model relied on.** A test that
asserts `db.AuditEvents.SingleAsync(x => x.Id == id)` is now ambiguous across months
(there should not be two, but the database no longer forbids it cheaply). Tests that need
a single event filter by `(Id, OccurredAt)`. The change is small; the audit tests do not
do this and the new partition tests use `SingleAsync(e => e.PrincipalId == ...)`, which
still works.

**The (OrganizationId, OccurredAt) index moves from the parent to each child.** EF used
to declare it on the model; the partition manager declares it on each partition's DDL
instead. Postgres's query planner uses the per-partition index, and the partition key
itself is the `OccurredAt` constraint, so the two are not redundant.

**Production deployment must call `EnsurePartitionedAuditAsync` as a migration step,
connected as a role that owns the schema.** The application role cannot create or drop
partitions; it does not own the table. The deploy step is one `await` against a context
connected as the migration role, before the application role starts serving traffic. The
harness's `HarnessEnvironment.CreateAsync` already runs as the schema owner; calling it
from there makes the smoke test prove the partition manager works end-to-end.

## v0.2 milestone

Like the elapsed-time cost proxy (ADR 0012), partitioning joins v0.2. v0.1 was the
eight-week milestone whose definition of done did not name this. The shipped version is
`0.1.2-preview.1`; the next published version with audit partitioning is
`0.2.0-preview.1`, which carries a breaking-change note for the composite primary key
documented above (none in practice, but a future maintainer looking at the old shape
should know why it changed).
