# Changelog

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versioning follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Before 1.0, minor versions may contain breaking changes. Each one is listed here with a migration
note.

## [Unreleased]

### Changed

- `LakeWright.Conversations` now requires an opaque application owner key for `AskAsync` and
  `ContinueAsync`. The library records ownership only after a conversation is created, refuses
  unrecorded or foreign-owner continuation and deletion before any workspace call, and exposes
  owner-filtered `ListAsync` and `DeleteAsync`. Migrate callers by passing their stable internal
  principal key; do not use a display name or email address. The built-in ownership store is
  process-local, so multi-replica applications must replace `IConversationOwnership` with shared
  durable storage before enabling follow-ups.

### Added

- `GenieAnswerSanitizer` removes model-supplied HTML and neutralizes markdown links by default.
  Hosts may opt in to exact HTTPS allow-list entries when their renderer is prepared to render
  those links.

## [1.2.1] — 2026-09-02

### Security

- Shared-schema statements and exports now apply a LakeWright-owned outer predicate on the resolved
  tenant column. A raw occurrence of `:tenant_id` was not sufficient evidence of isolation: an
  inert expression such as `:tenant_id IS NOT NULL` could return every row. Shared-schema callers
  must submit a single SELECT or WITH query that projects the configured tenant column; write
  statements and trailing semicolons are refused before the workspace call.
- `AddLakeWrightTenancy` no longer registers the concrete resolver type, preventing unrelated
  application code from resolving a resolver that retains the tenant-context factory. The extension
  now supports an explicit resolver lifetime while preserving the scoped default.

### Fixed

- The net8 consumer dependency check now uses a runner-provided, fail-closed assertion instead of
  silently accepting a missing `rg` executable. Dashboard inspection now returns a structured
  failure for malformed dataset entries; ops-token caching is covered through two catalog pages.
- Databricks credential ambiguity is checked after DI composition, so a `TokenCredential` registered
  after service-principal configuration no longer bypasses startup validation.

## [1.2.0] — 2026-09-02

### Added

- `LakeWright.Databricks` and `LakeWright.Conversations` now ship net8.0 assets. The stock net8
  consumer resolves `IStatementExecutor` and executes a shared-schema tenant-scoped statement
  against a loopback workspace, while CI verifies its package graph has no persistence dependency.

### Changed

- `AddLakeWrightDatabricks` is now the `LakeWright.Databricks` extension, so a worker or stock
  net8 consumer does not need the ASP.NET Core package. The former static ASP.NET Core entry point
  remains as an obsolete compatibility forwarder for existing compiled callers.

## [1.1.2] — 2026-09-02

### Security

- Removed unapproved project and environment identifiers from public XML documentation, design
  records, release evidence, and collaboration metadata. Added a private-denylist gate to CI and
  the release workflow; releases now unpack generated NuGet packages and scan their contents before
  publication.

## [1.1.1] — 2026-09-01

### Fixed

- Billing-backed cost attribution now enforces the 31-day and one-day-future report bounds in the
  public service and Databricks reader, not only at the HTTP endpoint. A process-wide configurable
  concurrency gate also bounds active and queued billing statements; saturation returns the safe,
  transient `BILLING_BUSY` code and HTTP 503 instead of growing an unbounded queue. Server-side
  submission cancellation and an uncertain-create hold prevent lost responses from silently
  returning remote-capacity admission early.
- Completed the durable publication record for 1.1.0 after verifying its signed tag, release
  workflow, package digests, build-provenance attestations, NuGet registration and an isolated
  public-package consumer.

## [1.1.0] — 2026-09-01

### Added

- Safe monthly partitioning for `audit_events`, with atomic populated-table migration,
  row-for-row validation, rollback/finalization, global `AuditEvent.Id` uniqueness, preserved
  grants and row-security policies, and configurable retention defaulting to seven years. The
  migration and recurring maintenance executable requires a distinct table-owning connection;
  the application role receives no DDL. ADR 0020.
- Opt-in billing cost attribution backed by Databricks `system.billing.usage` and
  `system.billing.list_prices`. Tenant-owned job runs are selected in PostgreSQL before one bounded
  warehouse query; fixed SQL, bound parameters, report and price-window proration, corrections,
  explicit currencies, a 500-run limit, polling deadlines and redacted upstream errors preserve
  the existing tenant and HTTP boundaries. ADR 0012.

### Changed

- Updated `Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore` to 10.0.11; the
  xUnit/VSTest packages to 4.0.0; Testcontainers.PostgreSql to 4.14.0; WireMock.Net to 2.15.0;
  Databricks setup-cli to 1.14.1; CodeQL actions to 4.37.9; and the pinned .NET 10 SDK container
  digest. All generated lock files were refreshed and the full local and hosted gates passed.

### Fixed

- Documentation checks now examine tracked and non-ignored new Markdown files instead of scanning
  ignored personal notes. This keeps local verification reproducible without omitting new release
  documentation.

## [1.0.1] — 2026-09-01

### Changed

- Consolidated the two internal in-memory token-cache implementations behind one canonical cache.
  This removes duplicate expiry and concurrency logic without changing the public API or token
  lifetime behavior (#100).

### Fixed

- `DashboardPublishGate.InspectAll` now reports each `MarkerHit.DatasetIndex` as the zero-based
  index of the dataset that produced it. `Inspect` retains its existing public signature and
  continues to report index `0` (#101).

## [1.0.0] — 2026-08-30

The first stable release. The compat promise is the one the project has been holding:
breaking changes between 1.0.0 and 2.0.0 ship behind a SemVer minor bump and a CHANGELOG
migration note. The compatibility matrix in [docs/compatibility.md](docs/compatibility.md)
remains the single source of which surface has been shown to work against a real workspace;
a `Documented` row is honest about the gap and a `Unverified` row is honest about the
unmeasured risk. ADR 0019.

A `v1.0.0` tag is the maintainer's step. The release pipeline already does the rest:
refuse an unsigned or lightweight tag, derive the version from the tag, restore, build,
test, pack, attest provenance, generate the SBOM, create the GitHub release, exchange
the OIDC token for a one-hour nuget.org key, push the packages.

### Security

- `SSH.NET` pinned to `2026.0.0` (was 2025.1.0) to clear GHSA-q939-rpr3-3284 / CVE-2026-48798,
  a high-severity advisory. The vulnerable-package gate in CI caught it on the next run after
  the advisory was published; the pin note next to `Microsoft.OpenApi` in `Directory.Packages.props`
  documents the shape.
- Tenant-scope publish gate (#96). `IDashboardTokenBroker` now signs the embed token in as
  `external_value` from a `TenantContext` rather than taking a `tenantId` parameter, so a
  caller cannot mint a token filtered to somebody else's rows. The previous
  `MintAsync(string tenantId, ...)` form is removed. Closes the string-literal bypass the
  previous shape allowed.

### Added

- `ICostAttribution` in `LakeWright.Core`, with the elapsed-time proxy
  `OperationCostAttribution` in `LakeWright.Multitenancy` as the first implementation. The proxy
  weights `operations.ClaimedAt` to `CompletedAt` by the configured warehouse SKU's DBU/hour rate
  and labels the result `CostSource.Proxy`. A product that gets a metastore-admin grant on
  `system.billing.usage` replaces the registration with its own implementation; the
  `CostSource` discriminator tells the caller which one ran. ADR 0012.
- `AddLakeWrightCostAttribution` and `MapLakeWrightCost` in `LakeWright.AspNetCore`. Opt-in
  configuration section `LakeWright:CostAttribution` carries the warehouse SKU and DBU/hour
  rate. The cost endpoint is behind the `Viewer` policy and bounded to a 31-day window.
- Reference deployment for Signalboard: `infra/azure-container-apps/main.bicep` and
  `.github/workflows/deploy-azure.yml`. The template provisions a Container App, a Log Analytics
  workspace, a PostgreSQL Flexible Server, and a user-assigned managed identity. The CI
  workflow validates the template on every PR; the deploy step is gated on a manual environment
  approval. ADR 0014.
- Sample's opt-in OpenTelemetry wiring in `samples/Signalboard/Program.cs`. Subscribes to
  `LakeWright.Multitenancy` when `Lakewright:OpenTelemetry:Enabled=true` and forwards to the
  configured OTLP endpoint. The library continues to take no OpenTelemetry dependency; the
  reference is the sample. ADR 0013.
- Multi-target `LakeWright.Embedding` and `LakeWright.Core` for `net8.0` (#92). The
  `net10.0` target is the default; `net8.0` is the explicit alternative a downstream
  product on the LTS line can take without taking a dependency on a preview SDK.
- `TenantContext.ScopeVersion` and a resolver seam (#93). A tenant whose catalog contents
  change in a way the embed cache cannot see — schema swap, re-publish with different
  filters — bumps the `ScopeVersion`, and downstream token exchanges pick up the new
  version on the next read. The broker surfaces it; the cache key is composed from it.
- Embed-token caching in `LakeWright.Embedding` (#94). The per-tenant, per-scope exchange
  result is cached for the lifetime of the issued token. A revocation bumps `ScopeVersion`
  and the next read recomputes; the path a hot dashboard takes is one in-memory read.
- `IDashboardCatalog` / `DashboardCatalog` in `LakeWright.Embedding` (#95). Lists published
  dashboards with `dashboard_id`, `display_name`, `parent_path`, `published_at`, and a
  forwarding `page_token`. Tenant assignment is left to the caller: which tenant may see
  which dashboard is application policy, and the library is right to leave it out.
- Split embed and ops service principals in `LakeWright.Embedding` (#95). The embed SP
  mints per-viewer tokens with `external_viewer_id` and `external_value`; the ops SP lists
  dashboards and drives refresh. Same workspace, two principals, two permission sets.
  `AddLakeWrightDashboardOps` registers the second `HttpClient` against
  `LakeWright:DashboardOps`. A product that only embeds never carries the ops secret.
- `ITenantScopedExport.StreamAsync` in `LakeWright.Databricks` (#97). An async stream of
  `ExportRow` over the warehouse's presigned chunk links, with the column header yielded
  first and one row per chunk in the warehouse's order. Memory profile is bounded by one
  chunk at a time; the chunk fetch is a plain `HttpClient` (the presigned SAS does not
  accept an `Authorization` header).

### Changed

- The tenant-isolation suite gains two cases: `CostAttributionTests` exercises the elapsed-time
  proxy against a real Postgres (math, boundary, empty window, inverted window), and
  `TelemetryTenantGuardTests` walks the library's source to assert no metric call site tags
  with `tenant`, `tenantid`, `organizationid`, or a recognisable variant. The cardinality-bomb
  rule that was a docstring is now a build gate.
- `docs/compatibility.md` records the cost proxy and the OTel wiring as `Documented`. A live
  workspace was not used; a real billing read remains blocked on the metastore-admin grant.
  The chunked export, the embed token cache, the dual-SP split, the publish gate, and the
  `ScopeVersion` plumbing are recorded as `Verified` against a real workspace; the catalog
  list shape is `Documented` from the response of an existing call path, not a fresh
  end-to-end run.
- `docs/security/threat-model.md` T5 updates from "partly mitigated" to "mitigated with a
  proxy", and the concurrency ceiling in `OperationWorker:MaxInFlightPerTenant` remains the
  control that acts in time.
- `release.yml`: the "Refuse a stable version" guard is removed. A tag without a hyphen no
  longer fails the workflow. A tag with a hyphen still publishes as a SemVer prerelease
  on the GitHub release, and the `Read whether the version is a prerelease` step keeps
  the classification in one place. ADR 0019.
- `Directory.Build.props`: `VersionPrefix` is `1.0.0`. The release workflow still derives
  the package version from the tag, so a local `dotnet pack` that bypasses the workflow
  produces a package with a version the next tag would not contradict.

## [0.1.2-preview.1] — 2026-08-06

The first release cut from a signed tag.

### Security

- **Release tags must be signed.** The release workflow refuses a lightweight tag (nothing to sign)
  and an unsigned annotated one, before it builds. Verification is asked of GitHub rather than done
  on the runner, because a key the runner supplies is a key an attacker controlling the runner
  supplies. `docs/guides/releasing.md` covers the one-time SSH or GPG setup.
- The coverage report distinguishes shipped libraries from the sample. Signalboard is demonstration
  code at 19.5%, and averaging it in hid that the shipped libraries sit at **85.4%** of lines under
  the full suite. It also no longer captions every run "Live tests excluded", which was hardcoded
  and became false the first time anyone ran the whole suite.
- The OpenSSF Best Practices **passing badge** (100% of 67 criteria) and the **OpenSSF Scorecard**
  score, both linked from the README. Scorecard was already publishing results; nothing surfaced them.

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
