targetScope = 'resourceGroup'

param accountName string
param location string

@description('Object ID of the ASP.NET service principal or managed identity, not an application client ID or browser user.')
param servicePrincipalObjectId string

@description('Explicitly approved autoscale RU/s ceiling.')
@minValue(1000)
param maxThroughput int

@description('Public access is disabled unless an environment explicitly enables it.')
@allowed([
  'Disabled'
  'Enabled'
])
param publicNetworkAccess string = 'Disabled'

@description('Optional public IPv4 addresses/CIDRs. When public access is enabled, empty permits any source IP; Entra authentication is still required.')
param allowedIpAddresses string[] = []

param tags object = {}

resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' = {
  name: accountName
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
    publicNetworkAccess: publicNetworkAccess
    ipRules: [for address in allowedIpAddresses: {
      ipAddressOrRange: address
    }]
    networkAclBypass: 'None'
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
        maxThroughput: maxThroughput
      }
    }
  }
}

resource serviceDataAccess 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-05-15' = {
  parent: cosmos
  name: guid(cosmos.id, servicePrincipalObjectId, 'plant-data')
  properties: {
    principalId: servicePrincipalObjectId
    roleDefinitionId: '${cosmos.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    scope: '${cosmos.id}/dbs/${database.name}'
  }
}

output accountId string = cosmos.id
output endpoint string = cosmos.properties.documentEndpoint
output databaseName string = database.name
output containerName string = records.name
