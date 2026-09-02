# ADR 0022: Shared schemas receive a library-owned tenant predicate

Status: accepted
Date: 2026-09-02
Amends: [ADR 0002](0002-enforce-tenant-isolation-in-the-query-layer.md)

## Context

Schema-per-tenant remains the default because an incorrect schema usually fails rather than reads
another tenant's data. Some products need a shared schema for a very large tenant count or for
cross-tenant aggregates. That shape is safe only when the tenant selected by the resolved context
constrains every result the warehouse returns.

## Decision

`TenantContext.Location` distinguishes `SchemaPerTenant` from `SharedSchema`. A shared location
carries a configurable tenant-column name, defaulting to `tenant_id`. Before either a statement or
an export reaches the Databricks session, the query layer wraps a single SELECT or WITH query and
applies its own equality predicate against that column. The caller cannot supply the parameter; the
query layer appends the resolved tenant ID itself. The caller query must project the configured
tenant column, or the warehouse rejects it instead of returning an unconstrained result.

## Consequences

- Schema-per-tenant callers are unchanged.
- Shared-schema callers must submit a single SELECT or WITH query that projects the configured
  tenant column. The library appends the bound predicate itself.
- This protects against accidental omissions and inert parameter references. It does not make a
  malicious SQL author trusted: source that deliberately fabricates a tenant column is out of scope
  for a library that accepts caller-authored SQL, so production review still restricts query authors.
