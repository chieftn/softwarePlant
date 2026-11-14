using './cosmos.nonproduction.bicep'

param namePrefix = 'factory'
param location = 'westus2'
param servicePrincipalObjectId = '<nonproduction-service-principal-object-id>'
// Development allows all networks; the application must still authenticate with Entra.
// Supply approved egress addresses/CIDRs here if network restrictions are required.
param allowedIpAddresses = []

// Illustrative ceiling, not deployment approval or a budget estimate.
param maxThroughput = 1000
param tags = {
  application: 'Factory'
}
