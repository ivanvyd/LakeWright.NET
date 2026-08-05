# ADR 0011: Brokered access as two modules, named for what they do

Status: accepted
Date: 2026-08-05

## Context

Two Databricks surfaces face an ISV's *own* customers rather than Databricks' users, and neither
ships tenancy:

- **AI/BI external embedding.** Databricks mints a browser-safe token whose `external_value` becomes
  `__aibi_external_value` in the dashboard's SQL. Whoever chooses that value chooses which tenant's
  rows a viewer sees.
- **The Genie Conversation API.** It takes no filter, no viewer identity and no row predicate. A
  question is answered against whatever tables the agent was curated with.

Research found no .NET implementation of either, and no reference implementation in any language
that scopes them per tenant.

## Decision

**Two projects, `LakeWright.Embedding` and `LakeWright.Conversations`, each referencing only
`LakeWright.Core`.** Neither takes the Databricks client library: both are OAuth and REST, so an
adopter who wants a dashboard token does not acquire a SQL stack to get one. A test enforces that
rather than a convention.

**Two rather than one, because they share nothing.** Embedding mints a downscoped browser token
through a three-leg exchange under a client secret. Conversations is a server-side call under a
`TokenCredential`, where the scoping problem is which agent, not which token. A single module would
be named for one half of its contents.

**The scoping key is never a parameter.** Both APIs take a `TenantContext`, which only membership
resolution can produce, and derive the boundary from it — `external_value` from the tenant id, the
Genie agent from a configured map. A signature accepting a caller-supplied string would move the
isolation boundary out of the library and into every call site.

**Neither is named `Genie`.** `LakeWright.Conversations` says what the module does without putting a
second Databricks product name into a permanent package identifier.

## Consequences

**A .NET developer searching nuget.org for "Genie" will not find this.** That is the cost, and it is
real: discoverability was the entire argument for publishing at all (ADR 0010). It is paid because
the trademark position is still unanswered — there is no public Databricks trademark policy, and the
letter this project intends to send states that "Databricks" appears only as descriptive text and as
the `LakeWright.Databricks` package name. Publishing `LakeWright.Genie` would make that sentence
false on the day it was sent, and package identifiers cannot be withdrawn. The Genie name appears in
the description, the XML docs and the README, where it is plainly nominative.

**Embedding needs a client secret, and nothing else here does.** ADR 0006 committed to secretless
authentication, and this is the exception: Databricks documents no other credential for the token
exchange. It is confined to one module, so the rest of the system keeps the property.

**One Genie agent per tenant is an operational cost.** Curating an agent is not free, and this
design multiplies that by the tenant count. The alternative — one agent, filtered per request — is
not available: the API has no such parameter. Refusing an unmapped tenant is the honest failure.

**Both surfaces are Public Preview.** Risk three in the register is that preview churn outruns a
solo maintainer, and this change adds two preview dependencies. The compatibility matrix carries the
verification date for each, which is what turns churn into a stale row rather than a silent lie.
