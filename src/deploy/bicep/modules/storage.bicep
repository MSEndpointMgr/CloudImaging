// modules/storage.bicep — Storage Accounts for portal assets and ImagingCoreApi blobs

param location string
param storageAppName string      // Portal frontend, deployment assets
param storageCoreApiName string  // ImagingCoreApi: sessions, OS images, boot images

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

output storageAppId string = storageApp.id
output storageCoreApiId string = storageCoreApi.id
output storageCoreApiName string = storageCoreApi.name
