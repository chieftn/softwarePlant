targetScope = 'resourceGroup'

@description('Name of the new production App Service. Existing backend resources are referenced, not redeployed.')
param appName string
param planName string
param location string

@description('Explicitly approve the hosting tier and ongoing charges before deployment.')
@allowed([
  'B1'
  'S1'
  'P0v3'
  'P1v3'
])
param planSku string = 'S1'

@description('Existing API application/client ID. This deployment makes no Entra changes.')
param apiClientId string
param cosmosAccountName string
param dataIdentityName string
param vnetName string

@description('Enable only after website.production.bicep, credential storage, Entra callbacks and user roles are configured.')
param webEnabled bool = false
param webPublicOrigin string = ''
param dataProtectionBlobUri string = ''
param dataProtectionKeyId string = ''

@description('Versionless Key Vault secret URI, not a credential value.')
param webClientSecretUri string = ''

@description('Dedicated existing enrollment identity. Supplying a name attaches it even while enrollment is disabled.')
param provisioningIdentityName string = ''

@description('Enable only after installer registration and explicitly approved provisioner Graph grants are configured.')
param enrollmentEnabled bool = false
param installerClientId string = ''

param tags object = {
  application: 'Factory'
  environment: 'production'
}

resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' existing = {
  name: cosmosAccountName
}

resource dataIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: dataIdentityName
}

resource provisioningIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = if (!empty(provisioningIdentityName)) {
  name: provisioningIdentityName
}

resource network 'Microsoft.Network/virtualNetworks@2024-05-01' existing = {
  name: vnetName
}

resource appSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-05-01' existing = {
  parent: network
  name: 'app-service'
}

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: planName
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
  name: appName
  location: location
  tags: tags
  kind: 'app,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: union({
      '${dataIdentity.id}': {}
    }, !empty(provisioningIdentityName) ? {
      '${provisioningIdentity!.id}': {}
    } : {})
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    publicNetworkAccess: 'Enabled'
    virtualNetworkSubnetId: appSubnet.id
    keyVaultReferenceIdentity: webEnabled ? dataIdentity.id : 'SystemAssigned'
    siteConfig: {
      // Linux App Service requires VNet routing for private Key Vault references.
      vnetRouteAllEnabled: webEnabled
      linuxFxVersion: 'DOTNETCORE|10.0'
      appCommandLine: 'dotnet Factory.Plant.Service.dll --urls http://0.0.0.0:8080'
      alwaysOn: true
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      scmMinTlsVersion: '1.2'
      http20Enabled: true
      healthCheckPath: '/health/live'
      appSettings: concat([
        { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
        { name: 'WEBSITE_RUN_FROM_PACKAGE', value: '1' }
        { name: 'WEBSITES_ENABLE_APP_SERVICE_STORAGE', value: 'true' }
        { name: 'Factory__TenantId', value: tenant().tenantId }
        { name: 'Factory__ClientId', value: apiClientId }
        { name: 'Factory__CosmosEndpoint', value: cosmos.properties.documentEndpoint }
        { name: 'Factory__CosmosDatabase', value: 'plant' }
        { name: 'Factory__CosmosContainer', value: 'records' }
        { name: 'Factory__DataCredential', value: 'ManagedIdentity' }
        { name: 'Factory__DataIdentityClientId', value: dataIdentity.properties.clientId }
        { name: 'Factory__PaginationKeyRingPath', value: '/home/data/factory-pagination-keys' }
        { name: 'Factory__SupportedProtocolVersions__0', value: '1' }
        { name: 'Factory__SupportedAssignmentSchemaVersions__0', value: '1' }
        { name: 'Factory__Web__Enabled', value: string(webEnabled) }
        { name: 'Factory__Enrollment__Enabled', value: string(enrollmentEnabled) }
      ], webEnabled ? [
        { name: 'Factory__ClientSecret', value: '@Microsoft.KeyVault(SecretUri=${webClientSecretUri})' }
        { name: 'Factory__Web__PublicOrigin', value: webPublicOrigin }
        { name: 'Factory__Web__DataProtectionBlobUri', value: dataProtectionBlobUri }
        { name: 'Factory__Web__DataProtectionKeyId', value: dataProtectionKeyId }
      ] : [], enrollmentEnabled ? [
        { name: 'Factory__Enrollment__InstallerClientId', value: installerClientId }
        { name: 'Factory__Enrollment__ProvisioningCredential', value: 'ManagedIdentity' }
        { name: 'Factory__Enrollment__ProvisioningClientId', value: provisioningIdentity!.properties.clientId }
        { name: 'Factory__Enrollment__ProvisioningObjectId', value: provisioningIdentity!.properties.principalId }
      ] : [])
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

resource logging 'Microsoft.Web/sites/config@2024-04-01' = {
  parent: web
  name: 'logs'
  properties: {
    applicationLogs: {
      fileSystem: {
        level: 'Information'
      }
    }
    httpLogs: {
      fileSystem: {
        enabled: true
        retentionInDays: 7
        retentionInMb: 35
      }
    }
  }
}

output serviceUrl string = 'https://${web.properties.defaultHostName}'
output webAppName string = web.name
output dataIdentityClientId string = dataIdentity.properties.clientId
