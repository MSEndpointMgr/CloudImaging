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
param deployApplicationGateway bool
param agwName string
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
    type: 'UserAssigned'
    userAssignedIdentities: { '${msiId}': {} }
  }
  properties: {
    serverFarmId: plan.id
    // mTLS: require client certificate on ALL inbound requests (FR-069, FR-011)
    clientCertEnabled: true
    clientCertMode: 'Required'
    siteConfig: {
      linuxFxVersion: 'DOTNET-ISOLATED|10'
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
      ]
    }
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
