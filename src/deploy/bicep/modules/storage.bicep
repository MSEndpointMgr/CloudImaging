// modules/storage.bicep — Storage Accounts for portal assets and ImagingCoreApi blobs

param location string
param storageAppName string      // Portal frontend, deployment assets
param storageCoreApiName string  // ImagingCoreApi: sessions, OS images, boot images

@description('Principal ID of the Device Gateway API user-assigned managed identity. Granted full blob/queue/table data-plane access on the app storage account for the host runtime, the device-session token store, the boot-media thumbprint cache and the proof-of-possession nonce store (FR-069).')
param deviceGatewayMsiPrincipalId string

@description('Principal ID of the Operator API user-assigned managed identity. Granted full blob/queue/table data-plane access on the app storage account (host runtime + operator data operations).')
param operatorMsiPrincipalId string

@description('Principal ID of the Imaging Core API user-assigned managed identity. Granted full blob/queue/table data-plane access on the core storage account (host runtime, OS/boot images, branding, session and catalog tables).')
param imagingCoreMsiPrincipalId string

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

// Deployment package container for the Imaging Core API (WEBSITE_RUN_FROM_PACKAGE).
// Code is deployed by uploading a versioned zip here and pointing the Function App
// at it; the app reads it using its managed identity (Storage Blob Data Reader).
resource containerCoreAppPackages 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobServiceCore
  name: 'app-packages'
  properties: { publicAccess: 'None' }
}

// Client-uploaded diagnostic logs, one blob per upload under a {sessionId}/ prefix, uploaded
// best-effort by the Cloud Imaging Client on any terminal imaging failure so support can inspect
// a device's full local log without relying on the technician retrieving it from WinPE before
// reboot. Auto-expired after 90 days by the lifecycle policy below to bound storage/PII exposure.
resource containerSessionLogs 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobServiceCore
  name: 'session-logs'
  properties: { publicAccess: 'None' }
}

// Lifecycle policy: delete session log blobs 90 days after last modification. Scoped only to the
// session-logs/ prefix so it never touches os-images/boot-images/branding/app-packages.
resource storageCoreLifecyclePolicy 'Microsoft.Storage/storageAccounts/managementPolicies@2023-05-01' = {
  parent: storageCoreApi
  name: 'default'
  properties: {
    policy: {
      rules: [
        {
          enabled: true
          name: 'expire-session-logs'
          type: 'Lifecycle'
          definition: {
            filters: {
              blobTypes: [ 'blockBlob' ]
              prefixMatch: [ 'session-logs/' ]
            }
            actions: {
              baseBlob: {
                delete: { daysAfterModificationGreaterThan: 90 }
              }
            }
          }
        }
      ]
    }
  }
}

// Blob service + deployment package container for the shared app storage account,
// used for the Operator API and Device Gateway API run-from-package deployments.
resource blobServiceApp 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storageApp
  name: 'default'
}

resource containerAppPackages 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobServiceApp
  name: 'app-packages'
  properties: { publicAccess: 'None' }
}

// ── RBAC: data-plane access for component managed identities ───────────────────
// Built-in data-plane roles. Each Function App reaches its storage account with
// its user-assigned managed identity (DefaultAzureCredential via AZURE_CLIENT_ID)
// for the host runtime (AzureWebJobsStorage), queues, and table/blob data. Without
// these grants the host cannot start and data operations return HTTP 403. The
// deterministic guid() names make every assignment idempotent across redeploys.
var storageBlobDataOwnerRoleId = 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
var storageQueueDataContributorRoleId = '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
var storageTableDataContributorRoleId = '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'

var dataPlaneRoleIds = [
  storageBlobDataOwnerRoleId
  storageQueueDataContributorRoleId
  storageTableDataContributorRoleId
]

// Operator API user MSI on the shared app storage account.
resource operatorStorageRbac 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for roleId in dataPlaneRoleIds: {
    name: guid(storageApp.id, operatorMsiPrincipalId, roleId)
    scope: storageApp
    properties: {
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleId)
      principalId: operatorMsiPrincipalId
      principalType: 'ServicePrincipal'
    }
  }
]

// Device Gateway API user MSI on the shared app storage account.
resource gatewayStorageRbac 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for roleId in dataPlaneRoleIds: {
    name: guid(storageApp.id, deviceGatewayMsiPrincipalId, roleId)
    scope: storageApp
    properties: {
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleId)
      principalId: deviceGatewayMsiPrincipalId
      principalType: 'ServicePrincipal'
    }
  }
]

// Imaging Core API user MSI on the core storage account.
resource coreStorageRbac 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for roleId in dataPlaneRoleIds: {
    name: guid(storageCoreApi.id, imagingCoreMsiPrincipalId, roleId)
    scope: storageCoreApi
    properties: {
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleId)
      principalId: imagingCoreMsiPrincipalId
      principalType: 'ServicePrincipal'
    }
  }
]

output storageAppId string = storageApp.id
output storageCoreApiId string = storageCoreApi.id
output storageCoreApiName string = storageCoreApi.name
