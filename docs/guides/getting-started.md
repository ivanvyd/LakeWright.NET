# Getting started

What exists today is a library, an HTTP API and a sample application, not a product. This gets you
to a running sample in about two minutes, then to the tests, then — if you want — to a live
workspace.

## Run the sample

Docker is the only requirement. You do **not** need a Databricks account, and you do not need the
.NET SDK — the app builds inside the image.

```bash
git clone https://github.com/ivanvyd/LakeWright.NET
cd LakeWright.NET/samples/Signalboard
docker compose up
```

To work on the code, run `docker compose up -d postgres` and `dotnet run` instead. That path does
need the .NET 10 SDK.

Open <http://localhost:8080>. The landing page seeds two organizations and three people and gives
you the curl commands that show a cross-tenant read returning 404 rather than 403, and a Viewer
being refused a write. That is the isolation model in about a minute, and it runs without a
workspace because tenancy and authorization never talk to Databricks.

## Run the tests

```bash
dotnet test --filter "Category!=Live"
```

That starts a PostgreSQL container and runs everything, including the cross-tenant isolation suite.
If it passes, your environment is correct.

## Wire it into an application

```csharp
using LakeWright.AspNetCore;
using LakeWright.Databricks;

builder.Services
    .AddAuthentication(/* your identity provider */)
    .AddOpenIdConnect(/* ... */);

builder.Services.AddLakeWright(builder.Configuration);
builder.Services.AddLakeWrightFeatureGate(builder.Configuration); // optional runtime kill switch

// Both are optional. Tenancy, authorization and the operations API work without Databricks;
// add these when you want queries and jobs, and omit the worker in a web-only process.
builder.Services.AddLakeWrightDatabricks(builder.Configuration);
builder.Services.AddLakeWrightOperationWorker(builder.Configuration);

var app = builder.Build();

app.UseAuthentication();
app.UseLakeWrightTenancy();   // must run before UseAuthorization
app.UseAuthorization();
app.MapLakeWrightOperations();
```

Order matters. `UseLakeWrightTenancy` resolves the tenant that the authorization policies then read,
so it sits between authentication and authorization. Put it elsewhere and every tenant policy fails
closed, which is the safe direction but a confusing morning.

`AddLakeWrightDatabricks` validates `WorkspaceUrl` and `WarehouseId` at startup, so a half-filled
`Databricks` section fails immediately rather than on the first query.

You register an `Azure.Core.TokenCredential`. On Azure that is `DefaultAzureCredential` backed by a
managed identity, which Databricks accepts directly with no stored secret
([ADR 0006](../decisions/0006-secretless-authentication.md), proved in
[spike 04](../planning/spike-04-managed-identity.md)):

```csharp
builder.Services.AddSingleton<TokenCredential>(new DefaultAzureCredential());
```

## Bring your own tenancy

`AddLakeWright` uses this library's PostgreSQL membership model. A product that already has a
trusted membership store can register its own resolver instead:

```csharp
public sealed class DirectoryTenantResolver(
    IDirectoryClient directory,
    ITenantContextFactory contexts) : ITenantContextResolver
{
    public async Task<TenantContext?> ResolveAsync(TenantId tenantId, string principalId, CancellationToken ct) =>
        await directory.IsMemberAsync(principalId, tenantId, ct)
            ? contexts.ForTenant(tenantId, "analytics")
            : null;
}

builder.Services.AddLakeWrightTenancy<DirectoryTenantResolver>();
builder.Services.AddLakeWrightDashboardEmbedding(
    builder.Configuration,
    http => http.AddStandardResilienceHandler());
builder.Services.AddLakeWrightDashboardOps(builder.Configuration);
builder.Services.AddLakeWrightDashboardRefresh(builder.Configuration);
```

`AddLakeWrightTenancy` passes the factory to the resolver and registers it nowhere else, so a
controller cannot mint a context from a caller-supplied tenant id. Return `null` for both a tenant
the principal cannot access and one that does not exist. See [ADR 0021](../decisions/0021-registered-resolvers-mint-tenant-contexts.md).

The embedding and dashboard-ops registrations do not install retries themselves. Pass their
optional `IHttpClientBuilder` callback when your host has a resilience policy; the callback applies
to every typed client registered by that call.

Embedding failures are typed for HTTP mapping: `TransportException` usually maps to 502 or 503,
`WorkspaceRejectedException` to 502, `NotPublishedException` to 404 or 409, and
`TenantScopeMissingException` to 400. Do not expose the workspace response excerpt directly to a
viewer; it is for server-side diagnostics.

When an adopter changes a tenant's effective scope, compute a new `ScopeVersion`, persist it with
the resolved tenant data, and call `IEmbedTokenCache.EvictTenant(tenantId)`. The changed version
alters `external_value`, which bypasses the vendor result cache; eviction removes this process's
old viewer-token entries. An iframe that is already rendered keeps its prior data until reload.

`TokenCredential` rather than a string, because Entra tokens expire within the hour and the
credential is what knows how to get another one. An earlier version took a `GetToken()` string,
which was read once at startup and left every Databricks call failing 401 a little later with
nothing to detect it.

### Dashboard refreshes

`AddLakeWrightDashboardRefresh` uses the separate `DashboardOps` principal and the Jobs API; it
does not pretend that publishing a Lakeview draft refreshes a warehouse result. Call
`StartOrJoinAsync` with the `TenantContext` produced by the resolver and a `RefreshJob` selected
by application configuration. Job-level parameters are sent for the tenant id, catalog, and schema,
so use a task type that supports job-parameter pushdown or explicitly forwards those parameters.

The default `IRefreshRunOwnership` store is process-local. That is safe after a restart or on the
wrong replica because an unrecorded run cannot be read, but it means a multi-replica host must
replace it with durable storage before serving a refresh-status endpoint. Do not call
`StatusAsync` with a bare run id from a browser; it requires the resolved `TenantContext` and checks
that the application recorded ownership before making a workspace request.

`IDashboardCacheBuster.BustOnceAsync(dashboardId, runId)` is separate from the job start so a
product can publish only after its refresh has reached a successful terminal state. It appends a
stable comment marker to each dataset query, PATCHes the draft with its ETag, and publishes the
revision. A repeated call for the same run is a no-op; a PATCH conflict is accepted only after a
fresh read proves another replica wrote the exact same marker. It publishes with
`embed_credentials: false` by default. Set `DashboardCacheBustOptions.EmbedCredentials` only when
that credential model is an intentional, reviewed part of the dashboard deployment.

`IDashboardPublishVerifier.HasUnpublishedChangesAsync` compares the draft's `update_time` with
the published revision timestamp and caches that inexpensive metadata check briefly. The public
Lakeview API does not return serialized SQL from its published-dashboard endpoint, so
`VerifyServedRevisionAsync` deliberately reports that verification is unavailable until the host
adds an `IPublishedDashboardDefinitionReader` for an authoritative published artifact. Register
`PublishedRevisionEmbedPrecondition` with the token broker only when that reader is present; it
then fails minting closed until `DashboardPublishGate` passes on the proved served definition.

`IDashboardMetadataCatalog` is the operations-only read surface for portal administration. It
reads draft and published metadata by opaque dashboard id and walks every page for `ListAllAsync`.
The shipped cache is local and deliberately short-lived; multi-replica hosts can call
`AddLakeWrightDistributedDashboardMetadataCache` after registering `IDistributedCache` to share
this read cache. Do not expose this operations
catalog directly to a browser: authorize the requested dashboard in the host before asking it for
metadata.

`IWarehouseWarmer` is an optional opening-a-board hint. `WarehouseWarmOptions.Enabled` defaults
to `false`; when explicitly enabled, the library requests a warehouse start at most once per
configured interval per warehouse and never reads a statement result. The in-memory limiter is
appropriate for one process. Replace `IWarehouseWarmLimiter` in a multi-replica application if a
single cross-replica warm rate is required.

### Readiness checks

After registering embedding and Databricks, add the opt-in readiness checks with
`builder.Services.AddHealthChecks().AddLakeWrightHealthChecks()`. They prove only the cached
workspace-token leg and a read-only warehouse-state call. The billable statement check is absent
unless the host explicitly enables it and registers `IReadinessStatementProbe`; that probe owns
the resolved tenant context and approved `SELECT 1` statement.

### Raw-data grids

`LakeWright.Databricks.RawData` turns a trusted `RawDataSource` definition into one
tenant-scoped, parameterized inline statement. A request cannot choose a view, physical column,
SQL operator text, or sort fragment: those are validated against the source's allow-listed fields.
Malformed values, excessive filters, values, offsets, and page sizes throw `ValidationException`
before a warehouse call, which a host maps to HTTP 400. Register the scoped service after
`AddLakeWrightDatabricks`:

```csharp
builder.Services.AddLakeWrightRawData(options => options.MaximumPageSize = 250);
```

`QueryAsync` deliberately refuses the `Export` request flag so a paged grid request cannot silently
become a file download. Use `IRawDataExportService.StartAsync` with the resolved tenant, an opaque
application owner key, and an application-owned operation id. Results at or below
`ExportInlineRowCap` are returned as bounded CSV lines. Larger results are recorded without a
workspace statement id and then retrieved through `StreamCsvAsync`, which checks the same tenant
and owner before it opens the tenant-scoped external-links stream. Replace
`IRawDataExportOwnership` with durable shared storage before serving the stream from multiple
replicas. Both the grid and CSV use the same source projection; text cells beginning with a formula
prefix are neutralized by default, unless the source opts out after a security review.

## Configuration

```json
{
  "ConnectionStrings": { "LakeWright": "Host=...;Database=lakewright" },
  "Multitenancy": { "Catalog": "lakewright_prod" },
  "Databricks": {
    "WorkspaceUrl": "https://adb-....azuredatabricks.net",
    "WarehouseId": "...",
    "Disposition": "INLINE",
    "InlineRowLimit": 10000
  },
  "OperationWorker": {
    "Jobs": { "analysis": 123456789, "export": 987654321 }
  },
  "LakeWright": {
    "DashboardRefresh": {
      "Policy": { "MinimumInterval": "00:15:00", "MaxConcurrentPerTenant": 1 },
      "JobLookupCacheDuration": "00:05:00"
    },
    "Features": {
      "Enabled": { "embedding": true, "statements": true, "operations": true, "conversations": true }
    }
  }
}
```

`Disposition` defaults to `INLINE`, which returns rows in the response and caps them at
`InlineRowLimit`. Switch to `EXTERNAL_LINKS` for exports; results then arrive as
`StatementOutcome.LargeResult` carrying presigned URLs, which you fetch **without** an
`Authorization` header.

`OperationWorker:Jobs` maps each `Operation.Kind` to the Databricks job that runs it. A kind with no
entry fails the operation saying so, rather than running whichever job happened to be configured.

`AddLakeWrightFeatureGate` is optional and defaults every feature to enabled. When registered, a
configuration reload can set any named feature to `false`; LakeWright refuses the call before
making an external request. It never records the tenant in the exception or telemetry. Use it for
an operational stop, not as authorization—the tenant resolver and per-call ownership checks remain
the security boundary.

The Databricks, Genie, and dashboard-operations registrations contribute to one LakeWright
startup validation summary. A half-configured host fails once with every missing or invalid
configuration key instead of emitting unrelated first-failure exceptions; resolve the reported
list before retrying startup.

For multiple application replicas, install `LakeWright.Caching.Distributed`, register your host's
`IDistributedCache`, then call `AddLakeWrightDistributedTokenCaches()`. Its cache keys are hashed
before reaching the cache provider and `EvictTenant` uses a shared generation marker, so an updated
scope makes earlier viewer tokens unreachable on every replica. Generic `IDistributedCache` has no
atomic lock primitive; the package collapses concurrent requests within each process, while a host
that needs global cold-miss coalescing must supply its cache provider's lease mechanism.

For Genie conversation continuation, listing, or deletion across replicas, install
`LakeWright.Caching.Redis`, register a shared `IConnectionMultiplexer`, then call
`AddLakeWrightRedisConversationOwnership()`. This is deliberately a Redis-specific adapter:
generic `IDistributedCache` cannot atomically claim a conversation or enumerate an owner's
conversations. Claims use Redis `SET NX`, so a foreign replica cannot overwrite an owner.

## Starting work safely from a client

`POST /organizations/{organizationId}/operations` accepts an `Idempotency-Key` header of up to 200
characters. Send one and a retried request returns the original operation instead of starting a
second Databricks run the tenant also pays for. The key is unique per organization and principal, so
two people in the same organization cannot collide.

| Situation | Response |
|---|---|
| First request | `202` with the new operation |
| Same key, same `kind` | `202` with the original operation |
| Same key, different `kind` | `422` — answering with the stored operation would answer a different question |
| Key longer than 200 characters | `400` — truncating it would dedupe requests that are not duplicates |

The key is yours and never reaches Databricks. The job idempotency token is generated server-side,
because a caller who could choose it could choose another tenant's.

## Watching it in production

The library publishes plain `System.Diagnostics` instruments and takes no OpenTelemetry dependency,
so it does not choose your exporter or its version. Subscribe with:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter(LakeWrightTelemetry.MeterName))
    .WithTracing(t => t.AddSource(LakeWrightTelemetry.ActivitySourceName));
```

| Instrument | What it tells you |
|---|---|
| `lakewright.operations.started` | Accepted operations, by kind. A replayed idempotency key does not count. |
| `lakewright.operations.completed` | Terminal operations, tagged with the state reached, so failure rate is a ratio of two series. |
| `lakewright.operations.queue_wait` | Seconds from accepted to claimed. A rising p99 against a flat median is one tenant's backlog pushing everyone back. |
| `lakewright.tenant.access_denied` | Refused tenant resolutions. These answer 404, so nothing in an access log separates them from a stale bookmark. |

None of them carries a tenant identifier. It is the first tag anyone reaches for and it is a
cardinality bomb in a system built to have many tenants: a thousand tenants turns four instruments
into four thousand time series. Tenant lands on spans, where sampling bounds the volume, and
per-tenant totals come from `operations` and `audit_events`.

## Talking to a model

Optional, and a separate package so a product that only queries a warehouse takes no
dependency on an AI client ([ADR 0009](../decisions/0009-a-separate-optional-ai-module.md)):

```csharp
builder.Services.AddSingleton<TokenCredential>(new DefaultAzureCredential());
builder.Services.AddDatabricksChatClient(
    new Uri("https://adb-....azuredatabricks.net"), "databricks-claude-sonnet-5");
```

You then inject `IChatClient` and use `Microsoft.Extensions.AI` as normal. The registration
authenticates with the same `TokenCredential` as everything else, so a deployment holds one
identity rather than an Entra token for Databricks and a separate key for the model.

Streaming works because the client carries a shim. Databricks attaches `usage` to every streaming
chunk with `completion_tokens` and `total_tokens` null, and the OpenAI deserialiser types them as
numbers, so an unpatched client throws part-way through the response. The shim strips the
incomplete object; the final chunk keeps its real numbers, so token counts still work.

## The audit trail

Starting an operation, completing one, and asking for a tenant you cannot reach each write a row to
`audit_events`, in the same transaction as the action. The denial matters most: it answers 404, so
the audit row is the only trace that someone went looking. The table is append-only in the model and
at the database — see [the SOC 2 mapping](../compliance/soc2-mapping.md).

## The Databricks side

The catalog is a prerequisite you create once; the bundle owns what lives inside it. See
[deploying-databricks.md](deploying-databricks.md), which also explains why catalog creation fails
on a Default Storage metastore.

## Before you build on this

Read [ADR 0002](../decisions/0002-enforce-tenant-isolation-in-the-query-layer.md). Unity Catalog row
filters do nothing when your backend connects as one service principal, which is why isolation lives
in the query layer here. If you take one idea from this repository, take that one.

Then read [docs/compatibility.md](../compatibility.md) so you know which parts have been run against
a real workspace and which have only been read in documentation.
