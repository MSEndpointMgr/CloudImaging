// modules/storage.bicep — Storage Accounts for portal assets and ImagingCoreApi blobs

param location string
param storageAppName string      // Portal frontend, deployment assets
param storageCoreApiName string  // ImagingCoreApi: sessions, OS images, boot images

@description('Principal ID of the Device Gateway API managed identity. Granted Storage Table Data Contributor on the app storage account for the device-session token store, boot-media thumbprint cache, and single-use proof-of-possession nonce store (FR-069).')
param deviceGatewayMsiPrincipalId string

resource storageApp 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAppName
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
  }
}

resource storageCoreApi 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageCoreApiName
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
  }
}

// Blob containers for ImagingCoreApi
resource blobServiceCore 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storageCoreApi
  name: 'default'
}

resource containerOsImages 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobServiceCore
  name: 'os-images'
  properties: { publicAccess: 'None' }
}

resource containerBootImages 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobServiceCore
  name: 'boot-images'
  properties: { publicAccess: 'None' }
}

resource containerBranding 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobServiceCore
  name: 'branding'
  properties: { publicAccess: 'None' }
}

// ── RBAC: data-plane access for component managed identities ───────────────────
// Storage Table Data Contributor (built-in role). The Device Gateway API uses
// DefaultAzureCredential (its user-assigned MSI) to reach Table Storage on this
// account for the device-session token store, the boot-media certificate
// thumbprint cache, and the single-use proof-of-possession nonce store (FR-069).
// Without this grant those table operations return HTTP 403, and session
// bootstrap fails closed. Provisioned here so every deployment configures it
// automatically. The deterministic guid() name makes the assignment idempotent.
var storageTableDataContributorRoleId = '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'

resource deviceGatewayTableAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageApp.id, deviceGatewayMsiPrincipalId, storageTableDataContributorRoleId)
  scope: storageApp
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageTableDataContributorRoleId)
    principalId: deviceGatewayMsiPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output storageAppId string = storageApp.id
output storageCoreApiId string = storageCoreApi.id
output storageCoreApiName string = storageCoreApi.name
