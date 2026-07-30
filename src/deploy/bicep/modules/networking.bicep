// modules/networking.bicep — VNet, subnets, NSG, Private DNS

param location string
param vnetName string
param vnetAddressPrefix string
param apiFunctionSubnetName string
param apiFunctionSubnetPrefix string
param privateEndpointSubnetName string
param privateEndpointSubnetPrefix string
param nsgFunctionsName string

@description('Delegated subnet for the Operator API Function App regional VNet integration. Each Function App plan requires its own dedicated Microsoft.Web/serverFarms-delegated subnet.')
param operatorSubnetName string
param operatorSubnetPrefix string

@description('Delegated subnet for the Device Gateway API Function App regional VNet integration.')
param gatewaySubnetName string
param gatewaySubnetPrefix string

@description('Private DNS zone name used to resolve the private-endpoint address of the Imaging Core API. Must be the App Service private-link zone.')
param appServicePrivateDnsZoneName string = 'privatelink.azurewebsites.net'

resource nsgFunctions 'Microsoft.Network/networkSecurityGroups@2024-01-01' = {
  name: nsgFunctionsName
  location: location
  properties: {
    securityRules: [
      {
        name: 'AllowHttpsInbound'
        properties: {
          priority: 100
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourcePortRange: '*'
          destinationPortRange: '443'
          sourceAddressPrefix: 'AzureFrontDoor.Backend'
          destinationAddressPrefix: '*'
        }
      }
    ]
  }
}

// Subnets are declared as standalone child resources (not inline) so that
// redeploys do not reconcile the subnets array on the VNet resource. The
// functions subnet carries a system-managed serviceAssociationLink from the
// Function App's regional VNet integration (Microsoft.Web/serverFarms); an
// inline VNet PUT tries to drop that link and fails with
// InUseSubnetCannotBeUpdated. Omitting subnets from the VNet body preserves
// existing subnets, and standalone subnet PUTs are idempotent no-ops when the
// configuration is unchanged.
resource vnet 'Microsoft.Network/virtualNetworks@2024-01-01' = {
  name: vnetName
  location: location
  properties: {
    addressSpace: {
      addressPrefixes: [vnetAddressPrefix]
    }
  }
}

resource apiFunctionSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-01-01' = {
  parent: vnet
  name: apiFunctionSubnetName
  properties: {
    addressPrefix: apiFunctionSubnetPrefix
    networkSecurityGroup: { id: nsgFunctions.id }
    delegations: [
      {
        name: 'delegation'
        properties: { serviceName: 'Microsoft.Web/serverFarms' }
      }
    ]
    privateEndpointNetworkPolicies: 'Disabled'
  }
}

// Subnets on the same VNet cannot be created/updated in parallel, so chain the
// private-endpoint subnet after the functions subnet.
resource privateEndpointSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-01-01' = {
  parent: vnet
  name: privateEndpointSubnetName
  properties: {
    addressPrefix: privateEndpointSubnetPrefix
    privateEndpointNetworkPolicies: 'Disabled'
  }
  dependsOn: [apiFunctionSubnet]
}

// Dedicated delegated subnet for the Operator API's regional VNet integration.
// A subnet delegated to Microsoft.Web/serverFarms can only back a single App
// Service plan, so the Operator, Device Gateway and Imaging Core apps (each on
// its own plan) require separate integration subnets. Chained to serialise
// subnet writes on the shared VNet.
resource operatorSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-01-01' = {
  parent: vnet
  name: operatorSubnetName
  properties: {
    addressPrefix: operatorSubnetPrefix
    networkSecurityGroup: { id: nsgFunctions.id }
    delegations: [
      {
        name: 'delegation'
        properties: { serviceName: 'Microsoft.Web/serverFarms' }
      }
    ]
    privateEndpointNetworkPolicies: 'Disabled'
  }
  dependsOn: [privateEndpointSubnet]
}

// Dedicated delegated subnet for the Device Gateway API's regional VNet integration.
resource gatewaySubnet 'Microsoft.Network/virtualNetworks/subnets@2024-01-01' = {
  parent: vnet
  name: gatewaySubnetName
  properties: {
    addressPrefix: gatewaySubnetPrefix
    networkSecurityGroup: { id: nsgFunctions.id }
    delegations: [
      {
        name: 'delegation'
        properties: { serviceName: 'Microsoft.Web/serverFarms' }
      }
    ]
    privateEndpointNetworkPolicies: 'Disabled'
  }
  dependsOn: [operatorSubnet]
}

// Private DNS zone for the App Service private link. Resolves the Imaging Core
// API's *.azurewebsites.net host name to its private-endpoint address so the
// VNet-integrated Operator and Device Gateway apps reach it over Private Link.
// The A records are populated automatically by the private endpoint's
// privateDnsZoneGroup (see imaging-core-api.bicep). Without this zone the host
// name resolves to the disabled public endpoint and callers receive HTTP 403.
resource appServicePrivateDnsZone 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: appServicePrivateDnsZoneName
  location: 'global'
}

resource appServicePrivateDnsZoneVnetLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  parent: appServicePrivateDnsZone
  name: '${vnetName}-link'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: { id: vnet.id }
  }
}

output vnetId string = vnet.id
output apiFunctionSubnetId string = apiFunctionSubnet.id
output privateEndpointSubnetId string = privateEndpointSubnet.id
output operatorSubnetId string = operatorSubnet.id
output gatewaySubnetId string = gatewaySubnet.id
output appServicePrivateDnsZoneId string = appServicePrivateDnsZone.id
