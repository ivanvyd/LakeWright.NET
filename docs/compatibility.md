# Compatibility matrix

What has been run against a real system, what has only been read in documentation, and when.

Anything not listed as **Verified** should be treated as unverified regardless of how confident the
surrounding prose sounds.

Last updated 2026-08-05, when the Genie Conversation API was verified against a live agent and the
dashboard token exchange was added without being verified at all — the first shipped module here
whose rows say Documented rather than Verified, and the reason is recorded beside them.

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
| `TokenCredential` as the shipping credential, through `AddLakeWrightDatabricks` | **Verified** | 2026-08-01 | `LiveDatabricksTests` registers `DefaultAzureCredential` and resolves `IStatementExecutor` and `IJobSubmitter` from the container, so the options binding, the startup validation and the credential are all on the path. No token is passed in. The SDK requests `2ff814a6-3304-4ab8-85cb-cd0e6f879c1d/.default`. |
| Entra ID token via **managed identity** (no user, no secret) | **Verified** | 2026-07-31 | [spike 04](planning/spike-04-managed-identity.md). Databricks resolved the caller as the managed identity; the identity needs no Azure RBAC role. |
| Statement Execution with typed parameters | **Verified** | 2026-08-01 | `LiveDatabricksTests`, through the registered executor. Previously 2026-07-31 via [spike 01](planning/spike-01-statement-execution.md), before the credential changed. |
| Parameters resist injection payloads | **Verified** | 2026-08-01 | Value `acme'; DROP TABLE x; --` returned as a literal |
| `EXTERNAL_LINKS` + `ARROW_STREAM` | **Verified** | 2026-07-31 | 200,000 rows, 3.26 MB retrieved |
| Presigned link rejects the `Authorization` header | **Verified** | 2026-07-31 | HTTP 400 from Azure blob |
| Failed statement returns rather than throws | **Verified** | 2026-08-01 | Surfaces as `StatementOutcome.Failure` with `BAD_REQUEST`, not as an empty success |
| `GetResultChunk`, multi-chunk reads | Unverified | | Test result fit in one chunk |
| Statement read-once semantics, 1 hour expiry | Documented | | |
| Jobs API: submit, poll to a terminal state | **Verified** | 2026-08-01 | `LiveDatabricksTests`, against a real job, through the registered `IJobSubmitter` |
| `idempotency_token` returns the original run on re-submission | **Verified** | 2026-08-01 | The whole reconciliation design rests on this, and it had only been proved against a fake until now |
| Statement rows returned inline | **Verified** | 2026-08-01 | Also caught the bug where an `EXTERNAL_LINKS` default left every successful query with zero rows |
| Unity Catalog row filters with a **shared** service principal | **Not supported** | | `session_user()` returns the principal, not the end user. This is why isolation lives in the query layer. [ADR 0002](decisions/0002-enforce-tenant-isolation-in-the-query-layer.md) |
| On-behalf-of user tokens for an externally hosted app | **Not supported** | | Exists only for Databricks Apps via `x-forwarded-access-token` |
| Databricks Apps as host for a customer-facing product | **Not supported** | | Anonymous access unsupported; every user must exist in the host's account. [ADR 0001](decisions/0001-host-the-application-outside-databricks.md) |
| Model serving: non-streaming chat via `Microsoft.Extensions.AI.OpenAI` | **Verified** | 2026-08-01 | `LiveChatTests`, through `AddDatabricksChatClient` |
| Model serving: **streaming** chat | **Verified, with a shim** | 2026-08-01 | Databricks attaches `usage` to every chunk with `completion_tokens` and `total_tokens` null; the OpenAI deserialiser types them as numbers and throws mid-stream. `StreamingUsageRepairPolicy` strips the incomplete object. A test asserts the call still fails without it, so the shim's necessity is checked rather than assumed |
| Model serving: tool calling | **Verified** | 2026-07-31 | [spike 03](planning/spike-03-openai-compatibility.md) |
| Model serving: **streaming** via the stock OpenAI client, unmodified | **Not supported** | 2026-08-01 | Confirmed still true today, which is why the shim exists. `LiveChatTests` asserts the unmodified client fails, so this row goes stale loudly rather than quietly. |
| Output-token metering on a streaming call | **Available with the shim** | 2026-08-01 | `completion_tokens` is null on every chunk but the last, which carries real numbers and passes through untouched. The wire supports it; per-tenant metering as a feature is not built — see [the roadmap](../ROADMAP.md). |
| MLflow tracing from .NET over OTLP | Documented | | |
| Declarative Automation Bundles: validate, deploy, summary, destroy | **Verified** | 2026-08-01 | CLI v1.10.0. Full lifecycle run against the workspace; dev mode prefixing observed. [Guide](guides/deploying-databricks.md) |
| Genie Conversation API: start a conversation, poll to `COMPLETED` | **Verified** | 2026-08-05 | `LiveGenieTests`, against a real agent in `lakewright-dev`. The answer came back with the SQL Genie generated, which is what proves the attachment shape this library reads is the shape Databricks sends |
| Genie Conversation API: follow-up in the same conversation | **Verified** | 2026-08-05 | `LiveGenieTests`. Same `conversation_id`, a new `message_id` |
| Genie message states beyond the documented three | **Verified** | 2026-08-05 | A live `start-conversation` returned `SUBMITTED`, which this library does not map and therefore keeps polling rather than treating as terminal. The open-ended-states rule, observed rather than assumed |
| Genie Agent as the only tenancy boundary | **Documented** | | The Conversation API takes no filter, no viewer identity and no row predicate. One agent per tenant is the design that follows; nothing in the API can be tested to confirm the absence of a feature |
| AI/BI external embedding: the three-leg token exchange | **Unverified** | | Implemented against the vendor's documented flow and covered by `EmbedTokenBrokerTests` over a fake workspace. **Not run against Databricks.** It needs a service principal OAuth secret, which the account API issues and a workspace token cannot mint, plus a published dashboard. `LiveEmbeddingTests` is written and unrun |
| Entra token accepted after `az login` to a non-default tenant | **Not supported** | 2026-08-05 | Databricks refuses it with `IncorrectClaimException: Expected iss claim to be .../A/, but was .../B/` — an HTTP 400 naming neither the workspace nor the fix. `LiveCredential` now honours `AZURE_TENANT_ID` |
| Creating a **catalog** via bundle or SQL on a Default Storage metastore | **Not supported** | 2026-08-01 | `INVALID_STATE: Metastore storage root URL does not exist`. Needs the UI or an explicit `MANAGED LOCATION`, so the catalog is a documented prerequisite. |

## Clouds

| Cloud | Status | Notes |
|---|---|---|
| Azure | **Verified** for the rows above | The reference deployment |
| AWS | Unverified | OAuth federation documented; nobody here has run it |
| GCP | Unverified | Same |
| Free Edition | **Service principal OAuth does not work** | Free Edition exposes no account console or account-level API, and service principal OAuth depends on account-level identity infrastructure — confirmed by Databricks on the community forum, 2026-08-01. Contributors use their own user identity there, or need no workspace at all: the sample runs without one |

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

- No reference deployment. The sample runs locally against a Postgres container; nothing has been
  deployed to Azure Container Apps, so the ingress half of encryption in transit and the managed
  identity path in a hosted process are both unproven outside the spikes.
- No per-tenant cost attribution in currency. Concurrency is capped per tenant, which bounds spend
  in flight, but nothing reads Databricks billing data — see T5 in
  [the threat model](security/threat-model.md).
- No observability *export*. `LakeWrightTelemetry` publishes four instruments and
  `TelemetryTests` asserts each one, but nothing here exports them: no reference dashboard, no
  alert, and no run in which the fairness and ceiling behaviour was watched rather than tested.
  The instruments are `System.Diagnostics` types with no OpenTelemetry dependency, so the exporter
  is the adopter's choice — see [getting started](guides/getting-started.md#watching-it-in-production).
- Live integration tests: `Category=Live` in the isolation suite, plus the four spikes. They are
  excluded from CI because they need a workspace and cost money.
- The dashboard token exchange has never run against Databricks. Everything else described as
  implemented here has. See the Unverified row above for exactly what is missing.
