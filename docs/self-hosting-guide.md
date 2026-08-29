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
| Windows ADK + WinPE add-on | On every technician workstation that runs Media Builder — see [Step 7](#installing-the-windows-adk-on-technician-workstations) |

---

## Step 1 — Create Three App Registrations

Cloud Imaging uses three separate Entra ID App Registrations, each with a single, clear purpose:

| Registration | Client type | Purpose |
|---|---|---|
| **Cloud Imaging Portal** | Single-page application (SPA) | Browser portal sign-in (technicians & administrators) |
| **Cloud Imaging Media Builder** | Mobile & desktop (public client) | Media Builder desktop app sign-in |
| **Cloud Imaging Operator API** | Web API (service-to-service) | Token audience for the Operator API |

> **Why three?** The browser portal is a Single-Page Application and the Media Builder is a native
> public client. Entra classifies these client types differently, and mixing an SPA platform with a
> *Mobile and desktop* platform in the **same** registration causes the SPA's cross-origin token
> redemption to be rejected with **AADSTS9002326**. Keeping them in separate registrations avoids
> this entirely.

### Registration 1: Cloud Imaging Portal (browser SPA)

1. In Entra ID → App Registrations → **New registration**
2. Name: `Cloud Imaging Portal` (or your branding)
3. Supported account types: **Single tenant**
4. Under **Authentication** → **Add a platform** → **Single-page application**, add your deployed Static Web App URL (e.g. `https://<swa-name>.azurestaticapps.net`). You can add a placeholder now and update it with the real hostname from the deployment outputs (Step 3). Do **not** add a *Mobile and desktop* platform to this registration — that reclassifies the app and breaks SPA sign-in with **AADSTS9002326**.
5. Under **Authentication** → **Advanced settings**, leave **Allow public client flows** = **No**. Setting it to **Yes** breaks the browser portal: the SPA's cross-origin token redemption is then rejected with **AADSTS9002326** (*cross-origin token redemption is permitted only for the 'Single-Page Application' client-type*).
6. Under **Expose an API**:
   - Set the **Application ID URI** to `api://<portalClientId>` (accept the default).
   - **Add a scope**: name `user_impersonation`, **Who can consent** = **Admins and users**, state **Enabled**. Fill in the consent display names/descriptions. The browser portal (an MSAL SPA) requests `api://<portalClientId>/user_impersonation` to obtain an access token for the portal backend — without it, sign-in fails with **AADSTS500011 (invalid_resource)** and the portal renders a blank page after sign-in.
7. Under **App roles**, add:
   - `CloudImaging.Administrator` (value: `CloudImaging.Administrator`, allowed for: Users/Groups)
   - `CloudImaging.Technician` (value: `CloudImaging.Technician`, allowed for: Users/Groups)
8. *(Optional)* Under **API permissions**, add Microsoft Graph → **Delegated** → `User.Read` and grant admin consent — lets the portal display the signed-in user's name.
9. Record the **Application (client) ID** → this is `portalClientId`

> The portal backend calls the Operator API using its **managed identity** (the `CloudImaging.PortalAccess` app role, assigned automatically in Step 4). The Portal registration therefore needs **no** API permission to the Operator API.

### Registration 2: Cloud Imaging Media Builder (desktop public client)

1. New registration — Name: `Cloud Imaging Media Builder`
2. Supported account types: **Single tenant**
3. Under **Authentication** → **Add a platform** → **Mobile and desktop applications**, add the redirect URI `http://localhost`. The Media Builder signs in with the interactive loopback (authorization code + PKCE) flow.
4. Under **Authentication** → **Advanced settings**, leave **Allow public client flows** = **No** — the loopback flow is already identified as a public client by its `http://localhost` redirect and does not need this flag.
5. Under **App roles**, add the same two user roles:
   - `CloudImaging.Administrator` (value: `CloudImaging.Administrator`, allowed for: Users/Groups)
   - `CloudImaging.Technician` (value: `CloudImaging.Technician`, allowed for: Users/Groups)
6. Record the **Application (client) ID** → this is `mediaBuilderClientId`

### Registration 3: Cloud Imaging Operator API (service-to-service)

1. New registration — Name: `Cloud Imaging Operator API`
2. Under **Expose an API**:
   - Set the **Application ID URI** to `api://<operatorApiClientId>` (accept the default).
   - **Add a scope**: name `user_impersonation`, **Who can consent** = **Admins and users**, state **Enabled**. Fill in the consent display names/descriptions. This delegated scope lets the Media Builder (an interactive user-facing public client) obtain an access token for the Operator API — without it, sign-in fails with **AADSTS650057 (Invalid resource)**.
3. Under **App roles**, add:
   - `CloudImaging.PortalAccess` (allowed for: **Applications**) — used by the portal backend managed identity.
   - `CloudImaging.MediaBuilderAccess` (allowed for: **Both (Users/Groups + Applications)**) — assigned to the technicians who run the Media Builder. It **must** allow *Users/Groups*, otherwise the technician's interactive token never carries the role and API calls return `403`.
4. Record the **Application (client) ID** → this is `operatorApiClientId`

### Media Builder → grant access to the Operator API

In **Cloud Imaging Media Builder** → **API permissions** → **Add a permission** → **My APIs**
→ select **Cloud Imaging Operator API** → **Delegated permissions** → check
`user_impersonation` → **Add permissions**. Then click **Grant admin consent for &lt;your tenant&gt;**.

This pre-configured, consented permission is mandatory: the Media Builder requests the
`api://<operatorApiClientId>/.default` scope, which only succeeds when the Media Builder registration
already holds a consented permission to the Operator API. Skipping it produces **AADSTS650057**.

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
- **Cloud Imaging Portal - Application (client) ID**: from Registration 1 (`portalClientId`)
- **Cloud Imaging Media Builder - Application (client) ID**: from Registration 2 (`mediaBuilderClientId`)
- **Operator API Client ID**: from Registration 3 (`operatorApiClientId`)
- **Tenant ID**: your Entra directory ID (auto-populated)
- **Azure region**: choose closest to your users
- **Deployment tier**: Standard (WAF_v2 adds Application Gateway — Enterprise only)

Click **Create** and wait ~15 minutes.

> **No SPA rebuild required.** The browser portal is a single prebuilt bundle that is
> **tenant-agnostic**. On startup it fetches its Entra ID settings (client ID, tenant,
> authority) at runtime from the portal backend's public `/api/config` endpoint, which
> the deployment populates from the **Portal Application (client) ID** and **Tenant ID**
> you entered above. The Template Spec also provisions a Static Web App **linked backend**,
> so the portal serves the UI and proxies `/api/*` to the backend from a **single origin** —
> the same origin you registered as the SPA redirect URI (Registration 1, step 4). You never
> edit or rebuild the frontend for your tenant.

---

## Step 4 — Post-Deployment Setup

```powershell
$rg = "corp-prod-rg"   # your resource group

# 1. Grant Microsoft Graph permission to the ImagingCore managed identity
.\scripts\grant-graph-permissions.ps1 -ResourceGroupName $rg

# 2. Assign Entra app roles to the portal managed identity and Media Builder SP
.\scripts\assign-service-roles.ps1 `
  -ResourceGroupName $rg `
  -OperatorApiClientId "<operatorApiClientId>" `
  -MediaBuilderClientId "<mediaBuilderClientId>"
```

This script only handles the **service-level** roles (`CloudImaging.PortalAccess` on the Portal's
managed identity). The **user-level** roles below still need to be assigned manually, per person.

---

## Step 5 — Assign Access to Your Administrators and Technicians

The app roles created in Step 1 are just definitions — nobody can sign in successfully until they're
assigned to actual users or groups. For the full access model (what each role grants in the Portal
vs. the Media Builder), see [roles-and-access.md](roles-and-access.md). To assign access:

1. **Portal users**: Entra ID → **Enterprise applications** → **Cloud Imaging Portal** →
   **Users and groups** → **Add user/group** → assign `CloudImaging.Administrator` or
   `CloudImaging.Technician` to each person (or group) who signs in to the browser portal.
2. **Media Builder users**: repeat on the **Cloud Imaging Media Builder** enterprise application —
   assignments are **not** shared between the two registrations, so a technician who uses both apps
   needs a role on *each* one.
3. **Media Builder API access**: Media Builder users also need `CloudImaging.MediaBuilderAccess` on
   the **Cloud Imaging Operator API** enterprise application — see
   [Step 8](#step-8--generate-your-first-boot-image) and
   [roles-and-access.md](roles-and-access.md#1-the-four-app-roles) for why this is a separate,
   additional assignment.

A user with no role assigned on a registration can still sign in, but sees an "Access denied"
screen (Portal) or has every workflow blocked (Media Builder).

---

## Step 6 — Generate a Boot Media Certificate

1. Sign in to the Cloud Imaging Portal as **CloudImaging.Administrator**
2. Navigate to **Configuration**
3. Under **Boot Media Certificate**, click **Generate Certificate**
4. Wait for confirmation

---

## Step 7 — Configure the Media Builder

The Media Builder is a desktop app that technicians run on their own workstations.
Because Cloud Imaging is deployed into **your** tenant, each packaged build must be
told which tenant, sign-in app, and Operator API to use. This is done with an
`appsettings.json` file placed **next to `CloudImaging.MediaBuilder.exe`**.

> **These values are public identifiers, not secrets.** The Media Builder is an MSAL
> *public client* — it has no client secret. It is therefore safe (and expected) to
> distribute a preconfigured `appsettings.json` alongside your packaged build. Do **not**
> put certificates, client secrets, or connection strings in this file.

Create or edit `appsettings.json`:

```json
{
  "EntraId": {
    "ClientId": "<mediaBuilderClientId>",
    "TenantId": "<your-tenant-id>",
    "OperatorApiScope": "api://<operatorApiClientId>/.default"
  },
  "OperatorApi": {
    "BaseUrl": "https://<operator-api-hostname>"
  }
}
```

| Key | Value / where it comes from |
|---|---|
| `EntraId:ClientId` | **Cloud Imaging Media Builder** Application (client) ID — `mediaBuilderClientId` from Step 1 |
| `EntraId:TenantId` | Your Entra **Directory (tenant) ID** (single-tenant sign-in) |
| `EntraId:OperatorApiScope` | `api://` + **Operator API** client ID (`operatorApiClientId`) + `/.default` |
| `OperatorApi:BaseUrl` | The Operator API Function App URL from the deployment outputs |

**Why single-tenant + `appsettings.json`:** the Media Builder registration is a **single-tenant** app
(Step 1), and the access token the Media Builder requests is audience-scoped to *your*
Operator API (`api://<operatorApiClientId>`) and calls *your* Operator API URL. Those are
per-deployment values, so a universal "sign in to any tenant" build is not possible — the
configuration must ship with the build.

**Role assignment (required):** the Operator API authorizes callers by the
`CloudImaging.MediaBuilderAccess` app role. In **Entra ID → Enterprise applications →
Cloud Imaging Operator API → Users and groups → Add user/group**, assign each technician (or
a group) to the **CloudImaging.MediaBuilderAccess** role. Because the role allows *Users/Groups*
(Step 1, Registration 3), Entra includes it in the `.default` token's `roles` claim. Without
this assignment, sign-in succeeds but API calls return `403`.

> **Two things are needed for a working Media Builder sign-in — don't skip either:**
> 1. The **`user_impersonation` delegated permission** on the Media Builder registration (granted + admin-consented) —
>    prevents `AADSTS650057` at sign-in.
> 2. The **`CloudImaging.MediaBuilderAccess` role assignment** to the user/group on the Operator
>    API enterprise app — prevents `403` on API calls.

> **Packaging checklist (before public/internal distribution):** confirm `appsettings.json`
> sits beside the executable, all four keys are filled in, and the file contains no secrets.
> If the file is missing or incomplete, the app still launches but the sign-in screen shows
> *"Entra ID sign-in is not configured"*.

### Installing the Windows ADK on technician workstations

Every workstation that will run **Generate Boot Image** needs the **Windows Assessment and
Deployment Kit (ADK)**. It is *not* bundled with Media Builder and must be installed
separately on each technician device (or baked into the device image).

The ADK ships as **two separate installers that must be the SAME version**:

| # | Installer | Run it, then select **only** |
|---|---|---|
| 1 | `adksetup.exe` (base ADK) | ☑ **Deployment Tools** — leave everything else (USMT, Windows Performance Toolkit, Application Compatibility Toolkit, VAMT, etc.) unchecked; Media Builder doesn't need them |
| 2 | `adkwinpesetup.exe` (WinPE add-on, downloaded and run *separately* after step 1) | ☑ **Windows Preinstallation Environment (WinPE)** — it's the only option |

Get both installers, matched to the same ADK release, from the official Microsoft page:
**<https://learn.microsoft.com/en-us/windows-hardware/get-started/adk-install>** (it links the
correct base ADK and WinPE add-on downloads for the current release together — don't mix an
older cached installer for one with a newer download for the other).

> **Why "Deployment Tools" specifically:** it's the feature that installs `DISM.exe`,
> `Oscdimg.exe`, and `copype.cmd`'s supporting environment (`DandISetEnv.bat`) under
> `Deployment Tools\<arch>\...` — everything Media Builder's boot image generation shells out
> to. The other ADK features (USMT, ACT, Windows Performance Toolkit, etc.) are unrelated to
> Cloud Imaging and only add install time/disk space.

> ⚠️ **The base ADK and the WinPE add-on version MUST match exactly.** They're installed and
> updated independently, so it's easy to end up with (for example) an older Deployment Tools
> paired with a newer WinPE add-on. When that happens, boot image generation fails deep inside
> Microsoft's `copype.cmd` with an error like *"Unable to copy boot sector file:
> ...\Deployment Tools\amd64\Oscdimg\efisys_EX.bin"* — the newer WinPE add-on's boot files
> reference boot-sector files that the older Deployment Tools release doesn't ship yet. Media
> Builder detects this specific mismatch and reports it clearly rather than surfacing the raw
> `copype.cmd` error, but the fix is always the same: **re-run `adksetup.exe` and update
> Deployment Tools to the same release as the WinPE add-on.**
>
> To check for a mismatch yourself, compare these two Add/Remove Programs entries — they must
> report the **same** version:
> ```powershell
> Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' |
>   Where-Object { $_.DisplayName -match 'Windows (Assessment and )?Deployment (Kit|Tools)$|WinPE Add-ons' } |
>   Select-Object DisplayName, DisplayVersion
> ```

Media Builder verifies the ADK + WinPE add-on are present (and blocks **Generate Boot Image**
with a link to the page above if not) before you can start a build.

### Packaging as a Win32 app (e.g. Intune)

The Media Builder is a self-contained `win-x64` publish (no separate .NET runtime needed on
the target machine), so it packages cleanly as a Win32 app through whatever deployment
tooling your organization already uses (Intune, ConfigMgr, etc.). Follow your existing Win32
packaging process for the generic parts (wrapping, install/uninstall commands, assignment) —
the parts specific to Cloud Imaging are:

- **Source content**: the extracted `CloudImaging.MediaBuilder.zip` from the
  [GitHub Releases](https://github.com/MSEndpointMgr/CloudImaging/releases) page (or your own
  `dotnet publish` output), with the tenant-specific `appsettings.json` from this step
  substituted in **before** wrapping. This is the one file that makes the package specific to
  your deployment — everything else in the folder is generic and identical for every tenant.
- **Detection rule**: base it on `CloudImaging.MediaBuilder.exe` existing at the install
  destination (optionally pinned to a file version), so re-deploying a new release is picked
  up as an update rather than silently skipped.
- **Dependency**: the **Windows ADK + WinPE add-on** (matching versions — see
  [Installing the Windows ADK on technician workstations](#installing-the-windows-adk-on-technician-workstations)
  above) must already be present on the technician's device — Media Builder detects the ADK
  install path at runtime and fails boot image generation with a clear error if it's missing.
  It is *not* bundled in the package; express it as a dependency in your packaging tool (or
  ensure it's baked into the technician device image) rather than trying to include it in the
  Media Builder app itself.
- **Install behavior**: the config is machine-wide, not per-user — install it once per device
  rather than per signed-in user.

> **Updating the config later** (e.g. rotating the client ID, or the Operator API URL
> changing after a redeploy) requires repackaging with the updated `appsettings.json` and
> publishing it as an app update — sign-in itself stays interactive per technician (MSAL
> loopback flow) and isn't affected by the packaging.

---

## Step 8 — Generate Your First Boot Image

1. Open the **Cloud Imaging Media Builder** on a technician workstation with the Windows ADK
   + WinPE add-on installed (see [Step 7](#installing-the-windows-adk-on-technician-workstations))
2. Sign in with your Entra ID credentials (must have `CloudImaging.Administrator` or `CloudImaging.Technician` role)
3. Select **Generate Boot Image**
4. Choose **Auto-download** (fetches latest Cloud Imaging Client from GitHub) or specify a local path
5. Optionally check **Enable command prompt access** under **Support Tools** (see
   [Client support tools](#client-support-tools) below) — off by default
6. Click **Generate** — the wizard produces a `.wim` file
7. Upload the WIM to the portal: **Boot Images** → **Upload**

> **Device Gateway URL is resolved automatically.** The Media Builder looks up the live Device
> Gateway API URL from the Operator API and stamps it into the Client's `appsettings.json` while
> building the WIM — there's nothing to configure manually, and every boot image build picks up
> the current URL even if the Device Gateway was redeployed or renamed since the Client binaries
> were built.

### Client support tools

The Operation Selection screen offers two troubleshooting tools, both accessible without leaving
the always-on-top Cloud Imaging Client window:

- **Connect to Wi-Fi** — always available (no opt-in required). Opens a dedicated window that
  scans for visible networks via `netsh wlan` and lets the technician connect to an
  Open or WPA2/WPA3-Personal network by SSID + passphrase. Enterprise/802.1X networks are shown
  (greyed out, labeled "Not supported") but cannot be connected to via this flow. No Wi-Fi
  credential is ever persisted — the temporary WLAN profile (which embeds the passphrase in
  plain text, per the netsh profile schema) is deleted immediately after the connect attempt.
- **Command Prompt** — hidden unless the boot image was built with **Enable command prompt
  access** checked (off by default, per boot image). Launches an interactive `cmd.exe` for
  advanced troubleshooting, temporarily dropping the Client window's always-on-top behavior so
  the console isn't hidden behind it; the Client returns to always-on-top automatically once the
  console is closed.

> **Security note:** only enable command prompt access for boot images used in trusted,
> supervised environments (e.g. IT staging) — an interactive shell in WinPE has full access to
> local disks and the network. Leave it unchecked for boot images that may be used unattended or
> by end users.

---



## Step 9 — Prepare USB Media

1. In **Cloud Imaging Media Builder**, select **Prepare USB Storage Device**
2. Select the boot image to deploy
3. Insert a USB drive (32 GB+, USB 3.x)
4. Click **Prepare** — the drive will be partitioned and the WIM deployed

---

## Step 10 — Image a Device

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

> **Storage permission (handled automatically):** the Function App components are deployed with
> **Run-From-Package** — each release ZIP is uploaded to the `app-packages` container using Entra ID
> data-plane auth, which needs the **Storage Blob Data Contributor** role (resource-group
> Owner/Contributor alone is *not* enough). `update.ps1` grants this role to your signed-in identity
> on the package storage accounts automatically on first run. This requires the **User Access
> Administrator** (or Owner) role listed in *What You'll Need*. If your account cannot assign roles,
> the script prints a warning — ask an administrator to grant **Storage Blob Data Contributor** on
> the `*stapp` and `*stcore` storage accounts, then re-run the upgrade.

---

## Getting Help

- **Roles & access reference**: `docs/roles-and-access.md`
- **Troubleshooting**: `docs/operations-runbook.md`
- **Architecture details**: `specs/001-cloud-windows-imaging/plan.md`
- **Issues**: [GitHub Issues](https://github.com/MSEndpointMgr/CloudImaging/issues)
