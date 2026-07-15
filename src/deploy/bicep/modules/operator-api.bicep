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
// userAuthClientId: Application (client) ID of the shared user-facing registration.
// Used as the Entra__SharedClientId app setting so the Operator API can validate
// tokens issued to users via the shared registration (e.g. Media Builder user flows).
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
    type: 'UserAssigned'
    userAssignedIdentities: { '${msiId}': {} }
  }
  properties: {
    serverFarmId: plan.id
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
      ]
    }
  }
}

output baseUrl string = 'https://${func.properties.defaultHostName}'
output funcId string = func.id
