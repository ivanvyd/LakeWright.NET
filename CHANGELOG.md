# Changelog

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versioning follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Before 1.0, minor versions may contain breaking changes. Each one is listed here with a migration
note.

## [Unreleased]

### Added

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

- `--locked-mode` inside an XML comment made `Directory.Build.props` unloadable, so no project
  built.
- The bundle CI job pointed at a directory git cannot track, failing before its own guard ran.
- CodeQL, dependency review and Scorecard are gated on repository visibility. They require Advanced
  Security on private repositories and were failing every run.

### Notes

- The project was named `LakeSaaS.NET` during planning and renamed to `Lakewright.NET` before any
  code or package was published. The former name misdescribed a build kit as a product.
