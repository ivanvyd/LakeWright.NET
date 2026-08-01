# Getting started

What exists today is a library and an HTTP API, not a product. There is no sample application to
look at yet — that is [M5](../planning/06-remaining-work.md). This gets you to running tests and,
if you want, a live workspace.

## Run the tests

You need Docker and the .NET 10 SDK. You do **not** need a Databricks account or a cloud
subscription.

```bash
git clone https://github.com/ivanvyd/lakewright-dotnet
cd lakewright-dotnet
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
builder.Services.AddLakewrightOperationWorker();   // omit in a web-only process

var app = builder.Build();

app.UseAuthentication();
app.UseLakewrightTenancy();   // must run before UseAuthorization
app.UseAuthorization();
app.MapLakewrightOperations();
```

Order matters. `UseLakewrightTenancy` resolves the tenant that the authorization policies then read,
so it sits between authentication and authorization. Put it elsewhere and every tenant policy fails
closed, which is the safe direction but a confusing morning.

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
