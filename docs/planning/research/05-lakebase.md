# 05 — Lakebase vs plain managed Postgres for Lakewright.NET transactional state

**Research date: 2026-07-31.** All "observed" dates below are 2026-07-31 unless stated otherwise.
Status labels (GA / Public Preview / Beta) are copied from the cited page as it read on that date.

Legend: **[V]** = verified against a primary source this session. **[D]** = derived arithmetic from
two verified primary figures (arithmetic shown). **[S]** = secondary source only, treat as unconfirmed.
**[?]** = could not determine.

---

## 1. What Lakebase is, as of 2026-07-31

### 1.1 Origin

**[V]** Databricks announced an agreement to acquire Neon (serverless Postgres, founded 2021 by
Nikita Shamgunov, Heikki Linnakangas, Stas Kelvich) on 2025-05-14 for approximately $1B.
- https://www.prnewswire.com/news-releases/databricks-agrees-to-acquire-neon-to-deliver-serverless-postgres-for-developers--ai-agents-302454992.html
- https://techcrunch.com/2025/05/14/databricks-to-buy-open-source-database-startup-neon-for-1b/

The Neon lineage is directly visible in the product: branching, point-in-time restore, scale-to-zero,
compute/storage separation, `ep-...` endpoint hostnames, and PgBouncer-based pooled endpoints are all
Neon concepts carried forward.

### 1.2 Two generations — this matters

There are **two** Lakebase products in the docs, and they have different status, different billing,
and different capacity units:

| | Lakebase **Provisioned** (legacy) | Lakebase **Autoscaling** (current) |
|---|---|---|
| Docs root | `/oltp/instances/` | `/oltp/projects/` |
| Unit of org | database *instance* | *project* → branches → computes |
| RAM per CU | ~16 GB | 2 GB |
| Status on Azure | **Public Preview** | **GA** |
| API | Database instance API | Postgres API (`/api/2.0/postgres/...`) |

**[V]** Azure "Lakebase Provisioned" page carries an explicit Public Preview banner listing regions
`westus, westus2, eastus, eastus2, centralus, southcentralus, northeurope, westeurope, australiaeast,
brazilsouth, canadacentral, centralindia, southeastasia, uksouth`.
https://learn.microsoft.com/azure/databricks/oltp/instances/

**[V]** Existing Provisioned instances are being migrated: *"If you have existing Lakebase Provisioned
instances, they are being upgraded to Lakebase Autoscaling."* Also: *"All new instances, including those
created using the Database instance API, run on the Autoscaling platform and use Lakebase Autoscaling
pricing."*
https://learn.microsoft.com/azure/databricks/oltp/projects/ and
https://learn.microsoft.com/azure/databricks/oltp/upgrade-to-autoscaling

**Everything below refers to Lakebase Autoscaling unless stated otherwise.**

### 1.3 Lifecycle status — verified, not assumed

| Cloud | Status | Date | Source |
|---|---|---|---|
| AWS | **GA** | announced ~2026-02-09 | https://www.databricks.com/blog/databricks-lakebase-generally-available ; https://community.databricks.com/t5/lakebase-articles/databricks-lakebase-is-now-generally-available/td-p/147678 |
| Azure | **Beta at AWS GA**, then **GA** | GA announced 2026-03-03 | https://www.databricks.com/blog/azure-databricks-lakebase-generally-available |
| GCP | planned "later in 2026" as of Feb 2026; `/gcp/en/oltp/projects/` docs exist | — | https://www.databricks.com/blog/databricks-lakebase-generally-available |

**[V]** The Feb 2026 AWS GA announcement explicitly said *"Generally Available on AWS and in beta on
Azure"*. The Azure GA blog is dated **2026-03-03**. So Azure Lakebase Autoscaling has been GA for
roughly five months as of today. **This is a young GA.** Weigh accordingly.

**[V]** The Azure `oltp/projects/` docs index carries no preview banner, and marks only
**Lakebase Change Data Feed** as *(Public Preview)*.
https://learn.microsoft.com/azure/databricks/oltp/projects/

### 1.4 Regions

**[V] Azure (Autoscaling):** `eastus, eastus2, centralus, southcentralus, westus, westus2,
canadacentral, brazilsouth, northeurope, uksouth, westeurope, australiaeast, centralindia,
southeastasia`. *"Your Lakebase project is created in your Databricks workspace region"* — the region is
**not selectable** independently of the workspace.
https://learn.microsoft.com/azure/databricks/oltp/projects/manage-projects (Region availability)

**[V] AWS (Autoscaling):** 12 regions — `us-east-1, us-east-2, us-west-2, ca-central-1, sa-east-1,
eu-central-1, eu-west-1, eu-west-2, ap-south-1, ap-southeast-1, ap-southeast-2, ap-northeast-1`.
https://docs.databricks.com/aws/en/oltp/projects/limitations

### 1.5 Postgres version and extensions

**[V]** *"Lakebase Postgres Autoscaling supports Postgres 16, Postgres 17, and Postgres 18. Postgres 17
is the default version."*
https://learn.microsoft.com/azure/databricks/oltp/projects/manage-projects

**[V]** Gotcha: projects created through the **legacy** Database instance API (or the Lakehouse/
Provisioned UI) get **PostgreSQL 16**; the Autoscaling UI / Postgres API default is **17**.
https://learn.microsoft.com/azure/databricks/oltp/upgrade-to-autoscaling

**[V] Extensions** are installed with standard `CREATE EXTENSION <name>;` (`CASCADE` and
`IF NOT EXISTS` supported) from a documented allowlist of 60+, including:
`postgis` (+raster/sfcgal/topology/tiger_geocoder), `vector` (pgvector), `hstore`, `citext`, `ltree`,
`cube`, `seg`, `isn`, `uuid-ossp`, `pg_trgm`, `unaccent`, `fuzzystrmatch`, `pg_jsonschema`,
`btree_gin`, `btree_gist`, `bloom`, `pg_stat_statements`, `pg_prewarm`, `pgstattuple`, `pgrowlocks`,
`plpgsql`, `pgcrypto`, `tablefunc`, `intarray`, `xml2`, `hll`, `pg_graphql`, `pg_hint_plan`,
`databricks_auth`, plus Lakebase-only `lakebase_text` / `lakebase_vector` (require Lakebase Search).
https://docs.databricks.com/aws/en/oltp/projects/extensions

**[V]** The `databricks_auth` extension is the mechanism for creating OAuth-backed Postgres roles
(`databricks_create_role(...)`). See §3.

### 1.6 Connection model and the pooler

**[V] Standard Postgres wire protocol on port 5432.** *"Connect with `psql` or any Postgres driver"*;
*"Lakebase supports direct external connections via the standard PostgreSQL wire protocol."*
Documented clients: psql, pgAdmin, DBeaver, PgHero. **JDBC and ODBC are documented elsewhere;
.NET/Npgsql is not named in the client docs** (see §3).
https://docs.databricks.com/aws/en/oltp/projects/postgres-clients

**[V] Connection string format:**
```
postgresql://<role>:<password-or-token>@<endpoint-uid>.databricks.com/databricks_postgres?sslmode=require
```
- Hostname uses the compute **UID** (`ep-sweet-butterfly-y2nm75e1`), not the compute name.
- On Azure the host form is `<endpoint-id>.database.<region>.cloud.databricks.com`.
- Default database: `databricks_postgres`. Port 5432.
- `PGHOST/PGDATABASE/PGUSER/PGPASSWORD/PGPORT` env vars or `DATABASE_URL` also accepted.
- **`sslmode=require` is mandatory** — *"Lakebase Autoscaling requires that all connections use SSL/TLS
  encryption."*

https://docs.databricks.com/aws/en/oltp/projects/connection-strings

**[V] Pooler is PgBouncer in transaction mode.** Pooled hostnames contain `-pooler` (read-write) or
`-ro-pooler` (read-only). Direct (unpooled) connections bypass it.
https://docs.databricks.com/aws/en/oltp/projects/connection-pooling

Documented restrictions **in pooled/transaction mode** — these are the ones that bite an ORM:
- SQL-level `PREPARE` statements (**driver-level prepared statements do work**)
- session-level `SET`
- session-held temporary tables
- `WITH HOLD` cursors
- advisory locks
- `LISTEN`/`NOTIFY`
- **`pg_dump` and schema migrations**  ← run EF Core migrations on the *direct* endpoint

**[V] Connection limits:**
- PgBouncer client connections: up to **10,000** simultaneous.
- Pool size ≈ 90% of server `max_connections` per user/database pair.
- Direct Postgres connections scale with compute: 8 CU → 1,678; 16 CU → 3,357.
- With autoscaling enabled, the max connections figure is *"the smaller of your maximum CU and 8× your
  minimum CU"* — e.g. a 2–8 CU range gives 1,795.
- **All** connections: 24-hour idle timeout, 3-day maximum connection lifetime.

https://docs.databricks.com/aws/en/oltp/projects/connection-pooling ;
https://docs.databricks.com/aws/en/oltp/projects/authentication

### 1.7 Branching, restore, HA

**[V]** Project → branches (root branch `production`, undeletable) → computes / roles / databases.
Child branches inherit all data at creation time and then diverge (copy-on-write clone).
History window configurable **2–30 days, default 7**; point-in-time restore and point-in-time
*branching* (query historical state) both build on it. Read replicas up to 6/branch. HA via automatic
failover, configurable.
https://learn.microsoft.com/azure/databricks/oltp/projects/manage-projects

**[V] Project limits** (per docs, 2026-07-31):

| Resource | Limit |
|---|---|
| Concurrently active computes | 20 (default branch exempt) |
| Read replicas per branch | 6 |
| Branches per project | 500 |
| Postgres roles per branch | 500 |
| Postgres databases per branch | 500 |
| Storage quota per branch | 16 TB |
| Projects per workspace | 1000 |
| Protected branches | 1 |
| Root branches | 3 |
| Manual snapshots | 10 |
| History retention | 30 days max |
| Scale-to-zero timeout | 60 s min, 7 days max |

Storage quota counts only live tables+indexes; PITR history does not count against it.

### 1.8 Compute sizing and scale-to-zero

**[V]** 1 CU = **2 GB RAM** plus proportional CPU and local SSD. Autoscaling 0.5–64 CU; fixed sizes
65–112 CU. Autoscaling operates within a user-set min/max range and never drops below min while active.
https://learn.microsoft.com/azure/databricks/oltp/projects/manage-computes ;
https://learn.microsoft.com/azure/databricks/oltp/projects/autoscaling

**[V] Two different "defaults" appear in the docs — note the discrepancy:**
- The auto-created `production` compute on a new project: **8–16 CU**, HA disabled, autoscaling on,
  scale-to-zero on (24 h).
- The **Compute defaults** setting applied to computes *you* create: **2 ↔ 4 CU**, scale-to-zero on (24 h).

Both from https://learn.microsoft.com/azure/databricks/oltp/projects/manage-projects. Cost modelling
below covers both.

**[V] Scale-to-zero:** default inactivity timeout **24 hours** (configurable 60 s – 7 days). Resume
*"within a few hundred milliseconds"*. While suspended, *"the compute consumes no resources and incurs
no compute costs."* On resume the session context resets — in-memory stats, caches, temp tables,
prepared statements and pools are gone. Storage billing while suspended is **not** explicitly addressed
on that page **[?]**, but storage is a separate DSU meter (§2) and there is no documented suspension of it,
so assume storage bills continuously.
https://docs.databricks.com/aws/en/oltp/projects/scale-to-zero

---

## 2. Pricing

The public pricing page (https://www.databricks.com/product/pricing/lakebase) renders its rate table
client-side and returned only `Loading...` to a fetch on 2026-07-31 — **no figure could be read from it
directly**. `https://docs.databricks.com/aws/en/oltp/projects/pricing` 301-redirects to that same page.

Instead the model below is assembled from two primary sources that *are* machine-readable:

### 2.1 Billing model — [V]

**[V]** Lakebase Autoscaling bills on **two independent meters**:

| Meter | SKU | Multiplier |
|---|---|---|
| Lakebase Autoscaling Compute | Database Serverless (DBU) | **0.213X** |
| Database Storage | Databricks Storage (DSU) | **15X** |
| Point in time restore (PITR) | Databricks Storage (DSU) | **8.7X** |
| Snapshots | Databricks Storage (DSU) | **3.91X** |

Source: https://learn.microsoft.com/azure/databricks/resources/pricing ("Serverless DBU consumption by SKU")

**[V]** Same page defines the DSU base: *"Databricks Storage - Per GB of stored data | 1X"* — so 1 DSU
= 1 GB stored (per month).

### 2.2 Rates — [V] from the Azure Retail Prices API (queried 2026-07-31, `armRegionName eq 'eastus'`)

```
Premium Database Serverless Compute DBU   $0.26   USD / 1 Hour
Premium Databricks Storage Unit DSU       $0.026  USD / 1
Premium Serverless SQL DBU                $0.70   USD / 1 Hour   (context)
Premium Interactive Serverless DBU        $0.95   USD / 1 Hour   (context — Databricks Apps SKU)
```
Endpoint: `https://prices.azure.com/api/retail/prices?$filter=serviceName eq 'Azure Databricks' and armRegionName eq 'eastus'`

### 2.3 Effective Lakebase rates — [D]

| Item | Arithmetic | Effective rate (Azure Premium, East US) |
|---|---|---|
| Compute | 0.213 × $0.26 | **$0.0554 per CU-hour** |
| Database storage | 15 × $0.026 | **$0.39 per GB-month** |
| PITR storage | 8.7 × $0.026 | **$0.226 per GB-month** |
| Snapshot storage | 3.91 × $0.026 | **$0.102 per GB-month** |

**[S] Cross-check:** secondary sources quote **$0.111/CU-hour** and **$0.35/GB-month** for the
*Lakebase Autoscaling Enterprise Tier on AWS US East*, with the caveat that these were "indicative,
final pricing to be released prior to the billing start date" and that "billing for Lakebase Autoscaling
usage begins in January 2026". The AWS compute figure is exactly 2× the derived Azure Premium figure —
consistent with an AWS Enterprise-tier DBU rate of $0.52 vs Azure Premium's $0.26 — and the storage
figures agree to within 11%. That agreement is why I have reasonable confidence in the derivation, but
**the Azure figures are the ones I verified.**
Secondary: https://medium.com/@patrikslechta/databricks-lakebase-autoscaling-cost-model-decoded-what-you-actually-pay-and-when-its-worth-it-1a7eddb1999a ; https://layerbase.com/blog/should-i-use-lakebase-for-postgres

### 2.4 What an idle instance actually costs — [D]

Scale-to-zero eliminates compute cost **only after the inactivity timeout elapses**, and the default
timeout is **24 hours**. Until then you pay the autoscaling **minimum** CU.

| Scenario (Azure Premium, East US) | Arithmetic | Cost |
|---|---|---|
| Default `production` compute (min 8 CU), idle 24 h before suspending | 8 × 24 × $0.0554 | **$10.63 per idle stretch** |
| Same, if scale-to-zero is **off** (always-on, min 8 CU) | 8 × 730 × $0.0554 | **~$324 / month** |
| Compute-defaults size (min 2 CU), always-on | 2 × 730 × $0.0554 | **~$81 / month** |
| Smallest autoscale floor (0.5 CU), always-on | 0.5 × 730 × $0.0554 | **~$20 / month** |
| Genuinely idle dev DB, timeout set to 60 s | ~0 compute | **≈ storage only** |
| 10 GB data + 7-day PITR history | 10 × $0.39 | **$3.90 / month** + PITR |

**The single biggest cost lever is the scale-to-zero timeout**, and the default (24 h) is the worst case
for a dev/demo database that gets poked once a day. Set it to minutes.

### 2.5 Always-On pricing — [V]

**[V]** Blog dated **2026-05-27**: a project can disable scale-to-zero and get **25% off the baseline
(minimum) capacity** vs standard autoscaling rates, applied after 24 hours of continuous use. Usage above
the minimum bills at regular autoscaling rates. An **additional 50% promotional discount** stacks, running
**through 2027-01-31**. HA replicas and the largest instances qualify automatically. No commitment; can be
toggled off.
https://www.databricks.com/blog/introducing-always-pricing-automatic-savings-databricks-lakebase

Applied to the 2 CU always-on case: $81 × 0.75 × 0.50 ≈ **$30/month** while the promo lasts, **$61/month**
after 2027-01-31. **[D]**

### 2.6 Free / dev tier

**[V]** **Lakebase Postgres is available in Databricks Free Edition**, which is serverless-only with
per-account quotas explicitly including *"Lakebase projects"*. Caveats stated in the docs: no guaranteed
reliability/support/SLA; exceeding quota shuts down the workspace's compute for the rest of the day (in
extreme cases the month); one workspace and one metastore per account; **"Free Edition accounts may not
be used for commercial purposes."**
https://docs.databricks.com/aws/en/getting-started/free-edition-limitations ;
https://learn.microsoft.com/en-us/azure/databricks/getting-started/free-edition

**[V]** The Lakebase pricing page also advertises *"Pay as you go with a 14-day free trial."*

**Read for an OSS accelerator:** Free Edition is a legitimate path for contributors to *try* Lakebase, but
the no-commercial-use clause and the day-killing quota behaviour mean it cannot be the documented
development environment for a project that expects to be adopted commercially. The exact Lakebase project
quota numbers in Free Edition **[?]** — not stated on the pages I read.

---

## 3. Lakebase from .NET

### 3.1 Does Npgsql work?

**Yes, with two configuration requirements.** Lakebase speaks the standard Postgres wire protocol on
5432 and Databricks documents *"any Postgres driver"*. But:

1. **[V] SSL is mandatory** (`sslmode=require`). **[V] Npgsql's default `SSL Mode` is `Prefer`**, which
   permits but does not require TLS. The connection string must set `SSL Mode=Require` (or `VerifyFull`)
   explicitly. Npgsql values: `Disable, Allow, Prefer, Require, VerifyCA, VerifyFull`.
   https://www.npgsql.org/doc/security.html
2. **Auth** — see below.

**[V]** .NET/Npgsql is **not named** in the Lakebase client documentation (which lists psql, pgAdmin,
DBeaver, PgHero) nor in the SDK connection guide (Python, Java, Go). No Databricks-published .NET example
for Lakebase was found. **This is a documentation gap, not a technical blocker** — the Java/JDBC example
is structurally identical to what Npgsql needs.

### 3.2 Authentication — two options, and the .NET-relevant asymmetry

**[V] Option A — native Postgres password auth.** Traditional role + non-expiring password.
Works through PgBouncer. **"Password connections are disabled by default for new Lakebase Autoscaling
projects"** — must be enabled. This is the simplest .NET path: an ordinary connection string.
https://docs.databricks.com/aws/en/oltp/projects/authentication

**[V] Option B — OAuth token as password.** Token lifetime **60 minutes**; *"token expiration is enforced
only at login. Open connections remain active even after the token expires."* Requires SSL.
**Critically: "Built-in connection pooling (PgBouncer) does not support OAuth authentication."**
So OAuth ⇒ direct endpoint only.

**[V] The .NET problem with Option B: there is no Databricks SDK for .NET.** The SDK-based flow is
documented for **Python (≥0.89.0), Java (≥0.73.0), Go (≥0.109.0)** only. Databricks' own guidance for
other languages is an explicit two-call REST flow:

```
POST {DATABRICKS_HOST}/oidc/v1/token          # Basic auth = clientId:clientSecret,
                                              # grant_type=client_credentials&scope=all-apis
                                              # -> .access_token (60 min)
POST {DATABRICKS_HOST}/api/2.0/postgres/credentials
     Authorization: Bearer <access_token>
     { "endpoint": "projects/<p>/branches/<b>/endpoints/<e>" }
                                              # -> .token (60 min), .expire_time
```
Then use `.token` as `PGPASSWORD`, with `PGUSER` = the **service principal client ID (UUID)**.
https://learn.microsoft.com/en-us/azure/databricks/oltp/projects/external-apps-manual-api ;
https://learn.microsoft.com/en-us/azure/databricks/oltp/projects/external-apps-connect

**[V] Npgsql has the right hook for this.** `NpgsqlDataSourceBuilder.UsePeriodicPasswordProvider(
Func<NpgsqlConnectionStringBuilder, CancellationToken, ValueTask<string>>, TimeSpan successRefreshInterval,
TimeSpan failureRefreshInterval)` is documented as *"the recommended way to fetch a rotating access
token."* `UsePasswordProvider` is the per-connection variant. This is the exact .NET analogue of the
`BeforeConnect` (pgx) / HikariCP-custom-DataSource (Java) patterns Databricks documents.
https://www.npgsql.org/doc/security.html ; https://www.npgsql.org/doc/api/Npgsql.NpgsqlDataSourceBuilder.html

**Estimated .NET integration cost: one small class** — an `HttpClient` doing the two POSTs with caching,
wired into `UsePeriodicPasswordProvider` with a ~55 min refresh interval. Databricks' Java example uses
`maxLifetime = 45 min` to recycle pooled connections ahead of the 60 min token expiry; the same guidance
applies to `Connection Lifetime` in Npgsql. Note Databricks' own note that the returned credential is
*workspace-scoped* despite requiring an `endpoint` parameter.

**[V] Prerequisites for the service-principal path** (all documented, all one-time):
- service principal with OAuth secret (up to 730-day lifetime) and **"Workspace access" entitlement enabled**
  — omitting this yields *"API is disabled for users without workspace-access entitlement"*;
- a matching Postgres OAuth role created **via SQL, not the UI**:
  `CREATE EXTENSION IF NOT EXISTS databricks_auth; SELECT databricks_create_role('{client-id}','SERVICE_PRINCIPAL');`
  followed by explicit `GRANT`s (role name is the client-ID UUID, case-sensitive).

### 3.3 EF Core

No Databricks documentation of EF Core against Lakebase was found **[?]**. Assessment from first
principles plus documented constraints:

**Should work.** EF Core + `Npgsql.EntityFrameworkCore.PostgreSQL` targets Postgres 16/17/18 — all of
which Lakebase supports — and Lakebase permits ordinary DDL. Migrations are plain `CREATE TABLE` /
`ALTER TABLE` SQL.

**Documented constraints that affect migrations specifically:**

| Constraint | Consequence for EF Core | Source |
|---|---|---|
| Pooled endpoint (PgBouncer transaction mode) lists *"pg_dump and schema migrations"* as unsupported | **Run `dotnet ef database update` / `Migrate()` against the direct (non-pooled) endpoint.** Neon documents the same rule for its own service. | connection-pooling docs; https://neon.com/docs/guides/entity-migrations |
| OAuth does not work through PgBouncer | Migrations over OAuth are direct-endpoint-only by construction | authentication docs |
| Session-level `SET` and advisory locks unsupported in pooled mode | EF Core's migration locking / any `SET` in a migration must be on the direct endpoint | connection-pooling docs |
| `CREATE TABLESPACE` errors out | Do not use `HasTablespace`-style config or tablespace DDL in migrations | compatibility docs |
| No Postgres `superuser`; `databricks_superuser` replaces it | Migrations cannot do anything superuser-gated. Roles created in the Lakebase UI get `databricks_superuser`, so ordinary DDL is fine. | compatibility docs |
| Extensions limited to the allowlist | `HasPostgresExtension("...")` only for allowlisted names | extensions docs |
| Unlogged tables do not survive restart/scale-to-zero; local disk cap 20 GiB (or 15 GiB × max CU) for temp/unlogged | Don't lean on unlogged tables | compatibility docs |
| Idle connections auto-close, destroying temp tables / prepared statements / advisory locks | Standard resilience: `EnableRetryOnFailure`, don't hold long-lived state | compatibility docs |

**Npgsql auto-prepare note [S/V mix]:** Npgsql's `Max Auto Prepare` conflicts with PgBouncer transaction
mode historically; PgBouncer ≥1.21.0 added `max_prepared_statements` support. Databricks states
*"driver-level prepared statements work"* through their pooler **[V]**, which implies the feature is
enabled, but I could not find the configured value **[?]**. Safe default: leave `Max Auto Prepare` at its
default (0, off) on the pooled endpoint until measured.

### 3.4 Hard incompatibilities — [V]

These are documented and unambiguous:

- **No Postgres `superuser`** and no host OS / local filesystem access. *"You can't connect using Postgres
  `superuser`, and any functionality that requires superuser privileges or direct local file system access
  is not allowed."*
- **No native logical replication.** *"Replicating data to or from a Lakebase database using native Postgres
  logical replication is not yet available"*; creating **replication slots, publications, or subscriptions
  is not supported.** → **Debezium / Npgsql logical-decoding CDC out of Lakebase is not possible.** Use
  Lakebase Change Data Feed (Public Preview) instead (§5).
- **No tablespaces** (`CREATE TABLESPACE` errors).
- **No Postgres log access.**
- Database parameters settable only at session/database/role level, **not instance level**.
- Database encoding/collation immutable after creation.

https://docs.databricks.com/aws/en/oltp/projects/compatibility

### 3.5 GitHub / community search result

Targeted searches for Npgsql/.NET issues against Lakebase found **nothing** — no GitHub issues in
`npgsql/npgsql` or `npgsql/efcore.pg` mentioning Lakebase, and no community threads on .NET + Lakebase.
The one relevant Databricks Community thread is Python/SQLAlchemy and confirms local external
connections work (§5.3). **[?]** Absence of reports is weak evidence either way — it more likely reflects
that .NET is a thin slice of the Databricks user base than that everything works.

---

## 4. Alternative: Azure Database for PostgreSQL Flexible Server

Kept brief per brief — we know Postgres works.

### 4.1 Cheapest viable dev/demo tier — [V]

**[V]** Rates from the Azure Retail Prices API, `eastus`, queried 2026-07-31:

| Item | Rate | Monthly (730 h) |
|---|---|---|
| Burstable **B1ms** (1 vCore, 2 GiB) compute | **$0.017 / hour** | **$12.41** |
| Burstable B2s (2 vCore, 4 GiB) | $0.068 / hour | $49.64 |
| Storage (Premium SSD v2 / standard data stored) | **$0.115 / GiB-month** | 32 GiB → **$3.68** |

Endpoint: `https://prices.azure.com/api/retail/prices?$filter=serviceName eq 'Azure Database for PostgreSQL' and armRegionName eq 'eastus'`

**Cheapest viable dev/demo total: ~$16/month** (B1ms + 32 GiB, the minimum storage size).

**[V]** Extra levers:
- **Stop/start**: *"The compute tier billing stops immediately when you stop the server."* Server stays
  stopped up to 7 days. → a demo environment can be near-free between sessions.
- **Azure free account**: 12 months free — 750 h/month of B1ms + 32 GB storage + 32 GB backup.
  https://learn.microsoft.com/azure/postgresql/configure-maintain/how-to-deploy-on-azure-free-account
- Backups: 7-day retention default, up to 35 days; free up to 100% of provisioned storage.

**[V] Caveat on Burstable, stated in the docs:** *"This tier is primarily designed for nonproduction
scenarios such as development, staging, or testing, does not qualify for 24/7 support, and root cause
analysis (RCA) may not be provided."* If CPU credits deplete *"the server might become unreachable."*
B1ms also caps at **50 max connections / 35 user connections** — tight for a connection-pooled web app;
B2s jumps to 429/414.
https://learn.microsoft.com/azure/postgresql/compute-storage/concepts-compute ;
https://learn.microsoft.com/azure/postgresql/configure-maintain/concepts-limits

**[V] Postgres versions supported: 18, 17, 16, 15, 14, 13, 12, 11.** Same major versions as Lakebase and
as `postgres:17` locally.

### 4.2 Entra ID auth — [V]

**[V]** Fully supported and first-class. Three server modes: *PostgreSQL authentication only*,
*Microsoft Entra authentication only*, *both*. Admin can be a **user, group, service principal, or managed
identity**. Token-based auth for applications is explicitly supported; the flow is
"request token from Entra → send JWT as the password → server validates with Entra."
https://learn.microsoft.com/azure/postgresql/security/security-entra-concepts ;
https://learn.microsoft.com/azure/postgresql/security/security-entra-configure

**[V]** Microsoft publishes .NET-specific guidance for exactly this pattern
(`Azure.Identity` token → `Npgsql` password provider):
https://devblogs.microsoft.com/dotnet/using-postgre-sql-with-dotnet-and-entra-id/

**This is the cleanest managed-identity story available to a .NET app**, and it is strictly better
supported than the Lakebase equivalent (which has no .NET SDK). Networking note: Entra needs outbound
reachability to `login.microsoftonline.com` and `graph.microsoft.com`; with VNet integration add an NSG
rule for the `AzureActiveDirectory` service tag.

### 4.3 Local story — [V]

`Testcontainers.PostgreSql` (NuGet) is the standard module; documented at
https://dotnet.testcontainers.org/modules/postgres/. Combined with `WebApplicationFactory` this gives
per-test-class disposable Postgres against the real engine. Nothing exotic required. This works against
`postgres:17` and therefore against the same major version Lakebase runs.

---

## 5. Decision inputs for an OSS accelerator

### 5.1 Is there a local Lakebase emulator? — **No.** [V/?]

- **[V]** No emulator is documented anywhere in `/oltp/projects/`. The Databricks Community answer to
  "how do I set up a Lakebase Postgres connection locally" is *connect over the internet to the real
  instance* using standard drivers and either an OAuth token or native password.
  https://community.databricks.com/t5/lakebase-discussions/databricks-app-how-to-setup-lakebase-postgres-connection-locally/m-p/149736
- **[V]** **Neon Local** (`neondatabase/neon_local` on Docker Hub) exists and does Docker-Compose-friendly
  ephemeral branching — but it is a **proxy to Neon Cloud**, not an offline emulator, and it is a
  neon.com product. **No Databricks documentation references Neon Local for Lakebase**, and I found no
  evidence it targets Lakebase endpoints. Treat as unavailable. **[?]** on whether it could be made to work.

### 5.2 Contributor friction if we require Lakebase

To run the app, a contributor would need: a Databricks workspace in a supported region → a Lakebase
project → a service principal with an OAuth secret and the workspace-access entitlement → a Postgres
OAuth role created by SQL with the correct case-sensitive UUID name → four env vars including a
`projects/.../branches/.../endpoints/...` resource path. Every one of those is a documented step with a
documented failure mode (the Databricks troubleshooting table lists five distinct auth errors).

Against `docker run -e POSTGRES_PASSWORD=x -p 5432:5432 postgres:17`.

This is not close. **Requiring Lakebase for local development would be a serious adoption tax on an OSS
accelerator**, and Free Edition cannot absorb it because of the no-commercial-use clause and the
quota-exhaustion behaviour (§2.6).

### 5.3 Can one EF Core model target both?

**Yes, with no schema divergence.** The reasons are concrete:

- Same Postgres major version is available on both (17 by default on Lakebase; 11–18 on Azure Flexible
  Server; `postgres:17` locally). Same `Npgsql.EntityFrameworkCore.PostgreSQL` provider, same SQL.
- The Lakebase restrictions that matter (§3.4) are all **things a normal application schema never uses**:
  superuser operations, tablespaces, logical replication, instance-level GUCs, unlogged tables.
- pgvector, PostGIS, citext, pg_trgm, uuid-ossp, hstore, ltree, pgcrypto are all on the Lakebase
  allowlist and all present in stock Postgres images / Azure Flexible Server.

**Where divergence actually lives — all operational, none in the model:**

| Concern | Local `postgres:17` | Azure Flexible Server | Lakebase |
|---|---|---|---|
| SSL | off | `Require` | `Require` (mandatory) |
| Credential | static | static **or** Entra token via `Azure.Identity` | static (opt-in) **or** Databricks OAuth token via 2-call REST |
| Migration endpoint | any | any | **direct (non-pooled) endpoint only** |
| Pooling | Npgsql | Npgsql / PgBouncer | Npgsql + optional Lakebase PgBouncer (transaction mode) |
| Cold start | none | none | first query after suspend, "few hundred ms" |

All of that is absorbed by a single `NpgsqlDataSourceBuilder` factory selected by configuration. That is
the right seam, and it is small.

**What is *not* portable** is anything Lakebase-specific we build on top: synced tables, Lakebase CDF,
branch-per-tenant or branch-per-PR workflows, Unity Catalog registration. Those need to sit behind an
interface with a no-op/manual implementation for the plain-Postgres path, and cannot be integration-tested
without a real workspace — so they belong in an optional, credential-gated CI job, not the default `dotnet test`.

---

## 6. Moving data between operational Postgres and the lakehouse

Four documented Databricks-native paths. **Direction matters and they are not symmetric.**

### 6.1 Lakehouse → Lakebase: **synced tables** (reverse ETL) — GA

**[V]** Serves Unity Catalog tables into Lakebase Postgres for low-latency app reads, via managed
Lakeflow pipelines that maintain both the UC synced table and the Postgres table.

| Mode | Behaviour | Latency |
|---|---|---|
| **Snapshot** | one-time full copy; efficient when >10% of rows change per cycle | batch, no latency guarantee |
| **Triggered** | scheduled incremental (inserts/updates/deletes) | configurable; "expensive below 5-minute intervals" |
| **Continuous** | streaming | **seconds**, 15-second minimum interval |

**[V] Throughput, and note the Autoscaling penalty:**
- Lakebase **Provisioned**: ~1,200 rows/s per CU (continuous/triggered); up to 15,000 rows/s per CU (snapshot).
- Lakebase **Autoscaling**: ~**150 rows/s per CU** (continuous/triggered); up to **2,000 rows/s per CU** (snapshot).

Since Autoscaling CUs are 2 GB (vs Provisioned's 16 GB), that is roughly the same per-GB throughput —
but it means **sync throughput scales with the CU count you are paying for**, which couples ingestion
capacity to compute spend.

**[V] Prerequisites and limits:**
- Triggered/Continuous require **change data feed enabled on the source Delta table**.
- 16 TB quota across all synced tables per instance.
- Type mapping: ARRAY/MAP/STRUCT → JSONB; **GEOGRAPHY, GEOMETRY, VARIANT unsupported**.
- Schema evolution: **additive changes only** in Triggered/Continuous.
- Duplicate keys need a timeseries key for dedup.
- Databricks *"strictly recommends running only read queries"* against synced tables in Postgres — they
  are a read-serving surface, not a writable table.

https://docs.databricks.com/aws/en/oltp/projects/reverse-etl ;
https://docs.databricks.com/aws/en/oltp/projects/sync-tables

**[?]** I found no documentation of synced tables targeting an **external** (non-Lakebase) Postgres.
Every reference frames the target as Lakebase. Treat this as Lakebase-only.

### 6.2 Lakebase → Lakehouse: **Lakebase Change Data Feed** — **Public Preview**

**[V]** Every insert/update/delete on a Lakebase Postgres table is captured from the WAL via logical
decoding (the `wal2delta` extension) and written to a Unity Catalog managed Delta table,
**batched and flushed every ~15 seconds**.

- Destination: `lb_<table_name>_history`, with system columns `_pg_change_type`, `_pg_lsn`, `_pg_xid`,
  `_timestamp`, `_sort_by`.
- Requires: Lakebase **Autoscaling on Postgres 17**; source tables in `databricks_postgres`;
  **`REPLICA IDENTITY FULL`** on participating tables; UC `USE CATALOG`/`USE SCHEMA`/`CREATE TABLE`;
  `CAN MANAGE` on the project.
- Limits: partitioned tables unsupported; empty tables skipped until they have a row; destination catalogs
  with default storage unsupported; types without a Delta equivalent become STRING.
- **Status: Public Preview** (explicitly marked on the docs index).

https://learn.microsoft.com/azure/databricks/oltp/projects/lakebase-cdf ;
https://docs.databricks.com/aws/en/oltp/projects/lakehouse-sync

**This is the only Databricks-native way to get Lakebase writes into Delta**, because native Postgres
logical replication is blocked (§3.4). It being Preview is a genuine risk if it is on our critical path.

### 6.3 External Postgres → Lakehouse: **Lakeflow Connect PostgreSQL connector** — **Public Preview**

**[V]** Managed CDC ingestion. Uses **logical replication** on the source; an ingestion gateway extracts
snapshot + change data into a UC staging volume, then a pipeline lands it.

- **Explicitly supports Azure Database for PostgreSQL**, plus AWS RDS/Aurora, GCP Cloud SQL, EC2/VMs, on-prem.
- Requires **PostgreSQL 13+**; the gateway must run in **continuous mode** to prevent WAL bloat and
  replication-slot accumulation.
- **Status: Public Preview — "reach out to your Databricks account team to enroll."**
- Latency **[?]** — not stated in what I read; it is a pipeline, so expect minutes not seconds.

https://docs.databricks.com/aws/en/ingestion/lakeflow-connect/postgresql-pipeline ;
https://docs.databricks.com/aws/en/ingestion/lakeflow-connect/postgresql-source-setup

**Note the mirror-image constraint:** this connector needs logical replication on the source — which
Lakebase does not offer. So Lakeflow Connect is the *external Postgres* path and CDF is the *Lakebase*
path; they are not interchangeable.

### 6.4 Lakehouse → query external Postgres: **Lakehouse Federation** — GA

**[V]** `CREATE CONNECTION` (host/port/user/password, secrets recommended) + `CREATE FOREIGN CATALOG`
gives a UC-governed, **read-only** foreign catalog over PostgreSQL with query pushdown and table-level
access control. Schemas stay in sync with the source. No copy, no latency — it is query-time federation,
so freshness is perfect and throughput is bounded by the source database.
https://docs.databricks.com/aws/en/query-federation/postgresql ;
https://docs.databricks.com/aws/en/query-federation/database-federation

Read-only. Cannot write back. Best for low-volume joins of operational data into analytics, not for
bulk analytical scans of a production OLTP box.

### 6.5 Summary matrix

| Path | Direction | Status | Latency | Works with plain Azure Postgres? |
|---|---|---|---|---|
| Synced tables / reverse ETL | Delta → Postgres | **GA** | Continuous: seconds (15 s min); Triggered: ≥5 min sensible; Snapshot: batch | **No** — Lakebase only |
| Lakebase CDF | Postgres → Delta | **Public Preview** | ~15 s batches | **No** — Lakebase only, PG17 only |
| Lakeflow Connect PostgreSQL | Postgres → Delta | **Public Preview**, enrollment-gated | pipeline; **[?]** | **Yes** (needs logical replication) — and **not** with Lakebase |
| Lakehouse Federation | Delta ← Postgres (read-only query) | **GA** | query-time (no staleness) | **Yes** |
| Hand-rolled JDBC/Spark write | Delta → Postgres | n/a (DIY) | batch | **Yes** |

**The load-bearing observation for the plan:** if we pick plain Azure Postgres, the **Delta → Postgres**
direction has **no managed Databricks path at all** — we would write a Spark/JDBC job ourselves. That is
the single strongest technical argument for Lakebase, and it is exactly the direction a SaaS app needs
(serve enriched/aggregated analytics back into the operational app).

Conversely, if we pick Lakebase, the **Postgres → Delta** direction depends on a **Public Preview**
feature (CDF), with no logical-replication fallback.

---

## 7. Open items / could not determine

- **[?]** Authoritative AWS Lakebase list price — the pricing page is client-rendered and the docs URL
  redirects to it. Azure figures were derived from verified Microsoft sources instead.
- **[?]** Whether database **storage** continues to bill while a compute is scaled to zero (not stated on
  the scale-to-zero page; assume yes).
- **[?]** Exact Lakebase project quotas inside Databricks Free Edition.
- **[?]** PgBouncer `max_prepared_statements` value configured by Databricks.
- **[?]** Lakeflow Connect PostgreSQL end-to-end latency figures.
- **[?]** Whether Neon Local can be pointed at a Lakebase endpoint (undocumented; assume no).
- **[?]** No .NET/Npgsql-specific Lakebase issue reports or success reports found anywhere — the .NET
  path is undocumented by Databricks and, as far as public evidence goes, untrodden. **Prototype it
  before committing.**

---

## 8. Recommendation

**Design for both; default to plain Postgres; make Lakebase a first-class opt-in production target.**

Concretely:
1. EF Core + Npgsql, targeting **Postgres 17**, with no Lakebase-specific schema constructs
   (no tablespaces, no superuser DDL, extensions restricted to the Lakebase allowlist).
2. Local/CI: `postgres:17` container + `Testcontainers.PostgreSql`. No Databricks account needed to run
   `dotnet test` or `dotnet run`.
3. One `NpgsqlDataSourceBuilder` factory behind configuration, with three credential strategies: static
   password (local), **Entra token via `Azure.Identity`** (Azure Flexible Server), and
   **Databricks OAuth via the two-call REST flow wired into `UsePeriodicPasswordProvider`** (Lakebase).
   Force `SSL Mode=Require` on both cloud paths.
4. Migrations always run on the **direct, non-pooled** endpoint.
5. Lakehouse integration (synced tables, CDF) behind an interface, with the Lakebase implementation
   exercised only in an optional credential-gated CI job.

**The trade-off, stated plainly:** this costs us one extra credential strategy and a documented
"migrations use the direct endpoint" rule — perhaps a day of work and a paragraph of docs. In exchange,
contributors clone and run with `docker run postgres:17`, we do not bet the accelerator's correctness on
a five-month-old GA with a Preview CDC path, and we keep the Lakebase story — which is the genuinely
differentiated part (GA synced tables are the only managed Delta → Postgres path that exists) — as
something an adopter turns on rather than something every contributor must provision.

The honest counter-argument: if the accelerator's entire premise is "SaaS **on Databricks**", making
Lakebase optional risks the lakehouse integration becoming the untested path. Mitigate by making the
Lakebase deployment the one we demo, document, and run in the reference environment — just not the one
required to open a PR.
