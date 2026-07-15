// modules/cloud-imaging-portal.bicep — Portal backend (App Service) + frontend (Static Web Apps)

param location string
param appServiceName string
param planName string
param stappName string
param appInsightsConnectionString string
param msiId string
param msiClientId string
param operatorApiBaseUrl string
// userAuthClientId: Application (client) ID of the shared user-facing registration.
// Used by portal frontend (MSAL) and portal backend (token validation) for user login.
param sharedEntraClientId string
param tenantId string
// App Service plan SKU for the Portal backend.
// Allowed: P1v3 (default), P2v3, P3v3 (PremiumV3 tier required for VNet integration).
@allowed(['P1v3','P2v3','P3v3'])
param appServiceSku string = 'P1v3'

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: planName
  location: location
  sku: { name: appServiceSku, tier: 'PremiumV3' }
  properties: { reserved: true }
}

resource appService 'Microsoft.Web/sites@2024-04-01' = {
  name: appServiceName
  location: location
  kind: 'app,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${msiId}': {} }
  }
  properties: {
    serverFarmId: plan.id
    siteConfig: {
      linuxFxVersion: 'NODE|22'
      appSettings: [
        { name: 'NODE_ENV', value: 'production' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
        { name: 'AZURE_CLIENT_ID', value: msiClientId }
        { name: 'OPERATOR_API_BASE_URL', value: operatorApiBaseUrl }
        { name: 'ENTRA_CLIENT_ID', value: sharedEntraClientId }
        { name: 'ENTRA_TENANT_ID', value: tenantId }
        { name: 'ENTRA_AUTHORITY', value: '${environment().authentication.loginEndpoint}${tenantId}' }
        // CORS configured to allow Static Web App origin
        { name: 'CORS_ALLOWED_ORIGINS', value: 'https://${stapp.properties.defaultHostname}' }
      ]
    }
  }
}

resource stapp 'Microsoft.Web/staticSites@2024-04-01' = {
  name: stappName
  location: location
  sku: { name: 'Standard', tier: 'Standard' }
  properties: {
    repositoryUrl: ''
    branch: ''
    buildProperties: {
      appLocation: 'src/cloud-imaging-portal/client'
      outputLocation: 'dist'
    }
  }
}

output portalUrl string = 'https://${stapp.properties.defaultHostname}'
output backendUrl string = 'https://${appService.properties.defaultHostName}'
output staticWebAppName string = stapp.name
