targetScope = 'resourceGroup'

param location string
param appName string
param dataIdentityName string
param vnetName string

param tags object = {
  application: 'Factory'
  environment: 'production'
}

var suffix = uniqueString(resourceGroup().id, appName, 'website')
var stem = 'factory-web-${suffix}'

resource dataIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: dataIdentityName
}

resource network 'Microsoft.Network/virtualNetworks@2024-05-01' existing = {
  name: vnetName
}

resource endpointSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-05-01' existing = {
  parent: network
  name: 'private-endpoints'
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'st${suffix}web'
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    publicNetworkAccess: 'Disabled'
    networkAcls: {
      bypass: 'None'
      defaultAction: 'Deny'
    }
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    isVersioningEnabled: true
    deleteRetentionPolicy: {
      enabled: true
      days: 30
    }
    containerDeleteRetentionPolicy: {
      enabled: true
      days: 30
    }
  }
}

resource keyRing 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'data-protection'
  properties: {
    publicAccess: 'None'
  }
}

resource blobDataAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyRing.id, dataIdentity.id, 'blob-data')
  scope: keyRing
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
    principalId: dataIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: 'kv${suffix}web'
  location: location
  tags: tags
  properties: {
    tenantId: tenant().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    enableSoftDelete: true
    enablePurgeProtection: true
    softDeleteRetentionInDays: 90
    publicNetworkAccess: 'Disabled'
    networkAcls: {
      // ARM key/secret provisioning uses the trusted-service path, not public access.
      bypass: 'AzureServices'
      defaultAction: 'Deny'
    }
  }
}

resource cookieProtectionKey 'Microsoft.KeyVault/vaults/keys@2023-07-01' = {
  parent: vault
  name: 'cookie-protection'
  properties: {
    kty: 'RSA'
    keySize: 2048
    keyOps: [
      'wrapKey'
      'unwrapKey'
    ]
    attributes: {
      enabled: true
    }
  }
}

resource cookieProtectionAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, cookieProtectionKey.name, dataIdentity.id, 'cookie-protection')
  scope: cookieProtectionKey
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '12338af0-0e69-4776-bea7-57ae8d297424')
    principalId: dataIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

var privateServices = [
  {
    name: 'blob'
    resourceId: storage.id
    groupId: 'blob'
    zone: 'privatelink.blob.${environment().suffixes.storage}'
  }
  {
    name: 'vault'
    resourceId: vault.id
    groupId: 'vault'
    zone: 'privatelink.vaultcore.azure.net'
  }
]

resource dnsZones 'Microsoft.Network/privateDnsZones@2024-06-01' = [for service in privateServices: {
  name: service.zone
  location: 'global'
  tags: tags
}]

resource dnsLinks 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = [for (service, index) in privateServices: {
  parent: dnsZones[index]
  name: '${stem}-link'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: network.id
    }
  }
}]

resource endpoints 'Microsoft.Network/privateEndpoints@2024-05-01' = [for service in privateServices: {
  name: '${stem}-${service.name}-endpoint'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: endpointSubnet.id
    }
    privateLinkServiceConnections: [
      {
        name: service.name
        properties: {
          privateLinkServiceId: service.resourceId
          groupIds: [
            service.groupId
          ]
        }
      }
    ]
  }
}]

resource dnsGroups 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = [for (service, index) in privateServices: {
  parent: endpoints[index]
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: service.name
        properties: {
          privateDnsZoneId: dnsZones[index].id
        }
      }
    ]
  }
}]

output storageAccountName string = storage.name
output vaultName string = vault.name
output webClientSecretUri string = '${vault.properties.vaultUri}secrets/website-client-secret'
output dataProtectionBlobUri string = '${storage.properties.primaryEndpoints.blob}${keyRing.name}/keys.xml'
output dataProtectionKeyId string = '${vault.properties.vaultUri}keys/${cookieProtectionKey.name}'
