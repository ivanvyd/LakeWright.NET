# Deploying to Azure

A reference deployment for Signalboard lives in `infra/azure-container-apps/`. It creates a
Container App, a Log Analytics workspace, a PostgreSQL Flexible Server and a user-assigned
managed identity. The Databricks workspace itself is created by the bundle in `databricks/`,
which is the right home for the platform side of the deployment.

## What this is

- A starting point. A real production deployment needs a VNet integration, private endpoints,
  a custom domain, Key Vault for the database password, and federated credentials for the
  managed identity. Each is named in [What this is not](#what-this-is-not) below.
- A one-resource-group template. It assumes the resource group exists and that the deployer has
  permission to create resources in it.
- A billable deploy. Running it creates resources that cost real money, which is why the
  GitHub Actions workflow gates it on a manual approval.

## What this is not

- **A production reference.** VNet integration, a private endpoint on PostgreSQL, a custom
  domain, Key Vault for the database password, and federated credentials for the managed
  identity are all omitted. Each is a one-line add at the right place in `main.bicep`, but
  shipping a reference deploy that hides them teaches the wrong shape: a deploy that does not
  use a private endpoint is a deploy that ships a public database, and the rest of the
  architecture is a footnote next to that.
- **A complete deploy script.** The `az deployment` command and the GitHub Actions workflow
  in `.github/workflows/deploy-azure.yml` are how the template is applied.
- **A Databricks deploy.** The bundle in `databricks/` is the Databricks half. The two
  together form a full reference deployment; either alone is the platform side of the
  application or the application side of the platform, never both.

## Parameters

| Parameter | Required | Notes |
|---|---|---|
| `namePrefix` | No | Default `lakewright`. Resource names are `${namePrefix}-<role>`. |
| `containerImage` | No | Default `mcr.microsoft.com/dotnet/samples:aspnetapp`. Pin to an immutable tag for any real deploy. |
| `databricksWorkspaceUrl` | Yes | The full workspace URL, e.g. `https://adb-12345.67.azuredatabricks.net`. |
| `databricksCatalog` | Yes | The catalog tenant schemas live in. Must already exist. |
| `postgresAdminLogin` | Yes | Use a generated name, not a person's. |
| `postgresAdminPassword` | Yes | Pass via Key Vault or a parameter file with secure-string semantics, never inline. |
| `location` | No | Defaults to the resource group's location. |

## Applying it

### Locally

```bash
az login
az deployment group create \
    --resource-group <rg> \
    --template-file infra/azure-container-apps/main.bicep \
    --parameters databricksWorkspaceUrl=https://adb-... databricksCatalog=lakewright_prod \
                 postgresAdminLogin=lakewright_admin @password.json
```

The output includes `containerAppFqdn` (the URL the application answers at),
`managedIdentityClientId` (which goes into the Databricks app registration), and
`postgresFqdn` (which goes into the bundle as the database connection).

### Through GitHub Actions

`.github/workflows/deploy-azure.yml` builds the Bicep, runs `bicep build` to validate it, and
deploys on a manual approval to a chosen environment. The workflow is a no-op for pull
requests that don't touch `infra/`; the deploy step requires an environment that has a
required-reviewer approval configured.

## After the deploy

1. Grant the managed identity `CAN RUN` on the published dashboard in Databricks if the
   product is using `LakeWright.Embedding`.
2. Configure the federated credential between the managed identity and the Databricks service
   principal, if OBO is needed. The template does not create the federated credential because
   the trust direction (Azure → Databricks, or Databricks → Azure) is a platform decision the
   bundle does not own.
3. Run `databricks bundle deploy -t prod` to land the platform half, then point the container
   image at the deploy commit and re-run the workflow.

## Compatibility

This template has not been deployed. It compiles under `az bicep build` against the Bicep
schema versions referenced, and the resource SKUs are documented in the Azure product
documentation, but no one has run a deploy with it. Marked **Documented** in
[the compatibility matrix](../compatibility.md) for the same reason the rest of the
deployment column is empty.
