metadata name = 'Judge model access'
metadata description = 'Grants the evaluation identity data-plane access to the existing Azure OpenAI judge account in its own resource group.'

/*
  Parameters
*/

@description('Name of the existing Azure AI Services account that hosts the judge deployment.')
param judgeAccountName string

@description('Principal ID of the managed identity that calls the judge.')
param principalId string

/*
  Resources
*/

resource judgeAccount 'Microsoft.CognitiveServices/accounts@2026-09-01' existing = {
  name: judgeAccountName
}

// Cognitive Services OpenAI User: inference only; no key listing or deployment management.
var openAiUserRoleId = '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'

resource judgeInvoker 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(judgeAccount.id, principalId, openAiUserRoleId)
  scope: judgeAccount
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', openAiUserRoleId)
  }
}

/*
  Outputs
*/

@description('Resource ID of the judge role assignment.')
output roleAssignmentId string = judgeInvoker.id
