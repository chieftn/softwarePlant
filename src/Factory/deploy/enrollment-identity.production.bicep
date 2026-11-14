targetScope = 'resourceGroup'

param location string
param identityName string

param tags object = {
  application: 'Factory'
  environment: 'production'
}

resource provisioner 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: identityName
  location: location
  tags: tags
}

output resourceId string = provisioner.id
output clientId string = provisioner.properties.clientId
output principalId string = provisioner.properties.principalId
