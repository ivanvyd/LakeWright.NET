# Documentation

## Start here

| If you want to | Read |
|---|---|
| Get it running | [Getting started](guides/getting-started.md) |
| Know whether this project is for you | [Product thesis](planning/01-product-thesis.md), including the strongest argument against it existing |
| Understand the architecture | [Architecture](planning/03-architecture.md) |
| Understand why tenant isolation works the way it does | [Tenant model](planning/04-tenant-model.md), then [ADR 0002](decisions/0002-enforce-tenant-isolation-in-the-query-layer.md) |
| Know what actually works against a real workspace | [Compatibility matrix](compatibility.md) |
| Contribute | [CONTRIBUTING.md](../CONTRIBUTING.md) and [Testing isolation](guides/testing-isolation.md) |
| Know what is next, and what is blocked | [Remaining work](planning/06-remaining-work.md) |
| Deploy the Databricks side | [Deploying Databricks](guides/deploying-databricks.md) |
| Assess it for security or compliance | [Threat model](security/threat-model.md), [SOC 2 mapping](compliance/soc2-mapping.md), [Data handling](compliance/data-handling.md) |

## Decisions

Every load-bearing choice has a record in [`decisions/`](decisions), in the format
Status / Date / Context / Decision / Consequences. They are binding: a change that contradicts one
needs a new record superseding it, in the same pull request.

Reversed decisions are marked superseded rather than deleted, because the reasoning that turned out
wrong is the part worth keeping.

## Evidence

[`planning/spike-*.md`](planning) record what was measured against a live Databricks workspace,
including the attempts that failed. Three of them exist because something believed from
documentation turned out to be false when run.

[`planning/research/`](planning/research) holds the raw research behind the plan, with
VERIFIED / RECALLED / COULD-NOT-DETERMINE labels per claim. Read it as evidence, not documentation.

## Conventions

**Nothing is claimed as working unless it was run.** The compatibility matrix separates verified
from documented from not-supported, with dates. If a document and the matrix disagree, the matrix
wins.

**Gaps are listed, not implied.** A compliance or security document that omits what is missing
reads as complete. Every table here marks partial and unimplemented rows explicitly.

**Documents carry the date they were last checked**, because an undated threat model describes
software that no longer exists.
