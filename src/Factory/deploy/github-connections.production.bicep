targetScope = 'resourceGroup'

@description('Existing website Key Vault. No vault, secret, application setting or GitHub App is created by this deployment.')
param vaultName string

@description('Existing service data identity. This grants secret get/set only; it does not grant Graph or model access.')
param dataIdentityName string

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: vaultName
}

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: dataIdentityName
}

resource secretAccessRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' = {
  name: guid(resourceGroup().id, 'factory-github-secret-get-set')
  properties: {
    roleName: 'Factory GitHub onboarding secrets (${resourceGroup().name})'
    description: 'Read and set secret values for managed GitHub onboarding. No list, delete, purge, key administration or RBAC management.'
    type: 'CustomRole'
    assignableScopes: [
      resourceGroup().id
    ]
    permissions: [
      {
        actions: []
        notActions: []
        dataActions: [
          'Microsoft.KeyVault/vaults/secrets/getSecret/action'
          'Microsoft.KeyVault/vaults/secrets/setSecret/action'
        ]
        notDataActions: []
      }
    ]
  }
}

resource secretAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, identity.id, secretAccessRole.id)
  scope: vault
  properties: {
    roleDefinitionId: secretAccessRole.id
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

output keyVaultUri string = vault.properties.vaultUri
