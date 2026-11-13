using './main.bicep'

param namePrefix = 'factory'
param location = '<choose-azure-region>'

// Illustrative development capacity, not an approved budget or production sizing.
param planSku = 'S1'
param cosmosMaxThroughput = 1000
param apiClientId = '<existing-entra-api-client-id>'
param supportedProtocolVersions = ['1']
param supportedAssignmentSchemaVersions = ['1']
param tags = {
  application: 'Factory'
  environment: 'development'
}
