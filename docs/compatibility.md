# Compatibility matrix

What has been run against a real system, what has only been read in documentation, and when.

Anything not listed as **Verified** should be treated as unverified regardless of how confident the
surrounding prose sounds.

Last updated 2026-09-01 for the 1.1.1 release. The complete local suite, package consumer and load
harness were rerun. Docker and browser verification are intentionally pending the reviewed merge
so they exercise the exact commit proposed for the signed tag. Databricks CLI 1.14.1 authenticated
to the active development workspace; bundle validation and the non-mutating plan passed. The
currency path is implemented and locally verified, but its system-table read remains blocked
because the verification identity lacks `USE SCHEMA` on `system.billing`.
A `Documented` row is not promoted to `Verified` by a stable version number; the matrix
records the work that has been done, not a claim about future work.

The commands, results and cleanup evidence are in the
[1.1.1 release evidence](release-evidence/v1.1.1.md), with the completed 1.1.0 publication record in
[the prior release evidence](release-evidence/v1.1.0.md).

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
| Current verification workspace | `dbw-private-project-dev`, Azure, premium SKU |
| Earlier specialized verification workspace | `lakewright-dev`, Azure, eastus2, premium SKU |
| Current warehouse | Serverless PRO, 10 minute auto-stop |
| .NET | 10.0.400 |
| Docker Desktop / engine | 4.88.1 / 29.7.2, WSL 2 Linux containers |
| `Microsoft.Azure.Databricks.Client` | 2.9.3 |
| PostgreSQL | 17 (Testcontainers, `postgres:17-alpine`) |

### 2026-09-01 release revalidation

The 1.0.1 release was checked with these commands:

```text
docker run --rm hello-world
dotnet restore --locked-mode
dotnet build --no-restore -c Release
dotnet format --verify-no-changes --no-restore
dotnet test --no-build --no-restore -c Release --filter "Category!=Live"
dotnet test --no-build --no-restore -c Release --filter "Category=TenantIsolation"
docker compose -p lakewright-v101-local -f samples/Signalboard/compose.yaml up -d --build
docker compose -p lakewright-v101-local -f samples/Signalboard/compose.yaml down -v
databricks bundle validate -t dev --var catalog=dbw_private-project_test
dotnet test --no-build -c Release --filter "FullyQualifiedName~LiveDatabricksTests"
```

The live run used a temporary `dbw_private-project_test.reference` schema, SQL notebook and job. All three
were removed after the three tests passed. The current workspace does not contain the model-serving
endpoint, Genie space, published dashboard and service-principal credentials required by
`LiveChatTests`, `LiveGenieTests` and `LiveEmbeddingTests`, so those historical rows were not dated
forward. Bundle deployment was not repeated; only authenticated validation was rerun. The complete
request, package-consumer, publication and cleanup commands are in the
[1.0.1 release evidence](release-evidence/v1.0.1.md).

## Databricks

| Capability | Status | Date | Evidence |
|---|---|---|---|
| Elapsed-time cost attribution (per-tenant, per-kind DBU) | **Documented** | 2026-08-29 | `OperationCostAttribution` against a real Postgres; the aggregation runs in `EXTRACT(EPOCH FROM (CompletedAt - ClaimedAt))` and is bounded by `ClaimedAt` to `CompletedAt` to exclude in-flight work. ADR 0012. |
| Billing cost attribution (per-tenant DBU and effective list-price currency) | **Documented** | 2026-09-01 | Local tests cover indexed distinct tenant-owned run selection in PostgreSQL, the 500-run/one-query budget, bound workspace/run/window parameters, report/price overlap proration, corrections, malformed rows, polling deadline and best-effort cancellation. The live system-table read is not verified because the development identity lacks the grant. [Runbook](guides/billing-cost-attribution.md), ADR 0012. |
| Bicep reference deployment template compiles | **Documented** | 2026-08-29 | `az bicep build --file infra/azure-container-apps/main.bicep` against the public schema. No deploy has been run. ADR 0014. |
| OpenTelemetry export via the sample's opt-in pipeline | **Documented** | 2026-08-29 | The sample's `Program.cs` subscribes to `LakeWright.Multitenancy` when `Lakewright:OpenTelemetry:Enabled=true`. Vendor-specific wiring is the adopter's. ADR 0013. |
| Entra ID token accepted as a Databricks bearer token (user principal) | **Verified** | 2026-09-01 | `LiveDatabricksTests`; previously [spike 01](planning/spike-01-statement-execution.md) |
| `TokenCredential` as the shipping credential, through `AddLakeWrightDatabricks` | **Verified** | 2026-09-01 | `LiveDatabricksTests` registers `DefaultAzureCredential` and resolves `IStatementExecutor` and `IJobSubmitter` from the container, so the options binding, the startup validation and the credential are all on the path. No token is passed in. The SDK requests `2ff814a6-3304-4ab8-85cb-cd0e6f879c1d/.default`. |
| Entra ID token via **managed identity** (no user, no secret) | **Verified** | 2026-07-31 | [spike 04](planning/spike-04-managed-identity.md). Databricks resolved the caller as the managed identity; the identity needs no Azure RBAC role. |
| Statement Execution with typed parameters | **Verified** | 2026-09-01 | `LiveDatabricksTests`, through the registered executor. Previously 2026-07-31 via [spike 01](planning/spike-01-statement-execution.md), before the credential changed. |
| Parameters resist injection payloads | **Verified** | 2026-09-01 | Value `acme'; DROP TABLE x; --` returned as a literal |
| `EXTERNAL_LINKS` + `ARROW_STREAM` | **Verified** | 2026-07-31 | 200,000 rows, 3.26 MB retrieved |
| Presigned link rejects the `Authorization` header | **Verified** | 2026-07-31 | HTTP 400 from Azure blob |
| Failed statement returns rather than throws | **Verified** | 2026-09-01 | Surfaces as `StatementOutcome.Failure` with `BAD_REQUEST`, not as an empty success |
| `GetResultChunk`, multi-chunk reads | Unverified | | Test result fit in one chunk |
| Statement read-once semantics, 1 hour expiry | Documented | | |
| Jobs API: submit, poll to a terminal state | **Verified** | 2026-09-01 | `LiveDatabricksTests`, against an isolated temporary job, through the registered `IJobSubmitter` |
| `idempotency_token` returns the original run on re-submission | **Verified** | 2026-09-01 | The repeated key returned the original temporary run rather than starting a second one |
| Statement rows returned inline | **Verified** | 2026-09-01 | Also caught the earlier bug where an `EXTERNAL_LINKS` default left every successful query with zero rows |
| Unity Catalog row filters with a **shared** service principal | **Not supported** | | `session_user()` returns the principal, not the end user. This is why isolation lives in the query layer. [ADR 0002](decisions/0002-enforce-tenant-isolation-in-the-query-layer.md) |
| On-behalf-of user tokens for an externally hosted app | **Not supported** | | Exists only for Databricks Apps via `x-forwarded-access-token` |
| Databricks Apps as host for a customer-facing product | **Not supported** | | Anonymous access unsupported; every user must exist in the host's account. [ADR 0001](decisions/0001-host-the-application-outside-databricks.md) |
| Model serving: non-streaming chat via `Microsoft.Extensions.AI.OpenAI` | **Verified** | 2026-08-01 | `LiveChatTests`, through `AddDatabricksChatClient` |
| Model serving: **streaming** chat | **Verified, with a shim** | 2026-08-01 | Databricks attaches `usage` to every chunk with `completion_tokens` and `total_tokens` null; the OpenAI deserialiser types them as numbers and throws mid-stream. `StreamingUsageRepairPolicy` strips the incomplete object. A test asserts the call still fails without it, so the shim's necessity is checked rather than assumed |
| Model serving: tool calling | **Verified** | 2026-07-31 | [spike 03](planning/spike-03-openai-compatibility.md) |
| Model serving: **streaming** via the stock OpenAI client, unmodified | **Not supported** | 2026-08-01 | Confirmed still true today, which is why the shim exists. `LiveChatTests` asserts the unmodified client fails, so this row goes stale loudly rather than quietly. |
| Output-token metering on a streaming call | **Available with the shim** | 2026-08-01 | `completion_tokens` is null on every chunk but the last, which carries real numbers and passes through untouched. The wire supports it; per-tenant metering as a feature is not built — see [the roadmap](../ROADMAP.md). |
| MLflow tracing from .NET over OTLP | Documented | | |
| Declarative Automation Bundles: validate, deploy, summary, destroy | **Verified** | 2026-09-01 | CLI v1.10.0. Validation was re-run against the active workspace; the full lifecycle and dev mode prefixing were previously run on 2026-08-01. [Guide](guides/deploying-databricks.md) |
| Genie Conversation API: start a conversation, poll to `COMPLETED` | **Verified** | 2026-08-05 | `LiveGenieTests`, against a real agent in `lakewright-dev`. The answer came back with the SQL Genie generated, which is what proves the attachment shape this library reads is the shape Databricks sends |
| Genie Conversation API: follow-up in the same conversation | **Verified** | 2026-08-05 | `LiveGenieTests`. Same `conversation_id`, a new `message_id` |
| Genie message states beyond the documented three | **Verified** | 2026-08-05 | A live `start-conversation` returned `SUBMITTED`, which this library does not map and therefore keeps polling rather than treating as terminal. The open-ended-states rule, observed rather than assumed |
| Genie Agent as the only tenancy boundary | **Documented** | | The Conversation API takes no filter, no viewer identity and no row predicate. One agent per tenant is the design that follows; nothing in the API can be tested to confirm the absence of a feature |
| AI/BI external embedding: the three-leg token exchange | **Verified** | 2026-08-06 | `LiveEmbeddingTests`, against a published dashboard in `lakewright-dev` with a service principal holding CAN RUN. Shipped **unverified** on 2026-08-05 and the matrix said so for a day |
| The scoped token carries the tenant as `external_value` | **Verified** | 2026-08-06 | `LiveEmbeddingTests` decodes the returned JWT payload and finds the tenant id inside it. The claim the module exists for, read off the wire rather than off the request we sent |
| Service principal OAuth secrets from a **workspace** admin | **Verified** | 2026-08-06 | `service-principal-secrets-proxy` issues them at workspace level; the account console is not required. An earlier reading of this said it was, because the account API answered 303 to a workspace token |
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
| Populated `audit_events` migration, monthly retention, ACL/RLS preservation and rollback | **Verified** | 2026-09-01, PostgreSQL 17 through `AuditPartitionTests`; scheduling the maintenance command remains an adopter operation. ADR 0020. |
| An operation is invisible to a tenant that does not own it | **Verified** | 2026-07-31 |
| EF Core model on Lakebase | Unverified | Standard Postgres only; no Lakebase-specific feature is used |

## Known gaps

- Billing attribution is not live-verified in the development workspace. The implementation ships
  as an opt-in, but the development identity still lacks access to `system.billing.usage` and
  `system.billing.list_prices`; the proxy remains the default. See the
  [billing runbook](guides/billing-cost-attribution.md), T5 in the threat model and ADR 0012.
- No reference deployment *executed*. The Bicep template in `infra/azure-container-apps/`
  compiles; the workflow in `.github/workflows/deploy-azure.yml` is in place; no one has run a
  deploy with it, so the ingress half of encryption in transit and the managed identity path in
  a hosted process are both unproven outside the spikes. ADR 0014.
- Live integration tests: `Category=Live` in the isolation suite, plus the four spikes. They are
  excluded from CI because they need a workspace and cost money.
