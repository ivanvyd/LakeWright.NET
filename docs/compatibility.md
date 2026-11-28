# Compatibility matrix

What has been run against a real system, what has only been read in documentation, and when.

Anything not listed as **Verified** should be treated as unverified regardless of how confident the
surrounding prose sounds.

Package evidence updated 2026-11-07 for the published 3.0.0 release. All thirteen stable package
versions passed public-feed, signature and payload verification. Public .NET 8 and .NET 10
consumers passed the membership, query and denied-tenant fixtures. Installed
`LakeWright.Tooling` 3.0.0 also passed `verify-floor` against the public Core, Databricks and
Embedding packages.

The 3.0.0 release checks did not run a live Databricks workspace or deploy the Azure application.
A separate [2026-11-07 AWS check](#2026-11-07-installed-package-aws-check) exercised the published
Core and Databricks packages. Earlier live-system observations retain their original dates.
A stable version does not advance those dates or verify another environment.

The current package-publication and consumer evidence is in the
[3.0.0 release evidence](release-evidence/v3.0.0.md). Earlier evidence remains available for
[2.1.2](release-evidence/v2.1.2.md), [2.1.1](release-evidence/v2.1.1.md),
[2.1.0](release-evidence/v2.1.0.md), [2.0.0](release-evidence/v2.0.0.md), [1.2.0](release-evidence/v1.2.0.md), [1.1.1](release-evidence/v1.1.1.md), and
[1.1.0](release-evidence/v1.1.0.md).

For the 3.1.0 changes, [main CI](https://github.com/ivanvyd/LakeWright.NET/actions/runs/44184251974)
passed at `137ba981f19a28a855ee11ddd86d7ea004f12e97`, including package validation against 3.0.0
and the .NET 8 consumer floor. The separate [conversation package fixture](../tests/conversation-consumer/README.md)
passed on .NET 8 and .NET 10 against locally packed packages. It verified authorized GET-only resume,
legacy adapter compatibility, and credential-safe logging through eleven HTTP client factories.
Those tests used synthetic responses. They do not establish live Genie compatibility or package
availability on nuget.org.

The 2026-09-29 documentation update recorded the v2.1.1 non-live release checks and a fresh net8
public-package consumer. The earlier v2.1.0 reviewed merge covered the complete local suite, load
harness, Docker, HTTP and browser checks. Databricks CLI authenticated to the development workspace
for that earlier verification; bundle validation and the non-mutating plan passed. CI independently
validated the bundle with setup-cli 1.14.1. Live currency attribution remains unverified because the
verification identity lacked `USE SCHEMA` on `system.billing`.

## Legend

| Status | Meaning |
|---|---|
| **Verified** | Executed against a live system on the date shown, with the evidence linked. |
| **Verified locally** | Executed by this repository's own tests or sample, locally or in CI, with no live Databricks workspace. |
| **Documented** | The vendor documents it. Nobody here has run it. |
| **Unverified** | Believed to work by inference. No evidence either way. |
| **Not supported** | Established as not working, with the reason. |

Each claim row has a stable ID, `LW-Cnnn`, with a matching `#lw-cnnn` anchor that other documents
cite. IDs are never renumbered or reused; a new row takes the next unused number wherever it sits.
`scripts/check-claim-ledger.py` fails on a missing, malformed or duplicate ID and on a README
citation that names no row. It warns about a live-verified row dated before the first release of
the current major version.

## Recorded verification environments

These rows record the earlier verification environments. The AWS check below used a separate,
existing, explicitly approved nonproduction workspace on 2026-11-07. The offline package
quickstart needs no workspace.

| | |
|---|---|
| Last recorded verification workspace | Authenticated Azure development workspace, premium SKU; identifier intentionally omitted |
| Earlier specialized verification workspace | `lakewright-dev`, Azure, eastus2, premium SKU |
| Last recorded warehouse | Serverless PRO, 10 minute auto-stop |
| .NET | 10.0.400 |
| Docker Desktop / engine | 4.88.1 / 29.7.2, WSL 2 Linux containers |
| `Microsoft.Azure.Databricks.Client` | 2.9.3 |
| PostgreSQL | 17 (Testcontainers, `postgres:17-alpine`) |

## Target-framework compatibility

| ID | Package surface | Target frameworks | Evidence |
|---|---|---|---|
| <a id="lw-c001"></a>LW-C001 | `LakeWright.Core` | net8.0, net10.0 | Local Release builds verified on 2026-09-02. |
| <a id="lw-c002"></a>LW-C002 | `LakeWright.Embedding` | net8.0, net10.0 | Local Release builds and the stock net8 `tests/consumer-floor` application verified on 2026-09-02. CI packs the candidate, installs it into that application, and rejects persistence dependencies from its net8 assets. |
| <a id="lw-c003"></a>LW-C003 | `LakeWright.Databricks` | net8.0, net10.0 | The net8 asset pins Azure.Core 1.44.1, while net10 retains the current Azure.Core line. The stock consumer resolves the public executor and sends a schema-per-tenant statement to a loopback workspace, verifying catalog and membership-resolved schema delivery without a persistence dependency. Generic shared-schema SQL is deliberately unsupported; see [ADR 0022](decisions/0022-shared-schema-tenancy.md). |
| <a id="lw-c004"></a>LW-C004 | `LakeWright.Conversations` | net8.0, net10.0 | Local Release builds verified on 2026-09-02. Its REST path carries no SQL or persistence dependency. |

The consumer-floor result is a compatibility boundary. It covers the public Embedding and
Databricks package path, not every package surface.

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
databricks bundle validate -t dev --var catalog=<temporary-validation-catalog>
dotnet test --no-build -c Release --filter "FullyQualifiedName~LiveDatabricksTests"
```

The live run used a temporary validation catalog with a `reference` schema, SQL notebook and job. All three
were removed after the three tests passed. That verification workspace lacked the model-serving
endpoint, Genie space, published dashboard and service-principal credentials required by
`LiveChatTests`, `LiveGenieTests` and `LiveEmbeddingTests`, so those historical rows were not dated
forward. Bundle deployment was not repeated; only authenticated validation was rerun. The complete
request, package-consumer, publication and cleanup commands are in the
[1.0.1 release evidence](release-evidence/v1.0.1.md).

## Databricks

| ID | Capability | Status | Date | Evidence |
|---|---|---|---|---|
| <a id="lw-c005"></a>LW-C005 | Elapsed-time cost attribution (per-tenant, per-kind DBU) | **Documented** | 2026-08-29 | `OperationCostAttribution` against a real Postgres; the aggregation runs in `EXTRACT(EPOCH FROM (CompletedAt - ClaimedAt))` and is bounded by `ClaimedAt` to `CompletedAt` to exclude in-flight work. ADR 0012. |
| <a id="lw-c006"></a>LW-C006 | Billing cost attribution (per-tenant DBU and effective list-price currency) | **Documented** | 2026-09-01 | Local tests cover indexed distinct tenant-owned run selection in PostgreSQL, the 500-run/one-query budget, bound workspace/run/window parameters, report/price overlap proration, corrections, malformed rows, polling deadline and best-effort cancellation. The live system-table read is not verified because the development identity lacks the grant. [Runbook](guides/billing-cost-attribution.md), ADR 0012. |
| <a id="lw-c007"></a>LW-C007 | Bicep reference deployment template compiles | **Documented** | 2026-08-29 | `az bicep build --file infra/azure-container-apps/main.bicep` against the public schema. No deploy has been run. ADR 0014. |
| <a id="lw-c008"></a>LW-C008 | OpenTelemetry export via the sample's opt-in pipeline | **Documented** | 2026-08-29 | The sample's `Program.cs` subscribes to `LakeWright.Multitenancy` when `Lakewright:OpenTelemetry:Enabled=true`. Vendor-specific wiring is the adopter's. ADR 0013. |
| <a id="lw-c009"></a>LW-C009 | Entra ID token accepted as a Databricks bearer token (user principal) | **Verified** | 2026-09-01 | `LiveDatabricksTests`; previously [spike 01](planning/spike-01-statement-execution.md) |
| <a id="lw-c010"></a>LW-C010 | `TokenCredential` as the shipping credential, through `AddLakeWrightDatabricks` | **Verified** | 2026-09-01 | `LiveDatabricksTests` registers `DefaultAzureCredential` and resolves `IStatementExecutor` and `IJobSubmitter` from the container, so the options binding, the startup validation and the credential are all on the path. No token is passed in. The SDK requests `2ff814a6-3304-4ab8-85cb-cd0e6f879c1d/.default`. |
| <a id="lw-c011"></a>LW-C011 | Entra ID token via **managed identity** (no user, no secret) | **Verified** | 2026-07-31 | [spike 04](planning/spike-04-managed-identity.md). Databricks resolved the caller as the managed identity; the identity needs no Azure RBAC role. |
| <a id="lw-c012"></a>LW-C012 | Statement Execution with typed parameters | **Verified** | 2026-09-01 | `LiveDatabricksTests`, through the registered executor. Previously 2026-07-31 via [spike 01](planning/spike-01-statement-execution.md), before the credential changed. |
| <a id="lw-c013"></a>LW-C013 | Parameters resist injection payloads | **Verified** | 2026-09-01 | Value `acme'; DROP TABLE x; --` returned as a literal |
| <a id="lw-c014"></a>LW-C014 | `EXTERNAL_LINKS` + `ARROW_STREAM` | **Verified** | 2026-07-31 | 200,000 rows, 3.26 MB retrieved |
| <a id="lw-c015"></a>LW-C015 | Presigned link rejects the `Authorization` header | **Verified** | 2026-07-31 | HTTP 400 from Azure blob |
| <a id="lw-c016"></a>LW-C016 | Failed statement returns rather than throws | **Verified** | 2026-09-01 | Surfaces as `StatementOutcome.Failure` with `BAD_REQUEST`, not as an empty success |
| <a id="lw-c017"></a>LW-C017 | `GetResultChunk`, multi-chunk reads | Unverified | | Test result fit in one chunk |
| <a id="lw-c018"></a>LW-C018 | Statement read-once semantics, 1 hour expiry | Documented | | |
| <a id="lw-c019"></a>LW-C019 | Jobs API: submit, poll to a terminal state | **Verified** | 2026-09-01 | `LiveDatabricksTests`, against an isolated temporary job, through the registered `IJobSubmitter` |
| <a id="lw-c020"></a>LW-C020 | `idempotency_token` returns the original run on re-submission | **Verified** | 2026-09-01 | The repeated key returned the original temporary run rather than starting a second one |
| <a id="lw-c021"></a>LW-C021 | Statement rows returned inline | **Verified** | 2026-09-01 | Also caught the earlier bug where an `EXTERNAL_LINKS` default left every successful query with zero rows |
| <a id="lw-c022"></a>LW-C022 | Unity Catalog row filters with a **shared** service principal | **Not supported** | | `session_user()` returns the principal, not the end user. This is why isolation lives in the query layer. [ADR 0002](decisions/0002-enforce-tenant-isolation-in-the-query-layer.md) |
| <a id="lw-c023"></a>LW-C023 | On-behalf-of user tokens for an externally hosted app | **Not supported** | | Exists only for Databricks Apps via `x-forwarded-access-token` |
| <a id="lw-c024"></a>LW-C024 | Databricks Apps as host for a customer-facing product | **Not supported** | | Anonymous access unsupported; every user must exist in the host's account. [ADR 0001](decisions/0001-host-the-application-outside-databricks.md) |
| <a id="lw-c025"></a>LW-C025 | Model serving: non-streaming chat via `Microsoft.Extensions.AI.OpenAI` | **Verified** | 2026-08-01 | `LiveChatTests`, through `AddDatabricksChatClient` |
| <a id="lw-c026"></a>LW-C026 | Model serving: **streaming** chat | **Verified, with a shim** | 2026-08-01 | Databricks attaches `usage` to every chunk with `completion_tokens` and `total_tokens` null; the OpenAI deserialiser types them as numbers and throws mid-stream. `StreamingUsageRepairPolicy` strips the incomplete object. A test asserts the call still fails without it, so the shim's necessity is checked rather than assumed |
| <a id="lw-c027"></a>LW-C027 | Model serving: tool calling | **Verified** | 2026-07-31 | [spike 03](planning/spike-03-openai-compatibility.md) |
| <a id="lw-c028"></a>LW-C028 | Model serving: **streaming** via the stock OpenAI client, unmodified | **Not supported** | 2026-08-01 | Confirmed still true today, which is why the shim exists. `LiveChatTests` asserts the unmodified client fails, so this row goes stale loudly rather than quietly. |
| <a id="lw-c029"></a>LW-C029 | Output-token metering on a streaming call | **Available with the shim** | 2026-08-01 | `completion_tokens` is null on every chunk but the last, which carries real numbers and passes through untouched. The wire supports it; per-tenant metering as a feature is not built — see [the roadmap](../ROADMAP.md). |
| <a id="lw-c030"></a>LW-C030 | MLflow tracing from .NET over OTLP | Documented | | |
| <a id="lw-c031"></a>LW-C031 | Declarative Automation Bundles: validate, deploy, summary, destroy | **Verified** | 2026-09-01 | CLI v1.10.0. Validation was re-run against the active workspace; the full lifecycle and dev mode prefixing were previously run on 2026-08-01. [Guide](guides/deploying-databricks.md) |
| <a id="lw-c032"></a>LW-C032 | Genie Conversation API: start a conversation, poll to `COMPLETED` | **Verified** | 2026-08-05 | `LiveGenieTests`, against a real agent in `lakewright-dev`. The answer came back with the SQL Genie generated, which is what proves the attachment shape this library reads is the shape Databricks sends |
| <a id="lw-c033"></a>LW-C033 | Genie Conversation API: follow-up in the same conversation | **Verified** | 2026-08-05 | `LiveGenieTests`. Same `conversation_id`, a new `message_id` |
| <a id="lw-c034"></a>LW-C034 | Genie message states beyond the documented three | **Verified** | 2026-08-05 | A live `start-conversation` returned `SUBMITTED`, which this library does not map and therefore keeps polling rather than treating as terminal. The open-ended-states rule, observed rather than assumed |
| <a id="lw-c035"></a>LW-C035 | Genie Agent as the only tenancy boundary | **Documented** | | The Conversation API takes no filter, no viewer identity and no row predicate. One agent per tenant is the design that follows; nothing in the API can be tested to confirm the absence of a feature. Shared mode exists for internal, staff-only tools and is never tenant-isolated. |
| <a id="lw-c036"></a>LW-C036 | Genie answer rendering hygiene | **Verified locally** | 2026-09-02 | `GenieAnswerSanitizerTests` prove raw HTML and markdown links are neutralized, including a `javascript:` target containing parentheses. Exact HTTPS host allow-listing is opt-in. |
| <a id="lw-c037"></a>LW-C037 | AI/BI external embedding: the three-leg token exchange | **Verified** | 2026-08-06 | `LiveEmbeddingTests`, against a published dashboard in `lakewright-dev` with a service principal holding CAN RUN. Shipped **unverified** on 2026-08-05 and the matrix said so for a day |
| <a id="lw-c038"></a>LW-C038 | The scoped token carries the tenant as `external_value` | **Verified** | 2026-08-06 | `LiveEmbeddingTests` decodes the returned JWT payload and finds the tenant id inside it. The claim the module exists for, read off the wire rather than off the request we sent |
| <a id="lw-c039"></a>LW-C039 | Service principal OAuth secrets from a **workspace** admin | **Verified** | 2026-08-06 | `service-principal-secrets-proxy` issues them at workspace level; the account console is not required. An earlier reading of this said it was, because the account API answered 303 to a workspace token |
| <a id="lw-c040"></a>LW-C040 | Entra token accepted after `az login` to a non-default tenant | **Not supported** | 2026-08-05 | Databricks refuses it with `IncorrectClaimException: Expected iss claim to be .../A/, but was .../B/` — an HTTP 400 naming neither the workspace nor the fix. `LiveCredential` now honours `AZURE_TENANT_ID` |
| <a id="lw-c041"></a>LW-C041 | Creating a **catalog** via bundle or SQL on a Default Storage metastore | **Not supported** | 2026-08-01 | `INVALID_STATE: Metastore storage root URL does not exist`. Needs the UI or an explicit `MANAGED LOCATION`, so the catalog is a documented prerequisite. |
| <a id="lw-c063"></a>LW-C063 | Databricks Apps runtime: Python 3.11 and Node.js 22, no .NET | **Documented** | 2026-11-28 | [Platform evidence](release-evidence/databricks-platform-2026-11-28.md), "Apps runtime" row. No .NET runtime or custom container is documented. |
| <a id="lw-c064"></a>LW-C064 | AI/BI external embedding filters rows per viewer through `__aibi_external_value` | **Documented** | 2026-11-28 | [Platform evidence](release-evidence/databricks-platform-2026-11-28.md), "External embedding filters per viewer" row. LW-C038 verified that the tenant reaches `external_value`; filtering inside a dashboard dataset has not been run here. The documentation does not state a price for external viewers. |
| <a id="lw-c065"></a>LW-C065 | OAuth custom identity claims for row filtering under app identity | **Documented** | 2026-11-28 | [Platform evidence](release-evidence/databricks-platform-2026-11-28.md), "OAuth custom identity claims" row. LakeWright's credentials do not mint the claim; nothing here has run it. |

### 2026-11-07 installed-package AWS check

A package-only .NET 8 consumer of public `LakeWright.Core` and `LakeWright.Databricks` 3.0.0 ran
against an existing AWS Free Edition workspace with a serverless PRO warehouse. It used the
`samples.bakehouse` catalog/schema metadata and a CLI user OAuth token forwarded through a custom
`TokenCredential` adapter. This did not exercise `DefaultAzureCredential`, managed identity or
LakeWright's machine-to-machine OAuth route.

The bound statement `SELECT :value AS value` returned one row containing `1`. HTTP diagnostics
observed one SQL POST and one total SQL request. A wrong-tenant request was denied by the consumer's
trusted membership resolver; rejection of a default statement was also checked. Each added zero SQL
requests. No resources, grants or tables were changed.

This verifies the installed-package statement path and those local rejection boundaries. It does
not verify separate server-side tenant schemas or RBAC, billing attribution, jobs, Azure hosting,
broader live regressions, cross-cloud parity or .NET 10 live execution. The older capability rows
above retain their original scope and dates.

## Clouds

| ID | Cloud | Status | Notes |
|---|---|---|---|
| <a id="lw-c042"></a>LW-C042 | Azure | **Verified** for the dated rows above | The hosted reference application remains unverified. |
| <a id="lw-c043"></a>LW-C043 | AWS | **Verified** for the bounded 2026-11-07 check above | Public Core/Databricks 3.0.0 on .NET 8 with a custom CLI user OAuth adapter. OAuth federation and LakeWright M2M remain unverified. |
| <a id="lw-c044"></a>LW-C044 | GCP | Unverified | OAuth federation documented; no live check recorded |
| <a id="lw-c045"></a>LW-C045 | Free Edition | **Verified** for the bounded user OAuth check above | The 2026-08-01 assessment recorded service-principal OAuth as unavailable based on the account-level API limitation and a community response. On 2026-11-07, [LakeSpeak's hosted M2M run](https://github.com/ivanvyd/LakeSpeak.NET/actions/runs/42409476224) passed nine live tests in the same Free Edition workspace using an existing client ID/secret pair. LakeWright's M2M route was not retested. The offline sample needs no workspace. |

## .NET

| ID | Capability | Status | Date |
|---|---|---|---|
| <a id="lw-c046"></a>LW-C046 | Interpolated SQL is a compile error | **Verified** | 2026-07-31, [spike 02](planning/spike-02-interpolation-guard.md) |
| <a id="lw-c047"></a>LW-C047 | Cross-tenant resolution refused, against real Postgres | **Verified** | 2026-07-31, [testing isolation](guides/testing-isolation.md) |
| <a id="lw-c048"></a>LW-C048 | Isolation suite fails when isolation is broken | **Verified** | 2026-07-31, same |
| <a id="lw-c049"></a>LW-C049 | `audit_events` refuses update and delete in application code | **Verified** | 2026-07-31 |
| <a id="lw-c050"></a>LW-C050 | `audit_events` refuses `ExecuteDelete`/`ExecuteUpdate` at the database | **Verified** | 2026-07-31, as the restricted application role |
| <a id="lw-c051"></a>LW-C051 | Populated `audit_events` migration, monthly retention, ACL/RLS preservation and rollback | **Verified** | 2026-09-01, PostgreSQL 17 through `AuditPartitionTests`; scheduling the maintenance command remains an adopter operation. ADR 0020. |
| <a id="lw-c052"></a>LW-C052 | An operation is invisible to a tenant that does not own it | **Verified** | 2026-07-31 |
| <a id="lw-c053"></a>LW-C053 | EF Core model on Lakebase | Unverified | Standard Postgres only; no Lakebase-specific feature is used |

## Application tier

These rows were run by the repository's tests and sample, against PostgreSQL 17 in Testcontainers
or Docker, at `4898c2a` plus the Signalboard SDK image fix. Databricks is a test double in all of
them, so none of them is evidence about a live workspace.

| ID | Capability | Status | Date | Evidence |
|---|---|---|---|---|
| <a id="lw-c054"></a>LW-C054 | Operation worker: crash reconciliation and resumed polling | **Verified locally** | 2026-11-28 | `OperationWorkerTests`, including `A_worker_that_stopped_polling_resumes_rather_than_resubmitting` and `A_rejected_timeout_cancellation_preserves_the_running_operation_for_reconciliation`. A live job interrupted by a crash has not been run. |
| <a id="lw-c055"></a>LW-C055 | Per-tenant compute limit: at most `OperationWorker:MaxInFlightPerTenant` operations in flight (default 4) | **Verified locally** | 2026-11-28 | `OperationClaimTests`: `A_tenant_at_its_ceiling_is_skipped_rather_than_failed`, `Completing_work_frees_the_ceiling_for_the_next_operation`, `One_tenants_backlog_does_not_starve_another`. It caps concurrency; currency budgets are not built. |
| <a id="lw-c056"></a>LW-C056 | Tenant middleware and role policies (Viewer, Member, Admin) | **Verified locally** | 2026-11-28 | `PermissionMatrixTests` compares the committed permission matrix with the routing table; `HttpIsolationTests`. In the running sample a Viewer read an operation (200) and was refused starting one (403). |
| <a id="lw-c057"></a>LW-C057 | Operations API client idempotency | **Verified locally** | 2026-11-28 | `OperationIdempotencyTests`. In the running sample a repeated `Idempotency-Key` returned the same operation, and the same key with another kind returned 422. |
| <a id="lw-c058"></a>LW-C058 | Telemetry instruments with low-cardinality tags | **Verified locally** | 2026-11-28 | `TelemetryTests` and `DatabricksTelemetryTests`: operation started and completed counters, queue wait, refused tenants counted, and no tenant instrument on the statement meter. Export to a collector is LW-C008. |
| <a id="lw-c059"></a>LW-C059 | `LakeWright.Multitenancy` does not reference `LakeWright.Databricks` | **Verified locally** | 2026-11-28 | `DependencyDirectionTests`. |
| <a id="lw-c060"></a>LW-C060 | Signalboard runs from `docker compose up` with no .NET SDK, workspace or configuration | **Verified locally** | 2026-11-28 | Docker 29.8.1: landing page 200, and another organization's operation answered 404. From 2026-11-07 the image did not build, because its SDK image carried 10.0.401 and `global.json` pins 10.0.400. No CI job builds the image; `scripts/check-sdk-pins.py` fails CI on that mismatch. |
| <a id="lw-c061"></a>LW-C061 | Published-package quickstart: a .NET 8 consumer of the released packages, offline | **Verified locally** | 2026-11-28 | `scripts/verify-package-quickstart.py` in the [main CI run](https://github.com/ivanvyd/LakeWright.NET/actions/runs/46142150789) at `4898c2a`: exact 3.1.0 packages, fresh package cache, nuget.org only, no project references. |
| <a id="lw-c062"></a>LW-C062 | Built on `Microsoft.Azure.Databricks.Client`, MIT-licensed | **Verified locally** | 2026-11-28 | `Directory.Packages.props` pins 2.9.3, and its nuspec declares the `MIT` license expression. LakeWright calls its Statement Execution and Jobs APIs (LW-C012, LW-C019). Its Unity Catalog coverage is the package's own description. |

## Bounded load evidence

The [2026-11-07 CI run](https://github.com/ivanvyd/LakeWright.NET/actions/runs/42392295904)
passed the configured 50 RPS, 30-second smoke profile and its accounting checks. This profile uses
in-process ASP.NET Core and disposable PostgreSQL. It does not call a live Databricks workspace.
The [load guide](../scripts/load/README.md) records the local sustained target and interpretation
limits. This smoke result does not establish sustained production capacity, remote TLS/ingress,
multi-host fairness or completion of real Databricks jobs.

## Known gaps

- Billing attribution is not live-verified in the development workspace. The implementation ships
  as an opt-in. Earlier verification was blocked by missing access to `system.billing.usage` and
  `system.billing.list_prices`; elapsed-time attribution remains the default. See the
  [billing runbook](guides/billing-cost-attribution.md), T5 in the threat model and ADR 0012.
- No reference deployment *executed*. The Bicep template in `infra/azure-container-apps/`
  compiles; the workflow in `.github/workflows/deploy-azure.yml` is in place; no one has run a
  deploy with it, so the ingress half of encryption in transit and the managed identity path in
  a hosted process are both unproven outside the spikes. ADR 0014.
- Live integration tests: `Category=Live` in the isolation suite, plus the four spikes. They are
  excluded from CI because they need a workspace and cost money.
