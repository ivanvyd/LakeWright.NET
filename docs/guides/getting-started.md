# Getting started

What exists today is a library, an HTTP API and a sample application, not a product. This gets you
to a running sample in about two minutes, then to the tests, then — if you want — to a live
workspace.

## Run the sample

You need Docker and the .NET 10 SDK. You do **not** need a Databricks account.

```bash
git clone https://github.com/ivanvyd/lakewright-dotnet
cd lakewright-dotnet/samples/Signalboard
docker compose up -d
dotnet run
```

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

builder.Services.AddLakewright(builder.Configuration);

// Both are optional. Tenancy, authorization and the operations API work without Databricks;
// add these when you want queries and jobs, and omit the worker in a web-only process.
builder.Services.AddLakewrightDatabricks(builder.Configuration);
builder.Services.AddLakewrightOperationWorker(builder.Configuration);

var app = builder.Build();

app.UseAuthentication();
app.UseLakewrightTenancy();   // must run before UseAuthorization
app.UseAuthorization();
app.MapLakewrightOperations();
```

Order matters. `UseLakewrightTenancy` resolves the tenant that the authorization policies then read,
so it sits between authentication and authorization. Put it elsewhere and every tenant policy fails
closed, which is the safe direction but a confusing morning.

`AddLakewrightDatabricks` validates `WorkspaceUrl` and `WarehouseId` at startup, so a half-filled
`Databricks` section fails immediately rather than on the first query.

You supply `IDatabricksTokenSource`. On Azure that is a managed identity requesting an Entra token,
which Databricks accepts directly with no stored secret ([ADR 0006](../decisions/0006-secretless-authentication.md),
proved in [spike 04](../planning/spike-04-managed-identity.md)).

## Configuration

```json
{
  "ConnectionStrings": { "Lakewright": "Host=...;Database=lakewright" },
  "Multitenancy": { "Catalog": "lakewright_prod" },
  "Databricks": {
    "WorkspaceUrl": "https://adb-....azuredatabricks.net",
    "WarehouseId": "...",
    "Disposition": "INLINE",
    "InlineRowLimit": 10000
  },
  "OperationWorker": { "JobId": 123456789 }
}
```

`Disposition` defaults to `INLINE`, which returns rows in the response and caps them at
`InlineRowLimit`. Switch to `EXTERNAL_LINKS` for exports; results then arrive as
`StatementOutcome.LargeResult` carrying presigned URLs, which you fetch **without** an
`Authorization` header.

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
