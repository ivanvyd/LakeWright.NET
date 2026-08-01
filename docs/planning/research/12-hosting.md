# Lakewright.NET — Hosting & Reference Deployment

Research date: **2026-07-31**. All pricing observed **2026-07-31, region East US, currency USD**, unless stated.

Evidence labels used throughout:
- **[V]** VERIFIED — stated explicitly in a primary source, cited.
- **[V-COMP]** VERIFIED BY COMPOSITION — each half documented in a primary source, cited; the combination was not executed in this session.
- **[R]** RECALLED / secondary source — treat as a lead, not a fact.

---

## 0. Executive answers

| Question | Answer |
| --- | --- |
| Where does the .NET app run? | Azure Container Apps, Consumption plan, `minReplicas: 0` for the demo. |
| Can Databricks Apps host it? | **No.** Two independent blockers: no .NET runtime, and it cannot serve users without a Databricks identity. |
| Can a managed identity reach Databricks with no stored secret? | **Yes on Azure**, fully documented, no client secret. Portable to AWS/K8s via Databricks OAuth token federation. |
| Reference deployment cost | **$0–1/month** demo (scale-to-zero, inside the ACA free grant); **~$11/month** if kept always-warm. |
| Cloud-neutral story | Dockerfile + `docker-compose.yml` is the honest minimum. Helm chart is optional and should be deferred. |

---

## 1. Azure Container Apps

### 1.1 .NET 10 support

ACA does not have a ".NET version" — it runs containers. **[V]**

> "Azure Container Apps supports: Any Linux-based x86-64 (`linux/amd64`) container image; Containers from any public or private container registry; Optional sidecar and init containers"
> — https://learn.microsoft.com/azure/container-apps/containers

Constraints that matter for a .NET image: **[V]**
- `linux/amd64` required. **An `arm64`-only image will not run.** This matters because Apple Silicon and Windows-on-ARM developers will produce arm64 images by default — the Dockerfile/CI must pin `--platform linux/amd64` or build a multi-arch manifest.
- No privileged containers.
- Max image size 8 GB per replica on the Consumption workload profile.
- Source: https://learn.microsoft.com/azure/container-apps/containers#limitations

.NET 10 is the right target: **[V]**

| Version | Released | Type | Support phase | End of support |
| --- | --- | --- | --- | --- |
| .NET 10 | 2025-11-11 | **LTS** | Active | **2028-11-14** |
| .NET 9 | 2024-11-12 | STS | Maintenance | 2026-11-10 |
| .NET 8 | 2023-11-14 | LTS | Maintenance | 2026-11-10 |

.NET 11 is **not released** as of 2026-07-14 (page last updated). Source: https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core

**Note for planning:** .NET 8 and .NET 9 both fall out of support on **2026-11-10**, roughly three months from now. There is no reason to target anything but .NET 10.

### 1.2 vCPU / memory allocations

Consumption plan requires CPU/memory to be one of a fixed set of pairs, always at a 1 vCPU : 2 GiB ratio: **[V]**

`0.25/0.5Gi`, `0.5/1.0Gi`, `0.75/1.5Gi`, `1.0/2.0Gi`, … up to `4.0/8.0Gi`.

> "Apps using the Consumption plan in a *Consumption only* environment are limited to a maximum of 2 cores and 4Gi of memory."
> — https://learn.microsoft.com/azure/container-apps/containers#configuration

Recommended for the accelerator demo: **0.5 vCPU / 1.0 GiB**. ASP.NET Core runs comfortably in 1 GiB; 0.25/0.5Gi is tight once EF Core, the Databricks SQL driver and an OpenTelemetry exporter are loaded.

### 1.3 Scale-to-zero and cold start

Scale-to-zero is the default posture and is genuinely free: **[V]**

> "When a revision is scaled to zero replicas, no resource consumption charges are incurred."
> — https://learn.microsoft.com/azure/container-apps/billing

Idle billing (the cheap rate) is a *different* thing from scaled-to-zero, and the conditions are strict. A replica bills at the reduced **idle** rate only when **all** of: **[V]**
- the revision has `minReplicas > 0`, and
- the revision is scaled *to* the minimum replica count, and
- all containers have started and are running, and
- the replica is not processing any HTTP requests, and
- the replica is using **less than 0.01 vCPU cores**, and
- the replica is receiving **less than 1,000 bytes per second** of network traffic.

Source: https://learn.microsoft.com/azure/container-apps/billing#minimum-number-of-replicas-are-running

> **Planning trap.** The "<0.01 vCPU" condition is easy to violate accidentally. A background `IHostedService` doing a periodic poll, a health-check timer, an OpenTelemetry metrics exporter on a 15s interval, or an EF Core connection-pool keepalive can each push a nominally-idle ASP.NET Core app over 0.01 vCPU and silently move the whole month to the **8× more expensive active rate**. If Lakewright.NET ships background jobs on by default, the always-warm cost estimate below is wrong. Either gate background work behind a flag that the demo profile disables, or budget at active rates.

Cold start guidance — Microsoft publishes mitigation advice but **no latency SLO or published number**: **[V]**

> "When your container app scales to zero during periods of inactivity, the next incoming request triggers a *cold start*. A cold-start is the time-consuming process of pulling your container image, provisioning resources, and starting your application code."
> — https://learn.microsoft.com/azure/container-apps/cold-start

Documented mitigations: shrink the image; keep the registry in the same region as the environment; use storage mounts for large files; implement a custom liveness probe or start listening on the target port early so ACA does not kill a slow-starting container; wake the app proactively on a schedule; instrument startup.

Third-party measurements found (**[R]** — blogs, not primary, and not reproduced here): 1–3 s for small containers, 5–10 s typical for .NET, and 15–30 s for a .NET app with a large DI graph. Treat these as order-of-magnitude only.

**Practical read:** a scale-to-zero demo will make the first visitor of the day wait somewhere in the single-to-low-double-digit seconds. For a public OSS demo that is acceptable *if* the landing page says so. It is not acceptable for anything a prospective user is being asked to evaluate under time pressure.

Two directly relevant mitigations for this project:
1. **Publish trimmed/AOT-friendly and keep the image small.** Use `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled` as the runtime base rather than the full `aspnet:10.0`.
2. **Add a startup liveness allowance.** ASP.NET Core apps that run EF Core migrations at boot are exactly the "application takes a long time to start" case the doc warns gets killed by the default liveness probe.

### 1.4 Managed identity

Both system-assigned and user-assigned identities are supported. **[V]** — https://learn.microsoft.com/azure/container-apps/managed-identity

Known limitation: init containers cannot access managed identities in consumption-only environments and dedicated workload profile environments. **[V]** (Irrelevant here unless migrations move to an init container — worth knowing, because "run EF Core migrations in an init container" is the obvious pattern and it would break MI-based DB auth.)

Mechanism — ACA injects two environment variables and exposes a local token endpoint: **[V]**
- `IDENTITY_ENDPOINT` — local URL to request tokens from.
- `IDENTITY_HEADER` — anti-SSRF header value, rotated by the platform.

```http
GET ${IDENTITY_ENDPOINT}?resource=<RESOURCE_URI>&api-version=2019-08-01
x-identity-header: <IDENTITY_HEADER value>
```

The `resource` parameter is explicitly **not** restricted to a whitelist:

> "The Microsoft Entra resource URI of the resource for which a token should be obtained. The resource could be one of the Azure services that support Microsoft Entra authentication **or any other resource URI**."
> — https://learn.microsoft.com/azure/container-apps/managed-identity#rest-endpoint-reference (emphasis added)

This is the hinge for §5. For .NET the recommended abstraction over this endpoint is `Azure.Identity` / `DefaultAzureCredential`. **[V]**

> "When using Azure Identity client library, you need to explicitly specify the user-assigned managed identity client ID."

Also worth adopting: `identitySettings.lifecycle` (API `2024-02-02-preview`+) restricts which containers can use which identity — `Init` / `Main` / `All` / `None`. An ACR-pull identity should be `None` so application code cannot borrow it. **[V]**

Caveat that will bite during setup: **[V]**

> "The back-end services for managed identities maintain a cache per resource URI for around 24 hours. If you update the access policy of a particular target resource and immediately retrieve a token for that resource, you may continue to get a cached token with outdated permissions until that token expires. Forcing a token refresh isn't supported."

So after granting the identity a Databricks role, a stale-permission token may persist. Restart the revision; do not conclude the wiring is broken.

### 1.5 Ingress, custom domains, TLS

Every container app with external HTTP ingress gets an auto-generated FQDN with TLS at no cost. Custom domains get a **free managed certificate**, auto-renewed. **[V]** — https://learn.microsoft.com/azure/container-apps/custom-domains-managed-certificates

Requirements: **[V]**
- HTTP ingress enabled and publicly reachable from the DigiCert validation IPs.
- Apex domain → `A` record to the environment's static IP, plus `TXT` at `asuid`.
- Subdomain → `CNAME` to the app's generated FQDN, plus `TXT` at `asuid.<sub>`.
- If a CAA record exists on the root domain, it must explicitly allow DigiCert: `0 issue digicert.com`.

> **Gotcha worth writing into the docs:** "Mapping to an intermediate CNAME value blocks certificate issuance and renewal. Examples of CNAME values are traffic managers, **Cloudflare**, and similar services." Putting the demo behind Cloudflare proxy (orange cloud) will break managed-cert renewal. Many OSS maintainers front everything with Cloudflare by reflex.

### 1.6 Pricing — real figures

Retrieved **2026-07-31** from the Azure Retail Prices API (`serviceName eq 'Azure Container Apps' and armRegionName eq 'eastus' and priceType eq 'Consumption'`), currency USD, region East US.

| Meter | Rate | Unit |
| --- | --- | --- |
| Standard vCPU **Active** Usage | **$0.000024** | 1 second (per vCPU) |
| Standard vCPU **Idle** Usage | **$0.000003** | 1 second (per vCPU) |
| Standard Memory **Active** Usage | **$0.000003** | 1 GiB-second |
| Standard Memory **Idle** Usage | **$0.000003** | 1 GiB-second |
| Standard Requests | **$0.40** | 1M requests |
| Dedicated Plan Management | $0.10 | 1 hour |
| Dedicated vCPU Usage | $0.057077 | 1 hour |
| Dedicated Memory Usage | $0.004978 | 1 hour |

Note the active/idle spread is **8× on vCPU** and **1× on memory** (memory active and idle are the same rate, $0.000003/GiB-s). Idle mode therefore only saves money on CPU.

Free monthly grant, **per subscription per calendar month**: **[V]**
- First **180,000 vCPU-seconds**
- First **360,000 GiB-seconds**
- First **2,000,000 HTTP requests**

> "Free usage doesn't appear on your bill. You're only charged when your resource usage exceeds the monthly free grants amounts."
> — https://learn.microsoft.com/azure/container-apps/billing#consumption-plan

Health probe requests are **not** billable, and only requests originating outside the environment are billable. **[V]**

**The grant is per subscription, not per app.** If the maintainer runs other container apps in the same subscription, the demo does not get the full grant. This is the single most likely reason a "$0" estimate turns into a real bill.

#### Scenario A — scale-to-zero demo (recommended)

`minReplicas: 0`, `0.5 vCPU / 1.0 GiB`, assume ~2 h/day of actual serving = 60 h/month = 216,000 seconds active.

| Line | Quantity | vs. grant | Cost |
| --- | --- | --- | --- |
| vCPU-seconds | 0.5 × 216,000 = **108,000** | under 180,000 | **$0.00** |
| GiB-seconds | 1.0 × 216,000 = **216,000** | under 360,000 | **$0.00** |
| Requests | far under 2M | under grant | **$0.00** |
| **ACA compute total** | | | **$0.00 / month** |

Scale-to-zero means no charge at all while idle, so the only consumption is the serving window. **A light public demo fits entirely inside the free grant.**

#### Scenario B — always-warm (`minReplicas: 1`), same size

730 h/month = 2,628,000 s total; assume 60 h active, 670 h idle. Assumes the app genuinely stays under 0.01 vCPU when idle (see the §1.3 trap).

| Line | Quantity | Billable after grant | Rate | Cost |
| --- | --- | --- | --- | --- |
| vCPU-s active | 108,000 | 0 (grant) | — | $0.00 |
| vCPU-s idle | 1,206,000 | 1,134,000 | $0.000003 | **$3.40** |
| GiB-s active | 216,000 | 0 (grant) | — | $0.00 |
| GiB-s idle | 2,412,000 | 2,268,000 | $0.000003 | **$6.80** |
| Requests | <2M | 0 | — | $0.00 |
| **ACA compute total** | | | | **≈ $10.20 / month** |

(180,000 vCPU-s and 360,000 GiB-s of grant applied to active usage first, remainder against idle.)

If the idle conditions are *not* met — background jobs keep CPU above 0.01 vCPU — the same workload bills at active rates: vCPU 1,314,000 − 180,000 = 1,134,000 × $0.000024 = **$27.22** plus memory $6.80 = **≈ $34/month**. That is a 3.3× miss, and it is the realistic outcome for an app with a default-on background scheduler.

#### Ancillary costs not in the meters above

- **Log Analytics workspace** — an ACA environment sends logs to Log Analytics (or Azure Monitor). A demo app's volume is small; estimate well under $1/month. **[R]** — not priced via the API in this session.
- **Container registry** — use **GitHub Container Registry (`ghcr.io`)**, free for public images, rather than Azure Container Registry. This removes a cost line *and* is the correct choice for an OSS project (contributors can pull without an Azure account). Note the §1.3 tradeoff: a registry outside Azure adds image-pull latency to cold start.
- **Virtual network** — not used in the reference deployment. ACA warns that BYO-vnet adds charges. **[V]**

### 1.7 `azd` and .NET Aspire

`azd` (Azure Developer CLI) has first-class Aspire support: it detects an Aspire app host and provisions a resource group, Azure Container Registry, Log Analytics workspace, and Container Apps environment, then deploys. **[V]** — https://learn.microsoft.com/dotnet/aspire/deployment/azure/aca-deployment-visual-studio

Aspire 9.2+ adds `aspire publish` / `aspire deploy` with extensible publishers; for Azure specifically, `azd` remains the recommended path. **[R]** — https://aspire.dev/deployment/azure/container-apps/ (vendor doc, but secondary to Learn for version specifics; the 9.2 version boundary was not independently confirmed).

**Recommendation for Lakewright.NET: adopt Aspire for local orchestration (F5 → app + Postgres + seeded Databricks config), but do NOT make `azd`/Aspire the only deployment path.** Reasons:
1. Aspire's `azd` path provisions ACR by default, which contradicts the free-`ghcr.io` recommendation above and adds a cost line.
2. Aspire's ACA generation is Azure-specific; making it the primary path silently violates the "must not be locked to one cloud" constraint.
3. Aspire is a large concept to put in front of a contributor whose actual goal is "see the Databricks bits."

Ship Aspire as the *inner-loop* story and a hand-written Bicep module as the *deployment* story. They serve different audiences.

---

## 2. Azure App Service

### 2.1 Free (F1) tier — not viable for a credible demo

| Constraint | Detail |
| --- | --- |
| Custom domains | **Not supported on Free (F1).** The plan must be a paid tier. **[R]** |
| Custom TLS / SSL binding | Not supported on F1 or D1. App Service managed certificates require **Basic, Standard, Premium, or Isolated**. **[R]** |
| CPU quota | **60 CPU-minutes per day**, shared per region per subscription across all Free apps. On exhaustion the app is stopped with `Error 403 – Web app is stopped (Quota exceeded)`. **[R]** |
| Price | $0.00/hour (`F1 App`, Azure App Service Free Plan - Linux, East US, 2026-07-31). **[V]** |

The 60 CPU-min/day quota is the killer: a demo that gets shared on Hacker News or LinkedIn will hard-stop partway through the day and stay down. The `*.azurewebsites.net` hostname does get TLS, so F1 is usable for a *private* scratch deploy — but not for a public demo.

Sources for the F1 limits are Microsoft Q&A and community pages rather than a single canonical limits table; labelled **[R]** and should be re-confirmed before being written into project docs as fact:
- https://learn.microsoft.com/en-us/azure/app-service/app-service-web-tutorial-custom-domain
- https://learn.microsoft.com/en-us/azure/app-service/configure-ssl-certificate
- https://learn.microsoft.com/en-us/answers/questions/1348827/monitor-cpu-time-usage-on-free-f1-app-service-pric

### 2.2 Paid tiers — real prices

Azure Retail Prices API, `serviceName eq 'Azure App Service'`, Linux products, East US, USD, retrieved **2026-07-31**. Monthly = hourly × 730.

| SKU | Product | $/hour | $/month |
| --- | --- | --- | --- |
| F1 | Free Plan - Linux | 0.000 | **$0.00** |
| B1 | Basic Plan - Linux | 0.017 | **$12.41** |
| B2 | Basic Plan - Linux | 0.034 | $24.82 |
| B3 | Basic Plan - Linux | 0.067 | $48.91 |
| S1 | Standard Plan - Linux | 0.095 | $69.35 |
| P0v3 | Premium v3 Plan - Linux | 0.0775 | $56.58 |
| P1v3 | Premium v3 Plan - Linux | 0.155 | $113.15 |
| P0v4 | Premium v4 Plan - Linux | 0.073 | $53.29 |
| P1v4 | Premium v4 Plan - Linux | 0.146 | $106.58 |

B1 is the cheapest tier that supports custom domains and managed certificates: **$12.41/month, billed whether or not anyone visits.**

### 2.3 App Service vs ACA

| | ACA (scale-to-zero) | ACA (always-warm) | App Service B1 |
| --- | --- | --- | --- |
| Monthly | **$0.00** | ~$10.20 | **$12.41** |
| Cold start | Yes (seconds) | No | No |
| Custom domain + free TLS | Yes | Yes | Yes |
| Managed identity | Yes | Yes | Yes |
| Deploy artifact | OCI image | OCI image | OCI image or zip |

App Service **is** a legitimate choice — it supports managed identity and .NET 10 containers fine, and B1 avoids cold start for $12.41. But it is a worse fit for this project on one axis that matters more than price: **App Service's deployment model is Azure-shaped**, whereas an ACA deployment is "run this OCI image with these env vars," which is exactly what a Kubernetes or Compose user also does. Choosing ACA keeps the container the single unit of deployment across every target, which is the whole point of the cloud-neutrality constraint.

---

## 3. Databricks Apps as the host — ruled out

**Verdict: Databricks Apps cannot host Lakewright.NET.** Two independent, each-sufficient blockers.

### 3.1 Blocker 1 — no .NET runtime

Databricks Apps supports Python and Node.js only. **[V]**

> "Develop apps locally using Python or Node.js, then deploy them to a workspace."
> "Popular Python frameworks include Streamlit, Dash, and Gradio. Node.js frameworks such as React, Angular, Svelte, and Express are also supported."
> — https://docs.databricks.com/aws/en/dev-tools/databricks-apps/

No container or bring-your-own-image option is documented. The `app.yaml` contract is a `command` plus `env` executed inside a Databricks-managed base image; there is no published mechanism for supplying your own image or installing a .NET runtime. **[V]** — https://docs.databricks.com/aws/en/dev-tools/databricks-apps/app-runtime

### 3.2 Blocker 2 — cannot serve external customers (decisive)

This is the one that ends the discussion, and it would end it even if .NET were supported.

> "You can't make Databricks apps public. Anonymous access and bypassing single sign-on (SSO) are not supported."
> — https://docs.databricks.com/aws/en/dev-tools/databricks-apps/permissions

The authorization model is OAuth 2.0 combining the app's service principal permissions with those of the **authenticated Databricks user** accessing it: **[V]**

> "The Databricks Apps authorization model is based on OAuth 2.0 and combines the permissions assigned to the app with those of the user accessing it."
> — https://docs.databricks.com/aws/en/dev-tools/databricks-apps/auth

The broadest possible grant is `All account users` — "All users and service principals in the current Databricks account." **[V]** External collaborators must be onboarded into your identity provider via SCIM/JIT provisioning, i.e. they become identities in *your* Databricks account. **[V]**

**Plainly: every end user of a Databricks App must be a principal in your Databricks account.** A customer-facing SaaS by definition has users who are not. Databricks Apps is an internal-tools platform, not a SaaS hosting platform.

### 3.3 Resource limits (for completeness)

| Size | CPU | Memory | Cost |
| --- | --- | --- | --- |
| `Medium` (default) | Up to 2 vCPUs | 6 GB | 0.5 DBU/hour |
| `Large` | Up to 4 vCPUs | 12 GB | 1 DBU/hour |

Source: https://learn.microsoft.com/en-us/azure/databricks/dev-tools/databricks-apps/compute-size **[V]**
Horizontal scaling across instances exists but is in **Beta**. **[V]**

### 3.4 Where Databricks Apps *does* fit

Not as the product, but potentially as a second, optional deliverable: an internal admin/ops console for the *operator* of a Lakewright.NET deployment (tenant onboarding, usage inspection, job status) — a population that genuinely all have Databricks accounts. That would be Python/Streamlit, not .NET, and should be scoped as a separate optional sample, not part of the core accelerator. Worth one sentence in the README to pre-empt "why don't you use Databricks Apps?", which will otherwise be the most common question the project receives.

---

## 4. Cloud-neutral option

### 4.1 What comparable OSS ships

| Project | Ships |
| --- | --- |
| ABP Framework | Dockerfiles + build scripts, Docker Compose, **and** Helm charts for Kubernetes **[R]** |
| eShopOnContainers (archived; → `dotnet/eShop`) | Docker Compose + Helm charts for AKS **[R]** |
| fullstackhero/dotnet-starter-kit (.NET 10) | Docker Compose **[R]** |
| boxyhq/saas-starter-kit | `docker-compose.yml` **[R]** |

Sources: https://abp.io/docs/latest/solution-templates/microservice/helm-charts-and-kubernetes, https://github.com/dotnet-architecture/eShopOnContainers, https://github.com/fullstackhero/dotnet-starter-kit, https://github.com/boxyhq/saas-starter-kit. These are secondary/vendor pages surveyed at a distance, not audited repo-by-repo — labelled **[R]**.

The pattern: **Docker Compose is universal; Helm appears only in microservices-shaped projects.** ABP and eShop ship Helm because they have 8–15 services to orchestrate. A single-service accelerator does not clear that bar.

### 4.2 The honest minimum

**Ship a `Dockerfile` and a `docker-compose.yml`. Do not ship a Helm chart at launch.**

- **`Dockerfile`** — multi-stage, `mcr.microsoft.com/dotnet/sdk:10.0` → `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled`, built `--platform linux/amd64` (see §1.1). This one file *is* the portability story: it is what ACA runs, what Cloud Run runs, what ECS runs, and what a Kubernetes `Deployment` runs.
- **`docker-compose.yml`** — app + Postgres, so `docker compose up` gives a working local instance with no Azure account. This is the contributor on-ramp and matters more than any cloud deployment target.
- **`infra/main.bicep`** — the Azure reference deployment. Explicitly labelled as *a* reference, not *the* deployment.

A Helm chart written before a single user has asked for one is a maintenance liability: it must track every app config change, it cannot be meaningfully tested in CI without a cluster, and a stale chart is worse than no chart. Add it when someone opens an issue asking for it — and note that Databricks' own workload-identity-federation docs give Kubernetes a **documented, secret-free** auth path (§5.4), so the Helm story is technically strong whenever it is written.

What makes the "runs anywhere" claim credible is not the number of manifests — it is that **the app takes all configuration from environment variables and acquires credentials through a pluggable provider** (§5.5). Ship that, and one Dockerfile is a complete portability story. Ship five manifests around a hard-coded `DefaultAzureCredential`, and it is not.

---

## 5. Managed identity → Databricks (the key security question)

### 5.1 Answer: yes, on Azure, with no stored secret

The Azure Databricks Entra resource ID is confirmed: **[V]**

> "The resource ID `2ff814a6-3304-4ab8-85cb-cd0e6f879c1d` is the standard identifier for Azure Databricks across all Azure environments."
> — https://learn.microsoft.com/en-us/azure/databricks/dev-tools/auth/aad-token-manual

An Entra access token with that audience is accepted directly as a bearer token by the Databricks REST API: **[V]**

```bash
curl -X GET \
  -H 'Authorization: Bearer <access-token>' \
  https://<databricks-instance>/api/2.0/clusters/list
```

Expected token claims: `aud` = `2ff814a6-3304-4ab8-85cb-cd0e6f879c1d`, `iss` = `https://sts.windows.net/<tenant-id>/`, `tid` = workspace tenant ID. **[V]**

Managed identities are explicitly supported as a Databricks authentication mechanism, and Databricks treats them as service principals: **[V]**

> "Azure automatically manages identities in Microsoft Entra ID for applications to authenticate with resources that support Microsoft Entra ID authentication, including Azure Databricks accounts and workspaces. This authentication method obtains Microsoft Entra ID tokens **without requiring you to manage credentials**."
> — https://learn.microsoft.com/en-us/azure/databricks/dev-tools/auth/azure-mi-auth

> "Databricks recommends using user-assigned managed identities for Azure managed identities authentication with Azure Databricks."

### 5.2 The exact flow for an Azure Container App

**Setup (one-time):**

1. Create a **user-assigned** managed identity. Copy its **Client ID**. **[V]** (azure-mi-auth Step 1)
2. Assign it to the Container App: `az containerapp identity assign -g <rg> -n <app> --user-assigned <identity-resource-id>` **[V]**
3. Add the MI to the Databricks **account** as a service principal — choose **Microsoft Entra ID managed** and paste the MI's **Client ID** as the Microsoft Entra application ID. **[V]** (azure-mi-auth Step 2)
4. Assign that service principal to the **workspace**. If the workspace is not identity-federated, use the MI's Client ID as the `ApplicationId`. **[V]** (azure-mi-auth Step 3)
5. Grant it Unity Catalog / SQL warehouse permissions as needed.

**Runtime (in the .NET app):**

```csharp
// Azure.Identity
var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
{
    ManagedIdentityClientId = "<user-assigned-mi-client-id>"   // required for UAMI
});

// Azure Databricks resource ID -> .default scope
var token = await credential.GetTokenAsync(
    new TokenRequestContext(["2ff814a6-3304-4ab8-85cb-cd0e6f879c1d/.default"]),
    ct);

request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
```

**No client secret anywhere.** **[V-COMP]** — Each half is documented: ACA's token endpoint accepts "any other resource URI" (§1.4) and `2ff814a6-…` is the documented Databricks resource ID accepted as a bearer token (§5.1). The `.default` scope form is documented for MSAL against this exact resource. The composition (ACA MI → Databricks REST) was **not executed in this session** and should be smoke-tested before it goes in the README as a claim.

Two setup notes that will cost time if missed:
- `ManagedIdentityClientId` is **mandatory** for user-assigned identities — `DefaultAzureCredential` otherwise tries the system-assigned identity. **[V]** (§1.4)
- After step 5, the ~24h managed-identity token cache (§1.4) can serve stale permissions. Restart the revision.

### 5.3 Critical gap: the Databricks SDKs do NOT implement this

This is the finding most likely to be missed, and it directly shapes the accelerator's design.

> "**Databricks Connect:** Databricks Connect relies on the Databricks SDK for Python for authentication. The Databricks SDK for Python has not yet implemented Azure managed identities authentication."
> "**Python:** The Databricks SDK for Python has not yet implemented Azure managed identities authentication."
> "**Java:** The Databricks SDK for Java has not yet implemented Azure managed identities authentication."
> "**VS Code:** The Databricks extension for Visual Studio Code does not yet support Azure managed identities authentication."
> — https://learn.microsoft.com/en-us/azure/databricks/dev-tools/auth/azure-mi

Only the **Go SDK**, **Terraform provider**, and **Databricks CLI** implement Azure MI auth today. **[V]**

There is no official Databricks SDK for .NET at all. Combined with the above, this means:

**Lakewright.NET must implement token acquisition itself — and that is an advantage, not a problem.** The implementation is a `TokenCredential` plus an `HttpClient` `DelegatingHandler`, perhaps 30 lines, and it lands the project in a strictly better position than the Python and Java SDKs. "Secret-free managed-identity auth to Databricks, which the official Python and Java SDKs still don't do" is a genuine, checkable differentiator for the README.

### 5.4 Portability — AWS, GCP, Kubernetes

The portable mechanism is **Databricks OAuth token federation** (workload identity federation), which works on all three clouds and is independent of Entra.

A service principal federation policy accepts an **arbitrary OIDC issuer**: **[V]**

> "**Issuer URL:** An HTTPS URL that identifies the workload identity provider, specified in the `iss` claim of workload identity tokens."
> "If unspecified, Azure Databricks retrieves the keys from the issuer's well-known endpoint, which is the recommended approach. Your identity provider must serve OpenID Provider Metadata at `<issuer-url>/.well-known/openid-configuration` that includes a `jwks_uri`…"
> — https://learn.microsoft.com/en-us/azure/databricks/dev-tools/auth/oauth-federation-policy

> "Databricks strongly recommends using workload identity federation to authenticate to Databricks from automated workloads whenever possible, as it eliminates the need for managing and rotating Databricks secrets."

Documented issuer examples (all **[V]**, from the federation-policy table):

| Runtime | Issuer | Subject |
| --- | --- | --- |
| **Kubernetes** (any cloud) | `https://kubernetes.default.svc` | `system:serviceaccount:<ns>:<sa>` |
| **AWS** (Lambda / ECS / EC2) | `https://<uuid>.tokens.sts.global.api.aws` | `arn:aws:iam::<account>:role/<role-name>` |
| GitHub Actions | `https://token.actions.githubusercontent.com` | `repo:<org>/<repo>:environment:prod` |
| Azure DevOps | `https://vstoken.dev.azure.com/<org_id>` | `sc://<org>/<project>/<connection>` |
| GitLab / CircleCI | (per table) | (per table) |

For AWS: "the `sub` claim in the token is the IAM role ARN of the calling workload (for example, the Lambda execution role, ECS task role, or EC2 instance role)." **[V]**

**Honest portability statement:**

| Target | Secret-free path | Status |
| --- | --- | --- |
| Azure Databricks + Azure Container Apps | Entra token for `2ff814a6-…`, direct bearer | **[V]** documented end to end |
| Any Databricks + **Kubernetes** (EKS/GKE/AKS/on-prem) | SA token → federation policy | **[V]** documented |
| **AWS** Databricks + Lambda/ECS/EC2 | AWS IAM outbound identity federation | **[V]** documented |
| **GCP** Databricks + Cloud Run/GCE | No GCP-specific example published | **[R]** — GKE works via the Kubernetes row; native Cloud Run/GCE was **not** confirmed. Do not claim it. |
| Non-Azure Databricks + **Azure** compute (Entra as issuer) | Plausible: register `https://login.microsoftonline.com/<tenant>/v2.0` as issuer | **INFERRED, NOT VERIFIED** — Entra is a conforming OIDC issuer and the policy accepts arbitrary issuers, but this combination is not in Databricks' documented examples and was not tested. Do not ship as a claim. |

**Bottom line: "no stored secret" is achievable on Azure and Kubernetes and AWS with documented, first-party mechanisms.** That is a strong and truthful portability claim. The two weak cells above are narrow and should simply not be asserted.

### 5.5 Design implication

Define a single seam and implement it three ways:

```
IDatabricksCredentialProvider
├── EntraManagedIdentityProvider   // Azure: DefaultAzureCredential → 2ff814a6-…/.default   [V]
├── OidcFederationProvider         // K8s SA token / AWS IAM / CI OIDC → Databricks token exchange   [V]
└── PersonalAccessTokenProvider    // local dev only; loud warning if used outside Development
```

This is what makes the cloud-neutrality claim real rather than aspirational, and it keeps `DefaultAzureCredential` from leaking into the core.

---

## 6. Secrets

### 6.1 Layered recommendation

| Layer | Mechanism | Use for |
| --- | --- | --- |
| **0 — best** | **No secret at all** — managed identity / workload identity federation (§5) | Databricks, Azure SQL, Key Vault, Storage |
| 1 — local dev | **.NET user-secrets** (`dotnet user-secrets`) | Dev-only PATs, local connection strings. Never in the repo. |
| 2 — deployed, unavoidable secrets | **Azure Key Vault**, referenced from Container Apps secrets, read via managed identity | Third-party API keys, SMTP, Stripe |
| 3 — config, not secrets | Plain environment variables | Workspace URL, warehouse ID, catalog name, feature flags |
| — avoid | Container Apps inline secret values in production | Explicitly discouraged by Microsoft |
| — not applicable | Databricks secret scopes | Scoped to code *running inside* Databricks (notebooks/jobs), not an external ASP.NET Core app |

The layering principle: **every secret you don't have is a secret you can't leak.** Layer 0 should cover Databricks entirely, which is the highest-value credential in the system.

### 6.2 Key Vault + Container Apps integration

Container Apps natively resolves Key Vault references — the app never sees the vault. **[V]** — https://learn.microsoft.com/en-us/azure/container-apps/manage-secrets

> "Avoid specifying the value of a secret directly in a production environment. Instead, use a reference to a secret stored in Azure Key Vault."

Setup: enable a managed identity on the app, then grant it the **Key Vault Secrets User** RBAC role on the vault. **[V]**

```bash
az containerapp create \
  -g <rg> -n <app> --environment <env> --image <image> \
  --user-assigned "<UAMI_RESOURCE_ID>" \
  --secrets "some-api-key=keyvaultref:<KEY_VAULT_SECRET_URI>,identityref:<UAMI_RESOURCE_ID>" \
  --env-vars "SomeApiKey=secretref:some-api-key"
```

Bicep/ARM form: `{ "name": "...", "keyVaultUrl": "<uri>", "identity": "system" }`. **[V]**

Rotation behaviour: **[V]**
- URI **without** a version → app picks up the latest version automatically **within 30 minutes**, and any active revision referencing it in an env var is **automatically restarted** to pick up the new value.
- URI **with** a version → pinned; full control, manual rotation.

For an accelerator, omit the version so rotation is automatic — but document the auto-restart, because an unexplained restart during a demo is alarming.

Two operational notes: **[V]**
- **System-assigned identity cannot be used with `az containerapp create`** — it does not exist until after the app is created. Use a **user-assigned** identity, which also matches the Databricks recommendation in §5.1. This makes UAMI the correct choice on both counts.
- Secrets are scoped to the app, not the revision; changing one does **not** create a new revision. Deploy a new revision or restart to pick up changes.
- If UDR-with-Azure-Firewall is ever used, allow the `AzureKeyVault` service tag and `login.microsoft.com`.

Secrets can also be mounted as files in a volume (one file per secret) rather than env vars — useful for anything multi-line. **[V]**

---

## 7. Free/cheap demo hosting for the OSS project

### 7.1 The options

| Option | Monthly | Notes |
| --- | --- | --- |
| **ACA scale-to-zero on the maintainer's subscription** | **$0** (inside free grant) | Cold start on first hit. Same platform the docs describe. |
| ACA always-warm (`minReplicas: 1`) | ~$10.20 | No cold start — if idle conditions hold (§1.3). |
| App Service B1 | $12.41 | No cold start, flat, no custom-domain caveats. |
| App Service F1 | $0 | 60 CPU-min/day kill switch, no custom domain. Unusable publicly. |
| Render free tier | $0 | Sleeps on inactivity; 512 MB. **[R]** |
| Fly.io | ~$2–25 | Free tier discontinued; trial is 2 VM-hours / 7 days. **[R]** |
| Hetzner + Coolify | ~$4–5 | Cheapest always-on, but it is a server you now operate. **[R]** |

Third-party pricing is **[R]** — aggregator blogs, not vendor pricing pages, and volatile. Re-check before publishing any of it.

### 7.2 Recommendation

**Do not run a live public demo at launch. Ship a recorded demo plus screenshots, and a one-command local path.**

Reasoning:

1. **The live demo is not the bottleneck on adoption.** Someone evaluating a Databricks SaaS accelerator needs to see it against *their* Databricks workspace with *their* data. A shared public demo cannot show that — it can only show a UI, which a 90-second screen recording shows equally well at zero risk.
2. **A live multi-tenant demo against a real Databricks workspace is a security liability.** It means a publicly reachable app holding credentials to a live workspace, with anonymous users driving queries. That is an unbounded compute bill and a genuine data-exposure surface. For a project whose entire pitch is secure multi-tenant Databricks access, having the demo be the weakest link is a bad trade.
3. **A stale or broken demo is worse than none.** An unreachable demo link on the README is read as "abandoned."
4. **The costs are asymmetric.** A recording costs an hour once. A live demo costs the always-warm bill plus the Databricks SQL warehouse behind it — which is the real expense and is *not* $10/month — plus ongoing operational attention.

The Databricks-side cost is the point people miss: the ACA compute is ~$10/month, but a SQL warehouse serving the demo is the dominant line item, and a serverless warehouse woken by anonymous traffic has no natural ceiling.

**If a live demo is wanted later**, the shape that works:
- ACA, `minReplicas: 0`, ~$0/month compute.
- Backed by **synthetic seed data in Postgres**, not a live Databricks workspace — demonstrates the app; risks nothing.
- README states "first load takes a few seconds — the demo scales to zero," which turns the cold start from a defect into a demonstrated feature.
- Budget alert on the subscription at $20.

That version is genuinely ~$0–1/month and carries almost no risk. It is worth doing **after** the project has users, not before.

---

## 8. Recommended reference deployment

```
GitHub repo
 ├── src/                       ASP.NET Core 10 app
 ├── Dockerfile                 multi-stage → aspnet:10.0-noble-chiseled, linux/amd64
 ├── docker-compose.yml         app + Postgres — the contributor on-ramp
 ├── infra/main.bicep           Azure reference deployment
 └── .github/workflows/         build → push ghcr.io → az containerapp update
```

**Azure resources:**

| Resource | SKU / config | Monthly |
| --- | --- | --- |
| Container Apps environment | Consumption workload profile | $0 (no dedicated profile → no plan management fee) |
| Container App | 0.5 vCPU / 1.0 GiB, `minReplicas: 0`, external HTTP ingress | **$0.00** (inside free grant) |
| User-assigned managed identity | — | $0 |
| Key Vault | Standard, ~2 secrets | <$0.10 |
| Log Analytics workspace | pay-as-you-go, tiny volume | <$1 **[R]** |
| Container registry | **ghcr.io** (public, free) | $0 |
| **Total** | | **≈ $0–1 / month** |

Always-warm variant (`minReplicas: 1`): **≈ $11/month**, subject to the §1.3 idle-conditions caveat.

**Auth posture:** user-assigned managed identity → Entra token for `2ff814a6-3304-4ab8-85cb-cd0e6f879c1d` → Databricks REST API. Zero stored Databricks credentials. Key Vault for third-party secrets only, resolved by the same identity.

**Why this shape:**
- The unit of deployment is an OCI image, so ACA / Kubernetes / Compose / ECS are all the same artifact — the cloud-neutrality constraint is satisfied structurally, not by paperwork.
- Costs nothing at rest, so an OSS maintainer can leave it deployed indefinitely.
- `ghcr.io` means contributors pull images without an Azure account.
- The one Azure-specific piece (`EntraManagedIdentityProvider`) sits behind an interface with two documented non-Azure siblings.

**Open items to resolve before publishing claims:**
1. Smoke-test the ACA-MI → Databricks REST call end to end (§5.2) — documented on both sides, not executed here.
2. Re-confirm the App Service F1 limits against a canonical Microsoft limits table; current sources are Q&A pages.
3. Measure actual cold start for the real image before writing any number in the README.
4. Do not assert GCP Cloud Run or Entra-as-federation-issuer support (§5.4) without testing.

---

## Sources

**Azure Container Apps**
- https://learn.microsoft.com/azure/container-apps/containers
- https://learn.microsoft.com/azure/container-apps/billing
- https://learn.microsoft.com/azure/container-apps/managed-identity
- https://learn.microsoft.com/azure/container-apps/manage-secrets
- https://learn.microsoft.com/azure/container-apps/custom-domains-managed-certificates
- https://learn.microsoft.com/azure/container-apps/cold-start
- https://azure.microsoft.com/en-us/pricing/details/container-apps/
- Azure Retail Prices API, 2026-07-31, East US, USD

**Azure Databricks auth**
- https://learn.microsoft.com/en-us/azure/databricks/dev-tools/auth/azure-mi
- https://learn.microsoft.com/en-us/azure/databricks/dev-tools/auth/azure-mi-auth
- https://learn.microsoft.com/en-us/azure/databricks/dev-tools/auth/aad-token-manual
- https://learn.microsoft.com/en-us/azure/databricks/dev-tools/auth/oauth-federation-policy
- https://learn.microsoft.com/en-us/azure/databricks/dev-tools/auth/oauth-federation-provider

**Databricks Apps**
- https://docs.databricks.com/aws/en/dev-tools/databricks-apps/
- https://docs.databricks.com/aws/en/dev-tools/databricks-apps/auth
- https://docs.databricks.com/aws/en/dev-tools/databricks-apps/permissions
- https://docs.databricks.com/aws/en/dev-tools/databricks-apps/app-runtime
- https://learn.microsoft.com/en-us/azure/databricks/dev-tools/databricks-apps/compute-size

**.NET / Aspire / App Service**
- https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core
- https://learn.microsoft.com/dotnet/aspire/deployment/azure/aca-deployment-visual-studio
- https://learn.microsoft.com/en-us/azure/app-service/app-service-web-tutorial-custom-domain
- https://learn.microsoft.com/en-us/azure/app-service/configure-ssl-certificate
