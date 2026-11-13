targetScope = 'resourceGroup'

@minLength(3)
@maxLength(8)
param namePrefix string
param location string

@description('Address space reserved for the production service and its private endpoint.')
param vnetAddressPrefix string = '10.42.0.0/24'
param appSubnetPrefix string = '10.42.0.0/26'
param endpointSubnetPrefix string = '10.42.0.64/27'

@minValue(1000)
param maxThroughput int
param tags object = {}

var suffix = take(uniqueString(resourceGroup().id, namePrefix, 'production'), 10)
var stem = '${namePrefix}-prod-${suffix}'

resource dataIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${stem}-data'
  location: location
  tags: union(tags, { environment: 'production' })
}

resource network 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: '${stem}-vnet'
  location: location
  tags: union(tags, { environment: 'production' })
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
  dependsOn: [
    appSubnet
  ]
}

resource dnsZone 'Microsoft.Network/privateDnsZones@2024-06-01' = {
  name: 'privatelink.documents.azure.com'
  location: 'global'
  tags: union(tags, { environment: 'production' })
}

resource dnsLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = {
  parent: dnsZone
  name: '${stem}-link'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: network.id
    }
  }
}

module cosmos './cosmos-resources.bicep' = {
  name: '${namePrefix}-production-cosmos'
  params: {
    accountName: '${stem}-cosmos'
    location: location
    servicePrincipalObjectId: dataIdentity.properties.principalId
    maxThroughput: maxThroughput
    tags: union(tags, { environment: 'production' })
  }
}

resource endpoint 'Microsoft.Network/privateEndpoints@2024-05-01' = {
  name: '${stem}-cosmos-endpoint'
  location: location
  tags: union(tags, { environment: 'production' })
  properties: {
    subnet: {
      id: endpointSubnet.id
    }
    privateLinkServiceConnections: [
      {
        name: 'cosmos'
        properties: {
          privateLinkServiceId: cosmos.outputs.accountId
          groupIds: [
            'Sql'
          ]
        }
      }
    ]
  }
}

resource dnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = {
  parent: endpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'cosmos'
        properties: {
          privateDnsZoneId: dnsZone.id
        }
      }
    ]
  }
}

output cosmosEndpoint string = cosmos.outputs.endpoint
output cosmosDatabase string = cosmos.outputs.databaseName
output cosmosContainer string = cosmos.outputs.containerName
output dataIdentityResourceId string = dataIdentity.id
output dataIdentityClientId string = dataIdentity.properties.clientId
output dataIdentityPrincipalId string = dataIdentity.properties.principalId
output appSubnetId string = appSubnet.id
