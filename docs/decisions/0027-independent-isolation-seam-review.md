# ADR 0027: Require independent review of tenant-isolation seams

Status: accepted
Date: 2026-09-02

## Context

The abandoned shared-schema strategy proposal would have decided which warehouse rows an
application could read. A small mistake in such a control can defeat every caller's authorization
model. Passing unit tests, package checks, and a review by the author are necessary evidence, but
are not independent scrutiny of that boundary.

## Decision

Any future change that introduces a tenant-scope strategy, its registration, or its SQL wrapper
requires approval from at least one reviewer who did not author that change before a minor or major
release containing it is promoted. The pull request or release evidence must identify the reviewer,
the reviewed commit, and the tenant-isolation test command used as evidence.

The current 2.0.0 candidate removes the unsafe generic shared-schema feature before publication,
so this gate is not a waiver for it. Any future reintroduction remains blocked until the independent
review is recorded.

## Consequences

- Review records become auditable release evidence instead of a convention hidden in a hosting
  provider's UI.
- An author cannot self-certify a new isolation strategy for a public minor or major release.
- Patch releases that do not change these seams are unaffected; a patch that changes one follows
  the same review rule regardless of its version number.
