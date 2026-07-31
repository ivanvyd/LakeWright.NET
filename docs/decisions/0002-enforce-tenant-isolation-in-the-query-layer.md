# ADR 0002: Enforce tenant isolation in the query layer, with schema-per-tenant

Status: accepted
Date: 2026-07-31

## Context

The intuitive design is to define Unity Catalog row filters on shared tables and let the platform
enforce tenancy. It is wrong in a way that is invisible until an audit.

Row filters and column masks resolve the caller with `session_user()`, documented as
invoker-evaluated and returning the connected identity. A backend that connects with one service
principal for all tenants gets the same value on every request, so the predicate is identical for
every tenant. Either the principal maps to one tenant and everyone else sees nothing, or it is
unmapped and everyone sees everything. `is_account_group_member()` fails the same way: the principal
sits in the union of all tenants' groups at once.

Databricks states the trade-off directly for Databricks Apps, the closest official analogue: app
identity means the app filters; user identity means Unity Catalog filters. There is no general
on-behalf-of flow for an externally hosted service.

Five isolation models were compared in `docs/planning/04-tenant-model.md`.

## Decision

Schema-per-tenant within a small number of catalogs, with isolation enforced in the .NET query layer.

Tenant context resolves once at the authentication boundary from the application database, never
from a client-influenced token claim. The query layer takes catalog and schema from that context and
cannot build a statement without one. No API accepts a caller-supplied schema name.

## Consequences

The failure mode of a bug is an error rather than a disclosure. A wrong schema name yields
`SCHEMA_NOT_FOUND`; a wrong `WHERE` clause against a shared table yields another tenant's rows with a
200 status. That difference was weighted above the stronger isolation of catalog-per-tenant.

Provisioning a tenant becomes a DDL operation with a failure mode and a rollback path, not an
`INSERT`. Cross-tenant analytics for our own product metrics need a separate aggregate path.

The ceiling is 10,000 schemas per catalog, and 10,000 users plus service principals per account
combined, which caps every design that gives each tenant its own identity.

Row filters are used only for tenants that have their own service principal, where `session_user()`
genuinely is the tenant. They are deliberately absent on the shared tier: the only correct
configuration for a shared principal is "sees everything", so a filter there would be decoration
that reads like a control, which is worse than no control.

The whole model rests on one enforcement point, so it gets a dedicated cross-tenant test suite as a
required CI check rather than test cases scattered through the suite.
