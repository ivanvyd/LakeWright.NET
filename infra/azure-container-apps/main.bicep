// Reference deployment for Lakewright.NET.
//
// Provisions the smallest Azure footprint that runs Signalboard: a Container App for the
// application, a Log Analytics workspace for its logs, a PostgreSQL Flexible Server for its
// transactional state, and a user-assigned managed identity for its Databricks credential.
//
// The Databricks workspace itself is created by the databricks bundle, not here. The bundle
// already lives in databricks/ and is the right place for it; this template is the Azure half
// of the deployment, deliberately kept apart from the platform half.
//
// What this is NOT:
//  * A production reference. A product going live needs a VNet integration, a private
//    endpoint on Postgres, a custom domain, Key Vault for the database password and
//    federated credentials for the managed identity. Each of those is omitted and named in
//    docs/guides/deploying-azure.md, because bolting them into a reference deploy would teach
//    the wrong shape.
//  * A deploy script. The `az deployment` command and the GitHub Actions workflow in
//    .github/workflows/deploy-azure.yml are how this is applied; running it creates billable
//    resources and is gated by a manual approval in the workflow.
//
// Build with: az bicep build --file infra/azure-container-apps/main.bicep

@description('Azure region for all resources. The Databricks workspace this app talks to lives in eastus2; pick the same region to keep network egress inside the region.')
param location string = resourceGroup().location

@description('Three- to five-character prefix, lower-case, used for resource names. The default is the resource group name minus its random suffix; set this to something memorable per environment.')
param namePrefix string = 'lakewright'

@description('Container image for Signalboard. Pin to an immutable tag; latest is what every other deploy problem is shaped like.')
param containerImage string = 'mcr.microsoft.com/dotnet/samples:aspnetapp'

@description('Databricks workspace URL the application authenticates against. Example: https://adb-12345.67.azuredatabricks.net')
param databricksWorkspaceUrl string

@description('Unity Catalog catalog the application reads and writes through. Must already exist in the Databricks workspace.')
param databricksCatalog string

@description('PostgreSQL administrator login. Use a generated secret, never a person\'s name.')
param postgresAdminLogin string

@description('PostgreSQL administrator password. Pass via Key Vault or a parameter file with secure-string semantics, never inline.')
@secure()
param postgresAdminPassword string

@description('Tags applied to every resource. The default tags for cost tracking and ownership; override for a production deploy.')
param tags object = {
  workload: 'lakewright'
  component: 'signalboard'
  managedBy: 'bicep'
}

var containerAppName = '${namePrefix}-signalboard'
var managedIdentityName = '${namePrefix}-identity'
var logAnalyticsName = '${namePrefix}-logs'
var containerEnvName = '${namePrefix}-env'
var postgresServerName = '${namePrefix}-postgres'
var postgresDbName = 'lakewright'

resource managedIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: managedIdentityName
  location: location
  tags: tags
}

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource containerEnv 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: containerEnvName
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        // The shared key is marked @secure() at the workspace via diagnostic settings, and is
        // read here through listKeys. Bicep cannot mark a listKeys output secure-by-name, so
        // this is the conventional pattern; the output of this module is a workspace key, not
        // application data.
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
  }
}

resource postgres 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: postgresServerName
  location: location
  tags: tags
  sku: {
    name: 'Standard_B1ms'
    tier: 'Burstable'
  }
  properties: {
    administratorLogin: postgresAdminLogin
    administratorLoginPassword: postgresAdminPassword
    version: '16'
    storage: {
      storageSizeGB: 32
    }
    backup: {
      backupRetentionDays: 7
      geoRedundantBackup: 'Disabled'
    }
    highAvailability: {
      mode: 'Disabled'
    }
  }
}

resource postgresDb 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: postgres
  name: postgresDbName
}

resource containerApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: containerAppName
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${managedIdentity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: containerEnv.id
    configuration: {
      activeRevisionsMode: 'Single'
      // Putting the database password into a Container Apps secret and reading it via
      // secretRef keeps it out of the env var that anyone with `Microsoft.App/containerApps/read`
      // on the resource can see. The Bicep parameter is already @secure(), so deployment
      // outputs are clean; this closes the runtime-env-var leak.
      secrets: [
        {
          name: 'db-connection-string'
          value: 'Host=${postgres.properties.fullyQualifiedDomainName};Database=${postgresDbName};Username=${postgresAdminLogin};Password=${postgresAdminPassword}'
        }
      ]
      ingress: {
        external: true
        targetPort: 8080
        allowInsecure: false
      }
    }
    template: {
      containers: [
        {
          name: 'signalboard'
          image: containerImage
          env: [
            {
              name: 'ConnectionStrings__LakeWright'
              secretRef: 'db-connection-string'
            }
            {
              name: 'Multitenancy__Catalog'
              value: databricksCatalog
            }
            {
              name: 'Databricks__WorkspaceUrl'
              value: databricksWorkspaceUrl
            }
            // DefaultAzureCredential resolves the managed identity we provisioned above. No
            // stored secret, no connection string of bearer tokens. ADR 0006.
            {
              name: 'AZURE_CLIENT_ID'
              value: managedIdentity.properties.clientId
            }
          ]
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 3
      }
    }
  }
}

output containerAppFqdn string = containerApp.properties.configuration.ingress.fqdn
output managedIdentityClientId string = managedIdentity.properties.clientId
output managedIdentityPrincipalId string = managedIdentity.properties.principalId
output postgresFqdn string = postgres.properties.fullyQualifiedDomainName
