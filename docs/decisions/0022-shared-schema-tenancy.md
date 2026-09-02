# ADR 0022: Reject generic shared-schema SQL

Status: accepted
Date: 2026-09-02
Amends: [ADR 0002](0002-enforce-tenant-isolation-in-the-query-layer.md)

## Context

Schema-per-tenant remains the default because an incorrect schema usually fails rather than reads
another tenant's data. Some products need a shared schema for a very large tenant count or for
cross-tenant aggregates. That shape is safe only when the tenant selected by the resolved context
constrains every result the warehouse returns.

## Decision

An outer predicate over a caller-projected tenant column is not an isolation boundary. Caller SQL
can project a matching constant or parameter under that alias while selecting rows from every
tenant. The same flaw applies to a caller-projected mapping key. String inspection cannot prove
the provenance of every tenant-owned relation in arbitrary SQL.

## Decision

LakeWright supports schema-per-tenant locations only. Generic shared-schema contexts, SQL wrappers,
scope-table strategies, and their dry-run tooling are removed before the 2.0.0 release. Statement
and export callers always receive catalog and schema from a membership-resolved context.

A future shared-schema design must own the source relations and predicates, or rely on an
independently enforced server-side policy. It must not accept arbitrary caller SQL as proof of row
ownership.

## Consequences

- Schema-per-tenant callers are unchanged.
- Existing shared-schema adopters must keep the policy outside the generic LakeWright statement and
  export APIs until a source-owned design is available.
- A query with an unprovable tenant predicate fails by design instead of being presented as an
  enforced library isolation guarantee.
