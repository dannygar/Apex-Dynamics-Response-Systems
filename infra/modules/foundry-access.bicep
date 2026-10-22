metadata name = 'Foundry agent access'
metadata description = 'Grants the evaluation identity permission to invoke agents in the existing Foundry project.'

/*
  Parameters
*/

@description('Name of the existing Foundry (AI Services) account that owns the project.')
param foundryAccountName string

@description('Name of the existing Foundry project that hosts the judge agent.')
param foundryProjectName string

@description('Principal ID of the managed identity that invokes the hosted agent.')
param principalId string

/*
  Resources
*/

resource account 'Microsoft.CognitiveServices/accounts@2026-09-01' existing = {
  name: foundryAccountName
}

resource project 'Microsoft.CognitiveServices/accounts/projects@2026-09-01' existing = {
  parent: account
  name: foundryProjectName
}

// Foundry Agent Consumer: least-privilege role to call agent endpoints; cannot create or change agents.
var agentConsumerRoleId = 'eed3b665-ab3a-47b6-8f48-c9382fb1dad6'

resource agentConsumer 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(project.id, principalId, agentConsumerRoleId)
  scope: project
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', agentConsumerRoleId)
  }
}

/*
  Outputs
*/

@description('Resource ID of the agent consumer role assignment.')
output roleAssignmentId string = agentConsumer.id
