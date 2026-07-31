# ADR 0006: No long-lived Databricks credentials anywhere

Status: accepted
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

Contributors run against a mock server or Free Edition with their own identity, so no contributor
needs a credential from the maintainer.
