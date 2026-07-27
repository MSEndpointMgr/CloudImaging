// modules/cloud-imaging-portal.bicep — Portal backend (App Service) + frontend (Static Web Apps)

param location string
param appServiceName string
param planName string
param stappName string
param appInsightsConnectionString string
param msiId string
param msiClientId string
param operatorApiBaseUrl string
// sharedEntraClientId: Application (client) ID of the Cloud Imaging Portal SPA registration.
// Used by the portal backend (ENTRA_CLIENT_ID) to validate browser sign-in tokens.
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
      linuxFxVersion: 'NODE|22-lts'
      // Explicit ESM entry point. Without this, Oryx guesses `node app.js`, which fails
      // with "Cannot use import statement outside a module" because the backend is ESM
      // ("type": "module"). The deploy bundle places package.json + node_modules + dist/
      // at wwwroot root, so `node dist/index.js` resolves runtime deps correctly.
      appCommandLine: 'node dist/index.js'
      appSettings: [
        { name: 'NODE_ENV', value: 'production' }
        // Do NOT use WEBSITE_RUN_FROM_PACKAGE=1 here: the value `1` is a Windows-only
        // feature. On Linux App Service the container leaves wwwroot mounted empty on a
        // cold restart, so `dist/index.js` disappears and the app crash-loops with
        // MODULE_NOT_FOUND (works right after deploy, breaks on the first platform
        // restart). Instead the deploy zip is extracted to the persistent /home/site/wwwroot
        // Azure Files share, which survives restarts. SCM_DO_BUILD_DURING_DEPLOYMENT is
        // pinned false because the bundle already ships prebuilt dist + node_modules, so
        // Oryx must extract only (never run a server-side npm build).
        { name: 'SCM_DO_BUILD_DURING_DEPLOYMENT', value: 'false' }
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

// Explicitly keep App Service Authentication (EasyAuth) disabled. The backend validates
// Entra JWTs itself in Express middleware (and serves /api/config + /api/health
// anonymously). If EasyAuth is ever toggled on without a fully configured identity
// provider, its middleware returns a blanket HTTP 400 for every request, so we pin it
// off here to keep deployments deterministic.
resource appServiceAuth 'Microsoft.Web/sites/config@2024-04-01' = {
  parent: appService
  name: 'authsettingsV2'
  properties: {
    globalValidation: {
      requireAuthentication: false
      unauthenticatedClientAction: 'AllowAnonymous'
    }
    platform: {
      enabled: false
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

// Link the App Service backend to the Static Web App. Requests to the portal
// origin's /api/* routes are proxied to the Node backend, giving the SPA a single
// origin (no CORS, no build-time API base URL) and letting it fetch its Entra
// runtime configuration from /api/config. Requires the SWA Standard plan.
resource stappBackend 'Microsoft.Web/staticSites/linkedBackends@2024-04-01' = {
  parent: stapp
  name: 'portal-backend'
  properties: {
    backendResourceId: appService.id
    region: location
  }
}

output portalUrl string = 'https://${stapp.properties.defaultHostname}'
output backendUrl string = 'https://${appService.properties.defaultHostName}'
output staticWebAppName string = stapp.name
