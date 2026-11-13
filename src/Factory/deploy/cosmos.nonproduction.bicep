targetScope = 'resourceGroup'

@minLength(3)
@maxLength(8)
param namePrefix string
param location string

@description('Enterprise application/service principal object ID used by the local ASP.NET process, not the signed-in developer.')
param servicePrincipalObjectId string

@description('Optional approved egress IPv4 addresses/CIDRs. Empty allows all networks for development; Entra service authentication and Cosmos RBAC remain required.')
param allowedIpAddresses string[] = []

@minValue(1000)
param maxThroughput int
param tags object = {}

var suffix = take(uniqueString(resourceGroup().id, namePrefix, 'nonproduction'), 10)

module cosmos './cosmos-resources.bicep' = {
  name: '${namePrefix}-nonproduction-cosmos'
  params: {
    accountName: '${namePrefix}-nonprod-${suffix}-cosmos'
    location: location
    servicePrincipalObjectId: servicePrincipalObjectId
    maxThroughput: maxThroughput
    publicNetworkAccess: 'Enabled'
    allowedIpAddresses: allowedIpAddresses
    tags: union(tags, { environment: 'nonproduction' })
  }
}

output cosmosEndpoint string = cosmos.outputs.endpoint
output cosmosDatabase string = cosmos.outputs.databaseName
output cosmosContainer string = cosmos.outputs.containerName
