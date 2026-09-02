# External tenancy sample

This net8 sample owns membership resolution itself and gives LakeWright only an
`ITenantContextResolver`. The resolver decides whether the authenticated principal belongs to the
requested tenant; only then does its injected `ITenantContextFactory` mint a `TenantContext`.

```powershell
dotnet run --project samples/ExternalTenancy/ExternalTenancy.csproj -c Release
```

In an application, replace the fixed demo membership check with the identity and membership store
you own. Do not construct `TenantContext` directly or accept a tenant ID from a request as proof of
membership.

The production composition root typically also does the following:

- derives `ScopeVersion` from the membership set and supplies it when the resolver creates the
  context, then evicts `IEmbedTokenCache` entries when that set changes;
- registers `AddLakeWrightDistributedTokenCaches` after a real `IDistributedCache` when more than
  one process mints tokens;
- passes the typed-client configuration callback to add its own resilience policy;
- installs an `ILakeWrightFeatureGate` implementation so embedding and statements can be disabled
  without a deploy;
- maps `TransportException` and `WorkspaceRejectedException` at the HTTP boundary without exposing
  provider response bodies; and
- registers `LakeWright.Databricks.RawData` only with trusted source definitions. For cross-replica
  CSV downloads, replace `IRawDataExportOwnership` with durable storage before exposing streams.

See [the adapter contract](../../docs/adapters.md) for the full replacement matrix.
