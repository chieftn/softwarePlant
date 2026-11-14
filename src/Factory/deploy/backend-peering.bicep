targetScope = 'resourceGroup'

@description('Existing VNet in the application region.')
param appVnetName string

@description('Existing VNet containing the Cosmos private endpoint.')
param cosmosVnetName string

resource appNetwork 'Microsoft.Network/virtualNetworks@2024-05-01' existing = {
  name: appVnetName
}

resource cosmosNetwork 'Microsoft.Network/virtualNetworks@2024-05-01' existing = {
  name: cosmosVnetName
}

resource appToCosmos 'Microsoft.Network/virtualNetworks/virtualNetworkPeerings@2024-05-01' = {
  parent: appNetwork
  name: 'to-${cosmosNetwork.name}'
  properties: {
    remoteVirtualNetwork: {
      id: cosmosNetwork.id
    }
    allowVirtualNetworkAccess: true
    allowForwardedTraffic: false
    allowGatewayTransit: false
    useRemoteGateways: false
  }
}

resource cosmosToApp 'Microsoft.Network/virtualNetworks/virtualNetworkPeerings@2024-05-01' = {
  parent: cosmosNetwork
  name: 'to-${appNetwork.name}'
  properties: {
    remoteVirtualNetwork: {
      id: appNetwork.id
    }
    allowVirtualNetworkAccess: true
    allowForwardedTraffic: false
    allowGatewayTransit: false
    useRemoteGateways: false
  }
  dependsOn: [
    appToCosmos
  ]
}

output appPeeringId string = appToCosmos.id
output cosmosPeeringId string = cosmosToApp.id
