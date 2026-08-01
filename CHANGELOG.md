# Changelog

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versioning follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Before 1.0, minor versions may contain breaking changes. Each one is listed here with a migration
note.

## [Unreleased]

### Added

- `Lakewright.AspNetCore`: tenant resolution middleware that turns the organization in a route into
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
- `Lakewright.Core`: `TenantId`, `TenantContext` and the resolver contract. `TenantContext` has no
  public constructor, so holding one means a membership check ran.
- `Lakewright.Databricks`: `TenantScopedStatement`, typed `StatementParameter` factories, and
  `StatementOutcome`, which unifies the client library's two failure modes. Interpolated SQL is a
  compile error.
- `Lakewright.Multitenancy`: organization, membership and audit-event model on EF Core and Npgsql,
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

### Fixed

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

- The project was named `LakeSaaS.NET` during planning and renamed to `Lakewright.NET` before any
  code or package was published. The former name misdescribed a build kit as a product.
