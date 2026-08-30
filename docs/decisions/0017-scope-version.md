# 0017 — Optional `ScopeVersion` on `TenantContext`

- Status: Accepted
- Date: 2026-08-30
- Supersedes: nothing
- Related: gap analysis §2.2, dashboard embedding token flow

## Context

`DashboardTokenBroker.IssueAsync` minted the AI/BI embed token with
`external_value = tenantId.ToString()`. The value is signed into the token and
becomes `__aibi_external_value` in the dashboard's SQL. The vendor caches the
result for 24 hours per `(tenant, dashboard)`.

A tenant whose entitlements can change — the common case in any real SaaS — is
therefore stuck seeing its old rows for up to 24 hours after its scope
narrows. That is a correctness and privacy issue, not a performance one.

The gap analysis (2.2) called this out as the single most valuable design gap.
It also documented the constraint nobody else had noticed: `|` and `:` are
reserved in the `urn:aibi:external_data:<val>:<viewer>:<board>` claim
format; a `|` makes Databricks return `400 "Dashboard ID is missing in
token claim."` `-` and `_` collide with the GUID body. `~` is the only safe
delimiter that does not collide with the GUID and survives both halves of a
typical `(guid, hex-md5)` scope version.

## Decision

`TenantContext` grows an optional `ScopeVersion` string. The broker
composes `{tenantId}~{scopeVersion}` when present, and falls back to the
bare `{tenantId}` when null — same value the previous version produced.
`TenantContext.Create` rejects a `ScopeVersion` containing `|`, `:`, or `~`
on construction so a caller cannot smuggle a corrupted claim through.

The isolation property the previous design defended — a caller cannot
choose `external_value` from the request, only the resolver can — is
preserved. The value is composed from the `TenantContext`, which is
internally constructible only via the resolver.

## Why not let the caller pass a string?

The previous design's XML doc argued for the constraint, and the argument
still holds: a string parameter would move the isolation boundary into
every call site. Letting the *tenant* carry a version keeps the boundary
at the resolver, which is where the principal's authorisation was already
checked. The version is part of the tenant's authorisation state, not a
free-form input from the request.

## What this is not

- Not a per-tenant result-cache invalidation. The cache is owned by
  Databricks; `external_value` is the only knob the API exposes. A change
  to `ScopeVersion` produces a different `external_value`, which produces
  a different cache key.
- Not a fix for every cache problem. The vendor has separate TTLs for
  the workspace token and the per-`/tokeninfo` result; this change only
  affects the latter, which is the per-tenant one. The workspace token is
  cached in 3.1 (separate task).
- Not a recommendation to invalidate aggressively. The caller should bump
  the version only when the scope actually changes; minting a new one on
  every request would defeat the cache.

## Consequences

- Backwards-compatible: a `null` `ScopeVersion` produces the same
  `external_value` the previous version did, byte for byte.
- The reserved-character check happens once, at `Create`, so a corrupted
  version cannot reach the broker at runtime. A bypass via
  `TenantContext.Create(tenantId, catalog, schema, scopeVersion: "a|b")`
  throws `ArgumentException`.
- The broker is now coupled to `~` as the delimiter. A future change to
  the delimiter would have to update both `Create` and `IssueAsync`; the
  test `ScopeVersionRejectsReservedDelimiter` makes the constraint
  explicit and stable.

## Verification

- `dotnet build samples/Signalboard/Signalboard.csproj` clean.
- 5 new unit tests under `tests/LakeWright.TenantIsolation.Tests/`:
  reserved-char rejection (3 cases), null means no change, non-null
  composes with `~`. The composition test uses
  `FakeDashboardTokenHost` (the same stub the existing embed suite
  uses), so it is hermetic — no live workspace required.
- 6 existing `EmbedToken` unit tests still pass.
