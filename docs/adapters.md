# Adapter contract

LakeWright owns the security-sensitive mechanics once a host supplies its own identity and
membership facts. This table is the supported replacement surface. Register replacements before
the corresponding `AddLakeWright...` method when that method uses a `TryAdd` default; follow the
specific registration note where the extension intentionally replaces its default.

| Extension point | Shipped default | Replace when |
| --- | --- | --- |
| `ITenantContextResolver` | none; the host registers its resolver | Always. Resolve membership from the host's identity store and use `ITenantContextFactory` only after authorization. |
| `ITenantScopeStrategy` | `ProjectedColumnScope` | Shared schemas resolve tenant access through a mapping table, or need another trusted SQL wrapper. Register with `AddLakeWrightTenantScopeStrategy`; the resolver selects the strategy, never a request. |
| `IScopeVersionSource` | none | A tenant's effective scope can change independently of its tenant id. Use `ScopeVersion.FromMembers` or supply a cached source. |
| `IWorkspaceTokenCache`, `IEmbedTokenCache` | process-local memory | More than one replica mints embedding tokens. `LakeWright.Caching.Distributed` supplies `IDistributedCache` adapters; add a provider-specific lease only if global cold-miss coalescing is required. |
| `ILakeWrightFeatureGate` | `AlwaysOnFeatureGate` | A host needs runtime disablement. ASP.NET Core hosts can use `AddLakeWrightFeatureGate`; another host implements the one-method interface. |
| typed HTTP clients | no retry policy | A host needs retry, circuit-breaking, or custom transport. Pass the `Action<IHttpClientBuilder>` registration callback and attach its own policy. |
| `IEmbedPrecondition` | none | A host requires an additional proof before minting a browser token, such as served-revision verification. |
| `IPublishedDashboardDefinitionReader` | none | A deployment system keeps the authoritative published dashboard artifact. The public Lakeview endpoint does not supply published serialized SQL. |
| `IRefreshRunOwnership` | process-local memory | A refresh status endpoint runs on multiple replicas. Persist tenant-to-run ownership before exposing status. |
| `IDashboardMetadataCache` | short-lived process memory | Operations metadata must be shared across replicas. `AddLakeWrightDistributedDashboardMetadataCache` supplies the distributed adapter. This is a read cache, never an authorization boundary. |
| `IWarehouseWarmLimiter` | process-local memory | More than one replica must obey one warehouse pre-warm rate. Warming remains disabled unless explicitly enabled. |
| `IRawDataExportOwnership` | process-local memory | A CSV stream endpoint runs on more than one replica. Persist opaque operation id, tenant, and owner; never substitute a workspace statement id. |
| `IConversationOwnership` | process-local memory | Genie continuation/list/delete runs on more than one replica. Unrecorded conversations must remain invisible. |

## HTTP failure mapping

`TransportException` normally maps to 502 or 503, `WorkspaceRejectedException` to 502, and a
tenant-scope or validation exception to 400. Do not return workspace body excerpts to a browser.
The host decides the exact response shape and logs the provider diagnostic under its own protected
logging policy.

## Scope-change revocation

When membership changes, compute and persist a new scope version, then call
`IEmbedTokenCache.EvictTenant(tenantId)`. The changed scope version also changes the browser token
claim, bypassing the vendor result cache. Already-rendered iframes keep their prior result until
reload.
