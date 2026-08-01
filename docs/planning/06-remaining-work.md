# Remaining work

Written 2026-08-01, against `main` at the Declarative Automation Bundle merge.

Two parts: five blockers, four of which still need a human, and five milestones that do not. The blockers are first
because three of them gate work in the second half, and one of them gets harder the longer it waits.

---

## Blockers

### B1. Verify service principals on Databricks Free Edition — CLOSED

**Answered 2026-08-01, from documentation rather than a signup.** The
[Free Edition limitations page](https://docs.databricks.com/aws/en/getting-started/free-edition-limitations)
states "No account console or account-level API access" and "Authentication is limited to email OTP,
Sign in with Google, and Sign in with Microsoft. No SSO or SCIM support." A Databricks employee
answering a Free Edition OAuth failure on the community forum put it directly: "Free Edition does not
provide access to the account console or account-level APIs, and service principal OAuth
authentication depends on that account-level identity infrastructure."

So service principal OAuth does not work on Free Edition. A workspace admin can add a service
principal, but the OAuth secret it would authenticate with is account-level, and Free Edition has no
account level to reach.

This no longer threatens the contributor story, because the story changed underneath it: Signalboard
runs on a Postgres container with no Databricks account at all. A contributor who wants a real
workspace uses their own user identity on Free Edition, or a paid workspace with a service
principal. The assumption is closed by being made irrelevant as much as by being answered.

### B2. Get the Databricks trademark position in writing

**Why.** There is **no public Databricks trademark policy**. `databricks.com/legal/trademark-policy`
returns 404 and the mark is absent from their 23-item legal index. Unlike Apache, the Linux
Foundation, Rust or Mozilla, Databricks publishes no OSS-facing grant, so "for Databricks" rests on
nominative fair use — a legal argument, not a permission. Partner terms separately forbid
designations "confusingly similar" to a Databricks mark.

**Steps.**

1. Email `brand@databricks.com`. Copy `press@databricks.com`, which is where the public press kit
   routes trademark questions.
2. State plainly: an independent open-source project called **LakeWright.NET**; "Databricks" appears
   only as descriptive text and as a `LakeWright.Databricks` package name; the project name contains
   no Databricks mark; no logo is used anywhere; the README carries a non-affiliation notice.
   Ask whether that is acceptable.
3. Save the reply. If it needs wording changes, they are cheap now and expensive after release.
4. If nothing comes back in four weeks, proceed with the conservative usage already in place and
   keep a record that the attempt was made and unanswered.

**Do not** wait on this to keep building. Do wait on it to make the repository public.

**Already mitigated, so this is prudence rather than a blocker.** The project name contains no
Databricks mark, no logo appears anywhere, `NOTICE` and the README both carry an explicit
non-affiliation notice, and every mention of Databricks is nominative. What the email buys is a
written answer instead of a legal argument.

**Effort:** 15 minutes, plus waiting.

---

### B3. Register `lakewright.dev`

**Why.** It is unregistered today. The `.com` is held by a speculator — registered 2026-07-23
through Gname, Cloudflare nameservers, no A record, a one-year registration expiring 2027-07-23.
A rename after the first release is expensive; a domain is not.

**Steps.**

1. Register `lakewright.dev` at any registrar. `.dev` is on the HSTS preload list, so it is HTTPS
   only, which is the right default for a developer tool.
2. Turn on auto-renew and registrar lock immediately. The failure mode here is forgetting.
3. Do not chase the `.com`. If you want it eventually, set a drop alert for 2027-07-23 and move on.

**Effort:** 10 minutes. **Cost:** roughly £12 a year.

---

### B4. Decide what history carries before the repository goes public

Making a repository public exposes every commit, not just the current tree. Rewriting history
afterwards does not help: forks, clones and caches keep the old objects.

A scan of all history found **no credentials** — no tokens, no keys, no client secrets, no
connection strings with real passwords. What it did find are three identifiers, redacted from the
current tree but still present in earlier commits:

| Value | What it is | Risk |
|---|---|---|
| Azure subscription id | An ARM resource-path component | Not a credential. Appears in support tickets and ARM ids routinely, and grants nothing without authentication |
| Managed identity application id | An Entra object id | Not a credential. Visible to anything the identity interacts with |
| Workspace URL | The hostname of a private workspace | Not a credential. Reaching it still requires authentication |

None is a secret, and none can be used to access anything. They do identify a personal Azure
account, which is why the current tree no longer carries them.

**The decision.** Either accept that history keeps them, which is defensible because they are
identifiers rather than secrets, or rewrite history before the repository goes public, which is
cheap now — one maintainer, no forks, no external clones — and impossible afterwards. This is a
judgement call about a personal account rather than a security finding, so it belongs to the
maintainer.

---

---

### B5. Make the repository public

**Why.** CodeQL, OpenSSF Scorecard and dependency review are gated off in
`.github/workflows/security.yml` because they need GitHub Advanced Security on a private repository
and are free on a public one. They are written and tested; they start running the moment visibility
flips, with no config change.

**Do B2 and B4 first.**

**Steps.**

1. ~~Redact the identifiers in `.claude/auto-dev.md`.~~ **Done 2026-08-01.** The current tree
   carries no workspace URL, subscription id or managed identity id. A scan of all history found no
   secrets, keys or passwords at all; what history still holds is covered by B4.
2. ~~Enable Discussions.~~ **Done 2026-08-01.** `.github/ISSUE_TEMPLATE/config.yml` routes
   "Question or idea" there, and it was off, so the contact link would have dead-ended on the first
   person who clicked it.
3. Flip visibility: **Settings → General → Danger Zone → Change visibility**.
4. Enable, in **Settings → Code security**: secret scanning, push protection, private vulnerability
   reporting, and Dependabot alerts. `SECURITY.md` and `CODE_OF_CONDUCT.md` both send reporters to
   private vulnerability reporting, so until it is on, two documents describe a channel that does
   not exist.
5. Add a ruleset on `main` requiring the five checks that exist today: `build`, `test`,
   `tenant-isolation`, `docs`, `bundle`. Require a pull request. `tenant-isolation` in particular
   must be required, or a filtered-out isolation suite passes silently.
6. Confirm CodeQL, Scorecard and dependency review actually ran on the next pull request. If they
   still skip, the visibility condition in `security.yml` is wrong and needs a look.
7. ~~Set repository topics.~~ **Done 2026-08-01**: `dotnet`, `databricks`, `multi-tenancy`, `saas`,
   `aspnetcore`, `blazor`, `unity-catalog`, `csharp`. Check the README renders and that the CI badge
   resolves once the repository is public — badge images 404 on a private repository, which is
   expected and fixes itself on the flip.
8. Expect a low Scorecard score at first. Branch protection and signed releases are the cheap wins;
   do not chase the number.

**Effort:** about 30 minutes.

---

## Milestones

Sequenced by dependency, not by appeal. Each states what "done" means, because a milestone without
an acceptance line finishes whenever someone is tired of it.

### M1. Asynchronous operations, end to end — DONE

Landed in `c6e4d2a`, with the live-workspace half of the acceptance criterion closed afterwards by
`Category=Live` tests. ADR 0005 is now a feature rather than a design.

The one part that is *not* done: nothing hosts `OperationWorker` as a running process. It is a
`BackgroundService` with no host, driven one iteration at a time by tests, and it starts running for
real in M2. "End to end" here means the library, not a process.

- A Jobs submitter in `LakeWright.Databricks`, using `idempotency_token`.
- `OperationWorker` as a `BackgroundService`: claim, submit, record the external id, poll with
  backoff, complete. Deliberately absent until now, because a worker with nothing to submit to is
  scaffolding.
- The reconciliation actor. **It must claim atomically**, the way `ClaimNextAsync` does — not read
  through `FindOrphanedForReconciliationAsync` and write later. An `xmin` concurrency token was
  tried and removed; the reasoning is in `LakeWrightDbContext`.
- Platform run states mapped into `OperationState` with an explicit unknown arm. Databricks
  documents its states as extensible, so an exhaustive switch is a future crash.

**Done when:** an operation submitted through the store reaches `Succeeded` against the live
workspace; a worker killed between submit and record leaves a row that reconciliation matches to
the real run rather than submitting a second one; and that crash case is a test, because no
happy-path test can reach it.

### M2. The application tier — mostly done

Unblocks two controls currently marked *Partial* in the compliance mapping.

- ASP.NET Core with provider-neutral OIDC. Entra ID as one configured provider, not the
  architecture.
- Tenant resolution middleware over the existing resolver.
- The operations API: `202 Accepted` with an operation URL, and a status endpoint that reads
  through `OperationStore` so ownership is enforced by the record rather than by the caller.
- Policy-based authorization with a default `[Authorize]` policy at endpoint routing, so an
  unprotected endpoint is opt-out rather than opt-in.
- A permission matrix generated from code by a test, so `docs/compliance/permissions.md` cannot
  drift from behaviour. That path is currently promised nowhere — it was removed when it turned out
  not to exist.

**Done.** Cross-tenant requests return 404 over HTTP, the isolation suite has HTTP-level cases,
and `docs/compliance/permissions.md` is generated from the routing table by a test that fails when
it drifts.

Still open in M2: per-tenant cost budgets (threat T5) and queue fairness (T6), and an identity
provider for local development so a contributor can sign in without wiring one up.

### M3. Tenant lifecycle — DONE

`TenantLifecycle.ProvisionAsync` creates the row and the Unity Catalog schema, idempotent on the
slug so a retry after a partial failure finishes the first attempt rather than minting a second
tenant. `BeginDeletionAsync` and `PurgeAsync` split deletion at the point where it stops being
reversible, and `PurgeAsync` refuses a tenant that is not pending deletion or that still has work
in flight.

The schema half sits behind `ITenantSchemaProvisioner` in `LakeWright.Core`, so the tenancy tier
still does not reference the Databricks integration, and a tenant provisions without Databricks
configured at all.

**Done:** deletion is implemented, and the *Design only* marker is off the compliance mapping.

### M4. The optional AI module — DONE

`AddDatabricksChatClient` registers Databricks model serving as an `IChatClient`. The streaming
shim strips the incomplete `usage` object Databricks attaches to every chunk, which the OpenAI
deserialiser refuses. Both halves are verified against `databricks-claude-sonnet-5`, including a
test asserting streaming still fails *without* the shim — so the day Databricks fixes the payload,
that test goes red and the shim can be deleted rather than carried forever.

Per-tenant token metering is not built. The shim makes it possible — the final chunk carries real
numbers — but metering belongs with cost attribution, which is blocked on the grant recorded under
T5 in the threat model.

**Still open:** offer the shim upstream, to Databricks or to the .NET client. It is a private fix
until then.

### M5. Signalboard

**Mostly done.** Two seeded tenants, three people, a Blazor dashboard over the operations API, and
the same API drivable from a terminal. `docker compose up -d` plus `dotnet run` gives a working
two-tenant application with no Databricks account, which was the acceptance criterion.

Still open here: synthetic operational events, per-tenant cost attribution, and jobs and pipelines
joining the bundle. The unchanged-against-a-real-workspace half is untested — the sample has only
been run against Postgres, and the managed identity path is evidenced by
[spike 04](spike-04-managed-identity.md) rather than by the sample itself.

---

## Cross-cutting, not a milestone

Both threats that `docs/security/threat-model.md` named unmitigated now have code, and what remains
of each is stated there rather than here:

- **Cost abuse (T5).** `MaxInFlightPerTenant` caps concurrent compute per tenant. A budget in
  currency still does not exist, and needs billing data that arrives too late to stop anything in
  flight — it belongs with observability, as alerting and chargeback rather than enforcement.
- **Queue starvation (T6).** Closed. The claim orders by in-flight count before age. What is left is
  a throughput limit, not a fairness one: a worker polls one run to completion before claiming
  again, so a burst queues even though no tenant can monopolise the workers.

## The decision worth making before M1

Risk #1 in the register is that the addressable market — .NET-first **and** Databricks-standardised
**and** selling customer-facing analytics — is thin. It is unmeasured, and every milestone above
assumes it is real.

The cheapest test is to publish the `session_user()` finding as an article first. There are four
spikes of live evidence to write from, three of which contradict the documentation. That costs a
weekend. M1 through M5 cost considerably more, and the article is worth writing regardless of what
it proves.
