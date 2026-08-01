# Changelog

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versioning follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Before 1.0, minor versions may contain breaking changes. Each one is listed here with a migration
note.

## [Unreleased]

### Added

- The Signalboard sample: two organizations, three people, a Blazor dashboard and the same API from
  a terminal. `docker compose up -d` plus `dotnet run`, no Databricks account needed.
- Client idempotency on `POST /organizations/{organizationId}/operations`. Send an
  `Idempotency-Key` and a retried request returns the original operation instead of starting a
  second Databricks run. Unique per organization and principal; `422` if the key is reused with a
  different `kind`, `400` above 200 characters.
- An audit trail that is written rather than only guarded. Starting an operation, completing one,
  and being refused a tenant each write to `audit_events` in the same transaction as the action.
  The refusal matters most: it answers 404, so the row is the only trace of the attempt.
- `OperationWorker:MaxInFlightPerTenant`, capping the Databricks compute one tenant can hold at
  once, and fair claim ordering by in-flight count before age. Together these close threat T6 and
  bound T5.
- `AddLakeWrightDatabricks`, so tenancy, authorization and the operations API can be adopted
  without a Databricks workspace.

- `LakeWright.AspNetCore`: tenant resolution middleware that turns the organization in a route into
  a resolved context or a 404, role policies over `MembershipRole` with a fallback policy so
  endpoints are protected by omission, and the operations API (`202 Accepted` plus a poll endpoint).
  It deliberately registers no identity provider — that choice belongs to the adopter.
- `docs/compliance/permissions.md`, generated from the routing table by a test that fails when the
  committed copy drifts from the code.
- Asynchronous operations end to end (ADR 0005): a Lakeflow Jobs submitter, an `OperationWorker`
  `BackgroundService` that claims, submits, records, polls and completes, and a reconciliation pass
  that recovers a run orphaned by a worker crash. Reconciliation re-submits with the original
  idempotency token rather than searching by tag, because the Jobs API does not expose the token on
  a run.
- `Category=Live` tests exercising the Databricks clients against a real workspace, excluded from CI.
- `LakeWright.Core`: `TenantId`, `TenantContext` and the resolver contract. `TenantContext` has no
  public constructor, so holding one means a membership check ran.
- `LakeWright.Databricks`: `TenantScopedStatement`, typed `StatementParameter` factories, and
  `StatementOutcome`, which unifies the client library's two failure modes. Interpolated SQL is a
  compile error.
- `LakeWright.Multitenancy`: organization, membership and audit-event model on EF Core and Npgsql,
  with a resolver that reads membership from the database and never from a token claim.
- Cross-tenant isolation suite, shown to fail when isolation is deliberately broken
  (`docs/guides/testing-isolation.md`).
- `docs/compatibility.md`: what has been verified against a live workspace, and what has not.
- `docs/compliance/soc2-mapping.md`: technical controls mapped to Trust Services Criteria, and the
  gaps the adopting organisation owns.
- Research and planning package under `docs/planning`, covering the ecosystem survey, competitor
  analysis, tenant isolation matrix, architecture options and the eight-week delivery plan.
- Open-source baseline: Apache-2.0 license, contribution guide, security policy, governance model.
- Build configuration targeting .NET 10 with warnings as errors and deterministic output.

### Security

- `audit_events` is now append-only at the database as well as in application code.
  `DatabaseHardening.ApplyAsync` grants the application role select and insert only, closing the
  `ExecuteUpdate`/`ExecuteDelete`/raw-SQL gap that no C# guard can reach. The role must not own the
  tables, since an owner keeps privileges `REVOKE` does not remove.
- `OperationStore` binds a Databricks statement identifier to the tenant that created it, and every
  lookup filters on the tenant. A statement identifier obtained from a log line is no longer
  sufficient to read another tenant's results.

Found by an adversarial review of the first implementation, before any release.

- `TenantContextFactory` was public, so any caller could construct a `TenantContext` for any tenant
  with no membership check and query that tenant's schema. Now `internal`, visible only to the
  resolver assembly and the isolation suite.
- The `audit_events` append-only guard covered one of four save paths. The synchronous overload and
  the two-argument async overload both bypassed it. `ExecuteUpdate`, `ExecuteDelete` and raw SQL
  remain uncatchable in C# and are recorded as an open gap rather than claimed as covered.
- `IStatementExecutor.GetAsync` and `CancelAsync` accepted a bare statement id while the interface
  documentation claimed every method was tenant-scoped. They now require a `TenantContext`, and the
  documentation states where ownership is actually enforced.
- The Unity Catalog identifier pattern used `$`, which in .NET also matches before a trailing
  newline, so `tenant_a\n` validated. Now `\z`.

### Changed

- `AddLakeWright` no longer registers the Databricks clients or validates `DatabricksOptions`. Call
  `AddLakeWrightDatabricks(configuration)` for those, and `AddLakeWrightOperationWorker` now takes
  the configuration too. **Migration:** add both calls if you use Databricks; do nothing if you
  adopted only the tenancy tier, which previously could not start without a workspace configured.
- `IDatabricksTokenSource` is replaced by `Azure.Core.TokenCredential`. **Migration:** register
  `DefaultAzureCredential` on Azure, or wrap your existing token source in a `TokenCredential`.
- `OperationWorkerOptions.JobId` is replaced by `Jobs`, a map from `Operation.Kind` to job id.
  **Migration:** change `"OperationWorker": { "JobId": 123 }` to
  `"OperationWorker": { "Jobs": { "analysis": 123 } }`. A kind with no entry now fails the
  operation saying so, rather than running whichever job was configured.
- `IJobSubmitter`, `RunOutcome` and `TenantScopedJobRun` moved from `LakeWright.Databricks` to
  `LakeWright.Core` (namespace `LakeWright.Core.Jobs`), so `LakeWright.Multitenancy` no longer
  references the Databricks integration. **Migration:** update the `using`.
- `OperationStore.CreateAsync` takes a client request id, and `ClaimNextAsync` takes a per-tenant
  ceiling.

### Fixed

- The Databricks bearer token was read once at startup and baked into a client that lives for the
  process. Under the managed identity ADR 0006 recommends, every Databricks call would fail 401
  permanently about an hour after boot, with nothing to detect it.
- An ordinary rolling deploy stranded in-flight operations as `Running` forever. Reconciliation
  claimed only rows with no run id, and once the id was recorded the only thing watching was a poll
  loop that exits on the shutdown token. Reconciliation now reclaims any uncompleted stale row and
  resumes the poll.
- Audit logging was documented and mapped to SOC 2 CC7.2 while nothing wrote a row. The
  append-only tests inserted their own synthetic rows, so they passed regardless.
- An endpoint added without its own `RequireAuthorization` was reachable by a member at any role:
  the fallback policy checks authentication and tenant resolution checks membership, neither checks
  role. The tenant route group now carries a Viewer floor, and the permission-matrix test fails on
  any tenant-scoped route with no role policy.
- The sample's documented `docker compose up -d` start produced an application that answered 500 to
  every request. Compose creates the database, and `EnsureCreatedAsync` does nothing when one
  already exists, so no tables were created.
- `OperationStore` read the system clock while the worker took an injected `TimeProvider`.
- Every successful Databricks query returned zero rows. The statement executor defaulted to
  `EXTERNAL_LINKS` while reading `DataArray`, which only `INLINE` populates. Inline is now the
  default with a row limit, and an external-link result is a distinct `LargeResult` outcome rather
  than a `Success` with an empty row list. Found by the first live test, invisible to unit tests.
- `--locked-mode` inside an XML comment made `Directory.Build.props` unloadable, so no project
  built.
- The bundle CI job pointed at a directory git cannot track, failing before its own guard ran.
- CodeQL, dependency review and Scorecard are gated on repository visibility. They require Advanced
  Security on private repositories and were failing every run.

### Notes

- The project was named `LakeSaaS.NET` during planning and renamed to `LakeWright.NET` before any
  code or package was published. The former name misdescribed a build kit as a product.
