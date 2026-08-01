# Databricks API Surface & Operational Gotchas for a .NET SaaS Backend

**Research date:** 2026-07-31
**Scope:** The precise REST API surface a .NET SaaS backend (LakeWright.NET) would call, plus operational limits and gotchas.
**Method:** Every claim below was read from a live docs page during this session unless explicitly marked otherwise.

**Legend**
- **[V]** VERIFIED — read from a live docs page this session, URL given.
- **[V-search]** VERIFIED via search-result excerpt of a primary doc page (docs.databricks.com / learn.microsoft.com), but the full page was not rendered. Slightly weaker than [V].
- **[UNDOC]** Not documented anywhere I could find. Stated as undocumented, not guessed.
- **[INFER]** My inference from verified facts. Flagged explicitly; not a doc claim.

**Caveat on the `/api/` reference pages:** `docs.databricks.com/api/workspace/...` pages are client-side rendered and return an empty shell to a fetcher. All field-level detail below therefore comes from the prose docs (tutorials, reference guides, limits pages), which are static and reliable. Where a field-level detail exists only on the JS-rendered API reference, I have marked it [UNDOC] rather than guessing.

---

## 1. OAuth

### 1.1 M2M — service principal client credentials

**[V]** https://docs.databricks.com/aws/en/dev-tools/auth/oauth-m2m

| Item | Value |
|---|---|
| Workspace token endpoint | `https://<databricks-instance>/oidc/v1/token` |
| Account token endpoint | `https://accounts.cloud.databricks.com/oidc/accounts/<account-id>/v1/token` |
| Grant | `grant_type=client_credentials` |
| Scope | `scope=all-apis` |
| Client auth | HTTP Basic — `--user "$CLIENT_ID:$CLIENT_SECRET"` |
| Response | `access_token`, `token_type: "Bearer"`, `expires_in: 3600` |
| **Access token lifetime** | **1 hour (3600 s)** |
| OAuth secret max lifetime | **730 days** (set at creation; secret shown once) |
| Refresh semantics | None — client_credentials has no refresh token. Re-request. |

Quoted: *"The `all-apis` scope requests an OAuth access token that allows the service principal to call any Databricks REST API it has permission to access."*

**Account-level vs workspace-level tokens:** account-level tokens can call both account-level and workspace-level REST APIs; workspace-level tokens call REST APIs within a single workspace only. **[V-search]** (oauth-m2m page summary)

**Role assumption (Public Preview):** add `assume_group=<group-id>` to a **workspace-level** token request to scope the token to a group. Not supported for account-level tokens. **[V]**

**Secret creation path:** Settings → Identity and access → Service principals → (select SP) → Secrets → Generate secret. Lifetime up to 730 days. Both **account admins and workspace admins** can do this; account admins can alternatively use the account console (User management → SP → Credentials & secrets). **[V-search]** https://docs.databricks.com/aws/en/admin/users-groups/manage-service-principals

> **Gotcha for LakeWright.NET:** the client_credentials access token is 1 hour with no refresh token. Any HTTP client must cache the token and refresh proactively (e.g. at T-5min), not react to a 401. The Databricks SDK for .NET does this for you; a hand-rolled `HttpClient` does not.

### 1.2 U2M — authorization code + PKCE

**[V]** https://docs.databricks.com/aws/en/dev-tools/auth/oauth-u2m

| Item | Value |
|---|---|
| Authorize (workspace) | `https://<databricks-instance>/oidc/v1/authorize` |
| Authorize (account) | `https://accounts.cloud.databricks.com/oidc/accounts/<account-id>/v1/authorize` |
| Token (workspace) | `https://<databricks-instance>/oidc/v1/token` |
| Token (account) | `https://accounts.cloud.databricks.com/oidc/accounts/<account-id>/v1/token` |

**Authorization request params:** `client_id` (`databricks-cli` for built-in tooling; custom OAuth apps use their own), `redirect_uri`, `response_type=code`, `state`, `code_challenge`, `code_challenge_method=S256`, `scope=all-apis+offline_access`. Optional `assume_group=<group-id>`.

**Token exchange params:** `client_id`, `grant_type=authorization_code`, `scope=all-apis offline_access`, `redirect_uri`, `code_verifier` (**43–128 chars**, charset `A–Z a–z 0–9 -._~`), `code`.

**Lifetimes:** *"Each access token is valid for one hour, after which a new token is automatically requested."* Response includes `refresh_token`.

**Security gotcha, quoted:** revoking consent **does not** invalidate existing tokens; applications can continue using refresh tokens *"until those tokens expire."* Refresh-token absolute lifetime: **[UNDOC]**.

**Scopes:** `all-apis` and `offline_access` are documented on this page. `sql`, `openid`, `profile`, `email` are referenced elsewhere in Databricks docs but are **not** specified on this page — treat as **[UNDOC]** for exact semantics.

**Custom OAuth app registration:** the page states you must use a custom app's `client_id` instead of `databricks-cli`, but gives **no registration procedure** and **draws no confidential-vs-public client distinction** on this page. **[UNDOC]** at this level of detail.

### 1.3 Workload identity federation / OIDC — **secretless**

This is the headline answer to "can an Azure Container App or GitHub Actions federate in without a stored client secret?" — **yes**, two independent mechanisms.

#### (a) Databricks-native OIDC token exchange

**[V]** https://docs.databricks.com/aws/en/dev-tools/auth/oauth-federation-exchange

| Item | Value |
|---|---|
| Endpoint (workspace) | `https://<databricks-workspace-host>/oidc/v1/token` |
| Endpoint (account) | `https://<databricks-account-host>/oidc/accounts/<account-id>/v1/token` |
| `grant_type` | `urn:ietf:params:oauth:grant-type:token-exchange` |
| `subject_token_type` | `urn:ietf:params:oauth:token-type:jwt` |
| `subject_token` | the federated JWT from your IdP |
| `scope` | `all-apis` |
| `client_id` | service principal UUID — **required only for service-principal federation policies**, omitted for account-wide policies |
| **Client secret** | **Not needed for either method.** |
| Response | `access_token`, `scope: all-apis`, `token_type: Bearer`, `expires_in` |

**Token lifetime, quoted:** *"The resulting Databricks OAuth token has the same expiration (`exp`) claim as the JWT provided in the `subject_token` parameter."* — i.e. the Databricks token inherits the IdP token's expiry, it is **not** a flat 1 hour.

**Federation policy config** **[V]** https://docs.databricks.com/aws/en/dev-tools/auth/oauth-federation-policy
- Required: **Issuer URL** (`iss` claim), **Subject** (defaults to `sub`), **Audiences** (`aud`; defaults to your Databricks account ID).
- Optional: **Subject Claim** (defaults to `sub`), token signature validation via **JWKS JSON (up to 5 keys)** or a **JWKS URI**.
- **Limit: max 20 service principal federation policies per service principal.**
- **Limit: max 20 account federation policies per Databricks account.**
- Best practice quoted: create a dedicated service principal per distinct external workload identity.

**Documented providers** **[V]** https://docs.databricks.com/aws/en/dev-tools/auth/oauth-federation-provider: GitHub Actions, Azure DevOps Pipelines, GitLab CI/CD, CircleCI, AWS IAM workloads, Jenkins, Terraform Cloud, Atlassian Bitbucket Pipelines. **Kubernetes and Azure managed identity are NOT listed on this overview page.**

**GitHub Actions specifics** **[V]** https://docs.databricks.com/aws/en/dev-tools/auth/provider-github: issuer is `https://token.actions.githubusercontent.com`; you set env vars `DATABRICKS_AUTH_TYPE: github-oidc`, `DATABRICKS_HOST`, `DATABRICKS_CLIENT_ID` and the SDK/CLI performs the exchange internally. **Answer: yes, GitHub Actions federates in with no stored secret.**

#### (b) Azure managed identity → Entra ID token (Azure only)

**[V]** https://learn.microsoft.com/en-us/azure/databricks/dev-tools/auth/azure-mi-auth and https://learn.microsoft.com/en-us/azure/databricks/dev-tools/auth/aad-token-manual

Azure Databricks accepts a **Microsoft Entra ID access token directly as a Databricks bearer token**. No Databricks secret, no federation policy.

- **Azure Databricks resource ID: `2ff814a6-3304-4ab8-85cb-cd0e6f879c1d`** — quoted as *"the standard identifier for Azure Databricks across all Azure environments."*
- MSAL scope string: `2ff814a6-3304-4ab8-85cb-cd0e6f879c1d/.default`
- CLI: `az account get-access-token --resource 2ff814a6-3304-4ab8-85cb-cd0e6f879c1d`
- Use: `Authorization: Bearer <entra-token>` against e.g. `https://<instance>/api/2.0/clusters/list`
- **Databricks treats managed identities as service principals.** You add the MI's **Client ID** as a service principal (choose "Microsoft Entra ID managed") at account level and/or assign it to the workspace.
- Works for **both** workspace-level and account-level APIs (separate config profiles: `azure_use_msi = true` plus `azure_workspace_resource_id` for workspace, `account_id` for account).
- Entra ID access tokens *"expire within one hour"*; elsewhere the same page says *"expire after 60-90 minutes by default."*
- Escape hatch if the SP is not yet in the workspace but holds Azure `Contributor`/`Owner` on the workspace resource: send `X-Databricks-Azure-SP-Management-Token` + `X-Databricks-Azure-Workspace-Resource-Id` headers alongside the bearer token. After first authentication *"the service principal becomes a workspace admin."*
- The page's worked example uses an Azure **VM**, not Container Apps. **[INFER]** Container Apps supports user-assigned managed identity and the IMDS endpoint identically, so the same flow applies — but the docs do not name Container Apps, so this is inference, not a documented claim.

**Note:** the Azure MI page makes **no mention of the federation-policy mechanism** — it is a separate, Azure-specific path. Two valid secretless routes exist on Azure; the Entra route is simpler and is the one `azure_use_msi` uses.

### 1.4 On-behalf-of / acting AS an end user against Unity Catalog

**[V]** https://docs.databricks.com/aws/en/dev-tools/databricks-apps/auth

There **is** an on-behalf-of-user mechanism, but it is **scoped to Databricks Apps** — apps hosted *inside* Databricks. It is **not** a general OAuth token-exchange an external .NET service can invoke.

- Called *"user authorization"* or *"on-behalf-of-user authorization."* **Public Preview.** Workspace admin must enable it.
- Databricks forwards the user's access token to the app in the **`x-forwarded-access-token`** HTTP header. (Other `X-Forwarded-*` headers are not enumerated on this page — **[UNDOC]**.)
- *"Databricks enforces all permissions based on the user's existing Unity Catalog policies"* — **row filters and column masks apply automatically.**
- Requires explicit user consent on first access.
- **Scopes** declared in `user_api_scopes` in `databricks.yml`. Documented examples: `sql`, `dashboards.genie`, `serving.serving-endpoints`, `files`/`file.files`, `genie`, `iam.access-control:read`, `iam.current-user:read` (default). **A complete authoritative scope list is not published on this page — [UNDOC].**
- Databricks **blocks** access outside approved scopes even if the user personally has permission.
- Contrast: **app authorization** gives the app a dedicated service principal via injected `DATABRICKS_CLIENT_ID` / `DATABRICKS_CLIENT_SECRET` env vars; limitation quoted: *"All users who interact with the app share the same permissions."*
- Stated limitation: users **can't revoke** consent once granted. Token lifetime/refresh for the forwarded token: **[UNDOC]**.

> **Integration-boundary implication:** if LakeWright.NET runs **outside** Databricks (Azure Container Apps), there is **no documented OBO/token-exchange flow** to act as an arbitrary end user against Unity Catalog. The realistic options are (a) run U2M authorization-code+PKCE and hold each user's Databricks OAuth token, (b) run as one service principal and enforce tenancy in your own SQL/app layer, or (c) host the user-facing surface as a Databricks App and use `x-forwarded-access-token`. This is the single most load-bearing finding for the integration-boundary decision.

---

## 2. SQL Statement Execution API

**Version: 2.0.** Base path `/api/2.0/sql/statements`. Requires **TLS 1.2 or above**.

**[V]** https://docs.databricks.com/aws/en/dev-tools/sql-execution-tutorial and https://learn.microsoft.com/en-us/azure/databricks/dev-tools/sql-execution-tutorial (Azure page last updated 2026-07-20)

### 2.1 Endpoints

| Operation | Method | Path |
|---|---|---|
| Execute statement | POST | `/api/2.0/sql/statements/` |
| Get status + manifest + first chunk | GET | `/api/2.0/sql/statements/{statement_id}` |
| Get result chunk by index | GET | `/api/2.0/sql/statements/{statement_id}/result/chunks/{chunk_index}` |
| Cancel | POST | `/api/2.0/sql/statements/{statement_id}/cancel` |

### 2.2 Request fields

`warehouse_id` (required), `statement` (required), `catalog`, `schema`, `parameters[]`, `format`, `disposition`, `wait_timeout`, `on_wait_timeout`, `row_limit`, `byte_limit`, `query_tags[]`.

### 2.3 wait_timeout — sync vs async

- Format: string, `"<x>s"`.
- **Range: 5–50 seconds inclusive.**
- **Default: 10 seconds.**
- `"0s"` → returns statement ID and status **immediately** (pure async).
- Default behaviour: **statement keeps running after the timeout**. Set `"on_wait_timeout":"CANCEL"` to cancel instead.
- On timeout the response is just `{"statement_id": "...", "status": {"state": "PENDING"}}`.

### 2.4 Statement states

`PENDING`, `RUNNING`, `SUCCEEDED`, `FAILED`, `CANCELED`, `CLOSED`.

### 2.5 disposition and format

| disposition | Size limit | Formats |
|---|---|---|
| `INLINE` (default) | **25 MiB** — exceeding it returns a failure status and **the statement is canceled** | `JSON_ARRAY` only |
| `EXTERNAL_LINKS` | **100 GiB** **[V-search]**; results beyond are truncated (`truncated: true` in manifest) | `JSON`, `CSV`, `ARROW_STREAM` |

**External link expiry: short-lived, `<= 15 minutes`** **[V-search]**; each `external_link` carries an `expiration` timestamp. **[V]**

Critical security rules, quoted **[V]**:
- *"Because SAS URLs are already generated with embedded temporary SAS tokens, you must not set an `Authorization` header in the download requests."* (Azure wording; AWS equivalent uses presigned S3 URLs.)
- *"The response payload output format and behavior, once they are set for a particular SQL statement ID, cannot be changed."*
- `EXTERNAL_LINKS` *"can be disabled upon request by creating a support case."*
- Fetching a chunk again **returns a new SAS URL**.

### 2.6 Chunk / pagination model

Manifest: `chunks[]`, `total_chunk_count`, `total_row_count`, `total_byte_count`, `truncated`, `format`, `schema.columns[]` (with `name`, `position`, `type_name`, `type_text`, `type_precision`, `type_scale`).

Result: `chunk_index`, `data_array` (INLINE) or `external_links[]`, `row_count`, `row_offset`, `next_chunk_index`, `next_chunk_internal_link`.

`next_chunk_internal_link` looks like `/api/2.0/sql/statements/<id>/result/chunks/1?row_offset=188416`.

> **Major gotcha, quoted:** *"as soon as the last chunk is fetched, the SQL statement is closed. After this closure, you cannot use that statement's ID to get its current status or to fetch any more chunks."* A retry-after-last-chunk in .NET will hard-fail. Chunk fetching is **once-only and destructive**.

### 2.7 Lifecycle / polling limits

- **You must poll at least once every 15 minutes to keep the statement alive.** **[V-search]**
- **Results are only available for one hour after success.** **[V-search]**
- `STATEMENT_TIMEOUT` SQL config: **0 to 172800 seconds (2 days)**, system default **172800 s (2 days)**. **[V-search]** https://docs.databricks.com/aws/en/sql/language-manual/parameters/statement_timeout

### 2.8 Parameterized statements — the SQL-injection defense

This is the precise syntax. Named parameters prefixed with `:` in the statement, matched by `name` in a `parameters` array.

```json
{
  "warehouse_id": "...",
  "catalog": "samples",
  "schema": "tpch",
  "statement": "SELECT l_orderkey, l_extendedprice, l_shipdate FROM lineitem WHERE l_extendedprice > :extended_price AND l_shipdate > :ship_date LIMIT :row_limit",
  "parameters": [
    { "name": "extended_price", "value": "60000", "type": "DECIMAL(18,2)" },
    { "name": "ship_date",      "value": "1995-01-01", "type": "DATE" },
    { "name": "row_limit",      "value": "2", "type": "INT" }
  ]
}
```

- Fields: **`name`** (required, no colon in the array — the colon appears only in the statement text), **`value`** (required), **`type`** (optional).
- **`type` defaults to `STRING` when omitted.**
- Types demonstrated in docs: `DECIMAL(18,2)`, `DATE`, `INT`, `STRING`. The docs describe these as examples; **the exhaustive supported-type list lives only on the JS-rendered API reference — [UNDOC]** from static pages. **[INFER]** it is the Databricks SQL type system, but do not rely on that without checking.
- Note `LIMIT :row_limit` is parameterizable — parameters are not restricted to WHERE-clause values.

Databricks' own words **[V]**: *"Databricks strongly recommends that you use parameters as a best practice for your SQL statements... Parameterized queries help protect against SQL injections attacks by handling input arguments separately from the rest of your SQL code and interpreting these arguments as literal values."*

> **Gotcha:** identifiers (table/column/catalog names) are **not** parameterizable. If LakeWright.NET needs dynamic table names for multi-tenancy, that must be an allow-list validated in .NET, not a parameter.

### 2.9 Other limits

- `row_limit` / `byte_limit` → set `truncated: true` when exceeded.
- `query_tags`: array of `{"key","value"}` for cost attribution; surfaces in `system.query.history`. **Public Preview.**
- **Concurrency limit per warehouse for this API: [UNDOC].** The resource-limits page has **no row for `/sql/statements`** at all (see §4). Related warehouse behaviour **[V-search]** https://docs.databricks.com/aws/en/compute/sql-warehouse/warehouse-behavior: classic/pro warehouses have a fixed limit of **one cluster per 10 concurrent queries**; a warehouse is always upscaled if a query waits **5 minutes** in the queue; downscale after **15 minutes** of low load. Serverless uses Intelligent Workload Management instead.
- SQL warehouses per workspace: **1,000**. **[V]** (resource limits table)

### 2.10 Access control

Caller needs `CAN USE` on the warehouse plus Unity Catalog / table-ACL permissions on the objects. **Only the user who executed a statement can fetch its results.** **[V]**

---

## 3. Jobs API

### 3.1 Current version: 2.2

**[V]** https://docs.databricks.com/aws/en/reference/jobs-api-2-2-updates — paths are `/api/2.2/jobs/...`. 2.0 and 2.1 still exist (2.1 is now labelled "Jobs (legacy) API" in the reference).

Changes in 2.2 vs 2.1:
- **Token-based pagination.** `next_page_token` in responses; `page_token` as a query param, e.g. `/api/2.2/jobs/get?job_id=11223344&page_token=Z29...E=`.
- **Arrays capped at 100 elements per response** — `tasks`, `parameters`, `job_clusters`, `environments`.
- Pagination **newly added** to `jobs/get` and `jobs/getrun` (it already existed on `jobs/list`, `jobs/listruns`).
- **Root-level `has_more` removed** from List jobs/runs — use presence of `next_page_token` instead.
- **Job queueing is enabled by default in 2.2** (in 2.0/2.1 it required `queue: true`). Set `queue: false` to disable.
- `jobs/getrun` gains `only_latest` query param (latest retry/repair attempts only).
- `ForEach` tasks return an `iterations` array of nested task runs; paginated when >100.

### 3.2 Endpoints (2.0 paths shown in the static reference; same shape at 2.2)

| Operation | Method | Path |
|---|---|---|
| Trigger a run of an existing job | POST | `/jobs/run-now` |
| One-time run without creating a job | POST | `/jobs/runs/submit` |
| Get a run | GET | `/jobs/runs/get` |
| Cancel a run | POST | `/jobs/runs/cancel` |

**[V]** https://docs.databricks.com/aws/en/reference/jobs-2.0-api

### 3.3 Run lifecycle states

**`life_cycle_state`** **[V-search]** (jobs-2.0-api / getrun reference): `PENDING`, `RUNNING`, `TERMINATING`, `TERMINATED`, `SKIPPED`, `INTERNAL_ERROR`, `BLOCKED`, `WAITING_FOR_RETRY`, `QUEUED`. Docs warn *"Additional states might be introduced in future releases."*

**`result_state`** **[V-search]**: `SUCCESS`, `FAILED`, `TIMEDOUT`, `CANCELED`, `MAXIMUM_CONCURRENT_RUNS_REACHED`, `UPSTREAM_CANCELED`, `UPSTREAM_FAILED`, `EXCLUDED`, `SUCCESS_WITH_FAILURES`, `DISABLED`.

Availability rules **[V-search]**:
- `TERMINATED` + had a task → result state **guaranteed** available.
- `PENDING` / `RUNNING` / `SKIPPED` → result state **not** available.
- `TERMINATING` / `INTERNAL_ERROR` → available **if** the run had a task and managed to start it.
- Once available, the result state **never changes**.

> **.NET gotcha:** because Databricks explicitly reserves the right to add states, a `switch` over these enums must have a non-throwing default (or map unknown → "unknown/still-running"), the opposite of the usual exhaustiveness rule. Model them as a closed enum + `Unknown` fallback, not a `never` check.

UI-level statuses (for reference, not API values) **[V]**: Queued, Pending, Running, Skipped, Succeeded, Succeeded with failures, Failed, Timed Out, Canceling, Canceled. Individual tasks can also be `Disabled`.

### 3.4 Idempotency

**Verbatim from the docs** **[V]** https://docs.databricks.com/aws/en/reference/jobs-2.0-api:

> *"An optional token to guarantee the idempotency of job run requests. If a run with the provided token already exists, the request does not create a new run but returns the ID of the existing run instead. If a run with the provided token is deleted, an error is returned. If you specify the idempotency token, upon failure you can retry until the request succeeds. Databricks guarantees that exactly one run is launched with that idempotency token. This token must have at most 64 characters."*

- Field name: **`idempotency_token`**. Max **64 characters**.
- **Dedup window: [UNDOC].** The docs state no time period. Widely-repeated community claims of "one hour" are **not** in the primary docs; do not design around a specific window. Note the doc's own caveat that a *deleted* run with that token produces an **error**, not a new run — so tokens are not safely reusable across time.

### 3.5 Job parameters

`run-now` and `runs/submit` accept job/task parameters (`job_parameters`, plus task-type-specific `notebook_params`, `python_params`, `jar_params`, `spark_submit_params`). Exact per-field schema lives on the JS-rendered reference — **[UNDOC]** at field level from static docs.

### 3.6 Polling vs push

**Rate limits directly constrain polling** — see §4. `/jobs/runs/get` is the most generous at **100 req/s per workspace**; `/jobs/runs/list` is **30 req/s**.

**Push alternatives — three exist:**

**(a) HTTP webhooks / notification destinations** **[V]** https://learn.microsoft.com/en-us/azure/databricks/jobs/notifications

Event types:

| `event_type` code | Sent when |
|---|---|
| `jobs.on_start` | a run starts |
| `jobs.on_success` | a run stops successfully or "succeeded with failures" |
| `jobs.on_failure` | a run stops in an unsuccessful state |
| `jobs.on_duration_warning_threshold_exceeded` | a run exceeds the configured duration threshold |

Payload shape (verbatim example):
```json
{
  "event_type": "jobs.on_start",
  "workspace_id": "your_workspace_id",
  "task": { "task_key": "task_name" },
  "run": { "run_id": "run_id_of_task", "parent_run_id": "run_id_of_parent_job_run" },
  "job": { "job_id": "job_id", "name": "job_name" }
}
```
(`task` and `parent_run_id` present only for task-level notifications.)

Constraints:
- **Max 3 system destinations per notification event type**, per job or task.
- HTTPS enforced; destination must use SSL certs signed by a trusted CA. **[V-search]**
- Job-level notifications are **not** sent when failed tasks are retried — use task notifications for that.
- Notification destinations are configured by an **admin** in workspace admin settings, not per-app. There is a Notification Destinations API (`docs.databricks.com/api/workspace/notificationdestinations`).
- Slack/Teams message content is explicitly unstable: *"You should not implement clients or processing that depend on the specific content or formatting of these messages."* **Use a user-defined webhook if you need a stable schema.**
- A 5th UI event, **streaming backlog**, exists but has **no listed `event_type` webhook code** in the table.

**(b) System tables** — `system.lakeflow` schema holds job/task run records account-wide; joinable with billing tables. **[V]** (jobs/monitor page). Queried via SQL, i.e. via the Statement Execution API. Latency: **[UNDOC]**.

**(c) Run history retention:** **60 days** for both jobs and pipelines. Runs list UI start-time filter covers only the **last 48 hours**. **[V]**

> **Gotcha:** *"Runs submitted through the Jobs API `runs/submit` endpoint... are one-time runs that aren't backed by a saved job. Because these runs have no associated job, you can't find them by filtering on a job Name."* Docs explicitly recommend create-then-run-now over `runs/submit` for durable, retryable jobs. **[V]**

### 3.7 Job-related resource limits **[V]**

| Metric | Limit | Scope |
|---|---|---|
| Jobs created per hour | 10,000 | Workspace |
| Tasks running simultaneously | 2,000 | Workspace |
| Parent tasks (Run job / For each) simultaneously | 750 | Workspace |
| Saved jobs | 12,000 | Workspace |

---

## 4. Rate limits and error taxonomy

### 4.1 Documented API rate limits

**[V]** https://learn.microsoft.com/en-us/azure/databricks/resources/limits (page updated 2026-07-28) — full table read. Mirror: https://docs.databricks.com/aws/en/resources/limits

All are **requests per second per workspace** unless noted. "Fixed = No" means an increase can be requested via your account team.

**Jobs API**

| Endpoint | Limit |
|---|---|
| `/jobs/create` | 20/s |
| `/jobs/delete` | 10/s |
| `/jobs/get` | 20/s |
| `/jobs/list` | 20/s |
| `/jobs/reset` | 20/s |
| `/jobs/run-now` | **20/s** |
| `/jobs/update` | 10/s |
| `/jobs/runs/cancel` | 10/s |
| `/jobs/runs/cancel-all` | 5/s |
| `/jobs/runs/delete` | 20/s |
| `/jobs/runs/export` | 20/s |
| `/jobs/runs/get` | **100/s** |
| `/jobs/runs/get-output` | 20/s (shared quota with `/jobs/runs/output`) |
| `/jobs/runs/list` | 30/s |
| `/jobs/runs/output` | 20/s (shared with `get-output`) |
| `/jobs/runs/repair` | 5/s |
| `/jobs/runs/submit` | **35/s** |

**Other endpoints relevant to a SaaS backend**

| API | Limit | Scope |
|---|---|---|
| DBFS `/dbfs` | 30/s | Workspace |
| Permissions API GET | 100/s | Workspace |
| Permissions API PATCH/PUT | 30/s | Workspace |
| Pipelines API GET | 150/s | Workspace |
| Pipelines API POST/PUT/DELETE | 50/s | Workspace |
| **Query History `/sql/history/queries` list** | **10/s** | **Account** |
| Secrets API | 1,100/min | Workspace |
| Token Management API | 40/s | Workspace |
| Workspace `/workspace/list` | 50/s | Workspace |
| Workspace `/workspace/import` | 30/s | Workspace |
| Workspace `/workspace/export` | 60/s | Workspace |
| Workspace `/workspace/delete` | 2/s | Workspace |
| Workspace `/workspace/mkdirs` | 20/s | Workspace |
| Git Credentials `/git-credentials/*` | 10/s combined | Workspace |
| Git folders `/repos/*` | 10/s combined | Workspace |
| Account SCIM GET | 20/s | Account |
| Account SCIM LIST | 240/min | Account |
| Account SCIM PATCH | 2/s | Account |
| Account SCIM POST/PUT/DELETE | 5/s | Account |
| Workspace SCIM GET | 255/min | Workspace |
| Workspace SCIM PATCH | 10/min | Workspace |
| Workspace SCIM POST/PUT/DELETE | 35/min | Workspace |
| OpenSharing providers/recipients/shares | 400/s each | Workspace |
| Data lineage `table-lineage` | 10,000/hr, 50,000/day | Account |
| Data lineage `column-lineage` | 40,000/hr, 300,000/day | Account |
| MLflow tracking (most write endpoints) | 120/s | Workspace |
| MLflow `*/search`, `experiments/list` | 7/s | Workspace |
| Model Registry (most) | 40/s | Workspace |

> **Critical gap:** the table contains **no row for `/api/2.0/sql/statements`**, **no row for Unity Catalog CRUD** (`/unity-catalog/catalogs|schemas|tables`), and **no row for `/serving-endpoints/*/invocations`**. Rate limits for the three APIs LakeWright.NET would lean on hardest are **[UNDOC]**. Design for backoff regardless; do not assume unlimited.

### 4.2 Throttling response and retry guidance

- **HTTP 429 Too Many Requests** — *"Request is rejected due to throttling."* **[V-search]**
- Foundation Model APIs: *"When a rate limit is exceeded, the service returns an HTTP 429 (Too Many Requests) response. Clients should implement retry logic with exponential backoff."* **[V-search]** https://docs.databricks.com/aws/en/machine-learning/foundation-model-apis/limits
- **`Retry-After` header: [UNDOC].** I found **no** Databricks documentation stating that a `Retry-After` header is returned on 429. Do not depend on it; use exponential backoff with jitter and treat `Retry-After` as an opportunistic optimisation if present.
- Rate-limiter caveat, quoted (FMAPI): *"The rate limiter is designed for low latency, which means concurrent requests are not checked ahead of time. The system records usage after a response is sent, so if several requests arrive at the same moment, they can all go through before usage is counted."* — bursts can overshoot the nominal limit.

### 4.3 Error response shape and error_code taxonomy

Error body shape **[V-search]**:
```json
{ "error_code": "Error code", "message": "Human-readable error message." }
```

| HTTP | `error_code` values documented |
|---|---|
| 400 | `BAD_REQUEST`, `INVALID_PARAMETER_VALUE`, `MALFORMED_REQUEST` |
| 401 | (unauthenticated — *"The request does not have valid authentication credentials for the operation."*) |
| 403 | `PERMISSION_DENIED` — *"Caller does not have permission to execute the specified operation."* Also `FEATURE_DISABLED`. |
| 404 | `RESOURCE_DOES_NOT_EXIST` — *"Operation was performed on a resource that does not exist."* |
| 429 | (throttling) |
| 500 | `INTERNAL_ERROR` |
| 503 | (service unavailable) |

Real-world 403 example **[V-search]**: `{"error_code": "PERMISSION_DENIED", "message": "User \"my-spn\" does not have Manage Run or Owner or Admin permissions on job 246372968680205"}`

**Caveat:** this taxonomy is assembled from per-endpoint error tables on the JS-rendered API reference pages plus search excerpts. **Databricks does not publish a single canonical `error_code` enum page.** The list above is the documented subset, not proven exhaustive. There is a separate, unrelated *SQL* error-condition catalogue at https://docs.databricks.com/aws/en/error-messages/error-classes (SQLSTATE-style conditions surfaced inside failed statements) — do not confuse the two.

> **.NET design note:** parse `error_code` as a string, not an enum. Retry on 429, 500, 503 and on transient 5xx; never retry 400/403/404. For `/jobs/run-now`, combine retry with `idempotency_token`.

---

## 5. Unity Catalog

### 5.1 Metadata listing APIs

**[V]** from the credential-vending page and CLI docs, the UC REST surface is under `/api/2.1/unity-catalog/` (volumes credentials at `/api/2.0/`). Catalogs/schemas/tables list endpoints follow `GET /api/2.1/unity-catalog/catalogs`, `/schemas`, `/tables`. **The exact query-parameter sets (`catalog_name`, `schema_name`, `max_results`, `page_token`, `include_browse`, `omit_columns`) live only on the JS-rendered reference pages — [UNDOC]** from static docs; confirm against `docs.databricks.com/api/workspace/catalogs|schemas|tables` in a browser before coding.

UC objects follow the **three-level namespace `catalog.schema.object`**. **[V-search]**

**UC resource limits** **[V]** (resource limits table):

| Object | Limit | Scope |
|---|---|---|
| Catalogs | 1,000 | Metastore |
| Schemas | 10,000 | Catalog |
| Tables | 10,000 | Schema |
| Tables | 1,000,000 | Metastore |
| Columns | 32,768 | Table |
| Volumes | 10,000 / 100,000 | Schema / Metastore |
| Functions | 10,000 | Schema |
| Storage + service credentials (combined) | 1,000 | Metastore |
| External locations | 10,000 | Metastore |
| Privileges | 4,000 on parent objects; 1,000 on non-parent objects | — |
| **ABAC policies** | 100/catalog, 100/schema, **50/table**, 10,000/metastore | — |
| **Principals per policy** | **20** (applies to both `TO` and `EXCEPT` clauses) | Policy |

### 5.2 Row filters and column masks

**[V]** https://docs.databricks.com/aws/en/data-governance/unity-catalog/filters-and-masks/

SQL definition:
```sql
-- Row filter
CREATE FUNCTION filter_name(params) RETURNS BOOLEAN RETURN condition;
ALTER TABLE table_name SET ROW FILTER filter_name ON (column_name);

-- Column mask
CREATE FUNCTION mask_name(column_value) RETURNS data_type RETURN masked_value;
ALTER TABLE table_name ALTER COLUMN column_name SET MASK mask_name;
```

**Can they read session/user context? Yes.** **[V-search]** https://docs.databricks.com/aws/en/data-governance/unity-catalog/abac/abac-vs-rls-cm and .../abac/performance:
- *"you can decide where to implement principal-based logic: in the policy's TO/EXCEPT clauses, or inside the UDF using identity functions like `current_user()` and `is_account_group_member()`."*
- Performance: *"Identity functions are resolved once during query analysis, not per row. Multiple calls to identity functions like `is_account_group_member()` with different group arguments result in a single UC API call, so the performance impact is typically minimal."*
- Dynamic views are the older alternative, *"gated by group-membership functions like `is_account_group_member()`."*
- Note: the filters-and-masks landing page itself does **not** name these functions for table-level filters — the ABAC pages do. Treat identity functions as supported in the UDF body.

**Databricks now recommends ABAC policies over table-specific UDFs** — *"apply filters and masks centrally using governed tags and reusable policies. ABAC scales across catalogs and schemas and can be defined by higher-level admins, so table owners can't override or remove them."* **[V-search]**

Limitations **[V]**: DBR <12.2 LTS unsupported; dedicated access mode not supported on DBR ≤15.3; DBR 15.4+ for reads, 16.3+ for writes; cannot apply to views; **incompatible with the Iceberg REST catalog and Unity REST APIs**; Delta Lake APIs unsupported; `MERGE` fails with nested/aggregated/windowed/limited policies; no time travel, cloning, or AI Search indexing; a policy cannot reference tables that themselves have active policies.

> **This is the key tenancy lever.** Because the Statement Execution API runs statements as the authenticated principal and *"only the user who executes a statement can make fetch requests for the statement's results"*, row filters keyed on `current_user()` / `is_account_group_member()` give real per-tenant isolation **only if LakeWright.NET calls with the end user's identity** — i.e. U2M tokens or a Databricks App. With a single shared service principal, every tenant is the same `current_user()` and UC row filters do nothing for you.

### 5.3 Credential vending / temporary credentials — yes, it exists

**[V]** https://docs.databricks.com/aws/en/external-access/credential-vending

| Endpoint | Method | Request fields |
|---|---|---|
| `/api/2.1/unity-catalog/temporary-table-credentials` | POST | `table_id` (required), `operation`: `READ` \| `READ_WRITE` |
| `/api/2.1/unity-catalog/temporary-path-credentials` | POST | `url` (required), `operation`: `PATH_READ` \| `PATH_READ_WRITE` \| `PATH_CREATE_TABLE` |
| `/api/2.0/unity-catalog/temporary-volume-credentials` | POST | `volume_id` (required), `operation`: `READ_VOLUME` \| `WRITE_VOLUME` |

**Purpose:** hand short-lived, downscoped cloud-storage credentials to an **external engine** (Spark, Flink, DuckDB, Trino/Starburst, Iceberg REST clients) so it can read/write table data directly in object storage, bypassing Databricks compute. Credentials *"inherit the privileges of the Databricks principal used to configure the integration."*

**Requirements:** External data access enabled on the metastore; **`EXTERNAL USE SCHEMA`** granted on the schema; `EXTERNAL USE LOCATION` on the external location for path-based external-table access; plus `SELECT` / `MODIFY` / `CREATE` as appropriate. **[V]** https://learn.microsoft.com/en-us/azure/databricks/external-access/unity-rest

**Eligibility:** only tables marked `HAS_DIRECT_EXTERNAL_ENGINE_READ_SUPPORT` / `HAS_DIRECT_EXTERNAL_ENGINE_WRITE_SUPPORT`. **[V-search]**

**Not supported:** **tables with row filters or column masks**, tables shared via Delta Sharing/OpenSharing, Lakehouse-federated (foreign) tables, views, materialized views, streaming tables, online tables, AI Search indexes. **[V]**

**Credential TTL: [UNDOC]** — the page does not state an expiration time.

**Auth for the Unity REST API path:** PAT or **OAuth M2M** (M2M *"Supports automatic credential and token refresh for long-running Spark jobs (>1 hour)"*). **[V]**

> **Read this as a red flag for LakeWright.NET:** credential vending and row-filters/column-masks are **mutually exclusive**. If the accelerator's tenancy story is UC row filters, credential vending is off the table for those tables, and vice versa. Pick one.

---

## 6. Model Serving

### 6.1 Invocation

**[V]** https://docs.databricks.com/aws/en/machine-learning/model-serving/score-custom-model-endpoints

- **Endpoint: `POST /serving-endpoints/{name}/invocations`**
- Auth: `Authorization: Bearer <token>` (curl examples use `-u token:$DATABRICKS_API_TOKEN`).
- Custom-model request bodies: `dataframe_split` (recommended), `dataframe_records`, `instances` (row-format tensors), `inputs` (columnar tensors). Protobuf/KServe v2 `ModelInferRequest` with `Content-Type: application/x-protobuf` is in preview.
- Custom-model responses wrap as `{"predictions": [...]}`.

### 6.2 Foundation-model / LLM shapes and streaming

**[V]** https://learn.microsoft.com/en-us/azure/databricks/machine-learning/foundation-model-apis/api-reference (updated 2026-07-28)

Same `/serving-endpoints/{name}/invocations` path. Three task shapes — **Chat Completions**, **Embeddings**, **Completions** — plus a newer **Responses API** (uses `input` instead of `messages`; OpenAI models, with a separate "Open Responses API" path for Claude/Gemini/Databricks-hosted models). *"The Foundation Model APIs are designed to be similar to OpenAI's REST API."*

**Streaming: yes, SSE.**
- `stream` parameter, **default `true`** on both Chat and Completions requests (note: **not** `false` — this differs from OpenAI and will surprise a .NET client that assumes non-streaming by default).
- Quoted: *"If this parameter is included in the request, responses are sent using the Server-sent events standard."*
- *"For streaming requests, the response is a `text/event-stream` where each event is a completion chunk object."*
- `object` is `"chat.completions"` non-streaming vs `"chat.completion.chunk"` streaming.
- `ChatCompletionChunk` has `index`, `delta` (ChatMessage), `finish_reason` — *"Only the first chunk is guaranteed to have `role` populated"*, *"Only the last chunk will have this populated"* (finish_reason).
- `usage` *"Might not be present on streaming responses."* Use `stream_options.include_usage: true` (Responses API) to force it.
- The literal `data: [DONE]` sentinel is **[UNDOC]** on this page.

Other notable request fields: `max_tokens`, `temperature` [0,2], `top_p` (0,1], `top_k`, `stop`, `n` (**provisioned throughput only**), `tools` (**max 32 functions**; `function` is the only supported tool type in Chat), `tool_choice` (`auto`/`required`/`none`/object), `response_format` (`text` / `json_object` / `json_schema` for structured outputs), `logprobs`, `top_logprobs` (0–20), `reasoning_effort` (`minimal`/`low`/`medium`/`high`, model-dependent), `service_tier` (`"priority"` / `"default"`; anything else errors).

`FunctionObject.parameters`: *"The number of `properties` is limited to 15 keys."*

Usage sub-message fields: `completion_tokens`, `prompt_tokens`, `total_tokens`, `reasoning_tokens`.

Responses API **unsupported params** (return 400): `background`, `store`, `conversation`.

### 6.3 Limits — hard numbers

**[V]** https://docs.databricks.com/aws/en/machine-learning/model-serving/model-serving-limits, cross-checked against the resource-limits table.

| Limit | Value |
|---|---|
| **Payload size per request** | **16 MB** |
| Payload size, **agent endpoints** | **4 MB** |
| **Model execution duration per request** | **597 seconds** |
| Endpoints per workspace | 1,000 (increase on request) |
| QPS per endpoint | 300,000 with route optimization; **200 non-route-optimized** |
| QPS per workspace | 300,000 with route optimization |
| Concurrency per model | 1,024 (custom option + route optimization) |
| Concurrency per workspace | 4,096 |
| Provisioned concurrency | 200 per endpoint and per model |
| Throughput target (not guaranteed) | 200 QPS per endpoint |
| Overhead latency | <20 ms with route optimization |
| Create/update operations | 50 in 5 minutes per workspace |
| CPU memory | 4 GB (CPU) / 8 GB (CPU_MEDIUM) / 16 GB (CPU_LARGE) |
| Env vars per served model | 50 (increase on request) |
| Request/response logging cutoff | anything over 1 MB not logged |

**Client-side HTTP timeout: [UNDOC]** — only the 597 s *model execution* limit is published, not a gateway/idle timeout. **[INFER]** set the .NET `HttpClient.Timeout` above 597 s for long inference, or use streaming to keep the socket active.

### 6.4 AI Gateway (branded "Unity AI Gateway")

**[V-search]** https://docs.databricks.com/aws/en/ai-gateway and .../ai-gateway/inference-tables

It exists and covers exactly the four things asked about: *"usage tracking, payload logging, rate limits, and guardrails on a model serving endpoint."*

- **Payload logging → inference tables:** requests/responses logged to **Unity Catalog Delta tables**.
- **Size caps:** requests/responses **larger than 10 MiB aren't logged**; `logging_error_codes` gets `MAX_REQUEST_SIZE_EXCEEDED` / `MAX_RESPONSE_SIZE_EXCEEDED`. **For CPU model serving endpoints the cap is 1 MiB (1,048,576 bytes)** and oversized payloads are logged as `null`.
- **Sampling:** CPU endpoints support a `sampling_fraction` between 0 and 1 (0%–100%); **default 100%**.
- **Delivery:** *"Logs are typically available within minutes of a request, but delivery isn't guaranteed."* — **not** an audit-grade channel.
- **Rate limiting:** configurable per endpoint; ITPM (input tokens/min), OTPM (output tokens/min), QPH (queries/hour). *"The most restrictive rate limit (ITPM, OTPM, QPH) applies at any given time."* Exceeding → **429** until the window resets. **[V-search]** https://docs.databricks.com/aws/en/ai-gateway/rate-limits

---

## 7. Free Edition — the OSS-contributor verdict

**[V]** https://learn.microsoft.com/en-us/azure/databricks/getting-started/free-edition-limitations (page updated **2026-07-20**) — full page read verbatim. Mirrors: docs.databricks.com/aws/en/... and /gcp/en/...

**What it is:** *"a no-cost version of Databricks designed for students, educators, hobbyists, and anyone interested in learning or experimenting with data and AI"* **[V]** https://docs.databricks.com/aws/en/getting-started/free-edition. **Serverless-only, quota-limited.** Distinct from the 14-day free trial (the trial gives the full platform).

### 7.1 Compute limits (verbatim table)

| Resource | Limit |
|---|---|
| Serverless compute for notebooks | Limited compute size and usage |
| **SQL warehouses** | **One SQL warehouse, limited to `2X-Small` cluster size** |
| **Jobs** | **Max of 5 concurrent job tasks per account** |
| Lakeflow pipelines | One active pipeline per pipeline type |
| Model serving endpoints | Limits on number of active endpoints; **no GPU serving**; **no provisioned throughput**; no custom models on GPU or batch inference; certain models not available |
| AI Search endpoints | One endpoint, one search unit; no Direct Vector Access |
| Databricks Apps | **Up to 3 per account**; auto-stopped after **24 hours** of running |
| Lakebase | One project per account, scale-to-zero compute |

Also: *"Free Edition users only have access to serverless compute resources. Custom compute configurations are not supported. Additionally, **outbound internet access is restricted to a limited set of trusted domains**."*

### 7.2 Administrative limitations (verbatim)

- **One workspace and one metastore per account.**
- **"No access to the account console or account-level APIs."**
- No compliance enforcement, security customization, or private networking.
- **"Authentication is limited to email OTP, Sign in with Google, and Sign in with Microsoft. No SSO or SCIM support."**

### 7.3 Unsupported features

R and Scala; custom workspace storage locations; online tables; clean rooms; all legacy Databricks features; Knowledge Assistant. Serverless notebooks also don't support JAR libraries, and **serverless compute has a max runtime of 7 days** (runs exceeding it are terminated and not retried). **[V-search]** https://docs.databricks.com/aws/en/compute/serverless/limitations

### 7.4 Quotas and enforcement

- Fair usage policy. *"If you exceed your quota, your workspace's compute resources will be shut down and unavailable for the rest of the day (and in extreme cases, the rest of the month). While your compute will be unavailable, your data and settings will not be deleted."*
- **Exact quota numbers for compute/jobs/model serving beyond the table above: [UNDOC].** The page defers to a fair-usage policy without publishing thresholds.
- **LinkedIn verification** unlocks limited serverless GPU compute and outbound internet access. *"LinkedIn verification does not remove all Free Edition limitations."*

### 7.5 Legal

- **"Free Edition accounts may not be used for commercial purposes."**
- Not covered by the Databricks support policy or SLA.
- Cannot become Marketplace providers.
- Databricks may delete accounts inactive for a prolonged period.

### 7.6 Verdict for LakeWright.NET

**Workable for an OSS sample, with three real caveats.**

Works:
- **Unity Catalog: yes** — one metastore, three-level namespace, row filters/column masks all present.
- **Serverless SQL warehouse: yes** — one 2X-Small. Sufficient for the Statement Execution API sample.
- **Jobs: yes** — capped at 5 concurrent tasks per account.
- **Model serving: yes** — pay-per-token foundation models, no GPU/provisioned throughput.
- **Workspace-level REST API: yes by implication.** The docs restrict only *"the account console or account-level APIs"*, which means workspace-level APIs are in scope. **[INFER]** — the page never affirmatively states "workspace REST API is supported"; it's an argument from the negative. Verify empirically before promising it in a README.

Caveats:
1. **Account-level OAuth is out.** The account token endpoint `accounts.cloud.databricks.com/oidc/accounts/<id>/v1/token` is an account-level API. **Only the workspace-level flow (`https://<instance>/oidc/v1/token`) can work.** **[INFER]** from "no account-level APIs" — not stated explicitly.
2. **Service principals + OAuth secrets: [UNDOC] for Free Edition.** The Free Edition page never mentions service principals. Workspace admins *can* generate SP OAuth secrets in workspace settings on the general platform, so it plausibly works — but **this is unverified and it is the single highest-risk assumption for the "OSS contributor runs the sample" story.** Test it on a real Free Edition account before the README depends on it. If it fails, contributors are limited to their own user identity (PAT or U2M), which changes the sample's auth story.
3. **"May not be used for commercial purposes."** Fine for contributors learning the accelerator; **not** fine as the runtime for anything an adopter ships. The README must say this plainly.
4. **Restricted outbound internet** from Free Edition compute may break samples that call external services from inside a notebook/job (unless LinkedIn-verified).

---

## Consolidated gotchas for the .NET integration boundary

1. **No general OBO flow.** On-behalf-of-user exists only for Databricks Apps via `x-forwarded-access-token`. An external .NET service acting as an end user against UC must use U2M tokens or move the surface into a Databricks App. (§1.4)
2. **UC row filters need per-user identity.** A shared service principal collapses `current_user()` to one value, making UC-level tenancy a no-op. (§5.2)
3. **Row filters/column masks and credential vending are mutually exclusive.** (§5.3)
4. **Statement chunk fetching is destructive** — the statement closes when the last chunk is read; no retry. Poll every ≤15 min to keep alive; results expire 1 h after success. (§2.6, §2.7)
5. **INLINE hard-fails at 25 MiB and cancels the statement** — it does not truncate. Choose `EXTERNAL_LINKS` + `ARROW_STREAM` for anything non-trivial, and **strip the Authorization header** on the SAS/presigned download. (§2.5)
6. **`wait_timeout` maxes at 50 s** — any query slower than that forces an async poll loop. (§2.3)
7. **`stream` defaults to `true`** on Foundation Model chat/completions — opposite of the OpenAI default. (§6.2)
8. **Job run states are explicitly open-ended** — do not write an exhaustive switch. (§3.3)
9. **`idempotency_token` has no documented dedup window** and errors if the matching run was deleted. (§3.4)
10. **No documented rate limits** for the Statement Execution API, Unity Catalog CRUD, or serving invocations — the three hottest paths. Assume backoff is mandatory. (§4.1)
11. **No documented `Retry-After` header.** Exponential backoff with jitter, not header-driven retry. (§4.2)
12. **Secretless auth is available and should be the default posture** — Entra managed identity on Azure (resource ID `2ff814a6-3304-4ab8-85cb-cd0e6f879c1d`), OIDC token exchange for GitHub Actions/CI. Stored client secrets (730-day max) should be the fallback, not the design. (§1.3)

---

## Source URLs

**OAuth**
- https://docs.databricks.com/aws/en/dev-tools/auth/oauth-m2m
- https://docs.databricks.com/aws/en/dev-tools/auth/oauth-u2m
- https://docs.databricks.com/aws/en/dev-tools/auth/oauth-federation
- https://docs.databricks.com/aws/en/dev-tools/auth/oauth-federation-policy
- https://docs.databricks.com/aws/en/dev-tools/auth/oauth-federation-provider
- https://docs.databricks.com/aws/en/dev-tools/auth/oauth-federation-exchange
- https://docs.databricks.com/aws/en/dev-tools/auth/provider-github
- https://learn.microsoft.com/en-us/azure/databricks/dev-tools/auth/azure-mi-auth
- https://learn.microsoft.com/en-us/azure/databricks/dev-tools/auth/aad-token-manual
- https://docs.databricks.com/aws/en/dev-tools/databricks-apps/auth
- https://docs.databricks.com/aws/en/admin/users-groups/manage-service-principals

**SQL Statement Execution**
- https://docs.databricks.com/aws/en/dev-tools/sql-execution-tutorial
- https://learn.microsoft.com/en-us/azure/databricks/dev-tools/sql-execution-tutorial
- https://docs.databricks.com/api/workspace/statementexecution (JS-rendered)
- https://docs.databricks.com/aws/en/sql/language-manual/parameters/statement_timeout
- https://docs.databricks.com/aws/en/compute/sql-warehouse/warehouse-behavior

**Jobs**
- https://docs.databricks.com/aws/en/reference/jobs-api-2-2-updates
- https://docs.databricks.com/aws/en/reference/jobs-2.0-api
- https://learn.microsoft.com/en-us/azure/databricks/jobs/monitor
- https://learn.microsoft.com/en-us/azure/databricks/jobs/notifications
- https://docs.databricks.com/aws/en/admin/system-tables/jobs

**Limits & errors**
- https://learn.microsoft.com/en-us/azure/databricks/resources/limits
- https://docs.databricks.com/aws/en/resources/limits
- https://docs.databricks.com/aws/en/machine-learning/foundation-model-apis/limits
- https://docs.databricks.com/aws/en/error-messages/error-classes (SQL conditions, not REST error_codes)

**Unity Catalog**
- https://docs.databricks.com/aws/en/data-governance/unity-catalog/filters-and-masks/
- https://docs.databricks.com/aws/en/data-governance/unity-catalog/abac/abac-vs-rls-cm
- https://docs.databricks.com/aws/en/data-governance/unity-catalog/abac/performance
- https://docs.databricks.com/aws/en/external-access/credential-vending
- https://learn.microsoft.com/en-us/azure/databricks/external-access/unity-rest

**Model Serving**
- https://docs.databricks.com/aws/en/machine-learning/model-serving/model-serving-limits
- https://docs.databricks.com/aws/en/machine-learning/model-serving/score-custom-model-endpoints
- https://learn.microsoft.com/en-us/azure/databricks/machine-learning/foundation-model-apis/api-reference
- https://docs.databricks.com/aws/en/ai-gateway
- https://docs.databricks.com/aws/en/ai-gateway/inference-tables
- https://docs.databricks.com/aws/en/ai-gateway/rate-limits

**Free Edition**
- https://learn.microsoft.com/en-us/azure/databricks/getting-started/free-edition-limitations
- https://docs.databricks.com/aws/en/getting-started/free-edition-limitations
- https://docs.databricks.com/aws/en/getting-started/free-edition
- https://docs.databricks.com/aws/en/compute/serverless/limitations
