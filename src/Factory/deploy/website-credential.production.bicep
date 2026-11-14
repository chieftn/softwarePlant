targetScope = 'resourceGroup'

param vaultName string
param dataIdentityName string

@description('Additional Entra website credential. Supply only through an in-memory secure ARM parameter, never files or command arguments.')
@secure()
@minLength(1)
param clientSecret string
param expiresOn int

resource dataIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: dataIdentityName
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: vaultName
}

resource webSignInSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'website-client-secret'
  properties: {
    value: clientSecret
    contentType: 'Entra confidential-client credential'
    attributes: {
      enabled: true
      exp: expiresOn
    }
  }
}

resource webCredentialAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, webSignInSecret.name, dataIdentity.id, 'web-credential')
  scope: webSignInSecret
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
    principalId: dataIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}
