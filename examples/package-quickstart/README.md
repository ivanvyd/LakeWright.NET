# Add tenant-scoped Databricks SQL to an existing .NET app

This standalone .NET 8 console consumer uses published `LakeWright.Core` and
`LakeWright.Databricks` **3.0.0** packages. Keep your application's identity provider,
membership store, and hosting. PostgreSQL, Signalboard, Docker, and a repository build are
not required for this path.

## Run the offline smoke check

Install a .NET 8 SDK or newer and the .NET 8 runtime. Download or copy this whole directory
to your own working directory. From that directory:

```bash
dotnet restore --configfile NuGet.Config
dotnet run -c Release -- --offline
```

Restore downloads NuGet packages. The executable then talks only to its own loopback HTTP
fixture. It does not read credentials, inspect workspace configuration, or fall back to a
real workspace. It refuses to run without `--offline`.

Expected output:

```text
ALLOW: member-alice -> Acme; tenant-scoped query returned 1.
DENY: member-alice -> Globex; membership denied; no statement sent; transport requests remain 1.
OFFLINE_OK: published 3.0.0; no workspace credentials or external network calls.
```

`MembershipResolver` checks a server-owned `(tenant, principal)` membership mapping before
using `ITenantContextFactory`. Alice belongs to Acme, and Bob belongs to Globex. Alice's
request for Globex returns no context; the example stops before building a statement. The
transport count stays at one and the allowed result stays unchanged. The fixture validates
the serialized catalog, schema, SQL, and bound parameter of the allowed query.

The empty `Directory.Build.*` and `Directory.Packages.props` files prevent inheritance of this
repository's build policy and central package versions. The local `global.json` accepts a
released .NET SDK from 8 onward. There are no project references or repository analyzers.

## Use it in your application

Follow [Add to an existing app](../../docs/guides/add-to-existing-app.md) to replace the
fixture membership mapping with your trusted store and choose one credential source. The
guide lists real-workspace prerequisites. The offline output above proves the package and
request boundary; live workspace execution is a separate, unverified step in this example.

The [ExternalTenancy source sample](../../samples/ExternalTenancy) remains available for
source builds. The worker and tenant-deletion fixes are introduced in **3.0.0**.
These fixes belong to the optional application modules and are not exercised by this
Core/Databricks package example.

For the latest stable .NET 10 target, use the [tested adaptation](../../docs/guides/add-to-existing-app.md#use-the-latest-stable-net-target).
It changes the target and aligns the direct Microsoft.Extensions references to 10.0.11;
the canonical project above continues to verify the .NET 8 floor. .NET 11 RC has not been
verified here. Preview testing is separate from stable .NET 10 support.
