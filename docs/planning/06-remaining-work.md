# Remaining work

Written 2026-08-01, against `main` at the Declarative Automation Bundle merge.

Two parts: four blockers that need a human, and five milestones that do not. The blockers are first
because three of them gate work in the second half, and one of them gets harder the longer it waits.

---

## Blockers

### B1. Verify service principals on Databricks Free Edition

**Why.** The contributor story says you can run this without a cloud account. Whether Free Edition
supports service principals at all is **undocumented** — the Free Edition limitations page never
mentions them, and it does say there is no access to account-level APIs. This is the single
highest-risk assumption in the project: if it is wrong, the README is wrong and the sample's auth
story changes.

**Steps.**

1. Sign up at `databricks.com/learn/free-edition` with a **personal** email, not the employer one.
   Free Edition forbids commercial use, and keeping it off a work identity avoids that argument.
2. In the workspace: **Settings → Identity and access → Service principals → Add**.
   If that section is absent or refuses, stop — that is the answer, record it.
3. Generate an OAuth secret for the service principal.
4. Get a token from the **workspace-level** endpoint. Account-level will not work; Free Edition has
   no account API.
   ```bash
   curl -u "<client-id>:<secret>" \
     https://<workspace-host>/oidc/v1/token \
     -d 'grant_type=client_credentials&scope=all-apis'
   ```
5. Call something with it:
   ```bash
   curl -H "Authorization: Bearer <token>" \
     https://<workspace-host>/api/2.0/preview/scim/v2/Me
   ```
6. Record the outcome in `docs/compatibility.md` under Free Edition, with the date.

**If it fails.** The contributor path becomes user identity (U2M or a personal access token). That
changes `CONTRIBUTING.md`, the README's "no cloud account needed" claim, and the local-development
design. Say so rather than qualifying it.

**Effort:** about 30 minutes. **Cost:** nothing.

---

### B2. Get the Databricks trademark position in writing

**Why.** There is **no public Databricks trademark policy**. `databricks.com/legal/trademark-policy`
returns 404 and the mark is absent from their 23-item legal index. Unlike Apache, the Linux
Foundation, Rust or Mozilla, Databricks publishes no OSS-facing grant, so "for Databricks" rests on
nominative fair use — a legal argument, not a permission. Partner terms separately forbid
designations "confusingly similar" to a Databricks mark.

**Steps.**

1. Email `brand@databricks.com`. Copy `press@databricks.com`, which is where the public press kit
   routes trademark questions.
2. State plainly: an independent open-source project called **Lakewright.NET**; "Databricks" appears
   only as descriptive text and as a `Lakewright.Databricks` package name; the project name contains
   no Databricks mark; no logo is used anywhere; the README carries a non-affiliation notice.
   Ask whether that is acceptable.
3. Save the reply. If it needs wording changes, they are cheap now and expensive after release.
4. If nothing comes back in four weeks, proceed with the conservative usage already in place and
   keep a record that the attempt was made and unanswered.

**Do not** wait on this to keep building. Do wait on it to make the repository public.

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

### B4. Make the repository public

**Why.** CodeQL, OpenSSF Scorecard and dependency review are gated off in
`.github/workflows/security.yml` because they need GitHub Advanced Security on a private repository
and are free on a public one. They are written and tested; they start running the moment visibility
flips, with no config change.

**Do B2 first.**

**Steps.**

1. **Redact the identifiers in `.claude/auto-dev.md`.** It is the only file in the repository
   carrying the live workspace URL and the Azure subscription ID. Neither is a credential — a
   workspace URL appears in any user's address bar — but neither belongs in a public repository
   either. Replace with placeholders and keep the real values in a gitignored local override.
   A full scan found no secrets, keys or passwords anywhere in history.
2. Flip visibility: **Settings → General → Danger Zone → Change visibility**.
3. Enable, in **Settings → Code security**: secret scanning, push protection, private vulnerability
   reporting, and Dependabot alerts.
4. Add a ruleset on `main` requiring the five checks that exist today: `build`, `test`,
   `tenant-isolation`, `docs`, `bundle`. Require a pull request. `tenant-isolation` in particular
   must be required, or a filtered-out isolation suite passes silently.
5. Confirm CodeQL, Scorecard and dependency review actually ran on the next pull request. If they
   still skip, the visibility condition in `security.yml` is wrong and needs a look.
6. Expect a low Scorecard score at first. Branch protection and signed releases are the cheap wins;
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

- A Jobs submitter in `Lakewright.Databricks`, using `idempotency_token`.
- `OperationWorker` as a `BackgroundService`: claim, submit, record the external id, poll with
  backoff, complete. Deliberately absent until now, because a worker with nothing to submit to is
  scaffolding.
- The reconciliation actor. **It must claim atomically**, the way `ClaimNextAsync` does — not read
  through `FindOrphanedForReconciliationAsync` and write later. An `xmin` concurrency token was
  tried and removed; the reasoning is in `LakewrightDbContext`.
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

### M3. Tenant lifecycle

- Provisioning: create the tenant's Unity Catalog schema, with rollback on partial failure and an
  idempotency test.
- Deletion, in the order in `docs/compliance/data-handling.md`: `PendingDeletion` first so it stays
  reversible, drain operations, drop the schema, delete rows, write the audit event.

**Done when:** deletion is implemented and the compliance mapping's *Design only* marker comes off.

### M4. The optional AI module

Two thirds of it needs no code: chat and tool calling already work through the stock OpenAI client.

- An `AddDatabricksChatClient` DI extension over `Microsoft.Extensions.AI.OpenAI`.
- **The streaming shim.** Databricks attaches `usage` to every chunk with `completion_tokens` and
  `total_tokens` null, and the OpenAI deserialiser requires numbers. A pipeline policy that repairs
  or strips it is roughly forty lines, and it is a precise upstream contribution to either
  Databricks or the .NET client. Offer it upstream.
- Per-tenant token metering. Note honestly that output tokens are unavailable on a streaming call
  until the shim exists.

**Done when:** streaming round-trips, and the upstream issue is filed whether or not it is accepted.

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

Two threats are named as unmitigated in `docs/security/threat-model.md` and should not quietly stay
that way:

- **Cost abuse (T5).** Only warehouse auto-stop limits the blast radius. Per-tenant query budgets
  and a cost ceiling belong with M2, where there is a request to reject.
- **Queue starvation (T6).** The claim loop is FIFO across all tenants with no fairness rule. One
  tenant can fill it. Belongs with M1, where the loop is being finished anyway.

## The decision worth making before M1

Risk #1 in the register is that the addressable market — .NET-first **and** Databricks-standardised
**and** selling customer-facing analytics — is thin. It is unmeasured, and every milestone above
assumes it is real.

The cheapest test is to publish the `session_user()` finding as an article first. There are four
spikes of live evidence to write from, three of which contradict the documentation. That costs a
weekend. M1 through M5 cost considerably more, and the article is worth writing regardless of what
it proves.
