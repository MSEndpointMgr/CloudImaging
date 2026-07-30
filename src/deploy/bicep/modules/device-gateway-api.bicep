// modules/device-gateway-api.bicep
// Premium tier required for mTLS clientCertificateMode=require (FR-069).
// EP1 is the minimum SKU; self-hosters may scale to EP2/EP3 for higher throughput.

param location string
param funcName string
param planName string
param storageAccountName string
param appInsightsConnectionString string
param msiId string
param msiClientId string
param imagingCoreApiBaseUrl string
param keyVaultName string
@description('Resource ID of the dedicated Microsoft.Web/serverFarms-delegated subnet for the Device Gateway API regional VNet integration. Required so the app can reach the private-link-only Imaging Core API.')
param vnetSubnetId string
param deployApplicationGateway bool
param agwName string
// When Application Gateway is deployed the Function App restricts inbound traffic
// to the AGW subnet only (defence-in-depth, FR-042, FR-069).
// Set to the AGW subnet address prefix (e.g. '10.0.5.0/24').
// Ignored when deployApplicationGateway=false. Kept clear of the operator
// (10.0.3.0/24) and gateway (10.0.4.0/24) VNet-integration subnets.
param agwSubnetPrefix string = '10.0.5.0/24'
// Elastic Premium SKU for the Function App plan.
// Allowed: EP1 (default), EP2, EP3. Must remain ElasticPremium tier for mTLS support.
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
    // Regional VNet integration + route-all so calls to the Imaging Core API are
    // routed through the VNet and resolve via the private DNS zone (FR-064).
    virtualNetworkSubnetId: vnetSubnetId
    vnetRouteAllEnabled: true
    // mTLS: require client certificate on ALL inbound requests (FR-069, FR-011)
    clientCertEnabled: true
    clientCertMode: 'Required'
    siteConfig: {
      linuxFxVersion: 'DOTNET-ISOLATED|10'      // When Application Gateway is deployed, restrict inbound traffic to the AGW subnet.
      // This is a defence-in-depth layer on top of mTLS cert validation (T163/T164).
      ipSecurityRestrictions: deployApplicationGateway ? [
        {
          ipAddress: agwSubnetPrefix
          action: 'Allow'
          priority: 100
          name: 'AllowApplicationGatewaySubnet'
          description: 'Allow traffic from Application Gateway subnet only (FR-042)'
        }
        {
          ipAddress: 'Any'
          action: 'Deny'
          priority: 65000
          name: 'DenyAll'
          description: 'Deny all other inbound traffic'
        }
      ] : []
      appSettings: [
        { name: 'FUNCTIONS_WORKER_RUNTIME', value: 'dotnet-isolated' }
        { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
        { name: 'AzureWebJobsStorage__accountName', value: storageAccountName }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
        { name: 'AZURE_CLIENT_ID', value: msiClientId }
        { name: 'ImagingCoreApi__BaseUrl', value: imagingCoreApiBaseUrl }
        { name: 'KeyVault__VaultUri', value: 'https://${keyVaultName}${environment().suffixes.keyvaultDns}' }
        // Thumbprint cache refresh interval (max 60 seconds per FR-069)
        { name: 'Security__CertThumbprintCacheRefreshSeconds', value: '60' }
        // Route DNS through Azure-provided resolver so the VNet-linked private DNS
        // zone resolves the Imaging Core API private endpoint (FR-064).
        { name: 'WEBSITE_DNS_SERVER', value: '168.63.129.16' }
        { name: 'WEBSITE_VNET_ROUTE_ALL', value: '1' }
      ]
    }
  }
}

// Run-from-package: the system-assigned identity reads the deployment zip from the
// app storage account's app-packages container (keyless). Storage Blob Data Reader.
resource appStorage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageAccountName
}

var storageBlobDataReaderRoleId = '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1'

resource gatewayPackageReadAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(appStorage.id, func.id, storageBlobDataReaderRoleId)
  scope: appStorage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataReaderRoleId)
    principalId: func.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Enterprise tier: Azure Application Gateway WAF_v2 (optional, default false)
// When deployed, the Function App accepts traffic only from the AGW subnet
module agw 'application-gateway.bicep' = if (deployApplicationGateway) {
  name: 'agw'
  params: {
    location: location
    agwName: agwName
    backendFqdn: func.properties.defaultHostName
  }
}

output baseUrl string = 'https://${func.properties.defaultHostName}'
output funcId string = func.id
