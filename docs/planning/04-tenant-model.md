# Tenant isolation decision matrix

Research date: 2026-07-31. Limits are quoted from Databricks docs and marked where undocumented.

## The constraint that eliminates the obvious design

Unity Catalog row filters resolve the caller with `session_user()`, evaluated as the invoker. A
single shared service principal returns one value for every request from every tenant.

Consequences, stated plainly because this is where teams lose data:

- A filter of the form `tenant_id = (SELECT tenant_id FROM map WHERE username = session_user())`
  resolves to the same row set on every call.
- Either the service principal maps to one tenant, and every other tenant sees nothing, or it is
  unmapped, and every tenant sees everything.
- `is_account_group_member()` fails the same way. The service principal sits in the union of all
  tenants' groups simultaneously, so a group-based filter grants the union on every query.

**Row filters are only a tenancy control when the connection carries a per-tenant identity.**

## The five models

| Model | Enforcement point | Scaling ceiling | Isolation | Local dev | Cost |
|---|---|---|---|---|---|
| **A. Shared table + `tenant_id`** | .NET query layer wraps each read with a bound tenant predicate | Table size only | Supported when every shared-schema read projects `tenant_id`; the library applies the resolved parameter before execution. | Trivial | Lowest, one warehouse |
| **B. Schema-per-tenant** | .NET catalog/schema resolution, plus grants | 10,000 schemas per catalog | Object-level grants are real. Wrong schema name returns an error, not another tenant's rows. | Good | Low |
| **C. Catalog-per-tenant** | Grants, optionally row filters with per-tenant SPN | ~300 with disaster recovery, 1,000 raisable | Strong. Separate storage credentials possible. | Poor beyond a handful | Medium |
| **D. Workspace-per-tenant** | Platform | Account-level | Strongest | Not reproducible in CI | Highest |
| **E. OpenSharing to customer's platform** | Delta Sharing | n/a | Strong, but the customer needs a platform | Not reproducible | Varies |

Combined ceiling on users plus service principals per account: **10,000, not raisable.** That caps
every design that gives each tenant its own identity, including B-with-SPN and C.

## Recommended default: B, schema-per-tenant, enforced in the .NET query layer

**Why.** It is the only model where the failure mode of a bug is an error rather than a disclosure.
Getting the schema name wrong yields `SCHEMA_NOT_FOUND`; getting a `WHERE` clause wrong in model A
yields another tenant's data with a 200 status. That difference is worth more than the isolation
strength of model C.

**Trade-off accepted.** Provisioning a tenant becomes a DDL operation with a failure mode and a
rollback path, not an `INSERT`. Cross-tenant analytics for our own product metrics get harder and
need a separate aggregate path.

**How enforcement works.** Tenant context resolves once, at the authentication boundary, into an
ambient `TenantContext`. The Databricks query layer takes the catalog and schema from that context
and refuses to build a statement without it. There is no API that accepts a caller-supplied schema
name. This is the single control that the whole model rests on, so it gets a dedicated cross-tenant
test suite that runs on every commit.

**Defence in depth where it is real.** For tenants on the isolated tier that have their own service
principal, row filters are added as a genuine second control, because there `session_user()` is the
tenant. For shared-tier tenants, row filters are deliberately not used: the only correct
configuration for a shared principal is "sees everything", so a filter would be decoration that
reads like a control. Decoration that reads like a control is worse than no control.

## Documented alternatives

**Model C, catalog-per-tenant**, as an isolated tier for customers who require it contractually.
Bounded at roughly 300 tenants where disaster recovery is in scope. Requires a per-tenant service
principal, which is where row filters start earning their keep.

**Model A, shared table**, is the right answer for tenants counted in tens of thousands, where B's
schema ceiling and provisioning cost dominate. The query layer wraps each shared-schema read with a
bound `tenant_id` predicate from `TenantContext`; a caller-supplied value is refused before the
warehouse is contacted (ADR 0022).

## Cost attribution

Query tags on the shared tier, apportioned by query. This is an **allocation, not a measurement**,
and the docs page must say so. A tenant's share of a shared warehouse is arithmetic over tagged
statements, not a metered cost.

Warehouse-per-tenant on the premium tier gives true measurement, at the cost of a warehouse per
tenant.

Note for the .NET path: query tags are set through the Statement Execution API or the ODBC
`ssp_query_tags` property. The Databricks query-tags documentation lists Python, Node, Go, JDBC,
ODBC and dbt; it does not list a first-class .NET connector.

## How tenant identity reaches Databricks

1. OIDC authenticates the user. Tenant membership resolves from the application database, never from
   a token claim the client could influence.
2. `TenantContext` is populated in middleware and is required by the Databricks query layer.
3. The statement is built with catalog and schema from that context, and parameters bound with the
   Statement Execution API's typed parameter support. String interpolation into SQL is a build
   failure, enforced by an analyzer rule.
4. Query tags carry the tenant ID for cost attribution.
5. Jobs receive the tenant ID as a job parameter, and the run is recorded against the operation row
   in the application database.

## How cross-tenant access is tested

A dedicated suite, not a test case:

- Every read endpoint, called with tenant A's credentials and tenant B's resource identifiers,
  asserting 404 rather than 403, so the API does not confirm existence.
- The query layer rejecting a statement built without a `TenantContext`.
- A seeded two-tenant fixture where tenant B's schema contains a canary row, asserted absent from
  every response to tenant A across the whole API surface.
- The provisioning path asserted idempotent and asserted to roll back cleanly on partial failure.

## Open questions

- Metastore-wide schema cap, distinct from the 10,000-per-catalog cap: undocumented.
- Rate limits on `/oidc/v1/token`: undocumented. Matters if per-tenant identity means an exchange per
  request, which is a reason the default model avoids it.
- Whether any .NET connector supports `query_tags` natively.
