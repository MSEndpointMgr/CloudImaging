# CloudImaging

Cloud-hosted Windows imaging for bare-metal devices running WinPE.

---

## Overview

CloudImaging provisions Windows OS images from Azure Blob Storage to bare-metal
devices over the network. A technician boots a target device from a prepared
USB drive; a lightweight client launches automatically in WinPE and displays
a one-time passcode. The same technician then opens the Cloud Imaging Portal
in a browser, on their phone, a second screen, or a nearby workstation, enters
the passcode to couple the session, assigns an OS image, and confirms. From
that point the device formats, downloads, and applies the image on its own,
reporting progress in real time.

The solution is deployed into your own Azure subscription with a Bicep
infrastructure-as-code package, published through an Azure Template Spec.
Everything runs on Azure PaaS services inside your tenant: there is no
on-premises server to build (no PXE, MDT, or WDS role), and no dependency on
MSEndpointMgr-hosted infrastructure. The resource group, network boundary,
and identity configuration are yours to manage.

The same workflow scales to multiple devices at once: a technician can boot
several devices in sequence, then couple, assign, and monitor all of them
from the same portal session, including bulk assignment of one OS image to
several coupled sessions at a time.

---

## How it works

1. **Deploy the solution.** The Bicep package is published and deployed into
   your Azure subscription as a Template Spec (see [Deployment](#deployment)
   below).
2. **Set up Entra ID access.** Create the app registrations for the Portal
   and Media Builder, add the `CloudImaging.Administrator` and
   `CloudImaging.Technician` app roles to each, and assign at least one
   person or group the Administrator role so someone can sign in to the
   portal. See [docs/roles-and-access.md](docs/roles-and-access.md) for the
   full role model.
3. **Configure the environment.** An administrator signs in to the Cloud
   Imaging Portal and sets up the prerequisites: generates the boot media
   certificate used for mTLS, adds OS images to the catalog, and optionally
   configures branding and device pre-flight authorization.
4. **Generate a boot image.** An administrator generates a WinPE boot image
   in Media Builder, embedding the client app and the configured
   certificate/branding. Administrator-only, since the image embeds the
   active mTLS boot-media certificate.
5. **Prepare a USB drive.** A technician (or administrator) writes the
   generated boot image to a USB drive in Media Builder. One boot image can
   be written to as many USB drives as needed for a bulk rollout.
6. **Boot the target device.** The technician boots the device from the USB
   drive; the client launches automatically and shows a session passcode.
7. **Couple the session.** The technician opens the portal, enters the
   passcode, assigns an OS image, and confirms. Multiple devices can be
   booted and coupled this way, then assigned and started together.
8. **Imaging runs.** Each device formats, downloads the OS image over a
   time-limited SAS URL, and applies it, reporting progress at every step.
9. **Result.** The client displays a success or failure screen; the portal
   reflects the final state of each session in real time, with a support
   reference code if imaging failed.

---

## Requirements

| Requirement | Detail |
|---|---|
| Azure subscription | Owner (or Contributor + User Access Administrator) on the target resource group, to deploy the Bicep package |
| Entra ID admin access | **Application Administrator** or **Cloud Application Administrator**, to create the app registrations, app roles, and admin consent used for Portal and Media Builder sign-in |
| Windows workstation with the ADK + WinPE add-on | Required to run Media Builder and generate boot images |
| USB drive per boot media set (≥ 26 GB) | Holds the WinPE boot partition (≥ 2 GB) and the OS image cache partition (≥ 24 GB) |
| Target devices with outbound HTTPS access | No inbound connectivity or VPN to the device is required |

See [docs/self-hosting-guide.md](docs/self-hosting-guide.md) for the full
tenant setup walkthrough.

---

## Architecture

CloudImaging is a six-component system:

| Component | Technology | Role |
|---|---|---|
| **Cloud Imaging Client** | WPF / .NET 10 / WinPE | Runs on the target device; drives the imaging session end to end |
| **Cloud Imaging Media Builder** | WPF / .NET 10 / Windows | Technician workstation app for generating WinPE boot images and preparing USB media |
| **Device Gateway API** | Azure Functions v4 / .NET 10 | Public HTTPS entry point for device-originated calls; enforces mTLS |
| **Operator API** | Azure Functions v4 / .NET 10 | Authenticated API for Portal and Media Builder; Entra ID + app-role secured |
| **Imaging Core API** | Azure Functions v4 / .NET 10 | Private, VNet-isolated service that owns session state, SAS tokens, and image catalog |
| **Cloud Imaging Portal** | React 19 + Node.js/Express | Web application for coupling sessions, assigning OS images, and managing configuration |

### Component interaction

```
Cloud Imaging Client (WinPE)
    │  mTLS
    ▼
Device Gateway API (public)  ──── Private Link ────►  Imaging Core API (private)
                                                              ▲
Cloud Imaging Portal (browser)                               │ Private Link
    │  Entra ID                                              │
    ▼                                                        │
Portal Backend (App Service)  ──►  Operator API (public) ───┘

Cloud Imaging Media Builder (Windows workstation)
    │  Entra ID
    ▼
Operator API
```

---

## Features

### Cloud Imaging Client

- Auto-launches in WinPE and displays an operation selection screen (Imaging and
  Decommissioning cards; Decommissioning is reserved for a future release)
- Registers a device session and displays a **6-character alphanumeric passcode**
  for coupling in the portal, without requiring Entra ID sign-in on the device
- Performs device pre-flight authorization against Autopilot and Intune Corporate
  Identifier records via Microsoft Graph; unauthorized devices are immediately shown
  a Not Authorized result with enrollment guidance
- Executes three visible imaging steps with real-time progress reporting:
  **Format**, **Download**, and **Apply**
- Downloads the OS image (.wim / .esd) directly from Azure Blob Storage via a
  time-limited SAS token URL; caches images to the USB cache partition with a
  30-day auto-purge policy; skips cache when space is insufficient
- Refreshes SAS tokens automatically when expiry is within 15 minutes
- Displays a terminal result screen on success or failure; on failure, shows a
  structured support reference code (e.g. `CIC-A1B2C3D4-DWN-1750000000`)
- Retry after failure navigates back to the operation selection screen without
  requiring a hardware reboot

### Cloud Imaging Portal

- **Left-sidebar navigation** with five sections: Sessions, OS Images, Boot Images,
  Branding, and Configuration
- **Sessions section** displays a live table with four filter tabs (Active by
  default, Completed, Failed, and All), each with a real-time count badge
- **Session coupling** via a persistent Couple Device button that opens a passcode
  modal; the passcode is case-insensitive and resolved server-side
- **OS image assignment** via an inline modal listing the active catalog (searchable,
  showing name, version, and file size); supports single-session and bulk assignment
  to multiple sessions simultaneously
- **Inline step progress** expands per row to show Format, Download, and Apply step
  status, timestamps, and sub-progress; failed sessions show the support reference
  code inline
- **OS image catalog management**: upload images via staged direct-to-blob flow with
  SHA-256 hash verification before images enter the active catalog
- **Boot image catalog**: upload WinPE boot image WIM artifacts (up to 5 active
  entries); the latest published entry is pre-selected as the recommended default
- **Branding configuration**: upload a custom organization logo embedded into boot
  images; falls back to the MSEndpointMgr default logo if none is configured
- **Configuration section** (Administrators only): toggle device pre-flight
  authorization, set SAS token URL expiry (default 4 hours), and manage the active
  boot media client certificate used for mTLS

### Cloud Imaging Media Builder

- Entra ID sign-in required at launch; role-based workflow visibility
  (`CloudImaging.Administrator` sees both workflows; `CloudImaging.Technician`
  sees Prepare USB Storage Device only)
- Checks that Windows ADK and the WinPE add-on are installed before enabling any
  workflow
- **Generate Boot Image**: builds a WinPE boot image WIM embedding the Cloud Imaging
  Client, the active mTLS client certificate, and the configured branding logo;
  Client binaries can be pulled automatically from the latest GitHub Release or
  supplied from a local path for offline/air-gap deployments
- **Prepare USB Storage Device**: lists qualifying removable USB drives (USB bus
  type + removable flag); writes the WinPE boot partition (≥ 2 GB) and an OS image
  cache partition (≥ 24 GB); auto-refreshes the device list on plug/unplug events

### Security

- **Mutual TLS (mTLS)**: all Cloud Imaging Client requests to the Device Gateway API
  require a boot media client certificate embedded at boot image generation time;
  the Device Gateway API runs on Azure Functions Premium EP1 with
  `clientCertificateMode=require`; an optional Azure Application Gateway can be
  deployed as additive edge enforcement via the `deployApplicationGateway` IaC
  parameter
- **Entra ID + app roles**: the Portal and Media Builder each have their own app
  registration, but share the same two user-level roles (`CloudImaging.Administrator`,
  `CloudImaging.Technician`), assigned independently per registration; service-level
  roles (`CloudImaging.PortalAccess`, `CloudImaging.MediaBuilderAccess`) control
  Operator API service access. See [docs/roles-and-access.md](docs/roles-and-access.md)
  for the full access model
- **Device-session tokens**: opaque, high-entropy bearer credentials issued at
  session initialization; separate from the one-time passcode and scoped to the
  specific device session
- **Passcode abuse controls**: composite-key rate limiting on coupling attempts
  (session ID + source context hash) detects and blocks distributed brute-force
  attacks; passcodes are stored as hashes at rest and consumed on first use
- **Token replay prevention**: replayed or stale device-session tokens are rejected
  with a configurable clock-skew tolerance window; replay events are emitted as
  structured security audit events
- **Log redaction**: passcodes, device-session tokens, SAS token URLs, and
  certificate private material are redacted from all structured logs and telemetry
  before persistence; CI security scan gates block merge if prohibited patterns are
  detected in output

### Observability

- All Azure-hosted components (Device Gateway API, Operator API, Imaging Core API,
  Portal backend) emit structured telemetry to a **shared Azure Application Insights
  workspace** (requests, dependencies, exceptions, custom traces)
- WPF applications write structured diagnostic logs to a local rolling log file only;
  no external APM connectivity required from WinPE or technician workstations

</details>

---

## Deployment

CloudImaging is deployed as an Azure Template Spec: the Bicep package is
published once per environment, then deployed through the standard Azure
portal Template Spec wizard.

1. **Check prerequisites** (see [Requirements](#requirements) above). For a
   full walkthrough of the Entra ID app registrations and tenant setup,
   follow [docs/self-hosting-guide.md](docs/self-hosting-guide.md).

2. **Publish the Template Spec** (one-time per environment):
   ```powershell
   .\src\deploy\scripts\publish-template-spec.ps1 `
       -ResourceGroupName 'rg-contoso-dev-cloudimaging' `
       -Location 'westeurope' `
       -Version '1.0.0'
   ```

3. **Deploy from the Azure portal**: open the Template Spec resource and
   select **Deploy** to launch the full tabbed wizard; supply your Entra ID
   app registration IDs and environment parameters.

4. **Upgrade later without redeploying infrastructure**: the included
   `update.ps1` script pushes new component packages to existing Azure
   resources via zip deploy, no re-provisioning required.

### Deployed resources

The Bicep IaC package provisions the following Azure resources:

- Azure Functions Premium EP1: Device Gateway API, Operator API, Imaging Core API
- Azure App Service (Linux, Node.js 22): Portal backend
- Azure Static Web Apps: Portal frontend
- Azure Storage Account: OS images, boot images, branding assets, and Table
  Storage for session and catalog state
- Azure Virtual Network with Private Endpoint and Private DNS: isolates the
  Imaging Core API from public access
- Azure Application Insights: shared telemetry workspace
- Azure Key Vault: boot media client certificate storage
- Managed Identities and RBAC role assignments

---

## Documentation

| Guide | Use it to |
|---|---|
| [Self-hosting guide](docs/self-hosting-guide.md) | Set up Entra ID app registrations and deploy into your tenant, step by step |
| [Operations runbook](docs/operations-runbook.md) | Day-2 operations: monitoring, certificate rotation, upgrades, troubleshooting |
| [Roles and access](docs/roles-and-access.md) | The full Entra ID app role and service role model |

---

## Release Model

Releases are split into three independently-versioned streams, each with its own tag
prefix and GitHub Release history:

| Stream | Tag | Contents |
|--------|-----|----------|
| Backend/IaC | `v#.#.#` | Three Function App packages, Portal frontend + backend, Bicep/deploy scripts, `update.ps1` |
| Cloud Imaging Client | `client-v#.#.#` | The WinPE client binary embedded into boot images |
| Media Builder | `mediabuilder-v#.#.#` | The technician-workstation Windows app |

Each stream is cut on its own cadence: a Client hotfix doesn't require a new backend
release, and vice versa. Because GitHub's Releases page is a flat list (not grouped by
stream), the backend/IaC and Client streams also maintain a moving "latest" alias release
(`iac-latest`, `client-latest`) that always points at the newest *stable* release in that
stream. `update.ps1 -Version latest` and Media Builder's "Automatic download" source
option resolve these aliases directly instead of GitHub's repo-wide latest release, which
would otherwise resolve to whichever stream published most recently. Media Builder has no
alias, since nothing auto-downloads it.

---

## License

[MIT](LICENSE)

