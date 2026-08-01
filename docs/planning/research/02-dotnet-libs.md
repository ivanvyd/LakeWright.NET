# LakeWright.NET Research — 02: Existing .NET ↔ Databricks Connectivity

**Researcher:** R02 (dotnet-libs)
**Date of research:** 2026-07-31
**Method:** Live fetches against nuget.org (search + registration/flatcontainer APIs), api.github.com, raw.githubusercontent.com, docs.databricks.com, learn.microsoft.com, adbc-drivers.org.

Every claim below is tagged **[VERIFIED]** (read live during this session, URL given) or **[RECALLED]** (from memory, not confirmed) or **[COULD NOT DETERMINE]**.

---

## 0. Headline conclusions

1. **There is no official Databricks .NET/C# SDK.** Databricks ships Python, JavaScript, Go, and Java officially, plus R via Databricks Labs. .NET is not listed. **[VERIFIED]**
2. **`Microsoft.Azure.Databricks.Client` is far more capable than its README suggests** — it has full Unity Catalog coverage (18 API clients) and a Statement Execution client. The README does not mention either. This is the single strongest "reuse" candidate for the REST/control-plane surface. **[VERIFIED]**
3. **Its one hard gap is auth portability**: it supports PAT + Entra ID/`TokenCredential` only. There is no Databricks-native OAuth M2M (`/oidc/v1/token` client-credentials) path, so on AWS/GCP workspaces you are effectively limited to PAT. **[VERIFIED — source read]**
4. **For SQL, the practical choice is now three-way**, not two: ODBC, the SQL Statement Execution REST API, or the **Apache Arrow ADBC Databricks driver for .NET** — a fully managed C# driver with CloudFetch and OAuth M2M, no native dependency. That third option did not meaningfully exist a year ago. **[VERIFIED]**
5. **`dotnet/spark` is NOT archived** — it shipped v2.3.1 on 2026-02-13 with .NET 8 support. A web search asserted it was archived in 2024; that is **wrong** (it conflated the 2023 *documentation* archival with the repo). Corrected below. **[VERIFIED via GitHub API]**
6. **Databricks does not publish a public OpenAPI spec.** Code generation against an official spec is not currently a viable strategy. **[VERIFIED — see §6, with caveats]**

---

## 1. NuGet package inventory

Source: <https://www.nuget.org/packages?q=databricks> (37 results) plus targeted queries against
`https://azuresearch-usnc.nuget.org/query?q=packageid:<id>` and
`https://api.nuget.org/v3-flatcontainer/<id>/index.json`. All **[VERIFIED]** unless noted.

### 1.1 Relevant packages

| Package ID | Owner | Latest | Last published | Downloads | License | Repo | What it actually covers |
|---|---|---|---|---|---|---|---|
| `Microsoft.Azure.Databricks.Client` | Microsoft, COSINE | **2.9.3** | **2026-02-16** | **2,194,301** | MIT | [Azure/azure-databricks-client](https://github.com/Azure/azure-databricks-client) | Typed REST client: Clusters, Jobs, DBFS, Files, Secrets, Groups, Libraries, Tokens, Workspace, Instance Pools, Permissions, Cluster Policies, Global Init Scripts, SQL/Warehouses, Repos, Pipelines (DLT), **Unity Catalog (full)**, **Statement Execution**, ML Experiments |
| `Apache.Arrow.Adbc.Drivers.Databricks` | lidavidm (ASF) | **0.23.0** | **2026-04-07** | **76,278** | Apache-2.0 | [apache/arrow-adbc](https://github.com/apache/arrow-adbc) | Managed C# ADBC driver for Databricks SQL. Thrift + CloudFetch, LZ4, OAuth PAT & client-credentials, catalog metadata. Marked **Experimental**. |
| `Apache.Arrow` | ASF (kou, curt_hagenlocher et al.) | **23.0.0** | **2026-05-06** | **38.6M** | Apache-2.0 | [apache/arrow](https://github.com/apache/arrow) | Arrow columnar format + IPC reader/writer for .NET. Mature, high volume. |
| `Energinet.DataHub.Core.Databricks.SqlStatementExecution` | GreenEnergyHub | **16.2.1** | **2026-07-21** | **169,495** | Apache-2.0 | github.com/Energinet-DataHub/geh-core — **404, not publicly accessible** | Streaming query executor over SQL Statement Execution API + Arrow. Targets **.NET 10**. |
| `Energinet.DataHub.Core.Databricks.Jobs` | GreenEnergyHub | 16.2.1 | (same family) | 44K | Apache-2.0 | same (404) | Jobs API wrapper |
| `CData.Databricks` | CDataSoftware | 26.0.9655 | — | 71,211 | **Commercial** | — | ADO.NET provider. Paid. |
| `CData.Databricks.EntityFrameworkCore8` | CDataSoftware | 26.0.9655 | — | 11K | Commercial | — | EF Core provider. Paid. |
| `HashiCorp.Cdktf.Providers.Databricks` | hashicorp | 15.5.0 | — | 142K | MPL-2.0 [RECALLED] | — | CDKTF bindings — infra provisioning, not runtime connectivity |
| `Pulumi.Databricks` | pulumi-bot | 1.102.0-alpha | — | 150K | Apache-2.0 [RECALLED] | — | Pulumi provider — infra provisioning |
| `Storage.Net.Microsoft.Azure.Databricks.Dbfs` | aloneguid | 9.2.6 | — | 60K | [COULD NOT DETERMINE] | — | Read-only DBFS filesystem abstraction |
| `ScalePad.Databricks.Zerobus` | **ScalePad** (authors field says "Databricks") | 0.0.5 | — | 6,991 | [COULD NOT DETERMINE] | none | Zerobus streaming ingest. **Metadata is misleading — owner is ScalePad, not Databricks.** |
| `Databricks.Zerobus.Sdk` / `Databricks.Solutions.Zerobus.Sdk` | guanjieshen (personal) | 0.1.3 / 0.2.0 | — | 447 / 118 | — | [guanjieshen/zerobus-dotnet](https://github.com/guanjieshen/zerobus-dotnet) | Managed gRPC Zerobus ingest client. **Personal account, not official Databricks.** |

### 1.2 Packages the brief asked about that DO NOT EXIST

- **`Databricks.Sdk`** (or any official Databricks-owned SDK package) — **does not exist**. NuGet query `Databricks.Sdk` returns 5 hits, all Zerobus/Reveal packages, none an official SDK. **[VERIFIED]**
- **`DataBricks.Client`** as a standalone package — **does not exist**. The query returns `Microsoft.Azure.Databricks.Client` and two Zerobus packages only. **[VERIFIED]**
- **`Azure.ResourceManager.Databricks`** — **does not exist on NuGet**. Confirmed three ways: `nuget.org/packages/Azure.ResourceManager.Databricks/` → HTTP 404; `api.nuget.org/v3-flatcontainer/azure.resourcemanager.databricks/index.json` → HTTP 404; search API `q=resourcemanager databricks` → `totalHits: 0`. **[VERIFIED]**
  - **Implication:** ARM control-plane management of Azure Databricks *workspace resources* from .NET has no first-party typed package. You would use the generic `Azure.ResourceManager` `ArmClient` with generic resource operations, or call the ARM REST API directly. Note this is workspace *provisioning*, not data-plane work — likely out of scope for LakeWright.NET anyway.
- **Simba drivers on NuGet** — no Simba/Databricks ODBC driver is distributed via NuGet. ODBC drivers are OS-level installs. **[VERIFIED — not present in the 37-result search]**

### 1.3 Supply-chain caution

Two findings worth carrying into the plan:

- `ScalePad.Databricks.Zerobus` sets its NuGet **authors** field to `"Databricks"` while the **owner** is `ScalePad` and there is no project URL. Author fields are free text on NuGet; this one reads as first-party but is not. **[VERIFIED via NuGet search API]**
- `Energinet.DataHub.Core.Databricks.*` declares repo `github.com/Energinet-DataHub/geh-core`, but that repo returns **404 unauthenticated** (both HTML and API). The `Energinet-DataHub` org lists only 4 public repos, none of them `geh-core`. The packages are actively published (16.2.1 on 2026-07-21) but **the source is not publicly auditable right now**. For an OSS accelerator taking a dependency, that is a real risk. **[VERIFIED — 404 reproduced twice]**

---

## 2. Official Databricks ODBC / JDBC drivers from .NET

### 2.1 ODBC — current state **[VERIFIED]**

- **Name change:** As of **February 2026** the driver was renamed from *Simba Spark ODBC Driver* to **Databricks ODBC Driver**. Databricks is no longer distributing new versions of the legacy Simba driver; existing versions remain supported for two years. The legacy Simba driver is formally **deprecated**.
  Source: <https://learn.microsoft.com/en-us/azure/databricks/integrations/odbc/> (page `ms.date` 2026-07-24)
- **Current version: 2.12.0.** Source: <https://www.databricks.com/spark/odbc-drivers-download>. Release notes at `https://databricks-bi-artifacts.s3.us-east-2.amazonaws.com/simba-databricks-odbc-drivers/2.12.0/docs/release-notes.txt`. Databricks supports each driver version for at least 2 years.
- **Install paths** (confirms platform support):
  - Windows 64-bit: `C:\Program Files\Databricks ODBC Driver`
  - Windows 32-bit: `C:\Program Files (x86)\Databricks ODBC Driver`
  - macOS: `/Library/databricks/databricksodbc`
  - **Linux: `/opt/databricks/databricksodbc`**
  Source: <https://learn.microsoft.com/en-us/azure/databricks/integrations/odbc/download>
- **Linux/Docker:** Linux is a supported install target and the docs give explicit non-Windows DSN examples (`unixODBC`-style ini blocks). So it works in Linux containers **[VERIFIED — Linux support and non-Windows DSN format]**, but you must bake the driver + `unixODBC` into the image. **ARM64/aarch64 support: [COULD NOT DETERMINE]** — the download page did not enumerate architectures in fetchable form. This matters if you target ARM container hosts and should be confirmed against the driver download page directly before committing.
- **License:** You must accept the [JDBC/ODBC driver license](https://databricks.com/jdbc-odbc-driver-license) before download; "By downloading the driver, you agree to the Terms & Conditions." **This is a proprietary EULA, not OSS.** Full redistribution terms **[COULD NOT DETERMINE]** — the license text itself was not fetched. **For an open-source accelerator that ships Docker images, whether you may redistribute the driver inside an image is a genuine legal question that must be answered before designing around ODBC.**

### 2.2 ODBC authentication — full matrix **[VERIFIED]**

Source: <https://learn.microsoft.com/en-us/azure/databricks/integrations/odbc/authentication> (`ms.date` 2026-04-03)

| Method | AuthMech | Auth_Flow | Min driver version | Notes |
|---|---|---|---|---|
| Microsoft Entra ID token | 11 | 0 | 2.6.15+ | Token pass-through, ~1h lifetime |
| OAuth 2.0 token pass-through | 11 | 0 | 2.7.5+ | Databricks OAuth secrets only; Entra secrets **not** supported |
| Databricks OAuth **U2M** (browser) | 11 | 2 | 2.8.2+ | Auto-refreshes. **Local apps only — explicitly does not work for server/cloud apps.** |
| Entra ID OAuth **U2M** | 11 | 2 (table) / 1 (DSN example) | 2.8.2+ | Docs are internally inconsistent on `Auth_Flow` here — table says 2, DSN example says 1. Flagging as a docs bug. |
| **Databricks OAuth M2M (client credentials)** | **11** | **1** | (no minimum stated) | `Auth_Client_ID` = SP UUID/App ID, `Auth_Client_Secret` = OAuth secret, `Auth_Scope` default `all-apis`. **This is the cloud-portable service auth.** |
| **Entra ID OAuth M2M** | 11 | 1 | 2.8.2+ | `Auth_Scope=2ff814a6-.../.default` + `OIDCDiscoveryEndpoint` |
| **Azure managed identity** | 11 | 3 | 2.7.7+ | `Auth_Client_ID` + `Azure_workspace_resource_id` |
| PAT (**legacy**) | 3 | — | — | `UID=token`, `PWD=<pat>`. Docs label it "legacy". |

**Answer to the brief's question:** Yes — the ODBC driver supports OAuth M2M *and* U2M, not just PAT, and also Azure managed identity. Note U2M is explicitly unusable from a server-side SaaS backend.

### 2.3 JDBC — open source, but Java **[VERIFIED]**

Databricks now ships an **open-source JDBC driver**: [databricks/databricks-jdbc](https://github.com/databricks/databricks-jdbc), **Apache-2.0**, active (`pushed_at` 2026-07-30, 37 stars, 39 open issues, not archived). It implements OAuth, CloudFetch, and UC volume ingestion.

This is strategically interesting as a **reference implementation** — it is the Apache-licensed, first-party answer to "how should a client talk to Databricks SQL" — but it is **Java and not consumable from .NET**. Value to LakeWright.NET is as a spec to read, not a dependency.

---

## 3. Databricks SQL from .NET without ODBC

Three viable routes. Ranked by how much of the problem they solve for a .NET SaaS backend.

### 3.1 Apache Arrow ADBC Databricks driver (managed C#) — the surprise **[VERIFIED]**

- Package: `Apache.Arrow.Adbc.Drivers.Databricks` **0.23.0**, published **2026-04-07**, **76,278** downloads, **Apache-2.0**.
- Targets **net8.0, netstandard2.0, net472** — so it works on modern .NET and Framework.
- Source: `apache/arrow-adbc` → `csharp/src/Drivers/Databricks/`. Repo is **not archived**, `pushed_at` **2026-07-31** (today), 617 stars, 381 open issues, Apache-2.0.
- **It is a pure managed implementation** — dependencies are `Apache.Arrow.Adbc.Drivers.Apache`, `K4os.Compression.LZ4(.Streams)`, `Microsoft.IO.RecyclableMemoryStream`. **No native ODBC driver, no unixODBC, no EULA.** That is the decisive advantage for Docker/Linux deploys.
- **Auth: PAT and OAuth client-credentials (M2M).** `adbc.databricks.oauth.grant_type = client_credentials` with client ID/secret. Basic auth explicitly not supported. U2M browser flow and token exchange are not documented as supported.
- **CloudFetch enabled by default**, with configurable compression, parallel downloads, prefetch, memory buffering, retries, timeouts.
- Catalog metadata queries supported (`adbc.connection.catalog`, `adbc.connection.db_schema`, `adbc.databricks.enable_pk_fk`).
- Source tree is substantial (~20 files plus `Auth/`, `Reader/`, `Result/` directories; `DatabricksConnection.cs` alone ~51 KB) — this is not a toy.
- **Status: the readme carries an "Experimental" badge.** **[VERIFIED]** That is the main caveat.
- There is a design doc in-tree, `statement-execution-api-design.md`, proposing to add the SQL Statement Execution API as an **alternative** protocol to Thrift (not a replacement), with protocol selection by config. **Status: proposed, not implemented.** It claims a ~12x improvement citing Databricks docs. Worth tracking. **[VERIFIED]**

**Ecosystem wrinkle — read this before depending on it.** An **"ADBC Driver Foundry"** (<https://adbc-drivers.org>) launched a *separate* Databricks ADBC driver on **2026-01-28**, with its own repo [adbc-drivers/databricks](https://github.com/adbc-drivers/databricks) (created 2025-10-22, Apache-2.0, **7 stars, 126 open issues**, active) and its own `dbc install databricks` package manager. The Foundry blog says contributors are "working with Databricks" but claims no corporate ownership. Its docs describe the driver as "an early version" and show **Python** examples; **whether the Foundry driver has a C#/.NET variant, and its NuGet ID, [COULD NOT DETERMINE]** — the Foundry docs page did not mention .NET.

So as of today there appear to be **two parallel Databricks ADBC efforts**: the ASF one in `apache/arrow-adbc` (which is what the .NET NuGet package ships from) and the Foundry one. **Risk: the .NET driver's home may move, or bifurcate.** This is a "verify before committing" item, not a blocker.

### 3.2 SQL Statement Execution REST API **[VERIFIED]**

Source: <https://docs.databricks.com/aws/en/dev-tools/sql-execution-tutorial>

- Endpoint `POST /api/2.0/sql/statements/`.
- **Result formats:** `JSON_ARRAY` (default), `ARROW_STREAM`, `CSV`.
- **Dispositions:** `INLINE` (**25 MiB cap** — exceeding it fails and cancels the statement) and `EXTERNAL_LINKS` (results staged, fetched via short-lived presigned URLs; no stated maximum).
- **Async model:** default 10s wait timeout, configurable **5–50s**, or `0s` for immediate return. On timeout you get a statement ID + status and poll.
- `byte_limit` and `row_limit` parameters available.
- Statement **duration** limits: not specified in the tutorial. **[COULD NOT DETERMINE]**

This is the practical, dependency-free answer: plain HTTPS + JSON, no driver, no EULA, works anywhere. The cost is that you implement paging, polling, external-link fetching, and Arrow decoding yourself — and `ARROW_STREAM` + `EXTERNAL_LINKS` is where the performance is, which means you need an Arrow reader.

### 3.3 Apache Arrow for .NET — maturity **[VERIFIED]**

- `Apache.Arrow` **23.0.0**, published **2026-05-06**, **38.6M total downloads**, Apache-2.0, owned by the ASF (`kou`, `kszucs`, `raulcd`, `asf`, `brycemecum`, `curt_hagenlocher`).
- Targets net8.0 / netstandard2.0 / net462.
- Release cadence is steady and tracks the main Arrow release train: 19.0.1 (2025-02-18), 20.0.0 (2025-04-27), 21.0.0 (2025-07-18), 22.0.1 (2025-09-19), 22.1.0 (2025-10-17), 23.0.0 (2026-05-06).
- Provides `ArrowStreamReader` / `ArrowFileReader` in `Apache.Arrow.Ipc` for async record-batch reading. **Buffer compression requires the separate `Apache.Arrow.Compression` package** and passing a `CompressionCodecFactory` to the reader — an easy thing to miss, and Databricks CloudFetch results are LZ4-compressed. **[VERIFIED]**
- There is now a dedicated [apache/arrow-dotnet](https://github.com/apache/arrow-dotnet) repo (the .NET implementation appears to have been split out of the monorepo). Exact split date and current repo stats **[COULD NOT DETERMINE]** — did not fetch. The NuGet package still declares `github.com/apache/arrow` as its repository.

**Verdict on Arrow .NET: mature enough.** 38.6M downloads, ASF-governed, active releases, IPC reader present. It is a safe foundation for parsing `ARROW_STREAM` chunks.

### 3.4 Native Thrift route

The ADBC driver *is* the Thrift route — it speaks the Spark/Databricks Thrift protocol over HTTP in managed C#. There is no separate general-purpose .NET Thrift-to-Databricks library worth adopting. **[VERIFIED by absence in the 37-package NuGet search]**

---

## 4. `Microsoft.Azure.Databricks.Client` — honest assessment

This is the closest existing thing to what LakeWright.NET would build, so it gets the deepest look.

### 4.1 Facts **[VERIFIED]**

| Attribute | Value | Source |
|---|---|---|
| Latest version | **2.9.3** | NuGet |
| Published | **2026-02-16** | NuGet |
| Total downloads | **2,194,301** | NuGet search API |
| License | **MIT** | NuGet + repo |
| Owners | **Microsoft**, COSINE | NuGet |
| Target frameworks | **net8.0, net9.0** (net10.0 computed) | NuGet |
| Repo | [Azure/azure-databricks-client](https://github.com/Azure/azure-databricks-client) | — |
| Repo state | **not archived**, `pushed_at` **2026-02-16**, 87 stars, 70 forks, **10 open issues** | api.github.com |
| Dependency | `Azure.Core >= 1.44.1` | NuGet |

Release cadence: 2.8.0 (2025-03-22), 2.9.0 (2025-07-10), 2.9.1 (2025-09-07), 2.9.2 (2025-12-02), 2.9.3 (2026-02-16). **Roughly quarterly. Steady, not fast.**

Recent commits **[VERIFIED]**: 2026-02-16 "Add missing ClusterEventType enum values" (by Copilot), 2026-02-16 dependabot, 2025-12-08 "Remove dotnet 6 build" (memoryz), 2025-12-01 "Add support for continuous, file_arrival, and table_update triggers in JobSettings" (memoryz), 2025-12-01 Copilot enum fix.

**Who maintains it:** in practice a single primary human maintainer, **`memoryz`** (the "COSINE" NuGet co-owner), with GitHub Copilot agent PRs and dependabot filling in. It lives under the `Azure` GitHub org and is MIT/Microsoft-owned, but **it does not read like a staffed Microsoft product team** — 87 stars, 10 open issues, quarterly releases, bus factor ≈ 1. **[VERIFIED from commit history; the "bus factor" reading is my inference, not a documented statement.]**

### 4.2 API coverage — the README is stale, the code is broad **[VERIFIED]**

The README lists 15 API areas and mentions **neither** Unity Catalog nor Statement Execution. The actual source tree
(`csharp/Microsoft.Azure.Databricks.Client/`) contains:

`ClustersApiClient`, `JobsApiClient`, `DbfsApiClient`, **`FilesApiClient`**, `SecretsApiClient`, `GroupsApiClient`, `LibrariesApiClient`, `TokenApiClient`, `WorkspaceApiClient`, `InstancePoolApiClient`, `PermissionsApiClient`, `ClusterPoliciesApiClient`, `GlobalInitScriptsApi`, `SQLApiClient`, `WarehouseApiClient`, `ReposApiClient`, `PipelinesApiClient`, **`StatementExecutionApiClient`**, **`UnityCatalogClient`**, `MachineLearningClient`.

And `UnityCatalog/` contains **18** clients: `CatalogsApiClient`, `ConnectionsApiClient`, `ExternalLocationsApiClient`, `FunctionsApiClient`, `LineageApiClient`, `MetastoresApiClient`, `ModelVersionApiClient`, `RegisteredModelApiClient`, `SchemasApiClient`, `SecurableWorkspaceBindingsApiClient`, `SharesApiClient`, `StorageCredentialsApiClient`, `SystemSchemasApiClient`, `TableConstraintsApiClient`, `TablesApiClient`, `UnityCatalogPermissionsApiClient`, `VolumesApiClient`, plus `Interfaces/`.

**So: Unity Catalog — yes, comprehensively. SQL Statement Execution — yes.** The brief's premise that these were missing came from the README; the code says otherwise.

`MachineLearning/` contains **only** `ExperimentApiClient.cs` and `IExperimentApi.cs`. UC has model *registry* (`RegisteredModelApiClient`, `ModelVersionApiClient`). **Model Serving endpoints (`/api/2.0/serving-endpoints`, `/serving-endpoints/{name}/invocations`) are NOT covered.** **[VERIFIED — directory listing; a code-search confirmation returned HTTP 401 unauthenticated, so this rests on the directory listing plus the absence of any serving file in the top-level listing.]**

### 4.3 Authentication — the real gap **[VERIFIED, source read directly]**

From `DatabricksClient.cs` there are exactly three public factory methods:

1. `CreateClient(string baseUrl, string token, ...)` — raw bearer token (PAT, or any token you obtained yourself).
2. `CreateClient(string baseUrl, string workspaceResourceId, string databricksToken, string managementToken, ...)` — Azure AAD service principal. Sets `X-Databricks-Azure-SP-Management-Token` and `X-Databricks-Azure-Workspace-Resource-Id` headers. Doc comment still says **"This feature is still in preview"** and requires the SP to have **Contributor** on the workspace resource.
3. `CreateClient(string baseUrl, TokenCredential credential, ...)` — Azure.Identity integration. The scope is **hardcoded**:
   ```csharp
   const string DatabricksScope = "2ff814a6-3304-4ab8-85cb-cd0e6f879c1d/.default";
   ```

**What this means:**
- The only non-Azure-specific path is #1, which requires you to bring your own token. **There is no built-in Databricks-native OAuth M2M** — no `/oidc/v1/token` client-credentials grant, no automatic refresh of a Databricks SP token.
- Consequence: **on AWS/GCP Databricks workspaces you are limited to PAT** unless you implement the OIDC client-credentials dance yourself and feed the token into #1. The `baseUrl` is a free parameter so the REST calls themselves work fine against any cloud — it is purely the auth layer that is Azure-shaped.
- Even on Azure, option #3 has a known refresh gap: open issue **#200 "Auto/background refresh for WorkloadIdentity/TokenCredential"** has been open since **2024-06-24**. **[VERIFIED]**

### 4.4 Open issues that matter for a SaaS accelerator **[VERIFIED]**

- **#303 (2026-04-23) "Jobs list endpoint incorrectly limited to 25"** — a real pagination bug in a core surface.
- #302 (2026-02-27) `VARIANT`, `GEOMETRY`, `GEOGRAPHY` missing from `DataType` enum.
- #298 (2026-01-15) missing `environments` parameter in job settings (serverless jobs).
- #290 (2025-10-16) `StatementExecutionApiClient` not trim/AOT-friendly — wants source generation. **Relevant if you care about container size or Native AOT.**
- #281 (2025-07-21) `for_each_task` (loop) tasks unsupported.
- **#257 (2025-02-01) "Proposal: Implementing Account Clients"** — **account-level APIs are not implemented.** For a multi-tenant SaaS accelerator this is significant: account-level SCIM, workspace provisioning, and budget/usage APIs live there.
- #200 (2024-06-24) token auto-refresh (above).
- #186 (2024-03-29) incorrect `RUNNING` lifecycle state when a run is `PENDING`.

**Pattern:** the gaps cluster exactly where a multi-tenant SaaS would push hardest — account-level APIs, serving endpoints, non-Azure OAuth, pagination, AOT-friendliness.

### 4.5 "Use it instead?" — honest verdict

**Yes, as a dependency for the workspace REST surface. No, as the whole answer.**

Reasons to use it: MIT, 2.2M downloads, Microsoft-owned org, .NET 8/9/10, genuinely broad UC + Jobs + Statement Execution coverage, still shipping. Rebuilding that typed model surface would be months of low-value work and you would be maintaining DTOs against an API that changes constantly.

Reasons it cannot be the whole answer: no Databricks-native OAuth M2M (cloud portability), no account-level APIs, no model serving, no automatic token refresh, thin ML coverage, bus-factor-1 maintenance, quarterly cadence, and a known pagination bug. Those are exactly the seams a SaaS accelerator has to own.

**Recommended shape: depend on it, wrap it behind your own interfaces, and add the missing pieces alongside rather than forking.** A fork inherits the maintenance burden you were trying to avoid; a wrapper lets you swap the implementation per capability if the upstream stalls.

---

## 5. `.NET for Apache Spark` (dotnet/spark) — **correction to the common belief**

**It is NOT archived.** **[VERIFIED via api.github.com]**

| Field | Value |
|---|---|
| `archived` | **false** |
| `disabled` | false |
| `pushed_at` | **2026-07-31T04:42:13Z** |
| `stargazers_count` | 2,097 |
| `open_issues_count` | 198 |
| `license.spdx_id` | MIT |
| `default_branch` | main |

Recent commits **[VERIFIED]** — all by `SparkSnail` (shinyang@microsoft.com):
- 2026-07-31 "Route PR package resolution through ManagedOSS feed (#1245)"
- 2026-05-14 "Add net472 target framework support (#1243)"
- 2026-05-09 "Upgrade pipeline agent image from windows-2019 to windows-2022 (#1242)"
- 2026-03-02 "Migrate pipeline templates from 1ES PT to WebXT PT (#1240)"
- 2026-02-13 "Merge branch 2.3.1 back to main (#1238)"

Releases **[VERIFIED]**: **v2.3.1 on 2026-02-13** (stable; binaries for **.NET 8** and .NET 4.8 on Linux/Windows/macOS), v2.3.1-rc1 2026-02-02, v2.3.0 2025-05-13, then a **three-year gap back to v2.1.1 (2022-06-01)**.

**Honest reading:** alive but on life support. The 2022→2025 gap, and the fact that 2026 activity is almost entirely one Microsoft engineer doing build-infrastructure maintenance (pipeline migrations, feed routing, TFM additions) rather than feature work, says this is **maintenance mode**, not active product development. Note also that the earlier documentation *was* archived (Microsoft stopped updating the .NET for Apache Spark docs around 2023-08-30) **[RECALLED — surfaced by web search, not independently confirmed]**, which is almost certainly the origin of the widespread "it's archived" claim.

**Relevance to LakeWright.NET: low.** It is a UDF/driver programming model for running .NET code *inside* Spark, not a connectivity library. It does not help a .NET SaaS backend talk to Databricks. **Do not build on it.**

---

## 6. OpenAPI specs and code generation — the build-vs-generate question

**Finding: Databricks does not publish a consumable public OpenAPI specification. Generation is not currently a viable strategy.** **[VERIFIED, with the caveats below]**

Evidence:

1. **`databricks/databricks-sdk-go` does not vendor a spec.** I fetched `.codegen.json` from `main` directly. Full contents:
   ```json
   {
     "mode": "go_v0",
     "api_changelog": true,
     "version": { "version/version.go": "const Version = \"$VERSION\"" },
     "toolchain": {
       "required": ["go"],
       "post_generate": ["make fmt", "go run github.com/vektra/mockery/v2@bfd46e35b15c2689ced221299bdcdeeff8aa0be3"]
     }
   }
   ```
   **There is no OpenAPI spec path or URL — public or private — in this file.** The generator config describes *how* to generate, not *from what*. **[VERIFIED]**
   The `openapi` Go *package* inside the SDK exists but is internal plumbing (error-response remapping), not a published spec. **[RECALLED from pkg.go.dev search result — not independently fetched]**

2. **The spec is internal to Databricks.** The SDKs are generated from a spec Databricks holds privately; commit traffic like "Update SDK to latest OpenAPI spec" in `databricks-sdk-py` reflects an internal sync, not a public artifact. **[RECALLED — inferred from search results; I did not find a public spec file to disprove or confirm]**

3. **A community project exists precisely because there is no official spec:** [openapi-community/databricks-openapi](https://github.com/openapi-community/databricks-openapi) — **scrapes the Databricks REST API web documentation** and generates OpenAPI specs from it. Community, not official. **1 star, 1 fork, 39 commits, no releases.** Output lives in `openapi_providers/` and is aimed at `stackql`. There is a near-identical `stackql/stackql-databricks-openapi`. **[VERIFIED for the repo; metrics are tiny]**

4. Databricks provides **Postman collections** for its REST APIs, which can be converted to OpenAPI. **[RECALLED — surfaced by search, not fetched]**

**License governing any spec: [COULD NOT DETERMINE].** Since no official public spec was located, there is no spec license to assess. The `databricks-sdk-go` *code* is Apache-2.0 **[RECALLED]**, but that governs the generated Go source, not a specification artifact you could regenerate from.

### Implication for build-vs-generate

**Generation via Kiota / NSwag / OpenAPI Generator is off the table as a primary strategy.** You would be generating from a scraped, 1-star, unofficial spec of unknown fidelity and unknown license — strictly worse than either hand-writing the DTOs you need or depending on `Microsoft.Azure.Databricks.Client`, which has already hand-written 2.2M-downloads' worth of them.

Two fallback ideas worth noting in the plan, neither verified as practical:
- **Transliterate from `databricks-sdk-go`.** It is Apache-2.0 and generated from the real spec, so its type shapes are authoritative. Attribution/licence obligations would apply and this is a large manual effort.
- **Read `databricks/databricks-jdbc` (Apache-2.0)** as the authoritative reference for SQL wire behaviour — CloudFetch, OAuth, UC volume ingestion.

---

## 7. Capability-by-capability verdict

| Capability | Verdict | Reasoning |
|---|---|---|
| **Auth** | **BUILD** (thin) | `Microsoft.Azure.Databricks.Client` covers PAT + Entra/`TokenCredential` only, with a hardcoded Entra scope and a 2-year-old open issue on token refresh (#200). Databricks-native OAuth M2M (`/oidc/v1/token`, client credentials) is **absent**, which is the auth you need for AWS/GCP portability and for a service-principal-per-tenant SaaS model. This is a small, well-specified, high-leverage piece: an OIDC client-credentials token provider with caching and refresh, exposed as a `TokenCredential`/delegate so it plugs into both the REST client and ADBC. Build it. |
| **REST client (workspace)** | **REUSE + EXTEND** | Depend on `Microsoft.Azure.Databricks.Client` 2.9.3 (MIT, 2.2M downloads, .NET 8/9/10, Unity Catalog + Statement Execution + Jobs already typed). Wrap behind your own interfaces so individual capabilities can be swapped. Extend for: account-level APIs (#257), serving endpoints, and the Jobs pagination bug (#303). Do **not** fork. |
| **SQL execution** | **REUSE, two-track** | Track A (default): **SQL Statement Execution REST API** + `Apache.Arrow` 23.0.0 for `ARROW_STREAM`/`EXTERNAL_LINKS` — zero native deps, no EULA, works in any container. Remember `Apache.Arrow.Compression` for LZ4. Track B (opt-in, high throughput): `Apache.Arrow.Adbc.Drivers.Databricks` 0.23.0 — managed C#, CloudFetch, OAuth M2M, no native driver. **Avoid ODBC as the default**: proprietary EULA with unresolved redistribution terms for an OSS project shipping Docker images, plus a native install in every image. Keep ODBC as a documented escape hatch only. |
| **Jobs** | **REUSE** | `JobsApiClient` is the most mature part of the library and got real feature work in Dec 2025 (continuous/file_arrival/table_update triggers). Known gaps to work around: pagination capped at 25 (#303), `for_each_task` unsupported (#281), `environments` param missing (#298). Wrap and patch around these rather than reimplementing. |
| **UC metadata** | **REUSE** | 18 typed Unity Catalog clients already exist (catalogs, schemas, tables, volumes, functions, lineage, shares, storage credentials, external locations, permissions, system schemas, constraints, model registry). This is the biggest single chunk of work you get for free, and the README's silence about it is the main reason people underestimate this library. |
| **Model serving** | **BUILD** | Not covered anywhere in the .NET ecosystem. `MachineLearning/` in the client has only Experiments; UC covers the model *registry* but not serving. You need `/api/2.0/serving-endpoints` (CRUD) and `/serving-endpoints/{name}/invocations` (scoring), including the route-optimized `endpoint_url` returned by `GET /api/2.0/serving-endpoints/{name}`. Small, self-contained, genuinely additive. |
| **Streaming ingest (Zerobus)** | **DEFER / caution** | Databricks' official Zerobus SDKs are Rust/Python/Go/TypeScript/Java — **no .NET**. The three .NET NuGet packages are community/personal (`guanjieshen`) or third-party-repackaged with misleading author metadata (`ScalePad`). Do not take a dependency without a security review. Out of scope for v1. |
| **Spark UDFs / dotnet-spark** | **AVOID** | Not archived, but maintenance-mode with a 3-year release gap and a bus factor of 1. Wrong tool anyway — it runs .NET inside Spark, it does not connect a .NET app to Databricks. |
| **Codegen from OpenAPI** | **DO NOT** | No official public spec. Only a 1-star community scraper of unknown fidelity and licence. Hand-written types (yours or the existing library's) beat generated-from-scraped. |
| **ARM / workspace provisioning** | **BUILD or SKIP** | No `Azure.ResourceManager.Databricks` package exists. Use generic `ArmClient` or ARM REST if needed. Probably out of scope — this is provisioning, not data plane. |

---

## 8. Open questions to resolve before locking the plan

1. **ODBC driver license redistribution terms** — may an OSS project bake the Databricks ODBC Driver into a published Docker image? Requires reading <https://databricks.com/jdbc-odbc-driver-license> in full. Currently **[COULD NOT DETERMINE]**. *(Recommendation reduces the stakes: ODBC is escape-hatch only.)*
2. **ODBC ARM64/aarch64 support** — **[COULD NOT DETERMINE]**. Matters only if ODBC stays in scope.
3. **ADBC driver home** — is the .NET Databricks ADBC driver staying in `apache/arrow-adbc`, or moving to the ADBC Driver Foundry (`adbc-drivers/databricks`)? Does the Foundry ship a .NET package? **[COULD NOT DETERMINE]** — worth one direct question to the maintainers, since it decides whether Track B is stable.
4. **Experimental status of the ADBC .NET driver** — what would it take for it to be considered production-ready? Affects whether Track B can be the default rather than opt-in.
5. **`Energinet.DataHub.Core.Databricks.*` source availability** — repo 404s. If the plan wants to borrow its streaming-executor design, the source needs to be locatable. Currently only the compiled package is public.
6. **`Microsoft.Azure.Databricks.Client` governance** — is Microsoft staffing this, or is it one maintainer under the Azure org banner? Worth asking in an issue before betting the accelerator's core on it. My reading of the commit history says the latter, but that is inference.
7. **Statement Execution duration limits** — not documented in the tutorial; needed for designing the polling/timeout strategy.

---

## 9. Source URLs used

**NuGet**
- <https://www.nuget.org/packages?q=databricks>
- <https://www.nuget.org/packages/Microsoft.Azure.Databricks.Client>
- <https://www.nuget.org/packages/Apache.Arrow.Adbc.Drivers.Databricks>
- <https://www.nuget.org/packages/Apache.Arrow/>
- <https://www.nuget.org/packages/Energinet.DataHub.Core.Databricks.SqlStatementExecution>
- `https://azuresearch-usnc.nuget.org/query?q=packageid:<id>` (several)
- `https://api.nuget.org/v3-flatcontainer/<id>/index.json` (several)

**GitHub**
- <https://github.com/Azure/azure-databricks-client> + `api.github.com/repos/Azure/azure-databricks-client` (+ `/commits`, `/contents/...`)
- `https://raw.githubusercontent.com/Azure/azure-databricks-client/master/csharp/Microsoft.Azure.Databricks.Client/DatabricksClient.cs`
- `https://raw.githubusercontent.com/Azure/azure-databricks-client/master/csharp/Microsoft.Azure.Databricks.Client/BearerHeaderHandler.cs`
- <https://github.com/Azure/azure-databricks-client/issues>
- <https://github.com/dotnet/spark> + `api.github.com/repos/dotnet/spark` (+ `/commits`, `/releases`)
- <https://github.com/apache/arrow-adbc> + `api.github.com/repos/apache/arrow-adbc/contents/csharp/src/Drivers/Databricks`
- <https://github.com/apache/arrow-adbc/blob/main/csharp/src/Drivers/Databricks/readme.md>
- `https://raw.githubusercontent.com/apache/arrow-adbc/main/csharp/src/Drivers/Databricks/statement-execution-api-design.md`
- <https://github.com/adbc-drivers/databricks> + `api.github.com/repos/adbc-drivers/databricks`
- <https://github.com/databricks/databricks-jdbc> + `api.github.com/repos/databricks/databricks-jdbc`
- `https://raw.githubusercontent.com/databricks/databricks-sdk-go/main/.codegen.json`
- <https://github.com/openapi-community/databricks-openapi>

**Databricks / Microsoft docs**
- <https://docs.databricks.com/aws/en/dev-tools/sdks>
- <https://docs.databricks.com/aws/en/dev-tools/sql-execution-tutorial>
- <https://learn.microsoft.com/en-us/azure/databricks/integrations/odbc/>
- <https://learn.microsoft.com/en-us/azure/databricks/integrations/odbc/download>
- <https://learn.microsoft.com/en-us/azure/databricks/integrations/odbc/authentication>
- <https://www.databricks.com/spark/odbc-drivers-download>
- <https://databricks.com/jdbc-odbc-driver-license> (linked, not fetched)

**ADBC Driver Foundry**
- <https://adbc-drivers.org/2026/01/28/new-adbc-driver-for-databricks.html>
- <https://adbc-drivers.org/drivers/databricks/>
