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
- **A Signalboard image you can expose.** Signalboard authenticates any `X-Demo-User` header, so
  it refuses to start outside Development unless `Signalboard:AllowDemoAuthentication` is true.
  The template does not set that, and must not. Before pointing `containerImage` at a
  Signalboard build, replace `AddDemoAuthentication` with a real identity provider.
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
| `postgresAdminPassword` | Yes | Pass via Key Vault or a parameter file with secure-string semantics, never inline. Used only by the server and the maintenance commands; the application never receives it. |
| `postgresAppLogin` | No | Default `lakewright_app`. The role the application connects as. It must not own the tables, or the audit table is not append-only. |
| `postgresAppPassword` | Yes | The application role's password. Same handling as the administrator password. |
| `location` | No | Defaults to the resource group's location. |

## Applying it

### Locally

```bash
az login
az deployment group create \
    --resource-group <rg> \
    --template-file infra/azure-container-apps/main.bicep \
    --parameters databricksWorkspaceUrl=https://adb-... databricksCatalog=lakewright_prod \
                 postgresAdminLogin=lakewright_admin @passwords.json
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
   `DATABRICKS_CATALOG`, `POSTGRES_ADMIN_LOGIN`, `POSTGRES_ADMIN_PASSWORD`, and
   `POSTGRES_APP_PASSWORD`. The password values are loaded from a Key Vault reference in real
   deploys and supplied as a parameter file in local deploys. The workflow writes its parameter
   file from those secrets inside the job; it never passes them inline.

The OIDC handshake happens in `azure/login@v3`; the `arm-deploy@v2` step then deploys with
the token it gets back. If the federated credential's subject does not match the workflow's
running subject (for example, only `main` is registered but the workflow uses the `production`
environment), Azure login fails with an `AADSTS70021` error. The federated-credential subject
must match the environment the deploy job uses.

## After the deploy

1. Provision the application role, then run the database migration, both as the PostgreSQL
   table owner and never with the application connection string. `provision-app-role` creates
   the LakeWright tables if the database has none, then the role named by `postgresAppLogin`
   with exactly the grants the application needs: insert and read on `audit_events`, nothing
   that can amend them. It refuses a role that is a superuser or owns the tables, and it sets the
   password on every run, so run it again after rotating `postgresAppPassword`. The template
   deploy starts the Container App before this step can run, so expect the application to fail
   its database connection until the role exists. The server's firewall admits Azure addresses
   only, so run these from an Azure host, or add a rule for your own address and delete it
   afterwards:

   ```bash
   az postgres flexible-server firewall-rule create -g <resource group> --server-name <server> \
     --name operator --start-ip-address <your IP> --end-ip-address <your IP>
   az postgres flexible-server firewall-rule delete -g <resource group> --server-name <server> \
     --name operator --yes   # after the commands below
   ```

   ```bash
   export LAKEWRIGHT_MIGRATION_CONNECTION_STRING='<migration-role connection string>'
   export LAKEWRIGHT_APPLICATION_ROLE=lakewright_app
   export LAKEWRIGHT_APPLICATION_ROLE_PASSWORD='<the postgresAppPassword value>'
   dotnet run --project tools/LakeWright.DatabaseMaintenance -- provision-app-role
   dotnet run --project tools/LakeWright.DatabaseMaintenance -- migrate
   dotnet run --project tools/LakeWright.DatabaseMaintenance -- validate
   ```

   Smoke the deployed application's audit read and append paths through its restricted role. Then
   run `finalize`; run `rollback` instead if the smoke fails. Schedule `maintain` at least monthly
   with the same migration identity (daily is recommended). Its default is seven years and two
   future partitions; override those with `LAKEWRIGHT_AUDIT_RETENTION_YEARS` and
   `LAKEWRIGHT_AUDIT_FUTURE_MONTHS`. The bounded migration defaults to a 15-second lock wait,
   five-minute statement timeout, one million rows, and 2 GiB. Configure those ceilings with
   `LAKEWRIGHT_AUDIT_LOCK_TIMEOUT_SECONDS`, `LAKEWRIGHT_AUDIT_STATEMENT_TIMEOUT_SECONDS`,
   `LAKEWRIGHT_AUDIT_MAX_MIGRATION_ROWS`, and `LAKEWRIGHT_AUDIT_MAX_MIGRATION_BYTES`. The historical
   month span defaults to 120 partitions and is configured with
   `LAKEWRIGHT_AUDIT_MAX_HISTORICAL_PARTITIONS` (maximum 1,200). If rollback is used, inspect the
   retained partitioned copy and run `finalize` to clean it up before another `migrate`. Alert on a
   non-zero exit. See
   [ADR 0020](../decisions/0020-safe-audit-table-partitioning.md).

2. Grant the managed identity `CAN RUN` on the published dashboard in Databricks if the
   product is using `LakeWright.Embedding`.
3. Configure the federated credential between the managed identity and the Databricks service
   principal, if OBO is needed. The template does not create the federated credential because
   the trust direction (Azure → Databricks, or Databricks → Azure) is a platform decision the
   bundle does not own.
4. Run `databricks bundle deploy -t prod` to land the platform half, then point the container
   image at the deploy commit and re-run the workflow.

## Compatibility

The template was deployed once, on 2026-11-28, into a nonproduction subscription in centralus
with the default placeholder image, then deleted. The deploy succeeded; the Container App
answered 200 over HTTPS and redirected HTTP; and `provision-app-role`, `migrate` and `validate`
passed against the new server through a temporary operator rule. That deploy also showed the
server had no firewall rule, so nothing could reach it, the application included; the
Azure-services rule was added afterwards and has not itself been deployed. No LakeWright
application image has run on it, so the managed identity path and the database connection from
a hosted process remain unverified. [The compatibility matrix](../compatibility.md) marks the
deploy and database setup **Verified** and the hosted application path unverified (LW-C007).

PostgreSQL Flexible Server cannot be created in every region on every subscription type: the
same deploy failed in eastus2 with `The value of the 'Version' should be in: []`, and succeeded
in centralus.

The template sets `max_connections=200` on the Flexible Server and `Maximum Pool Size=12`
on the application's connection string, matching [ADR 0015](../decisions/0015-postgres-sizing-for-production.md).
The deployment guide and the load harness report against the same numbers, so a deploy that
drifts from those values is one a reviewer should challenge.

## Known limitations

- **The Log Analytics shared key is exposed in `az deployment group show` output.** Bicep's
  `listKeys().primarySharedKey` returns a non-`@secure()` value, and the deployment record
  contains it. The key grants **ingest** access to the workspace (write logs), not read
  access. A reader on the resource group sees the key. The conventional mitigations are:
  switch to a `diagnosticSetting` (which writes via Azure's control plane) and read the
  key into a Bicep variable, or move to a customer-managed key and store it in Key Vault.
  Tracked as a follow-up; the deploy guide explicitly says "not production-ready."

- **No VNet, no private endpoint, no custom domain.** The deploy guide is explicit about
  these being production prerequisites, not template omissions.
