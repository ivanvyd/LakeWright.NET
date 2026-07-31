# Ecosystem and competitor analysis

Research date: 2026-07-31. Verified against primary sources on that date. Databricks moves fast and
several findings below are Public Preview, so re-check before relying on any of them.

## Official Databricks surface

| Question | Finding |
|---|---|
| Is there an official .NET SDK? | No. The canonical SDK page (Jun 26, 2026) lists Python, JavaScript, Go and Java; R is Labs. No C# repo in the `databricks` org. Dev-tools release notes never mention .NET. No announced plan found. |
| Any .NET in the accelerator ecosystem? | No. 219 repos in `databricks-industry-solutions`, 49 in `databrickslabs`. Python, Scala, TypeScript, R, Rust. Zero C#. |
| Official multi-tenant SaaS reference architecture? | None. The reference-architecture page updated 2026-07-28 has a three-sentence "Business Apps" section pointing at Databricks Apps. Tenant isolation and customer onboarding are absent. Only community forum posts cover it. |
| Can Databricks Apps host a customer-facing SaaS? | No. "You can't make Databricks apps public. Anonymous access and bypassing single sign-on (SSO) are not supported." See ADR 0001. |

**Databricks Labs is not open source.** `ucx`, `dqx` and `dbldatagen` carry the proprietary
Databricks License: "You may not use the Licensed Materials except in connection with your use of the
Databricks Services." Two consequences: clean-room authorship is mandatory, and a genuinely
Apache-2.0 project here is more permissive than most of Databricks' own community output.

**No public trademark policy exists.** `databricks.com/legal/trademark-policy` returns 404 and the
mark is absent from the 23-item legal index. Unlike Apache, the Linux Foundation, Rust or Mozilla,
Databricks publishes no OSS-facing trademark grant. Nominative use falls back on fair-use doctrine,
which is an argument rather than a permission. Practical rules: never the logo, never "Databricks"
in the project name, "for Databricks" as subordinate descriptive text, attribution line in the README.

## What already exists in .NET

| Capability | Verdict | Basis |
|---|---|---|
| REST client, Unity Catalog, Statement Execution, Jobs | **Reuse** | `Microsoft.Azure.Databricks.Client` 2.9.3. MIT, 2.2M downloads, .NET 8/9/10, 18 typed UC clients. See ADR 0004. |
| Model serving | **Build** | Not covered anywhere in .NET. Small and self-contained. |
| Chat over model serving | **Reuse** | Databricks exposes an OpenAI-compatible API, so `Microsoft.Extensions.AI.OpenAI` works. No bespoke client. |
| MLflow tracing | **Reuse** | OTLP/HTTP to `/api/2.0/otel/v1/traces`. The standard OpenTelemetry exporter covers it. |
| Multitenancy plumbing | **Reuse** | Finbuckle.MultiTenant. |
| ODBC | Available | Databricks ODBC Driver, renamed from Simba Spark ODBC in Feb 2026. |

## Competitors, and what they do not cover

**Embedded analytics platforms** (Cube, GoodData, Sigma, Omni, Looker, ThoughtSpot, Preset, Luzmo)
occupy the data tier. They solve the semantic layer and per-tenant row-level security. They do not
give a .NET team an application architecture, and none has a .NET SDK worth the name.

**Databricks-native app surfaces** (Databricks Apps, AppKit, App Spaces, Genie App Builder,
Marketplace Apps) occupy the app tier but are workspace-scoped and TypeScript or Python. Marketplace
Apps requires your customer to be a Databricks customer.

**.NET SaaS boilerplates** (ABP, BlazorPlate, Blazor Blueprint) occupy the app tier generically and
have zero data-platform story. Every one models tenancy as an EF Core query filter.

The quadrant of app tier, Databricks-native, .NET is unoccupied.

## The strongest argument against this project

Recorded because it is the one an evaluator will reach independently.

> Databricks is building this quadrant itself and picked TypeScript. AppKit is first-party,
> Apache-2.0, Node and React, for exactly "build a data app on Databricks". App Spaces, Genie App
> Builder, serverless micro apps and Marketplace Apps all shipped or previewed within fourteen
> months. A third-party .NET accelerator competes with a vendor roadmap actively filling the space
> in a language it has chosen. Meanwhile the pieces a .NET team actually needs are a few hundred
> lines of `HttpClient` against documented endpoints. And the addressable market is the intersection
> of three sets: .NET-first, Databricks-standardised, and selling customer-facing analytics.

**The rebuttal.** Databricks Apps cannot serve external customers by its own documentation, and
Marketplace Apps requires the customer to be a Databricks customer. Databricks monetises compute
inside their customer's account; a .NET ISV runs one account and resells. Those models diverge at the
billing boundary, not the roadmap boundary. The quadrant stays open because the case is not
Databricks' business, not because they have not reached it.

If that stops being true, the README should say so.

## Claims this project will not make

- "The missing Databricks SDK for .NET." False; see ADR 0004.
- "Calling Databricks from C# is hard." It is REST.
- "Dashboard embedding for external customers is unsolved." Databricks AI/BI external embedding
  ships with row-level security and no per-viewer fee. Reimplementing it is negative-value.
- "Databricks has no app distribution." False since 2026-06-16.
