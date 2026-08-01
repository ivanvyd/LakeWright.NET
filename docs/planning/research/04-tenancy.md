# 04 — Multi-Tenant Isolation on Databricks

Research for **LakeWright.NET**. Compiled 2026-07-31. Every claim below carries a URL. Doc dates are recorded where the page exposes them (Microsoft Learn mirrors of the Databricks docs expose `ms.date` / `updated_at`; docs.databricks.com generally does not).

**Status legend**
- **[VERIFIED]** — read from a primary source this session, URL given.
- **[COMMUNITY]** — practitioner write-up, blog, or forum. Not official.
- **[UNDETERMINED]** — looked for it, could not find a documented answer.

---

## 0. Terminology change you must know about

**Delta Sharing was renamed OpenSharing on 2026-06-10.** [VERIFIED]

> "Delta Sharing is now OpenSharing" — Databricks blog, *Introducing OpenSharing: the Next Evolution of Delta Sharing for the Agentic Era*, 2026-06-10.
> https://www.databricks.com/blog/introducing-opensharing-next-evolution-delta-sharing-agentic-era

OpenSharing is now a Linux Foundation project, adds Iceberg IRC client support, agent skills, AI models and unstructured data, and covers on-prem/private-cloud sources. Databricks states existing Delta Sharing deployments keep working with no breaking changes. Press release: https://www.databricks.com/company/newsroom/press-releases/databricks-announces-opensharing

Consequence for us: the **current docs and the current limits tables use the word "OpenSharing"**, not "Delta Sharing". Anything in our planning package written as "Delta Sharing" should say "OpenSharing (formerly Delta Sharing)". The `system.billing.usage` product enum value is `DATA_SHARING`, and the sharing system table is `system.sharing.materialization_history`.

---

## 1. Official Databricks guidance for ISVs / SaaS builders

### 1.1 What exists

**Partner Well-Architected Framework (PWAF)** — launched 2026-02-10. [VERIFIED]
- Announcement: https://www.databricks.com/blog/introducing-new-databricks-partner-program-and-well-architected-framework-isvs-and-data (2026-02-10)
- Framework site: https://databrickslabs.github.io/partner-architecture/ — self-described as "The definitive architecture guide for technology partners to create world-class products, integrations, and data shares on the Databricks Data Intelligence Platform."
- Three partner archetypes: **Connected ISV Partners** ("Connect your product to the lakehouse"), **Data Collaboration Partners**, and **Built-On ISV Partners** ("Build your Product on Databricks").
- "Built-On" is defined in the announcement as: *"solutions built on top of Databricks, with Databricks serving as the foundational implementation behind the partner's own front-end/API/intellectual property."* That is exactly LakeWright.NET's shape.

**Older ISV best-practices PDF** — https://assets.docs.databricks.com/_extras/documents/best-practices-building-isv-integrations.pdf
Title metadata reads `[For External] Best Practices - Building ISV Integrations - V(2.6.1)`, dated **2024-06-19**. Could not extract body text (binary PDF, no local PDF renderer available in this environment). Given the 2024 date it predates ABAC policies, query tags, OAuth token federation and Databricks Apps user authorization — treat as stale and do not cite it for mechanics.

### 1.2 What does NOT exist

**[UNDETERMINED] There is no page on docs.databricks.com or learn.microsoft.com that is a "multi-tenant SaaS reference architecture" for Unity Catalog.** I searched `docs.databricks.com` for multi-tenant / multitenancy / tenant isolation guidance. The only official docs page that uses the phrase "multi-tenant applications" prescriptively is the **Lakebase (Postgres) Data API** page, which is about Postgres RLS, not Unity Catalog:

> "**Multi-tenant applications**: Isolate data between different customers or organizations."
> "**Tenant isolation** — Restricts rows to the user's organization: `CREATE POLICY tenant_data ON clients USING (tenant_id = (SELECT tenant_id FROM user_tenants WHERE user_email = current_user));`"
> — https://docs.databricks.com/aws/en/oltp/projects/data-api (doc date 2026-07-13)

That page also makes the architectural point that matters to us:

> "Unlike direct database connections where you control the connection context, HTTP APIs expose your database to multiple users through a single endpoint... RLS policies ensure each user automatically sees only their authorized data."

Note the same trap applies: Lakebase RLS keys off the Postgres `current_user`, which is the Postgres role mapped to the **authenticated Databricks identity**. One shared service principal ⇒ one role ⇒ no per-tenant discrimination. Same failure mode as Unity Catalog row filters (see §3).

### 1.3 The closest thing to official isolation guidance

**Unity Catalog best practices** — https://learn.microsoft.com/en-us/azure/databricks/data-governance/unity-catalog/best-practices (doc date **2026-07-27**). Verbatim:

> "**Catalogs are the primary unit of data isolation in the typical Unity Catalog data governance model.** Schemas add an additional layer of organization."

> "Metastores provide regional isolation but are **not intended as default units of data isolation**. Data isolation typically begins at the catalog level."

> "You can have only **one metastore per region**. All workspaces in that region share that metastore."

> "When work environments and data both have the same isolation requirements, you can bind a catalog to a specific workspace."

> "Give preference to **catalog-level storage** as your primary unit of data isolation."

> "Set up groups so that you can use them effectively to grant access to data and other Unity Catalog securables. **Avoid direct grants to users whenever possible.**"

> "Use service principals to run jobs."

> "Reserve direct `MODIFY` access to production tables for service principals."

Storage striping warning that bites at high tenant counts:

> "ADLS accounts support 20,000 requests per second by default. This can cause workload throttling and slowdown. Using multiple containers in the same storage account doesn't change this account-wide limit. You should therefore **stripe storage across multiple storage accounts**."

**Workspace-catalog binding** — https://learn.microsoft.com/en-us/azure/databricks/data-governance/unity-catalog/access-control/workspace-catalog-binding (doc date **2026-04-29**, updated 2026-05-12):

> "In Unity Catalog, all catalogs are accessible by default from any workspace attached to the same metastore. Workspace-catalog binding lets you override this default to restrict a catalog to one or more specific workspaces. **Access from an unbound workspace is denied, even for users with explicit privilege grants on the catalog.**"

Binding is set via `databricks catalogs update <cat> --isolation-mode ISOLATED` then `databricks workspace-bindings update-bindings catalog <cat> --json '{"add":[{"workspace_id":<id>,"binding_type":"BINDING_TYPE_READ_WRITE"}]}'`. `BINDING_TYPE_READ_ONLY` is also available. External locations, storage credentials and service credentials can be bound the same way.

**Summary of official position:** Databricks tells you the catalog is the isolation unit and tells you how to bind, grant, filter and mask. It does **not** publish a tenant-count-versus-model decision matrix. Anyone claiming Databricks "recommends schema-per-tenant above 100 tenants" is quoting a consultancy, not Databricks.

---

## 2. The five isolation models

### 2.0 The scaling ceilings (the numbers that decide everything)

All from **https://learn.microsoft.com/en-us/azure/databricks/resources/limits** (doc date **2026-07-28**), cross-checked against **https://docs.databricks.com/aws/en/resources/limits**. The page states: *"For limits where **Fixed** is **No**, you can request a limit increase through your Databricks account team."*

| Resource | Metric | Limit | Scope | Fixed? |
|---|---|---|---|---|
| Unity Catalog | **Catalog** | **1,000** | Metastore | No |
| Unity Catalog | **Schema** | **10,000** | Catalog | No |
| Unity Catalog | Schema | 10,000 | Metastore | *(see note)* |
| Unity Catalog | **Table** | **10,000** | Schema | No |
| Unity Catalog | **Table** | **1,000,000** | Metastore | Yes |
| Unity Catalog | Column | 32,768 | Table | Yes |
| Unity Catalog | Volume | 10,000 / 100,000 | Schema / Metastore | No |
| Unity Catalog | Function | 10,000 | Schema | No |
| Unity Catalog | Registered model | 1,000 / 5,000 | Schema / Metastore | No |
| Unity Catalog | External location | 10,000 | Metastore | No |
| Unity Catalog | Storage + service credential (combined) | 1,000 | Metastore | No |
| Unity Catalog | Connection | 1,000 | Metastore | No |
| Unity Catalog | **Privileges (grants)** | **4,000** | Parent object (metastore, catalog, schema) | No |
| Unity Catalog | **Privileges (grants)** | **1,000** | Non-parent object (table, view, share) | No |
| Unity Catalog | **Policy (ABAC)** | 100 / 100 / 50 / 10,000 | Catalog / Schema / Table / Metastore | No |
| Unity Catalog | **Principals per policy** (`TO` **and** `EXCEPT`) | **20** | Policy | No |
| Unity Catalog | Secret | 100 / 1,000 | Schema / Metastore | No |
| Identity | **Users and Service Principals** | **10,000** | **Account** | **No** |
| Identity | **Groups** | **5,000** | Account | Yes |
| Identity | Direct group memberships | 1,500 | Account | Yes |
| Identity | Layers of nested groups | 10 | Account | Yes |
| Tags | Tag assignment | 50 | Securable object | Yes |
| Tags | Governed tag | 1,000 | Account | Yes |
| SQL warehouses | Total number | **1,000** | Workspace | Yes |
| Databricks Apps | App | 100 | Workspace | Yes |
| Jobs | Saved jobs | 12,000 | Workspace | Yes |
| Jobs | Tasks running simultaneously | 2,000 | Workspace | Yes |
| Genie Agents | Tables or views per space | 30 | Genie Agent | No |
| Clean rooms | Clean room | 10 | Metastore | No |

**Workspaces per account** — the AWS limits page lists it, the Azure page does not (on Azure a workspace is an ARM resource, so the constraint moves to Azure subscription quotas: 980 resource groups per subscription, 800 resources per resource group, 25,000 VMs per subscription per region).
- AWS: **Workspaces (Premium tier) = 10 per account**, **Workspaces (Enterprise tier) = 50 per account**, Fixed: No. — https://docs.databricks.com/aws/en/resources/limits
- Azure: no per-account workspace row exists on https://learn.microsoft.com/en-us/azure/databricks/resources/limits. Related caps: **Workspaces per network connectivity configuration = 50** (NCC), **NCC per region = 10 per account**.

**Note on the "10,000 schemas per metastore" claim.** The first search result asserted a metastore-level schema limit. The rendered Azure limits table I read lists `Schema | 10,000 | Catalog` and does **not** list a metastore-scoped schema row. Treat the metastore-wide schema cap as **[UNDETERMINED]** and design against the catalog-scoped 10,000.

**OpenSharing (formerly Delta Sharing) limits** — same page:

| Metric | Limit | Scope | Fixed? |
|---|---|---|---|
| **Provider** | 1,000 | Metastore | No |
| **Recipient** | **5,000** | Metastore | No |
| **Share** | 1,000 | Metastore | No |
| Share | **20** | **Catalog** | No |
| **Table** | 1,000 | Share | No |
| Schema | 500 | Share | No |
| Volume / Function / Model | 1,000 each | Share | No |
| Notebook | 100 | Share | No |
| Active files | 400,000 | Table | Yes |
| RemoveFile actions | 100,000 | Table | Yes |

**API rate limits that constrain per-tenant provisioning** — same page:
- Account SCIM API: `GET` 20/s, `LIST` 240/min, **`PATCH` 2/s**, `POST`/`PUT`/`DELETE` 5/s (Account scope).
- Permissions API: `GET` 100/s, `PATCH`/`PUT` **30/s** (Workspace).
- Workspace-level SCIM: `POST`/`PUT`/`DELETE` **35/min**.
- Query History API `/sql/history/queries` list: **10/s, Account scope**.
- Jobs `/jobs/create` 20/s, `/jobs/runs/submit` 35/s.
- OpenSharing providers/recipients/shares APIs: 400/s each.

The `PATCH 2/s` account SCIM limit is the practical brake on bulk tenant/group onboarding.

**Quota introspection APIs** — https://docs.databricks.com/aws/en/data-governance/unity-catalog/resource-quotas (doc date 2026-06-03):
- `GET /api/2.1/unity-catalog/resource-quotas/{parent_securable_type}/{parent_full_name}/{quota_name}` — "Returns accurate counts within 30 minutes of creation operations."
- `GET /api/2.1/unity-catalog/resource-quotas/all-resource-quotas` — "Unlike `GetQuotas`, `ListQuotas` has no SLA on the freshness of counts."

Both require account-admin authorization. Worth wiring into LakeWright.NET as a preflight check before tenant provisioning.

---

### 2.a Shared table + `tenant_id` column, enforced by row filters / dynamic views

**Mechanics** [VERIFIED] — https://learn.microsoft.com/en-us/azure/databricks/data-governance/unity-catalog/filters-and-masks/ (doc date **2026-07-30**) and `.../manually-apply` (doc date **2026-07-21**).

Table-level form:
```sql
CREATE FUNCTION <fn>(<param> <type>, ...) RETURN {boolean expression};
ALTER TABLE <table> SET ROW FILTER <fn> ON (<column>, ...);
ALTER TABLE <table> DROP ROW FILTER;
-- or at creation:
CREATE TABLE sales (region STRING, id INT) WITH ROW FILTER us_filter ON (region);
```
Column masks:
```sql
CREATE FUNCTION ssn_mask(ssn STRING)
  RETURN CASE WHEN is_account_group_member('HumanResourceDept') THEN ssn ELSE '***-**-****' END;
ALTER TABLE users ALTER COLUMN ssn SET MASK ssn_mask;
-- extra inputs:
ALTER TABLE customers ALTER COLUMN address
  SET MASK mask_address_by_country USING COLUMNS (country, '_address_viewers');
```

Mapping-table (ACL) pattern, which is the canonical multi-tenant shape:
```sql
CREATE TABLE valid_users(username string);
CREATE FUNCTION row_filter()
  RETURN EXISTS(SELECT 1 FROM valid_users v WHERE v.username = SESSION_USER());
CREATE TABLE data_table (x INT, y INT, z INT) WITH ROW FILTER row_filter ON ();
```

**ABAC policies** are now the Databricks-preferred form: *"Databricks recommends ABAC policies when you need consistent row filtering and column masking across many tables. ABAC policies attach at the catalog or schema level and apply automatically based on governed tags."* They require **governed tags** defined at account level.
- https://docs.databricks.com/aws/en/data-governance/unity-catalog/abac/requirements (doc date ~2026-06)
- ABAC compute floor: **serverless**, or standard compute on **DBR 16.4+**, or dedicated compute on DBR 16.4+ with FGAC. Older runtimes cannot access ABAC-secured tables.

**Cost:** no direct charge for the feature, but see the FGAC serverless surcharge below and `billing_origin_product = FINE_GRAINED_ACCESS_CONTROL` in `system.billing.usage`.

**Documented limitations that matter** (verbatim from the filters-and-masks page, doc date 2026-07-30):
- "Databricks Runtime versions below 12.2 LTS do not support row filters or column masks. **These runtimes fail securely**, meaning that if you try to access tables from these runtimes, no data is returned."
- "**You cannot apply row-level security or column masks to a view.**"
- "You cannot use **Iceberg REST catalog or Unity REST APIs** to access tables with row filters or column masks."
- "**Delta Lake APIs are not supported.**"
- "**Time travel does not work** with row-level security or column masks."
- "**Deep and shallow clones are not supported** on tables that have row-level security or column masks."
- "Path-based access to files in tables with policies is not supported."
- "`MERGE` statements do not support tables with row filter or column-mask policies that contain nesting, aggregations, windows, limits, or non-deterministic functions."
- "Databricks Runtime versions below 17.2 do not support `DELETE`, `UPDATE`, and `MERGE` on partitioned tables with row filter or column-mask policies defined on the partition column."
- "Row filters and column masks **cannot reference tables that also have active row filters or column masks**."
- "You cannot create an AI Search index from a table that has row filters or column masks applied."
- "OpenSharing providers cannot share tables with **table-level** row filters or column masks." (ABAC-based ones can be shared if the share owner is exempt.)
- Only **one distinct row filter** can resolve at runtime for a given user+table, and one distinct mask per column+user. Multiple matching ABAC policies ⇒ access denied.
- Dedicated access mode: DBR ≤15.3 cannot read at all; 15.4 LTS–16.2 read-only; writes need DBR 16.3+. And: *"When you query tables with row filters or column masks from dedicated access mode compute, Databricks uses **serverless compute** to enforce fine-grained access controls (FGAC)... **You might be charged for serverless compute resources**."*

**Danger — silent wrong results.** From the same page: if the UDF parameter type doesn't match the column type and ANSI mode is off (`spark.sql.ansi.enabled = false`), uncastable values become `NULL` silently, and *"a row filter that returns all rows instead of filtering them"* is a documented outcome. The docs show a worked example where a mistyped filter returns **every row**. For a tenancy filter that is a total isolation failure. **Set `spark.sql.ansi.enabled = true`.**

**Performance guidance** (same page): prefer simple `CASE` over mapping tables/subqueries; minimise UDF arguments ("Databricks cannot optimize away column references that come from UDF arguments"); avoid many `AND` conjuncts; use `try_divide`-style non-throwing expressions because throwing expressions block predicate pushdown; prefer SQL UDFs over Python.

**Verdict:** scales to any tenant count on object counts (one table, one filter). Isolation strength depends entirely on the caller identity question in §3.

---

### 2.b Schema-per-tenant within one catalog

**Mechanics:** `CREATE SCHEMA tenant_<id>`; grant `USE CATALOG` on the parent + `USE SCHEMA`/`SELECT` on the schema to a per-tenant account group.

**Ceiling:** **10,000 schemas per catalog** (raisable). **10,000 tables per schema.** The grant ceiling is the sharper constraint: **4,000 privileges on a parent object** (metastore, catalog, schema). If every tenant's grants land on the shared catalog, you hit 4,000 rows of grants long before 10,000 schemas — budget grants per catalog, not schemas.

**Correction to a widely-cited claim:** DevIQ's Part 1 states schemas have *"no hard limits on number"* (https://www.deviq.io/insights/multi-tenant-isolation-databricks-part-1, Shawn Davison, 2026-04-21). That is **wrong** against the current limits page — 10,000 per catalog, marked `Fixed: No`. Don't repeat it in the planning package.

**Cost:** no per-schema charge. Storage can be set at schema level (managed storage is resolved at the lowest available level in metastore → catalog → schema).

**Local-dev friendliness:** good. A schema is cheap to create and drop; a dev loop can spin up `tenant_test_<guid>` schemas.

---

### 2.c Catalog-per-tenant within one metastore

**Ceiling:** **1,000 catalogs per metastore** (`Fixed: No`, raisable via account team). This is the hard wall for this model — 1,000 tenants out of the box, and you must negotiate for more.

**Extra ceilings that bind at the same time:**
- One metastore per region (UC best practices). So "more metastores" means "more regions", not "more tenants".
- 10,000 external locations and **1,000 combined storage+service credentials** per metastore. If each tenant gets its own storage credential, the credential cap (1,000) binds at the same point as the catalog cap.
- Managed disaster recovery: **300 catalogs per account**, **10 catalogs per failover group**. If DR is a requirement, catalog-per-tenant caps out at ~300, not 1,000.

**Isolation strength:** strongest logical isolation without separate workspaces. Combine with workspace-catalog binding (`--isolation-mode ISOLATED`) so an unbound workspace is denied regardless of grants.

**Cost:** no per-catalog charge. Real cost is operational — provisioning, grant management, and the SCIM `PATCH 2/s` rate limit during onboarding.

---

### 2.d Workspace-per-tenant

**Ceiling — this is the model that dies first.**
- AWS: **10 workspaces per account (Premium)**, **50 (Enterprise)**, raisable. — https://docs.databricks.com/aws/en/resources/limits
- Azure: no documented per-account workspace cap; constrained instead by Azure subscription quotas and by **50 workspaces per network connectivity configuration** with **10 NCCs per region per account** (⇒ ~500 workspaces per region if every workspace needs serverless private networking).

**Isolation strength:** highest — separate control plane surface, separate compute, separate workspace-local admin groups, and a natural blast radius boundary.

**Cost:** highest. Each workspace needs its own warehouses (or its own idle warehouse spend), its own jobs, its own apps. `Databricks Apps: 100 per workspace`, `SQL warehouses: 1,000 per workspace` are generous per workspace but you're paying per workspace.

**Local-dev friendliness:** poor. You cannot realistically stand up a workspace per test tenant in CI.

**Verdict:** viable only for a small number of high-value / regulated tenants. Not a base model for a SaaS accelerator. Note the workspace-local group trap called out in the binding doc: *"the workspace admins group is a workspace-local group"* and does not work across workspaces.

---

### 2.e OpenSharing (formerly Delta Sharing) to the customer's own platform

**Mechanics** — https://docs.databricks.com/aws/en/opensharing/ (doc date **2026-07-20**):
- **Databricks-to-Databricks**: both sides have UC-enabled workspaces. No token management; the provider requests a *sharing identifier* from the recipient.
- **Databricks-to-Open**: recipient on any platform (Spark, pandas, Power BI, Iceberg IRC clients). Auth by long-lived bearer token **or** OIDC federation with short-lived OAuth tokens.

**Ceiling:** **5,000 recipients per metastore**, **1,000 shares per metastore**, and critically **20 shares per catalog** and **1,000 tables per share**. If you model one share per tenant off a single shared catalog, the **20-shares-per-catalog** limit bites at 20 tenants. You'd need ~1 catalog per 20 tenants, re-coupling you to the 1,000-catalog cap (⇒ ~20,000 tenants theoretical, but you've now got catalog sprawl).

**Cost model** (from the same page):
- "OpenSharing **within a region incurs no egress cost**."
- Serverless recipients: no incremental charge for materialization.
- Classic compute, same account: recipient pays.
- Classic compute, different account: recipient pays, but the **provider's serverless performs filtering** (so the provider absorbs that).
- **Open recipients (non-Databricks): the provider pays** via interactive serverless.
- Cross-region/cross-cloud may trigger cloud-vendor egress unless SecureConnect is enabled, which bills through Databricks instead. UC best practices adds: *"Use OpenSharing for tables that are infrequently accessed, because you are responsible for egress charges from cloud region to cloud region."*

**Governance caveats** (UC best practices, 2026-07-27):
> "Lineage graphs are created at the metastore level, and do not cross region or platform boundaries."
> "Access control is defined at the metastore level, and does not cross region or platform boundaries... you must grant privileges on the destination share in the destination."

Plus, from the filters-and-masks page: **table-level row filters/masks cannot be shared at all**; ABAC-based ones only if the share owner is in the `EXCEPT` clause.

**Verdict:** this is a *delivery* model, not an *isolation* model. Excellent as a premium tier ("bring your own lakehouse"), useless as the default path for a SaaS that owns the UX.

---

## 3. Row-level security mechanics — and the service-principal question

### 3.1 The identity functions

| Function | Semantics | Source |
|---|---|---|
| `session_user()` | The session (connected) user. **Preferred.** | https://learn.microsoft.com/en-us/azure/databricks/sql/language-manual/functions/session_user |
| `current_user()` | **Deprecated alias for `session_user`.** | https://learn.microsoft.com/en-us/azure/databricks/sql/language-manual/functions/current_user (doc date **2026-06-16**) |
| `is_account_group_member(group)` | "Returns true if the **session (connected) user** is a direct or indirect member of the specified group at the account level." | https://learn.microsoft.com/en-us/azure/databricks/sql/language-manual/functions/is_account_group_member (doc date **2026-06-24**) |
| `is_member(group)` | Workspace-local group variant. | (related function, same family) |

Two verbatim quotes that decide the architecture:

> "Returns the user executing the statement. `current_user` is an alias for `session_user`.
> **Warning: This function is deprecated.** The SQL standard reserves `CURRENT_USER` for the authorized user, but in Databricks it returns the session user, which can be misleading. Use `session_user` instead.
> **Note: When called by a service principal, this function returns the UUID of the service principal instead of a readable name.**"
> — `current_user` reference, doc date 2026-06-16

> "All filters run with **definer's rights** except for functions that check user context (for example, the `SESSION_USER` and `IS_ACCOUNT_GROUP_MEMBER` functions) **which run as the invoker**."
> — https://learn.microsoft.com/en-us/azure/databricks/data-governance/unity-catalog/filters-and-masks/manually-apply, doc date 2026-07-21

And for ABAC policies:

> Policies on underlying tables "are evaluated using the **session user's identity**" — the person (or principal) running the query.
> — https://docs.databricks.com/aws/en/data-governance/unity-catalog/abac/requirements

### 3.2 The precise answer to "one service principal for all tenants"

**If your ASP.NET backend connects with ONE service principal for all tenants, row filters and column masks give you ZERO tenant isolation.** [VERIFIED]

The chain of reasoning, each link cited:
1. Row filters and ABAC policies resolve the caller via `session_user()` / `is_account_group_member()`, which are explicitly documented as **invoker-evaluated** and as returning **the session (connected) user**.
2. When the connection is authenticated as a service principal, `session_user()` returns **that service principal's UUID** — documented verbatim above. It does not, and cannot, know which of your end users triggered the HTTP request.
3. Therefore the filter predicate evaluates identically for every tenant's request. A filter of the form `WHERE tenant_id = (SELECT tenant_id FROM map WHERE username = session_user())` resolves to the *same* row set on every call. Either the SP maps to one tenant (and all other tenants see nothing), or the SP is exempt/unmapped (and every tenant sees everything, or nothing).
4. `is_account_group_member()` has the same problem: it tests the SP's group memberships. An SP can be in many groups, but it is in the *union* of all tenants' groups simultaneously, so a group-based filter grants the union of all tenants' rows on every query.

**Conclusion: with a single shared service principal you MUST enforce tenancy in your own query layer.** Row filters degrade to a defence-in-depth backstop against your own bugs at best — and even that is weak, because the only correct configuration for a shared SP is "SP sees everything", which means the filter contributes nothing.

The same conclusion is stated plainly by Databricks for Databricks Apps, which is the closest official analogue to our architecture:

> "**All actions initiated by the app use the service principal's permissions.**... However, it **doesn't support user-level access control. All users who interact with the app share the same permissions defined for the service principal, which prevents the app from enforcing fine-grained policies based on individual user identity.**"
> — https://learn.microsoft.com/en-us/azure/databricks/dev-tools/databricks-apps/auth, doc date **2026-07-21**

versus the user-authorization mode:

> "User authorization enables fine-grained access control by applying Unity Catalog features like **row-level filters and column masks** to app activity... Because Databricks evaluates user authorization requests with the **user's identity**, these policies apply automatically when the app accesses data... **No additional filtering logic is needed in the app.**"
> — same page

That is the official, explicit statement of the trade-off: **app identity ⇒ you filter; user identity ⇒ Unity Catalog filters.**

### 3.3 Where row filters DO work for us

Row filters become a real tenancy control only when the *connection* carries a per-tenant identity. Three ways to get that:
1. **Per-tenant service principal** — the SP is the tenant. `session_user()` = SP UUID = tenant key. Filter on a mapping table keyed by SP UUID, or put each SP in a `tenant_<id>` group and use `is_account_group_member()`. Ceiling: **10,000 users + service principals per account, combined** (`Fixed: No`).
2. **On-behalf-of end-user tokens** — the connection is the end user. Ceiling: end users must exist in your Databricks account, so the same 10,000 cap applies, and it's now shared with real humans.
3. **Per-tenant warehouse/compute with a per-tenant run-as identity** — same as (1) with extra cost.

### 3.4 Compute support

| Compute | Table-level filters/masks | ABAC policies |
|---|---|---|
| **SQL warehouse** (classic/pro/serverless) | ✅ Supported | ✅ Supported |
| Serverless compute | ✅ | ✅ |
| Standard access mode | ✅ DBR 12.2 LTS+ | ✅ DBR 16.4+ |
| Dedicated access mode | ⚠️ DBR 15.4 LTS+ read-only; writes DBR 16.3+; runs FGAC on serverless (billable) | ⚠️ DBR 16.4+ with FGAC |
| DBR < 12.2 LTS | ❌ fails securely (returns no data) | ❌ |
| Iceberg REST catalog / Unity REST API | ❌ | ❌ |
| Delta Lake APIs | ❌ | ❌ |

Source: filters-and-masks index + manually-apply + abac/requirements, all cited above. **SQL warehouses (including serverless) are fully supported** — which is what a .NET app connecting over the SQL connector or Statement Execution API will use.

---

## 4. Acting as the end user

### 4.1 Databricks Apps on-behalf-of-user (OBO) — Public Preview

https://learn.microsoft.com/en-us/azure/databricks/dev-tools/databricks-apps/auth (doc date **2026-07-21**)

- Every Databricks App gets a **dedicated service principal**, auto-provisioned, non-reusable, deleted with the app. Credentials injected as `DATABRICKS_CLIENT_ID` / `DATABRICKS_CLIENT_SECRET`.
- **User authorization** forwards the user's access token to the app in the **`x-forwarded-access-token` HTTP header**. The app passes it straight to the SQL connector:
  ```python
  user_token = request.headers.get("x-forwarded-access-token")
  conn = sql.connect(server_hostname=cfg.host, http_path="<warehouse-http-path>", access_token=user_token)
  ```
  Node equivalent uses `authType: 'access-token', token: userToken`.
- Scopes are declared per app (`sql`, `genie`, `files`, …). Default without any scope: `iam.access-control:read`, `iam.current-user:read` — identity only, no data.
- **"When a user first accesses an app, Databricks prompts them to explicitly authorize the app to act within the requested scopes. After granting consent, users can't revoke it."**
- Workspace admins can allowlist which scopes developers may request; **None** disables user authorization entirely.
- Status: **Public Preview**, and a workspace admin must enable it.

**Applicability to LakeWright.NET:** this is the *Databricks-hosted* app runtime. An externally-hosted ASP.NET app does not receive `x-forwarded-access-token`. To replicate the pattern outside Databricks Apps you need §4.2.

### 4.2 OAuth token federation — the external-app path

https://docs.databricks.com/aws/en/dev-tools/auth/oauth-federation (doc date **2026-01-29**), `.../oauth-federation-policy` and `.../oauth-federation-exchange` (both doc date **2026-06-16**).

Two policy types:
- **Account federation policy** — "enables all users and service principals in your Databricks account to access Databricks APIs using tokens from your identity provider." Typically paired with SCIM so IdP users are synced into the account.
- **Service principal federation policy** (workload identity federation) — an automated workload authenticates **as a specific service principal** using a runtime-issued token, no stored Databricks secret.

Token exchange:
```
POST https://<databricks-workspace-host>/oidc/v1/token                       # account-wide policies
POST https://<databricks-account-host>/oidc/accounts/<account-id>/v1/token   # account-level resources
```
with `grant_type=urn:ietf:params:oauth:grant-type:token-exchange`,
`subject_token=<IdP JWT>`, `subject_token_type=urn:ietf:params:oauth:token-type:jwt`, `scope=all-apis`,
and `client_id` **only** for service-principal federation policies.

- JWT must be signed **RS256 or ES256**.
- **"The resulting Databricks OAuth token has the same expiration (`exp`) claim as the JWT provided."**
- Subject mapping: the `subject_claim` (default `sub`) maps directly to a Databricks username. If `sub` is `alice@customer.com`, the exchanged token authenticates **as that Databricks user**.
- **Policy limits: 20 account federation policies per account; 20 service principal federation policies per service principal.**
- Rate limits on the exchange endpoint: **[UNDETERMINED]** — not documented.

**The catch:** the app cannot mint identities. It can only exchange a JWT that its IdP already issued for a subject that already exists in the Databricks account. So a true per-end-user model requires **every end user of every tenant to be SCIM-provisioned into your Databricks account** — against the **10,000 users + service principals** cap. For a SaaS with 1,000 tenants × 20 users, that's 20,000 principals: over the cap, needing an account-team increase, plus SCIM `PATCH 2/s` for the churn.

### 4.3 Per-tenant service principals — the pragmatic middle

Cost and limits:
- **10,000 users + service principals per account, combined, `Fixed: No`** (raisable). — resources/limits, 2026-07-28
- **5,000 groups per account, `Fixed: Yes`** — so a `tenant_<id>_users` group per tenant caps you at 5,000 tenants and that one is marked non-raisable.
- **1,500 direct group memberships**, **10 layers of nested groups**.
- Service principals cost nothing to create. The cost is SCIM/API throughput (`PATCH 2/s`) and lifecycle management.
- Note "A user can't belong to more than 50 Databricks accounts."

This is the pattern the one serious practitioner write-up recommends [COMMUNITY]:

> "a single-workspace architecture using **Service Principal per organization** (SSO-SPN pattern). Each tenant organization receives its own Service Principal, workspace mapping, and permissions within Unity Catalog. Users authenticate via their organization's identity provider (Entra, Okta, Auth0) rather than requiring individual Databricks accounts. **The application layer manages tenant isolation**, with all API calls routed through the tenant's SPN while maintaining user identity in application logs."
> — Ust Oldfield (Head of Analytics, Advancing Analytics), *Built-On Databricks: Delivering Multi-Tenant Analytics*, **2026-05-20**. https://www.advancinganalytics.co.uk/blog/built-on-databricks-delivering-multi-tenant-analytics

Note what that says: even with SPN-per-tenant, *the application layer manages tenant isolation*, and end users are **not** Databricks principals. The SPN gives you an audit boundary and a UC grant boundary; user-level identity stays in your app.

### 4.4 `SET` session variables — not a security boundary

**[UNDETERMINED as a security mechanism, and do not use it as one.]** There is no documented "impersonate this user for this session" `SET` command. Query tags (§5) are session/statement metadata for attribution, explicitly warned as plain text and globally replicated — they are not authenticated and any client can set any value. Anything that reads a client-supplied session variable inside a row filter is trivially spoofable by whoever holds the connection.

---

## 5. Cost attribution per tenant

### 5.1 Query tags — the right primitive (Public Preview)

https://learn.microsoft.com/en-us/azure/databricks/sql/user/queries/query-tags (doc date **2026-07-24**, updated 2026-07-27)

> "Query tags are custom key-value pairs that you apply to SQL workloads. You can use query tags to group queries by business context, **track warehouse costs**, and identify sources of long-running queries."

Surfaces in `system.query.history.query_tags`, the Query History UI, and the ListQueries API.

**Setting them from .NET-relevant paths:**
- **Statement Execution API** (statement-level) — the cleanest fit for an ASP.NET backend:
  ```bash
  curl -X POST "https://${DATABRICKS_HOST}/api/2.0/sql/statements" \
    -H "Authorization: Bearer ${DATABRICKS_TOKEN}" -H "Content-Type: application/json" \
    -d '{"warehouse_id":"abc123","statement":"SELECT ...",
         "query_tags":[{"key":"team","value":"engineering"},{"key":"env","value":"prod"}]}'
  ```
- **JDBC (OSS driver v3.0.3+)**: `query_tags=team:engineering,env:prod` in the URL or a `Properties` object.
- **JDBC/ODBC (Simba)**: parameter is **`ssp_query_tags`**, and ODBC additionally requires `ApplySSPWithQueries=0`.
- **SQL**: `SET QUERY_TAGS ...` (session-level), usable anywhere you can submit SQL.
- Session-config string syntax: `key:value` pairs comma-separated; escape `:` `,` `\` with a backslash.

**Limits** (verbatim from the page):
- "Query tags are supported for **Databricks SQL workloads only**. The `query_tags` column is not populated for other compute types."
- "Query tags are limited to **10 KB per session**." Exceeding ⇒ tags dropped + `tags_dropped: true` sentinel.
- "Each query supports a maximum of **20 user-specified tags**."
- "Tag keys and values must not exceed **128 characters**."
- "Tag keys must not contain the characters `,`, `:`, `-`, `/`, `=`, or `.`" — **so `tenant-id` and `tenant.id` are invalid keys; use `tenant_id`.**
- "Keys starting with `@@` are reserved for internal use."
- Via session config, invalid tags are silently dropped with `tag_invalid: true`; **via SQL they raise an error**. Prefer SQL or the API if you need failures to be loud.

> "**Warning:** Tag data is stored as plain text and might be replicated globally. Do not include passwords, personally identifiable information, or other sensitive data in tag keys or values."

⇒ Use an opaque tenant GUID, never a customer name.

Query pattern:
```sql
SELECT statement_id, query_tags, executed_by, start_time
FROM system.query.history
WHERE MAP_CONTAINS_KEY(query_tags, 'tenant_id') AND query_tags['tenant_id'] = '<guid>'
ORDER BY start_time DESC;
```

**Minimum connector versions:** Python connector v4.1.3 (session) / v4.2.6 (statement); Node.js v1.12.0; Go v1.9.0; JDBC OSS v3.0.3; dbt-databricks 1.11.0; Databricks SDK for Python 0.86.0. **[UNDETERMINED] No .NET/ADO.NET connector version is listed on this page** — the .NET path is the Statement Execution API (`query_tags` in the request body) or the ODBC driver with `ssp_query_tags` + `ApplySSPWithQueries=0`. This is a real gap to design around and should be flagged in the planning package.

### 5.2 `system.query.history`

https://learn.microsoft.com/en-us/azure/databricks/admin/system-tables/query-history (doc date **2026-07-30**). **Public Preview.** Path `system.query.history`. Retention **365 days**, regional, **does not support streaming**.

Columns useful for per-tenant attribution: `statement_id`, `executed_by` / `executed_by_user_id` (who ran it), **`executed_as` / `executed_as_user_id`** ("the name of the user or service principal whose privilege was used to run the statement"), `compute` struct (`type`: `WAREHOUSE` or `SERVERLESS_COMPUTE`, `warehouse_id`), `total_duration_ms`, `execution_duration_ms`, **`waiting_for_compute_duration_ms`**, **`waiting_at_capacity_duration_ms`** (queue time — your noisy-neighbour SLI), `read_bytes`, `read_rows`, `produced_rows`, `total_task_duration_ms`, `from_result_cache`, `query_parameters`, `query_source`, **`query_tags`**.

Caveats: `statement_text` and `error_message` are **empty if customer-managed keys are configured** (decryptable only by adding a key config to the `system` catalog, which "removes any Unity Catalog grants you previously applied to the `system.query` schema" — reapply grants after). "Due to storage limitations, longer statement text values are compressed."

Access: "**By default, only admins have access** to the system table." To share with others, Databricks recommends a dynamic view per user/group.

### 5.3 `system.billing.usage`

https://learn.microsoft.com/en-us/azure/databricks/admin/system-tables/billing (doc date **2026-07-23**, updated 2026-07-27). Retention **365 days**, **global**, supports streaming.

Key columns: `record_id`, `workspace_id`, `sku_name`, `usage_start_time`/`usage_end_time`/`usage_date`, **`custom_tags` (map)**, `usage_quantity` (DBUs), **`usage_metadata` struct** (`warehouse_id`, `cluster_id`, `job_id`, `job_run_id`, `app_id`, `app_name`, `dlt_pipeline_id`, `endpoint_name`, `usage_policy_id`, …), **`identity_metadata` struct** (`run_as`, `owned_by`, `created_by`), `record_type` (`ORIGINAL` / `RETRACTION` / `RESTATEMENT`), `billing_origin_product`, `product_features`.

**`identity_metadata.owned_by`** "only applies to SQL warehouse usage and logs the user or service principal who **owns** the SQL warehouse responsible for the usage" — so warehouse-per-tenant gives you first-class billing attribution via `owned_by` + `warehouse_id`.

**Correction handling matters** — always aggregate, never read single rows:
```sql
SELECT usage_metadata.warehouse_id, usage_date, SUM(usage_quantity) AS dbus
FROM system.billing.usage
GROUP BY ALL HAVING dbus != 0
```
because a `RETRACTION` row carries a negative `usage_quantity` that cancels the `ORIGINAL`.

Products relevant to us: `SQL`, `INTERACTIVE`, `APPS`, `DATA_SHARING`, **`FINE_GRAINED_ACCESS_CONTROL`** ("Serverless usage from fine-grained access control on dedicated compute"), `DEFAULT_STORAGE`, `NETWORKING`.

Pricing join: `system.billing.list_prices` (retention **Indefinite**, global).

### 5.4 The attribution architecture, and its limits

**Critically: `system.billing.usage.custom_tags` is populated from compute-resource tags (warehouse/cluster/job tags), NOT from query tags.** Query tags live only in `system.query.history`. So there are exactly two attribution strategies:

| Strategy | Granularity | Accuracy | Mechanism |
|---|---|---|---|
| **Warehouse-per-tenant** | Direct $ | Exact | Tag the warehouse with `tenant_id` ⇒ flows to `usage.custom_tags`; also `usage_metadata.warehouse_id` + `identity_metadata.owned_by` |
| **Shared warehouse + query tags** | Proportional | **Estimated** | Tag each statement with `tenant_id`; compute each tenant's share of warehouse DBUs by apportioning `system.billing.usage` over a work proxy from `system.query.history` (`total_task_duration_ms`, `read_bytes`, or `execution_duration_ms`) grouped by `query_tags['tenant_id']` |

The second is the only option that scales, and it is **inherently an allocation model, not a measurement**. Idle warehouse time, autoscaling overhead, and cache warming belong to no tenant and must go to a shared/overhead bucket. State this honestly in the planning package: on a shared warehouse, **per-tenant cost is apportioned, not metered**. `total_task_duration_ms` is the least-bad proxy (it is "the combined time it took to run the query across all cores of all nodes").

### 5.5 Latency — this is the answer to "what's the latency of system tables"

**[VERIFIED]** Databricks does **not** publish a numeric SLA. The documented statement is:

> "**No support for real-time monitoring. Data is updated throughout the day.** If you don't see a log for a recent event, check back later."
> — https://learn.microsoft.com/en-us/azure/databricks/admin/system-tables/ (doc date **2026-07-30**), "Known issues"

Other operational facts from that page:
- System tables are free; you pay only for compute to query them.
- Data is shared to you **via OpenSharing** from a Databricks-hosted storage account in your metastore's region.
- **`VACUUM` retention is the default 7 days** — "your streaming query might break if it lags behind by more than 7 days."
- Streaming from system tables needs **DBR 16.4+**; CDF via `readChangeFeed` needs **DBR 17.3+**; `Trigger.AvailableNow` needs **DBR 18.0+**. Set `skipChangeCommits = true`.
- "New columns may be added to existing system tables at any time" — enable schema evolution if you copy them.
- "System table queries that are not sufficiently selective return the following error: `System Table query returned too much data. Please repeat query with more selective predicates.`"

**Practical consequence for LakeWright.NET:** system tables cannot back a live in-product usage meter or a real-time quota enforcer. They are a **daily batch billing source**. If the product needs near-real-time per-tenant usage display, LakeWright.NET must maintain its own counter in the app tier (e.g. record `statement_id` + duration + bytes from the Statement Execution API response at query time) and reconcile against `system.billing.usage` on a daily job.

Retention summary for tables we care about: `billing.usage` 365d, `query.history` 365d, `access.audit` 365d, `compute.node_timeline` 90d, `billing.list_prices` indefinite, `access.workspaces_latest` indefinite.

---

## 6. Known pitfalls

### 6.1 Official — concurrency, queuing, cold start

**SQL warehouse sizing, scaling and queuing** — https://learn.microsoft.com/en-us/azure/databricks/compute/sql-warehouse/warehouse-behavior (doc date **2026-07-21**). Verbatim:

- "The **maximum number of queries in a queue for all warehouse types is 1,000**."
- Classic/pro: "These SKUs have a **fixed limit of one cluster per 10 concurrent queries**."
- Classic/pro autoscaling: "2-6 minutes of query load: Add 1 cluster. 6-12 minutes: Add 2 clusters. 12-22 minutes: Add 3 clusters. Over 22 minutes: Add 3 clusters plus 1 more for every additional 15 minutes of load." / "If a query waits in the queue for **5 minutes**, the warehouse scales up." / "If load remains low for **15 consecutive minutes**, the warehouse scales down."
- Serverless uses **Intelligent Workload Management (IWM)**: ML-predicted resource requirements, queue if no capacity, autoscale on rising wait times. "Databricks recommends using a serverless SQL warehouse for most workloads."
- Sizing advice: "Start with a single larger warehouse and let serverless features manage concurrency... It is usually more efficient to size down if necessary than to start small and scale up." Monitor **Peak Queued Queries**; "A consistent value above 0 indicates that you may need a larger cluster size or more clusters."
- Warehouse-level `statement_timeout` is in **Beta** (admin must enable the preview). Precedence: session > warehouse > workspace. This is the tool for capping a runaway tenant query.
- Classic/pro on Azure need vCPU quota: "between **4 and 8 Azure vCPU for each core in the cluster**", plus re-provisioning roughly every 24 hours.

**Databricks blog, *Architecting a High-Concurrency, Low-Latency Data Warehouse on Databricks That Scales*** — 2025-09-02, Ben Dunmire, Dan Lueck, Jen Lim. https://www.databricks.com/blog/architecting-high-concurrency-low-latency-data-warehouse-databricks-scales
- "Segment users (human/automated) and their query patterns (interactive BI, ad hoc, scheduled reports) to use different warehouses scoped by application context." — i.e. **Databricks' own answer to noisy neighbours is warehouse segmentation**, which for a SaaS means warehouse-per-tier or warehouse-per-tenant-cohort.
- Threshold: "persistent queuing (where **peak queued queries are >10**)" indicates a scaling problem.
- On noisy neighbours specifically: "**Reach out to your Databricks account contact to access upcoming features intended to prevent noisy neighbors.**" ⇒ As of that post there was **no shipped, self-serve noisy-neighbour control**. I found no later doc superseding this. **[UNDETERMINED]** whether such a feature has since GA'd.

### 6.2 Community anecdotes

**[COMMUNITY] The 20,000-customer identity wall.** Databricks Community thread *Azure Databricks Multi Tenant Solution*, opened 2024-09-19 by `funsjanssen`, answered **2025-11-06 by `mark_ott` (Databricks Employee)**. https://community.databricks.com/t5/administration-architecture/azure-databricks-multi-tenant-solution/td-p/91025
- Question: 20,000+ customers from different organisations needing SSO into Power BI over Databricks with RLS/CLS, without paying for Entra Premium MFA trust at that scale.
- Databricks employee response: "Azure Databricks does not natively support directly configuring external OIDC/SAML providers (like Auth0)"; identity "must transit through Entra"; Entra B2B is "robust but **expensive for MFA trust at mass scale**"; "direct Auth0 as an SSO bridge is not currently feasible for Azure Databricks without major caveats."
- Recommended workarounds included dynamic views with `current_user()` **and explicitly "app-layer impersonation models (with security trade-offs noted)"**.
- *Caveat:* that answer predates or sits alongside the OAuth token federation docs (2026-01-29 / 2026-06-16), which do allow an arbitrary HTTPS issuer + JWKS. Whether account-wide token federation now supersedes the "must transit through Entra" answer on Azure is **[UNDETERMINED]** — worth a direct test before building on it.

**[COMMUNITY] Workspace-per-tenant + catalog-per-tenant + RLS, all three.** *Building MultiTenant Architecture on Databricks Platform*, author `Rathorer`, Databricks Community Articles, **2025-07-21**. https://community.databricks.com/t5/community-articles/building-multitenant-architecture-on-databricks-platform/td-p/125937
- Advocates workspace-per-tenant as primary, catalog-per-tenant bound to that workspace, per-tenant storage containers with per-workspace IAM roles, and RLS as a backstop.
- Decision matrix given: single shared metastore "suitable for B2B SaaS with **<50 practical tenants**"; metastore-per-tenant for regulated industries.
- **Says nothing about cost attribution.** That absence is itself informative — the hard part is under-served in community material.

**[COMMUNITY] Model-by-tenant-count sizing.** Shawn Davison (DevIQ), *Multi-Tenant Isolation for Databricks, Part 1: Choosing the Right Architecture*, **2026-04-21**. https://www.deviq.io/insights/multi-tenant-isolation-databricks-part-1
- Workspaces: "< 20 enterprise tenants needing hard compliance separation"; Catalogs: "< 100 tenants"; Schemas: "> 100 tenants", "recommended for most growth-stage and enterprise SaaS platforms".
- ⚠️ **Contains a factual error**: claims schemas have "no hard limits on number". Contradicted by the limits page (10,000 per catalog). Don't cite this number.
- Part 2, *Tenant-Scoped Genie Spaces*, **2026-05-06** (https://www.deviq.io/insights/multi-tenant-isolation-databricks-part-2) argues for one Genie space per tenant — relevant because **Genie Agents are limited to 30 tables or views per space** (`Fixed: No`) and there is no documented per-account Genie space cap, but 10,000 conversations per space.

**[COMMUNITY] App-layer-owns-tenancy, SPN-per-tenant.** Ust Oldfield, Advancing Analytics, **2026-05-20** (cited in §4.3). Also warns against "recreating Databricks UI features", which results in "maintaining a poor imitation".

**[COMMUNITY] Serverless SQL cost surge.** Rajeshwari Raghuraman, Medium. https://medium.com/@rrajeshwaris/serverless-sql-warehouse-cost-surge-and-optimization-operation-fb9c8ac22ea8
- XL warehouse, 1–8 cluster autoscale, ~$150/day, **45-minute auto-stop**, provisioned for a 12–15 concurrent-query peak while actual average was 3–5. 23 queries ran >15 minutes. Reduced $17,500 → $15,000/month.
- Two lessons for us: **auto-stop is the single biggest idle-cost lever**, and **you need a per-query kill switch** — which is what the Beta warehouse-level `statement_timeout` provides.

**[COMMUNITY] Provisioning throughput.** Databricks Community threads on ingesting from hundreds of separate ADLS accounts note that each external location maps to exactly one storage path with no wildcards, and flag "no granular permissions at the metastore level — you're either an admin or not", which complicates having a provisioning service principal that can create and grant across all tenant catalogs without being a full metastore admin. https://community.databricks.com/t5/data-engineering/best-pattern-for-ingesting-data-from-hundreds-of-separate-adls/td-p/149991

### 6.3 Pitfalls I'd add from reading the primary docs

1. **The ANSI-mode row-filter footgun** (§2.a) — a type mismatch silently returns *all rows*. Highest-severity finding in this document.
2. **`EXCEPT` clause capped at 20 principals per ABAC policy**, and the `TO` list shares that 20. You cannot enumerate tenants in a policy; you must use groups or a mapping table.
3. **Materialized views and streaming tables break under ABAC** unless the pipeline owner and run-as identity are in `EXCEPT` — a refresh-failure trap that only appears in production.
4. **Query tag key charset**: `-`, `.`, `/`, `=`, `,`, `:` are all illegal in keys. `tenant-id` fails.
5. **Grants ceiling before object ceiling**: 4,000 privileges per parent object will bind before 10,000 schemas per catalog if you grant per tenant on the shared catalog.
6. **DR caps catalog-per-tenant at ~300**, not 1,000, if managed disaster recovery is in scope.
7. **20 shares per catalog** kills naive share-per-tenant off a shared catalog at 20 tenants.
8. **Workspace-local admin groups don't cross workspaces** — bites when unbinding or re-binding default workspace catalogs.
9. **System tables are batch, not real-time**, and 7-day `VACUUM` will break a lagging stream.

---

## 7. Recommendation for LakeWright.NET

1. **Default model: schema-per-tenant in a small number of catalogs**, with tenancy enforced in the .NET query layer (a mandatory catalog/schema resolution step keyed off the authenticated tenant), plus per-tenant SPNs where an audit/grant boundary is required. Catalog-per-tenant as an upgrade tier for tenants who demand it, bounded at ~300 (DR) / 1,000 (raisable).
2. **Do not build the isolation story on row filters with a shared service principal.** Cite §3.2. If we want UC-enforced tenancy, we need per-tenant identity on the connection, and that is a deliberate, costly architectural choice with a 10,000-principal ceiling.
3. **Row filters/ABAC as defence-in-depth only where a per-tenant SPN exists** — where `session_user()` is genuinely the tenant.
4. **Cost: query tags + apportionment on the shared tier, warehouse-per-tenant on the premium tier.** Be explicit that shared-tier per-tenant cost is an allocation, and that the .NET path to query tags is the Statement Execution API or ODBC `ssp_query_tags`, not a first-class .NET connector option.
5. **Noisy neighbour: warehouse segmentation by tier, serverless with IWM, warehouse-level `statement_timeout` (Beta), and alert on `waiting_at_capacity_duration_ms`.** There is no shipped self-serve noisy-neighbour control.
6. **Local-dev friendliness ranking:** shared-table > schema-per-tenant > catalog-per-tenant >> OpenSharing >> workspace-per-tenant. Anything above catalog-per-tenant is not reproducible in CI.

---

## 8. Open questions

- **[UNDETERMINED]** Metastore-wide schema cap — is there one distinct from the 10,000-per-catalog cap?
- **[UNDETERMINED]** Rate limits on the OAuth token-exchange endpoint (`/oidc/v1/token`). Matters if every end-user request triggers an exchange.
- **[UNDETERMINED]** Whether a first-class **.NET/ADO.NET** Databricks connector supports `query_tags` natively (the query-tags page lists Python/Node/Go/JDBC/ODBC/dbt only).
- **[UNDETERMINED]** Whether the "upcoming noisy-neighbor features" from the 2025-09-02 blog have shipped.
- **[UNDETERMINED]** Whether account-wide OAuth token federation on **Azure** Databricks now permits a non-Entra issuer, superseding the 2025-11-06 Databricks-employee answer.
- **[UNDETERMINED]** Whether Databricks Apps user authorization (Public Preview) has a documented GA date or a supported pattern for externally-hosted apps.
- Content of the 2024 ISV best-practices PDF (could not extract; no PDF renderer available).

---

## Source index

| # | URL | Type | Date |
|---|---|---|---|
| 1 | https://learn.microsoft.com/en-us/azure/databricks/resources/limits | Official docs | 2026-07-28 |
| 2 | https://docs.databricks.com/aws/en/resources/limits | Official docs | 2026-07-28 |
| 3 | https://learn.microsoft.com/en-us/azure/databricks/data-governance/unity-catalog/filters-and-masks/ | Official docs | 2026-07-30 |
| 4 | https://learn.microsoft.com/en-us/azure/databricks/data-governance/unity-catalog/filters-and-masks/manually-apply | Official docs | 2026-07-21 |
| 5 | https://docs.databricks.com/aws/en/data-governance/unity-catalog/abac/requirements | Official docs | ~2026-06 |
| 6 | https://docs.databricks.com/aws/en/data-governance/unity-catalog/abac/policy-evaluation | Official docs | 2026-06-23 |
| 7 | https://learn.microsoft.com/en-us/azure/databricks/sql/language-manual/functions/current_user | Official docs | 2026-06-16 |
| 8 | https://learn.microsoft.com/en-us/azure/databricks/sql/language-manual/functions/is_account_group_member | Official docs | 2026-06-24 |
| 9 | https://learn.microsoft.com/en-us/azure/databricks/dev-tools/databricks-apps/auth | Official docs | 2026-07-21 |
| 10 | https://docs.databricks.com/aws/en/dev-tools/auth/oauth-federation | Official docs | 2026-01-29 |
| 11 | https://docs.databricks.com/aws/en/dev-tools/auth/oauth-federation-policy | Official docs | 2026-06-16 |
| 12 | https://docs.databricks.com/aws/en/dev-tools/auth/oauth-federation-exchange | Official docs | 2026-06-16 |
| 13 | https://learn.microsoft.com/en-us/azure/databricks/sql/user/queries/query-tags | Official docs | 2026-07-24 |
| 14 | https://learn.microsoft.com/en-us/azure/databricks/admin/system-tables/query-history | Official docs | 2026-07-30 |
| 15 | https://learn.microsoft.com/en-us/azure/databricks/admin/system-tables/billing | Official docs | 2026-07-23 |
| 16 | https://learn.microsoft.com/en-us/azure/databricks/admin/system-tables/ | Official docs | 2026-07-30 |
| 17 | https://learn.microsoft.com/en-us/azure/databricks/compute/sql-warehouse/warehouse-behavior | Official docs | 2026-07-21 |
| 18 | https://learn.microsoft.com/en-us/azure/databricks/data-governance/unity-catalog/best-practices | Official docs | 2026-07-27 |
| 19 | https://learn.microsoft.com/en-us/azure/databricks/data-governance/unity-catalog/access-control/workspace-catalog-binding | Official docs | 2026-04-29 |
| 20 | https://docs.databricks.com/aws/en/data-governance/unity-catalog/resource-quotas | Official docs | 2026-06-03 |
| 21 | https://docs.databricks.com/aws/en/opensharing/ | Official docs | 2026-07-20 |
| 22 | https://docs.databricks.com/aws/en/oltp/projects/data-api | Official docs | 2026-07-13 |
| 23 | https://www.databricks.com/blog/introducing-opensharing-next-evolution-delta-sharing-agentic-era | Databricks blog | 2026-06-10 |
| 24 | https://www.databricks.com/blog/introducing-new-databricks-partner-program-and-well-architected-framework-isvs-and-data | Databricks blog | 2026-02-10 |
| 25 | https://www.databricks.com/blog/architecting-high-concurrency-low-latency-data-warehouse-databricks-scales | Databricks blog | 2025-09-02 |
| 26 | https://databrickslabs.github.io/partner-architecture/ | Databricks Labs | n/d |
| 27 | https://assets.docs.databricks.com/_extras/documents/best-practices-building-isv-integrations.pdf | Databricks PDF | 2024-06-19 |
| 28 | https://community.databricks.com/t5/administration-architecture/azure-databricks-multi-tenant-solution/td-p/91025 | Community (DB employee reply) | 2024-09-19 / 2025-11-06 |
| 29 | https://community.databricks.com/t5/community-articles/building-multitenant-architecture-on-databricks-platform/td-p/125937 | Community | 2025-07-21 |
| 30 | https://www.advancinganalytics.co.uk/blog/built-on-databricks-delivering-multi-tenant-analytics | Practitioner blog | 2026-05-20 |
| 31 | https://www.deviq.io/insights/multi-tenant-isolation-databricks-part-1 | Practitioner blog | 2026-04-21 |
| 32 | https://www.deviq.io/insights/multi-tenant-isolation-databricks-part-2 | Practitioner blog | 2026-05-06 |
| 33 | https://medium.com/@rrajeshwaris/serverless-sql-warehouse-cost-surge-and-optimization-operation-fb9c8ac22ea8 | Practitioner blog | n/d |
| 34 | https://community.databricks.com/t5/data-engineering/best-pattern-for-ingesting-data-from-hundreds-of-separate-adls/td-p/149991 | Community | n/d |
