# Changelog

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versioning follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Before 1.0, minor versions may contain breaking changes. Each one is listed here with a migration
note.

## [Unreleased]

Nothing yet.

## [0.1.1-preview.1] — 2026-08-06

The first release published to nuget.org. Packages carry a prerelease suffix, so `dotnet add
package` needs `--prerelease`; see [ADR 0010](docs/decisions/0010-publish-prerelease-packages.md)
for why that is the shape and what it commits to.

### Added

- `TenantLifecycle`: provisioning and deletion. Before this, nothing in `src/` could create an
  organization — an adopter wrote rows by hand — and `PendingDeletion` stopped reads while nothing
  ever removed anything. Deletion follows the order in `docs/compliance/data-handling.md` and
  refuses a tenant that is not pending deletion or still has work in flight.
- `ITenantSchemaProvisioner` in `LakeWright.Core`, implemented by `DatabricksSchemaProvisioner`.
  The only DDL this library issues and the only statement it sends without a `TenantContext`, so
  it is a narrow interface a caller has to reach for deliberately.
- `IJobSubmitter.CancelRunAsync`. **Migration:** a custom implementation needs the new member.
- `LakeWright.AI`: `AddDatabricksChatClient` registers Databricks model serving as an `IChatClient`.
  Optional, and deliberately not part of `AddLakeWrightDatabricks` — a product that queries a
  warehouse has no reason to take an AI dependency.
- A streaming shim. Databricks attaches `usage` to every streaming chunk with `completion_tokens`
  and `total_tokens` null, which the OpenAI deserialiser refuses. The policy strips the incomplete
  object rather than zeroing it, because zeros deserialise and then lie.
- **Packages publish to nuget.org**, with a prerelease suffix so `dotnet add package` needs
  `--prerelease`. They were already built, attested and attached to the GitHub release; what was
  missing was the one surface a .NET developer searches. A release tag without a prerelease suffix
  now fails the workflow. See [ADR 0010](docs/decisions/0010-publish-prerelease-packages.md).
  Publishing is **keyless**: `NuGet/login` exchanges a GitHub OIDC token for a key valid one hour,
  against a policy naming this owner, repository and workflow file. No publishing secret is stored,
  so there is none to leak or rotate.
- Coverage measurement, reported per project by `scripts/report-coverage.py` into the CI summary
  and deliberately not gated. The first run, 2026-08-05: 46.2% of lines overall, with
  `LakeWright.Databricks` at 6.6% and `LakeWright.AI` at 41.3%, because nearly all of the Databricks
  coverage lives in `Category=Live` tests that CI excludes.
- `scripts/check-doc-claims.sh`, a CI check for documentation claims the repository contradicts.
  It found a fifth stale claim on its first run; review found a sixth it had missed, and its rules
  now match the subject of a claim rather than one phrasing of it.
- **`LakeWright.Embedding`**: `AddLakeWrightDashboardEmbedding` registers `IDashboardTokenBroker`,
  which runs the AI/BI external-embedding exchange and returns a browser-safe scoped token. The
  tenant is signed in as `external_value` from a `TenantContext` rather than taken as a parameter,
  so a caller cannot mint a token filtered to somebody else's rows. Verified against a live
  workspace on 2026-08-06: the returned JWT carries the tenant id in its payload.
- **`LakeWright.Conversations`**: `AddLakeWrightGenie` registers `IGenieConversations` over the Genie
  Conversation API, with one agent per tenant and no fallback, because that API takes no filter and
  the agent is the only tenancy boundary available. Verified against a live agent. Both modules
  reference only `LakeWright.Core`, enforced by a test.
  [ADR 0011](docs/decisions/0011-brokered-access-as-separate-modules.md).

### Changed

- The raw research, product thesis, ecosystem survey and risk register are no longer tracked.
  They were planning material rather than documentation, and they were 73% of `docs/` by volume.
  The spikes stayed, because the compatibility matrix cites them as evidence. Open work and the
  list of what nobody has checked moved into `ROADMAP.md`, which is where a public project's open
  work belongs.

### Security

- The coverage report distinguishes shipped libraries from the sample. Signalboard is demonstration
  code at 19.5%, and averaging it in hid that the shipped libraries sit at **85.4%** of lines under
  the full suite. It also no longer captions every run "Live tests excluded", which was hardcoded
  and became false the first time anyone ran the whole suite.
- The OpenSSF Best Practices **passing badge**, at 100% of 67 criteria, linked from the README.

- Property-based tests over the Unity Catalog identifier guard, the one value that reaches
  Databricks unparameterised. They are mutation-tested rather than assumed: a `$` anchor instead of
  `\z`, permitted uppercase, and a removed length ceiling each turn them red. The first version of
  them did **not** catch the `$` anchor — random strings essentially never land on "valid identifier
  plus a trailing newline" — so the hostile-suffix cases are a deterministic theory beside the
  properties, and the bug the guard's comment describes finally has a regression test.

- The sample's base images are pinned by digest, not by tag. `10.0-noble` today and `10.0-noble`
  next month are different images, so the build that was reviewed was not necessarily the image
  that shipped. Dependabot already watches that directory, so pinning does not mean freezing.
- `SECURITY.md` no longer claims the reference posture stores no long-lived credentials. It nearly
  does: `LakeWright.Embedding` needs a service principal OAuth secret, because Databricks documents
  no other credential for that exchange. One optional module, named rather than glossed.
- Recorded why neither HTTP client calls `RedactLoggedHeaders`. Since .NET 9 the factory redacts
  every header value by default, and naming headers *un-redacts* the ones you did not name — so the
  obvious hardening would have widened the exposure it appears to close.
- `SECURITY.md` states which OpenSSF Scorecard checks fail structurally for a one-person project,
  rather than leaving a reader to infer it from a score.

### Fixed

- A browser smoke-test assertion read the page twice and asked through a double negative, which
  also tripped CodeQL: `.includes('LakeWright.NET')` looks like a hostname check to
  `js/incomplete-url-substring-sanitization`, because of the dot.
- `Category=Live` tests failed with an opaque HTTP 400 (`IncorrectClaimException: Expected iss claim
  to be .../A/, but was .../B/`) when `az login` had defaulted to a different Entra tenant than the
  workspace's. `LiveCredential` honours `AZURE_TENANT_ID` now. The message named neither the
  workspace nor the fix.
- **Script injection in the release workflow.** Tag-derived values were interpolated into `run:`
  blocks as text, so a tag named `v1.0.0-$(...)` — a valid git ref — executed as shell in a job
  holding `contents: write` and, after this release, the nuget.org publish key. They are passed as
  environment variables now. Eight of the eleven sites predate this change; the key that made them
  worth exploiting did not, which is why all eleven were fixed rather than the three added here.
- **The prerelease guard accepted a stable version.** It tested for a hyphen anywhere, and SemVer
  puts build metadata after the prerelease label, so `1.0.0+exp-sha.5114f85` — stable — passed and
  would have published permanently. Build metadata is stripped before the test. The same bug was in
  the pre-existing `--prerelease` flag logic for the GitHub release.
- Six documentation claims the repository contradicted: the security workflow described as gated
  off while private, in the compatibility matrix and the threat model, when it had been public and
  running since 2026-07-31; observability described as absent in the README and the compatibility
  matrix, in a repository whose four instruments `TelemetryTests` asserts; secret scanning and push
  protection described as not yet enabled; a control deferred to a milestone that had already
  shipped without it; and the operation worker described as unhosted after
  `AddLakeWrightOperationWorker` shipped.
- A run that exceeded `RunTimeout` was abandoned, not stopped. The worker marked the operation
  failed and returned, leaving the job executing — still spending the compute the timeout exists to
  bound, and still holding the tenant's schema, which tenant deletion would then drop underneath it
  having counted the operation as finished.
- `BeginDeletionAsync` accepted a tenant still being provisioned, so a concurrent `ProvisionAsync`
  could fail with a concurrency exception for a call that did nothing wrong.
- The `AuditEvent.OrganizationId` value converter dereferenced a null it documents as valid. No
  call site writes one yet, so nothing had triggered it.
- Every GitHub release attached the entire `CHANGELOG.md`, so each repeated all the history before
  it, and a tag pushed before its heading was renamed would publish notes opening with
  `## [Unreleased]`. Notes are now the section for the tagged version, and the release fails when
  there is not one. A tag with a hyphen is published as a pre-release; `gh` does not infer that.

## [0.1.0] — 2026-08-01

First tagged release. Everything below shipped before it; the tag exists to give the work a
boundary and to prove the release pipeline, which had never been run.

Packages are built and attested but **not published to NuGet**. Build from source, or take the
artifacts attached to the release.

### Added

- The Signalboard sample: two organizations, three people, a Blazor dashboard and the same API from
  a terminal. `docker compose up -d` plus `dotnet run`, no Databricks account needed.
- Client idempotency on `POST /organizations/{organizationId}/operations`. Send an
  `Idempotency-Key` and a retried request returns the original operation instead of starting a
  second Databricks run. Unique per organization and principal; `422` if the key is reused with a
  different `kind`, `400` above 200 characters.
- An audit trail that is written rather than only guarded. Starting an operation, completing one,
  and being refused a tenant each write to `audit_events` in the same transaction as the action.
  The refusal matters most: it answers 404, so the row is the only trace of the attempt.
- `OperationWorker:MaxInFlightPerTenant`, capping the Databricks compute one tenant can hold at
  once, and fair claim ordering by in-flight count before age. Together these close threat T6 and
  bound T5.
- `AddLakeWrightDatabricks`, so tenancy, authorization and the operations API can be adopted
  without a Databricks workspace.
- Telemetry: four `System.Diagnostics` instruments covering operations started, completions by
  state, queue wait, and refused tenant resolutions. No OpenTelemetry dependency, and no tenant
  identifier on any metric — that tag is a cardinality bomb in a system built for many tenants.
- A container image for the sample and an `app` service in compose, so `docker compose up` runs the
  whole thing with no .NET SDK. Chiseled runtime, non-root, no shell, with `/health` reporting the
  database connection.
- `tests/ui/smoke.mjs`, a browser smoke test for the sample. Not part of CI.
- Screenshots in `docs/images/`, taken from the running application by that smoke test.

- `LakeWright.AspNetCore`: tenant resolution middleware that turns the organization in a route into
  a resolved context or a 404, role policies over `MembershipRole` with a fallback policy so
  endpoints are protected by omission, and the operations API (`202 Accepted` plus a poll endpoint).
  It deliberately registers no identity provider — that choice belongs to the adopter.
- `docs/compliance/permissions.md`, generated from the routing table by a test that fails when the
  committed copy drifts from the code.
- Asynchronous operations end to end (ADR 0005): a Lakeflow Jobs submitter, an `OperationWorker`
  `BackgroundService` that claims, submits, records, polls and completes, and a reconciliation pass
  that recovers a run orphaned by a worker crash. Reconciliation re-submits with the original
  idempotency token rather than searching by tag, because the Jobs API does not expose the token on
  a run.
- `Category=Live` tests exercising the Databricks clients against a real workspace, excluded from CI.
- `LakeWright.Core`: `TenantId`, `TenantContext` and the resolver contract. `TenantContext` has no
  public constructor, so holding one means a membership check ran.
- `LakeWright.Databricks`: `TenantScopedStatement`, typed `StatementParameter` factories, and
  `StatementOutcome`, which unifies the client library's two failure modes. Interpolated SQL is a
  compile error.
- `LakeWright.Multitenancy`: organization, membership and audit-event model on EF Core and Npgsql,
  with a resolver that reads membership from the database and never from a token claim.
- Cross-tenant isolation suite, shown to fail when isolation is deliberately broken
  (`docs/guides/testing-isolation.md`).
- `docs/compatibility.md`: what has been verified against a live workspace, and what has not.
- `docs/compliance/soc2-mapping.md`: technical controls mapped to Trust Services Criteria, and the
  gaps the adopting organisation owns.
- Research and planning package under `docs/planning`, covering the ecosystem survey, competitor
  analysis, tenant isolation matrix, architecture options and the eight-week delivery plan.
- Open-source baseline: Apache-2.0 license, contribution guide, security policy, governance model.
- Build configuration targeting .NET 10 with warnings as errors and deterministic output.

### Security

- `audit_events` is now append-only at the database as well as in application code.
  `DatabaseHardening.ApplyAsync` grants the application role select and insert only, closing the
  `ExecuteUpdate`/`ExecuteDelete`/raw-SQL gap that no C# guard can reach. The role must not own the
  tables, since an owner keeps privileges `REVOKE` does not remove.
- `OperationStore` binds a Databricks statement identifier to the tenant that created it, and every
  lookup filters on the tenant. A statement identifier obtained from a log line is no longer
  sufficient to read another tenant's results.

Found by an adversarial review of the first implementation, before any release.

- `TenantContextFactory` was public, so any caller could construct a `TenantContext` for any tenant
  with no membership check and query that tenant's schema. Now `internal`, visible only to the
  resolver assembly and the isolation suite.
- The `audit_events` append-only guard covered one of four save paths. The synchronous overload and
  the two-argument async overload both bypassed it. `ExecuteUpdate`, `ExecuteDelete` and raw SQL
  remain uncatchable in C# and are recorded as an open gap rather than claimed as covered.
- `IStatementExecutor.GetAsync` and `CancelAsync` accepted a bare statement id while the interface
  documentation claimed every method was tenant-scoped. They now require a `TenantContext`, and the
  documentation states where ownership is actually enforced.
- The Unity Catalog identifier pattern used `$`, which in .NET also matches before a trailing
  newline, so `tenant_a\n` validated. Now `\z`.

### Changed

- `AddLakeWright` no longer registers the Databricks clients or validates `DatabricksOptions`. Call
  `AddLakeWrightDatabricks(configuration)` for those, and `AddLakeWrightOperationWorker` now takes
  the configuration too. **Migration:** add both calls if you use Databricks; do nothing if you
  adopted only the tenancy tier, which previously could not start without a workspace configured.
- `IDatabricksTokenSource` is replaced by `Azure.Core.TokenCredential`. **Migration:** register
  `DefaultAzureCredential` on Azure, or wrap your existing token source in a `TokenCredential`.
- `OperationWorkerOptions.JobId` is replaced by `Jobs`, a map from `Operation.Kind` to job id.
  **Migration:** change `"OperationWorker": { "JobId": 123 }` to
  `"OperationWorker": { "Jobs": { "analysis": 123 } }`. A kind with no entry now fails the
  operation saying so, rather than running whichever job was configured.
- `IJobSubmitter`, `RunOutcome` and `TenantScopedJobRun` moved from `LakeWright.Databricks` to
  `LakeWright.Core` (namespace `LakeWright.Core.Jobs`), so `LakeWright.Multitenancy` no longer
  references the Databricks integration. **Migration:** update the `using`.
- `OperationStore.CreateAsync` takes a client request id, and `ClaimNextAsync` takes a per-tenant
  ceiling.

### Fixed

- The dashboard printed its own page route as the address to copy, beside a hint inviting you to
  fetch it as another tenant and observe a 404. That address 404s for everyone, so the
  demonstration proved nothing while appearing to prove the project's whole point.
- The Start button did nothing when clicked before the Blazor circuit connected, and every visit
  rendered the page, flashed `Loading.`, then rendered it again. Both were invisible to the xUnit
  suite, which reads prerendered HTML and never sees the interactive render.
- The Databricks bearer token was read once at startup and baked into a client that lives for the
  process. Under the managed identity ADR 0006 recommends, every Databricks call would fail 401
  permanently about an hour after boot, with nothing to detect it.
- An ordinary rolling deploy stranded in-flight operations as `Running` forever. Reconciliation
  claimed only rows with no run id, and once the id was recorded the only thing watching was a poll
  loop that exits on the shutdown token. Reconciliation now reclaims any uncompleted stale row and
  resumes the poll.
- Audit logging was documented and mapped to SOC 2 CC7.2 while nothing wrote a row. The
  append-only tests inserted their own synthetic rows, so they passed regardless.
- An endpoint added without its own `RequireAuthorization` was reachable by a member at any role:
  the fallback policy checks authentication and tenant resolution checks membership, neither checks
  role. The tenant route group now carries a Viewer floor, and the permission-matrix test fails on
  any tenant-scoped route with no role policy.
- The sample's documented `docker compose up -d` start produced an application that answered 500 to
  every request. Compose creates the database, and `EnsureCreatedAsync` does nothing when one
  already exists, so no tables were created.
- `OperationStore` read the system clock while the worker took an injected `TimeProvider`.
- Every successful Databricks query returned zero rows. The statement executor defaulted to
  `EXTERNAL_LINKS` while reading `DataArray`, which only `INLINE` populates. Inline is now the
  default with a row limit, and an external-link result is a distinct `LargeResult` outcome rather
  than a `Success` with an empty row list. Found by the first live test, invisible to unit tests.
- `--locked-mode` inside an XML comment made `Directory.Build.props` unloadable, so no project
  built.
- The bundle CI job pointed at a directory git cannot track, failing before its own guard ran.
- CodeQL, dependency review and Scorecard are gated on repository visibility. They require Advanced
  Security on private repositories and were failing every run.

### Notes

- The project was named `LakeSaaS.NET` during planning and renamed to `LakeWright.NET` before any
  code or package was published. The former name misdescribed a build kit as a product.
