# ADR 0034: Isolate maintenance lock refresh from repository writes

Status: accepted

Date: 2026-11-07

## Context

Dependabot updates central package versions without regenerating NuGet locks. Restore and build
can execute bump-branch code; a job with repository-write permission exposes its write credential.

## Decision

The manual workflow runs from the default branch and takes a `dependabot/nuget/*` branch as input.
Trusted scripts come from the workflow commit. Three fresh runners separate the work:

- A read-only resolver validates the branch and resolves its exact branch ref to an immutable SHA.
- A read-only candidate runner checks out that SHA, refreshes locks, restores in locked mode and
  builds. Its outputs carry untrusted lock data, and cannot supply the writer's trusted source SHA.
- A writer receives the SHA directly from the resolver and executes only trusted workflow code.

Set workflow-level `cache-mode: none`, with no job overrides, to deny cache restores and saves.
Runtime cache tokens are independent of `contents` permissions; GitHub enforces their scope.

The exact payload schema carries base64 lock bytes, matching branch/base SHA and every existing
tracked regular `packages.lock.json` path. Reject extra files, traversal, symlinks and malformed lock JSON.
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
See [workflow triggers](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/trigger-a-workflow) and [rerun semantics](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/re-run-workflows-and-jobs).

Validation checks supported lock formats, dependency structure and content-hash encoding. It does
not solve NuGet ranges, judge dependency suitability or attest an honest candidate build.
New lock formats require review; compromised candidate code can propose valid lock data.

The design trusts default-branch code, pinned Actions, hosted runner isolation, and GitHub's
[scoped cache tokens](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax#cache-mode), job tokens and artifact delivery.
Payload size checks apply after download. Local checks verify policy and Git data handling;
hosted cache denial, token scope and runner/artifact behavior require verification on GitHub.
