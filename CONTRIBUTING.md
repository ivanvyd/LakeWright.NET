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

To run something rather than test it, start the sample. It needs Docker and nothing else:

```bash
cd samples/Signalboard
docker compose up -d postgres
dotnet run
```

`docker compose up` on its own builds and runs the application too, which is the path to check
before opening a pull request that touches the sample.

Then open <http://localhost:8080> and sign in as one of the three seeded people. See
[samples/Signalboard/README.md](samples/Signalboard/README.md). The Databricks side is separately
deployable — see [deploying-databricks.md](docs/guides/deploying-databricks.md).

### If you cloned before 2026-08-01, on Windows or macOS

The projects were renamed from `Lakewright.*` to `LakeWright.*`. On a case-insensitive filesystem
git updates its index but leaves the directories at their old casing, so `dotnet build` keeps
working while a Docker build fails with types it cannot find — the container filesystem is
case-sensitive and the project references no longer resolve.

Check every directory git tracks, not just `src` — the projects live under `src`, `tests` and
`samples`, and a remedy naming only one of them leaves the others stale.

```bash
ls src tests samples
```

Anything spelled `Lakewright.*` rather than `LakeWright.*` is stale. Do not reach for a scripted
`[ -e "$path" ]` check: the shell asks the filesystem, and the filesystem is the thing being
case-insensitive, so it answers yes for a directory whose real name differs only in case. Reading
the listing is both simpler and correct.

Fix each one by renaming through a temporary name, which is what makes a case-only rename take
effect at all:

```bash
mv tests/Lakewright.TenantIsolation.Tests tests/tmp__
mv tests/tmp__ tests/LakeWright.TenantIsolation.Tests
```

A fresh clone is unaffected, which is why CI never sees this.

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
