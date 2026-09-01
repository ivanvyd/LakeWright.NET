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

**Mitigated with a proxy.** The real-time control is `OperationWorker:MaxInFlightPerTenant`, which
caps how many operations one tenant can have running at once, across every worker. That is a
ceiling on the compute a runaway loop can buy before anyone notices. Warehouse auto-stop bounds
the idle half. Evidence: `OperationClaimTests.A_tenant_at_its_ceiling_is_skipped_rather_than_failed`.

The reporting half is `ICostAttribution` (ADR 0012). The default elapsed-time proxy sums
`EXTRACT(EPOCH FROM (CompletedAt - ClaimedAt))` per kind and weights it by the configured
warehouse SKU's DBU/hour. The opt-in billing implementation instead returns DBUs and effective
list-price currency amounts from the Databricks system tables; the `CostSource` discriminator
tells the caller which one ran.

Nothing here caps the *cost of one query*. A single operation against a large warehouse is bounded
only by the run timeout — which cancels the run rather than merely abandoning it, so the timeout
is a real ceiling on a single operation's spend rather than a ceiling on how long anyone watches it.

**What a currency budget would take, established 2026-08-01 rather than assumed.** Two things
block a real billing read, and neither is the code anyone would write first:

The tenant does not reach the compute in a form billing can see. `TenantScopedJobRun` passes
`lakewright_tenant_id` as a *job parameter*, and Databricks attributes usage in
`system.billing.usage` by `custom_tags`, which come from the job or cluster definition, not from
per-run parameters. Tagging per run is not available on `RunNow`, and a job per tenant does not
scale. So attribution goes the other way. PostgreSQL first selects `operations.ExternalId` for the
resolved tenant. The Databricks query receives only those job run ids as bound parameters and
also filters the configured `workspace_id`; application code joins the returned rows to operation
kinds. PostgreSQL is never named in Databricks SQL. A provider response containing any run id not
in the tenant-owned set fails the report instead of being attributed.

Reading those tables needs a grant the default development identity does not have. Querying `system.billing.usage` as
the workspace identity returns `INSUFFICIENT_PERMISSIONS: User does not have USE SCHEMA on Schema
'system.billing'`. It is an administrative decision, so the proxy remains the default. A product
with access to both `system.billing.usage` and `system.billing.list_prices` opts in with
`AddLakeWrightBillingCostAttribution`. The code path is covered locally with fixed-query,
malformed-row, correction, ownership, polling and cancellation tests; the system-table grants and
live response shape remain workspace verification steps.

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

CodeQL, Scorecard and dependency review are free on a public repository and have run since this one
became public on 2026-07-31. They stay behind a visibility condition so that a fork kept private
does not collect three permanently red checks.

Publishing to nuget.org stores no credential: trusted publishing exchanges a GitHub OIDC token for
a key valid for one hour, against a policy bound to this owner, repository and workflow file. There
is no long-lived key to steal from the runner or from repository secrets.

**Accepted residual:** a tag push is the trigger, so anyone who can push a `v*` tag can publish
under this project's identity, and anyone who can change `release.yml` on the default branch can
change what gets published. Tag-derived values are passed to shell steps as environment variables
rather than interpolated into the script text, which closes the injection path that would otherwise
have turned a tag name into arbitrary code inside that job.

## What is out of scope

Databricks platform security; the cloud provider's infrastructure; physical security; the security of
an adopter's own fork after they modify it.

## How to use this

If a change adds a path across a trust boundary, it belongs in T1–T7 or it needs a new entry, and it
needs a case in the isolation suite. If a control listed here is removed, the row must be moved to
"accepted residual" with a reason, not deleted.
