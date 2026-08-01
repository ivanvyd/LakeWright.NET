# SOC 2 control mapping

## What this document does and does not claim

LakeWright.NET implements technical controls that map to the SOC 2 Trust Services Criteria. It
cannot itself hold a SOC 2 report.

SOC 2 is an examination of controls **at a service organization**. It applies to an organisation,
not to software, and it produces a *report* rather than a certificate. A tool can support an
organisation's controls; it cannot hold the attestation. Any project claiming to be "SOC 2
compliant" or "SOC 2 certified" is either confused or lying, and the phrasing is wrong even for a
company that holds a report.

The useful version: a team adopting this architecture starts closer to auditable than a team
starting from an empty solution, because the controls an auditor asks about are already
implemented and already produce evidence.

**Wording this project uses:** "Implements technical controls that map to the SOC 2 Trust Services
Criteria."

**Wording this project will not use:** "SOC 2 compliant", "SOC 2 certified", "SOC 2 ready" as a
badge, or any graphic implying attestation.

## Scope

Security is the only mandatory Trust Services category. It is realised through nine common criteria,
CC1 to CC9. The other four categories (Availability, Processing Integrity, Confidentiality, Privacy)
are elective.

The mapping below covers what an accelerator can implement in code. CC1 (control environment), CC3
(risk assessment) and most of CC9 (vendor risk) are organisational and cannot be shipped. They are
listed as gaps, because being explicit about what the accelerator does not do is what makes the rest
credible.

## Mapping

| Control | Criteria | Implementation and evidence artifact |
|---|---|---|
| Tenant isolation | CC6.1, CC6.3 | Query layer that cannot build a statement without a resolved tenant context, and a context that only the resolver assembly can construct (`internal` plus `InternalsVisibleTo`). Evidence: the cross-tenant isolation suite as a required CI check, with results retained per run, plus a demonstration that it fails when isolation is removed. |
| Authentication | CC6.1 | **Adopter's responsibility, by design.** The web tier requires an authenticated principal carrying a stable subject claim and refuses anonymous requests through a fallback policy, evidenced by `HttpIsolationTests`. It deliberately does not register an identity provider: choosing one for an adopter is choosing their identity architecture. Call `AddAuthentication().AddOpenIdConnect(...)` yourself. |
| Authorization | CC6.1, CC6.3 | Role policies over `MembershipRole`, enforced by `TenantRoleHandler`. The tenant-scoped route group carries Viewer as a floor, so an endpoint added without its own policy still requires membership at a role. The application-wide fallback policy asks only for an authenticated user, which is protection from anonymous callers and not from a member at the wrong role — the two were described as one until 2026-08-01. Roles are a floor: an Admin satisfies a Member policy. Evidence: [permissions.md](permissions.md), generated from the routing table by a test that fails when it drifts, plus `HttpIsolationTests` asserting a Viewer is refused a Member endpoint. |
| Audit logging | CC7.2, CC7.3 | Three actions write `audit_events` in the same transaction as the action itself: an operation started, an operation completed, and a principal asking for a tenant it cannot reach. The last matters most, because that request answers 404 and the audit row is the only trace of it. Tamper-resistance is separate and has three layers: an init-only entity, a change-tracker guard on every `SaveChanges` overload, and `REVOKE UPDATE, DELETE` from the application role so the database refuses what C# cannot see. Evidence: `AuditTrailTests` asserts each action produces its row, and `AuditLockdownTests`, connecting **as the application role**, asserts `ExecuteDelete` and `ExecuteUpdate` fail with `insufficient_privilege` while insert and select still work. The two were conflated until 2026-08-01: only the lockdown was tested, nothing wrote a row, and this cell claimed the control anyway. Note the deployment requirement: the application role must not own the tables, because an owner keeps privileges `REVOKE` does not remove. |
| Encryption in transit | CC6.7 | **Partial.** Databricks REST and SQL run over TLS by default, and that half needs no code. The ingress half (HTTPS only, HSTS, TLS 1.2 minimum) has nothing to configure yet because there is no ingress. The scheduled TLS check that would evidence it does not exist; it arrives with M2. |
| Encryption at rest | CC6.7, Confidentiality | Cloud provider storage encryption on Delta storage and the operational database, both provider-managed by default. No application-level column encryption, deliberately — see [data-handling.md](data-handling.md#encryption) for what that covers and what it does not. There is no ADR on this; an earlier version of this row cited one that was never written. |
| Secret management | CC6.1, CC6.6 | No long-lived Databricks credentials: managed identity on Azure, OIDC federation in CI. ADR 0006, proved end to end in [spike 04](../planning/spike-04-managed-identity.md). Evidence: the absence of secrets in configuration and a clean history scan. Push protection and secret scanning are **not yet enabled** — they need the repository to be public, which is blocker B4. |
| Change management | CC8.1 | Every change through a pull request with review, required status checks, and a linear audited history. Architecture changes require an ADR. Evidence: the Git history and branch protection settings. |
| Access provisioning and deprovisioning | CC6.1, CC6.2, CC6.3 | Identity lives in the IdP; group-based role assignment. The architecture deliberately has no local user store to go stale, so joiner/mover/leaver is the IdP's lifecycle. Evidence: documented as an architectural constraint. |
| Access review | CC6.2, CC6.3 | Scheduled extract of users by tenant by role with last-login, joined against `system.access.audit` for actual usage. Sign-off recorded with reviewer identity and timestamp. |
| Backup and restore | Availability A1.2, CC7.5 | Delta time travel plus scheduled `DEEP CLONE` to separate storage; point-in-time restore on the operational database. The control auditors test is the **restore**, not the backup, so a scheduled restore drill writes its result to an evidence table. |
| Incident response | CC7.3, CC7.4, CC7.5 | `SECURITY.md` with a private reporting channel and stated response expectations. GitHub security advisories as the coordinated disclosure mechanism. |
| Vulnerability management | CC7.1, CC9.1 | Dependabot, CodeQL on push and pull request, `dotnet list package --vulnerable --include-transitive` as a failing gate, OpenSSF Scorecard weekly with SARIF into the Security tab. |
| Monitoring and alerting | CC7.1, CC7.2 | **Partial.** The library publishes four `System.Diagnostics` instruments — operations started, completions by state, queue wait, refused tenant resolutions — and an activity source, named on `LakeWrightTelemetry`, plus a `/health` endpoint in the sample. It deliberately takes no OpenTelemetry dependency, so **subscribing to them, exporting them and alerting on them is the adopter's**: nothing here raises an alert on failure rate or warehouse queue depth. Evidence: `TelemetryTests` asserts each instrument records; there is no alerting to evidence. |
| Data retention and deletion | Privacy, Confidentiality | **Design only.** `OrganizationState.PendingDeletion` stops reads before anything is destroyed and is honoured by both resolution and the claim loop. The ordered procedure, classification and retention periods are in [data-handling.md](data-handling.md); the deletion itself is not implemented. |

## Reading this table honestly

Rows marked **Partial** or **Design only** are not controls yet. They are listed because leaving
them out would make the table look complete, and a control an adopter believes they inherited and
did not is worse than one they know they have to build.

Current status, so it can be checked at a glance rather than read for:

| Status | Rows |
|---|---|
| Implemented and evidenced by a test | Tenant isolation, authorization within a tenant, audit logging |
| Implemented, evidence is configuration or history | Change management, secret management (the architecture; the GitHub settings are still B4), encryption at rest |
| Partial | Authorization across tenants at the account layer (no access review or provisioning yet — see those rows), encryption in transit, vulnerability management (CodeQL and Scorecard are gated off while the repository is private), monitoring and alerting |
| Design only | Access review, backup and restore, data retention and deletion, logical access provisioning |
| Adopter's responsibility | Authentication (the seam exists; the provider is theirs) |
| Not started | Incident response beyond `SECURITY.md` |

Every row in the table above appears in this summary. Two were missing from an earlier version, which
is exactly the failure an at-a-glance table is supposed to prevent.

## What this does not cover

| Gap | Criteria | Why |
|---|---|---|
| Control environment: org structure, background checks, security training, board oversight | CC1.1 to CC1.5 | Organisational. No software can provide it. |
| Risk assessment and vendor risk management | CC3.1 to CC3.4, CC9.2 | The adopting organisation owns this. A `risk-register.md` skeleton and the SBOM-derived vendor list are provided as a starting point, nothing more. |
| Physical access | CC6.4, CC6.5 | The cloud provider's responsibility, covered by their own report. |
| Monitoring of controls, internal audit | CC4.1, CC4.2 | Organisational process. |

An adopter still needs an auditor, a scope, a period, and their own policies. This mapping shortens
the technical half of the work and does nothing for the other half.
