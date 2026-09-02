# Contributing

## Before you write code

Open an issue first for anything beyond a typo or an obvious bug. This project declines useful
features on purpose (see [GOVERNANCE.md](GOVERNANCE.md#scope-control)), and finding that out after
you have written the code is a bad experience for both of us.

Good first issues are labelled `good-first-issue` and are genuinely scoped: each states the file to
change, the expected behaviour, and how to verify it.

## Protect confidential context

This is a public repository. Never include real client or customer names, project codenames,
workspace or catalog identifiers, private document paths, internal domains, or other identifying
details without explicit approval for that disclosure. Use neutral descriptions and placeholders
in source, tests, examples, documentation, screenshots, commit messages, pull requests, issues,
release notes, CI output, and generated package documentation.

Before opening a pull request or publishing a package, inspect both the human-written change and
the generated artifacts. A harmless-looking XML comment can become public API documentation inside
a package, and a private identifier in a commit message remains visible even after the file changes.

Only the repository owner can approve an exception. Obtain that approval in writing through a
private channel before creating a public branch, issue, or pull request. The approval must name the
exact value and public surfaces it covers. Record only that scoped approval exists in public
metadata; do not copy the private context there. The repository owner updates the private CI
configuration when an approved disclosure requires it. Contributors must not weaken or bypass the
confidentiality check.

Raster images require a separate visual review because text can be embedded in pixels. Existing
public images are approved by exact SHA-256 hash in `scripts/approved-public-images.sha256`. A new
or changed image must be inspected at full resolution, added to that file, and accompanied by the
`confidentiality-image-reviewed` pull-request label applied by the repository owner. Applying the
label records that the bytes were reviewed; it does not approve disclosure of any real identifier.
The workflow removes that label whenever the pull request or issue changes, including new comments,
so every approval applies only to the public state the owner actually inspected. Images embedded in
GitHub discussion are blocked by the same owner-review label rather than fetched by a secret-bearing
runner.

Changes to the scanner, its private-policy allowlist, or any workflow that enforces confidentiality
also require the repository owner to apply `confidentiality-control-reviewed`. The trusted workflow
removes that label on every subsequent content change. This keeps a previously approved control
revision from authorizing its own replacement.

The confidentiality preflight runs before build, test, or documentation scripts so candidate
source cannot first disclose an identifier through public CI output. Code from external forks is
scanned as data by a trusted workflow and is not executed in the repository's CI context.

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

The confidentiality check is the narrow exception: a trusted scanner from the default branch reads
fork source and pull-request metadata as data. It never runs fork scripts, restores packages, builds
code, or grants a write token. The private denylist therefore remains unavailable to the submitted
code while the required check can still block a disclosure before merge.

## Security

Do not report vulnerabilities through issues or pull requests. See [SECURITY.md](SECURITY.md).
