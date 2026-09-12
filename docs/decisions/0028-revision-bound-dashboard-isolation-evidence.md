# ADR 0028: Require revision-bound, source-owned dashboard isolation evidence

Status: accepted

Date: 2026-09-12

Supersedes: the security interpretation of ADR 0025

## Context

`__aibi_external_value` is a claim value, not an isolation mechanism by itself. The previous
publish gate reported an executable occurrence of that token and its result was used as if it
proved that every tenant-owned relation was filtered. It does not. A projection, non-null check,
tautology, unused CTE, unconstrained union branch, or join filtered on the wrong relation can all
contain the token while returning rows from another tenant.

Arbitrary SQL needs relation-aware evidence. Another tokenizer or regular expression would only
create a larger list of bypasses. The public Lakeview published endpoint gives revision metadata,
not the serialized definition viewers receive, so the library cannot recover that proof itself.

## Decision

`DashboardMarkerLint` is retained as a deployment lint. It detects executable marker references
outside literals, comments, and quoted identifiers. A passing lint verdict is never a publish
approval or tenant-isolation assertion. The legacy `DashboardPublishGate` name remains as an
source-compatible wrapper with the same limited meaning.

Strict embed verification requires all of the following before a token is minted:

1. A host-owned `ITenantDashboardAssignment` authorizes the resolved tenant for the requested
   dashboard. Workspace catalog visibility, folder placement, and a browser-supplied dashboard id
   are not assignment evidence.
2. `IPublishedDashboardDefinitionReader` supplies the serialized definition that its source of
   record proves is served, never the mutable draft.
3. `IPublishedDashboardIsolationEvidenceReader` supplies a source-owned assertion that every
   tenant-owned relation is constrained. Its dashboard id, published revision timestamp, and
   SHA-256 digest must match both the platform's published metadata and the served definition.

The source-owned verifier belongs in an adopter's controlled query-template compiler, parsed-query
or lineage policy, or equivalent deployment system. When any item is absent, stale, mismatched, or
unproven, `PublishedRevisionEmbedPrecondition` fails closed before the broker contacts either token
endpoint. LakeWright does not attempt to infer tenant ownership from arbitrary dashboard SQL.

## Consequences

- Existing uses of the lint still receive author feedback, but must not make security decisions.
- Strict embedding is an explicit host composition because tenant-to-dashboard assignment and
  relation ownership are host data. Existing hosts that register only a definition reader now fail
  strict verification until they also supply revision-bound evidence and assignment.
- The default token broker remains a token-exchange primitive. A host exposing it to a browser must
  install a strict precondition; otherwise it owns the authorization and dashboard-safety boundary.
- Tests cover marker false positives, evidence digest/revision mismatches, unassigned dashboards,
  and a valid source-owned assertion. Live workspace behavior remains an adopter verification step.
