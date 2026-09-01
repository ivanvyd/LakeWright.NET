# ADR 0020: Safely partition audit events by month

Status: accepted
Date: 2026-09-01

## Context

`audit_events` is append-only and defaults to seven years of retention. A single table turns
retention into a large `DELETE`; monthly PostgreSQL range partitions make expiry a bounded metadata
operation. The first implementation proposed dropping the EF-created table, changed the entity key
from `Id` to `(Id, OccurredAt)`, interpolated partition bounds into SQL, and created only the current
and next months at application startup. It would have deleted existing history and eventually made
a long-running application unable to append. Those are unacceptable properties for an audit log.

PostgreSQL cannot enforce a unique key on `Id` alone across partitions ranged by `OccurredAt`.
Partition DDL also requires table ownership, while the application role deliberately has only
`SELECT` and `INSERT` on the audit parent.

## Decision

Partition DDL lives in the non-packable `tools/LakeWright.DatabaseMaintenance.Core` boundary. Its
types are internal and are not part of any LakeWright NuGet package. The executable
`tools/LakeWright.DatabaseMaintenance` is the only supported deployment and scheduled-maintenance
entry point. It reads a separate `LAKEWRIGHT_MIGRATION_CONNECTION_STRING`; no application
connection-string fallback exists.

Migration takes an exclusive table lock and an advisory transaction lock. In one serializable
transaction it renames the ordinary table to `audit_events_unpartitioned_backup`, creates the
partitioned parent and every month needed by existing rows, copies all rows, validates the copy in
both directions with `EXCEPT ALL`, installs the identity registry and trigger, and recreates grants
and row-security policies. Any failure rolls the entire transaction back. The migration role must
own the original table. The preflight rejects application `UPDATE`, `DELETE`, or `TRUNCATE` grants
rather than carry a mutable audit-history contract forward.

The one-transaction copy is supported only up to one million rows and 2 GiB by default. Both the
estimated relation size before the exclusive lock and the exact row count after it are checked.
The lock wait defaults to 15 seconds and every maintenance transaction has a five-minute statement
timeout. Enforced bounds are 100 milliseconds to five minutes for lock wait, one second to one
hour for statement time, one to 100 million rows, and 1 MiB to 1 TiB. Sparse history is bounded
separately: the oldest-to-newest range defaults to at most 120 monthly partitions and may be set
from one to 1,200. Larger tables or wider ranges need a separately designed staged migration; this
command refuses them instead of beginning an unbounded outage.

An owning migration role normally bypasses row security, but PostgreSQL `FORCE ROW LEVEL SECURITY`
makes even the owner subject to policies. The migration records the source flags, disables only
`FORCE` inside its rollback-safe transaction while copying and validating all rows, then restores
the original flags on both the rollback copy and partitioned parent. Validation, rollback, and
finalization use the same bounded owner bypass. Integration coverage runs this lifecycle as a real
non-superuser owner whose policy hides one of two rows.

The public EF identity remains `AuditEvent.Id`. A non-partitioned `audit_event_ids` registry has a
primary key on `Id`; a locked-down `SECURITY DEFINER` trigger inserts into it in the same transaction
as every audit row. Duplicate IDs across different months therefore remain impossible. The
application has no direct registry privileges.

The original table remains as a rollback copy until an operator runs `validate` and `finalize`.
A versioned lifecycle row must agree with the physical table topology before any operation runs.
`rollback` first copies post-migration rows back into the original table, validates the copy,
restores its explicit grants, revokes access to the retained partitioned copy, and atomically swaps
names. Running `finalize` after rollback removes the retained partition tree and helper artifacts;
only then can the ordinary table be migrated again. Partition removal runs only in a finalized
lifecycle.

Maintenance is a recurring deployment job, not an application startup hook. Every run creates the
current month plus the configured future window (two months by default), then removes only managed
partitions whose `EndsAt` is at or before the retention cutoff. Retention is configurable in whole
calendar years and defaults to seven. Partition identifiers are generated and quoted inside
PostgreSQL; month calculations, including month-end arithmetic across daylight-saving transitions,
are explicitly UTC regardless of the database session time zone. Clocks, bounds, retention cutoffs,
and lock keys sent by .NET are parameters.

## Deployment sequence

1. Stop writers or drain the application; migration takes an exclusive lock but a quiet window
   makes the duration predictable.
2. Run `migrate`, then `validate`, with the table-owning migration-role connection.
3. Start the new application version and smoke reads and appends through the restricted role.
4. Run `finalize` to remove the rollback copy.
5. Schedule `maintain` at least monthly; daily is cheap and leaves time to react to a failed job.

Before finalization, run `rollback` to restore the original table. Inspect the retained partitioned
copy, then run `finalize` to clean it up; remigration is refused before that cleanup. After accepting
and finalizing the partitioned table, restore from the database backup rather than pretending an
in-place rollback is still available.

## Consequences

- Existing data, grants, row-security policies, append-only ACLs, and the model's `Id` identity are
  retained and covered by PostgreSQL integration tests.
- The one-time migration blocks audit writers while it copies. The bounded command is deliberately
  not a staged online migrator; tables beyond the configured limits require a separate plan.
- The registry is small but not free. Retention deletes the corresponding registry range in the
  same transaction as its partition drop.
- Maintenance requires a real scheduler and alerting owned by the adopter. The library supplies the
  deterministic command; it cannot claim the adopter scheduled it.
