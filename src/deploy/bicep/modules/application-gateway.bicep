// modules/application-gateway.bicep — Enterprise tier WAF_v2 in front of Device Gateway API
// Only deployed when deployApplicationGateway=true (FR-042, FR-069)

param location string
param agwName string
param backendFqdn string

resource agw 'Microsoft.Network/applicationGateways@2024-01-01' = {
  name: agwName
  location: location
  properties: {
    sku: {
      name: 'WAF_v2'
      tier: 'WAF_v2'
      capacity: 1
    }
    gatewayIPConfigurations: []   // Populated during full implementation
    frontendIPConfigurations: []
    frontendPorts: []
    httpListeners: []
    requestRoutingRules: []
    backendAddressPools: [
      {
        name: 'deviceGatewayBackend'
        properties: {
          backendAddresses: [{ fqdn: backendFqdn }]
        }
      }
    ]
    backendHttpSettingsCollection: []
    webApplicationFirewallConfiguration: {
      enabled: true
      firewallMode: 'Prevention'
      ruleSetType: 'OWASP'
      ruleSetVersion: '3.2'
    }
  }
}

output agwId string = agw.id
output agwPublicIp string = ''
