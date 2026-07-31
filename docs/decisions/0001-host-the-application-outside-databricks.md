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
