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

**The deploy step authenticates with OIDC, not a stored secret.** Before the first deploy,
configure the following in Entra ID:

1. Register an app registration in the subscription's home tenant. Note its `client-id` and
   the home `tenant-id`.
2. On that app registration, add a **federated credential** of type "GitHub Actions deploying
   Azure resources" with the following subject: `repo:<owner>/<repo>:environment:<name>` for
   each environment the workflow targets (the example uses `production`).
3. Grant the app registration the `Contributor` role on the resource group the workflow
   deploys into.
4. In the repository or environment, define the secrets `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`,
   `AZURE_SUBSCRIPTION_ID`, `AZURE_RESOURCE_GROUP`, `DATABRICKS_WORKSPACE_URL`,
   `DATABRICKS_CATALOG`, `POSTGRES_ADMIN_LOGIN`, and `POSTGRES_ADMIN_PASSWORD`. The
   `POSTGRES_ADMIN_PASSWORD` value is loaded from a Key Vault reference in real deploys and
   supplied as a parameter file in local deploys — never inline in the workflow.

The OIDC handshake happens in `azure/login@v2`; the `arm-deploy@v1` step then deploys with
the token it gets back. If the federated credential's subject does not match the workflow's
running subject (e.g. only `main` is registered but the workflow is running on a PR), the login
step succeeds and the deploy step fails with an "AADSTS70021" error. The federated-credential
subject must include the branch or environment the workflow runs under.

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

## Known limitations

- **The Log Analytics shared key is exposed in `az deployment group show` output.** Bicep's
  `listKeys().primarySharedKey` returns a non-`@secure()` value, and the deployment record
  contains it. The key grants **ingest** access to the workspace (write logs), not read
  access. A reader on the resource group sees the key. The conventional mitigations are:
  switch to a `diagnosticSetting` (which writes via Azure's control plane) and read the
  key into a Bicep variable, or move to a customer-managed key and store it in Key Vault.
  Tracked as a follow-up; the deploy guide explicitly says "not production-ready."

- **`arm-deploy@v1` is in maintenance mode.** Microsoft's recommended successor is
  `azure/bicep-deploy`, which is first-party and supports OIDC without the action's
  quirks. Tracked as a follow-up.

- **No VNet, no private endpoint, no custom domain.** The deploy guide is explicit about
  these being production prerequisites, not template omissions.
