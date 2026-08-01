# Getting started

What exists today is a library, an HTTP API and a sample application, not a product. This gets you
to a running sample in about two minutes, then to the tests, then — if you want — to a live
workspace.

## Run the sample

You need Docker and the .NET 10 SDK. You do **not** need a Databricks account.

```bash
git clone https://github.com/ivanvyd/lakewright-dotnet
cd lakewright-dotnet/samples/Signalboard
docker compose up
```

Docker is the only requirement. To work on the code, run `docker compose up -d postgres` and
`dotnet run` instead, which needs the .NET 10 SDK.

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
builder.Services
    .AddAuthentication(/* your identity provider */)
    .AddOpenIdConnect(/* ... */);

builder.Services.AddLakeWright(builder.Configuration);

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

`TokenCredential` rather than a string, because Entra tokens expire within the hour and the
credential is what knows how to get another one. An earlier version took a `GetToken()` string,
which was read once at startup and left every Databricks call failing 401 a little later with
nothing to detect it.

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
  }
}
```

`Disposition` defaults to `INLINE`, which returns rows in the response and caps them at
`InlineRowLimit`. Switch to `EXTERNAL_LINKS` for exports; results then arrive as
`StatementOutcome.LargeResult` carrying presigned URLs, which you fetch **without** an
`Authorization` header.

`OperationWorker:Jobs` maps each `Operation.Kind` to the Databricks job that runs it. A kind with no
entry fails the operation saying so, rather than running whichever job happened to be configured.

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
