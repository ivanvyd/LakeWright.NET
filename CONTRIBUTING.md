# Contributing

## Before you write code

Open an issue first for anything beyond a typo or an obvious bug. This project declines useful
features on purpose (see [GOVERNANCE.md](GOVERNANCE.md#scope-control)), and finding that out after
you have written the code is a bad experience for both of us.

Good first issues are labelled `good-first-issue` and are genuinely scoped: each states the file to
change, the expected behaviour, and how to verify it.

## Running it

You do not need a Databricks account to work on most of this.

```bash
docker compose up -d          # Postgres, the mock Databricks server, an OIDC provider
dotnet run --project src/Signalboard/Signalboard.Web
dotnet test                   # unit, contract and isolation suites; no external services
```

The .NET Aspire AppHost is a convenience layer. It is never the only way to run the project, because
that would make a fast-moving dependency a condition of contributing.

Tests that need a live workspace are excluded by default and run with:

```bash
dotnet test --filter Category=Live
```

They require a Databricks profile and they create and destroy real resources. Read
`docs/guides/live-testing.md` before running them against anything you care about.

## Standards the build enforces

Warnings are errors. Code style is enforced at build time, not in review, so `dotnet build` tells you
about formatting before a human does.

Two rules matter more than the rest:

- **Never interpolate into SQL.** Statements are built through the parameterised path, including
  catalog and schema identifiers. An analyzer rule fails the build otherwise.
- **Flow the `CancellationToken`.** Every Databricks call is network I/O. `CS1998` and `CA2016` are
  errors here.

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
