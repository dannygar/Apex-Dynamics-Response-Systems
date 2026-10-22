metadata name = 'Apex Evaluation workspace'
metadata description = '''
Runs the Evaluation workspace as one Azure Container App per Docker image:
* web     (external)  ASP.NET Core API serving the built SPA on the same origin.
* worker  (no ingress) Evaluation worker: ingestion, malware scanning, scans.
* judge   (internal)  agent-eval HTTP service; supplies the judge contract heartbeat and diagnostics.
* clamav  (internal TCP) ClamAV daemon used by the worker for malware scanning.
* migrate (job)       Applies EF Core migrations.
Scans are scored by the agent-eval Foundry hosted agent (same judge image) in the existing Foundry project;
the worker calls its Invocations endpoint with the shared user-assigned managed identity.
Tenant policy (MCAPSGov SFI-NS2.2.1) forces public network access off for SQL and Storage, so the
environment is VNet-integrated and reaches both through private endpoints with private DNS zones.
No secrets are stored; all service-to-service access uses Microsoft Entra ID.
'''

/*
  Common
*/

@description('Short environment name used to derive the resource token.')
param environmentName string

@description('Azure region for all resources in this resource group.')
param location string = resourceGroup().location

@description('When false, deploys only the foundation (identity, registry, data, network, environment) so images can be built first.')
param shouldDeployApps bool = true

/*
  Identity
*/

@description('Entra tenant that issues user tokens for the API.')
param entraTenantId string

@description('Application (client) ID of the Evaluation API registration; v2 tokens use it as audience.')
param entraApiAudience string

@description('Delegated scope every API call must carry.')
param entraRequiredScope string = 'Evaluation.Access'

@description('UPN of the Entra user that administers the SQL server.')
param sqlAdminLogin string

@description('Object ID of the Entra user that administers the SQL server.')
param sqlAdminObjectId string

/*
  Networking
*/

@description('Address space of the workload virtual network.')
param vnetAddressPrefix string = '10.40.0.0/16'

@description('Delegated infrastructure subnet of the consumption-only Container Apps environment (/23 or larger).')
param appsSubnetPrefix string = '10.40.0.0/23'

@description('Delegated infrastructure subnet used by the current Container Apps environment instance (/23 or larger).')
param apps2SubnetPrefix string = '10.40.4.0/23'

@description('Subnet hosting the SQL and Blob private endpoints.')
param endpointsSubnetPrefix string = '10.40.2.0/24'

/*
  Judge model (existing Azure OpenAI deployment)
*/

@description('Name of the existing Azure AI Services account hosting the judge model deployment.')
param judgeAccountName string

@description('Resource group of the existing judge model account.')
param judgeResourceGroupName string

@description('Endpoint of the existing judge model account.')
param judgeEndpoint string

@description('Judge model deployment name.')
param judgeDeployment string

@description('Azure OpenAI data-plane API version used by agent-eval.')
param judgeApiVersion string

@description('Judge calls per artifact; agent-eval keeps the median.')
@minValue(1)
param judgeSamples int = 1

/*
  Foundry hosted agent (existing project)
*/

@description('Name of the existing Foundry account that owns the agent project.')
param foundryAccountName string

@description('Name of the existing Foundry project that hosts the judge agent.')
param foundryProjectName string

@description('Resource group of the existing Foundry account.')
param foundryResourceGroupName string

@description('Principal ID of the Foundry project managed identity; it pulls the agent image from the registry.')
param foundryProjectPrincipalId string

@description('Name of the agent-eval hosted agent the worker invokes.')
param foundryAgentName string

/*
  Compute
*/

@description('Image for the web (API + SPA) container app.')
param webImage string?

@description('Image for the worker container app and migration job.')
param workerImage string?

@description('Image for the internal judge container app and the Foundry hosted agent.')
param judgeImage string?

@description('Image for the internal ClamAV container app.')
param clamavImage string?

/*
  Variables
*/

var resourceToken = toLower(uniqueString(subscription().id, resourceGroup().id, location, environmentName))
var tags = { 'azd-env-name': environmentName, workload: 'apex-evaluation' }
var databaseName = 'evaluation'
var blobContainerName = 'evaluation-private'
var foundryProjectEndpoint = 'https://${foundryAccountName}.services.ai.azure.com/api/projects/${foundryProjectName}'

/*
  Identity and registry
*/

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: 'azid${resourceToken}'
  location: location
  tags: tags
}

// Public endpoint is required: Foundry projects created before 2026-06-25 pull hosted-agent images publicly.
resource registry 'Microsoft.ContainerRegistry/registries@2025-11-01' = {
  name: 'azcr${resourceToken}'
  location: location
  tags: tags
  sku: { name: 'Basic' }
  properties: {
    adminUserEnabled: false
    anonymousPullEnabled: false
    publicNetworkAccess: 'Enabled'
    policies: {
      // Hosted agents authenticate to ACR with ARM-scoped Entra tokens.
      azureADAuthenticationAsArmPolicy: { status: 'enabled' }
    }
  }
}

var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'

resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, identity.id, acrPullRoleId)
  scope: registry
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
  }
}

// AcrPull, not Repository Reader: repository-scoped roles apply only to ABAC-mode registries.
resource foundryImagePull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, foundryProjectPrincipalId, acrPullRoleId)
  scope: registry
  properties: {
    principalId: foundryProjectPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
  }
}

/*
  Storage: private documents and extracted text
*/

resource storage 'Microsoft.Storage/storageAccounts@2026-09-01' = {
  name: 'azst${resourceToken}'
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    minimumTlsVersion: 'TLS1_2'
    publicNetworkAccess: 'Disabled'
    networkAcls: { defaultAction: 'Deny', bypass: 'None' }
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2026-09-01' = {
  parent: storage
  name: 'default'
  properties: {
    deleteRetentionPolicy: { enabled: true, days: 7 }
    containerDeleteRetentionPolicy: { enabled: true, days: 7 }
  }
}

resource blobContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2026-09-01' = {
  parent: blobService
  name: blobContainerName
  properties: { publicAccess: 'None' }
}

var blobContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'

resource blobAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(blobContainer.id, identity.id, blobContributorRoleId)
  scope: blobContainer
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', blobContributorRoleId)
  }
}

/*
  Database: Entra-only Azure SQL
*/

resource sqlServer 'Microsoft.Sql/servers@2025-01-01' = {
  name: 'azsql${resourceToken}'
  location: location
  tags: tags
  properties: {
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Disabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      azureADOnlyAuthentication: true
      login: sqlAdminLogin
      sid: sqlAdminObjectId
      tenantId: entraTenantId
      principalType: 'User'
    }
  }
}

resource database 'Microsoft.Sql/servers/databases@2025-01-01' = {
  parent: sqlServer
  name: databaseName
  location: location
  tags: tags
  sku: { name: 'Basic', tier: 'Basic' }
  properties: {
    requestedBackupStorageRedundancy: 'Local'
  }
}

/*
  Private networking: VNet, private endpoints, and DNS for SQL and Blob
*/

resource vnet 'Microsoft.Network/virtualNetworks@2026-05-01' = {
  name: 'azvn${resourceToken}'
  location: location
  tags: tags
  properties: {
    addressSpace: { addressPrefixes: [vnetAddressPrefix] }
    subnets: [
      {
        // New VNet environments require the infrastructure subnet to be delegated to Microsoft.App/environments.
        name: 'apps'
        properties: {
          addressPrefix: appsSubnetPrefix
          delegations: [{ name: 'containerapps', properties: { serviceName: 'Microsoft.App/environments' } }]
        }
      }
      {
        name: 'endpoints'
        properties: { addressPrefix: endpointsSubnetPrefix }
      }
      {
        // Retained 'apps' stays bound to a prior environment until platform deletion completes.
        name: 'apps2'
        properties: {
          addressPrefix: apps2SubnetPrefix
          delegations: [{ name: 'containerapps', properties: { serviceName: 'Microsoft.App/environments' } }]
        }
      }
    ]
  }
}

var privateLinks = [
  { name: 'sql', groupId: 'sqlServer', target: sqlServer.id, zone: 'privatelink${az.environment().suffixes.sqlServerHostname}' }
  { name: 'blob', groupId: 'blob', target: storage.id, zone: 'privatelink.blob.${az.environment().suffixes.storage}' }
]

resource dnsZones 'Microsoft.Network/privateDnsZones@2024-06-01' = [
  for link in privateLinks: {
    name: link.zone
    location: 'global'
    tags: tags
  }
]

resource dnsLinks 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = [
  for (link, i) in privateLinks: {
    parent: dnsZones[i]
    name: 'azvl${resourceToken}'
    location: 'global'
    tags: tags
    properties: {
      registrationEnabled: false
      virtualNetwork: { id: vnet.id }
    }
  }
]

resource privateEndpoints 'Microsoft.Network/privateEndpoints@2026-05-01' = [
  for (link, i) in privateLinks: {
    name: 'azpe${link.name}${resourceToken}'
    location: location
    tags: tags
    properties: {
      subnet: { id: vnet.properties.subnets[1].id }
      privateLinkServiceConnections: [
        { name: link.name, properties: { privateLinkServiceId: link.target, groupIds: [link.groupId] } }
      ]
    }
  }
]

resource dnsZoneGroups 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2026-05-01' = [
  for (link, i) in privateLinks: {
    parent: privateEndpoints[i]
    name: 'default'
    properties: {
      privateDnsZoneConfigs: [{ name: link.name, properties: { privateDnsZoneId: dnsZones[i].id } }]
    }
  }
]

/*
  Monitoring
*/

resource logs 'Microsoft.OperationalInsights/workspaces@2026-03-01' = {
  name: 'azlog${resourceToken}'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

/*
  Cross-resource-group access
*/

// Internal judge app calls the judge model directly with the shared identity.
module judgeAccess 'modules/judge-access.bicep' = {
  name: 'judge-access-${resourceToken}'
  scope: resourceGroup(judgeResourceGroupName)
  params: {
    judgeAccountName: judgeAccountName
    principalId: identity.properties.principalId
  }
}

// Worker invokes the Foundry hosted agent.
module foundryAccess 'modules/foundry-access.bicep' = {
  name: 'foundry-access-${resourceToken}'
  scope: resourceGroup(foundryResourceGroupName)
  params: {
    foundryAccountName: foundryAccountName
    foundryProjectName: foundryProjectName
    principalId: identity.properties.principalId
  }
}

/*
  Container Apps environment
*/

// Consumption-only (no workloadProfiles): workload-profile VNet mode needs the unregistered
// Microsoft.Network/AllowBringYourOwnPublicIpAddress feature in this subscription.
resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  // Instance suffix avoids a name clash with an environment still pending platform deletion.
  name: 'azcae${resourceToken}1'
  location: location
  tags: tags
  properties: {
    vnetConfiguration: {
      infrastructureSubnetId: vnet.properties.subnets[2].id
      internal: false
    }
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

/*
  Container Apps (one per image)
*/

var webAppName = 'azweb${resourceToken}'
var webOrigin = 'https://${webAppName}.${environment.properties.defaultDomain}'
// Min Pool Size keeps one authenticated connection open so idle periods don't add a fresh managed-identity login.
var connectionString = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${databaseName};Authentication=Active Directory Managed Identity;User Id=${identity.properties.clientId};Encrypt=True;TrustServerCertificate=False;Connection Timeout=60;Min Pool Size=1;'

var dataEnv = [
  { name: 'AZURE_CLIENT_ID', value: identity.properties.clientId }
  { name: 'Database__Provider', value: 'SqlServer' }
  { name: 'ConnectionStrings__Evaluation', value: connectionString }
  { name: 'Storage__Provider', value: 'AzureBlob' }
  { name: 'Storage__ContainerUri', value: '${storage.properties.primaryEndpoints.blob}${blobContainerName}' }
]

var judgeEnv = [
  { name: 'AZURE_CLIENT_ID', value: identity.properties.clientId }
  { name: 'AZURE_OPENAI_ENDPOINT', value: judgeEndpoint }
  { name: 'AZURE_OPENAI_DEPLOYMENT', value: judgeDeployment }
  { name: 'AZURE_OPENAI_API_VERSION', value: judgeApiVersion }
  { name: 'JUDGE_SAMPLES', value: string(judgeSamples) }
]

var registries = [{ server: registry.properties.loginServer, identity: identity.id }]
var identityConfig = {
  type: 'UserAssigned'
  userAssignedIdentities: { '${identity.id}': {} }
}

resource clamav 'Microsoft.App/containerApps@2026-07-01' = if (shouldDeployApps) {
  name: 'azclam${resourceToken}'
  location: location
  tags: union(tags, { 'azd-service-name': 'clamav' })
  identity: identityConfig
  dependsOn: [acrPull]
  properties: {
    environmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Single'
      registries: registries
      // TCP ingress is reachable only inside the VNet-integrated environment.
      ingress: {
        external: false
        transport: 'tcp'
        targetPort: 3310
        exposedPort: 3310
      }
    }
    template: {
      containers: [
        {
          // clamd loads ~1.2 GB of signatures and briefly doubles during reloads.
          name: 'clamav'
          image: clamavImage!
          resources: { cpu: json('1.5'), memory: '3Gi' }
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
}

resource judge 'Microsoft.App/containerApps@2026-07-01' = if (shouldDeployApps) {
  name: 'azjdg${resourceToken}'
  location: location
  tags: union(tags, { 'azd-service-name': 'judge' })
  identity: identityConfig
  dependsOn: [acrPull, judgeAccess]
  properties: {
    environmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Single'
      registries: registries
      // Internal HTTPS only; never exposed outside the environment.
      ingress: {
        external: false
        targetPort: 8081
        transport: 'http'
        allowInsecure: false
      }
    }
    template: {
      containers: [
        {
          name: 'judge'
          image: judgeImage!
          command: ['judge-sidecar']
          args: ['--host', '0.0.0.0', '--port', '8081']
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: judgeEnv
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
}

resource worker 'Microsoft.App/containerApps@2026-07-01' = if (shouldDeployApps) {
  name: 'azwrk${resourceToken}'
  location: location
  tags: union(tags, { 'azd-service-name': 'worker' })
  identity: identityConfig
  dependsOn: [acrPull, blobAccess, foundryAccess, dnsZoneGroups, dnsLinks]
  properties: {
    environmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Single'
      registries: registries
    }
    template: {
      containers: [
        {
          name: 'worker'
          image: workerImage!
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: concat(dataEnv, [
            // Internal TCP ingress is reachable by app name inside the environment; the internal FQDN routes through the HTTP proxy and times out.
            { name: 'Malware__ClamAvHost', value: clamav!.name }
            { name: 'Malware__ClamAvPort', value: '3310' }
            { name: 'Judge__Mode', value: 'FoundryAgent' }
            { name: 'Judge__SidecarUrl', value: 'https://${judge!.properties.configuration.ingress.fqdn}' }
            { name: 'Judge__FoundryProjectEndpoint', value: foundryProjectEndpoint }
            { name: 'Judge__FoundryAgentName', value: foundryAgentName }
            { name: 'Judge__RequestTimeoutMinutes', value: '10' }
          ])
        }
      ]
      // One replica keeps judge spend predictable; job leases tolerate more.
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
}

resource web 'Microsoft.App/containerApps@2026-07-01' = if (shouldDeployApps) {
  name: webAppName
  location: location
  tags: union(tags, { 'azd-service-name': 'web' })
  identity: identityConfig
  dependsOn: [acrPull, blobAccess, dnsZoneGroups, dnsLinks]
  properties: {
    environmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Single'
      registries: registries
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
        // The SPA is served by the API itself, so only its own origin is allowed.
        corsPolicy: {
          allowedOrigins: [webOrigin]
          allowedMethods: ['GET', 'POST', 'PUT']
          allowedHeaders: ['Authorization', 'Content-Type', 'Idempotency-Key']
        }
      }
    }
    template: {
      containers: [
        {
          name: 'web'
          image: webImage!
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: concat(dataEnv, [
            { name: 'ASPNETCORE_URLS', value: 'http://+:8080' }
            { name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED', value: 'true' }
            { name: 'Entra__TenantId', value: entraTenantId }
            { name: 'Entra__Audience', value: entraApiAudience }
            { name: 'Entra__RequiredScope', value: entraRequiredScope }
            // Microsoft's B2B invitation email lands invitees here; the SPA routes them to team selection.
            { name: 'Invitations__RedirectUrl', value: '${webOrigin}/' }
            // The API app registration trusts this managed identity (federated credential), so invitations are sent
            // on behalf of the signed-in administrator; Register-EntraApps.ps1 configures the trust and consent.
            { name: 'Invitations__DelegatedClientId', value: entraApiAudience }
          ])
        }
      ]
      scale: {
        // One warm replica: scale-to-zero made the first page load after idle wait 6-9 seconds (container start +
        // first managed-identity SQL login). Costs roughly $12/month at idle consumption rates.
        minReplicas: 1
        maxReplicas: 2
        rules: [{ name: 'http', http: { metadata: { concurrentRequests: '30' } } }]
      }
    }
  }
}

resource migrate 'Microsoft.App/jobs@2026-07-01' = if (shouldDeployApps) {
  name: 'azjob${resourceToken}'
  location: location
  tags: tags
  identity: identityConfig
  dependsOn: [acrPull, dnsZoneGroups, dnsLinks]
  properties: {
    environmentId: environment.id
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 900
      replicaRetryLimit: 0
      manualTriggerConfig: { parallelism: 1, replicaCompletionCount: 1 }
      registries: registries
    }
    template: {
      containers: [
        {
          name: 'migrate'
          image: workerImage!
          args: ['--initialize-database']
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: dataEnv
        }
      ]
    }
  }
}

/*
  Outputs
*/

@description('Public URL of the Evaluation workspace.')
output webUrl string = webOrigin

@description('Evaluation workspace URL fragment route.')
output evaluationUrl string = '${webOrigin}/#evaluation'

@description('MSAL redirect bridge URI to register on the SPA app.')
output redirectUri string = '${webOrigin}/redirect.html'

@description('Container registry name.')
output registryName string = registry.name

@description('Container registry login server.')
output registryLoginServer string = registry.properties.loginServer

@description('Web container app name.')
output webAppName string = webAppName

@description('Worker container app name.')
output workerAppName string = 'azwrk${resourceToken}'

@description('Internal judge container app name.')
output judgeAppName string = 'azjdg${resourceToken}'

@description('Migration job name.')
output migrationJobName string = 'azjob${resourceToken}'

@description('Container Apps environment resource ID (operator jobs run here to reach private endpoints).')
output environmentId string = environment.id

@description('Azure region of the workload.')
output location string = location

@description('SQL server fully qualified domain name.')
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName

@description('Database name.')
output databaseName string = databaseName

@description('Managed identity name; also its SQL user name.')
output identityName string = identity.name

@description('Managed identity client ID.')
output identityClientId string = identity.properties.clientId

@description('Foundry project endpoint hosting the judge agent.')
output foundryProjectEndpoint string = foundryProjectEndpoint
