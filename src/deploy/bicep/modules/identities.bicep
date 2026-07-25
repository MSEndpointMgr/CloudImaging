// modules/identities.bicep — Managed System Identities for all components

param location string
param msiDeviceGatewayName string
param msiOperatorApiName string
param msiImagingCoreName string
param msiPortalBackendName string

resource msiDeviceGateway 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: msiDeviceGatewayName
  location: location
}

resource msiOperatorApi 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: msiOperatorApiName
  location: location
}

resource msiImagingCore 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: msiImagingCoreName
  location: location
}

resource msiPortalBackend 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: msiPortalBackendName
  location: location
}

output deviceGatewayMsiId string = msiDeviceGateway.id
output deviceGatewayMsiClientId string = msiDeviceGateway.properties.clientId
output deviceGatewayPrincipalId string = msiDeviceGateway.properties.principalId
output operatorApiMsiId string = msiOperatorApi.id
output operatorApiMsiClientId string = msiOperatorApi.properties.clientId
output imagingCoreMsiId string = msiImagingCore.id
output imagingCoreMsiClientId string = msiImagingCore.properties.clientId
output imagingCorePrincipalId string = msiImagingCore.properties.principalId
output portalBackendMsiId string = msiPortalBackend.id
output portalBackendMsiClientId string = msiPortalBackend.properties.clientId
