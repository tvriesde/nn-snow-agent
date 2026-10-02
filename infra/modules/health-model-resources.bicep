targetScope = 'resourceGroup'

param location string
@minLength(3)
@maxLength(18)
param namePrefix string
param ownershipId string
param backendResourceId string
param frontendResourceId string
param appServicePlanResourceId string

var httpServerErrors = {
  signalKind: 'AzureResourceMetric'
  metricNamespace: 'microsoft.web/sites'
  metricName: 'Http5xx'
  timeGrain: 'PT5M'
  aggregationType: 'Maximum'
  displayName: 'Http Server Errors'
  refreshInterval: 'PT1M'
  dataUnit: 'Count'
  evaluationRules: {
    degradedRule: { operator: 'GreaterThan', threshold: 0 }
    unhealthyRule: { operator: 'GreaterThan', threshold: 0 }
  }
}

resource healthModel 'Microsoft.CloudHealth/healthmodels@2026-09-01-preview' = {
  name: '${namePrefix}-health'
  location: location
  tags: {
    application: 'employee-it-helpdesk'
    ownershipId: ownershipId
  }
  identity: { type: 'SystemAssigned' }
  properties: {}
}

resource authentication 'Microsoft.CloudHealth/healthmodels/authenticationsettings@2026-09-01-preview' = {
  parent: healthModel
  name: 'systemassigned'
  properties: {
    displayName: 'SystemAssigned'
    authenticationKind: 'ManagedIdentity'
    managedIdentityName: 'SystemAssigned'
  }
}

resource backendEntity 'Microsoft.CloudHealth/healthmodels/entities@2026-05-01-preview' = {
  parent: healthModel
  name: '3e4980d9-ce73-492b-892e-380e75214d85'
  properties: {
    displayName: last(split(backendResourceId, '/'))
    canvasPosition: { x: -400, y: 200 }
    icon: { iconName: 'Resource' }
    impact: 'Standard'
    signalGroups: {
      azureResource: {
        authenticationSetting: authentication.name
        azureResourceId: backendResourceId
        signals: [
          union(httpServerErrors, { name: 'fafd1899-e9dd-4933-bd07-1bc66e53145e' })
        ]
      }
    }
  }
}

resource frontendEntity 'Microsoft.CloudHealth/healthmodels/entities@2026-05-01-preview' = {
  parent: healthModel
  name: '42824da5-1c23-424d-88ea-6302b743a844'
  properties: {
    displayName: last(split(frontendResourceId, '/'))
    canvasPosition: { x: -130, y: 80 }
    icon: { iconName: 'Resource' }
    impact: 'Standard'
    signalGroups: {
      azureResource: {
        authenticationSetting: authentication.name
        azureResourceId: frontendResourceId
        signals: [
          union(httpServerErrors, { name: '51db77a7-ac37-4843-aa4c-bea318162266' })
        ]
      }
    }
  }
}

resource planEntity 'Microsoft.CloudHealth/healthmodels/entities@2026-05-01-preview' = {
  parent: healthModel
  name: '6776ebf5-56cf-43f9-b556-f8f2f74cbeea'
  properties: {
    displayName: last(split(appServicePlanResourceId, '/'))
    canvasPosition: { x: -130, y: 480 }
    icon: { iconName: 'Resource' }
    impact: 'Standard'
    signalGroups: {
      azureResource: {
        authenticationSetting: authentication.name
        azureResourceId: appServicePlanResourceId
        signals: [
          {
            name: '5cd33081-eeb2-4fff-b4c7-8926f9ad2caa'
            signalKind: 'AzureResourceMetric'
            metricNamespace: 'microsoft.web/serverfarms'
            metricName: 'CpuPercentage'
            timeGrain: 'PT5M'
            aggregationType: 'Average'
            displayName: 'CPU Percentage'
            refreshInterval: 'PT1M'
            dataUnit: 'Percent'
            evaluationRules: {
              degradedRule: { operator: 'GreaterThan', threshold: 80 }
              unhealthyRule: { operator: 'GreaterThan', threshold: 95 }
            }
          }
          {
            name: '505015b0-9055-41f0-96ba-88ec0a64d19b'
            signalKind: 'AzureResourceMetric'
            metricNamespace: 'microsoft.web/serverfarms'
            metricName: 'MemoryPercentage'
            timeGrain: 'PT1M'
            aggregationType: 'Average'
            displayName: 'Memory Percentage'
            refreshInterval: 'PT1M'
            dataUnit: 'Percent'
            evaluationRules: {
              degradedRule: { operator: 'GreaterThan', threshold: 75 }
              unhealthyRule: { operator: 'GreaterThan', threshold: 90 }
            }
          }
        ]
      }
    }
  }
}

resource rootEntity 'Microsoft.CloudHealth/healthmodels/entities@2026-05-01-preview' = {
  parent: healthModel
  name: healthModel.name
  properties: {
    displayName: healthModel.name
    canvasPosition: { x: -130, y: -120 }
    impact: 'Standard'
  }
}

resource backendHosting 'Microsoft.CloudHealth/healthmodels/relationships@2026-05-01-preview' = {
  parent: healthModel
  name: '3e4980d9-ce73-492b-892e-380e--6776ebf5-56cf-43f9-b556-f8f2'
  properties: {
    displayName: 'IsHostedWithin'
    parentEntityName: backendEntity.name
    childEntityName: planEntity.name
  }
}

resource frontendHosting 'Microsoft.CloudHealth/healthmodels/relationships@2026-05-01-preview' = {
  parent: healthModel
  name: '42824da5-1c23-424d-88ea-6302--6776ebf5-56cf-43f9-b556-f8f2'
  properties: {
    displayName: 'IsHostedWithin'
    parentEntityName: frontendEntity.name
    childEntityName: planEntity.name
  }
}

resource rootFrontend 'Microsoft.CloudHealth/healthmodels/relationships@2026-05-01-preview' = {
  parent: healthModel
  name: 'c25edd86-963b-451a-a960-103f8ae7b72c'
  properties: {
    parentEntityName: rootEntity.name
    childEntityName: frontendEntity.name
  }
}

resource frontendBackend 'Microsoft.CloudHealth/healthmodels/relationships@2026-05-01-preview' = {
  parent: healthModel
  name: 'df7ec38a-7345-4e15-ae58-65970aaf42ed'
  properties: {
    parentEntityName: frontendEntity.name
    childEntityName: backendEntity.name
  }
}

output healthModelName string = healthModel.name
output healthModelId string = healthModel.id
output principalId string = healthModel.identity.principalId
output authenticationSettingId string = authentication.id
