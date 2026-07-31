# Roadmap

Dates are targets, not commitments. This is maintained in personal time.

## v0.1 — the eight-week milestone

The goal is one thing: an experienced .NET team reads the repository and understands how a
multi-tenant product on Databricks should be assembled, then runs it.

### Definition of done

- `docker compose up` then `dotnet run` gives a working two-tenant application with no Databricks
  account required, backed by a mock Databricks server.
- Pointing it at a Databricks Free Edition workspace runs the same application against real
  Databricks SQL and a real Lakeflow job.
- The cross-tenant isolation suite passes and fails loudly when isolation is broken on purpose.
- `databricks bundle deploy -t dev` creates the Databricks side, and `destroy` removes it.
- Every load-bearing decision has an ADR.
- The compatibility matrix states what was verified, against what, and when.

### Weeks 1-2: prove the risky parts first

The three assumptions that would invalidate the plan are tested before anything is built on them.

| Spike | Question it answers | Status |
|---|---|---|
| Statement Execution through the client library | Does `Microsoft.Azure.Databricks.Client` support parameters, `EXTERNAL_LINKS` and `ARROW_STREAM`? | **Done.** Kill condition not triggered. [spike 01](docs/planning/spike-01-statement-execution.md) |
| Interpolation guard | Can interpolated SQL be made a compile error? | **Done**, after the first attempt turned out to be inert. [spike 02](docs/planning/spike-02-interpolation-guard.md) |
| Managed identity to Databricks | Does an Entra token work as a Databricks bearer token end to end? | **Done.** A container with a user-assigned managed identity called the REST API with no stored secret; Databricks resolved the caller as the identity itself. ADR 0006 stands. [spike 04](docs/planning/spike-04-managed-identity.md) |
| Free Edition service principals | Can a contributor authenticate as a service principal on Free Edition? Undocumented. | Open. Highest-risk assumption in the contributor story. |
| `Microsoft.Extensions.AI.OpenAI` against Databricks | Does chat, streaming and tool calling round-trip? | **Done, partly.** Chat and tool calling work with no client code. Streaming fails: Databricks sends a malformed `usage` on every chunk. Module stays in v0.1; streaming needs a shim, and it is a well-evidenced upstream contribution. [spike 03](docs/planning/spike-03-openai-compatibility.md) |

Also in this window: solution skeleton, EF Core model, Postgres via Testcontainers, CI green.
**Done**, except that CI has never run a bundle job because there is no bundle yet.

### Weeks 3-4: the tenancy core

Tenant context resolution, membership model, schema-per-tenant provisioning with rollback, and the
query layer that cannot build a statement without a tenant context. The cross-tenant isolation suite
is written here, before the features it protects.

### Carried from the security review — both closed

- **`REVOKE UPDATE, DELETE ON audit_events`.** Done. `DatabaseHardening.ApplyAsync` creates the
  application role and grants it select and insert only. Proved by tests that connect as that role
  and get `insufficient_privilege` from `ExecuteDelete` and `ExecuteUpdate`.
- **Statement ownership.** Done. `OperationStore` is the only route to an external statement id and
  every lookup filters on the tenant. A caller holding another tenant's operation id gets null,
  indistinguishable from one that does not exist.

Still open here: the worker that claims operations with `SELECT ... FOR UPDATE SKIP LOCKED`, and the
reconciliation pass that matches orphaned rows to runs. The record they need now exists.

### Weeks 5-6: operations and Databricks

The operation record, the `SKIP LOCKED` claim loop, Statement Execution with `EXTERNAL_LINKS`, async
job submission with `idempotency_token`, and the reconciliation pass for orphaned runs. Declarative
Automation Bundle with dev and prod targets.

### Week 7: the sample and the deployment

Signalboard: two seeded tenants, synthetic operational events, a dashboard, one long-running
analysis, and per-tenant cost attribution. Reference deployment to Azure Container Apps.

### Week 8: make it adoptable

Documentation, compatibility matrix, the first backlog of well-scoped issues, and the demo recording.
Optional AI module if the week 1 spike passed.

## Explicit non-goals for v0.1

- Vector Search and RAG. Standing hourly cost, no scale-to-zero, and a real tenant-isolation design
  problem. It gets its own milestone and its own ADR.
- Dashboard embedding. Databricks ships it. Reimplementing it is negative-value work.
- Any NuGet package. Packages ship when they have independent value and a stable surface, not
  because folders exist.
- A `dotnet new` template. It ossifies the structure before we know the structure is right.
- Catalog-per-tenant and workspace-per-tenant as implemented paths. Documented, not built.
- Billing, invoicing, or payment integration.
- Multi-cloud verification. AWS and GCP paths are documented from the docs and labelled unverified.

## After v0.1

Ordered by how often the question is likely to be asked, not by how interesting it is to build.

1. Catalog-per-tenant as an implemented isolation tier, with per-tenant service principals and row
   filters as a genuine second control.
2. Tenant-scoped Genie for external customers. Research found this unserved in every language.
3. Vector Search with tenant-safe filtering.
4. Lakebase as a documented alternative to PostgreSQL, once it is generally available on Azure.
5. A `Lakewright.Databricks` package, if the client wrappers prove stable and independently useful.
