# ADR 0014: Reference deployment is a Bicep template, exercised by a build, not a deploy

Status: accepted
Date: 2026-08-29

## Context

The compatibility matrix records, in the Known Gaps section, that "no reference deployment" is the reason "the ingress half of encryption in transit and the managed identity path in a hosted process are both unproven outside the spikes." Two blockers: a Bicep template takes shape, and billable Azure resources take money, and a maintainer in personal time has to be the one to press the button. A template that compiles but has never been deployed is a step in the right direction; a deploy that exists but cannot be reproduced is not.

## Decision

**A Bicep template in `infra/azure-container-apps/main.bicep` provisions the smallest Azure footprint that runs Signalboard:** a Container App, a Log Analytics workspace, a PostgreSQL Flexible Server, and a user-assigned managed identity. The Databricks workspace is created by the bundle in `databricks/`, which is the platform half of the deployment and the right home for it.

**The template is exercised by `az bicep build`, not by `az deployment`.** The CI gate compiles the template against the public Bicep schema; no Azure login, no resource group, no billable resources. A future deploy uses `az deployment group create` with parameters supplied by Key Vault, and the GitHub Actions workflow in `.github/workflows/deploy-azure.yml` is the automation path. The deploy step is gated on a manual environment approval and is a no-op on pull requests that do not touch `infra/`.

**Production hardening is named in the docstring, not built.** A VNet integration, a private endpoint on PostgreSQL, a custom domain, Key Vault for the database password, and federated credentials for the managed identity are each one-line additions at the right place in `main.bicep`, and each is omitted. Shipping a reference deploy that hides them teaches the wrong shape: a deploy that does not use a private endpoint is a deploy that ships a public database, and the rest of the architecture is a footnote next to that. The companion docstring in `main.bicep` and the prose in `docs/guides/deploying-azure.md` say so.

**The CI workflow `deploy-azure.yml` validates on every PR, deploys on a manual gate.** It uses `azure/login@v2` with OIDC rather than a stored secret, mirroring the package-publish workflow. Secrets live in repository or environment secrets; the workflow reads them by name and never logs them.

## Consequences

**The compatibility matrix gains one row:** "Bicep template compiles against the public schema" is `Documented`, not `Verified`, because no one has run a deploy with it. The `deploy-azure.yml` workflow is the path to that promotion; it runs in a separate context from the test suite and can be promoted on the first successful deploy.

**Two new package references, both pinned.** `OpenTelemetry.Extensions.Hosting` and `OpenTelemetry.Exporter.OpenTelemetryProtocol` joined `Directory.Packages.props` for the sample's reference wiring (ADR 0013). The reference deployment adds no packages, only Bicep schema versions referenced inline.

**An adopter with a different deploy target (AWS, GCP, a Kubernetes cluster) reads the template and copies the shape rather than the syntax.** The shape — managed identity, log analytics, parameterised image, environment-scoped secrets — is the design; the syntax is Azure-specific. A multi-cloud reference deploy is the v0.2 backlog item it has always been.

**The compatibility matrix's Known Gaps shrinks by one row.** "No reference deployment" moves to a row in the matrix with `Documented` and a date. The remaining gaps — no cost attribution in currency, no real ingress load test, no independent human review — are unchanged.
