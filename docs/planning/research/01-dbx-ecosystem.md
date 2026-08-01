# The Official Databricks Ecosystem Surface — and Whether Lakewright.NET Would Duplicate It

**Research date:** 2026-07-31
**Researcher:** subagent (dbx-ecosystem)
**Method:** live WebFetch / WebSearch against primary sources (docs.databricks.com, databricks.com, github.com/databricks*, github.com/databrickslabs, github.com/databricks-industry-solutions).

**Legend used throughout:**
- **[VERIFIED]** — read on a live page during this session, with the URL and the page's own "last updated" date where shown.
- **[RECALLED]** — from training memory, not confirmed live. Treat as a lead, not a fact.
- **[COULD NOT DETERMINE]** — searched, did not find an authoritative primary source.

---

## 1. The Official Databricks SDK List — Is There a .NET SDK?

### 1.1 The canonical docs page

**Source:** https://docs.databricks.com/aws/en/dev-tools/sdks — page shows **"Last updated on Jun 26, 2026"**. **[VERIFIED]**

Exact intro sentence, quoted verbatim from that page:

> "Databricks provides the following software development kits (SDKs) to build solutions that integrate with Databricks, using the popular programming languages Python, JavaScript, Go, and Java."

SDKs listed on that page: **[VERIFIED]**

| SDK | Owner | Notes |
|---|---|---|
| Databricks SDK for Python | Official (Databricks) | repo describes itself as "Databricks SDK for Python (Beta)" |
| Databricks SDKs for JavaScript | Official (Databricks) | modular, per-API npm packages |
| Databricks SDK for Go | Official (Databricks) | |
| Databricks SDK for Java | Official (Databricks) | |
| Databricks SDK for R | **Databricks Labs** (not core) | listed on the same page but attributed to Labs |
| English SDK for Apache Spark | Official (Databricks) | not a platform SDK — natural-language-to-Spark |

The page itself carries **no GA/beta/experimental status column**. **[VERIFIED]**

A second docs page, https://docs.databricks.com/aws/en/dev-tools/ (the dev-tools hub), lists the same set in a table: "Databricks Python SDK, Databricks JavaScript SDK, Databricks Java SDK, Databricks Go SDK, Databricks R SDK" against the use case "Application development, Integrate with existing deployment systems, Create custom Databricks workflows and web services." **[VERIFIED]** — **no .NET or C# anywhere on that page.**

### 1.2 SDK status detail

- **JavaScript SDK:** "The Databricks SDK for JavaScript is in Beta and is supported for production use cases. Interfaces might still change slightly before GA (e.g. name standardization and minor ergonomic tweaks)." Repo: https://github.com/databricks/sdk-js. **[VERIFIED via search result text; I did not open the repo README directly — treat the exact quote as high-confidence but second-hand.]**
- **Python SDK:** repo title on https://github.com/databricks/databricks-sdk-py reads "Databricks SDK for Python (Beta)". **[VERIFIED]**
- **R SDK:** attributed to Databricks Labs; described in search results as "Experimental" as of Jun 26, 2026. **[VERIFIED for the Labs attribution on the docs page; the "Experimental" wording is from a search snippet, not a page I opened — second-hand.]**

### 1.3 Repository-level sweep of the `databricks` GitHub org

**Source:** https://github.com/orgs/databricks/repositories?q=sdk **[VERIFIED]**

| Repo | Language | Description |
|---|---|---|
| `zerobus-sdk` | Rust | Databricks's Zerobus Ingest SDKs |
| `appkit` | TypeScript | "Build Databricks Apps faster with our brand-new Node.js + React SDK" |
| `sdk-js` | TypeScript | Databricks modular SDKs for JavaScript |
| `databricks-sdk-java` | Java | Databricks SDK for Java |
| `databricks-sdk-go` | Go | Databricks SDK for Go |
| `databricks-sdk-py` | Python | Databricks SDK for Python (Beta) |
| `sdk-go` | Go | (no description) |
| `databricks-dbutils-scala` | Scala | The Scala SDK for Databricks |
| `tabular-sdk-go` | Shell | Golang SDK for the Tabular API |

**No .NET/C# SDK repository exists in the `databricks` GitHub org.** **[VERIFIED]**

### 1.4 Release notes cross-check

**Source:** https://docs.databricks.com/aws/en/release-notes/dev-tools/ — "Last updated on Apr 14, 2026". **[VERIFIED]**

Covers: SDK for Python, SDK for Java, SDK for Go; plus SQL Connector for Python, SQL Driver for Node.js, SQL Driver for Go, Databricks ODBC Driver, Databricks JDBC Driver. **.NET is not mentioned anywhere on this page.** **[VERIFIED]**

### 1.5 Announced plans for a .NET SDK

**[COULD NOT DETERMINE]** — I found no Databricks announcement, roadmap item, GitHub issue, or docs page indicating a planned or in-progress official .NET/C# SDK. Absence of evidence on the pages I read is not proof none exists, but I checked the canonical SDK page, the dev-tools hub, the dev-tools release notes, and the org repo listing, and none mention it.

### 1.6 What .NET developers actually have today

- **Databricks ODBC Driver** — official, first-party. Renamed **from "Simba Spark ODBC Driver" to "Databricks ODBC Driver" as of February 2026**, and Databricks recommends migrating to it. Docs: https://docs.databricks.com/aws/en/integrations/odbc/ ; download: https://www.databricks.com/spark/odbc-drivers-download. .NET consumes it via `System.Data.Odbc` / `OdbcConnection`. **[VERIFIED via search results and docs URLs; I did not open the ODBC page body directly — the Feb-2026 rename is second-hand from a search snippet.]**
- **Databricks JDBC Driver** — official, JVM only, irrelevant to .NET.
- **Community .NET wrappers** — e.g. https://github.com/elastacloud/databricks-dotnet-rest-sdk ("An SDK for the Databricks REST API in dotnet") and https://github.com/anhhchu/databricks-dotnet. **Neither is a Databricks product.** **[VERIFIED that the repos exist under non-Databricks orgs; I did not assess their maintenance status, coverage, or last-commit date.]**

**Bottom line for topic 1: there is no official or semi-official .NET/C# Databricks SDK, and no announced plan for one that I could find. The only first-party .NET-reachable surface is the ODBC driver (and the raw REST API).**

---

## 2. Databricks Solution Accelerators / Field Solutions

### 2.1 What they are and where they live

**Marketing page:** https://www.databricks.com/solutions/accelerators **[VERIFIED]**
Describes accelerators as *"fully functional notebooks and best practices"* designed to *"speed up results across your most common and high-impact use cases,"* helping teams go *"from idea to proof of concept (PoC) in as little as two weeks."* The page filters by industry only (Retail and Consumer Goods, Manufacturing, Financial Services, Healthcare and Life Sciences, Media & Entertainment, Public Sector, Technology and Software). It does **not** state a count and does **not** link the GitHub org from the body text I retrieved.

**GitHub home:** https://github.com/databricks-industry-solutions **[VERIFIED]**
Self-described as the GitHub home for Databricks Solution Accelerators — *"fully functional notebooks that tackle the most common and high-impact use cases."* **219 repositories** in the org as of 2026-07-31. **[VERIFIED]**

### 2.2 Representative accelerators

**Source:** https://github.com/orgs/databricks-industry-solutions/repositories?type=source&sort=stargazers **[VERIFIED]**

| Accelerator | URL | Language | Stars | What it is |
|---|---|---|---|---|
| pixels | https://github.com/databricks-industry-solutions/pixels | JavaScript | 423 | Large-scale medical (DICOM/HLS) image processing + OHIF Viewer |
| security-analysis-tool | https://github.com/databricks-industry-solutions/security-analysis-tool | Python | 181 | Analyzes Databricks account/workspace security config vs. best practice |
| many-model-forecasting | https://github.com/databricks-industry-solutions/many-model-forecasting | Python | 99 | Large-scale forecasting framework |
| diy-llm-qa-bot | https://github.com/databricks-industry-solutions/diy-llm-qa-bot | Python | 82 | LLM customer-service bot (**archived**) |
| esg-scoring | https://github.com/databricks-industry-solutions/esg-scoring | Python | 81 | NLP extraction of ESG initiatives |
| smolder | https://github.com/databricks-industry-solutions/smolder | Scala | 70 | HL7 Apache Spark datasource |
| lakehouse-industry-data-models | https://github.com/databricks-industry-solutions/lakehouse-industry-data-models | PLpgSQL | 65 | Industry data model implementations |
| auto-data-linkage | https://github.com/databricks-industry-solutions/auto-data-linkage | Python | 54 | Entity resolution / dedup |
| industry-solutions-blueprints | https://github.com/databricks-industry-solutions/industry-solutions-blueprints | Shell | 44 | **The official template repo for new accelerators** |
| smart-claims | https://github.com/databricks-industry-solutions/smart-claims | Python | 35 | Insurance claims process |

Others visible: `hls-llm-doc-qa` (58, Python), `causal-incentive` (42, Jupyter), `dbignite` (39, Python), `fine-grained-demand-forecasting` (36, Python), `product-search` (35, Python).

### 2.3 Typical structure and licensing

**Template repo** (https://github.com/databricks-industry-solutions/industry-solutions-blueprints) **[VERIFIED]**:
- Deployment model: clone into a Databricks Workspace → open the **Asset Bundle Editor** in the Databricks UI → Deploy → run the job that executes notebooks sequentially.
- Structure: `notebooks/`, `dashboards/`, `scripts/`, `apps/`, plus `databricks.yml` and `requirements.txt`.
- Features called out: Databricks, Unity Catalog, Serverless Compute.
- **Standard license boilerplate, verbatim:**
  > "© 2025 Databricks, Inc. All rights reserved. The source in this project is provided subject to the Databricks License. All included or referenced third party libraries are subject to the licenses set forth below."

The `pixels` accelerator carries the same boilerplate in its own year form: **"© 2024 Databricks, Inc. All rights reserved. The source in this notebook is provided subject to the Databricks License."**, pointing at https://databricks.com/db-license-source. **[VERIFIED]**

`security-analysis-tool` README disclaimer, verbatim: **[VERIFIED]**
> "The code in this project is provided for exploration purposes only and is not formally supported by Databricks under any Service Level Agreements (SLAs). It is provided AS-IS, without any warranties or guarantees."

It adds: *"Please do not submit support tickets to Databricks for issues related to the use of this project"* and *"there are no formal SLAs for support."*

### 2.4 Are any accelerators .NET or application-tier?

- **.NET: no.** Across the 15 repos I enumerated by stars and the org's language distribution, the languages are Python, JavaScript/TypeScript, Scala, Jupyter, PLpgSQL, Shell. **No C#/.NET repo appeared.** **[VERIFIED for the repos I enumerated; I did not exhaustively scan all 219.]**
- **Application-tier: partially, and increasingly.** This is the more nuanced finding:
  - `pixels` ships a real web app tier — the **OHIF Viewer deployed as a Databricks App** — alongside notebooks and a Model Serving endpoint. Languages: Python, SQL, JavaScript/TypeScript. **[VERIFIED]**
  - `security-analysis-tool` has `/app/brickhound` and `/src` application directories, plus `/terraform`, `/dabs`, `/dashboards`, `/notebooks`, `/tests`. **[VERIFIED]**
  - The blueprint template includes an `apps/` folder by default. **[VERIFIED]**

  So the accelerator program has moved beyond pure notebooks into Databricks-App-hosted front-ends — **but all of it is Python/JS, all of it deploys into a workspace, and none of it is a multi-tenant application framework.**

---

## 3. Databricks Labs (github.com/databrickslabs)

### 3.1 Scale and description

- Org profile description: **"Labs projects to accelerate use cases on the Databricks Unified Analytics Platform"** **[VERIFIED]**
- **49 repositories**, 1.7k followers, no public members listed. **[VERIFIED]** (https://github.com/databrickslabs)
- Curated landing page: https://www.databricks.com/learn/labs **[VERIFIED]**

### 3.2 The exact support-boundary disclaimer

Two wordings are in use. Both quoted verbatim.

**Repo-level (from `databrickslabs/ucx` README):** **[VERIFIED]**
> "Please note that all projects in the databrickslabs GitHub account are provided for your exploration only, and are not formally supported by Databricks with Service Level Agreements (SLAs). They are provided AS-IS, and we do not make any guarantees of any kind."

`databrickslabs/lakebridge` carries the same substance: *"is provided for your exploration only and is not formally supported by Databricks with Service Level Agreements (SLAs). They are provided AS-IS, and we do not make any guarantees."* **[VERIFIED]**

**Databricks.com-level (from https://www.databricks.com/learn/labs):** **[VERIFIED]**
> "All projects in the https://github.com/databrickslabs account are provided for your exploration only, and are not formally supported by Databricks with service level agreements (SLAs)."

Search snippets also render this page's disclaimer as ending "They are provided AS IS." **[VERIFIED via search snippet — second-hand on that trailing sentence.]**

### 3.3 Licensing — this is the important and counter-intuitive finding

**Databricks Labs is not open source in the OSI sense.** Three Labs repos checked, all three carry the proprietary **"Databricks License"**, not Apache 2.0 or MIT:

| Repo | LICENSE file | Copyright year |
|---|---|---|
| `databrickslabs/ucx` | "Databricks License" | 2023 |
| `databrickslabs/dqx` | "Databricks License" | 2024 |
| `databrickslabs/dbldatagen` | "Databricks License" | 2020 |

**[VERIFIED — I opened each LICENSE file.]**

The license text (also at https://www.databricks.com/db-license-source, "DB license") contains this restriction, quoted verbatim: **[VERIFIED]**
> "You may not use the Licensed Materials except in connection with your use of the Databricks Services pursuant to the Agreement."

and

> "Your use of the Licensed Materials must comply at all times with any restrictions applicable to the Databricks Services"

It defines "Agreement" as *"The agreement between Databricks, Inc., and you governing the use of the Databricks Services, as that term is defined in the Master Cloud Services Agreement (MCSA) located at www.databricks.com/legal/mcsa."*

**This is not an OSI-approved open source license.** It ties use of the code to being a Databricks customer. It permits redistribution and modification with notice retention, but the platform tie-in disqualifies it as open source. **[VERIFIED — the restriction text is quoted from the license itself; the "not OSI-approved" characterization is my analysis of that text, and the OSI approved-license list does not contain it.]**

**Caveat:** I checked 3 of 49 Labs repos. Some Labs repos may use Apache 2.0. Note that `databricks/appkit` (in the **core** `databricks` org, not Labs) **is Apache-2.0**. **[VERIFIED]** Do not generalize "all Labs is proprietary" without a wider sweep — but the three I sampled, including their two flagship projects, were all Databricks License.

### 3.4 Project list

From https://www.databricks.com/learn/labs **[VERIFIED]**: DQX, Kasal, Lakebridge, Databricks MCP, Conversational Agent App, Knowledge Assistant Chatbot, Feature Registry Application, Mosaic, DLT-META, Smolder, Geoscan, Migrate, Data Generator (dbldatagen), DeltaOMS, Splunk Integration, DiscoverX, brickster (R), DBX, Tempo, PyLint Plugin, PyTester, Delta Sharing Java Connector, Overwatch, UCX.

Top repos by stars in the org: dolly (11k, Python), dbldatagen (485), dbx (462), dqx (439), tempo (342, Jupyter), mosaic (325, Jupyter), ucx (308), dlt-meta (268), overwatch (230, Scala), ontobricks (209), cicd-templates (203), ontos (200), migrate (198), automl-toolkit (191, HTML), lakebridge (153), discoverx (143), dataframe-rules-engine (141, Scala), pytester (136). **[VERIFIED]**

### 3.5 Is there any .NET project in Databricks Labs?

**No.** **[VERIFIED]** — Languages across the org are Python (dominant), Scala, Jupyter, R, HTML. No C#/.NET repo appears in the star-sorted listing or the curated project list. The only non-Python-family first-party language SDKs anywhere are Java, Go, TypeScript, Scala, Rust.

### 3.6 Governance model

**[COULD NOT DETERMINE]** — I found no published governance document (contribution acceptance criteria, maintainer model, graduation path from Labs to product, or deprecation policy). The Labs page describes projects as *"created by the field to help customers get their use cases into production faster"* **[VERIFIED via search snippet — second-hand]**, and the support disclaimer is the only formal boundary statement I found. There is no CNCF-style or Apache-style governance charter that I could locate.

---

## 4. Databricks Apps — Runtimes, Limits, and Multi-Tenant SaaS Suitability

**This is the decision-critical section. The answer is unambiguous and it is a hard no for customer-facing multi-tenant SaaS.**

### 4.1 What it is

**Source:** https://docs.databricks.com/aws/en/dev-tools/databricks-apps/ **[VERIFIED]**
> "Databricks Apps enables developers to build and deploy secure data and AI applications directly on the Databricks platform, which eliminates the need for separate infrastructure."

### 4.2 Supported runtimes — TODAY

**Source:** https://docs.databricks.com/aws/en/dev-tools/databricks-apps/system-env — **"Jun 11, 2026"**. **[VERIFIED]**

The system environment, quoted:
- OS: **"Ubuntu 22.04 LTS"**
- Python: **"Python 3.11, running in a dedicated virtual environment. All dependencies are isolated within this environment."**
- Node.js: **"Node.js version 22.16. Manage dependencies with npm or pnpm using a package.json file."**
- uv: **"uv version: 0.10.2"**

Pre-configured framework support named on that page: **Streamlit, FastAPI, Flask, Gradio, Dash, Express, Uvicorn.** **[VERIFIED]**

From the overview page: *"Popular Python frameworks include Streamlit, Dash, and Gradio"*; for Node.js, *"React, Angular, Svelte, and Express are also supported."* **[VERIFIED]**

**.NET is not present in the system environment and is not mentioned in any Apps documentation page I read** (overview, system-env, app-runtime, app-development, key-concepts, compute-size, permissions). **[VERIFIED]**

### 4.3 Can it run a .NET / ASP.NET Core app via a generic container or arbitrary command?

**Containers/Docker: no support documented anywhere.** I found **no** mention of containers, Docker, custom images, or BYO-runtime across any Apps docs page I read. **[VERIFIED — as an absence across six pages, not as an explicit prohibition.]**

**Arbitrary command: the `command` field exists, but the docs do not authorize arbitrary binaries.**
**Source:** https://docs.databricks.com/aws/en/dev-tools/databricks-apps/app-runtime — **"Jun 23, 2026"**. **[VERIFIED]**

> "By default, Databricks runs Python apps using the command `python <my-app.py>`... If your app includes Node.js, the default command is `npm run start`."

and, importantly:

> "Because Databricks doesn't run the command in a shell, environment variables defined outside the app configuration aren't available to your app."

**Assessment (my analysis, clearly labelled):** The `command` field is an argv array executed without a shell, in an Ubuntu 22.04 sandbox with Python 3.11 and Node 22.16 installed and no documented .NET runtime. A **self-contained, single-file, linux-x64 .NET publish** placed in the app source tree and invoked directly by `command` is *theoretically* the only plausible route, because it needs no pre-installed runtime. **But:** the docs neither describe nor sanction this; there is no documented statement on whether arbitrary ELF binaries may execute, whether the filesystem permits the executable bit, or whether egress/deps resolution would work. **[COULD NOT DETERMINE — this is an untested hypothesis. It must be empirically validated before any plan depends on it, and even if it works it would be an unsupported configuration that Databricks could break without notice.]**

### 4.4 Compute limits

**Source:** https://docs.databricks.com/aws/en/dev-tools/databricks-apps/compute-size — **"Last updated May 29, 2026"**. **[VERIFIED]**

| Size | vCPU | Memory | Cost |
|---|---|---|---|
| **Medium (default)** | Up to 2 vCPUs | 6 GB | 0.5 DBU/hour |
| **Large** | Up to 4 vCPUs | 12 GB | 1 DBU/hour |

Confirmed on the system-env page too: *"By default, each app can use up to 2 virtual CPUs (vCPUs) and 6 GB of memory."* **[VERIFIED]**

**Horizontal scaling** ("running apps across multiple instances for higher availability and concurrency") exists but is **in Beta** as of that page. Specific concurrency limits, replica counts, and scaling thresholds are **not documented**. **[VERIFIED — including the absence.]**

Billing: *"Apps are billed per hour of compute time while running, based on provisioned capacity."* **[VERIFIED]**

There is also a documented cap on the **number of apps per workspace**, cross-referenced to https://docs.databricks.com/aws/en/resources/limits. **[VERIFIED that the cross-reference exists; I did not read the specific number.]**

### 4.5 Auth model — the decisive constraint

**Source:** https://docs.databricks.com/aws/en/dev-tools/databricks-apps/permissions — **"Apr 15, 2026"**. **[VERIFIED]**

Quoted verbatim:
> **"You can't make Databricks apps public. Anonymous access and bypassing single sign-on (SSO) are not supported."**

The recommended workaround for outside users is *"identity federation with SCIM and JIT provisioning to onboard users through your identity provider without granting full workspace access"* — i.e. **external users must still become identities in your Databricks account.** **[VERIFIED]**

From https://docs.databricks.com/aws/en/dev-tools/databricks-apps/auth **[VERIFIED]**:
> "To obtain tokens for Databricks Apps, both users and service principals authenticate using standard OAuth 2.0 flows."

Users get in via **SSO** ("Users authenticate through your identity provider when single sign-on (SSO) is configured") or **OTP** ("Users receive a temporary password if SSO isn't configured"). Two authorization modes: **app authorization** (dedicated service principal) and **user authorization** (app acts with the user's identity and permissions). No anonymous or non-Databricks-identity path is documented.

From https://docs.databricks.com/aws/en/dev-tools/databricks-apps/key-concepts **[VERIFIED]**:
- *"Each app has its own configuration, identity, and isolated runtime environment"*; *"Databricks automatically creates a service principal for each app."*
- URL pattern: `https://<app-name>-<workspace-id>.<region>.databricksapps.com`; *"Databricks automatically assigns each app a unique URL"* and *"You can't change the URL after you create the app."*
- Apps belong to specific workspaces and *"can access workspace-level resources like SQL warehouses and account-level resources like Unity Catalog."*
- *"Developers can also choose to share apps with users outside the workspace but within the same Databricks account"* — external users must first be *"sync[ed]...into the account using your identity provider."* Access requires `CAN_USE` or `CAN_MANAGE`.

### 4.6 Networking, egress, custom domains

**Source:** https://docs.databricks.com/aws/en/dev-tools/databricks-apps/networking **[VERIFIED via search-result content; I did not open the page body directly — treat details as high-confidence second-hand.]**
- Ingress and egress controllable via IP access lists, front-end private connectivity, and network policies.
- Egress: network connectivity configurations (NCCs) give **stable/fixed public egress IPs** for external allowlisting.
- **Custom domains: there is no documented custom-domain feature.** The only "custom domain" guidance concerns *conditional DNS forwarding for the `databricksapps.com` domain* so private-link name resolution works. **You get a `*.databricksapps.com` URL, not `app.yourcompany.com`.** **[VERIFIED as an absence across the pages read; the fixed-URL statement in key-concepts corroborates it.]**
- Network policy limits: max 2500 destinations; 100 storage destinations per policy; 100 FQDNs as allowed domains.

### 4.7 Verdict: is Databricks Apps suitable for hosting a multi-tenant customer-facing SaaS?

**No. It is workspace-scoped internal tooling.** The evidence:

1. **No public/anonymous access, period** — the explicit "You can't make Databricks apps public" quote above. A customer-facing SaaS requires unauthenticated marketing/signup/reset flows at minimum, and typically its own identity system. **[VERIFIED]**
2. **Every end user must be an identity in *your* Databricks account.** That inverts the SaaS model — your customers' users would need provisioning into your Databricks tenancy. **[VERIFIED]**
3. **No custom domains** — customers hit `<app>-<workspace-id>.<region>.databricksapps.com`. **[VERIFIED as absence]**
4. **2–4 vCPU / 6–12 GB ceiling**, with multi-instance scaling still in Beta. **[VERIFIED]**
5. **Per-customer deployment**: search results indicate that to serve each customer with a separate Databricks account, *"they need their own app instance deployed into their workspace."* **[VERIFIED via search snippet — second-hand, but consistent with the workspace-scoping documented in key-concepts.]**
6. Databricks' own developer-hub framing positions Apps around **internal** use — the developer site carries pages titled *"What platform supports building and deploying many small **internal** apps for different teams using shared enterprise data?"* and *"What is the best way to deploy an **internal** data app without setting up separate hosting and authentication infrastructure?"* (developers.databricks.com/perspectives/...). **[VERIFIED that pages with these titles exist; I did not open their bodies.]**

**Databricks Apps is the right host for an internal analytics/ops tool inside one workspace. It is the wrong host for a customer-facing multi-tenant SaaS.**

---

## 5. Databricks Guidance for Customer-Facing Applications & Multi-Tenant SaaS

### 5.1 The official reference architectures

**Source:** https://docs.databricks.com/aws/en/lakehouse-architecture/reference — **"July 28, 2026"** (three days before this research). **[VERIFIED]**

Published architectures: Reference architecture for the Databricks platform on AWS; Lakeflow Connect; Batch ingestion and ETL; Streaming and CDC; Machine learning and AI (traditional); Agent applications; BI and SQL analytics; **Business Apps**; Lakehouse federation; Catalog federation; Share data with 3rd-party tools; Consume shared data from Databricks. Downloadable as 11x17 PDFs.

The **"Business Apps"** section, quoted in full: **[VERIFIED]**
> "Databricks Apps enables developers to build and deploy secure data and AI applications directly on the Databricks platform, which eliminates the need for separate infrastructure. Apps are hosted on the Databricks serverless platform and integrate with key platform services. Use Lakebase, if the app needs OLTP data that got synched from Databricks."

Elsewhere in the reference-architecture material, the application swim lane is described as: **[VERIFIED]**
> "The final business applications are in this swim lane. Examples include custom clients such as AI applications connected to Model Serving for real-time inference or applications that access data pushed from Databricks to an operational database."

The **"Agent applications"** section: **[VERIFIED]**
> "For deploying models in a scalable and enterprise-grade way, use the MLOps capabilities to publish the models in model serving."

### 5.2 Is there official multi-tenant SaaS-on-Databricks guidance?

**[COULD NOT DETERMINE — and this is a meaningful negative finding.]**

I found **no** first-party docs.databricks.com or databricks.com page that is a reference architecture, best-practice guide, or design pattern for building a **multi-tenant, customer-facing SaaS product** on Databricks. Specifically:

- The reference-architecture page (read live, dated 2026-07-28) **does not mention multi-tenancy, tenant isolation, customer onboarding, or external customer serving** in the Business Apps or Agent applications sections. **[VERIFIED]**
- The "custom clients" phrasing above is the closest official acknowledgement that an application tier exists outside Databricks — and it is one sentence with no architectural detail. **[VERIFIED]**

What *does* exist is **community content, not official guidance**:
- https://community.databricks.com/t5/community-articles/building-multitenant-architecture-on-databricks-platform/td-p/125937 — community article describing workspace separation per tenant, Unity Catalog fine-grained access control, and UDF-based row-level security; catalogs as `tenant_a_catalog` / `tenant_b_catalog` / `shared_catalog` with bronze/silver/gold schemas; storage isolation by bucket/folder per tenant with External Locations, Storage Credentials, and per-tenant IAM roles.
- https://community.databricks.com/t5/technical-blog/one-tenant-multiple-subsidiaries-account-and-tenant-architecture/ba-p/160786
- https://community.databricks.com/t5/administration-architecture/azure-databricks-multi-tenant-solution/td-p/91025

**[VERIFIED that these community pages exist with these titles/URLs; the architectural details are from search-result summaries, second-hand. Community forum content is NOT official Databricks guidance and carries no support commitment.]**

Adjacent official capabilities relevant to serving external parties, but not a SaaS architecture:
- **Delta Sharing / Open Sharing** — *"Enterprise-grade data sharing with 3rd parties is provided by OpenSharing, which enables direct access to data in the object store secured by Unity Catalog"*, also underpinning **Databricks Marketplace**. This is data-product distribution, not application SaaS. **[VERIFIED via reference-architecture search content — second-hand.]**
- **Lakebase** (serverless Postgres for the OLTP/application tier) — **Generally Available on AWS, beta on Azure, announced February 2026**. Includes Unity Catalog governance integration, automated backups + PITR, up to **8 TB per instance**, **Postgres 17**. Since **March 12, 2026** new instances are created as **Autoscaling projects**; existing Provisioned instances auto-upgraded starting **June 2026**. Docs: https://docs.databricks.com/aws/en/oltp/ ; product: https://www.databricks.com/product/lakebase ; GA blog: https://www.databricks.com/blog/databricks-lakebase-generally-available. **[VERIFIED that these URLs exist; the version/date specifics are from search-result summaries — second-hand, worth re-confirming on the GA blog directly before relying on them.]**
- **Databricks External User Terms** — https://www.databricks.com/legal/external-user-terms exists as a legal document. **[VERIFIED that the link exists on the legal index; I did not read it. This is likely relevant to any plan involving external users and should be read before committing to a design.]**

---

## 6. Trademark / Brand Usage Policy for Community Projects

**[LARGELY COULD NOT DETERMINE — no public, community-facing trademark policy found.]**

### 6.1 What I checked

Full enumeration of https://www.databricks.com/legal **[VERIFIED]** — 23 policies listed:

Master Cloud Services Agreement (`/legal/mcsa`), Acceptable Use Policy (`/legal/aup`), External User Terms (`/legal/external-user-terms`), U.S. Public Sector Services, Free Edition Terms of Service, Partner Terms and Conditions (`/company/partners`), Website Terms of Use (`/legal/terms-of-use`), Event Terms, Usage Commit Terms, Additional Billing and Commitment Terms, Procurement Master Supplier Agreement, Data Processing Addendum, Amendment to DPA, EU Data Act Addendum, Privacy Notice, International Data Transfers FAQ, Databricks Subprocessors, Cookie Notice, Applicant Privacy Notice, Code of Conduct, Third Party Code of Conduct, Modern Slavery Statement, Security Addendum.

> **There is no trademark policy, brand policy, or trademark usage guidelines link on the Databricks legal index page.** **[VERIFIED]**

`https://www.databricks.com/legal/databricks-trademark-usage-policy` → **HTTP 404**. **[VERIFIED]**
`https://brand.databricks.com/terms-and-conditions` → page exists but body content did not render usable policy text via fetch. **[Could not extract]**
`https://brandguides.brandfolder.com/databricks-extended-brand-guidelines/logo` → same, content truncated on fetch. **[Could not extract]**

### 6.2 What I did find

**From the Website Terms of Use** (https://www.databricks.com/legal/terms-of-use), verbatim: **[VERIFIED]**
> "'Apache' and 'Spark' are trademarks of the Apache Software Foundation. Any other third party trademarks, service marks, logos, trade names or other proprietary designations, that are or may become present within the Sites, including within any Content, are the registered or unregistered trademarks of the respective parties."

Site footer: *"Apache, Apache Spark, Spark, the Spark Logo, Apache Iceberg, Iceberg, and the Apache Iceberg logo are trademarks of the Apache Software Foundation."* **[VERIFIED]**

**From the Partner Program Terms & Conditions** (https://databricks.com/partnertcs) — this is a **partner-only** contract, not a community policy: **[VERIFIED via search-result content — second-hand]**
- *"Databricks Marks"* = *"the Databricks trademarks, trade names, service marks, logos, service names and other distinctive brand features relating to the Databricks products and services."*
- *"Any use of a Databricks Mark by Partner must correctly attribute ownership of such mark to Databricks and must be in accordance with applicable law and Databricks' then-current trademark usage guidelines that have been provided or made available to Partner."*
- *"Databricks owns the Databricks Marks and ... any and all goodwill and other proprietary rights that are created by or that result from Partner's use of a Databricks Mark inure solely to the benefit of Databricks."*
- *"Partners will not contest or aid in contesting the validity or ownership of any Databricks Mark or take any action in derogation of Databricks' rights therein, including, without limitation, applying to register any trademark, trade name or other designation that is confusingly similar to any Databricks Mark."*

**Note the mechanism:** the partner terms reference *"Databricks' then-current trademark usage guidelines that have been **provided or made available to Partner**"* — i.e. the actual guidelines are distributed to partners, not published as a public community policy. **[VERIFIED from the quoted text]**

**Brand contact:** permission requests go to **brand@databricks.com**, providing name and title, company name and location, and a description of the request. **[VERIFIED via search-result summary of the brand guidelines — second-hand. I could not read the primary brand-guidelines page.]**

**Logo restrictions** (from brand guidelines, second-hand): *"Do not alter the brand mark, word mark or lockups in any way. Always use the Databricks logo in its original state."* Four word-mark color variants (two dark, two light). Assets at brand.databricks.com and Brandfolder. **[Second-hand]**

### 6.3 Practical assessment for a community project (my analysis, clearly labelled)

There is **no published safe-harbour wording** for third-party projects — no equivalent of the Apache Software Foundation's public project-branding guidelines or the Linux Foundation's trademark usage pages. What this means:

- **No affirmative permission exists in public** for a project to use "Databricks" in its name. The absence of a policy is not permission.
- The strongest verified signal is the partner-terms prohibition on *"applying to register any trademark, trade name or other designation that is **confusingly similar** to any Databricks Mark."* That clause binds partners contractually, but it also telegraphs how Databricks views name collisions generally. **[VERIFIED quote; the inference is mine.]**
- **"Lakewright.NET" does not contain "Databricks"** — that is favourable. A descriptive, non-trademark-using nominative reference ("for Databricks", "works with Databricks") is the conventional low-risk pattern, but I could not find a Databricks page that explicitly blesses it. **[COULD NOT DETERMINE]**
- **Do not use the Databricks logo** without writing to brand@databricks.com.
- Note that Databricks' own naming convention for community-adjacent things is to hold the name themselves (`databrickslabs/*`, `databricks-industry-solutions/*`) rather than license the mark outward. **[VERIFIED observation of the org names.]**

**Recommendation:** if the name or any "Databricks" reference is material to the project, email brand@databricks.com and get it in writing. The public record is insufficient to self-clear.

---

## IMPLICATIONS FOR LAKEWRIGHT.NET

### Genuine gaps — real, verified, and durable

1. **No official .NET SDK, and no announced plan for one.** The canonical page (dated Jun 26, 2026) names Python, JavaScript, Go, Java; R is Labs. The `databricks` GitHub org has SDKs in Python, Java, Go, TypeScript, Scala, Rust — and no C#. Dev-tools release notes (Apr 14, 2026) never mention .NET. **This is the single most defensible gap.** .NET shops today are on ODBC or hand-rolled REST.
2. **No official multi-tenant SaaS-on-Databricks reference architecture.** The reference-architecture page — updated three days before this research — has a "Business Apps" section that is three sentences long and points entirely at Databricks Apps. Multi-tenancy, tenant isolation at the application tier, and customer onboarding are absent. The only material on this is community forum posts.
3. **No .NET anywhere in the accelerator or Labs ecosystem.** 219 accelerator repos, 49 Labs repos, zero C#.
4. **Databricks Apps cannot host a customer-facing SaaS.** *"You can't make Databricks apps public. Anonymous access and bypassing single sign-on (SSO) are not supported."* Plus: no custom domains, 2–4 vCPU ceiling, every user must be an identity in your Databricks account, horizontal scaling still Beta. **A customer-facing .NET SaaS must be hosted outside Databricks (Azure App Service / Container Apps / AKS) and talk to Databricks as a data platform.** This should be a locked architectural premise, not an open question.

### Duplication risks — where Lakewright.NET would collide

1. **Data-tier accelerators are saturated.** 219 repos covering forecasting, entity resolution, ESG, claims, medical imaging, semantic search. Do not build notebook/pipeline content; that space is thoroughly occupied and is Databricks' own turf.
2. **`databricks/appkit` is the shape of the competition.** Official, Apache-2.0, TypeScript — "Build Databricks Apps faster with our brand-new Node.js + React SDK", with plugins including a Lakebase plugin returning a `pg.Pool` for Prisma/Drizzle/TypeORM. **Databricks is actively investing in an opinionated app-framework layer — just not in .NET.** If Lakewright.NET positions as "the app framework for Databricks", AppKit is the reference point, and the differentiator has to be .NET + genuine multi-tenancy, not "framework for Databricks apps".
3. **Lakebase closes the OLTP gap.** GA on AWS since ~Feb 2026, Postgres 17, 8 TB/instance, Unity Catalog integration. Do not build a bespoke transactional store; a .NET SaaS should target Lakebase (or any Postgres) via Npgsql/EF Core. Reusing the canonical component beats a near-duplicate.
4. **Thin-wrapper risk on the SDK.** A .NET SDK that only wraps the REST API duplicates what community projects (elastacloud/databricks-dotnet-rest-sdk) already attempt and what Databricks could ship at any time. The durable value is the *SaaS layer above* it — tenant isolation, Unity Catalog-backed authorization mapping, per-tenant catalog/schema provisioning, metering — not the transport.

### Constraints to design around

- **Licensing asymmetry is a positioning advantage.** Databricks Labs and the accelerators ship under the proprietary **Databricks License** — *"You may not use the Licensed Materials except in connection with your use of the Databricks Services"* — which is **not OSI open source**. A genuinely Apache-2.0/MIT Lakewright.NET is more permissive than most of Databricks' own community output. (Verified on ucx, dqx, dbldatagen; not an exhaustive sweep.)
- **Support-boundary wording is a solved problem — copy it.** The Labs disclaimer is battle-tested: *"provided for your exploration only, and are not formally supported by Databricks with Service Level Agreements (SLAs). They are provided AS-IS..."* Adapt it (naming Lakewright.NET, not Databricks, as the non-supporter).
- **Trademark is an open legal item.** There is **no public Databricks trademark policy** — it 404s, and it is absent from the 23-item legal index. Partner terms forbid *"confusingly similar"* designations. "Lakewright.NET" avoids the mark, which is good, but nominative use ("for Databricks") is not publicly blessed. **Email brand@databricks.com before launch; do not use the logo.**
- **The Apps-as-host hypothesis needs an empirical test or an explicit kill.** A self-contained linux-x64 .NET binary invoked via `app.yaml`'s shell-less `command` is the only conceivable path, and it is undocumented, unsupported, and unverified. Even if it works, the auth model still forbids customer-facing use — so it is at best a route for *internal* .NET tooling on Databricks, never for the SaaS itself.
