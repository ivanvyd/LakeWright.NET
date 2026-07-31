# Contributing

## Before you write code

Open an issue first for anything beyond a typo or an obvious bug. This project declines useful
features on purpose (see [GOVERNANCE.md](GOVERNANCE.md#scope-control)), and finding that out after
you have written the code is a bad experience for both of us.

Good first issues are labelled `good-first-issue` and are genuinely scoped: each states the file to
change, the expected behaviour, and how to verify it.

## Running it

You do not need a Databricks account to work on this. You do need Docker, because the isolation
suite runs against a real PostgreSQL container rather than an in-memory substitute.

```bash
dotnet build                                      # warnings are errors
dotnet test --filter "Category!=Live"             # everything that needs no cloud account
dotnet test --filter "Category=TenantIsolation"   # the suite the rest of it rests on
dotnet format --verify-no-changes
```

There is no application to run yet. The web tier, the Declarative Automation Bundle and the
Signalboard sample arrive over the milestones in [ROADMAP.md](ROADMAP.md), and this section grows
with them.

Tests tagged `Category=Live` need a real workspace and create real resources. None exist yet; the
live verification done so far is recorded in [docs/compatibility.md](docs/compatibility.md).

## Standards the build enforces

Warnings are errors. Code style is enforced at build time, not in review, so `dotnet build` tells you
about formatting before a human does.

Two rules matter more than the rest:

- **Never interpolate into SQL.** Passing an interpolated string to `TenantScopedStatement.Create`
  does not compile. Values go in as `StatementParameter` arguments; catalog and schema come from the
  tenant context and are validated as identifiers.
- **Flow the `CancellationToken`.** Every Databricks call is network I/O. `CS1998` and `CA2016` are
  errors here.

Anything touching tenant resolution, the query layer or authentication needs a case in the
isolation suite, and you should break the thing it guards and watch it fail before trusting it. See
[docs/guides/testing-isolation.md](docs/guides/testing-isolation.md) for why: one control in this
repository already shipped with a test that passed while the control did nothing.

## Pull requests

- One logical change. A refactor and a fix in one pull request will be asked to split.
- Tests for the behaviour you changed. A bug fix without a test that fails before it is not a fix.
- Anything touching tenant resolution, the query layer, or authentication needs a case in the
  cross-tenant isolation suite.
- Architecture or public API changes need an ADR in `docs/decisions`, in the same pull request.
- Commits are signed off (DCO): `git commit -s`. There is no CLA.

Draft pull requests are welcome early. Say what you are unsure about and it will get looked at.

## What gets declined

- Features useful in any SaaS product regardless of Databricks. They belong in a dependency.
- New abstractions without two existing call sites that need them.
- Dependencies that replace something the standard library or an existing dependency already does.
- Anything that makes the local-development path require a cloud account.

## Continuous integration on forks

Pull requests from forks run build, tests and `bundle validate` in schema-only mode. They do not
receive repository secrets, so jobs needing Databricks credentials are skipped rather than failed. A
maintainer runs those before merge. This is deliberate: a workflow that hands secrets to fork code is
an exfiltration path, not a convenience.

## Security

Do not report vulnerabilities through issues or pull requests. See [SECURITY.md](SECURITY.md).
