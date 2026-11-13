targetScope = 'resourceGroup'

@description('Lowercase letters and digits only. Used with a resource-group-specific suffix.')
@minLength(3)
@maxLength(8)
param namePrefix string

@description('Explicitly selected Azure region. Check service availability and cost before deployment.')
param location string

@description('Development hosting SKU. This template does not configure production scale-out or autoscale.')
@allowed([
  'S1'
  'S2'
  'S3'
  'P1v3'
  'P2v3'
  'P3v3'
])
param planSku string

@description('Explicitly approved Cosmos autoscale RU/s ceiling; billed independently of App Service.')
@minValue(1000)
param cosmosMaxThroughput int

@description('Existing Entra v2 API application client ID. This template does not create registrations or grant service-entry roles.')
param apiClientId string

@description('Explicitly approved registration compatibility. This does not install or implement worker adapters.')
@minLength(1)
@maxLength(16)
param supportedProtocolVersions array

@minLength(1)
@maxLength(16)
param supportedAssignmentSchemaVersions array

@description('Address space must not overlap networks this deployment will connect to.')
param vnetAddressPrefix string = '10.42.0.0/24'
param appSubnetPrefix string = '10.42.0.0/26'
param endpointSubnetPrefix string = '10.42.0.64/27'

param tags object = {}

var suffix = take(uniqueString(resourceGroup().id, namePrefix), 10)
var stem = '${namePrefix}-${suffix}'
var blobContainers = [
  'snapshots'
  'evidence'
  'archives'
  'data-protection'
]

resource dataIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${stem}-data'
  location: location
  tags: tags
}

// No Graph grants are made here. Directory bootstrap requires separate administrator approval.
resource provisioningIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${stem}-provisioner'
  location: location
  tags: tags
}

resource network 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: '${stem}-vnet'
  location: location
  tags: tags
  properties: {
    addressSpace: {
      addressPrefixes: [
        vnetAddressPrefix
      ]
    }
  }
}

resource appSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-05-01' = {
  parent: network
  name: 'app-service'
  properties: {
    addressPrefix: appSubnetPrefix
    delegations: [
      {
        name: 'app-service'
        properties: {
          serviceName: 'Microsoft.Web/serverFarms'
        }
      }
    ]
  }
}

resource endpointSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-05-01' = {
  parent: network
  name: 'private-endpoints'
  properties: {
    addressPrefix: endpointSubnetPrefix
    privateEndpointNetworkPolicies: 'Disabled'
  }
  // Subnet writes update the same VNet; do not submit them concurrently.
  dependsOn: [
    appSubnet
  ]
}

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${stem}-logs'
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${stem}-insights'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
    DisableLocalAuth: true
  }
}

resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' = {
  name: '${stem}-cosmos'
  location: location
  tags: tags
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    consistencyPolicy: {
      defaultConsistencyLevel: 'Strong'
    }
    locations: [
      {
        locationName: location
        failoverPriority: 0
        isZoneRedundant: false
      }
    ]
    backupPolicy: {
      type: 'Continuous'
      continuousModeProperties: {
        tier: 'Continuous30Days'
      }
    }
    publicNetworkAccess: 'Disabled'
    disableLocalAuth: true
    minimalTlsVersion: 'Tls12'
  }
}

resource database 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-05-15' = {
  parent: cosmos
  name: 'plant'
  properties: {
    resource: {
      id: 'plant'
    }
  }
}

resource records 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = {
  parent: database
  name: 'records'
  properties: {
    resource: {
      id: 'records'
      partitionKey: {
        paths: [
          '/scopeId'
        ]
        kind: 'Hash'
        version: 2
      }
      indexingPolicy: {
        indexingMode: 'consistent'
        automatic: true
        includedPaths: [
          { path: '/type/?' }
          { path: '/plantId/?' }
          { path: '/lineId/?' }
          { path: '/state/?' }
          { path: '/submittedAt/?' }
          { path: '/assignmentId/?' }
          { path: '/id/?' }
          { path: '/principal/tenantId/?' }
          { path: '/principal/objectId/?' }
        ]
        excludedPaths: [
          { path: '/*' }
        ]
      }
    }
    options: {
      autoscaleSettings: {
        maxThroughput: cosmosMaxThroughput
      }
    }
  }
}

resource cosmosDataAccess 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-05-15' = {
  parent: cosmos
  name: guid(cosmos.id, dataIdentity.id, 'plant-data')
  properties: {
    principalId: dataIdentity.properties.principalId
    roleDefinitionId: '${cosmos.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    scope: '${cosmos.id}/dbs/${database.name}'
  }
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'st${suffix}'
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

resource containers 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = [for container in blobContainers: {
  parent: blobService
  name: container
  properties: {
    publicAccess: 'None'
  }
}]

resource blobDataAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (container, index) in blobContainers: {
  name: guid(containers[index].id, dataIdentity.id, 'blob-data')
  scope: containers[index]
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
    principalId: dataIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}]

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: '${stem}-kv'
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
      bypass: 'None'
      defaultAction: 'Deny'
    }
  }
}

var privateServices = [
  {
    name: 'cosmos'
    resourceId: cosmos.id
    groupId: 'Sql'
    zone: 'privatelink.documents.azure.com'
  }
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

resource monitorDataAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(insights.id, dataIdentity.id, 'monitor-publisher')
  scope: insights
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '3913510d-42f4-4e42-8a64-420c390055eb')
    principalId: dataIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: '${stem}-plan'
  location: location
  tags: tags
  kind: 'linux'
  sku: {
    name: planSku
    capacity: 1
  }
  properties: {
    reserved: true
  }
}

resource web 'Microsoft.Web/sites@2024-04-01' = {
  name: '${stem}-web'
  location: location
  tags: tags
  kind: 'app,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${dataIdentity.id}': {}
      '${provisioningIdentity.id}': {}
    }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    publicNetworkAccess: 'Enabled'
    virtualNetworkSubnetId: appSubnet.id
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      appCommandLine: './Factory.Plant.Service --urls http://0.0.0.0:8080'
      alwaysOn: true
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      scmMinTlsVersion: '1.2'
      http20Enabled: true
      healthCheckPath: '/health/live'
      appSettings: concat([
        { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
        { name: 'WEBSITE_RUN_FROM_PACKAGE', value: '1' }
        { name: 'Factory__TenantId', value: tenant().tenantId }
        { name: 'Factory__ClientId', value: apiClientId }
        { name: 'Factory__CosmosEndpoint', value: cosmos.properties.documentEndpoint }
        { name: 'Factory__CosmosDatabase', value: database.name }
        { name: 'Factory__CosmosContainer', value: records.name }
        { name: 'Factory__DataCredential', value: 'ManagedIdentity' }
        { name: 'Factory__DataIdentityClientId', value: dataIdentity.properties.clientId }
        { name: 'Factory__PaginationKeyRingPath', value: '/home/data/factory-pagination-keys' }
      ], [for (version, index) in supportedProtocolVersions: {
        name: 'Factory__SupportedProtocolVersions__${index}'
        value: version
      }], [for (version, index) in supportedAssignmentSchemaVersions: {
        name: 'Factory__SupportedAssignmentSchemaVersions__${index}'
        value: version
      }])
    }
  }
}

resource disableFtpCredentials 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-04-01' = {
  parent: web
  name: 'ftp'
  properties: {
    allow: false
  }
}

resource disableScmCredentials 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-04-01' = {
  parent: web
  name: 'scm'
  properties: {
    allow: false
  }
}

output serviceUrl string = 'https://${web.properties.defaultHostName}'
output webAppName string = web.name
output dataIdentityClientId string = dataIdentity.properties.clientId
output provisioningIdentityClientId string = provisioningIdentity.properties.clientId
output cosmosEndpoint string = cosmos.properties.documentEndpoint
output storageBlobEndpoint string = storage.properties.primaryEndpoints.blob
output vaultUri string = vault.properties.vaultUri
output applicationInsightsConnectionString string = insights.properties.ConnectionString
