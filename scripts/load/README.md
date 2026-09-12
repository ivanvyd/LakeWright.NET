# Load verification

Checked 2026-09-12. The harness records generated traffic, incomplete work, endpoint latency,
and database sampling evidence. Fast responses alone cannot make an under-delivered run pass.

## Profiles

The workflow runs the **smoke** profile on PRs, schedule, and manual dispatch: 50 RPS for
30 seconds. The local **sustained** target is 500 RPS for five minutes. Both use
`WebApplicationFactory<Program>` and disposable PostgreSQL Testcontainers. They exercise
ASP.NET Core and PostgreSQL without a Databricks workspace, sockets, TLS, or ingress.

The **loopback** profile drives a running Signalboard through real sockets. It signs in using
the actual antiforgery form and cookie, and accepts only loopback URLs:

```powershell
pwsh ./scripts/load/run-loopback-network.ps1 -BaseUrl http://127.0.0.1:8080
```

This profile explicitly omits the database gate because it has no database sampler. Its
JSON labels the gate unevaluated. It does not establish remote ingress or production capacity.

## What the in-process profile measures

- A configurable number of seeded tenants with Member principals; traffic targets the first tenant.
- A synthetic 80/20 mix of operation starts and cost reads, through bounded dispatch workers
  and an arrival queue.
- Scheduled, issued, completed, failed, dropped, censored, queued, and in-flight counts.
  Every arrival must reconcile. Work unfinished at the phase cutoff is censored and fails
  the accounting gate, even when it is a small fraction of the offered load.
- Per-endpoint p50/p95/p99 and errors, plus completion/scheduled and actual/requested rates.
- PostgreSQL client connections sampled once per second, including the sampler. This is
  server-wide client connections divided by `max_connections`, not Npgsql busy/idle pool slots.
  The server is started with `-c max_connections=...` and verified with `SHOW max_connections`.
  The configured Npgsql `MaxPoolSize` is applied to both seed and application connection strings.
- Sample and sampling-failure counts. A TestServer verdict requires samples and no sampling
  failures; missing evidence cannot be reported as an observed zero.
- JSON with options, measurements, verdict, and process CPU time, RSS snapshot, and thread count.

Each run warms up for ten seconds before the timed phase. Database sampling includes warmup.
Exit zero means every required gate passed; an infrastructure or evidence failure also fails
the run. CI retains the JSON when a gate fails.

## Run locally

From the repository root:

```bash
dotnet restore LakeWright.slnx --locked-mode
dotnet restore scripts/load/Lakewright.LoadHarness.MechanismTests/Lakewright.LoadHarness.MechanismTests.csproj --locked-mode
dotnet build scripts/load/Lakewright.LoadHarness/Lakewright.LoadHarness.csproj -c Release --no-restore

dotnet run --project scripts/load/Lakewright.LoadHarness/Lakewright.LoadHarness.csproj -c Release --no-build -- \
  --rps=50 --duration=30 --results=artifacts/load-smoke.json

dotnet run --project scripts/load/Lakewright.LoadHarness/Lakewright.LoadHarness.csproj -c Release --no-build -- \
  --rps=500 --duration=300 --results=artifacts/load-sustained.json
```

Use `--name=value` syntax. Command-line values override their `LW_HARNESS_*` environment
counterparts; the exact names are in `HarnessOptions.cs`.

| Flag | Default | Meaning |
|---|---|---|
| `--rps` | 500 | Requested arrivals per second |
| `--duration` | 300 | Main phase seconds |
| `--connections` | 8 | Maximum requests in flight |
| `--queue` | 64 | Maximum arrivals waiting for a slot |
| `--min-achieved` | 0.95 | Minimum completion fraction and actual/requested rate |
| `--min-samples` | 20 | Minimum completed observations per endpoint |
| `--pg-max-connections` | 200 | Verified PostgreSQL server limit |
| `--pg-pool` | 12 | Application Npgsql maximum pool size |
| `--p99-operations` | 500 | Operation p99 must be below this many milliseconds |
| `--p99-cost` | 200 | Cost p99 must be below this many milliseconds |
| `--error-rate` | 0.001 | Failed/dropped arrivals must be below this fraction |
| `--pool` | 0.8 | Sampled server connection utilisation must be below this fraction |
| `--pg-image` | postgres:17-alpine | Disposable PostgreSQL image |
| `--seed` | 2 | Seeded tenant count |
| `--base-url` | unset | Select loopback socket/cookie profile |
| `--results` | unset | JSON output path |

## Interpretation limits

These are local regression targets. Neither profile establishes production capacity, a
month-long availability SLO, multi-host fairness, or restart/chaos behavior. The workload
accepts operations into the local database; it does not complete real Databricks jobs.

For deployment-specific evidence, use a separate load generator and record the revision,
image digests, hardware and resource limits, replica count, network/ingress, database limits,
dataset, and raw result. Keep successful endpoint latency separate from dropped or censored
traffic. Report the actual verdict, including a failed target. Do not infer a database race
or other root cause from a status code alone.
