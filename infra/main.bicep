targetScope = 'subscription'

param location string = 'swedencentral'
@minLength(3)
@maxLength(18)
param namePrefix string = 'snowdemo'
param resourceGroupName string = '${namePrefix}-rg'
param ownershipId string
param backendClientId string
param frontendClientId string
param tenantId string = subscription().tenantId
param modelName string = 'gpt-5-nano'
param modelVersion string
@allowed(['Standard', 'DataZoneStandard'])
param modelSku string
@minValue(1)
param modelCapacity int = 50
param modelLocation string = location
param modelDeploymentName string = 'helpdesk-mini'
param lunaModelEnabled bool = true
@minValue(1)
@maxValue(333)
param lunaModelCapacity int = 10

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
  tags: {
    application: 'employee-it-helpdesk'
    ownershipId: ownershipId
  }
}

module app 'resources.bicep' = {
  name: '${namePrefix}-resources'
  scope: rg
  params: {
    location: location
    namePrefix: namePrefix
    ownershipId: ownershipId
    backendClientId: backendClientId
    frontendClientId: frontendClientId
    tenantId: tenantId
    modelName: modelName
    modelVersion: modelVersion
    modelSku: modelSku
    modelCapacity: modelCapacity
    modelLocation: modelLocation
    modelDeploymentName: modelDeploymentName
    lunaModelEnabled: lunaModelEnabled
    lunaModelCapacity: lunaModelCapacity
  }
}

resource mcpReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(subscription().id, ownershipId, 'mcp-reader')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'acdd72a7-3385-48ef-bd42-f606fba81ae7')
    principalId: app.outputs.mcpPrincipalId
    principalType: 'ServicePrincipal'
  }
}

module healthModel 'health-model.bicep' = {
  name: '${namePrefix}-health-model'
  params: {
    location: location
    namePrefix: namePrefix
    resourceGroupName: rg.name
    ownershipId: ownershipId
    backendResourceId: app.outputs.resources.backendId
    frontendResourceId: app.outputs.resources.frontendId
    appServicePlanResourceId: app.outputs.resources.appServicePlanId
  }
}

output resources object = union(app.outputs.resources, {
  healthModelName: healthModel.outputs.healthModelName
  healthModelId: healthModel.outputs.healthModelId
  healthModelPrincipalId: healthModel.outputs.principalId
})
output readerRoleAssignmentId string = mcpReader.id
output healthModelReaderRoleAssignmentId string = healthModel.outputs.readerRoleAssignmentId
output resourceGroupId string = rg.id
