// modules/operator-api.bicep — Entra-authenticated operator gateway
// Premium tier for pre-warmed instances (99.5% uptime target SC-016).
// EP1 is the minimum SKU; self-hosters may scale to EP2/EP3.

param location string
param funcName string
param planName string
param storageAccountName string
param appInsightsConnectionString string
param msiId string
param msiClientId string
param imagingCoreApiBaseUrl string
@description('Resource ID of the dedicated Microsoft.Web/serverFarms-delegated subnet for the Operator API regional VNet integration. Required so the app can reach the private-link-only Imaging Core API.')
param vnetSubnetId string
// sharedEntraClientId: Application (client) ID of the Cloud Imaging Media Builder registration.
// Used as the Entra__SharedClientId app setting so the Operator API can validate
// tokens issued to users via the Media Builder (native public client) sign-in flow.
param sharedEntraClientId string
// Application (client) ID of the Operator API's own Entra app registration (FR-040b).
// This is the audience the Operator API validates incoming tokens against.
// Created manually before deployment as a prerequisite — not Bicep-provisioned.
param operatorApiClientId string
param tenantId string
// Elastic Premium SKU for the Function App plan.
// Allowed: EP1 (default), EP2, EP3. Must remain ElasticPremium tier.
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
    siteConfig: {
      linuxFxVersion: 'DOTNET-ISOLATED|10'
      appSettings: [
        { name: 'FUNCTIONS_WORKER_RUNTIME', value: 'dotnet-isolated' }
        { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
        { name: 'AzureWebJobsStorage__accountName', value: storageAccountName }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
        { name: 'AZURE_CLIENT_ID', value: msiClientId }
        { name: 'ImagingCoreApi__BaseUrl', value: imagingCoreApiBaseUrl }
        { name: 'Entra__TenantId', value: tenantId }
        // ClientId used for the Operator API's OWN token validation audience
        { name: 'Entra__ClientId', value: operatorApiClientId }
        // SharedClientId used when Operator API makes outbound calls on behalf of user context
        { name: 'Entra__SharedClientId', value: sharedEntraClientId }
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

resource operatorPackageReadAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(appStorage.id, func.id, storageBlobDataReaderRoleId)
  scope: appStorage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataReaderRoleId)
    principalId: func.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

output baseUrl string = 'https://${func.properties.defaultHostName}'
output funcId string = func.id
