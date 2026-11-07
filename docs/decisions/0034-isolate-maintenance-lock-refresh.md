# ADR 0034: Isolate maintenance lock refresh from repository writes

Status: accepted

Date: 2026-11-07

## Context

Dependabot updates central package versions without regenerating NuGet lock files.
Refreshing those files requires restore and build, which can execute code from the bump branch.
Running that code in a job with repository-write permission exposes the write credential.

## Decision

The manual workflow runs from the default branch and takes a `dependabot/nuget/*` branch as input.
Trusted scripts come from the workflow commit. Three fresh runners separate the work:

- A read-only resolver validates the branch and resolves its exact branch ref to an immutable SHA.
- A read-only candidate runner checks out that SHA, refreshes locks, restores in locked mode and
  builds. Its outputs carry untrusted lock data, and cannot supply the writer's trusted source SHA.
- A writer receives the SHA directly from the resolver and executes only trusted workflow code.

The bounded payload has an exact schema, matching branch and base SHA, and the complete set of
existing tracked regular `packages.lock.json` paths. It carries base64-encoded lock bytes.
Validation rejects unexpected files, traversal, symlinks, malformed JSON and invalid lock structures.
Limits are 16 MiB per payload, 1 MiB per decoded lock, 128 files and 512 characters per path.

The writer fetches Git objects into a new bare repository with an empty template. It constructs an
index, tree and commit without checking out the target or loading its code, configuration, hooks,
attributes or build outputs. A matching tree produces no commit or push. A changed branch before
the writer fetches is rejected; a normal non-force push rejects concurrent advancement afterward.

## Consequences

Candidate execution has read-only repository permission. The fresh writer alone has
`contents: write`; it uses the workflow token and needs no PAT or `actions: write` permission.
Push events from `GITHUB_TOKEN` do not start workflows. PR runs can await approval; after a changed
push, select 'Approve workflows to run' for pending runs. If expected checks are absent, close and
reopen the PR with your own identity. Rerunning an older run retains its original SHA.
See [workflow triggers](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/trigger-a-workflow) and
[rerun semantics](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/re-run-workflows-and-jobs).

Lock validation checks supported format versions, dependency structure and content-hash encoding.
It does not solve NuGet version ranges or establish that a dependency is suitable. New lock formats
require review. A compromised candidate runner can propose valid lock data within this boundary.

The design trusts the default-branch workflow, pinned Actions, GitHub runner isolation, scoped
tokens and artifact delivery. Payload limits apply after artifact download. Local Git fixtures
exercise data validation, commit boundaries and races; hosted token, runner and artifact behavior
still requires verification on GitHub.
