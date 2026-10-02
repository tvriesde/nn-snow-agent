targetScope = 'resourceGroup'

param openAIAccountName string
param deploymentName string = 'helpdesk-luna'
@minValue(1)
@maxValue(333)
param capacity int = 10

resource account 'Microsoft.CognitiveServices/accounts@2024-10-01' existing = {
  name: openAIAccountName
}
resource luna 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: account
  name: deploymentName
  sku: { name: 'DataZoneStandard', capacity: capacity }
  properties: {
    model: { format: 'OpenAI', name: 'gpt-6-luna', version: '2026-09-22' }
    versionUpgradeOption: 'NoAutoUpgrade'
  }
}

output deploymentId string = luna.id
output modelDeploymentName string = luna.name
