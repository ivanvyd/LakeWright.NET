# Add LakeWright to an existing application

Start with the [published-package quickstart](../../examples/package-quickstart/README.md).
It targets .NET 8 and pins the published 2.1.2 packages. You can use the tenant-scoped SQL
layer without adopting the reference application's database, identity provider, or UI.

## Register the packages

In your application's project directory:

```bash
dotnet add package LakeWright.Core --version 2.1.2
dotnet add package LakeWright.Databricks --version 2.1.2
```

Keep your normal DI and configuration packages. For an Azure application that uses the
credential route below, also reference `Azure.Identity` at the version maintained by your
application. The executable quickstart does not need that package because its credential is
an offline fixture.

The credential fragment later in this guide is an adaptation excerpt, not a verified .NET 8
credential dependency graph. This repository uses `Azure.Identity` only for .NET 10. The tested
.NET 8 consumer deliberately uses `OfflineCredential` and the older Azure Core dependency floor.
Choose a credential package compatible with your application's target and verify that graph
separately; installing the newest credential package can change its transitive dependency floor.

## Use the latest stable .NET target

As checked on 7 November 2026, [.NET 10 is the latest stable release](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core).
.NET 11 RC3 is a prerelease, not the stable support target. The canonical quickstart retains
the .NET 8 consumer floor; .NET 10 consumers select the packages' `lib/net10.0` assets.

To adapt a copy of the quickstart, install a released .NET 10 SDK and change these entries
in `PackageQuickstart.csproj`, keeping the LakeWright packages pinned to 2.1.2:

```xml
<TargetFramework>net10.0</TargetFramework>
<PackageReference Include="Microsoft.Extensions.Configuration" Version="[10.0.11]" />
<PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="[10.0.11]" />
```

Then run the same `dotnet restore --configfile NuGet.Config` and
`dotnet run -c Release -- --offline` commands. Changing only the target while keeping the
example's exact 8.x Extensions pins causes package downgrade errors; align those direct
references with the .NET 10 dependency graph. This adaptation was compiled and ran the
same offline membership/query/denial fixture using the published 2.1.2 .NET 10 assets.
It does not establish a live credential or workspace integration.

## Resolve membership before reaching data

Implement `ITenantContextResolver` with a constructor accepting `ITenantContextFactory`, then
register it with `services.AddLakeWrightTenancy<YourMembershipResolver>()`. Use your existing
trusted membership store to look up the authenticated principal's membership in the requested
tenant. Load catalog and schema from server-owned tenant configuration. Only then call
`contexts.ForTenant(tenantId, catalog, schema)`.

The [complete resolver](../../examples/package-quickstart/Program.cs) contains a two-tenant
mapping and a wrong-tenant denial. Replace that fixture mapping with your membership query.
A route value, header, or client-selectable tenant claim identifies the requested tenant;
it is never proof of membership. Obtain `principalId` from the authenticated server identity,
not another request parameter. Do not register or expose `ITenantContextFactory` to endpoints.

At the endpoint, resolve first and return 404 for a missing context. The query path is:

```csharp
var tenant = await resolver.ResolveAsync(requestedTenant, principalId, cancellationToken);
if (tenant is null) { return Results.NotFound(); }

var statement = TenantScopedStatement.Create(
    tenant, "SELECT :value AS value", StatementParameter.Int("value", 1));
var outcome = await executor.ExecuteAsync(statement, cancellationToken);
```

Handle `StatementOutcome.Success`, `Failure`, `Pending`, and `LargeResult` deliberately.
[Statement results and exports](statement-results-and-exports.md) covers polling, deadlines,
and large results. SQL is trusted application code. Bind values with `StatementParameter`;
never concatenate SQL or accept caller-supplied catalog/schema identifiers. If you later expose
polling or cancellation, look up the stored statement identifier through tenant-owned application
state first. `IStatementExecutor.GetAsync` does not itself prove ownership of that identifier.

## Configure a real Azure workspace separately

The offline executable has no live mode. Before adapting its registration for real use, provide:

- An Azure Databricks workspace reachable by the application and a SQL warehouse ID.
- A Unity Catalog catalog and a separate, existing schema per tenant. The resolver must return
  their stored identifiers. The smoke query uses a bound constant and needs no fixture table.
- A Databricks-recognized application or developer identity with warehouse `CAN USE`,
  `USE CATALOG`, and `USE SCHEMA` on the intended objects. Queries against tables also need
  `SELECT` on those tables. Provisioning schemas is a separate administrative operation.
- Your application's authenticated principal and trusted membership lookup.

Use the canonical Azure `TokenCredential` route:

```csharp
services.AddSingleton<Azure.Core.TokenCredential>(new Azure.Identity.DefaultAzureCredential());
services.AddLakeWrightTenancy<YourMembershipResolver>();
services.AddLakeWrightDatabricks(configuration);
```

Configure `Databricks:WorkspaceUrl` and `Databricks:WarehouseId` through your application's
configuration provider. For developer sign-in, use `az login` with an identity authorized in
that workspace. For Azure hosting, configure the application's managed identity and its
Databricks permissions. Do not reuse `OfflineCredential` or `OfflineWorkspace` in this registration.

Register exactly one credential source. Workspace OAuth with both `Databricks:ClientId` and
`Databricks:ClientSecret` is the alternative described in
[ADR 0023](../decisions/0023-databricks-credential-selection.md); do not combine it with
`TokenCredential`. Keep secrets in your application's secret store. The reference deployment
uses [secretless authentication](../decisions/0006-secretless-authentication.md).

No live workspace was used to verify this quickstart. Run an allowed query and the wrong-tenant
request against your own explicitly authorized non-production workspace before relying on that
integration. The denied request should produce no Databricks call or application mutation.

## Optional application modules

Add `LakeWright.Multitenancy` when you want the supplied PostgreSQL organization, membership,
audit, and durable-operation model. Add `LakeWright.AspNetCore` when you want its middleware
and operations API. These application-tier modules target .NET 10; the Core/Databricks path
above supports .NET 8. They are not prerequisites for the package example.

Worker admission and deletion changes are introduced in **3.0.0**. In that version,
`ClaimNextAsync` requires ReadCommitted admission (including PostgreSQL's equivalent
ReadUncommitted mode), and purge discards never-submitted queued work only after the deletion
grace period. See
[ADR 0033](../decisions/0033-operation-admission-and-deletion-liveness.md). Published 2.1.2
retains its released behavior; this package smoke check does not test those source changes.
