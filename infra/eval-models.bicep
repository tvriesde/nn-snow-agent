targetScope = 'resourceGroup'

param openAIAccountName string
param searchServiceName string
param keyVaultName string
param operatorObjectId string

type EvaluationDeployment = {
  deployment: string
  model: string
  version: string
  capacity: int
}

@description('Additive evaluation deployments only. Existing app deployments are not managed here.')
param deployments EvaluationDeployment[] = [
  { deployment: 'eval-gpt-5-mini', model: 'gpt-5-mini', version: '2025-08-07', capacity: 10 }
  { deployment: 'eval-gpt-4-1-mini', model: 'gpt-4.1-mini', version: '2025-04-14', capacity: 10 }
  { deployment: 'eval-gpt-4-1', model: 'gpt-4.1', version: '2025-04-14', capacity: 10 }
  { deployment: 'eval-judge', model: 'gpt-5', version: '2025-08-07', capacity: 10 }
]

resource account 'Microsoft.CognitiveServices/accounts@2024-10-01' existing = {
  name: openAIAccountName
}

@batchSize(1)
resource models 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = [for item in deployments: {
  parent: account
  name: item.deployment
  sku: { name: 'DataZoneStandard', capacity: item.capacity }
  properties: {
    model: { format: 'OpenAI', name: item.model, version: item.version }
    versionUpgradeOption: 'NoAutoUpgrade'
  }
}]

output deploymentNames array = [for item in deployments: item.deployment]

resource search 'Microsoft.Search/searchServices@2023-11-01' existing = {
  name: searchServiceName
}
resource vault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}
resource secret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' existing = {
  parent: vault
  name: 'azure-openai-key'
}
resource searchReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(search.id, operatorObjectId, 'evaluation-search-reader')
  scope: search
  properties: {
    principalId: operatorObjectId
    principalType: 'User'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '1407120a-92aa-4202-b7e9-c0e197c71c8f')
  }
}
resource secretReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(secret.id, operatorObjectId, 'evaluation-secret-reader')
  scope: secret
  properties: {
    principalId: operatorObjectId
    principalType: 'User'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
  }
}
