# TODO: Networking API version and default outbound access (1.2.0)

Status: **not started**. Target: release 1.2.0. Nothing is broken today; this keeps it that way when
the networking Bicep is next touched.

## Context

Azure changed the default for new virtual networks: with a networking API version released after
31 March 2026, new subnets get `defaultOutboundAccess = false` (private subnets). Deployments that
use an earlier API version keep the old default.

[`networking.bicep`](../src/deploy/bicep/modules/networking.bicep) pins
`Microsoft.Network/virtualNetworks@2024-01-01` and `.../subnets@2024-01-01`, so new installs from
the Template Spec, whenever they happen, still get the old default. The three Function App subnets
are also delegated (`Microsoft.Web/serverFarms`), and Microsoft states that private subnets do not
apply to delegated PaaS subnets, whose outbound access the service manages. With route-all
enabled, the apps leave through their own outbound addresses.

So outbound access (Microsoft Graph, vendor driver catalogs, vendor downloads) depends on two
things holding: the pinned API version, and the delegated-subnet exemption.

## What to do in 1.2.0

- Before raising the `Microsoft.Network` API version in `networking.bicep`, decide the subnet
  default explicitly instead of inheriting the new one:
  - set `defaultOutboundAccess` on every subnet that is not delegated (the private endpoint
    subnet and the Application Gateway subnet), and
  - confirm the delegated Function App subnets still reach the internet in a test deployment.
- Re-check the Microsoft Learn pages for any change to the delegated-subnet exemption:
  [Default outbound access](https://learn.microsoft.com/azure/virtual-network/ip-services/default-outbound-access)
  and [App Service virtual network integration](https://learn.microsoft.com/azure/app-service/overview-vnet-integration).
- Decide whether to offer an optional NAT gateway for a fixed outbound IP. If it is added, Storage
  traffic from the apps must use service endpoints (`Microsoft.Storage`) or private endpoints, and
  existing installs need an upgrade path (Template Spec redeploy or an idempotent `upgrade.ps1`
  step).
- Add a check to the release validation that Imaging Core can reach a vendor catalog host and
  `graph.microsoft.com` after a fresh install.
