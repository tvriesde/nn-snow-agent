param location string
param namePrefix string
param ownershipId string
param backendClientId string
param frontendClientId string
param tenantId string
param modelName string
param modelVersion string
param modelSku string
param modelCapacity int
param modelLocation string
param modelDeploymentName string

var suffix = uniqueString(resourceGroup().id)
var tags = { application: 'employee-it-helpdesk', ownershipId: ownershipId }
var backendName = '${namePrefix}-api-${suffix}'
var frontendName = '${namePrefix}-web-${suffix}'
var functionName = '${namePrefix}-index-${suffix}'

resource mcp 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-mcp'
  location: location
  tags: tags
}
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'st${suffix}'
  location: location
  tags: tags
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    publicNetworkAccess: 'Enabled'
  }
}
resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: { deleteRetentionPolicy: { enabled: true, days: 7 } }
}
resource containers 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = [for name in ['servicenow', 'indexer-state', 'deploymentpackage']: {
  parent: blobs
  name: name
  properties: { publicAccess: 'None' }
}]
resource search 'Microsoft.Search/searchServices@2023-11-01' = {
  name: '${namePrefix}-search-${suffix}'
  location: location
  tags: tags
  sku: { name: 'free' }
  properties: {
    replicaCount: 1
    partitionCount: 1
    hostingMode: 'default'
    publicNetworkAccess: 'enabled'
    disableLocalAuth: true
  }
}
resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${namePrefix}-logs'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    workspaceCapping: { dailyQuotaGb: json('0.1') }
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}
resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${namePrefix}-insights'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    DisableLocalAuth: true
  }
}
resource openai 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: '${namePrefix}-ai-${suffix}'
  location: modelLocation
  tags: tags
  kind: 'OpenAI'
  sku: { name: 'S0' }
  properties: {
    customSubDomainName: '${namePrefix}-ai-${suffix}'
    publicNetworkAccess: 'Enabled'
  }
}
resource failureAnomalies 'Microsoft.AlertsManagement/smartDetectorAlertRules@2021-04-01' = {
  name: 'Failure Anomalies - ${insights.name}'
  location: 'global'
  tags: tags
  properties: {
    state: 'Disabled'
    severity: 'Sev3'
    frequency: 'PT1M'
    scope: [insights.id]
    detector: { id: 'FailureAnomaliesDetector' }
    actionGroups: { groupIds: [] }
  }
}
resource model 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: openai
  name: modelDeploymentName
  sku: { name: modelSku, capacity: modelCapacity }
  properties: {
    model: { format: 'OpenAI', name: modelName, version: modelVersion }
    versionUpgradeOption: 'NoAutoUpgrade'
  }
}
resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: 'kv-${suffix}'
  location: location
  tags: tags
  properties: {
    tenantId: tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    publicNetworkAccess: 'Enabled'
    accessPolicies: []
  }
}
resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: '${namePrefix}-f1'
  location: location
  tags: tags
  kind: 'linux'
  sku: { name: 'F1', tier: 'Free' }
  properties: { reserved: true }
}
resource backend 'Microsoft.Web/sites@2024-04-01' = {
  name: backendName
  location: location
  tags: tags
  kind: 'app,linux'
  identity: {
    type: 'SystemAssigned, UserAssigned'
    userAssignedIdentities: { '${mcp.id}': {} }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      appCommandLine: 'chmod +x /home/site/wwwroot/mcp/azmcp && dotnet /home/site/wwwroot/Helpdesk.Backend.dll'
      alwaysOn: false
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      appSettings: map(items({
        AzureOpenAI__Endpoint: openai.properties.endpoint
        AzureOpenAI__ApiKey: '@Microsoft.KeyVault(VaultName=${vault.name};SecretName=azure-openai-key)'
        AzureOpenAI__Deployment: model.name
        AzureOpenAI__ReasoningEffort: modelName == 'gpt-5-nano' ? 'low' : ''
        Search__Endpoint: 'https://${search.name}.search.windows.net'
        Search__IndexName: 'servicenow-knowledge'
        Azure__SubscriptionId: subscription().subscriptionId
        Azure__TenantId: tenantId
        Azure__LogAnalyticsWorkspace: workspace.name
        Azure__ApplicationInsightsResourceId: insights.id
        AzureMcp__ClientId: mcp.properties.clientId
        AzureMcp__ExecutablePath: '/home/site/wwwroot/mcp/azmcp'
        AzureMcp__CachePath: '/home/.mcp-cache'
        AzureMcp__Enabled: 'true'
        Authentication__TenantId: tenantId
        Authentication__Audience: backendClientId
        Authentication__RequiredScope: 'Helpdesk.Access'
        Frontend__Origin: 'https://${frontendName}.azurewebsites.net'
        Azure__ApplicationResources__0__Alias: 'Employee IT Helpdesk backend'
        Azure__ApplicationResources__0__ResourceId: resourceId('Microsoft.Web/sites', backendName)
        Azure__ApplicationResources__1__Alias: 'Employee IT Helpdesk frontend'
        Azure__ApplicationResources__1__ResourceId: resourceId('Microsoft.Web/sites', frontendName)
        APPLICATIONINSIGHTS_CONNECTION_STRING: insights.properties.ConnectionString
        APPLICATIONINSIGHTS_AUTHENTICATION_STRING: 'Authorization=AAD'
        APPLICATIONINSIGHTS_SAMPLING_PERCENTAGE: '10'
        ApplicationInsightsAgent_EXTENSION_VERSION: '~3'
        XDT_MicrosoftApplicationInsights_Mode: 'recommended'
        APPINSIGHTS_PROFILERFEATURE_VERSION: 'disabled'
        APPINSIGHTS_SNAPSHOTFEATURE_VERSION: 'disabled'
      }), setting => { name: setting.key, value: setting.value })
    }
  }
}
resource frontend 'Microsoft.Web/sites@2024-04-01' = {
  name: frontendName
  location: location
  tags: tags
  kind: 'app,linux'
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'NODE|24-lts'
      alwaysOn: false
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      appCommandLine: 'npm start'
      appSettings: map(items({
        API_BASE_URL: 'https://${backendName}.azurewebsites.net'
        ENTRA_TENANT_ID: tenantId
        ENTRA_CLIENT_ID: frontendClientId
        ENTRA_API_SCOPE: 'api://${backendClientId}/Helpdesk.Access'
        SCM_DO_BUILD_DURING_DEPLOYMENT: 'false'
      }), setting => { name: setting.key, value: setting.value })
    }
  }
}
resource flexPlan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: '${namePrefix}-flex'
  location: location
  tags: tags
  kind: 'functionapp'
  sku: { name: 'FC1', tier: 'FlexConsumption' }
  properties: { reserved: true }
}

// Composed from the Functions Team timer/http Flex templates; SystemAssigned
// is supported there. Public endpoints omit the optional VNet, not identity RBAC.
resource indexer 'Microsoft.Web/sites@2024-04-01' = {
  name: functionName
  location: location
  tags: tags
  kind: 'functionapp,linux'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: flexPlan.id
    httpsOnly: true
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storage.properties.primaryEndpoints.blob}deploymentpackage'
          authentication: { type: 'SystemAssignedIdentity' }
        }
      }
      runtime: { name: 'dotnet-isolated', version: '10.0' }
      scaleAndConcurrency: { instanceMemoryMB: 512, maximumInstanceCount: 40 }
    }
    siteConfig: {
      alwaysOn: false
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      appSettings: map(items({
        FUNCTIONS_EXTENSION_VERSION: '~4'
        AzureWebJobsStorage__credential: 'managedidentity'
        AzureWebJobsStorage__blobServiceUri: storage.properties.primaryEndpoints.blob
        AzureWebJobsStorage__queueServiceUri: storage.properties.primaryEndpoints.queue
        AzureWebJobsStorage__tableServiceUri: storage.properties.primaryEndpoints.table
        Source__BlobServiceUri: storage.properties.primaryEndpoints.blob
        Source__ContainerName: 'servicenow'
        State__ContainerName: 'indexer-state'
        Search__Endpoint: 'https://${search.name}.search.windows.net'
        Search__IndexName: 'servicenow-knowledge'
        IndexingSchedule: '0 */15 * * * *'
        APPLICATIONINSIGHTS_CONNECTION_STRING: insights.properties.ConnectionString
        APPLICATIONINSIGHTS_AUTHENTICATION_STRING: 'Authorization=AAD'
      }), setting => { name: setting.key, value: setting.value })
    }
  }
}

resource storageRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for role in [
  'b7e6dc6d-f1e8-4753-8033-0f276bb0955b' // Storage Blob Data Owner: host locks, deployment and state.
  '974c5e8b-45b9-4653-ba55-5f855dd0fb88' // Storage Queue Data Contributor: host diagnostics.
  '17d1049b-9a84-46fb-8f53-869881c3d3ab' // Storage Account Contributor: identity-based host.
]: {
  name: guid(storage.id, indexer.id, role)
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', role)
    principalId: indexer.identity.principalId
    principalType: 'ServicePrincipal'
  }
}]
resource searchRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (role, i) in [
  '1407120a-92aa-4202-b7e9-c0e197c71c8f'
  '8ebe5a00-799e-43f5-93ac-243d3dce84a7'
]: {
  name: guid(search.id, i == 0 ? backend.id : indexer.id, role)
  scope: search
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', role)
    principalId: i == 0 ? backend.identity.principalId : indexer.identity.principalId
    principalType: 'ServicePrincipal'
  }
}]
resource vaultReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, backend.id, 'secrets-user')
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
    principalId: backend.identity.principalId
    principalType: 'ServicePrincipal'
  }
}
resource telemetryRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for i in range(0, 2): {
  name: guid(insights.id, i == 0 ? backend.id : indexer.id, 'metrics-publisher')
  scope: insights
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '3913510d-42f4-4e42-8a64-420c390055eb')
    principalId: i == 0 ? backend.identity.principalId : indexer.identity.principalId
    principalType: 'ServicePrincipal'
  }
}]
resource mcpLogs 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(workspace.id, mcp.id, 'logs-reader')
  scope: workspace
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '73c42c96-874c-492b-b04d-ab87d138a893')
    principalId: mcp.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

output mcpPrincipalId string = mcp.properties.principalId
output resources object = {
  backendName: backend.name
  backendId: backend.id
  backendUrl: 'https://${backend.properties.defaultHostName}'
  frontendName: frontend.name
  frontendId: frontend.id
  frontendUrl: 'https://${frontend.properties.defaultHostName}'
  indexerName: indexer.name
  indexerId: indexer.id
  indexerUrl: 'https://${indexer.properties.defaultHostName}'
  storageName: storage.name
  storageId: storage.id
  storageBlobEndpoint: storage.properties.primaryEndpoints.blob
  searchName: search.name
  searchId: search.id
  searchEndpoint: 'https://${search.name}.search.windows.net'
  vaultId: vault.id
  vaultName: vault.name
  openAIId: openai.id
  mcpPrincipalId: mcp.properties.principalId
}
