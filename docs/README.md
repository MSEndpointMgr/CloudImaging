# Cloud Imaging documentation

Cloud-hosted Windows imaging for bare-metal devices running WinPE.

**Audience**: IT administrators and technicians deploying and running Cloud Imaging in their own Azure tenant

---

## Where to start

| Guide | Use it to |
|---|---|
| [Setup instructions](setup-instructions.md) | **Start here for a new deployment.** Prerequisites, Entra ID app registrations, deploying the Azure resources, and post-deployment/initial portal configuration, step by step |
| [Upgrade instructions](upgrade-instructions.md) | Move an existing deployment to a newer release: the three release streams, running `update.ps1`, applying infrastructure changes, rebuilding boot media, and rolling back |
| [Operations runbook](operations-runbook.md) | Run it day to day: monitoring, certificate rotation, upgrades, troubleshooting |
| [Roles and access](roles-and-access.md) | The full Entra ID app role and service role model |

## The three components

- **Cloud Imaging Client** — the WinPE application that runs on the device being imaged. It registers a session, waits to be coupled and assigned an OS image from the portal, then formats the disk, downloads and applies the image, configures boot, and applies the recovery environment.
- **Cloud Imaging Portal** — the web portal a technician uses to couple devices, assign OS images, monitor progress, and manage the OS image, recovery image, boot image and branding catalogs.
- **Cloud Imaging Media Builder** — the Windows desktop application that generates the WinPE boot image and prepares bootable USB media.

They talk to three Azure Functions APIs (Device Gateway, Operator, Imaging Core) deployed into your own tenant. See the [architecture overview](https://github.com/MSEndpointMgr/CloudImaging#architecture) in the repository README for the full component and resource diagram.

## Elsewhere

- [Releases](https://github.com/MSEndpointMgr/CloudImaging/releases) — the three independently-versioned release streams
- [Report an issue](https://github.com/MSEndpointMgr/CloudImaging/issues)
- [Security policy](https://github.com/MSEndpointMgr/CloudImaging/blob/main/SECURITY.md) — how to report a vulnerability
- [Contributing](https://github.com/MSEndpointMgr/CloudImaging/blob/main/CONTRIBUTING.md) — building the solution and submitting changes
