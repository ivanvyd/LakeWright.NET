# Risk register and initial backlog

## Risks

Ordered by expected damage, not by likelihood.

| # | Risk | Why it is real | Mitigation | Cheap validation |
|---|---|---|---|---|
| 1 | The addressable market is the intersection of .NET-first, Databricks-standardised, and selling customer-facing analytics, and that intersection is small | Each set is large; the overlap is unmeasured | Size the project for one maintainer. Make each module useful alone so partial adoption counts | Publish the tenancy finding as an article before writing the code and measure whether anyone cares |
| 2 | Databricks fills the quadrant | AppKit, App Spaces, Genie App Builder and Marketplace Apps all shipped or previewed within 14 months | Position on the structural gap (external customers) rather than the feature gap | Re-read the Apps external-access documentation each release. If public access ships, re-evaluate the project |
| 3 | Preview churn outruns a solo maintainer | Query tags, AI/BI external embedding, Genie Conversation API, App Spaces and Apps user authorization are all Public Preview | Compatibility matrix with verification dates. Depend on GA surfaces on the critical path | A scheduled CI job that runs live smoke tests weekly and opens an issue on failure |
| 4 | ~~Free Edition cannot run the sample~~ | **Retired 2026-08-01.** Service principal OAuth genuinely does not work on Free Edition — Databricks confirmed account-level identity infrastructure is required and absent. The risk is retired rather than mitigated: the sample runs on a Postgres container, so no workspace is needed to contribute | — | — |
| 5 | The secretless auth claim does not survive contact | Managed identity to Databricks is documented on both sides but unexecuted here | Week-one spike. Withdraw the claim rather than qualify it | Deploy a minimal Container App and call one REST endpoint |
| 6 | The tenancy finding is wrong or already widely known | It is the project's central claim | It is sourced to Databricks' own documentation. If it is well known, the value shifts to the implementation | Search for prior art before publishing. If someone got there first, cite them |
| 7 | Scope grows into a generic .NET SaaS framework | The most common failure mode for accelerators | Written scope test in GOVERNANCE.md; the feature template asks the question | Count the issues declined. Zero declined means the test is not being applied |
| 8 | Cost of the reference deployment surprises an adopter | Serverless SQL warehouses bill per second and a poll loop can be expensive | Document warehouse auto-stop, poll intervals and the cost model | Run the sample for a week and publish the actual bill |
| 9 | `Microsoft.Azure.Databricks.Client` is abandoned | It is a dependency on the critical path | Wrapper interfaces so it can be swapped. Contribute upstream rather than fork | Check commit cadence quarterly |
| 10 | Trademark objection | No public Databricks trademark policy exists | No logo, brand-first naming, attribution line | Email brand@databricks.com before launch and keep the reply |

## First backlog

Ordered so that the risky things are settled before anything is built on them.

### Spikes, week 1, all with kill conditions

1. ~~Verify service principal auth on Databricks Free Edition.~~ **Done 2026-08-01 from the vendor's own answer rather than a signup: it does not work, and no longer matters.**
2. **Verify managed identity to Azure Databricks.** Minimal Container App, user-assigned identity,
   Entra token for `2ff814a6-3304-4ab8-85cb-cd0e6f879c1d`, one REST call. Kill condition: withdraw
   the secretless claim.
3. **Verify `Microsoft.Extensions.AI.OpenAI` against a Databricks serving endpoint.** Non-streaming
   chat, streaming, tool calling. Kill condition: the AI module leaves v0.1.
4. **Verify `Microsoft.Azure.Databricks.Client` covers Statement Execution with `EXTERNAL_LINKS`
   and typed parameters.** Kill condition: we write that one client ourselves.

### Foundation, weeks 1 to 2

5. Solution skeleton, `Directory.Packages.props` with pinned versions, `dotnet format` green in CI.
6. EF Core model for organisation, membership, subscription, operation and audit event, with the
   first migration.
7. Postgres via Testcontainers, and the test base class that gives each test a clean database.
8. Mock Databricks server (WireMock.Net) with recorded, sanitised fixtures for the endpoints in use.
9. A local OIDC provider in compose, so the sample can demonstrate real sign-in rather than a header. Postgres and the application are already there; the mock Databricks server this item also named was dropped, because the sample runs without Databricks instead of against a fake one.

### Tenancy core, weeks 3 to 4

10. Tenant resolution middleware and `TenantContext`, resolved from the application database only.
11. The Databricks query layer that cannot construct a statement without a tenant context, with an
    analyzer rule failing the build on string interpolation into SQL.
12. Schema-per-tenant provisioning with rollback on partial failure, and an idempotency test.
13. **The cross-tenant isolation suite**, written before the features it protects, wired as a
    required CI check.
14. Membership and role authorization, with 404 rather than 403 on cross-tenant resource access.

### Operations and Databricks, weeks 5 to 6

15. Operation record, state machine, and the product-facing state mapping that never leaks a platform state.
16. `BackgroundService` claim loop with `SELECT ... FOR UPDATE SKIP LOCKED`.
17. Statement Execution with `EXTERNAL_LINKS` and `ARROW_STREAM`, including stripping the
    `Authorization` header on presigned downloads.
18. Job submission with `idempotency_token`, plus the reconciliation pass for runs orphaned by a
    worker crash between submit and record.
19. Declarative Automation Bundle with dev and prod targets and service principal `run_as`.
20. Query tags for per-tenant cost attribution, with the documentation stating plainly that shared-tier
    attribution is an allocation and not a measurement.

### Good first issues

Deliberately scoped so someone unfamiliar can finish one in an evening: adding a Databricks
`error_code` to the retry classification table with a test; adding a mock-server fixture for an
endpoint not yet covered; a Problem Details mapping for one error class; documenting one Free Edition
limitation found in practice; adding a cross-tenant case for a newly added endpoint.

Each will state the file, the expected behaviour, and how to verify. An issue labelled good-first
that requires reading the whole codebase is not one.
