# Lakewright.LoadHarness — load harness for Lakewright.NET

A load harness that drives the kit's sample in-process at a target RPS, captures
per-endpoint latency percentiles + error rate, samples Postgres connection-pool
utilisation, and asserts four SLO gates.

## Why

The project's own ROADMAP says "nothing is load-tested." This harness is the first
piece of that work. It runs against the same `WebApplicationFactory<Program>` + testcontainers
Postgres path the test suite uses, so it gives a real signal without needing a real
Databricks workspace.

## What it does

- Brings up a `postgres:17-alpine` container via testcontainers.
- Boots the sample (`samples/Signalboard`) in-process via `WebApplicationFactory<Program>`.
- Seeds a configurable number of tenants with one Viewer principal each.
- Drives N concurrent workers at a target RPS for a configurable duration, alternating
  between `POST /operations` and `GET /cost` in roughly 80/20.
- Samples `pg_stat_activity` every second and tracks peak connection count.
- Computes p50/p95/p99 latency, error rate, and peak pool utilisation.
- Asserts four SLO gates and exits non-zero on failure.

## SLO gates (defaults)

| Gate | Default | Override |
|---|---|---|
| `POST /operations` p99 | < 500 ms | `--p99-operations` |
| `GET /cost` p99 | < 200 ms | `--p99-cost` |
| Combined error rate | < 0.1% | `--error-rate` |
| Peak Postgres pool utilisation | < 80% | `--pool` |

## How to run

```bash
# From the repo root
dotnet build scripts/load/Lakewright.LoadHarness/Lakewright.LoadHarness.csproj -c Release

# A small smoke run (10s, 50 RPS, very loose SLOs)
dotnet run --project scripts/load/Lakewright.LoadHarness/Lakewright.LoadHarness.csproj -c Release --no-build -- \
    --rps=50 --duration=10 \
    --p99-operations=5000 --p99-cost=2000 \
    --error-rate=0.5 --pool=0.99

# A production-target run (5 minutes, 500 RPS, the default SLOs)
dotnet run --project scripts/load/Lakewright.LoadHarness/Lakewright.LoadHarness.csproj -c Release --no-build -- \
    --rps=500 --duration=300
```

All flags:

| Flag | Default | Description |
|---|---|---|
| `--rps` | 500 | Target requests per second |
| `--duration` | 300 | Run length, seconds |
| `--connections` | 1024 | HTTP client max connections per endpoint |
| `--max-pool` | 100 | EF Core / Npgsql max pool size (passed to Postgres as `max_connections`) |
| `--p99-operations` | 500 | SLO gate, /operations POST p99 in ms |
| `--p99-cost` | 200 | SLO gate, /cost GET p99 in ms |
| `--error-rate` | 0.001 | SLO gate, combined error rate (0..1) |
| `--pool` | 0.8 | SLO gate, peak pool utilisation (0..1) |
| `--pg-image` | postgres:17-alpine | Postgres image to use in testcontainers |
| `--seed` | 2 | Number of seeded tenants |

Env-var equivalents: `LW_HARNESS_RPS`, `LW_HARNESS_DURATION`, etc. Command-line flags
override env vars.

## Known issues (first run)

The first end-to-end run came back with a 100% error rate even with the correct
`X-Demo-User` header and matching memberships. The harness captures and reports
the error correctly — the SLO gate fires — but the underlying cause is in how the
sample's auth scheme interacts with the test server. This is a real bug in the
integration, not in the harness. Track it under "Cost endpoint integration test"
in the load-harness follow-up work.

## Why these defaults

500 RPS and a 31-day SLO are the targets the user picked at planning, scaled down
to a 5-minute smoke for the first full cycle. The SLOs are deliberately
generous; tighten them as you have real production data.

## What this is not

- Not a network load generator. The harness uses `Microsoft.AspNetCore.TestHost.TestServer`,
  which runs the application in-process. Latency includes ASP.NET Core + EF Core + Postgres
  but excludes TCP, TLS, and any load balancer. For network-inclusive latency, run the
  harness against the deployed Bicep stack.
- Not a chaos test. Killing a worker mid-claim, killing Postgres, killing the warehouse
  are all out of scope. The existing claim loop and reconciliation have their own tests; this
  harness is for **throughput under steady state**, not resilience.
- Not a multi-host test. Eight workers in one process is what the test server can pump.
  Add replicas in the Bicep stack and run this harness from each.
