# ADR 0012: Cost attribution from an elapsed-time proxy or correlated billing usage

Status: accepted
Date: 2026-08-29

## Context

The threat model records (T5) that the project bounds Databricks spend by capping concurrency per tenant but has no way to report cost in currency. Two things block a real billing read, and neither is the code anyone would write first: querying `system.billing.usage` requires a metastore-admin grant, and a tenant that reaches compute as a job parameter does not surface in billing data the way a tagged job would. The grant is an administrative decision, not a feature, and this project does not hold it.

That leaves two questions for the codebase. What does the cost API look like, and what does it return when the only data available is the application database? Until now there was no API, so the question was avoided.

## Decision

**An `ICostAttribution` interface in `LakeWright.Core`, with a `TenantContext` as its only resolution input.** It returns a `TenantCostSummary` carrying a window, a `CostSource` discriminator, the configured warehouse SKU, a total DBU count, and a per-kind breakdown.

**The first implementation, `OperationCostAttribution` in `LakeWright.Multitenancy`, reads the application database.** It sums `EXTRACT(EPOCH FROM (CompletedAt - ClaimedAt))` per kind, weights by the configured DBU/hour, and labels the result `CostSource.Proxy`. Only terminal-state operations are counted, because an open-ended duration is not a cost number. The aggregation runs in Postgres rather than the application: a 100k-row operations table pulled across the wire to sum in memory is the kind of "small data" that is small until it isn't.

**A billing implementation is split at the existing dependency boundary.**
`BillingCostAttribution` in `LakeWright.Multitenancy` selects tenant-owned job run ids and their
operation kinds from PostgreSQL. `IBillingUsageReader` in `LakeWright.Core` is the typed seam;
`DatabricksBillingUsageReader` in `LakeWright.Databricks` reads only those run ids from
`system.billing.usage`. `LakeWright.AspNetCore` composes both. Multitenancy therefore does not
reference the Databricks integration, and PostgreSQL is never assumed to be visible to Databricks
SQL.

**The billing query is fixed SQL with bound values.** It filters both `workspace_id` and
`usage_metadata.job_run_id`, plus timestamp and `usage_date` bounds. Run ids are chunked into bound
parameters. Correlation and the distinct-operation count happen in application code. A billing
row for an id not selected from the tenant's operations is rejected rather than ignored.

**Currency is explicit and additive.** `TenantCostSummary` and `CostByKind` retain their original
constructors and gain init-only `EstimatedListCost` collections. Each `CurrencyAmount` keeps its
currency code beside its amount; unlike currencies are never added. Cost is calculated from
`system.billing.list_prices.pricing.effective_list.default` at the price effective when the usage
ended. This is estimated effective list-price cost, not an adopter's negotiated invoice amount.
The query sums every billing record, including negative correction rows, before the application
aggregates by operation kind.

**Configuration belongs to the adopter, not the library.** A product running more than one warehouse SKU picks one as the proxy, or moves to a billing read. `CostAttributionOptions` carries the SKU and DBU/hour rate; the section is bound by `AddLakeWrightCostAttribution` rather than read by `AddLakeWright`, so a contributor working on the application without a workspace never hits a validation failure on a value they have no opinion about.

**The endpoint is opt-in via `MapLakeWrightCost`, behind the Viewer policy, with a 31-day window cap.** A customer-facing usage page that lets a tenant ask for a multi-year range is asking Postgres to sum a range nobody actually wants.

## Consequences

**The threat model updates from "partly mitigated" to "mitigated with a proxy."** The proxy is documented as such: the per-tenant report is "elapsed compute time on the configured warehouse SKU" and not currency, and the dollar number an operator would want is hours late rather than actionable in flight. The ceiling in `OperationWorker:MaxInFlightPerTenant` remains the control that acts in time; this one reports afterwards.

**`system.billing.usage` is now a shipping opt-in rather than only an extension point.** A product
with the system-table grants calls `AddLakeWrightBillingCostAttribution` after the base Databricks
registration. Workspaces without those grants keep the proxy. Billing records can arrive hours
after a run, so the billing report is eventually consistent and an absent row is not replaced with
proxy data.

**A test pins the property that the library's own instruments never carry a tenant id.** Per-tenant totals come from `operations` and `audit_events` rather than from the metrics, which is the property the cardinality-bomb rule was written to protect. A future change that adds a `tenant` or `tenantid` tag to a metric call site fails the build with the offending line.

**`CostAttribution` joins the v0.2 milestone rather than v0.1.** v0.1 was the eight-week milestone whose definition of done did not name this. The published version is `0.1.2-preview.1`; the next published version with cost attribution is `0.2.0-preview.1`, which carries a breaking-change note for the `Operation` rows the implementation reads (none, today, but documented for the next maintainer).
