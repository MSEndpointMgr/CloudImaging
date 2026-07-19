# Cloud Imaging Self-Hosting Quick-Start Guide

**Spec reference**: FR-041, FR-044a, T108  
**Audience**: IT administrators deploying Cloud Imaging into their own Azure tenant

---

## What You'll Need

| Requirement | Details |
|---|---|
| Azure subscription | Contributor + User Access Administrator on target resource group |
| Azure CLI | [Install guide](https://learn.microsoft.com/azure/cli/install) |
| Az PowerShell | `Install-Module Az` |
| Azure SWA CLI | `npm install -g @azure/static-web-apps-cli` |
| Entra ID permissions | Create/update App Registrations |

---

## Step 1 — Create Two App Registrations

Cloud Imaging requires two separate Entra ID App Registrations:

### Registration A: User Authentication (portal + Media Builder)

1. In Entra ID → App Registrations → **New registration**
2. Name: `Cloud Imaging` (or your branding)
3. Supported account types: **Single tenant**
4. Redirect URI: `http://localhost` (add more later)
5. Under **App roles**, add:
   - `CloudImaging.Administrator` (value: `CloudImaging.Administrator`, allowed for: Users/Groups)
   - `CloudImaging.Technician` (value: `CloudImaging.Technician`, allowed for: Users/Groups)
6. Record the **Application (client) ID** → this is `userAuthClientId`

### Registration B: Operator API (service-to-service)

1. New registration — Name: `Cloud Imaging Operator API`
2. Under **App roles**, add:
   - `CloudImaging.PortalAccess` (allowed for: Applications)
   - `CloudImaging.MediaBuilderAccess` (allowed for: Applications)
3. Record the **Application (client) ID** → this is `operatorApiClientId`

---

## Step 2 — Publish the Template Spec

Download the release package from the [GitHub Releases](https://github.com/MSEndpointMgr/CloudImaging/releases) page.

```powershell
# Authenticate
Connect-AzAccount
az login

# Create a resource group for the Template Spec itself
New-AzResourceGroup -Name rg-cloudimaging-specs -Location eastus

# Publish
cd deploy/

.\scripts\publish-template-spec.ps1 `
  -ResourceGroupName rg-cloudimaging-specs `
  -Location eastus `
  -Version 1.0.0
```

The script prints a portal URL — open it in a browser.

---

## Step 3 — Deploy via Template Spec Wizard

Fill in the wizard:

- **Resource prefix**: 2–4 alphanumeric characters (e.g. `corp`)
- **Environment**: `prod` (or `dev` for testing)
- **User Auth Client ID**: from Registration A
- **Operator API Client ID**: from Registration B
- **Tenant ID**: your Entra directory ID (auto-populated)
- **Azure region**: choose closest to your users
- **Deployment tier**: Standard (WAF_v2 adds Application Gateway — Enterprise only)

Click **Create** and wait ~15 minutes.

---

## Step 4 — Post-Deployment Setup

```powershell
$rg = "corp-prod-rg"   # your resource group

# 1. Grant Microsoft Graph permission to the ImagingCore managed identity
.\scripts\grant-graph-permissions.ps1 -ResourceGroupName $rg

# 2. Assign Entra app roles to the portal managed identity
.\scripts\assign-service-roles.ps1 -ResourceGroupName $rg
```

---

## Step 5 — Generate a Boot Media Certificate

1. Sign in to the Cloud Imaging Portal as **CloudImaging.Administrator**
2. Navigate to **Configuration**
3. Under **Boot Media Certificate**, click **Generate Certificate**
4. Wait for confirmation

---

## Step 6 — Generate Your First Boot Image

1. Open the **Cloud Imaging Media Builder** on a technician workstation with Windows ADK installed
2. Sign in with your Entra ID credentials (must have `CloudImaging.Administrator` or `CloudImaging.Technician` role)
3. Select **Generate Boot Image**
4. Choose **Auto-download** (fetches latest Cloud Imaging Client from GitHub) or specify a local path
5. Click **Generate** — the wizard produces a `.wim` file
6. Upload the WIM to the portal: **Boot Images** → **Upload**

---

## Step 7 — Prepare USB Media

1. In **Cloud Imaging Media Builder**, select **Prepare USB Storage Device**
2. Select the boot image to deploy
3. Insert a USB drive (32 GB+, USB 3.x)
4. Click **Prepare** — the drive will be partitioned and the WIM deployed

---

## Step 8 — Image a Device

1. Boot the target device from the USB drive
2. The device auto-launches Cloud Imaging Client and displays a **passcode**
3. Sign in to the portal as a Technician
4. In the **Sessions** section, click **Couple Device** and enter the passcode
5. Click **Assign Image**, select an OS image, confirm
6. The device downloads and applies the image automatically

---

## Upgrading

```powershell
# Download the new release archive
.\update.ps1 -ResourceGroupName corp-prod-rg

# Or from a local archive
.\update.ps1 -ResourceGroupName corp-prod-rg -ArchivePath C:\Downloads\cloud-imaging-v1.1.0.zip
```

---

## Getting Help

- **Troubleshooting**: `docs/operations-runbook.md`
- **Architecture details**: `specs/001-cloud-windows-imaging/plan.md`
- **Issues**: [GitHub Issues](https://github.com/MSEndpointMgr/CloudImaging/issues)
