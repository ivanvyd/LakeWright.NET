# ADR 0015: Postgres sized for production, with audit partitioning

Status: accepted
Date: 2026-08-30

## Context

The project publishes a Bicep template (`infra/azure-container-apps/main.bicep`)
that provisions a single `Microsoft.DBforPostgreSQL/flexibleServers` instance
with `POSTGRES_MAX_CONNECTIONS` defaulting to 100. The same default is
inherited by the testcontainers harness, the test suite, and the sample's
`compose.yaml`.

At an enterprise target of 10 web servers + 5 workers, each of 15 processes
holding its own EF Core pool of 100 connections exhausts the 1500-connection
ceiling before any real load. The audit table (`audit_events`) grows at one
row per claim, complete, and access denial — 300+ rows/sec at 100 ops/sec,
which the existing single-partition `audit_events` indexes cannot serve
without losing the cache.

The existing ADRs cover the choice to use Postgres (0003) and the
separation between the migration and application roles (the deploy guide
calls this out), but not the production sizing. The harness's
`POSTGRES_MAX_CONNECTIONS=100` makes the SLO gate's "peak pool
utilisation" measurement real — but the SLO is then anchored at a
default that is below production capacity.

## Decision

Three changes, all anchored at the same Postgres default:

- **`max_connections = 200` on the production Postgres**, raised from 100.
  200 is enough for 15 processes × 12-connection pools + headroom, sized
  to the harness's measured utilisation at the production target.
- **Per-process EF Core pool = 12** (set via `UseNpgsql` `MaxBatchSize` or
  `MaxPoolSize`). The harness's 100 was the EF Core pool *and* the Postgres
  ceiling; with 200 ceiling, 12 pool per process is the right balance
  between connection pressure and the harness's worker count.
- **`audit_events` is partitioned by month**, with the existing
  `(OrganizationId, OccurredAt)` index rebuilt per partition. The
  `audit_events` write rate is a function of the harness's measured
  throughput, not the kit's design. At 300+ rows/sec, single-partition
  indexes fall out of cache within minutes; monthly partitions keep
  each partition's working set bounded.

The harness's SLO gate measures **peak connection usage** and
**error rate** against these new defaults. A failure surfaces as a
build break; a passing run is the gate for the next sizing iteration.

The Bicep template's `POSTGRES_MAX_CONNECTIONS` is updated to 200.
The sample's `compose.yaml` sets `command: ["postgres", "-c", "max_connections=200"]`
so the dev experience matches. The test fixture's testcontainers
overrides the same env var to 200. The harness's `MaxConnections`
default moves to 200 and the EF Core pool default to 12.

## Consequences

- **Postgres is no longer the bottleneck on the path to the harness's
  500 RPS target.** A single instance handles the default 100 RPS × 5
  replicas × 12-connection pool comfortably; 200 `max_connections`
  is a soft target, not a hard one. The Bicep template's
  `Microsoft.DBforPostgreSQL/flexibleServers` SKU (`Standard_B1ms` in
  the current template) supports this; if we hit it, the Bicep
  template's tier moves up.
- **Audit partitioning is a real change.** The harness's tests
  do not exercise audit volume, so the partition migration is
  covered by a one-off migration script and verified by a smoke
  query (insert N rows, query for N across partitions). The test
  suite's existing `AuditTrailTests` continues to run against the
  partitioned table with the same assertions.
- **The migration role split (already documented in
  `docs/compliance/permissions.md`) is now enforced by the
  deployment**, not just by convention. The Bicep template's
  `Microsoft.DBforPostgreSQL/flexibleServers` uses a single
  connection-string for the application; the migration role
  connects separately. This is the part the deploy guide has
  documented as a deployment requirement; the change makes the
  template follow its own doc.
- **The harness's SLO gate is the new gate**, not the old 100-connection
  default. The defaults move to 200; if the harness produces a passing
  smoke at 50 RPS, the real run is the one that needs to be re-evaluated.
  The next round of sizing will be informed by the real numbers.

## What is out of scope

- The kit's other databases (audit storage, decision-log storage)
  are out of scope. The audit-storage and decision-log backends
  are separate ADRs and not this change.
- Lakebase's eventual GA on Azure. The existing ADR 0003 captures
  this; revisit when the trigger fires.
- Production-specific replica set tuning. That's a deployment
  concern, not a code change; the Bicep template's tier is set
  per-environment by the deployer.
