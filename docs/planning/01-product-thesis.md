# Product thesis

Research date: 2026-07-31. Every claim below traces to a source in `docs/planning/02-ecosystem.md`.

## The one-sentence version

Lakewright.NET is the reference architecture and reusable primitives for .NET teams that sell
analytics to customers who are not themselves Databricks customers.

## The thesis changed during research

The original framing was "the missing .NET SDK and the missing reference architecture". Research
killed the first half of that and narrowed the second.

**What the evidence removed from the pitch:**

| Claim we intended to make | Why it is dead |
|---|---|
| "The missing Databricks SDK for .NET" | `Microsoft.Azure.Databricks.Client` exists: MIT, 2.2M downloads, .NET 8/9/10, 18 typed Unity Catalog clients, Statement Execution, Jobs. Building another is negative-value. |
| "Calling Databricks from C# is hard" | It is REST. Several people have blogged it. |
| "Dashboards for external customers are unsolved" | Databricks AI/BI external embedding ships today with no per-viewer fee and row-level security via `__aibi_external_value`. Reimplementing it is negative-value. |
| "Databricks has no app distribution" | False since 2026-06-16 (Marketplace Apps). |

Anyone evaluating the project will find these out in an afternoon. Claiming them costs more
credibility than they buy.

**What survived, and why it is defensible:**

1. **Unity Catalog isolation with a shared service principal does not work, and nothing in .NET
   addresses it.** This is the technical core. See below.
2. **Per-tenant cost attribution.** Databricks bills compute, not viewers. Attributing warehouse
   spend to a tenant so an ISV can price with a known margin is unsolved in every artifact reviewed,
   in every language.
3. **The composition.** No reference architecture in any language assembles tenant lifecycle plus
   Unity Catalog isolation plus token brokering plus cost attribution. Python could have. It has not.
   That is evidence the composition is the work, not the parts.

## The finding the architecture is built around

Row filters and column masks resolve the caller with `session_user()`, which is invoker-evaluated
and returns the *connected* identity. When an ASP.NET backend connects as one service principal for
all tenants, that function returns the same service principal UUID on every request. The filter
predicate is therefore identical for every tenant.

Databricks states the trade-off directly, for Databricks Apps, which is the closest official
analogue to this architecture:

> All actions initiated by the app use the service principal's permissions... However, it doesn't
> support user-level access control. All users who interact with the app share the same permissions
> defined for the service principal, which prevents the app from enforcing fine-grained policies
> based on individual user identity.

�?" Databricks Apps authentication, doc dated 2026-07-21

**App identity means you filter. User identity means Unity Catalog filters.** There is no third
option, and there is no general on-behalf-of flow for an externally hosted service.

This matters because the intuitive design is wrong in a way that is invisible until an audit. A team
that configures row filters and connects with one service principal has built a system that either
shows every tenant nothing, or shows every tenant everything. It will pass a demo.

## Why Databricks will not close this gap

The strongest argument against this project is that Databricks is building the app tier itself:
AppKit (first-party, Apache-2.0, Node + React), App Spaces, Genie App Builder, Marketplace Apps.
All shipped or previewed within fourteen months. All TypeScript and Python.

The rebuttal is structural rather than hopeful. Databricks Apps cannot serve external customers by
its own documentation, and Marketplace Apps requires the customer to be a Databricks customer.
Databricks monetises compute inside *their customer's* account. A .NET ISV runs one account and
resells capacity to businesses that have never heard of a lakehouse. Those two models diverge at the
billing boundary, not at the roadmap boundary, so the case stays unserved because it is not
Databricks' business, not because they have not got to it yet.

That argument should be in the README. If it stops being true, the project should say so.

## Target adopter

A .NET team, three to twenty engineers, that has already chosen Databricks for its data platform and
now has to ship a customer-facing product on top of it. They are competent at ASP.NET Core and
unfamiliar with Unity Catalog's grant model. They will not reorganise their product around notebooks.

**Not the target:** teams evaluating whether to adopt Databricks; teams wanting a generic .NET SaaS
starter; teams building internal dashboards, who should use Databricks Apps and stop reading here.

## Honest scope of the addressable market

The intersection of three sets: .NET-first shops, Databricks-standardised shops, and shops selling
customer-facing analytics. Each set is large. The intersection may not be. This is a real risk and it
is recorded in the risk register rather than argued away.

The project is sized accordingly: small enough for one maintainer, useful in pieces, and honest
about which pieces are glue.
