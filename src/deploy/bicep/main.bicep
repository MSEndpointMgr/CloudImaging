// CloudImaging — main.bicep
// Azure Template Spec entry point for community deployment (FR-041, FR-042).
// Published via publish-template-spec.ps1 and deployed through the Azure Portal
// Template Spec wizard (uiFormDefinition.json).

targetScope = 'resourceGroup'

// ── Parameters ───────────────────────────────────────────────────────────────

@maxLength(4)
@description('Short alphanumeric organization identifier used as the leading segment of all resource names.')
param resourcePrefix string

@maxLength(4)
@description('Deployment environment label (dev, prod, or custom abbreviation).')
param environment string = 'dev'

@description('Application (client) ID of the Cloud Imaging Portal SPA Entra ID app registration. This is the audience the Portal backend validates browser sign-in tokens against.')
param portalClientId string

@description('Application (client) ID of the Cloud Imaging Media Builder (native public client) Entra ID app registration. Used by the Operator API to validate user tokens originating from the Media Builder.')
param mediaBuilderClientId string

@description('Entra ID tenant ID (auto-populated from portal session).')
param tenantId string = tenant().tenantId

@description('Deploy Azure Application Gateway WAF_v2 in front of Device Gateway API (Enterprise tier).')
param deployApplicationGateway bool = false

@description('Boot media certificate validity period in days.')
param certValidityPeriodDays int = 365

@description('Passcode TTL in minutes.')
param passcodeTtlMinutes int = 30

@description('Application (client) ID of the manually-created Operator API Entra app registration. Used to configure the Operator API token validation audience (FR-040b).')
param operatorApiClientId string

@description('OS image SAS token URL expiry in minutes.')
param sasTokenExpiryMinutes int = 240

@description('Boot image SAS token URL expiry in minutes.')
param bootImageSasExpiryMinutes int = 120

@description('Clock skew tolerance for token expiry validation in seconds.')
param clockSkewToleranceSeconds int = 30

@description('VNet address prefix.')
param vnetAddressPrefix string = '10.0.0.0/16'

@description('Subnet for Function Apps (Device Gateway, Operator, Imaging Core).')
param apiFunctionSubnetPrefix string = '10.0.1.0/24'

@description('Subnet for Private Endpoints.')
param privateEndpointSubnetPrefix string = '10.0.2.0/24'

@description('Dedicated delegated subnet for the Operator API Function App regional VNet integration.')
param operatorSubnetPrefix string = '10.0.3.0/24'

@description('Dedicated delegated subnet for the Device Gateway API Function App regional VNet integration.')
param gatewaySubnetPrefix string = '10.0.4.0/24'

@description('Azure region for all resources.')
param location string = resourceGroup().location

@description('Elastic Premium SKU for all API Function App plans. EP1 is required as the minimum for mTLS, VNet integration, and pre-warmed instances.')
@allowed(['EP1','EP2','EP3'])
param functionAppSku string = 'EP1'

@description('PremiumV3 SKU for the Cloud Imaging Portal backend App Service plan.')
@allowed(['P1v3','P2v3','P3v3'])
param appServiceSku string = 'P1v3'

// ── Naming convention helpers ─────────────────────────────────────────────────
// Pattern: {prefix}-{env}-{type}[-{name}] for all resources
// Storage Accounts: {prefix}{env}st{purpose} (no hyphens, max 24 chars)

var prefix = '${resourcePrefix}-${environment}'
var storagePrefix = '${resourcePrefix}${environment}'

// Resource names
var names = {
  // Networking
  vnet: '${prefix}-vnet'
  apiFunctionSubnet: '${prefix}-snet-functions'
  privateEndpointSubnet: '${prefix}-snet-pe'
  operatorSubnet: '${prefix}-snet-operator'
  gatewaySubnet: '${prefix}-snet-gateway'
  nsgFunctions: '${prefix}-nsg-functions'

  // Storage
  storageApp: '${storagePrefix}stapp'       // Portal frontend Static Web App assets
  storageCoreApi: '${storagePrefix}stcore'  // ImagingCoreApi: session state, OS images, boot images

  // Key Vault (singleton — no name segment)
  keyVault: '${prefix}-kv'

  // Log Analytics + Application Insights (singleton)
  logAnalytics: '${prefix}-log'
  appInsights: '${prefix}-appi'

  // Managed System Identities
  msiDeviceGateway: '${prefix}-msi-gateway'
  msiOperatorApi: '${prefix}-msi-operator'
  msiImagingCore: '${prefix}-msi-core'
  msiPortalBackend: '${prefix}-msi-portal'

  // App Service Plans (Premium EP1 required for mTLS and Private Link)
  planDeviceGateway: '${prefix}-plan-gateway'
  planOperatorApi: '${prefix}-plan-operator'
  planImagingCore: '${prefix}-plan-core'
  planPortalBackend: '${prefix}-plan-portal'

  // Function Apps
  funcDeviceGateway: '${prefix}-func-gateway'
  funcOperatorApi: '${prefix}-func-operator'
  funcImagingCore: '${prefix}-func-core'

  // App Service (Portal backend)
  appPortalBackend: '${prefix}-app-portal'

  // Static Web Apps (Portal frontend)
  stappPortalFrontend: '${prefix}-stapp-portal'

  // Private Endpoints
  pepImagingCore: '${prefix}-pep-core'

  // Application Gateway (Enterprise tier)
  agw: '${prefix}-agw'
}

// ── Module calls ──────────────────────────────────────────────────────────────

module observability 'modules/observability.bicep' = {
  name: 'observability'
  params: {
    location: location
    logAnalyticsName: names.logAnalytics
    appInsightsName: names.appInsights
  }
}

module networking 'modules/networking.bicep' = {
  name: 'networking'
  params: {
    location: location
    vnetName: names.vnet
    vnetAddressPrefix: vnetAddressPrefix
    apiFunctionSubnetName: names.apiFunctionSubnet
    apiFunctionSubnetPrefix: apiFunctionSubnetPrefix
    privateEndpointSubnetName: names.privateEndpointSubnet
    privateEndpointSubnetPrefix: privateEndpointSubnetPrefix
    operatorSubnetName: names.operatorSubnet
    operatorSubnetPrefix: operatorSubnetPrefix
    gatewaySubnetName: names.gatewaySubnet
    gatewaySubnetPrefix: gatewaySubnetPrefix
    nsgFunctionsName: names.nsgFunctions
  }
}

module storage 'modules/storage.bicep' = {
  name: 'storage'
  params: {
    location: location
    storageAppName: names.storageApp
    storageCoreApiName: names.storageCoreApi
    deviceGatewayMsiPrincipalId: identities.outputs.deviceGatewayPrincipalId
    operatorMsiPrincipalId: identities.outputs.operatorApiPrincipalId
    imagingCoreMsiPrincipalId: identities.outputs.imagingCorePrincipalId
  }
}

module keyVault 'modules/key-vault.bicep' = {
  name: 'key-vault'
  params: {
    location: location
    keyVaultName: names.keyVault
    imagingCoreMsiPrincipalId: identities.outputs.imagingCorePrincipalId
  }
}

module identities 'modules/identities.bicep' = {
  name: 'identities'
  params: {
    location: location
    msiDeviceGatewayName: names.msiDeviceGateway
    msiOperatorApiName: names.msiOperatorApi
    msiImagingCoreName: names.msiImagingCore
    msiPortalBackendName: names.msiPortalBackend
  }
}

module imagingCoreApi 'modules/imaging-core-api.bicep' = {
  name: 'imaging-core-api'
  dependsOn: [storage]
  params: {
    location: location
    funcName: names.funcImagingCore
    planName: names.planImagingCore
    storageAccountName: names.storageCoreApi
    appInsightsConnectionString: observability.outputs.appInsightsConnectionString
    msiId: identities.outputs.imagingCoreMsiId
    msiClientId: identities.outputs.imagingCoreMsiClientId
    vnetSubnetId: networking.outputs.apiFunctionSubnetId
    pepSubnetId: networking.outputs.privateEndpointSubnetId
    pepName: names.pepImagingCore
    privateDnsZoneId: networking.outputs.appServicePrivateDnsZoneId
    keyVaultName: names.keyVault
    sharedEntraClientId: portalClientId
    tenantId: tenantId
    passcodeTtlMinutes: passcodeTtlMinutes
    sasTokenExpiryMinutes: sasTokenExpiryMinutes
    bootImageSasExpiryMinutes: bootImageSasExpiryMinutes
    certValidityPeriodDays: certValidityPeriodDays
    clockSkewToleranceSeconds: clockSkewToleranceSeconds
    functionAppSku: functionAppSku
  }
}

module deviceGatewayApi 'modules/device-gateway-api.bicep' = {
  name: 'device-gateway-api'
  dependsOn: [storage]
  params: {
    location: location
    funcName: names.funcDeviceGateway
    planName: names.planDeviceGateway
    storageAccountName: names.storageApp
    appInsightsConnectionString: observability.outputs.appInsightsConnectionString
    msiId: identities.outputs.deviceGatewayMsiId
    msiClientId: identities.outputs.deviceGatewayMsiClientId
    imagingCoreApiBaseUrl: imagingCoreApi.outputs.internalBaseUrl
    keyVaultName: names.keyVault
    vnetSubnetId: networking.outputs.gatewaySubnetId
    deployApplicationGateway: deployApplicationGateway
    agwName: names.agw
    functionAppSku: functionAppSku
  }
}

module operatorApi 'modules/operator-api.bicep' = {
  name: 'operator-api'
  dependsOn: [storage]
  params: {
    location: location
    funcName: names.funcOperatorApi
    planName: names.planOperatorApi
    storageAccountName: names.storageApp
    appInsightsConnectionString: observability.outputs.appInsightsConnectionString
    msiId: identities.outputs.operatorApiMsiId
    msiClientId: identities.outputs.operatorApiMsiClientId
    imagingCoreApiBaseUrl: imagingCoreApi.outputs.internalBaseUrl
    vnetSubnetId: networking.outputs.operatorSubnetId
    sharedEntraClientId: mediaBuilderClientId
    operatorApiClientId: operatorApiClientId
    tenantId: tenantId
    functionAppSku: functionAppSku
  }
}

module cloudImagingPortal 'modules/cloud-imaging-portal.bicep' = {
  name: 'cloud-imaging-portal'
  params: {
    location: location
    appServiceName: names.appPortalBackend
    planName: names.planPortalBackend
    stappName: names.stappPortalFrontend
    appInsightsConnectionString: observability.outputs.appInsightsConnectionString
    msiId: identities.outputs.portalBackendMsiId
    msiClientId: identities.outputs.portalBackendMsiClientId
    operatorApiBaseUrl: operatorApi.outputs.baseUrl
    sharedEntraClientId: portalClientId
    tenantId: tenantId
    appServiceSku: appServiceSku
  }
}

// ── Outputs ───────────────────────────────────────────────────────────────────

output portalUrl string = cloudImagingPortal.outputs.portalUrl
output deviceGatewayApiUrl string = deviceGatewayApi.outputs.baseUrl
output operatorApiUrl string = operatorApi.outputs.baseUrl
output resourceNames object = names
