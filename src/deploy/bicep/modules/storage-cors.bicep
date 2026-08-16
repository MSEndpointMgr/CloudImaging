// modules/storage-cors.bicep — Blob service CORS for direct browser-to-blob uploads.
//
// The portal client (Boot Images page) uploads boot media WIM files directly to Blob
// Storage via a short-lived write-SAS URL (T128, FR-063) rather than proxying the bytes
// through the portal backend — a browser XHR `PUT` straight to
// `https://<storageCoreApiName>.blob.core.windows.net/boot-images/...`. Browsers require
// the STORAGE ACCOUNT itself (not just the portal backend) to answer the CORS preflight
// for that cross-origin request; without a matching `corsRules` entry the preflight
// `OPTIONS` fails with 403 "CORS not enabled or no matching rule found for this request."
// and the upload never starts (found + fixed 2026-08-16).
//
// Deployed as its OWN module — deliberately depending on `cloudImagingPortal` (for the
// Static Web App's auto-generated hostname) rather than folding this into storage.bicep,
// which is a dependency of imagingCoreApi/deviceGatewayApi/operatorApi/cloudImagingPortal
// itself. Adding that dependency there would create a circular module reference. Nothing
// depends on THIS module, so it can safely deploy last without affecting ordering elsewhere.

param storageCoreApiName string
param portalOrigin string

resource storageCoreApi 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageCoreApiName
}

resource blobServiceCore 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storageCoreApi
  name: 'default'
  properties: {
    cors: {
      corsRules: [
        {
          allowedOrigins: [portalOrigin]
          allowedMethods: ['GET', 'HEAD', 'PUT', 'OPTIONS']
          allowedHeaders: ['content-type', 'x-ms-blob-type', 'x-ms-version', 'x-ms-date']
          exposedHeaders: ['*']
          maxAgeInSeconds: 3600
        }
      ]
    }
  }
}
