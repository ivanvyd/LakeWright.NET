# Compatibility matrix

What has been run against a real system, what has only been read in documentation, and when.

Anything not listed as **Verified** should be treated as unverified regardless of how confident the
surrounding prose sounds.

Last updated 2026-07-31.

## Legend

| Status | Meaning |
|---|---|
| **Verified** | Executed against a live system on the date shown, with the evidence linked. |
| **Documented** | The vendor documents it. Nobody here has run it. |
| **Unverified** | Believed to work by inference. No evidence either way. |
| **Not supported** | Established as not working, with the reason. |

## Environments

| | |
|---|---|
| Verification workspace | `lakewright-dev`, Azure, eastus2, premium SKU |
| Warehouse | Serverless, 2X-Small, 10 minute auto-stop |
| .NET | 10.0.302 |
| `Microsoft.Azure.Databricks.Client` | 2.9.3 |
| PostgreSQL | 17 (Testcontainers, `postgres:17-alpine`) |

## Databricks

| Capability | Status | Date | Evidence |
|---|---|---|---|
| Entra ID token accepted as a Databricks bearer token (user principal) | **Verified** | 2026-07-31 | [spike 01](planning/spike-01-statement-execution.md) |
| Entra ID token via **managed identity** (no user) | Documented | | Week-one spike, still open |
| Statement Execution with typed parameters | **Verified** | 2026-07-31 | [spike 01](planning/spike-01-statement-execution.md) |
| Parameters resist injection payloads | **Verified** | 2026-07-31 | Value `acme'; DROP TABLE x; --` returned as a literal |
| `EXTERNAL_LINKS` + `ARROW_STREAM` | **Verified** | 2026-07-31 | 200,000 rows, 3.26 MB retrieved |
| Presigned link rejects the `Authorization` header | **Verified** | 2026-07-31 | HTTP 400 from Azure blob |
| Failed statement returns rather than throws | **Verified** | 2026-07-31 | `State=FAILED`, `Manifest` and `Result` null |
| `GetResultChunk`, multi-chunk reads | Unverified | | Test result fit in one chunk |
| Statement read-once semantics, 1 hour expiry | Documented | | |
| Jobs API, `idempotency_token` | Documented | | Not yet exercised |
| Unity Catalog row filters with a **shared** service principal | **Not supported** | | `session_user()` returns the principal, not the end user. This is why isolation lives in the query layer. [ADR 0002](decisions/0002-enforce-tenant-isolation-in-the-query-layer.md) |
| On-behalf-of user tokens for an externally hosted app | **Not supported** | | Exists only for Databricks Apps via `x-forwarded-access-token` |
| Databricks Apps as host for a customer-facing product | **Not supported** | | Anonymous access unsupported; every user must exist in the host's account. [ADR 0001](decisions/0001-host-the-application-outside-databricks.md) |
| Model serving, OpenAI-compatible surface | Documented | | Blocking spike before the AI module is built |
| MLflow tracing from .NET over OTLP | Documented | | |
| Declarative Automation Bundles | Documented | | No bundle in the repository yet |

## Clouds

| Cloud | Status | Notes |
|---|---|---|
| Azure | **Verified** for the rows above | The reference deployment |
| AWS | Unverified | OAuth federation documented; nobody here has run it |
| GCP | Unverified | Same |
| Free Edition | Unverified | Whether service principals and OAuth secrets work there is **undocumented** and is the highest-risk open assumption in the contributor story |

## .NET

| Capability | Status | Date |
|---|---|---|
| Interpolated SQL is a compile error | **Verified** | 2026-07-31, [spike 02](planning/spike-02-interpolation-guard.md) |
| Cross-tenant resolution refused, against real Postgres | **Verified** | 2026-07-31, [testing isolation](guides/testing-isolation.md) |
| Isolation suite fails when isolation is broken | **Verified** | 2026-07-31, same |
| `audit_events` refuses update and delete in application code | **Verified** | 2026-07-31 |
| `audit_events` refuses `ExecuteDelete`/`ExecuteUpdate` at the database | **Verified** | 2026-07-31, as the restricted application role |
| An operation is invisible to a tenant that does not own it | **Verified** | 2026-07-31 |
| EF Core model on Lakebase | Unverified | Standard Postgres only; no Lakebase-specific feature is used |

## Known gaps

- No ASP.NET Core integration, no bundle, no sample application, no operation worker.
- Live integration tests exist only as the spikes above.
- `dependency-review`, CodeQL and Scorecard are gated off while the repository is private, because
  they need Advanced Security there. They start running when it goes public.
