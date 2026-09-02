# ADR 0027: Require independent review of tenant-isolation seams

Status: accepted
Date: 2026-09-02

## Context

`ITenantScopeStrategy`, `ProjectedColumnScope`, and `ScopeTableScope` decide which warehouse rows
an application can read. They are deliberately library-owned controls: a small mistake in any one
of them can defeat every caller's authorization model. Passing unit tests, package checks, and a
review by the author are necessary evidence, but are not independent scrutiny of that boundary.

## Decision

Any change to a tenant-scope strategy, its registration, or its SQL wrapper requires approval from
at least one reviewer who did not author that change before a minor or major release containing it
is promoted. The pull request or release evidence must identify the reviewer, the reviewed commit,
and the tenant-isolation test command used as evidence.

The current 2.0.0 candidate has no recorded independent reviewer yet. That is an explicit release
gate, not an assertion that the automated tests replace the review.

## Consequences

- Review records become auditable release evidence instead of a convention hidden in a hosting
  provider's UI.
- An author cannot self-certify a new isolation strategy for a public minor or major release.
- Patch releases that do not change these seams are unaffected; a patch that changes one follows
  the same review rule regardless of its version number.
