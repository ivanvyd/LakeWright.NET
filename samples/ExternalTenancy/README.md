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
