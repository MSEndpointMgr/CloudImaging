# CloudImaging

Cloud-orchestrated Windows OS imaging for bare-metal devices running WinPE.  
A self-hostable, Azure-native solution for enterprise device provisioning.

---

## Overview

CloudImaging provisions Windows OS images from Azure Blob Storage to bare-metal
devices over the network. A technician boots a target device into WinPE, where
the **Cloud Imaging Client** application launches automatically and guides the
imaging session from start to finish. An operator in a web portal couples the
session, assigns an OS image, and monitors progress in real time — all from a
browser, with no physical access to the device required.

The solution is packaged as a reproducible, self-hostable deployment bundle.
Organizations deploy it into their own Azure tenant using an Azure Template Spec
wizard and a Bicep-based infrastructure-as-code package. No external hosting or
dependency on MSEndpointMgr infrastructure is required.

---

## Architecture

CloudImaging is a six-component polyglot system:

| Component | Technology | Role |
|---|---|---|
| **Cloud Imaging Client** | WPF / .NET 10 / WinPE | Runs on the target device; drives the imaging session end-to-end |
| **Cloud Imaging Media Builder** | WPF / .NET 10 / Windows | Technician workstation app for generating WinPE boot images and preparing USB media |
| **Device Gateway API** | Azure Functions v4 / .NET 10 | Public HTTPS entry point for device-originated calls; enforces mTLS |
| **Operator API** | Azure Functions v4 / .NET 10 | Authenticated API for Portal and Media Builder; Entra ID + app-role secured |
| **Imaging Core API** | Azure Functions v4 / .NET 10 | Private, VNet-isolated service that owns session state, SAS tokens, and image catalog |
| **Cloud Imaging Portal** | React 19 + Node.js/Express | Web application for operators to manage sessions, OS images, and configuration |

### Component Interaction

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
  for operator coupling without requiring Entra ID sign-in on the device
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
- **Sessions section** displays a live table with four filter tabs — Active (default),
  Completed, Failed, and All — each with a real-time count badge
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
  cache partition (≥ 20 GB); auto-refreshes the device list on plug/unplug events

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

---

## How It Works

1. **Prepare boot media**: a Media Builder operator generates a WinPE boot image
   embedding the Cloud Imaging Client and prepares a USB drive
2. **Boot the target device**: the technician inserts the USB drive; the Cloud
   Imaging Client launches automatically and displays a session passcode
3. **Couple the session**: a portal operator enters the passcode in the Cloud
   Imaging Portal, assigns an OS image, and confirms
4. **Imaging runs**: the Cloud Imaging Client downloads the OS image via a SAS URL
   and applies it, reporting progress at each step
5. **Result**: the client displays a success or failure screen; the portal reflects
   the final session state in real time

---

## Deployment

CloudImaging uses an **Azure Template Spec** deployment model.

1. **Prerequisites**
   - An Azure subscription where the deploying user holds Owner (or Contributor +
     User Access Administrator) at the resource group scope
   - An Entra ID shared app registration with the two user-facing app roles
     (`CloudImaging.Administrator`, `CloudImaging.Technician`) configured — see the
     deployment guide in the release bundle for step-by-step instructions
   - Windows ADK and WinPE add-on installed on the Media Builder workstation

2. **Publish the Template Spec** (one-time per environment)
   ```powershell
   .\src\deploy\scripts\publish-template-spec.ps1 `
       -ResourceGroupName 'rg-contoso-dev-cloudimaging' `
       -Location 'westeurope' `
       -Version '1.0.0'
   ```

3. **Deploy from the Azure portal**: navigate to the Template Spec resource and
   select **Deploy** to launch the full tabbed wizard; supply your Entra ID app
   registration IDs and environment parameters

4. **Upgrade** (code-only): use the included `update.ps1` script to push new
   component packages to existing Azure resources via zip deploy — no
   infrastructure re-provisioning required

### Deployed Resources

The Bicep IaC package provisions the following Azure resources:

- Azure Functions Premium EP1 — Device Gateway API, Operator API, Imaging Core API
- Azure App Service (Linux, Node.js 22) — Portal backend
- Azure Static Web Apps — Portal frontend
- Azure Storage Account — OS images, boot images, branding assets, Table Storage
  for session and catalog state
- Azure Virtual Network with Private Endpoint and Private DNS — isolates Imaging
  Core API from public access
- Azure Application Insights — shared telemetry workspace
- Azure Key Vault — boot media client certificate storage
- Managed Identities and RBAC role assignments

---

## Release Model

Every versioned GitHub Release contains a complete bundle with artifacts from all
six components: three Function App packages, Portal frontend and backend, Cloud
Imaging Client WinPE binary, Media Builder Windows installer, IaC templates,
parameter templates, and documentation. All components are included in every
release regardless of which changed since the prior release. Versioning is
solution-level.

---

## License

[MIT](LICENSE)
