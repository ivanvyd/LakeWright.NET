# 08 â€” Competitive and Adjacent Landscape

Research date: **2026-07-31**. All pricing figures are list prices observed on the date noted.

Method note: every claim below is tagged **[VERIFIED]** (I fetched the cited page or the search engine returned a direct quote from an authoritative source) or **[RECALLED / WEAK]** (secondary source, SEO aggregator, or my own inference). Vendor marketing pages are treated as authoritative only for their own pricing and feature claims, not for competitor comparisons.

---

## Executive summary of what changed

Two findings dominate everything else in this document, and both cut **against** a naive framing of the project:

1. **Databricks shipped first-party external-user dashboard embedding.** "Embedding for external users" lets a SaaS vendor put a live AI/BI dashboard in front of their own customers, with per-tenant row-level filtering, and *no Databricks account for the viewer*. Blog dated 2025-11-11; docs updated 2026-07-15. This eats the single most obvious feature of a "customer-facing analytics on Databricks" accelerator.
2. **Databricks shipped a Marketplace app distribution model.** Apps on Databricks Marketplace (public preview, announced 2026-06-16) is a Snowflake-Native-Apps-equivalent: publish once, any Databricks customer installs and runs it in their own workspace. Databricks now *does* have the distribution story it lacked.

Both are Python/TypeScript-shaped. Neither has a .NET on-ramp. That is where the surviving niche lives, and it is narrower than "build a SaaS on a lakehouse."

---

## 1. Embedded analytics / semantic layer platforms

### Summary table

| Product | Databricks source? | Per-tenant multi-tenancy / RLS | Price (observed 2026-07-31) | .NET SDK | Replaces or complements Lakewright.NET? |
|---|---|---|---|---|---|
| **Cube** (cube.dev) | Yes (documented data source) | Yes â€” multi-tenant by design, RLS + pre-aggregations | Free tier; **$40/dev/mo** Starter; **$80/dev/mo** Premium; Explorer **$40/user/mo**, Viewer **$20/user/mo**; Enterprise custom | **No** â€” REST/GraphQL/SQL APIs only | **Complements.** It is a semantic/data tier, not an app tier. A .NET app can consume its REST API. |
| **Preset / Apache Superset** | Yes | Superset: guest tokens + RLS, DIY. Preset: workspace-per-customer, managed | Preset Team **$49/mo** (entry); Superset OSS free | **No** | Complements/overlaps on the chart tier. Superset OSS multi-tenancy is explicitly DIY. |
| **GoodData** | Yes | Yes â€” isolated workspace per tenant, the canonical model | Not public. Third-party estimate ~**$1,500/mo** entry; year-1 embedded deployments **$60Kâ€“$250K** *(secondary source â€” treat as weak)* | **No** (React + Python SDKs) | **Overlaps heavily** on the analytics tier. Does not give you an app, billing, or tenant lifecycle. |
| **Sigma Computing** (embedded) | Yes â€” Databricks is a first-class warehouse | Yes â€” "Sigma Tenants" + programmatic tenant provisioning APIs; RLS inherited from warehouse | Not public. Vendr median ~**$61,000/yr**, range **$17.5Kâ€“$130K+** *(procurement aggregator)* | **No** | **Overlaps** on analytics; complements on app tier. |
| **Omni** (incl. Explo) | Yes â€” publishes Databricks-specific BI content | Yes (Explo's core competency was embedded multi-tenant) | Not public | **No** | Overlaps on analytics tier. |
| **Looker (embedded)** | Yes | Yes â€” long-established `user_attributes` model | Not public | **No** | Overlaps; Google-ecosystem gravity. |
| **ThoughtSpot Everywhere** | Yes | "Logical multi-tenancy"; multi-tenant support gated behind higher tiers *(secondary)* | Pro **$50/end-user** or **$0.10/query**; Enterprise custom | **No** | Overlaps on the search/NL analytics tier. |
| **Explo** | â€” | â€” | â€” | â€” | **Acquired by Omni, 2025-10-22.** Platform runs ~12 months for migration. Dead as an independent choice. |
| **Luzmo** | Yes ("integrates with Databricks") | Yes â€” RLS via JWT + user attributes | **â‚¬995/mo** Starter (annual), **â‚¬2,495/mo** Premium, Enterprise custom | **No** | Overlaps on dashboards; complements on app tier. |
| **Propel** (propeldata.com) | **Probably not** â€” connector list is Snowflake, BigQuery, S3, Redshift, ClickHouse, Postgres, Kafka, webhooks. Databricks was "planned" as of a 2022 post; no evidence it shipped | Yes â€” "multi-tenant access policies" | Storage + query consumption based; tiers not public | **No** | **Not a Databricks play.** It is a serverless-ClickHouse alternative to the lakehouse, i.e. a *substitute* for the data tier, not a complement. |

### Evidence

- Cube pricing tiers and per-seat figures **[VERIFIED â€” fetched https://cube.dev/pricing on 2026-07-31]**. Note: the pricing page itself does not name Databricks; Databricks support is asserted by Cube's own comparison articles and by secondary sources **[RECALLED / WEAK for the Databricks claim specifically]** â€” https://cube.dev/articles/best-embedded-analytics-platforms-2026
- Luzmo pricing â‚¬995 / â‚¬2,495 / custom, MAU-based, white-labelling included at all tiers **[VERIFIED â€” fetched https://www.luzmo.com/pricing on 2026-07-31]**. Note: an SEO aggregator reported *dollar* figures of $995/$2,050/$3,100 (https://www.draxlr.com/embedded-analytics-pricing/) which contradicts the vendor page; **trust the vendor page**.
- Explo acquisition by Omni, 2025-10-22, Explo becomes a wholly owned subsidiary, platform operates ~12 more months **[VERIFIED â€” https://www.businesswire.com/news/home/20251022265779/en/Omni-Accelerates-Growth-With-Acquisition-of-Explo and https://omni.co/blog/omni-acquires-explo]**
- Sigma Tenants / programmatic tenant provisioning **[VERIFIED via vendor page â€” https://www.sigmacomputing.com/product/embedded-analytics]**; pricing **[RECALLED / WEAK â€” https://www.vendr.com/marketplace/sigma]**
- GoodData Databricks connectivity + workspace-per-tenant **[VERIFIED via vendor â€” https://www.gooddata.ai/platform/embedded-analytics/]**; pricing **[RECALLED / WEAK â€” https://upsolve.ai/blog/gooddata-pricing, https://embeddable.com/blog/gooddata-alternatives]**
- Superset guest tokens + RLS, and the explicit statement that self-hosted Superset multi-tenancy requires "a separate Superset instance per customer or building custom code" **[VERIFIED via vendor â€” https://preset.io/blog/open-source-embedded-analytics-platforms/]**
- ThoughtSpot pricing **[RECALLED / WEAK â€” https://www.thoughtspot.com/pricing via secondary summaries]**
- Propel connector list **[VERIFIED via vendor docs listing â€” https://www.propeldata.com/docs]**; Databricks-planned-2022 **[RECALLED / WEAK â€” https://medium.com/propel-data-analytics-blog/why-we-built-propel-data-ed2b3277b237]**

### The .NET SDK question â€” answered

**Not one of these products ships a .NET/C# SDK.** Every one of them is React-first with a Python and/or Node server SDK. This is consistent and it is the single most repeatable finding in this whole document.

That said â€” and this is the honest counterweight â€” **none of them need one.** They are all HTTP/JWT services. A .NET backend can mint a Cube JWT or a Superset guest token in ~30 lines of `HttpClient` code. "No .NET SDK" is a papercut, not a moat. Anyone claiming otherwise is overselling.

---

## 2. Databricks-native app surfaces â€” the material section

### 2a. AI/BI Dashboard embedding for external users

This is the most important thing in the document.

**[VERIFIED â€” fetched https://learn.microsoft.com/en-us/azure/databricks/dashboards/share/embedding/external-embed, page `ms.date` 2026-07-14, `updated_at` 2026-07-15]**

> "Embedding for external users uses a service principal and scoped access tokens to authenticate and authorize access to embedded dashboards. This approach lets you share dashboards with viewers outside of your organization, such as partners and customers, **without provisioning Azure Databricks accounts for those users**."

The flow, exactly:

1. User signs into *your* app. Your frontend asks your server for a dashboard access token.
2. Your server uses the **service principal secret** â†’ `POST /oidc/v1/token` with `grant_type=client_credentials&scope=all-apis` â†’ broad OAuth token.
3. Your server calls `GET /api/2.0/lakeview/dashboards/{dashboard_id}/published/tokeninfo?external_viewer_id=â€¦&external_value=â€¦`
4. Your server re-calls `/oidc/v1/token` with the `tokeninfo` response + `authorization_details` â†’ a **tightly-scoped, browser-safe token**.
5. Frontend instantiates `DatabricksDashboard` from `@databricks/aibi-client` with that token.

Per-tenant RLS mechanism:

```sql
SELECT * FROM sales WHERE region = __aibi_external_value
```

`external_value` is signed into the OAuth token, cannot be tampered with by the client, and surfaces in dashboard dataset queries as the global variable `__aibi_external_value`. `external_viewer_id` goes to audit logs.

**Documented constraints [VERIFIED, same page]:**
- `external_viewer_id` **must not** contain PII. `external_value` *may* contain PII.
- Combined size of `external_viewer_id` + `external_value` **must not exceed 1 KB.**
- Rate limit: **20 dashboard loads per second.** "You can open more than 20 dashboards at once, but no more than 20 can start loading simultaneously."
- **"Ask Genie is not available in external embedding."** Natural-language querying for external users requires the Genie Conversation API instead.
- Workspace admin must allowlist approved hosting domains.
- Requires third-party cookies enabled **[VERIFIED â€” https://docs.databricks.com/aws/en/ai-bi/admin/embed]**
- Service principal needs `CAN RUN` on the dashboard and `SELECT` on underlying tables (when not published with shared data permissions).
- `hideDatabricksLogo: true` removes the "Powered by Databricks" footer â€” white-labelling is supported.

**Status:** the AWS admin doc labels "Embedding for external users" **(Public Preview)** **[VERIFIED â€” https://docs.databricks.com/aws/en/ai-bi/admin/embed]**. The Azure Learn deep-dive page carries no preview banner. Treat as Public Preview.

**Pricing [VERIFIED â€” https://www.databricks.com/blog/how-embed-databricks-aibi-dashboards-customer-facing-applications, 2025-11-11]:**
> "Databricks does not charge per user or per viewer session. You only pay for the SQL compute that powers the dashboard queries."

This is a *very* aggressive commercial position against Luzmo (â‚¬995/mo), GoodData (~$60K+/yr) and Sigma (~$61K/yr median).

**Reference implementations [VERIFIED â€” https://github.com/databricks-solutions/aibi-dashboards-external-embedding]:** Flask backend + React/Vite frontend. 7 stars, 13 commits. The in-doc samples are **Python and JavaScript only**. **No .NET sample exists anywhere.**

**Implication for Lakewright.NET:** the dashboard-embedding feature is *solved by the platform*. An accelerator cannot claim to invent it. What it *can* do is ship the step-2-to-4 token-exchange broker as tested, DI-registered, `IHttpClientFactory`-based C# â€” including the non-obvious `authorization_details` JSON round-trip that the samples show but do not explain. That is a genuine but small piece of work: call it 200â€“400 lines.

### 2b. Databricks Apps

**[VERIFIED â€” https://developers.databricks.com/docs/apps/overview]** â€” the docs are blunt about the boundary:

> apps are not suitable for "Public-facing or customer-facing apps" since "users must be authenticated identities in your Databricks account."

Corroborated **[VERIFIED â€” https://docs.databricks.com/aws/en/dev-tools/databricks-apps/auth]**: auth is workspace SSO or OTP; on-behalf-of-user authorization forwards the signed-in user's token via `x-forwarded-access-token`; each app gets a dedicated service principal.

- GA since **2025-06-11**; 20,000+ apps across 2,500+ orgs; 28 regions, all three clouds **[VERIFIED â€” https://databricks.com/blog/announcing-general-availability-databricks-apps]**
- Supported runtimes: **Python (Streamlit, Dash, Gradio) and Node.js (React, Angular, Svelte, Express)** **[VERIFIED â€” https://docs.databricks.com/aws/en/dev-tools/databricks-apps/]**. **No .NET. No custom-container escape hatch documented for Apps** (custom Docker images exist for AI Runtime CLI workloads and Databricks Container Services on *compute*, not for Apps) **[VERIFIED â€” https://docs.databricks.com/aws/en/release-notes/product/2026/june]**
- Billing: "Apps are billed per hour of compute time while running, based on provisioned capacity." No public per-unit figure on the docs page or the pricing page (JS-rendered) **[VERIFIED absence, 2026-07-31]**
- **2026 additions [VERIFIED â€” https://www.databricks.com/blog/enabling-governed-vibe-coding-enterprise-apps-databricks and Summit coverage]:** **AppKit** (TypeScript/Node+React SDK, Apache-2.0, 88 stars, https://github.com/databricks/appkit), **App Spaces** (governance boundary for groups of apps), **Genie App Builder**, and **serverless micro apps** that scale to zero.

**Read this carefully:** Databricks is *actively investing* in a first-party app framework â€” and it chose **TypeScript**. AppKit is the shape Lakewright.NET would occupy, in a different language, with a vendor behind it.

### 2c. Genie embedding

**[VERIFIED â€” https://docs.databricks.com/aws/en/genie/embed]** Genie Space iframe embedding **requires viewers to have Databricks accounts**:

> "Users who are not signed in to Databricks are prompted to authenticate before they can interact with the embedded agent."

Combined with "Ask Genie is not available in external embedding" (Â§2a), this is a **real, currently-open gap**: there is no turnkey way to give *your SaaS customers* conversational analytics. You must build it yourself on the **Genie Conversation API** (Public Preview; `POST /api/2.0/genie/spaces/{space_id}/start-conversation`, stateful, supports follow-ups) **[VERIFIED â€” https://docs.databricks.com/aws/en/genie/conversation-api, https://www.databricks.com/blog/genie-conversation-apis-public-preview]**.

**This is the strongest single niche found in this research.** It is a REST API with no per-tenant scoping baked in â€” the tenant-scoping, prompt-boundary and result-authorization logic is yours to write, in whatever language you like.

### 2d. Delta Sharing / OpenSharing

**[VERIFIED â€” https://www.databricks.com/blog/introducing-opensharing-next-evolution-delta-sharing-agentic-era, June 2026]** Databricks announced **OpenSharing**, moving Delta Sharing to an independent open-source project and extending scope from data to models and agents, adding Iceberg IRC client support.

Constraints **[VERIFIED â€” Databricks docs via search]**: 100 IP/CIDR values per recipient (IPv4 only), IP access lists apply to open-sharing recipients only. Open-sharing recipient tokens issued before 2026-03-09 stop working **2027-07-01**.

Delta Sharing is a *bulk data delivery* channel, not an app channel. It solves "give my customer their data"; it does not solve "give my customer a product." Complementary, not competitive.

---

## 3. Data-app frameworks in other languages â€” and the .NET story

| Framework | Language | Customer-facing multi-tenant? | Databricks? |
|---|---|---|---|
| Streamlit / Dash / Gradio | Python | No â€” internal-tool shaped; first-class inside Databricks Apps | Yes, natively |
| AppKit | TypeScript | Not documented as such; on-behalf-of-user only | Yes, first-party |
| Evidence | Markdown/SQL â†’ static | Static-site shaped, not per-tenant SaaS | Yes (adapter) |
| Rill | Go/DuckDB | Not the target | Partial |
| Hex | SaaS | Yes â€” white-label + multi-tenant + Embed API **[VERIFIED â€” https://hex.tech/product/embedded-analytics/]** | Yes |
| Retool | SaaS/low-code | Internal-tools first; Retool Embed exists for external. 2025 Databricks Emerging Partner of the Year; 2026 Lakebase launch partner **[VERIFIED â€” https://retool.com/blog/retool-databricks-data-ai-summit-2026]** | Yes |
| **Ivy Framework** | **C#** | **No â€” explicitly "internal tools" / "backoffice"** | **No** |

**The .NET story: confirmed effectively absent.**

**Ivy Framework** (https://github.com/Ivy-Interactive/Ivy-Framework) is the closest thing to a C# Streamlit â€” Apache-2.0, 431 stars, full-stack C# with no JS **[VERIFIED â€” fetched 2026-07-31]**. Its connector list is **SQL Server, Postgres, Supabase, MariaDB, MySQL, Airtable, Oracle, Google Spanner, ClickHouse, Snowflake, BigQuery**. **Databricks is not on it.** It is tagged for internal tools and backoffice, and says nothing about multi-tenant SaaS. Caveat: Ivy the *company* (ivy.app) has pivoted its homepage to an AI coding agent ("Ivy Tendril"), which raises a maintenance-continuity question for the framework.

Blazor is a general web UI framework, not a data-app framework â€” it gives you nothing lakehouse-shaped.

---

## 4. Snowflake comparison â€” and Databricks now has an answer

**Snowflake Native App Framework [VERIFIED â€” https://docs.snowflake.com/en/developer-guide/native-apps/native-apps-about, https://www.snowflake.com/en/blog/marketplace-monetization-turn-data-apps-revenue-stream/]:**
- Apps install into the **consumer's** Snowflake account; data never leaves it.
- Distribution via Snowflake Marketplace or private listings.
- **On-platform monetization is GA**; **Custom Event Billing is Public Preview** â€” API-based metering so a provider can charge on nearly any value dimension.

**Databricks equivalent â€” this is new and it changes positioning.**

**[VERIFIED â€” https://www.databricks.com/blog/announcing-apps-databricks-marketplace, dated 2026-06-16]:**
> "Databricks customers can now discover, install, and run third-party data and AI applications directly inside their secure Databricks workspaces."
> "Apps deploy natively within your secure, governed Unity Catalog â€” your data remains where it is, in your environment."
> "providers publish once, and any Databricks customer can discover, request access, install, and run an app entirely within their own environment, with no vendor infrastructure to maintain."

Status: **Public Preview**. Runtime: the Databricks Apps runtime, i.e. **Python or Node.js**. Databricks Labs publishes ISV guidance at https://databrickslabs.github.io/partner-architecture/data-collaboration/marketplace-apps.

Real ISVs are already shipping on it â€” Cotality CLIP App, Acxiom Real ID **[VERIFIED â€” https://www.businesswire.com/news/home/20260616758894/en/Acxiom-Awarded-2026-Databricks-ISV-MarTech-Partner-of-the-Year-at-Data-AI-Summit]**.

**So: Databricks now has a native-app distribution model.** The 2024-era talking point "Snowflake has Native Apps, Databricks has nothing" is **dead as of June 2026**. Any positioning that leans on it is wrong.

The important structural consequence: Marketplace Apps is a **B2B2-workspace** model â€” your customer must themselves be a Databricks customer. It does **not** serve the "my customers are ordinary businesses who have never heard of a lakehouse" case. That case still requires you to run your own SaaS. That distinction is the load-bearing part of the positioning argument.

---

## 5. Commercial .NET SaaS boilerplates

| Product | Price (observed 2026-07-31) | Multi-tenancy | Data platform / lakehouse story |
|---|---|---|---|
| **ABP** (abp.io) | OSS free; **Team $2,999**, **Business $5,999**, **Enterprise $9,999** â€” one-time perpetual, 3 dev seats each | Yes â€” DB-per-tenant, schema-per-tenant, shared DB | **None** |
| **BlazorPlate** | Not shown on homepage (separate pricing page) | Yes â€” dedicated DB, shared, single-tenant | **None** |
| **Blazor Blueprint** | **Â£499** one-time Enterprise; OSS core free for personal/non-commercial | Yes â€” isolated data, custom domains, per-tenant config | **None** |
| **Brick Starter** | Flat price, same for all editions (figure not exposed) | Yes | **None** |
| **BlogArray.SaaS** | OSS | Yes â€” OpenIddict + Finbuckle.MultiTenant | **None** |

**[VERIFIED â€” fetched https://abp.io/pricing 2026-07-31]:** all tiers listed above with exact figures; "All licenses are perpetual, with unlimited deployment and unlimited project count"; 30-day money-back. **Explicitly no data platform, analytics, warehouse, or lakehouse features.**

**[VERIFIED â€” fetched https://www.blazorplate.net/ 2026-07-31]:** .NET 10, three multi-tenancy modes, ASP.NET Identity/OAuth2/2FA, OpenTelemetry, L1+L2 hybrid cache, Hangfire, SignalR, 20+ languages, SQL Server default with PostgreSQL/MySQL support. **No mention of Databricks, Snowflake, BigQuery, or any warehouse/lakehouse/analytics platform.**

**Confirmed: zero .NET SaaS boilerplates have a data-platform story.** Their world ends at "OLTP database + Identity + Stripe." Their tenancy model is *transactional* (EF Core query filters over a tenant-id column), which does not transfer to a lakehouse where isolation must be enforced in Unity Catalog and in the SQL warehouse, not in the ORM.

This is the cleanest, least contestable gap in the document â€” and note it is *symmetric* with Â§1: the analytics vendors have no .NET, and the .NET vendors have no analytics.

---

## 6. Existing Databricks + .NET content â€” the novelty calibration

**Be honest: the space is thin, but thin because of low demand, not because it is hard.**

**No official Databricks .NET SDK exists. [VERIFIED â€” fetched https://docs.databricks.com/aws/en/dev-tools/sdks 2026-07-31]** Official SDKs: **Python, JavaScript, Go, Java**, plus **R** via Databricks Labs. C#/.NET is absent.

What does exist:

| Artifact | What it is | Depth |
|---|---|---|
| `Azure/azure-databricks-client` | Microsoft-maintained C# REST client, **87 stars**, 248 commits, .NET 6, covers Clusters/Jobs/DBFS/Secrets/Groups/Libraries/Tokens/Workspace/Instance Pools/Permissions/Policies/Init Scripts/Repos/DLT/**SQL Warehouses** | Real library, but **workspace-*administration*** oriented. No Unity Catalog coverage found. Not an app-building SDK. |
| `elastacloud/databricks-dotnet-rest-sdk` | Unofficial community REST wrapper | Low activity |
| `Mimetis/DbricksSqlNet` | Small SQL-warehouse connector (`SqlWarehouseConnectionOptions`: Host, ApiKey, WarehouseId, Catalog, Schema) | Single-purpose |
| `anhhchu/databricks-dotnet` | **1 star**, 14 commits. Two samples: Simba ODBC and SQL Statement Execution API, both querying `samples.tpch.orders`. Self-described "example code for educational purposes" | **Toy** |
| aibits.blog, "Building Data-Driven Apps with .NET and Databricks", Eumar Assis, 2025-04-22 (+ `eumarassis/DatabricksDotNetSample`) | .NET 8 Minimal API, OAuth M2M + U2M + PAT, REST SQL execution, minimal HTML UI | **Shallow.** Explicitly covers *no* multi-tenancy, *no* RLS, *no* per-tenant isolation, *no* Unity Catalog governance, *no* connection pooling/caching/cost. A getting-started post. |
| CData KB articles (ADO.NET provider, EF Core, MVC) | Commercial driver vendor docs | Vendor-specific, connectivity-only |

**[VERIFIED]** for all repo metrics â€” each fetched 2026-07-31. **[VERIFIED]** for the aibits depth assessment â€” fetched the article.

**Conference talks:** searched Data + AI Summit 2026 coverage (30,000+ attendees, Moscone, June 15â€“18). **No .NET/C# sessions surfaced.** Microsoft was a Legend Sponsor with breakouts, but the content is Azure-Databricks-platform, not .NET-application. **[VERIFIED absence within search reach â€” this is weaker evidence than a positive finding; a session could exist and not be indexed.]**

**Honest calibration:** every individual piece â€” auth, SQL execution, token exchange â€” is documented and someone has blogged it. **What does not exist anywhere is the composition**: tenant model + Unity Catalog isolation + warehouse routing + cost attribution + embed token broker + Genie scoping, assembled and tested. Novelty is in the assembly and the opinionated defaults, **not** in any single API call. Any claim of "first .NET Databricks integration" would be false and easily falsified. "First opinionated .NET reference architecture for multi-tenant lakehouse SaaS" is defensible.

---

## 7. Positioning map

**Axis X â€” tier occupied:** Data tier (semantic layer, warehouse, RLS) â†â†’ App tier (auth, tenancy, billing, UI shell)
**Axis Y â€” Databricks-nativeness:** Generic/warehouse-agnostic â†â†’ Databricks-native

```
                    DATABRICKS-NATIVE
                            ^
                            |
   Databricks AI/BI         |         Databricks Apps (GA)
   external embedding       |         AppKit (TypeScript)
   (Public Preview)         |         App Spaces / Genie App Builder
                            |         Marketplace Apps (Public Preview)
   Delta Sharing /          |         Genie Conversation API (Preview)
   OpenSharing              |
                            |    << Lakewright.NET sits here-ish,
                            |       app tier, Databricks-native,
                            |       .NET-only >>
   ---------------------------------------------------------> APP TIER
   DATA TIER                |
                            |
   Cube                     |         Retool  Â·  Hex
   GoodData                 |         Ivy Framework (C#, no Databricks)
   Sigma  Â·  Omni           |
   Looker  Â·  ThoughtSpot   |         ABP  Â·  BlazorPlate
   Preset/Superset          |         Blazor Blueprint  Â·  Brick Starter
   Luzmo                    |         (all: zero data-platform story)
   Propel (non-Databricks)  |
                            |
                            v
                   GENERIC / AGNOSTIC
```

**The quadrant Lakewright.NET claims â€” app tier Ã— Databricks-native Ã— .NET â€” is genuinely unoccupied.** Nothing else is in it. But an empty quadrant is not automatically a valuable one, and the map has two crowding pressures pointing straight at it:

- **From above-right:** Databricks itself, with AppKit and Marketplace Apps, is building out exactly this quadrant â€” in TypeScript, with a vendor's resources.
- **From bottom-right:** ABP and friends own .NET SaaS scaffolding. If lakehouse-backed .NET SaaS ever becomes a real market, adding a Databricks module is a quarter of work for them, not a rewrite.

---

## 8. Is the gap real? â€” verdict

**Yes, but it is narrow, and it is narrower than it was twelve months ago.**

### What is genuinely unserved (defensible)

1. **Tenant-scoped Genie for external customers.** Iframe Genie needs Databricks accounts; external dashboard embedding explicitly excludes Ask Genie. The Conversation API is the only path and it ships no tenant-scoping. Open in *every* language, .NET included. **Strongest item.**
2. **Lakehouse-shaped tenancy for .NET.** Every .NET boilerplate models tenancy as an EF Core query filter. Unity Catalog isolation, warehouse routing, and per-tenant cost attribution are a different problem that no .NET artifact addresses.
3. **The composition itself.** No reference architecture in any language assembles tenant lifecycle + UC isolation + embed-token broker + cost attribution. Python could do it and hasn't; that is evidence the composition is the work.
4. **Per-tenant cost attribution.** Databricks bills compute, not viewers. Attributing warehouse spend back to a tenant for SaaS margin is unsolved in every artifact reviewed.

### What is not a gap â€” do not claim these

- **Dashboard embedding.** Solved first-party, Public Preview, no per-viewer fee, RLS included. Reimplementing it is negative-value.
- **Row-level security.** `__aibi_external_value` exists.
- **"Databricks has no app distribution."** False since 2026-06-16.
- **"Calling Databricks from C# is hard."** It is REST. Multiple people have blogged it.
- **"No .NET SDK is a blocker."** `Azure/azure-databricks-client` covers admin APIs; SQL Statement Execution is plain HTTP.

### Strongest counter-argument to the project's existence

> Databricks is building this quadrant itself, and it picked TypeScript.
>
> AppKit is a first-party, Apache-2.0, Node+React SDK for exactly "build a data app on Databricks." App Spaces adds governance, Genie App Builder adds generation, serverless micro apps add scale-to-zero economics, and Marketplace Apps adds distribution. All shipped or previewed inside the last fourteen months, all Python/TypeScript. A third-party .NET accelerator is not competing with a vacuum â€” it is competing with a vendor roadmap that is actively filling the space in a language it has chosen, and that will keep filling it.
>
> Meanwhile the two things a .NET team actually needs â€” an embed-token broker and a Genie tenant-scoper â€” are each a few hundred lines of `HttpClient` against documented REST endpoints. A competent .NET team writes them in a sprint. The gap is real but it may be *smaller than the cost of maintaining an accelerator across Databricks' preview churn*: in this research alone, external embedding, Marketplace Apps, Genie Conversation API, and App Spaces are all Public Preview, i.e. all still moving.
>
> And the addressable market is the intersection of three sets: shops that are .NET-first, that have standardised on Databricks, and that sell customer-facing analytics. Each set is large; the intersection may not be.

### The rebuttal worth making

Databricks Apps **cannot** serve external customers â€” its own docs say so. Marketplace Apps requires your customer to *be* a Databricks customer. So for the actual target case â€” a .NET ISV selling analytics to businesses that have never heard of a lakehouse â€” the vendor roadmap does not reach, and will not reach soon, because that case is not Databricks' business model. Databricks monetises compute in *their* customer's account; a .NET ISV runs one account and resells. That is a structural divergence of interest, not a temporary gap, and it is the honest reason the quadrant stays open.

**Recommended framing:** not "the missing SDK" and not "the first .NET Databricks integration" â€” both are false or trivial. Frame as **the reference architecture and reusable primitives for .NET ISVs reselling lakehouse analytics to non-Databricks customers**, whose defensible core is tenant isolation, embed/Genie token brokering, and per-tenant cost attribution. Everything else in the accelerator should be honestly labelled as glue.

---

## Sources

Databricks â€” embedding and apps:
- https://learn.microsoft.com/en-us/azure/databricks/dashboards/share/embedding/external-embed
- https://docs.databricks.com/aws/en/ai-bi/admin/embed
- https://www.databricks.com/blog/how-embed-databricks-aibi-dashboards-customer-facing-applications
- https://github.com/databricks-solutions/aibi-dashboards-external-embedding
- https://developers.databricks.com/docs/apps/overview
- https://docs.databricks.com/aws/en/dev-tools/databricks-apps/auth
- https://docs.databricks.com/aws/en/dev-tools/databricks-apps/
- https://databricks.com/blog/announcing-general-availability-databricks-apps
- https://www.databricks.com/blog/announcing-apps-databricks-marketplace
- https://databrickslabs.github.io/partner-architecture/data-collaboration/marketplace-apps
- https://github.com/databricks/appkit
- https://www.databricks.com/blog/enabling-governed-vibe-coding-enterprise-apps-databricks
- https://docs.databricks.com/aws/en/genie/embed
- https://docs.databricks.com/aws/en/genie/conversation-api
- https://www.databricks.com/blog/genie-conversation-apis-public-preview
- https://www.databricks.com/blog/introducing-opensharing-next-evolution-delta-sharing-agentic-era
- https://docs.databricks.com/aws/en/dev-tools/sdks
- https://docs.databricks.com/aws/en/release-notes/product/2026/june

Embedded analytics vendors:
- https://cube.dev/pricing Â· https://cube.dev/articles/best-embedded-analytics-platforms-2026
- https://www.luzmo.com/pricing
- https://www.gooddata.ai/platform/embedded-analytics/ Â· https://www.gooddata.ai/pricing/
- https://www.sigmacomputing.com/product/embedded-analytics Â· https://www.vendr.com/marketplace/sigma
- https://preset.io/blog/open-source-embedded-analytics-platforms/
- https://www.thoughtspot.com/pricing
- https://omni.co/blog/omni-acquires-explo Â· https://www.businesswire.com/news/home/20251022265779/en/Omni-Accelerates-Growth-With-Acquisition-of-Explo
- https://www.propeldata.com/docs Â· https://www.propeldata.com/product
- https://hex.tech/product/embedded-analytics/
- https://retool.com/blog/retool-databricks-data-ai-summit-2026 Â· https://retool.com/integrations/databricks

Snowflake:
- https://docs.snowflake.com/en/developer-guide/native-apps/native-apps-about
- https://www.snowflake.com/en/blog/marketplace-monetization-turn-data-apps-revenue-stream/

.NET ecosystem:
- https://abp.io/pricing
- https://www.blazorplate.net/
- https://blazorblueprint.net/
- https://www.brickstarter.net/
- https://github.com/Ivy-Interactive/Ivy-Framework
- https://github.com/Azure/azure-databricks-client
- https://github.com/anhhchu/databricks-dotnet
- https://github.com/elastacloud/databricks-dotnet-rest-sdk
- https://github.com/Mimetis/DbricksSqlNet
- https://aibits.blog/2025/04/22/building-data-driven-apps-with-net-and-databricks/ Â· https://github.com/eumarassis/DatabricksDotNetSample
