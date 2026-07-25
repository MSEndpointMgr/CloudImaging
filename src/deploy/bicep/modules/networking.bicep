// modules/networking.bicep — VNet, subnets, NSG

param location string
param vnetName string
param vnetAddressPrefix string
param apiFunctionSubnetName string
param apiFunctionSubnetPrefix string
param privateEndpointSubnetName string
param privateEndpointSubnetPrefix string
param nsgFunctionsName string

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

output vnetId string = vnet.id
output apiFunctionSubnetId string = apiFunctionSubnet.id
output privateEndpointSubnetId string = privateEndpointSubnet.id
