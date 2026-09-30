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

output resources object = app.outputs.resources
output readerRoleAssignmentId string = mcpReader.id
output resourceGroupId string = rg.id
