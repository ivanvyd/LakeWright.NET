# ADR 0006: No long-lived Databricks credentials anywhere

Status: accepted, amended 2026-11-28
Date: 2026-07-31

## Context

The common pattern is a Databricks personal access token or an OAuth client secret in configuration.
Both are long-lived bearer credentials that leak through logs, screenshots and repositories, and an
accelerator that demonstrates them teaches them.

## Decision

No personal access tokens in any documented path. No stored Databricks client secret in the
reference deployment.

- **Application to Databricks on Azure.** The Container App's user-assigned managed identity requests
  an Entra ID token for resource `2ff814a6-3304-4ab8-85cb-cd0e6f879c1d`, which Azure Databricks
  accepts directly as a bearer token. No Databricks secret and no federation policy.
- **CI to Databricks.** GitHub Actions OIDC through a Databricks federation policy.
- **Users to the application.** Provider-neutral `AddOpenIdConnect`. Entra ID is one configured
  provider, not the architecture.

## Consequences

The Entra path is Azure-specific. It sits behind an interface with documented AWS and GCP siblings
that use OAuth token federation, and those siblings are labelled unverified until someone runs them.

Federation policies are capped at 20 per service principal and 20 per account, which bounds how many
distinct CI identities can exist.

Both secretless paths are documented on the vendor's side and have not been executed end to end here.
They are week-one spikes with a stated kill condition: if the managed identity path does not work,
the secretless claim is withdrawn from the README rather than qualified in a footnote.

Contributors need no credential from the maintainer. This originally said they would run against a
mock server, which was never built; what exists is the Signalboard sample, which runs on a Postgres
container with no Databricks account at all. Anyone who does need a workspace uses Free Edition with
their own identity.

## Amendment: 2026-11-28, CI credentials as built

The CI decision above was not implemented. Two workflows hold stored client secrets. The `bundle`
job in `ci.yml` uses `DATABRICKS_CLIENT_SECRET` for `databricks bundle validate`, and
`live-net8-validation.yml` uses `AZURE_CLIENT_SECRET` for the live .NET 8 check. Until each moves to
GitHub Actions OIDC federation, these controls compensate:

- The `bundle` job runs only for pushes and same-repository pull requests, and installs nothing but
  the pinned Databricks CLI. The PyPI schema validator runs in a separate job that receives no
  secret.
- The live validation job runs only from the default branch and in the `live-validation`
  environment. Its secrets belong in that environment, behind a deployment-branch rule limited to
  the default branch. That repository setting is the binding control, because a dispatched branch
  supplies its own copy of the workflow file.

OIDC federation remains the target. The reference deployment and the application paths are
unchanged by this amendment.
