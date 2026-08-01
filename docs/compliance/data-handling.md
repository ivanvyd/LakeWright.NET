# Data classification, retention and deletion

Supports the Confidentiality and Privacy Trust Services categories. See
[soc2-mapping.md](soc2-mapping.md) for the wider control set and for what this project can and
cannot claim.

An adopter owns the policy. This describes what the architecture does, so that a policy can be
written against something real.

## Classification

| Data | Where it lives | Class | Notes |
|---|---|---|---|
| Tenant analytical data | Unity Catalog, one schema per tenant | Customer confidential | Never copied into the application database |
| Organization, membership | PostgreSQL | Customer confidential | `PrincipalId` is the IdP subject, not an email |
| Operation records | PostgreSQL | Customer confidential | Holds a Databricks statement or run id, not results |
| Audit events | PostgreSQL, append-only | Customer confidential | `Detail` is JSON and must never carry credentials, tokens or query text |
| Databricks tokens | Memory only, lifetime of a request | Secret | Acquired from IMDS, never persisted or logged |
| Telemetry | OpenTelemetry backend | Operational | Carries tenant and operation ids, never values |

**Analytical results never land in the application database.** The operation record holds a
reference; the results stay in Databricks. That is a deliberate boundary and it is what keeps this
table's classification from escalating to whatever the tenant's most sensitive column is.

## Personal data

The architecture stores one identifier per person, the IdP subject, and no profile. There is no
name, email, address or preference store, because the identity provider already has them and a
second copy is a second thing to keep correct and to delete.

An adopter who adds a profile table changes this document's answer and their own privacy posture.

## Retention

| Data | Retained | Basis |
|---|---|---|
| Organization, membership | While the tenant exists | Operational necessity |
| Operation records | Configurable, default 90 days after completion | Support and dispute resolution |
| Audit events | Configurable, default 7 years | Typical audit expectation; the adopter sets it |
| Analytical data | The tenant's own retention policy | Their data, their rule |

Audit events are append-only, so retention is enforced by deletion at the database level under a
role that is allowed to, never by the application. That is the same asymmetry as everywhere else
here: the application can add history and cannot rewrite it.

## Deleting a tenant

`OrganizationState.PendingDeletion` stops reads before anything is destroyed, so deletion is
reversible until the schema drop runs. The order matters:

1. Set `PendingDeletion`. Resolution now refuses the tenant and the claim loop stops taking its work.
2. Let in-flight operations finish or cancel them. Dropping a schema underneath a running query
   fails the query in a way that is hard to attribute later.
3. Drop the tenant's Unity Catalog schema.
4. Delete `organizations`; membership and operations cascade.
5. Write the audit event.

**Audit events survive tenant deletion.** They record that the tenant existed and that it was
deleted, which is the one fact a deletion cannot be allowed to erase. They hold identifiers and
actions, not tenant content.

**Status: implemented.** `TenantLifecycle.BeginDeletionAsync` performs step 1 and `PurgeAsync` the
rest, refusing a tenant that is not pending deletion, one with work still in flight, and one that
has not been pending for the caller's stated grace period. Evidence: `TenantLifecycleTests`.

Two things the procedure does not give you. The grace period has no default — a caller passes the
window it wants, because a value chosen here would be wrong for someone — so an adopter that passes
zero gets no cooldown and no warning. And the schema drop runs before the database transaction that
deletes the rows, so a crash in between leaves the data gone, the row still `PendingDeletion`, and
no audit row saying so; a retry completes it, because the drop is idempotent, but nothing retries on
its own.

## Encryption

In transit: TLS to Databricks, to PostgreSQL and to the browser. At rest: whatever the cloud
provider's storage gives, which for both Delta storage and a managed PostgreSQL is
provider-managed encryption by default.

No application-level column encryption. Adding it for a field that is already encrypted at rest and
in transit buys little and costs key management, so it is a deliberate absence rather than an
oversight. A tenant requiring customer-managed keys is a Databricks and cloud configuration, not an
application change.
