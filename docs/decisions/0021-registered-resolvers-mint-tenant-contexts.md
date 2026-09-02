# ADR 0021: Registered resolvers mint tenant contexts

Status: accepted
Date: 2026-09-02
Amends: [ADR 0002](0002-enforce-tenant-isolation-in-the-query-layer.md), enforcement mechanism only

## Context

Every data-reaching call takes a `TenantContext`. It has no public constructor, so possession of
one must mean that membership was checked. Previously, the internal factory was visible to the
shipped EF resolver through `InternalsVisibleTo`. That stopped forged contexts, but it also made a
product with its own membership store unable to adopt the embedding package without adopting this
repository's PostgreSQL model.

## Decision

`LakeWright.Core` exposes `ITenantContextFactory`, but keeps its implementation internal.
`AddLakeWrightTenancy<TResolver>()` creates that implementation and passes it to a registered
resolver through `ActivatorUtilities`; it never registers the factory as a service. The resolver
must declare the constructor seam or registration fails. The shipped EF resolver uses the same
path. The worker's system-owned resolution remains internal to the multitenancy assembly and uses
that resolver rather than restoring broad friend-assembly access.

## Consequences

- A product can supply a resolver over its own trusted membership store without gaining a general
  context-construction API.
- A controller, page, or background job cannot request `ITenantContextFactory` from DI.
- The isolation suite verifies both the external resolver path and the system worker's stored
  schema path.
- A resolver that does not validate membership is an explicit composition-root decision and is
  unsafe by design.
