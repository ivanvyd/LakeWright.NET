# ADR 0001: Host the application outside Databricks

Status: accepted
Date: 2026-07-31

## Context

Databricks Apps hosts web applications on the Databricks serverless platform with no separate
infrastructure. It would remove a deployment target, a hosting bill and an authentication hop. The
question was whether a customer-facing multi-tenant product can live there.

## Decision

It cannot. The application tier runs outside Databricks, as a container.

The Databricks Apps permissions documentation states: "You can't make Databricks apps public.
Anonymous access and bypassing single sign-on (SSO) are not supported." External users must be
provisioned as identities in the host's own Databricks account.

Three further constraints, any one of which would be disqualifying on its own: no custom domains,
so customers reach `<app>-<workspace-id>.<region>.databricksapps.com`; a ceiling of 2 to 4 vCPU with
multi-instance scaling still in beta; and a runtime with Python 3.11 and Node 22.16 and no .NET, no
documented container support, and a shell-less `command` field.

## Consequences

A hosting target and its cost are now ours. The reference deployment is Azure Container Apps with
scale-to-zero, which costs approximately nothing at rest, and the unit of deployment is an OCI image
so Kubernetes, Compose and ECS are the same artifact.

The premise removes an entire branch from the architecture comparison. Options were evaluated only
for externally hosted applications.

A self-contained linux-x64 .NET binary invoked through the `command` field is the one conceivable
route to running .NET inside Apps. It is undocumented and unverified, and even if it worked the auth
model still forbids customer-facing use. It is recorded here so nobody re-derives it.

Databricks Apps remains the right answer for internal tooling, and the README says so rather than
implying the product is wrong.

## Amendment: 2026-11-28, premises rechecked

Two of the three further constraints have moved, and the decision does not change:

- Compute now reaches an XLarge size of up to 12 vCPU, in some regions only. Horizontal scaling
  became generally available on 2026-09-29, with one to five instances per app.
- An app's own address is still fixed, but account-level Custom URLs are in Public Preview and
  give the account one branded domain.
- The runtime is still Python 3.11 and Node.js 22 with no .NET and no custom container.

The deciding premise is unchanged and was re-fetched verbatim: Apps cannot be made public, and every
user must be an identity in the Databricks account. The sources are in
[the 2026-11-28 evidence record](../release-evidence/databricks-platform-2026-11-28.md), and
[When not to use LakeWright](../guides/when-not-to-use-lakewright.md) turns them into a decision
table.
