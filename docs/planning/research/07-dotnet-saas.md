# R07 — The .NET Multi-Tenant SaaS Ecosystem: What to REUSE vs BUILD

Research date: **2026-07-31**. Every version/date below was checked live against nuget.org, github.com, learn.microsoft.com or aspire.dev on this date. Items marked **RECALLED** were not verified from a primary source and should be re-checked before they become load-bearing.

Baseline context (VERIFIED):
- .NET 10 is the current LTS. Latest SDK **10.0.302**, runtime **10.0.10**, released **2026-07-14**. Support runs 2025-11-11 → 2028-11-14. — https://dotnet.microsoft.com/en-us/download/dotnet/10.0
- .NET 11 is in preview (Preview 6 landed 2026-07-14), GA scheduled **2026-11-10** as an STS release. Do **not** target it. — https://github.com/dotnet/core/blob/main/release-notes/11.0/README.md

---

## 1. Multitenancy libraries

### Finbuckle.MultiTenant — VERIFIED

| Fact | Value | Source |
|---|---|---|
| Latest version | **10.1.2**, published **2026-07-15** (GitHub release tagged 2026-07-14) | https://www.nuget.org/packages/Finbuckle.MultiTenant · https://github.com/Finbuckle/Finbuckle.MultiTenant/releases |
| License | **Apache-2.0** | https://www.nuget.org/packages/Finbuckle.MultiTenant |
| .NET 10 support | Yes — v10 targets `net10.0`. Major versions now track .NET major versions ("target the version of MultiTenant that matches your .NET version") | https://github.com/Finbuckle/Finbuckle.MultiTenant/blob/main/docs/Introduction.md |
| Downloads | 9.7M total; 21,979 on 10.1.2 in ~2 weeks | https://www.nuget.org/packages/Finbuckle.MultiTenant |
| Maintenance health | **Healthy.** Releases roughly every 2–6 weeks through 2026 (10.0.6 Apr 21, 10.0.7 Apr 29, 10.0.8 May 13, 10.1.0 May 25, 10.1.1 Jun 10, 10.1.2 Jul 15). Actively backports to v8 and v9 branches on the same day (v8.1.16 and v9.4.11 both shipped 2026-07-14) — a strong maintenance signal. | https://github.com/Finbuckle/Finbuckle.MultiTenant/releases |
| Maintainer | Finbuckle LLC (single-vendor OSS, sponsored via GitHub Sponsors) | https://github.com/Finbuckle/Finbuckle.MultiTenant/blob/main/docs/Introduction.md |

**Bus-factor caveat:** this is effectively one company / small team. Apache-2.0 means we can hard-fork if it dies, and the same-day multi-branch backporting suggests discipline, but it is not a Microsoft-backed dependency.

**What you actually get** (https://www.finbuckle.com/MultiTenant/Docs/v10.0.0/Strategies, `/Stores`, `/EFCore`, `/Authentication`):

*10 tenant resolution strategies*, chainable in priority order with fallback: Static, Delegate, HttpContext, **Base Path** (`/initech/...`), Claim (defaults to a `__tenant__` claim), Session, **Route** (`__tenant__` route param), **Host** (subdomain templates), **Header**, and Remote Authentication Callback (a specialised strategy that survives the OIDC/OAuth2 redirect round-trip — this one is genuinely fiddly to write yourself).

*6 tenant stores*: In-Memory (`ConcurrentDictionary`, case-sensitivity option), **Configuration** (reads `appsettings.json`, read-only, honours config reload), **EF Core** (`EFCoreStoreDbContext`, normally a separate catalog DB), HTTP Remote (calls an endpoint, returns `TenantInfo`), Distributed Cache (Redis/SQL/NCache, indexes each tenant twice — by ID and by identifier, with sliding expiry), and Echo (returns a synthesised tenant, for tests). Custom stores via `IMultiTenantStore<TTenantInfo>`.

*EF Core integration*: `MultiTenantDbContext` base class, or `IMultiTenantDbContext` on an existing context. Mark entities with `[MultiTenant]` or `.IsMultiTenant()`; the library adds/uses a shadow `TenantId` property and installs a **named global query filter**. `EnforceMultiTenant()` in `SaveChanges` throws on cross-tenant writes, governed by `TenantMismatchMode` / `TenantNotSetMode`.

*Per-tenant auth*: `WithPerTenantAuthentication()` maps conventional `ITenantInfo` properties (`CookieLoginPath`, `CookieLogoutPath`, `CookieAccessDeniedPath`, `OpenIdConnectAuthority`, `OpenIdConnectClientId`, `OpenIdConnectClientSecret`) onto the auth handlers; `WithPerTenantOptions<T>` covers everything else. — https://github.com/Finbuckle/Finbuckle.MultiTenant/blob/main/docs/Authentication.md

**Documented caveats we must design around** (from the EFCore doc):
- Global query filters apply **only at the query root**. Related entities pulled in via `Include` are **not** filtered. This is the single most dangerous footgun — an unfiltered navigation is a cross-tenant data leak, and it is a property of EF Core, not of Finbuckle.
- Attaching entities requires non-null PKs; `EnforceMultiTenantOnTracking()` is needed to auto-stamp `TenantId`.
- `IgnoreQueryFilters()` silently disables isolation (Finbuckle exposes a `TenantToken` constant so you can ignore *other* filters selectively — use that, and ban bare `IgnoreQueryFilters()` with an analyzer or review rule).

### Competitors — VERIFIED

- **SaasKit** (`SaasKit.Multitenancy`) — MIT, but last NuGet publish **2016-07-17**, 90 commits total. Not archived, but the maintainer says he supports it "in my spare time." **Dead for our purposes.** — https://www.nuget.org/packages/SaasKit.Multitenancy · https://github.com/saaskit/saaskit
- **ABP Framework** — `Volo.Abp.Core` **10.6.0**, published **2026-07-27**, **LGPL-3.0-only**, targets net8.0/9.0/10.0, 44.4M downloads. Multi-tenancy is a first-class architectural pillar with a SaaS module (tenants + editions). — https://www.nuget.org/packages/Volo.Abp.Core · https://github.com/abpframework/abp
- **OrchardCore** — `OrchardCore.Application.Cms.Targets` **3.0.1**, published **2026-07-09**, **BSD-3-Clause**, targets `net10.0`. Mature multi-tenant shell architecture, but it is a CMS, not a library. — https://www.nuget.org/packages/OrchardCore.Application.Cms.Targets

### VERDICT: **REUSE Finbuckle.**

The "write ~200 lines ourselves" framing understates the job. The 200 lines gets you *one* resolution strategy plus an EF query filter. What you do not get for 200 lines, and what will each cost days when you hit them:

1. **Remote authentication callback resolution.** During an OIDC round-trip the tenant context is gone by the time the IdP redirects back to `/signin-oidc`. Finbuckle solves this by round-tripping the tenant through the OIDC `state` parameter. Their v10.1.2 changelog literally includes "handle missing remote callback state consistently" — they are still fixing edge cases in this after 10 major versions. That is the shape of a problem you do not want to own.
2. **Per-tenant `IOptions` monitoring.** Making `OpenIdConnectOptions` / `CookieAuthenticationOptions` vary per tenant means intercepting the options cache with a tenant-keyed cache. Correct, thread-safe, and cheap is not a 200-line problem.
3. **`TenantMismatchMode` / `TenantNotSetMode` on `SaveChanges`.** The write-side guard is the part homegrown implementations always skip, and it is the one that stops a bug becoming a breach.

Cost of taking it: one Apache-2.0 dependency, ~1MB, no transitive weight of note, and it is compatible with any OSS licence we pick. Cost of not taking it: we become the maintainers of a multitenancy library, which is precisely the "reinventing a generic SaaS framework" risk the project is trying to avoid.

**Take Finbuckle.MultiTenant 10.1.2. Wrap it behind our own thin `ITenantContext` seam** so the dependency is one file deep and swappable, and so our public API does not leak `Finbuckle.*` types to consumers of the accelerator.

**Do NOT take ABP.** It is LGPL-3.0 — workable for a dynamically-linked app but a genuine adoption tax for an accelerator that enterprises will vendor and modify, and it is an entire opinionated framework (modules, DI conventions, ABP Studio) that would dictate our architecture end to end. Steal its *ideas*, not its packages.

---

## 2. Aspire

### Status as of 2026-07-31 — VERIFIED

| Fact | Value | Source |
|---|---|---|
| Current version | **13.4.6**, published **2026-06-19** | https://www.nuget.org/packages/Aspire.CLI · https://www.nuget.org/packages/Aspire.AppHost.Sdk |
| License | **MIT** | https://www.nuget.org/packages/Aspire.CLI |
| Name | **"Aspire"** — the ".NET" prefix was dropped at 13.0 (Nov 2025) to reflect Python/TypeScript/JS support | https://visualstudiomagazine.com/articles/2025/11/12/microsoft-releases-aspire-13.aspx · https://devblogs.microsoft.com/aspire/aspire-13-2-announcement/ |
| Release cadence | Fast. 13.0 (Nov 2025) → 13.2 (**2026-03-23**) → 13.3 (**2026-05-07**) → 13.4.x (Jun 2026), with patches every few days | https://aspire.dev/whats-new/aspire-13-2/ · https://aspire.dev/whats-new/aspire-13-3/ |

### Is `aspire` a separate CLI now? — **Yes.** VERIFIED

It is its own tool, installable five ways, and since 13.3 it ships as a **NativeAOT .NET global tool** (instant startup):

```
winget install Microsoft.Aspire          # Windows
dotnet tool install -g Aspire.Cli
npm install -g @microsoft/aspire-cli
brew install --cask microsoft/aspire/aspire
irm https://aspire.dev/install.ps1 | iex
```
— https://aspire.dev/get-started/install-cli/

### Prerequisites — VERIFIED

- **.NET 10 SDK required for the AppHost project itself** (orchestrated apps may still target net8.0+). AppHost `<TargetFramework>` must be `net10.0` at Aspire 13. — https://aspire.dev/get-started/prerequisites/ · https://github.com/dotnet/docs-aspire/blob/main/docs/get-started/upgrade-to-aspire-13.md
- **An OCI container runtime is mandatory**: Docker Desktop (recommended) or Podman (`ASPIRE_CONTAINER_RUNTIME=podman`). Rancher Desktop works but is explicitly *not* supported/tested. — https://aspire.dev/get-started/prerequisites/
- AppHost projects use `Sdk="Aspire.AppHost.Sdk/13.x"` directly in the `<Project>` tag; the SDK now auto-includes `Aspire.Hosting.AppHost`.

### What it gives us

**Postgres** (`Aspire.Hosting.PostgreSQL` 13.4.6, `aspire add postgres`) — https://aspire.dev/integrations/databases/postgres/postgres-host/
- `AddPostgres("pg").AddDatabase("lakewright")` — runs `docker.io/library/postgres`, issues `CREATE DATABASE`.
- `WithDataVolume()` / `WithDataBindMount()` for persistence; `WithInitBindMount()` for seed SQL.
- `WithPgAdmin()` / `WithPgWeb()` for a browser DB UI, `WithHostPort()` to pin ports.
- Health checks wired automatically via `AspNetCore.HealthChecks.Npgsql`.
- `WithPostgresMcp()` adds an MCP sidecar so coding agents can query the DB.
- ⚠️ **Breaking:** Aspire 13.4 defaults to **PostgreSQL 18**, which moved its data dir from `/var/lib/postgresql/data` to `/var/lib/postgresql`. Volumes created under PG 17 need migration or an explicit image pin. Pin the image tag in our AppHost.

**Keycloak** (`Aspire.Hosting.Keycloak` + `Aspire.Keycloak.Authentication`) — https://aspire.dev/integrations/security/keycloak/
- `builder.AddKeycloak("keycloak", 8080)` runs `quay.io/keycloak/keycloak`, auto-generates an admin password into the AppHost secret store.
- `WithRealmImport("./realms")` copies realm JSON into `/opt/keycloak/data/import` — **local dev only**; the docs are explicit that it does not survive `aspire publish`/`aspire deploy`. Production realm seeding needs a custom image, an init service hitting the Admin REST API, or Terraform.
- Client side: `AddKeycloakJwtBearer(serviceName, realm, options)` and `AddKeycloakOpenIdConnect(serviceName, realm, options)`.
- ⚠️ Pin a **stable port** (8080). Aspire's default dynamic ports break OIDC because the authority URL (with port) is baked into persisted browser cookies that outlive the AppHost.
- ⚠️ In production you must set the Authority explicitly — Aspire's `https+http://` service-discovery scheme fails OIDC's HTTPS metadata requirement.

**Service discovery, dashboard, OTel**: the ServiceDefaults project wires OpenTelemetry traces/metrics/logs and health endpoints; the dashboard is an OTLP consumer. Since 13.3 you can run `aspire dashboard` standalone against *any* OTLP emitter — useful for us because contributors can get the trace view without adopting the AppHost. Since 13.3 the dashboard also captures browser console logs, network requests and screenshots via the Browsers integration. — https://aspire.dev/whats-new/aspire-13-3/

**Testing**: `Aspire.Hosting.Testing` 13.4.6 (MIT, net8/9/10) gives `DistributedApplicationTestingBuilder` — spin the whole app model up in an integration test. — https://www.nuget.org/packages/Aspire.Hosting.Testing

**Auth metrics**: .NET 10 added first-class auth/authz and ASP.NET Core Identity metrics (`aspnetcore.identity.*`, authenticated request duration, challenge/forbid/signin/signout counts) that render in the Aspire dashboard out of the box. — https://learn.microsoft.com/aspnet/core/release-notes/aspnetcore-10.0#authentication-and-authorization

### Deployment to Azure Container Apps — VERIFIED, and the story has changed

There are now **two** paths, and the docs describe both:

1. **`aspire deploy` (current, first-class).** Supported targets: Docker/Docker Compose, Kubernetes (Helm-based engine since 13.3), AKS, **Azure Container Apps**, and Azure App Service. `aspire publish` does a one-way artifact handoff preserving unresolved parameters; `aspire deploy` resolves parameters and applies changes directly (and does *not* consume previously published assets). `aspire destroy` (new in 13.3) tears environments down across Azure, K8s and Docker. First run prompts for Azure sign-in, subscription, resource group, location. — https://aspire.dev/deployment/deploy-with-aspire/ · https://aspire.dev/whats-new/aspire-13-3/ · https://learn.microsoft.com/dotnet/aspire/deployment/aspire-deploy/caching-integrations-deployment
2. **`azd` (still documented and supported).** `azd init` detects the AppHost, `azd up` = package → provision → deploy. Mechanically: `azd` runs the AppHost with `--publisher manifest` to emit the Aspire manifest, generates Bicep in-memory, provisions via ARM, uses `dotnet publish` container support to build images, pushes to ACR, then rolls new ACA revisions. `azd config set alpha.infraSynth on; azd infra gen` materialises the Bicep into an `infra/` folder for version control — regenerating overwrites your edits. — https://learn.microsoft.com/dotnet/aspire/deployment/azd/aca-deployment-azd-in-depth

For an OSS accelerator, **`azd infra gen` is the more valuable path**: it produces reviewable, committable Bicep that a reader can learn from and an enterprise can fork, rather than an opaque CLI action.

### Cost/complexity of adopting Aspire in an OSS repo

Honest accounting:

*Costs*
- **Hard Docker requirement.** A contributor with no container runtime cannot `aspire run`. This is the single biggest barrier.
- **Extra global tool.** `dotnet build` / `dotnet test` do not install it; contributors must run an install step. Mitigate with a `dotnet tool install` in a manifest or a documented one-liner.
- **Fast-moving surface.** 13.0 → 13.4 in eight months with real breaking changes (PG 18 data dir; AppHost SDK restructure; dashboard MCP server removed in 13.3). An OSS repo pinned to 13.4.6 will need deliberate upgrade passes.
- **Unfamiliarity.** Most .NET contributors in 2026 have *seen* Aspire but many have not shipped with it. Reviewing an AppHost diff is a new skill.

*Benefits*
- One command replaces a docker-compose file plus a README full of connection strings.
- Free OTel wiring and a dashboard is a genuinely strong demo asset for an accelerator whose whole point is observability of tenant operations.
- Keycloak-in-a-container with realm import solves our offline-auth problem outright (see §4).
- MIT licence, Microsoft-maintained.

**Recommendation: adopt Aspire, but make it optional.** Structure the repo so `dotnet run` against a `docker compose up` Postgres works standalone, and the AppHost is a convenience layer on top. Never let Aspire become the only way to run the app — that is what turns a fast-moving dependency into a contributor tax.

---

## 3. Reference architectures worth stealing from

| Project | URL | License | What's worth borrowing | Copying implications |
|---|---|---|---|---|
| **dotnet/eShop** | https://github.com/dotnet/eShop | **MIT** | The canonical Aspire reference. AppHost composition, ServiceDefaults shape, integration-events/outbox layout, identity service wiring, `azd` deployment. Currently on .NET 9 with a `release/8.0` branch — **not yet updated to .NET 10** (342 commits). | MIT — copy freely with attribution. Safest source of copy-paste in this list. |
| **Azure Architecture Center — multitenancy series** | https://learn.microsoft.com/azure/architecture/guide/multitenant/overview | Docs (CC-BY-4.0 via MicrosoftDocs), **not code** | Three sections: *architectural considerations* (requirements, trade-offs), *architectural approaches* (compute, networking, storage/data, messaging, identity, deployment, governance, cost), *service-specific guidance* (isolation models per Azure service). Plus a design checklist. Explicitly distinguishes "your tenants" from "Microsoft Entra tenants" — terminology we should adopt verbatim. Last reviewed 2025-04-17. | Prose and diagrams. Cite it, paraphrase it, do not lift text wholesale. Zero code risk. |
| **Azure Architecture Center — tenancy models** | https://learn.microsoft.com/azure/architecture/guide/multitenant/considerations/tenancy-models | Docs | The shared-everything ↔ isolated-everything spectrum, with the trade-off axes named (scale, isolation, cost efficiency, performance, complexity, manageability). This is the framing for our own tenancy ADR. | As above. |
| **SaaS and Multitenant Solution Architecture hub** | https://learn.microsoft.com/azure/architecture/guide/saas-multitenant-solution-architecture/ | Docs | Business-model framing (B2B vs B2C vs enterprise platform). | As above. |
| **dotnet/aspire-samples** | https://github.com/dotnet/aspire-samples | **MIT** | Database containers (Postgres/Mongo/SQL), **EF Core migrations sample**, volume-mount persistence, **Polyglot Task Queue** (React + Node API + Python/C# workers over RabbitMQ), Image Gallery (Blob + Queues + **Container Apps Jobs**), Docker Compose deployment configs. 602 commits, active. | MIT. **But** the repo carries an explicit disclaimer that samples "may not illustrate best practices for production environments" — treat as pattern reference, not production code. |
| **ABP Framework** | https://github.com/abpframework/abp | **LGPL-3.0** | The tenant + *edition* model (feature/quota tiers per tenant) is the best-articulated in .NET OSS. Also its `ICurrentTenant` ambient-scope pattern with an explicit `Change(tenantId)` escape hatch for admin/background work. | ⚠️ **LGPL-3.0.** Do not copy code. Reading it and re-implementing an idea is fine; lifting a file is not. Also LGPL makes it a poor *dependency* for a permissively-licensed accelerator that enterprises will vendor and modify. |
| **OrchardCore** | https://github.com/OrchardCore/OrchardCore | **BSD-3-Clause** | Its **tenant shell** architecture: per-tenant DI container + isolated middleware pipeline, tenants created/started at runtime without a restart. If we ever need per-tenant plugin isolation, this is the reference. On `net10.0` (3.0.1, 2026-07-09). | BSD-3-Clause — permissive; copying is fine with attribution + the no-endorsement clause. |
| **SaasKit** | https://github.com/saaskit/saaskit | MIT | Historical interest only — its `ITenantResolver` shape influenced everything after it. | Dead since 2016. Do not depend on it. |

**Licence guidance for our repo:** pick MIT or Apache-2.0. Then our copy sources are eShop (MIT), aspire-samples (MIT), OrchardCore (BSD-3) — all compatible. ABP (LGPL-3.0) and Hangfire Core (LGPL-3.0) are read-only for code purposes.

---

## 4. Auth — OIDC for multi-tenant B2B SaaS on ASP.NET Core 10

### The state of play — VERIFIED

**`Microsoft.Identity.Web` 4.14.2**, published **2026-07-30**, **MIT**, targets net8.0/9.0/10.0 + netstandard2.0 + net462. — https://www.nuget.org/packages/Microsoft.Identity.Web

It gives you `AddMicrosoftIdentityWebApp` / `AddMicrosoftIdentityWebApi`, MSAL token caching, `EnableTokenAcquisitionToCallDownstreamApi`, `MicrosoftIdentityMessageHandler` (auto-attaches bearer tokens to `HttpClient`), `BlazorAuthenticationChallengeHandler` (handles incremental consent + Conditional Access in Blazor Server), and certificateless/managed-identity credentials. There is an official Aspire + Entra ID guide. — https://learn.microsoft.com/entra/msidweb/frameworks/aspire

**Plain `AddOpenIdConnect`** is the provider-neutral path and has caught up substantially:
- **PAR (RFC 9126) is on by default since .NET 9** when the server advertises support. — https://learn.microsoft.com/aspnet/core/security/authentication/configure-oidc-web-authentication?view=aspnetcore-10.0
- Confidential client + code flow + PKCE is the documented recommendation; **public OIDC clients are explicitly no longer recommended for web apps**.
- `AdditionalAuthorizationParameters` lets you inject `prompt`, `audience`, `acr_values` etc. without event plumbing.
- .NET 10 added: auth/authz + Identity **metrics** (visible in the Aspire dashboard), **passkey/WebAuthn support in ASP.NET Core Identity** (in the Blazor Web App template), and cookie-login-redirect suppression for known API endpoints. — https://learn.microsoft.com/aspnet/core/release-notes/aspnetcore-10.0

### Recommendation

**Use provider-neutral `AddOpenIdConnect` as the primary path; treat Entra ID as one configured provider, not the architecture.**

Reasoning, stated as the trade-off: `Microsoft.Identity.Web` is meaningfully better *if and only if* you are Entra-only — the token cache, downstream-API handler and CA challenge handling are real work you'd otherwise write. But it is an Entra-shaped abstraction (`AzureAd` config section, `api://` audiences, MSAL cache), and an OSS accelerator that only authenticates against Entra excludes every contributor and adopter who is not on Entra. For a **B2B multi-tenant SaaS** the tenant→IdP mapping is ours to own anyway (each customer org may bring its own IdP), which is exactly what Finbuckle's `WithPerTenantAuthentication` + `WithPerTenantOptions<OpenIdConnectOptions>` is built for.

Concretely:
- Primary: `AddOpenIdConnect` + cookie, confidential client, code flow + PKCE, PAR when advertised.
- Per-tenant authority/clientId/secret resolved from the tenant store via Finbuckle's per-tenant options.
- Entra ID multi-tenant app registration (`/organizations` or `/common` authority + `issuer` validation against a tenant allow-list) documented as a **recipe**, not the default. ⚠️ The classic Entra multi-tenant trap is disabling issuer validation to accept all tenants — validate the issuer against your own tenant table.
- Add `Microsoft.Identity.Web` only in an optional `samples/entra-id` project, so its Entra-shaped config never contaminates the core.

### What runs offline in a container for contributors

**Keycloak, via the Aspire integration** (or plain docker-compose):

```csharp
var keycloak = builder.AddKeycloak("keycloak", 8080)
                      .WithDataVolume()
                      .WithRealmImport("./realms");   // local dev only
```
— https://aspire.dev/integrations/security/keycloak/

Ship a committed `realms/lakewright-dev.json` containing two or three demo tenant organizations, a couple of users each, and the client registrations. `git clone` → `aspire run` → working multi-tenant login, no cloud account, no network. That is the single highest-leverage contributor-experience decision available to us.

**Zitadel** is the credible alternative: Go binary, lower memory than Keycloak's JVM, API-first, with organizations/projects/scoped-policies as first-class product concepts (a better conceptual fit for B2B SaaS than Keycloak realms). Keycloak 26.x added an Organizations feature that closes some of that gap. Zitadel v4 runs as two containers (Go core + Next.js login UI). — https://www.cerbos.dev/blog/keycloak-vs-zitadel · https://zitadel.com/blog/zitadel-vs-keycloak

**Pick Keycloak** — solely because it has a first-party Aspire hosting *and* client integration and Zitadel does not. That removes ~150 lines of hand-rolled container plumbing and, more importantly, means the pattern is documented on aspire.dev where contributors will find it. If we ever drop Aspire, revisit.

**Auth0** — SaaS-only, no offline story. Fine as a documented production option, wrong as the dev default.

---

## 5. Async / background work

Our shape: **durable operation records, poll an external system (Databricks), survive restarts.** That is a *durable job/state machine over an existing Postgres*, not a message bus.

### Licensing — VERIFIED, and this is where the landscape shifted

| Option | Version / date | License | Notes |
|---|---|---|---|
| `BackgroundService` + Postgres | in-box (.NET 10) | MIT (platform) | Zero deps |
| **Hangfire** Core | **1.8.24**, 2026-07-16 | **LGPL-3.0** | Free, commercial use allowed. Pro (Redis storage, extra libs, support, source access) is $500/$1,500/$4,500 per org per year — perpetual for the purchased version, annual renewal for updates. Targets net451/netstandard1.3/netstandard2.0. |
| **Quartz.NET** | **3.19.1** (July 2026) | **Apache-2.0** | Explicit `net10.0` target. 173.4M downloads, 900+ dependent packages. |
| **MassTransit** | **9.2.0**, 2026-07-27 | ⚠️ **COMMERCIAL** | v9 is a paid product from Massient. "MassTransit is a commercial product that must be licensed." v8 and earlier remain free under their original licence but are **unsupported** under the new agreement. |
| **Wolverine** (`WolverineFx`) | **6.24.2**, 2026-07-30 | **MIT** | Targets net9.0/net10.0. 6.7M downloads. JasperFx's *commercial* product is CritterWatch (BSL), a separate monitoring tool — Wolverine itself stays MIT. |
| **Temporal .NET SDK** | **1.17.0**, 2026-07-13 | MIT (RECALLED — package licence not fetched; repo is MIT) | Needs a Temporal server (extra container) |
| **Azure Durable Functions** (isolated) | ext. **1.18.0**, 2026-07-10 | MIT | ⚠️ **In-process model support ends 2026-11-10** — isolated only |

Sources: https://www.nuget.org/packages/Hangfire · https://www.hangfire.io/pricing/ · https://www.nuget.org/packages/Quartz · https://www.nuget.org/packages/MassTransit · https://massient.com/license · https://www.nuget.org/packages/WolverineFx · https://github.com/JasperFx/wolverine/blob/main/LICENSE · https://github.com/temporalio/sdk-dotnet · https://learn.microsoft.com/azure/durable-task/durable-functions/durable-functions-dotnet-isolated-overview

⚠️ **MassTransit pricing:** search results report $400/mo or $4,000/yr (SMB), $1,200/mo or $12,000/yr (enterprise), and a 100% discount for organisations under $1M USD gross annual revenue. The licence agreement text at https://massient.com/license does **not** state pricing or a revenue-based discount, and explicitly gives **no OSS exemption** for v9+. Treat the pricing figures as **RECALLED** and the "no OSS exemption" as **VERIFIED**.

### Assessment against our criteria

| Option | Operational complexity | Local-dev friendliness | .NET 10 | Fit for "durable operation record + poll external system" |
|---|---|---|---|---|
| **`BackgroundService` + Postgres `SKIP LOCKED`** | Lowest — no new infra | Perfect: the Postgres we already run | Native | **Excellent.** The operation record *is* the domain entity we already need to expose in the API/UI |
| Hangfire | Low (SQL storage) — but its own schema + dashboard | Good | Targets netstandard2.0; runs on .NET 10 | Good for fire-and-forget/recurring; its job model is opaque (serialised method calls), which fights a first-class `Operation` aggregate. **LGPL-3.0 is friction for an accelerator.** |
| Quartz.NET | Low–medium (ADO.NET JobStore, clustering config) | Good | Explicit net10.0 | Good scheduler, weak durable-state model. Great if we need cron; overkill as a queue |
| MassTransit | Medium (broker required) | Needs RabbitMQ container | Yes | ⚠️ **Commercial. Disqualified for an OSS accelerator.** |
| Wolverine | Medium (but has a Postgres-backed durable inbox/outbox) | Good — Postgres-only mode works | net9/net10 | **Strong technical fit**, MIT. Cost is conceptual weight: Wolverine's handler discovery/codegen is a whole programming model to learn |
| Temporal | **High** — server + UI + its own DB | Extra containers, extra concepts | Beta-ish SDK at 1.17.0 | Technically the *best* answer for "survive restarts, poll external system." Wrong cost for an accelerator |
| Durable Functions | High — Functions host + storage; Azure-coupled | Azurite works but is clunky | Isolated worker only after 2026-11-10 | Ties us to Azure Functions; contradicts "runs anywhere" |

### RECOMMENDATION: **`BackgroundService` + Postgres `SELECT ... FOR UPDATE SKIP LOCKED`, with the operation record as a first-class domain entity.**

`SKIP LOCKED` has been in Postgres since 9.5. A worker's claim query never waits on a row another worker holds, so workers do not form a blocking convoy; if a worker crashes mid-job the transaction rolls back, the lock releases, and the row returns to `pending` for another worker. — https://www.netdata.cloud/academy/update-skip-locked/

The trade-off, stated plainly: **we trade a mature scheduler/retry/dashboard for total ownership of our durable state and zero licensing risk.** We will hand-write claim/lease/retry/backoff/dead-letter — call it 300–500 lines with tests. In exchange:

- No new infrastructure and no new licence. Every alternative is either LGPL (Hangfire), commercial (MassTransit), or an extra container (Temporal, Wolverine-with-broker, Durable Functions).
- The operation record is a *product feature*, not a hidden job row: tenants need to see "your Databricks job is running / succeeded / failed," which means we need a queryable, tenant-scoped `Operation` table with our own schema regardless. Hangfire's opaque serialised-invocation model would force us to maintain that table *in addition* to Hangfire's.
- It's the most *teachable* artifact. An accelerator whose background processing is ~400 readable lines of C# + SQL teaches more than one that says "add Hangfire."
- Tenant isolation is under our control, in our schema, filtered by the same mechanism as everything else.

**Escape hatch to document in the ADR:** if we later need cron scheduling, add **Quartz.NET** (Apache-2.0, native net10.0) alongside — it schedules, we still own the durable record. If we later need real distributed sagas, **Wolverine** (MIT) is the upgrade, not MassTransit.

**Explicitly rejected:** MassTransit (commercial as of v9), Temporal (operational cost), Durable Functions (Azure coupling + in-process EOL 2026-11-10).

---

## 6. Blazor vs React for the sample UI

### Blazor state in .NET 10 — VERIFIED

Blazor Web Apps set render mode **per component** via `@rendermode` (project-level server-vs-WASM choice is long gone). Modes: **Static SSR** (no interactivity, fastest, SEO), **Interactive Server** (SignalR circuit), **Interactive WebAssembly** (client runtime), **Interactive Auto** (Server first, then WASM once the runtime is cached). — https://learn.microsoft.com/aspnet/core/blazor/components/render-modes?view=aspnetcore-10.0

.NET 10 additions relevant to us — https://learn.microsoft.com/aspnet/core/release-notes/aspnetcore-10.0:
- **Circuit state persistence** — a Server circuit's state survives a dropped/paused connection (tab throttling, mobile app-switching, network blips) as long as there's no full page refresh. This directly fixes Interactive Server's worst demo failure mode.
- **`[PersistentState]` attribute** — declarative prerender-state persistence, replacing a pile of `PersistentComponentState` boilerplate.
- **Passkey/WebAuthn in the Blazor Web App template** — out-of-the-box passkey management and login UI.
- All WASM client files are fingerprinted+browser-cached; `BlazorCacheBootResources` **removed**.
- `NavigationManager.NavigateTo` no longer throws during static SSR (behaviour now matches interactive).

### Charting

**Blazor** (all verified on nuget.org):
- **Blazor-ApexCharts 7.0.0** (2026-07-24, **MIT**, net8/9/10, 3.2M downloads) — the best-maintained option. ⚠️ Note: ApexCharts 6.0 introduced **premium** interaction modules (history, perspectives, link, ink, measure, contextMenu, storyboard) that run in trial mode **with a watermark** until licensed. The core chart types stay free — but we must not use a premium module in a demo.
- **Plotly.Blazor 7.1.0** — 40+ chart types incl. 3D and SVG maps, MIT, over MIT plotly.js.
- **BlazorExpress.ChartJS** (Blazor Bootstrap) — Apache-2.0, Chart.js wrapper, usable without the rest of the Blazor Bootstrap kit.
- `ChartJs.Blazor` — older, less active.
- Syncfusion/Telerik/DevExpress — commercial, disqualified for OSS.

**React**: Recharts, visx, Nivo, ECharts, Chart.js, Plotly — all MIT, all with far larger example corpora and AI-assistant training coverage.

Sources: https://www.nuget.org/packages/Blazor-ApexCharts · https://www.nuget.org/packages/Plotly.Blazor · https://demos.blazorbootstrap.com/charts

### Contributor-pool argument, both sides

*For Blazor:* the target contributor for a **.NET SaaS accelerator on Databricks** is a .NET backend engineer. Blazor means one language, one solution, one `dotnet build`, one debugger, one test framework. No `npm`, no bundler config, no `node_modules` in CI, no second dependency-audit surface. A backend contributor can fix a UI bug without a context switch. Auth is dramatically simpler: server-side rendering means the OIDC cookie just works, with no token-in-browser storage question and no BFF proxy to build.

*For React:* the global pool of React developers dwarfs the Blazor pool by an order of magnitude. The component/charting ecosystem is deeper, the design-system options (shadcn/ui, Radix, MUI) are better, and — a real 2026 consideration — AI coding assistants produce markedly better React than Blazor because of training-data volume. A React demo also *looks* more like what a SaaS buyer expects.

### RECOMMENDATION: **Blazor Web App, Interactive Server, with Static SSR for content pages.**

**The trade-off, named: we trade a larger contributor pool and a richer component ecosystem for a single-language repo, a trivial auth story, and no Node toolchain in CI.**

For *this* project that trade is right, because the accelerator's value is the backend — tenancy, Databricks integration, durable operations — and the UI exists to demonstrate it. A React SPA would add a second toolchain, a second dependency-audit surface, and a token-handling design (BFF or otherwise) that is pure distraction from what we are actually teaching. Our contributors are .NET people.

Specifics:
- **Interactive Server, not Auto.** Auto's dual-target constraint (every component must run in both Server and WASM contexts, so no direct DbContext/service injection) doubles the cognitive load for no benefit in a demo. .NET 10's circuit-state persistence removes the historical argument against Server.
- **Static SSR for marketing/docs/landing pages** — free performance, free SEO.
- **Charts: Blazor-ApexCharts 7.0.0** (MIT), sticking strictly to non-premium chart types.
- **Hedge:** keep the API surface a clean, documented, OpenAPI-described HTTP API that the Blazor app consumes like any other client. Then "swap in a React front end" is a genuinely available option for adopters rather than a rewrite, and we can say so in the README.

---

## 7. Testing stack

### Current state — VERIFIED

| Package | Version | Date | License | TFMs | Notes |
|---|---|---|---|---|---|
| **xunit.v3** | **3.2.2** | 2026-01-14 | Apache-2.0 | net8.0, net472+ | **Shipped and stable** — 16.5M downloads on 3.2.2 alone, 35.5M total. Used by Jellyfin, Bitwarden, WPF. 4.0.0-pre.154 exists. Supports both Microsoft Testing Platform and VSTest. |
| **TUnit** | **1.63.0** | ~Jul 2026 | RECALLED (MIT per repo, not fetched) | net8.0, netstandard2.0 | Source-generated compile-time discovery, parallel by default, Native AOT + trimming. 4.5M downloads. Their published benchmark: 13.87ms vs xUnit v3's 509.85ms on a data-driven scenario. |
| **NUnit** | not fetched — RECALLED | — | MIT | — | Mature, no compelling reason for a greenfield repo |
| **Testcontainers.PostgreSql** | **4.13.0** | 2026-07-02 | MIT | net8.0, netstandard2.0 (compatible net9/net10) | 34.9M downloads, 49 dependent packages, used by Semantic Kernel and Elsa |
| **WireMock.Net** | **2.13.0** | 2026-07-19 | Apache-2.0 | net8.0, netstandard2.1, net462+ | 50.5M downloads. 2.13.0 added `TestOutputHelperWireMockLogger` for xUnit. Runs in-proc, standalone, or as a container. Used by RestSharp and the k8s client |
| **Microsoft.AspNetCore.Mvc.Testing** | **10.0.10** | 2026-07-14 | MIT | net10.0 | `WebApplicationFactory`. 358.7M downloads |
| **Verify.XunitV3** | **31.27.0** | 2026-07-21 | MIT | net6.0–net11.0, net472/48 | 3.8M downloads. Used by Aspire, MSBuild, WinForms |
| **Verify.Xunit** | 31.12.5 | 2026-02-11 | MIT | — | ⚠️ **DEPRECATED** — "legacy and no longer maintained." Migrate to Verify.XunitV3 |
| **Aspire.Hosting.Testing** | **13.4.6** | 2026-06-19 | MIT | net8/9/10 | `DistributedApplicationTestingBuilder` |

Sources: https://www.nuget.org/packages/xunit.v3 · https://www.nuget.org/packages/TUnit · https://www.nuget.org/packages/Testcontainers.PostgreSql · https://www.nuget.org/packages/WireMock.Net · https://www.nuget.org/packages/Microsoft.AspNetCore.Mvc.Testing · https://www.nuget.org/packages/Verify.XunitV3 · https://www.nuget.org/packages/Verify.Xunit · https://www.nuget.org/packages/Aspire.Hosting.Testing

**Correction to stale commentary:** several 2025-era blog posts describe xUnit v3 as "still prerelease." That is **out of date**. v3.2.2 is stable, shipped 2026-01-14, with 16.5M downloads on that version alone.

### RECOMMENDED STACK

```xml
<PackageVersion Include="xunit.v3"                            Version="3.2.2" />
<PackageVersion Include="Microsoft.AspNetCore.Mvc.Testing"    Version="10.0.10" />
<PackageVersion Include="Testcontainers.PostgreSql"           Version="4.13.0" />
<PackageVersion Include="WireMock.Net"                        Version="2.13.0" />
<PackageVersion Include="Verify.XunitV3"                      Version="31.27.0" />
<PackageVersion Include="Aspire.Hosting.Testing"              Version="13.4.6" />
```

Each choice, with its trade-off:

- **xUnit v3 over TUnit.** TUnit is genuinely faster and technically more modern (source-gen discovery, AOT). We trade that speed for ubiquity: xUnit is what every .NET contributor already knows, what every IDE and CI provider supports without configuration, and — critically — what `Verify` and `Aspire.Hosting.Testing` are documented against. For an OSS accelerator, contributor familiarity beats a 36× benchmark on data-driven tests we won't have many of. Revisit at TUnit 2.x.
- **Testcontainers over an in-memory provider.** Tenant isolation depends on EF Core global query filters generating correct SQL, and `UseInMemoryDatabase` does not generate SQL. Testing tenant isolation against anything but real Postgres is testing nothing. The trade: tests require Docker — which Aspire already requires, so no net new prerequisite.
- **WireMock.Net over hand-rolled `HttpMessageHandler` fakes.** We are polling an external system (Databricks); we need to simulate 429s, 500s, slow responses and state transitions (`PENDING → RUNNING → SUCCESS`). WireMock's stateful scenarios do that declaratively. Apache-2.0, 50.5M downloads, and it added an xUnit logger this month.
- **`Verify.XunitV3`, not `Verify.Xunit`** — the latter is formally deprecated. Use it narrowly: OpenAPI document snapshots, generated SQL for tenant-filtered queries, and the Aspire manifest. Snapshot tests over hand-written assertions everywhere is a maintenance trap.
- **`WebApplicationFactory`** for API-level tests (auth, tenant resolution from header/route/host, authorization) — in-process, fast, no container.
- **`Aspire.Hosting.Testing`** for a small number of true end-to-end tests only. It boots the whole app model, so it is slow; keep it to a smoke suite.

**Test layering:** unit (no I/O, xUnit) → API integration (`WebApplicationFactory` + Testcontainers Postgres + WireMock) → E2E smoke (`Aspire.Hosting.Testing`). Run the first two on every PR; gate the third to main or a nightly job so contributor PR latency stays low.

---

## Open items / lower-confidence flags

1. **MassTransit pricing and the sub-$1M revenue discount** — reported by secondary sources; not present in the licence text at massient.com/license. Verify with Massient directly if it ever matters. The "commercial, no OSS exemption for v9+" conclusion is solid and is what drives our decision.
2. **TUnit and Temporal .NET SDK licences** — inferred MIT from repos, not read off the NuGet package pages. Neither is in our recommended stack.
3. **dotnet/eShop is on .NET 9, not .NET 10** as of this check. Verify before citing it as a .NET 10 exemplar.
4. **ABP commercial split** (ABP Studio / ABP Suite free-vs-paid tiers) not fully mapped. Irrelevant if we take the recommendation to avoid ABP as a dependency.
5. **Aspire 13.5+** may land before we ship. The PG 17→18 data-directory break in 13.4 shows this line ships breaking changes in minor versions — pin exact versions in `Directory.Packages.props` and schedule deliberate upgrade passes.
