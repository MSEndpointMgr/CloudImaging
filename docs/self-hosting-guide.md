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

---

## Step 5 — Generate a Boot Media Certificate

1. Sign in to the Cloud Imaging Portal as **CloudImaging.Administrator**
2. Navigate to **Configuration**
3. Under **Boot Media Certificate**, click **Generate Certificate**
4. Wait for confirmation

---

## Step 6 — Configure the Media Builder

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

---

## Step 7 — Generate Your First Boot Image

1. Open the **Cloud Imaging Media Builder** on a technician workstation with Windows ADK installed
2. Sign in with your Entra ID credentials (must have `CloudImaging.Administrator` or `CloudImaging.Technician` role)
3. Select **Generate Boot Image**
4. Choose **Auto-download** (fetches latest Cloud Imaging Client from GitHub) or specify a local path
5. Click **Generate** — the wizard produces a `.wim` file
6. Upload the WIM to the portal: **Boot Images** → **Upload**

---

## Step 8 — Prepare USB Media

1. In **Cloud Imaging Media Builder**, select **Prepare USB Storage Device**
2. Select the boot image to deploy
3. Insert a USB drive (32 GB+, USB 3.x)
4. Click **Prepare** — the drive will be partitioned and the WIM deployed

---

## Step 9 — Image a Device

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
