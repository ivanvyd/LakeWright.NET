# Roadmap

Dates are targets, not commitments. This is maintained in personal time.

Two halves. **Open work** is what is next and what is blocking it. Everything after it records the
original plan and what landed against it, kept because a plan is only honest beside its outcome.

## Open work

Nothing here lives only in someone's head. Where an item has detail elsewhere, this table says
where.

| Open item | Recorded in | Blocked on |
|---|---|---|
| Cost attribution in currency | [Threat model, T5](docs/security/threat-model.md) | A metastore-admin grant on `system.billing`, and the fact that the tenant reaches compute as a job parameter rather than a tag |
| Per-tenant token metering | [Compatibility](docs/compatibility.md) | The same grant — it belongs with cost attribution |
| Offering the streaming shim upstream | [ADR 0009](docs/decisions/0009-a-separate-optional-ai-module.md) | Nothing but doing it. `LiveChatTests` holds the reproduction |
| Reference deployment to Azure Container Apps | [Compatibility](docs/compatibility.md) | Billable resources someone has to decide to create. Until then nothing has been deployed and the matrix says so |
| An observability export | [Getting started](docs/guides/getting-started.md#watching-it-in-production) | Nothing. The instruments exist and are tested; exporting them is the adopter's, and a reference setup goes with the deployment above |
| Synthetic events and cost attribution in Signalboard | This file, week 7 | The cost half shares the grant above |
| A first backlog of well-scoped issues | This file, week 8 | Nothing. The issue tracker is empty, so a contributor arriving has nowhere obvious to start |
| A demo recording | This file, week 8 | Nothing. Dropped rather than deferred |

If this table and [docs/compatibility.md](docs/compatibility.md) ever disagree, the matrix wins — it
records what was executed, and this one records what is intended.

### What nobody has checked

The table above is work someone chose not to do yet. This is the other kind: things nobody has
measured, so the honest answer to "is it right?" is that we do not know.

- **Half the test suite cannot run in CI.** The full suite is 155 tests and covers **85.4% of the
  lines in the shipped libraries** (measured 2026-08-06). CI runs 143 of them and reaches 71.7% of
  the same lines, because the Databricks integration is exercised by `Category=Live` tests that need
  a workspace and cost money. So a green CI run says a great deal about tenancy and isolation, and
  much less about the Databricks client wrappers — `LakeWright.Databricks` is 50.8% covered by the
  full suite and 6.6% by the part CI runs. The gap is not untested code; it is code CI is not
  allowed to test.
- **No independent human has read this code.** Every pull request has been opened and merged by the
  maintainer with zero approvals. The reviews have been thorough and adversarial, and they have all
  been briefed by the person whose work they reviewed, who then chose what to act on.
- **Nothing has ever been deployed.** Every claim is verified locally or against a development
  workspace. The managed-identity path in a hosted process rests on
  [spike 04](docs/planning/spike-04-managed-identity.md) alone, and the ingress half of encryption
  in transit has nothing to configure because there is no ingress.
- **Nothing is load-tested.** Fair claim ordering and the per-tenant ceiling are proven by tests over
  small data and by one review's measurements. Neither has run under real concurrency, and the
  numbers in [the threat model](docs/security/threat-model.md) come from reasoning about the queue
  rather than from watching it.
- **The streaming shim depends on undocumented behaviour.** Databricks may change the payload
  without notice, in either direction. `LiveChatTests` fails loudly if the bug disappears; nothing
  warns if it changes shape instead.
- **The addressable market is unmeasured.** The largest risk was always that the intersection of
  .NET-first, Databricks-standardised, and selling customer-facing analytics is thin. The cheap
  test — publishing the `session_user()` finding as an article and seeing whether anyone cares —
  has not been run, and everything below assumes an audience that has not been shown to exist.
  The risk register that recorded this is planning material and is not tracked here.

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
| Free Edition service principals | Can a contributor authenticate as a service principal on Free Edition? | **Closed, negative.** Free Edition has no account-level identity infrastructure, which service principal OAuth requires. The contributor story no longer depends on it: the sample needs no workspace. |
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

**Done.** The claim loop, the `BackgroundService`, the Jobs submitter and the reconciliation actor
all landed, verified against a live workspace by `Category=Live` tests. The worker is hosted:
`AddLakeWrightOperationWorker` registers it as a hosted service, and Signalboard calls it when a
workspace URL is configured — without one, operations stay Pending because nothing submits them.

**When the reconciliation actor is built it must claim atomically**, the same way `ClaimNextAsync`
does, rather than reading with `FindOrphanedForReconciliationAsync` and writing later. A slow-but-
alive worker and reconciliation can otherwise both act on one row and the later write silently
undoes the earlier. An `xmin` concurrency token was tried as an alternative and removed: the claim
is a raw `UPDATE`, so the version the change tracker holds afterwards is read mid-statement and
every subsequent write failed as a false conflict. Atomic claiming is the simpler fix and matches
what is already there.

### Weeks 5-6: operations and Databricks

The operation record, the `SKIP LOCKED` claim loop, Statement Execution with `EXTERNAL_LINKS`, async
job submission with `idempotency_token`, and the reconciliation pass for orphaned runs.

The Declarative Automation Bundle with dev and prod targets is **done**: catalog schema and
serverless warehouse, validated and deployed and destroyed against a real workspace. See
[docs/guides/deploying-databricks.md](docs/guides/deploying-databricks.md). Jobs and pipelines join
it when there is a sample for them to run.

### Week 7: the sample and the deployment

**Partly done.** Signalboard ships two seeded tenants, three people, a Blazor dashboard over the
operations API, and the same API drivable from a terminal — `docker compose up -d` plus `dotnet run`
with no Databricks account. Still open from this week: synthetic operational events, per-tenant cost
attribution, and the reference deployment to Azure Container Apps.

### Week 8: make it adoptable

**Partly done.** Documentation and the compatibility matrix landed, the optional AI module landed
because the week 1 spike passed, and the packages publish. Two things from this week did not happen
and are not scheduled: the first backlog of well-scoped issues, and the demo recording. The issue
tracker is empty, so a contributor arriving has nowhere obvious to start.

## Explicit non-goals for v0.1

- Vector Search and RAG. Standing hourly cost, no scale-to-zero, and a real tenant-isolation design
  problem. It gets its own milestone and its own ADR.
- Dashboard embedding. Databricks ships it. Reimplementing it is negative-value work. **Brokering
  the token is not embedding**: `LakeWright.Embedding` mints the scoped token a viewer needs and
  stops there. Rendering stays `@databricks/aibi-client`'s job, and this project ships no
  replacement for it.
- ~~Any NuGet package.~~ **Reversed 2026-08-05, see [ADR 0010](docs/decisions/0010-publish-prerelease-packages.md).**
  The packages were already built, attested and attached to the v0.1.0 release; what was withheld
  was the one surface a .NET developer actually searches. They publish with a prerelease suffix, so
  the version string carries the warning the non-goal used to.
- A `dotnet new` template. It ossifies the structure before we know the structure is right.
- Catalog-per-tenant and workspace-per-tenant as implemented paths. Documented, not built.
- Billing, invoicing, or payment integration.
- Multi-cloud verification. AWS and GCP paths are documented from the docs and labelled unverified.

## After v0.1

Ordered by how often the question is likely to be asked, not by how interesting it is to build.

1. Catalog-per-tenant as an implemented isolation tier, with per-tenant service principals and row
   filters as a genuine second control.
2. ~~Tenant-scoped Genie for external customers.~~ **Built 2026-08-05** as
   `LakeWright.Conversations`, and verified against a live agent. One agent per tenant, because the
   Conversation API takes no filter. [ADR 0011](docs/decisions/0011-brokered-access-as-separate-modules.md).
3. Vector Search with tenant-safe filtering.
4. Lakebase as a documented alternative to PostgreSQL, once it is generally available on Azure.
5. A stable package surface. The packages publish as prereleases today; dropping the suffix means
   committing to the API, which needs adopters first.
