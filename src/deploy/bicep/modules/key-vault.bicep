// modules/key-vault.bicep — Azure Key Vault for boot media certificate PFX storage (FR-068, FR-069)

param location string
param keyVaultName string
param imagingCoreMsiPrincipalId string

@description('Resource ID of the private-endpoint subnet the Key Vault private endpoint is placed in.')
param pepSubnetId string

@description('Name of the Key Vault private endpoint.')
param pepName string

@description('Resource ID of the privatelink.vaultcore.azure.net private DNS zone used to auto-register the private endpoint A records.')
param privateDnsZoneId string

resource keyVault 'Microsoft.KeyVault/vaults@2024-04-01-preview' = {
  name: keyVaultName
  location: location
  properties: {
    sku: { family: 'A', name: 'standard' }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    enabledForDeployment: false
    enabledForTemplateDeployment: false
    enabledForDiskEncryption: false
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Deny'
    }
  }
}

// Grant Imaging Core API managed identity Key Vault Secrets Officer
// so it can store and retrieve certificate PFX blobs (FR-068)
var kvSecretsOfficerRoleId = 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'

resource roleAssignmentCore 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name: guid(keyVault.id, imagingCoreMsiPrincipalId, kvSecretsOfficerRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', kvSecretsOfficerRoleId)
    principalId: imagingCoreMsiPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// Private Endpoint — the Imaging Core API is VNet-integrated with route-all, and the
// vault's networkAcls default to Deny (no VNet rule), so runtime Key Vault data-plane
// calls (store/retrieve boot media PFX) must traverse Private Link. Private endpoint
// traffic bypasses the firewall entirely, so defaultAction=Deny stays in effect for
// the public endpoint (FR-068, FR-069).
resource pep 'Microsoft.Network/privateEndpoints@2024-01-01' = {
  name: pepName
  location: location
  properties: {
    subnet: { id: pepSubnetId }
    privateLinkServiceConnections: [
      {
        name: pepName
        properties: {
          privateLinkServiceId: keyVault.id
          groupIds: ['vault']
        }
      }
    ]
  }
}

// Registers the private endpoint's A records into privatelink.vaultcore.azure.net so
// the VNet-integrated Imaging Core API resolves the vault host name to its private IP.
resource pepDnsZoneGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-01-01' = {
  parent: pep
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'privatelink-vaultcore-azure-net'
        properties: {
          privateDnsZoneId: privateDnsZoneId
        }
      }
    ]
  }
}

output keyVaultId string = keyVault.id
output keyVaultUri string = keyVault.properties.vaultUri
