# 0018 — Cache the AI/BI embed token exchange

- Status: Accepted
- Date: 2026-08-30
- Supersedes: nothing
- Related: gap analysis §3.1, dashboard embedding token flow, 0017

## Context

`DashboardTokenBroker.IssueAsync` runs a three-leg OAuth exchange on every
call: workspace token (leg 1), `GET /tokeninfo` (leg 2), downscoped
viewer token (leg 3). Each leg is an HTTP roundtrip to the Databricks
workspace, costing the viewer 200–600 ms of wall time on a cold path and
several hundred requests per minute on a hot dashboard.

A second `IssueAsync` for the same `(tenant, dashboard, viewer)` triple
within the workspace-token lifetime is mechanically the same exchange:
the workspace token is reusable, the `tokeninfo` body is deterministic
for the same `external_value`, and the downscoped token is the only
artefact that changes (and only because Databricks re-mints it on each
`/tokeninfo` call). Caching the **downscoped token** turns the steady
state of an embedded dashboard into a single in-memory dictionary
lookup.

The gap analysis (§3.1) called this the single biggest performance win
in the library.

## Decision

Two optional `IDashboardTokenBroker` dependencies, both registered as
no-ops in tests and by default implementations in DI:

- `IWorkspaceTokenCache` — keyed on `ClientId`. The workspace token is
  the same for every `(tenant, dashboard, viewer)` triple; the only
  legitimate invalidation is a service-principal credential rotation.
- `IEmbedTokenCache` — keyed on `(TenantId, ScopeVersion, DashboardId,
  ViewerId)`. The downscoped token is per-viewer; the `ScopeVersion`
  segment exists because of 0017, where a scope bump produces a
  different `external_value` and therefore a different token.

The caches are *absolutely* expired at the token's own
`ExpiresAt - 30s`, not at a guessed lifetime. The vendor today issues
one-hour tokens; that is read from the response, not assumed.

The cache implementation is `ConcurrentDictionary<TKey, Entry>` of
`Lazy<Task<EmbedToken>>` records. The `Lazy` is the dogpile-collapse
mechanism: when 20 callers race for the same key, the first builds the
`Lazy`; every subsequent caller observes the same `Lazy` and awaits its
single inner `Task`. The factory body therefore runs exactly once per
key per cache lifetime, which is the whole point.

## Why `Lazy<Task<T>>` and not `IMemoryCache.GetOrCreateAsync`?

`IMemoryCache.GetOrCreateAsync` synchronises the lookup with an internal
lock but runs the factory **outside** the lock. Concurrent callers all
wait for the lock, all observe the miss, and all run the factory. That
is a textbook dogpile, and the standard fix is to wrap the factory in a
`Lazy<T>` so the inner work is the value being deduplicated.

`IMemoryCache` also has a structural problem for this use case: the
expiration has to be set on the `ICacheEntry` *before* the factory
body runs, because the entry is committed at that point. Computing
`ExpiresAt - safetyMargin` requires the factory's result, so the
expiration has to be written back afterwards — which `IMemoryCache` does
not expose cleanly. A `ConcurrentDictionary<Entry>` with the expiration
as a mutable field on the entry lets the broker write the computed
expiration after `await` returns, with no extra plumbing.

## Why not `IDistributedCache`?

The downscoped token is a per-viewer secret. Distributing it across
multiple application instances means every instance is a copy of every
viewer's downscope authority. The blast radius of a compromised cache
goes from "this process" to "every process the cache can reach", and
the eviction story (per-instance LRU vs. shared TTL) gets harder for no
latency win that the existing in-memory lookup doesn't already give us.

A consumer that needs cross-instance sharing can implement
`IEmbedTokenCache` over `IDistributedCache` themselves. The seam is
small and the security tradeoff is the consumer's to make, not the
library's.

## What this is not

- Not a vendor-side cache invalidation. The Databricks service still
  caches the `/tokeninfo` body for 24 hours; that is the bigger
  contributor to "the same scope returns old rows". A consumer who needs
  to invalidate the vendor cache bumps `ScopeVersion` (0017).
- Not a substitute for credential rotation. The workspace token cache
  is keyed on `ClientId`; rotating the service principal's secret
  invalidates by changing the key, not by clearing the cache.
- Not a recommendation to cache beyond the token's lifetime. The 30s
  safety margin is the only protection against a clock skew between
  this process and the workspace.

## Consequences

- Backwards-compatible: omitting the cache from the `DashboardTokenBroker`
  constructor (tests do this) falls through to the original
  three-leg-every-time path.
- The cache grows without bound under churn, which is fine for the
  workload (one entry per (tenant, dashboard, viewer) tuple, with
  bounded tenant and dashboard counts in any one deployment). A
  consumer that needs eviction pressure implements
  `IEmbedTokenCache` over `IMemoryCache` with size limits.
- The `MemoryWorkspaceTokenCache` and `MemoryEmbedTokenCache` classes
  are `internal`; the public surface is the two interfaces. The test
  project accesses them via `InternalsVisibleTo`.
- A consumer that registers a custom implementation *before* calling
  `AddLakeWrightDashboardEmbedding` keeps it; the default uses
  `TryAddSingleton`.

## Verification

- `dotnet build LakeWright.slnx` clean.
- 8 new unit tests under
  `tests/LakeWright.TenantIsolation.Tests/EmbedTokenBrokerCacheTests`:
  - second open for the same viewer makes zero HTTP calls
  - different viewer / dashboard / tenant / scope-version misses
  - workspace token shared across tenants/dashboards/viewers
  - 20 concurrent callers for the same key collapse to one exchange
  - advancing the clock past `ExpiresAt - 30s` triggers a refresh
- 7 existing `EmbedToken` unit tests (no-cache path) still pass.
- Other test failures in the suite are environmental: Testcontainers
  (Docker not running) and `LiveEmbeddingTests` (no live workspace).
  They are unaffected by this change.
