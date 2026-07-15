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

resource vnet 'Microsoft.Network/virtualNetworks@2024-01-01' = {
  name: vnetName
  location: location
  properties: {
    addressSpace: {
      addressPrefixes: [vnetAddressPrefix]
    }
    subnets: [
      {
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
      {
        name: privateEndpointSubnetName
        properties: {
          addressPrefix: privateEndpointSubnetPrefix
          privateEndpointNetworkPolicies: 'Disabled'
        }
      }
    ]
  }
}

output vnetId string = vnet.id
output apiFunctionSubnetId string = vnet.properties.subnets[0].id
output privateEndpointSubnetId string = vnet.properties.subnets[1].id
