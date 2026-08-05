# Governance

## Current state

One maintainer, [@ivanvyd](https://github.com/ivanvyd). Decisions are made by that person after
public discussion in issues.

Stating this plainly is deliberate. A solo project that publishes a governance document describing
committees and voting procedures is describing something that does not exist, and contributors work
that out quickly.

## How decisions get made

Anything that changes the architecture, the public API surface, or a documented guarantee needs an
architecture decision record in [`docs/decisions`](docs/decisions) before the code merges. The record
states the context, the decision, and the consequences including the ones we dislike.

Discussion happens in the issue or pull request. If a decision is reversed later, the original ADR is
marked superseded rather than deleted, because the reasoning that turned out wrong is the useful part.

## Becoming a maintainer

There is no committee to join and no fixed threshold. The realistic path: land several non-trivial
pull requests, review other people's work, and demonstrate judgement about what belongs in the
project and what does not. At that point commit access is offered.

If a second maintainer joins, this document changes to describe two maintainers and how they resolve
disagreement. It will not describe a process before there are people to run it.

## Scope control

The most likely way this project fails is scope growth into a generic .NET SaaS framework. To
prevent that, a proposed feature needs to answer yes to at least one:

- Does it address a problem specific to running a multi-tenant product on Databricks?
- Does it belong to the tenant isolation, asynchronous operation, or cost attribution core?

If the honest answer is that it would be useful in any SaaS product, it belongs in a dependency or in
the reader's own codebase, not here. Declining useful things is how this stays maintainable by one
person.

## Releases

Semantic versioning. Pre-1.0 the minor version may break. Breaking changes are listed in
[CHANGELOG.md](CHANGELOG.md) with a migration note.

Packages publish to nuget.org with a prerelease suffix, so `dotnet add package` needs `--prerelease`
and nobody acquires one by accident. Dropping the suffix commits to the API surface, which needs
adopters to commit to. A package that exists solely because a folder existed is still a maintenance
liability — [ADR 0010](docs/decisions/0010-publish-prerelease-packages.md) records why publishing
nothing turned out to cost more than it saved.

## Inactivity

If the maintainer becomes unresponsive for 6 months, the README will be updated to say so. An
unmaintained project that says it is unmaintained is more useful than one that pretends otherwise.
