targetScope = 'subscription'

param location string = 'swedencentral'
@minLength(3)
@maxLength(18)
param namePrefix string = 'snowdemo'
param resourceGroupName string = '${namePrefix}-rg'
param ownershipId string
param backendResourceId string = resourceId(subscription().subscriptionId, resourceGroupName, 'Microsoft.Web/sites', '${namePrefix}-api-${uniqueString('/subscriptions/${subscription().subscriptionId}/resourceGroups/${resourceGroupName}')}')
param frontendResourceId string = resourceId(subscription().subscriptionId, resourceGroupName, 'Microsoft.Web/sites', '${namePrefix}-web-${uniqueString('/subscriptions/${subscription().subscriptionId}/resourceGroups/${resourceGroupName}')}')
param appServicePlanResourceId string = resourceId(subscription().subscriptionId, resourceGroupName, 'Microsoft.Web/serverFarms', '${namePrefix}-f1')

module healthModel 'modules/health-model-resources.bicep' = {
  name: '${namePrefix}-health-resources'
  scope: resourceGroup(resourceGroupName)
  params: {
    location: location
    namePrefix: namePrefix
    ownershipId: ownershipId
    backendResourceId: backendResourceId
    frontendResourceId: frontendResourceId
    appServicePlanResourceId: appServicePlanResourceId
  }
}

var readerRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'acdd72a7-3385-48ef-bd42-f606fba81ae7')

resource reader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(subscription().id, ownershipId, resourceId(subscription().subscriptionId, resourceGroupName, 'Microsoft.CloudHealth/healthmodels', '${namePrefix}-health'), readerRoleId)
  properties: {
    roleDefinitionId: readerRoleId
    principalId: healthModel.outputs.principalId
    principalType: 'ServicePrincipal'
  }
}

output healthModelName string = healthModel.outputs.healthModelName
output healthModelId string = healthModel.outputs.healthModelId
output principalId string = healthModel.outputs.principalId
output authenticationSettingId string = healthModel.outputs.authenticationSettingId
output readerRoleAssignmentId string = reader.id
