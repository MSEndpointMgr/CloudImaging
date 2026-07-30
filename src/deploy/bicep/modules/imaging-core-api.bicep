// modules/imaging-core-api.bicep — Private Imaging Core API (VNet-only, no public endpoint)
// Premium EP1 required for VNet integration (FR-020, FR-022)
// Also: T148 — DeviceManagementServiceConfig.Read.All grant for Microsoft Graph queries (FR-026)

param location string
param funcName string
param planName string
param storageAccountName string
param appInsightsConnectionString string
param msiId string
param msiClientId string
param vnetSubnetId string
param pepSubnetId string
param pepName string
@description('Resource ID of the privatelink.azurewebsites.net private DNS zone used to auto-register the private endpoint A records.')
param privateDnsZoneId string
param keyVaultName string
// sharedEntraClientId: Application (client) ID of the Cloud Imaging Portal registration,
// carried for Entra token validation context in the Imaging Core API (private, Private Link only).
param sharedEntraClientId string
param tenantId string
param passcodeTtlMinutes int
param sasTokenExpiryMinutes int
param bootImageSasExpiryMinutes int
param certValidityPeriodDays int
param clockSkewToleranceSeconds int
// Elastic Premium SKU for the Function App plan.
// Allowed: EP1 (default), EP2, EP3. Must remain ElasticPremium tier for VNet integration.
@allowed(['EP1','EP2','EP3'])
param functionAppSku string = 'EP1'

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: planName
  location: location
  sku: { name: functionAppSku, tier: 'ElasticPremium' }
  properties: { reserved: true }
}

resource func 'Microsoft.Web/sites@2024-04-01' = {
  name: funcName
  location: location
  kind: 'functionapp,linux'
  identity: {
    // System-assigned identity reads the run-from-package blob (Storage Blob Data
    // Reader, granted below); the user-assigned identity is used by app code.
    type: 'SystemAssigned, UserAssigned'
    userAssignedIdentities: { '${msiId}': {} }
  }
  properties: {
    serverFarmId: plan.id
    virtualNetworkSubnetId: vnetSubnetId
    vnetRouteAllEnabled: true
    // No public access — accessible only via Private Link (FR-020)
    publicNetworkAccess: 'Disabled'
    siteConfig: {
      linuxFxVersion: 'DOTNET-ISOLATED|10'
      appSettings: [
        { name: 'FUNCTIONS_WORKER_RUNTIME', value: 'dotnet-isolated' }
        { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
        { name: 'AzureWebJobsStorage__accountName', value: storageAccountName }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
        { name: 'AZURE_CLIENT_ID', value: msiClientId }
        { name: 'Entra__TenantId', value: tenantId }
        { name: 'Entra__ClientId', value: sharedEntraClientId }
        { name: 'KeyVault__VaultUri', value: 'https://${keyVaultName}${environment().suffixes.keyvaultDns}' }
        { name: 'Security__PasscodeTtlMinutes', value: string(passcodeTtlMinutes) }
        { name: 'Security__SasTokenExpiryMinutes', value: string(sasTokenExpiryMinutes) }
        { name: 'Security__BootImageSasExpiryMinutes', value: string(bootImageSasExpiryMinutes) }
        { name: 'Security__CertValidityPeriodDays', value: string(certValidityPeriodDays) }
        { name: 'Security__ClockSkewToleranceSeconds', value: string(clockSkewToleranceSeconds) }
      ]
    }
  }
}

// Private Endpoint — exposes ImagingCoreApi only via Private Link to DeviceGatewayApi and OperatorApi
resource pep 'Microsoft.Network/privateEndpoints@2024-01-01' = {
  name: pepName
  location: location
  properties: {
    subnet: { id: pepSubnetId }
    privateLinkServiceConnections: [
      {
        name: pepName
        properties: {
          privateLinkServiceId: func.id
          groupIds: ['sites']
        }
      }
    ]
  }
}

// Registers the private endpoint's A records into privatelink.azurewebsites.net so
// VNet-integrated callers (Operator, Device Gateway) resolve the Core API host name
// to its private IP instead of the disabled public endpoint (FR-020).
resource pepDnsZoneGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-01-01' = {
  parent: pep
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'privatelink-azurewebsites-net'
        properties: {
          privateDnsZoneId: privateDnsZoneId
        }
      }
    ]
  }
}

// Run-from-package: the system-assigned identity reads the deployment zip from the
// core storage account's app-packages container (keyless). Storage Blob Data Reader.
resource coreStorage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageAccountName
}

var storageBlobDataReaderRoleId = '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1'

resource corePackageReadAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(coreStorage.id, func.id, storageBlobDataReaderRoleId)
  scope: coreStorage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataReaderRoleId)
    principalId: func.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// T148 — Grant DeviceManagementServiceConfig.Read.All to ImagingCoreApi managed identity
// This enables the pre-flight authorization Microsoft Graph queries (FR-026)
// Note: Microsoft Graph app role assignments require a separate approach:
// The managed identity principalId must be granted this role via PowerShell/CLI post-deploy
// because Bicep does not natively support Graph API role assignments.
// The grant-graph-permissions.ps1 script handles this step.

output internalBaseUrl string = 'https://${func.properties.defaultHostName}'
output funcId string = func.id
