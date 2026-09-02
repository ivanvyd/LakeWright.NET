# ADR 0022: Shared schemas require a bound tenant parameter

Status: accepted
Date: 2026-09-02
Amends: [ADR 0002](0002-enforce-tenant-isolation-in-the-query-layer.md)

## Context

Schema-per-tenant remains the default because an incorrect schema usually fails rather than reads
another tenant's data. Some products need a shared schema for a very large tenant count or for
cross-tenant aggregates. That shape is safe only when every SQL statement proves that it filters on
the tenant selected by the resolved context.

## Decision

`TenantContext.Location` distinguishes `SchemaPerTenant` from `SharedSchema`. A shared location
carries a configurable tenant parameter name, defaulting to `tenant_id`. Before either a statement
or an export reaches the Databricks session, the query layer scans executable SQL for that parameter.
Strings, comments, and backtick identifiers do not count. The caller cannot supply the parameter;
the query layer appends the resolved tenant ID itself. Absence or a caller override throws
`TenantScopeMissingException` before any network call.

## Consequences

- Schema-per-tenant callers are unchanged.
- Shared-schema callers must include `:tenant_id` in every data-reaching statement.
- The guard is a refusal mechanism, not a substitute for a correct predicate; review still checks
  that the predicate constrains the intended table.
