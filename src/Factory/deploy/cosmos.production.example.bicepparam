using './cosmos.production.bicep'

param namePrefix = 'factory'
param location = 'westus2'

// Illustrative ceiling, not production sizing or deployment approval.
param maxThroughput = 1000
param tags = {
  application: 'Factory'
}
