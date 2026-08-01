# Threat model

Scope: the application tier and its path to Databricks. Databricks' own security is theirs and is
covered by their reports.

Last reviewed 2026-08-01, against the code at that date. A threat model that is not dated is a
threat model for software that no longer exists.

## What is being protected

| Asset | Why it matters |
|---|---|
| Tenant analytical data in Unity Catalog | The product exists to show it to one tenant and not another |
| Tenant membership records | They are the only source of truth for who may see what |
| Audit events | Their value is entirely in being unalterable |
| Databricks credentials | One leaked token reaches every tenant's data at once |
| Databricks compute budget | Spend is an availability and cost-of-goods problem, not only a bill |

## Trust boundaries

```
     untrusted                    │  trusted
  ─────────────────────────────── │ ─────────────────────────────────
  end user's browser              │
  tenant-supplied identifiers ────┼──▶ tenant resolution (application DB)
  tenant-supplied query values ───┼──▶ bound parameters
                                  │
                                  │  application  ──▶ Databricks (managed identity)
                                  │  application  ──▶ PostgreSQL (restricted role)
```

Everything crossing left to right is attacker-controlled. The tenant identifier in a request is a
*claim*, not a fact, and the boundary is where it becomes one.

## Threats

Ordered by what the project would lose if it happened.

### T1. One tenant reads another's data

The whole product risk. Four ways in, and the controls against each:

| Path | Control | Evidence |
|---|---|---|
| A request names another tenant | Membership resolved from the application database, never from a token claim | `CrossTenantResolutionTests` |
| Code builds a query without a tenant | `TenantScopedStatement` cannot be constructed without a `TenantContext` | `StatementScopingTests` |
| Code forges a tenant context | `TenantContextFactory` is `internal`, visible only to the resolver assembly | `A_tenant_context_cannot_be_manufactured_from_outside` |
| A statement id is used to read results | `OperationStore` binds the id to its tenant; no lookup by id alone | `OperationOwnershipTests` |

**Accepted residual:** Unity Catalog does not enforce this. With a shared service principal its row
filters are a no-op (ADR 0002), so the application tier is the only control. There is no second line
of defence, which is why the isolation suite is a required check and why it is demonstrated failing.

### T2. SQL injection into a Databricks statement

Values are bound, not interpolated; an interpolated literal does not compile. Catalog and schema are
identifiers, which have no parameter form, so they come from the tenant context and are validated.

**Accepted residual:** a statement string built at runtime by concatenation still compiles. The
compiler stops the interpolation footgun, not all dynamic SQL. See spike 02.

### T3. Databricks credential disclosure

No long-lived credential exists to disclose: the application uses a managed identity (spike 04). The
remaining exposure is sending the Entra token somewhere it should not go — specifically to presigned
result storage, which Azure rejects with HTTP 400, so the mistake fails rather than leaks.

Logging never includes SQL text, parameter values or tokens.

### T4. Audit tampering

Init-only entity, a change-tracker guard on every `SaveChanges` overload, and `REVOKE UPDATE, DELETE`
from the application role so the database refuses what C# cannot see.

**Accepted residual:** anyone with the migration role can still alter audit rows. Separating those
roles is a deployment requirement, not something code can enforce.

### T5. Cost abuse

A tenant, or a bug, drives unbounded Databricks compute.

**Partly mitigated.** `OperationWorker:MaxInFlightPerTenant` caps how many operations one tenant can
have running at once, across every worker, which is a ceiling on the compute a runaway loop can buy
before anyone notices. Warehouse auto-stop bounds the idle half. Evidence:
`OperationClaimTests.A_tenant_at_its_ceiling_is_skipped_rather_than_failed`.

What is still missing is a budget in currency. That needs Databricks billing data, which arrives
hours after the compute is spent — too late to stop anything in flight, and useful only for alerting
and chargeback after the fact. A concurrency ceiling is the control that acts in time; a spend
ceiling is the control an auditor asks for, and this project has the first and not the second.

Nothing here caps the *cost of one query*. A single operation against a large warehouse is bounded
only by the run timeout.

### T6. Denial of service against the operation queue

One tenant fills the queue and starves the rest.

**Mitigated.** The claim loop was strict oldest-first, and a performance review quantified what that
cost: with three replicas, one tenant submitting four long-running operations occupied every slot
and the others waited up to the run timeout, two hours by default.

Candidates are now ordered by how many operations that tenant already holds in flight, then by age,
so a tenant holding none is served before a tenant holding two however long the second has waited.
Within one tenant it stays oldest-first. `MaxInFlightPerTenant` caps the rest. Evidence:
`OperationClaimTests.One_tenants_backlog_does_not_starve_another`, which fails when the ordering
reverts to age alone.

One limit remains: a worker still polls a run to completion before claiming the next, so throughput
per process is one operation. That is a scaling characteristic rather than a fairness one — adding
replicas raises the ceiling and no tenant can monopolise them — but it means a deployment sized for
its steady state will queue during a burst.

### T7. Supply chain

Pinned dependency versions with committed lock files, SHA-pinned GitHub Actions, least-privilege
workflow permissions, and a vulnerable-package gate. Fork pull requests never receive secrets.

**Accepted residual:** CodeQL, Scorecard and dependency review need Advanced Security on a private
repository and are gated off until the repository is public.

## What is out of scope

Databricks platform security; the cloud provider's infrastructure; physical security; the security of
an adopter's own fork after they modify it.

## How to use this

If a change adds a path across a trust boundary, it belongs in T1–T7 or it needs a new entry, and it
needs a case in the isolation suite. If a control listed here is removed, the row must be moved to
"accepted residual" with a reason, not deleted.
