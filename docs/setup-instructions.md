# Cloud Imaging Setup Instructions

**Audience**: IT administrators deploying Cloud Imaging into their own Azure tenant

This is the guide to follow for a full deployment, start to finish. It's organized into five
phases, run in order. Step numbers restart within each phase, so a cross-reference to another
phase names both ("Phase 3, Step 2").

| Phase | Covers | Steps |
|---|---|---|
| [1. Prerequisites](#phase-1-prerequisites) | Install tooling, create the three Entra ID app registrations | 1 step |
| [2. Deploy the Azure resources](#phase-2-deploy-the-azure-resources) | Publish the Template Spec, run the deployment wizard | 2 steps |
| [3. Post-deployment setup](#phase-3-post-deployment-setup) | Run the post-deploy script, assign user roles, complete the initial portal configuration (boot media certificate, OS images, optional settings) | 3 steps |
| [4. Configure the Media Builder](#phase-4-configure-the-media-builder) | Point the desktop app at your tenant, install the Windows ADK, deploy the MSI with Intune | 1 step |
| [5. Operate Cloud Imaging](#phase-5-operate-cloud-imaging) | Generate a boot image, prepare USB media, image a device | 3 steps |

---

## Phase 1: Prerequisites

### What You'll Need

| Requirement | Details |
|---|---|
| Azure subscription | Contributor + User Access Administrator on target resource group |
| Azure CLI | [Install guide](https://learn.microsoft.com/azure/cli/install) |
| Az PowerShell | `Install-Module Az` |
| Microsoft Graph PowerShell | `Install-Module Microsoft.Graph.Authentication, Microsoft.Graph.Applications -Scope CurrentUser` |
| Azure SWA CLI | `npm install -g @azure/static-web-apps-cli`; used by `update.ps1` to publish the portal frontend when upgrading |
| Entra ID permissions | Create/update App Registrations; Privileged Role Administrator or Global Administrator for the post-deployment Microsoft Graph permission grant |
| Microsoft Intune | An active Intune license is required when device pre-flight authorization is enabled |
| Windows ADK + WinPE add-on | On every technician workstation that runs Media Builder: see [Installing the Windows ADK](#installing-the-windows-adk-on-technician-workstations) |

> **Why both Contributor *and* User Access Administrator?** The Bicep package creates Azure role
> assignments for the deployed managed identities (Storage, Key Vault, package containers) as part
> of the deployment (`Microsoft.Authorization/roleAssignments` resources). Built-in
> **Contributor** explicitly excludes `Microsoft.Authorization/*/Write`, so it can't create
> those role assignments on its own; without the extra role (or **Owner**, which already
> includes it), the deployment fails partway through with an authorization error. The same
> permission is needed again later: `update.ps1` grants itself **Storage Blob Data
> Contributor** on the package storage accounts on first run.

---

### Step 1: Create Three App Registrations

Cloud Imaging uses three separate Entra ID App Registrations, each with a single, clear purpose:

| Registration | Client type | Purpose |
|---|---|---|
| **Cloud Imaging Portal** | Single-page application (SPA) | Browser portal sign-in (technicians & administrators) |
| **Cloud Imaging Operator API** | Web API (service-to-service) | Token audience for the Operator API |
| **Cloud Imaging Media Builder** | Mobile & desktop (public client) | Media Builder desktop app sign-in |

> **Why three?** The browser portal is a Single-Page Application and the Media Builder is a native
> public client. Entra classifies these client types differently, and mixing an SPA platform with a
> *Mobile and desktop* platform in the **same** registration causes the SPA's cross-origin token
> redemption to be rejected with **AADSTS9002326**. Keeping them in separate registrations avoids
> this entirely.

#### Registration 1: Cloud Imaging Portal (browser SPA)

1. In Entra ID → App Registrations → **New registration**
2. Name: `Cloud Imaging Portal` (or your branding)
3. Supported account types: **Single tenant**
4. Once created, record the **Application (client) ID** shown on the **Overview** page → this
   is `portalClientId`. You'll need it in a couple of the steps below.
5. Under **Authentication** → **Add a platform** → **Single-page application**: you don't have
   a Static Web App hostname yet, it's only created by the deployment in
   [Phase 2](#phase-2-deploy-the-azure-resources). Add a placeholder redirect URI for now (e.g.
   `https://localhost`) and come back after [Phase 2, Step 2](#step-2-deploy-via-template-spec-wizard) to
   replace it with the real hostname (e.g. `https://<swa-name>.azurestaticapps.net`) from the
   deployment outputs. Do **not** add a *Mobile and desktop* platform to this registration; that
   reclassifies the app and breaks SPA sign-in with **AADSTS9002326**.
6. Under **Authentication** → **Advanced settings**, leave **Allow public client flows** = **No**. Setting it to **Yes** breaks the browser portal: the SPA's cross-origin token redemption is then rejected with **AADSTS9002326** (*cross-origin token redemption is permitted only for the 'Single-Page Application' client-type*).
7. Under **Expose an API**:
   - Set the **Application ID URI** to `api://<portalClientId>` (accept the default; Entra
     pre-fills this with the `portalClientId` you already recorded above).
   - Click **Add a scope** and fill in the form:
     - **Scope name**: `user_impersonation`
     - **Who can consent?**: Admins and users
     - **Admin consent display name**: `Access Cloud Imaging Portal backend`
     - **Admin consent description**: `Allows the app to access the Cloud Imaging Portal backend on behalf of the signed-in user.`
     - Leave **User consent display name** and **User consent description** blank; Entra falls back to the admin consent text above when they're empty, so there's nothing to fill in.
     - **State**: Enabled
     - Click **Add scope**.
     - The browser portal (an MSAL SPA) requests `api://<portalClientId>/user_impersonation` to obtain an access token for the portal backend; without it, sign-in fails with **AADSTS500011 (invalid_resource)** and the portal renders a blank page after sign-in.
8. Under **App roles**, add:
   - `CloudImaging.Administrator` (value: `CloudImaging.Administrator`, allowed for: Users/Groups)
   - `CloudImaging.Technician` (value: `CloudImaging.Technician`, allowed for: Users/Groups)
   - `CloudImaging.Reader` (value: `CloudImaging.Reader`, allowed for: Users/Groups): read-only, limited to the Dashboard and Reports; see [roles-and-access.md](roles-and-access.md).
9. *(Optional)* Under **API permissions**, grant admin consent for the **Microsoft Graph →
   User.Read** delegated permission; Entra adds it to every new registration by default, so
   there's nothing to add, only consent to grant. It's used only to show the signed-in user's
   **profile photo** in the header (their name always displays regardless, from the sign-in
   token); without consent, the portal falls back to an initials avatar. Skipping this is
   always safe, including in tenants that block user consent to Graph permissions: the
   portal never bundles this scope with the required sign-in scope, so it can't affect
   sign-in either way.

> The portal backend calls the Operator API using its **managed identity** (the `CloudImaging.PortalAccess` app role, assigned automatically in Phase 3, Step 1). The Portal registration therefore needs **no** API permission to the Operator API.

#### Registration 2: Cloud Imaging Operator API (service-to-service)

1. New registration. Name: `Cloud Imaging Operator API`
2. Supported account types: **Single tenant**
3. Once created, record the **Application (client) ID** shown on the **Overview** page → this
   is `operatorApiClientId`. You'll need it in the next step below.
4. Under **Expose an API**:
   - Set the **Application ID URI** to `api://<operatorApiClientId>` (accept the default;
     Entra pre-fills this with the `operatorApiClientId` you already recorded above).
   - Click **Add a scope** and fill in the form:
     - **Scope name**: `user_impersonation`
     - **Who can consent?**: Admins and users
     - **Admin consent display name**: `Access Cloud Imaging Operator API`
     - **Admin consent description**: `Allows the app to access the Cloud Imaging Operator API on behalf of the signed-in user.`
     - Leave **User consent display name** and **User consent description** blank; Entra falls back to the admin consent text above when they're empty, so there's nothing to fill in.
     - **State**: Enabled
     - Click **Add scope**.
     - This delegated scope lets the Media Builder (an interactive user-facing public client) obtain an access token for the Operator API; without it, sign-in fails with **AADSTS650057 (Invalid resource)**.
5. Under **App roles**, add:
   - `CloudImaging.PortalAccess` (allowed for: **Applications**): used by the portal backend managed identity.
   - `CloudImaging.MediaBuilderAccess` (allowed for: **Both (Users/Groups + Applications)**): assigned to the technicians who run the Media Builder. It **must** allow *Users/Groups*, otherwise the technician's interactive token never carries the role and API calls return `403`.

#### Registration 3: Cloud Imaging Media Builder (desktop public client)

1. New registration. Name: `Cloud Imaging Media Builder`
2. Supported account types: **Single tenant**
3. Once created, record the **Application (client) ID** shown on the **Overview** page → this
   is `mediaBuilderClientId`.
4. Under **Authentication** → **Add a platform** → **Mobile and desktop applications**, add the redirect URI `http://localhost`. The Media Builder signs in with the interactive loopback (authorization code + PKCE) flow.
5. Under **Authentication** → **Advanced settings**, leave **Allow public client flows** = **No**; the loopback flow is already identified as a public client by its `http://localhost` redirect and does not need this flag.
6. Under **App roles**, add the same two user roles:
   - `CloudImaging.Administrator` (value: `CloudImaging.Administrator`, allowed for: Users/Groups)
   - `CloudImaging.Technician` (value: `CloudImaging.Technician`, allowed for: Users/Groups)
7. **Grant access to the Operator API (required):** Under **API permissions** → **Add a
   permission** → **My APIs** → select **Cloud Imaging Operator API** → **Delegated
   permissions** → check `user_impersonation` → **Add permissions**. Then click **Grant admin
   consent for &lt;your tenant&gt;**. The Media Builder requests the
   `api://<operatorApiClientId>/.default` scope, which only succeeds once this permission is
   consented; skipping it fails sign-in with **AADSTS650057**.

#### *(Optional)* Verify the three app registrations before continuing

`verify-app-registrations.ps1` re-checks the settings above via Microsoft Graph (read-only, no
changes made) and prints a pass/fail checklist, so mistakes surface now instead of as a cryptic
AADSTS error later. It ships in the deployment bundle, so download and extract that first
([Phase 2, Step 1](#step-1-publish-the-template-spec)), then run it from the `deploy/` folder:

```powershell
.\scripts\verify-app-registrations.ps1 `
  -PortalClientId       "<portalClientId>" `
  -OperatorApiClientId  "<operatorApiClientId>" `
  -MediaBuilderClientId "<mediaBuilderClientId>"
```

A couple of things (admin consent status) can't be checked from a script and are called out at
the end as manual checks instead.

---

## Phase 2: Deploy the Azure Resources

### Step 1: Publish the Template Spec

Download **`cloud-imaging-<version>.zip`** (e.g. `cloud-imaging-mse-ci-v1.0.0.zip`) from the
backend/infrastructure release on the [GitHub Releases](https://github.com/MSEndpointMgr/CloudImaging/releases)
page; it's the bundle without `-client-` or `-mediabuilder-` in its tag, since those ship
separately. Optionally verify it against the accompanying `SHA256SUMS` file. Extract the
bundle, then extract **`deploy.zip`** from inside it; that produces the `deploy/` folder used
below.

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

The script publishes a **Template Spec** named `CloudImaging` into `rg-cloudimaging-specs`,
then prints a direct link to that resource in the Azure Portal (the marketplace `#create` URL
doesn't work for Template Specs, so this manual link is how you reach it). Open the link
(signed in to the target tenant) and click **Deploy** on the Template Spec resource's page;
that launches the Form View wizard used in Step 2. If you'd rather navigate manually instead
of using the link: **Resource groups → `rg-cloudimaging-specs` → CloudImaging → Deploy**.

---

### Step 2: Deploy via Template Spec Wizard

Fill in the wizard. It has two tabs:

**Basics**

- **Subscription / Resource group / Region**: the deployment's target. Pick the region closest
  to your users. Your Entra tenant ID is taken from this subscription automatically, so there's
  nothing to enter for it.
- **Deployment Environment**: `Production (prod)` (or `Development (dev)` for testing). This
  becomes the second segment of every resource name.

**Configuration**

- **Resource Prefix**: 1 to 4 lowercase letters/digits (e.g. `corp`). The wizard shows a live
  preview of the resulting resource names beneath the field.
- **Cloud Imaging Portal - Application (client) ID**: from Registration 1 (`portalClientId`)
- **Operator API - Application (client) ID**: from Registration 2 (`operatorApiClientId`)
- **Cloud Imaging Media Builder - Application (client) ID**: from Registration 3 (`mediaBuilderClientId`)
- **Deployment Tier**: `Standard` enforces mTLS at the Function App layer and is the right
  choice for most organizations. `Enterprise` additionally puts an Application Gateway WAF_v2
  in front of the Device Gateway API.
- **Compute**, **Security Settings**, and **Networking**: every field here is pre-filled with a
  working default. Only change them if you have specific sizing, certificate/token lifetime, or
  IP addressing requirements.

Click **Create** and wait ~15 minutes.

**Update the Portal redirect URI now that you have a real hostname.** Open the deployed
resource group → the **Static Web App** resource → copy its **URL** from the Overview page
(e.g. `https://<swa-name>.azurestaticapps.net`). Go back to **Registration 1 (Cloud Imaging
Portal) → Authentication** and replace the placeholder redirect URI you added in
[Phase 1, Step 1](#registration-1-cloud-imaging-portal-browser-spa) with this real hostname. Portal
sign-in fails with **AADSTS50011 (redirect URI mismatch)** until this is done.

> **That's the only redirect URI this app needs.** The portal website and its backend are
> served from that same Static Web App address, so there's no separate backend URL to
> register anywhere.

---

## Phase 3: Post-Deployment Setup

### Step 1: Run the Post-Deployment Scripts

```powershell
$rg          = "corp-prod-rg" # your resource group
$prefix      = "corp"         # Resource Prefix from the deployment wizard
$environment = "prod"         # dev or prod

# 1. Grant Microsoft Graph permission to the ImagingCore managed identity
.\scripts\grant-graph-permissions.ps1 `
   -ResourceGroupName $rg `
   -ResourcePrefix $prefix `
   -Environment $environment

# 2. Assign the Operator API service-level role to the Portal backend's managed identity
.\scripts\assign-service-roles.ps1 `
  -ResourceGroupName $rg `
   -OperatorApiClientId "<operatorApiClientId>" `
   -ResourcePrefix $prefix `
   -Environment $environment
```

Both scripts authenticate interactively when needed and are safe to rerun. The Graph permission
script requests the delegated `AppRoleAssignment.ReadWrite.All` and `Application.Read.All` scopes,
then verifies that the Imaging Core managed identity has the Microsoft Graph application permission
`DeviceManagementServiceConfig.Read.All`. Microsoft Entra role and permission changes can take time
to propagate; wait several minutes before testing pre-flight authorization.

`assign-service-roles.ps1` only handles `CloudImaging.PortalAccess` (assigned to the Portal
backend's managed identity, which can't be done from the Azure Portal UI). The **user-level**
roles below, including `CloudImaging.MediaBuilderAccess`, still need to be assigned manually,
per person.

> **Already deployed with an older version of this script?** Earlier versions also granted
> `CloudImaging.MediaBuilderAccess` directly to the Media Builder app registration itself
> (visible as an **Application** permission on its **API permissions** page). That grant never
> did anything (Media Builder only ever signs in as the technician, so Entra never reads it),
> and it's safe to delete: open **Cloud Imaging Media Builder → API permissions**, find the
> `CloudImaging.MediaBuilderAccess` row, and **Remove permission**. Leave `user_impersonation`
> and `User.Read` on that same page alone.

---

### Step 2: Assign Access to Your Administrators and Technicians

The app roles created in Phase 1, Step 1 are just definitions; nobody can sign in successfully until
they're assigned to actual users or groups. For the full access model (what each role grants in the
Portal vs. the Media Builder), see [roles-and-access.md](roles-and-access.md). To assign access:

1. **Portal users**: Entra ID → **Enterprise applications** → **Cloud Imaging Portal** →
   **Users and groups** → **Add user/group** → assign `CloudImaging.Administrator` or
   `CloudImaging.Technician` to each person (or group) who signs in to the browser portal.
2. **Media Builder users**: repeat on the **Cloud Imaging Media Builder** enterprise application;
   assignments are **not** shared between the two registrations, so a technician who uses both apps
   needs a role on *each* one.
3. **Media Builder API access**: Media Builder users need one more assignment, on a *different*
   enterprise application. Go to **Enterprise applications** → **Cloud Imaging Operator API** →
   **Users and groups** → **Add user/group**, and assign the same people (or group) to the
   **CloudImaging.MediaBuilderAccess** role. The roles in point 2 control what the Media Builder
   *shows* a technician; this one is what lets the app call the Operator API at all. Without it,
   sign-in succeeds but every API call returns `403`.

A user with no role assigned on a registration can still sign in, but sees an "Access denied"
screen (Portal) or has every workflow blocked (Media Builder).

---

### Step 3: Initial Portal Configuration

Before handing the portal to your technicians, sign in as **CloudImaging.Administrator** and
complete these one-time setup tasks. The first two are **required**; imaging cannot happen
without them; the rest are optional and can be revisited any time from **Configuration**.

1. **Generate the boot media certificate** (required). Navigate to **Configuration** →
   **Certificates** tab → click **Generate Certificate** and wait for confirmation. This
   certificate secures the mTLS handshake between booted devices and the Device Gateway API,
   and is embedded into every boot image Media Builder generates. Without an active
   certificate, **Generate Boot Image** (Phase 4) refuses to run.
2. **Add at least one OS image to the catalog** (required). Navigate to **OS Images** →
   **Upload** and provide the WIM/ESD file, a name, and a version. Devices have nothing to
   image without at least one catalog entry.
3. **Configure branding** *(optional)*. Navigate to **Branding** to set your organization's
   logo and colors, shown in both the portal and the boot media UI.
4. **Add locations** *(optional)*. Navigate to **Locations** to define site labels technicians
   can tag onto boot media and filter devices by; see [roles-and-access.md](roles-and-access.md).
5. **Configure device pre-flight authorization** *(optional, off by default)*. Before enabling it,
   confirm the Microsoft Graph permission script above ended with `[OK]`. Add at least one test
   device to Windows Autopilot, or add its exact manufacturer, model, and serial-number tuple under
   **Intune → Devices → Enrollment → Corporate device identifiers**. Under **Configuration** →
   **Preflight**, enable the requirement, then boot that device and verify its session reaches
   `SessionAllowed`. Also test an unknown device and verify that it reaches
   `SessionNotAuthorized`. Disable the setting again if either result is unexpected; when disabled,
   registration skips Microsoft Graph and proceeds.
6. **Review Security and Miscellaneous settings** *(optional)*. Still under **Configuration**:
   certificate/token validity periods and clock skew tolerance (**Security**) and session history
   retention (**Miscellaneous**) have working defaults and only need attention for organization-specific requirements.
7. **Turn on version checking** *(optional)*. **Configuration** → **Miscellaneous** → **Version**
   shows the release this deployment is running. Enabling **Check GitHub for new releases** lets
   the portal backend periodically read the latest published release number from github.com and
   notify administrators when an upgrade is available. It is **off by default**: it is the only
   outbound call the portal makes outside your tenant, so leave it off if your network policy
   prohibits that, or if the deployment has no outbound internet access. Nothing is ever
   installed automatically; see [upgrade-instructions.md](upgrade-instructions.md).

---

## Phase 4: Configure the Media Builder

### Step 1: Configure and Distribute the Media Builder

The Media Builder is a desktop app that technicians run on their own workstations. Each
installation has to be told which tenant, sign-in app and Operator API to use, so collect these
four values first:

| # | Value | Where it comes from |
|---|---|---|
| 1 | Media Builder client ID | [Phase 1, Registration 3](#registration-3-cloud-imaging-media-builder-desktop-public-client) → Application (client) ID |
| 2 | Tenant ID | Entra ID → Overview → Directory (tenant) ID |
| 3 | Operator API client ID | [Phase 1, Registration 2](#registration-2-cloud-imaging-operator-api-service-to-service) → Application (client) ID |
| 4 | Operator API URL | The `operatorApiUrl` deployment output from [Phase 2, Step 2](#step-2-deploy-via-template-spec-wizard) |

All four are public identifiers, not secrets. The Media Builder is an MSAL *public client* and has
no client secret. Never put certificates, client secrets or connection strings in its configuration.

Supply them either on the **MSI command line** (managed deployment, recommended) or in
**`appsettings.json`** (manual install). Both are covered below.

#### Building the msiexec command line

One property per value, in the same order, all on a single line (as Intune requires):

```
msiexec /i CloudImaging.MediaBuilder.msi /qn /norestart ENTRAIDCLIENTID=<1. media builder client ID> ENTRAIDTENANTID=<2. tenant ID> OPERATORAPICLIENTID=<3. operator API client ID> OPERATORAPIBASEURL=<4. operator API URL>
```

Filled in:

```
msiexec /i CloudImaging.MediaBuilder.msi /qn /norestart ENTRAIDCLIENTID=6f1c2a84-3d5b-4e17-9a2c-0b7e5d81f430 ENTRAIDTENANTID=b3e7d902-14af-4c68-85d1-7f2a6c093e55 OPERATORAPICLIENTID=d84a5f61-27c9-4b03-9e8f-1a6d3c70b214 OPERATORAPIBASEURL=https://ci-operator-api-prod.azurewebsites.net
```

Rules when assembling it:

- Property names are **uppercase**, with **no spaces** around `=`.
- Quote any value containing a space: `INSTALLFOLDER="D:\Apps\Cloud Imaging Media Builder"`.
- Give `OPERATORAPICLIENTID` the **bare client ID**. The MSI turns it into the
  `api://<operatorApiClientId>/.default` scope; pasting a full `api://...` string there is rejected
  with an error rather than installed as a broken doubled-up scope.
- `OPERATORAPIBASEURL` is the hostname only: no `/api` suffix, no trailing slash.
- Every property is optional. Omit them all to install now and configure later; the app runs but
  the sign-in screen shows *"Entra ID sign-in is not configured"* until the values are present.

Two optional properties exist beyond the four above:

| Property | Purpose |
|---|---|
| `INSTALLFOLDER` | Install location. Defaults to `%ProgramFiles%\MSEndpointMgr\Cloud Imaging Media Builder`. |
| `OPERATORAPISCOPE` | The complete scope string, replacing what `OPERATORAPICLIENTID` would derive. Only needed if you replaced the Operator API's default `api://<operatorApiClientId>` Application ID URI with a custom one. Wins when both are supplied. |

Append `/l*v C:\Windows\Temp\mediabuilder-install.log` while troubleshooting a failed install.

The MSI writes the values to `HKLM\SOFTWARE\MSEndpointMgr\CloudImaging\MediaBuilder`. To confirm
what a workstation actually received:

```powershell
Get-ItemProperty 'HKLM:\SOFTWARE\MSEndpointMgr\CloudImaging\MediaBuilder' |
  Select-Object ClientId, TenantId, OperatorApiScope, OperatorApiBaseUrl, InstallFolder
```

Those same registry values can be pushed by a Group Policy preference or an Intune remediation
script, so a client ID rotation or an Operator API URL change never requires repackaging.

#### Configuring a manual install

For an xcopy install from `CloudImaging.MediaBuilder.zip` (no MSI), put the values in
`appsettings.json` beside `CloudImaging.MediaBuilder.exe` instead:

```json
{
  "EntraId": {
    "ClientId": "<1. media builder client ID>",
    "TenantId": "<2. tenant ID>",
    "OperatorApiScope": "api://<3. operator API client ID>/.default"
  },
  "OperatorApi": {
    "BaseUrl": "<4. operator API URL>"
  }
}
```

Note that this file wants the **full scope**, not the bare client ID. Deriving it is something
only the MSI does for you.

The registry values take precedence over `appsettings.json`, value by value, so an MSI-managed
workstation ignores whatever the file contains. Leave the shipped file at its empty defaults when
you deploy the MSI.

> **Two things are needed for a working Media Builder sign-in. Don't skip either:**
> 1. The **`user_impersonation` delegated permission** on the Media Builder registration, added
>    *and* admin-consented ([Phase 1, Registration 3](#registration-3-cloud-imaging-media-builder-desktop-public-client));
>    prevents `AADSTS650057` at sign-in.
> 2. The **`CloudImaging.MediaBuilderAccess` role assignment** to the user or group on the Operator
>    API enterprise application ([Phase 3, Step 2](#step-2-assign-access-to-your-administrators-and-technicians));
>    prevents `403` on API calls.

#### Installing the Windows ADK on technician workstations

Every workstation that will run **Generate Boot Image** needs the **Windows Assessment and
Deployment Kit (ADK)**. It is *not* bundled with Media Builder and must be installed
separately on each technician device (or baked into the device image).

The ADK ships as **two separate installers that must be the SAME version**:

| # | Installer | Run it, then select **only** |
|---|---|---|
| 1 | `adksetup.exe` (base ADK) | ☑ **Deployment Tools**; leave everything else (USMT, Windows Performance Toolkit, Application Compatibility Toolkit, VAMT, etc.) unchecked; Media Builder doesn't need them |
| 2 | `adkwinpesetup.exe` (WinPE add-on, downloaded and run *separately* after step 1) | ☑ **Windows Preinstallation Environment (WinPE)**; it's the only option |

Get both installers, matched to the same ADK release, from the official Microsoft page:
**<https://learn.microsoft.com/en-us/windows-hardware/get-started/adk-install>** (it links the
correct base ADK and WinPE add-on downloads for the current release together; don't mix an
older cached installer for one with a newer download for the other).

> **Why "Deployment Tools" specifically:** it's the feature that installs `DISM.exe`,
> `Oscdimg.exe`, and `copype.cmd`'s supporting environment (`DandISetEnv.bat`) under
> `Deployment Tools\<arch>\...`, everything Media Builder's boot image generation shells out
> to. The other ADK features (USMT, ACT, Windows Performance Toolkit, etc.) are unrelated to
> Cloud Imaging and only add install time/disk space.

> ⚠️ **The base ADK and the WinPE add-on version MUST match exactly.** They're installed and
> updated independently, so it's easy to end up with (for example) an older Deployment Tools
> paired with a newer WinPE add-on. When that happens, boot image generation fails deep inside
> Microsoft's `copype.cmd` with an error like *"Unable to copy boot sector file:
> ...\Deployment Tools\amd64\Oscdimg\efisys_EX.bin"*, the newer WinPE add-on's boot files
> reference boot-sector files that the older Deployment Tools release doesn't ship yet. Media
> Builder detects this specific mismatch and reports it clearly rather than surfacing the raw
> `copype.cmd` error, but the fix is always the same: **re-run `adksetup.exe` and update
> Deployment Tools to the same release as the WinPE add-on.**
>
> To check for a mismatch yourself, compare these two Add/Remove Programs entries; they must
> report the **same** version:
> ```powershell
> Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' |
>   Where-Object { $_.DisplayName -match 'Windows (Assessment and )?Deployment (Kit|Tools)$|WinPE Add-ons' } |
>   Select-Object DisplayName, DisplayVersion
> ```

Media Builder verifies the ADK + WinPE add-on are present (and blocks **Generate Boot Image**
with a link to the page above if not) before you can start a build.

#### Deploying the Media Builder with Intune

Every [GitHub Release](https://github.com/MSEndpointMgr/CloudImaging/releases) in the
`mse-ci-mediabuilder-v#.#.#` stream ships **`CloudImaging.MediaBuilder.msi`**, plus
`CloudImaging.MediaBuilder.zip` with identical content if you prefer your own packaging process.

**1. Wrap the MSI** with the
[Microsoft Win32 Content Prep Tool](https://github.com/Microsoft/Microsoft-Win32-Content-Prep-Tool):

```
IntuneWinAppUtil.exe -c <folder containing the msi> -s CloudImaging.MediaBuilder.msi -o <output folder>
```

**2. Create the Win32 app** (**Apps → Windows → Add → Windows app (Win32)**):

| Setting | Value |
|---|---|
| Install command | The single-line `msiexec` command from [Building the msiexec command line](#building-the-msiexec-command-line) |
| Uninstall command | `msiexec /x {ProductCode} /qn /norestart` |
| Install behavior | **System** (the app installs per-machine) |
| Detection rule | **MSI** → the product code, with *"MSI product version check"* set to greater-than-or-equal the version you are deploying |
| Requirement | 64-bit Windows |
| Dependency | Windows ADK + WinPE add-on |

The **Windows ADK + WinPE add-on** is deliberately not bundled in the MSI. Express it as an Intune
app dependency, or bake it into the technician device image.

##### What the MSI does

- Installs the self-contained app to `%ProgramFiles%\MSEndpointMgr\Cloud Imaging Media Builder`.
- Writes the configuration to `HKLM\SOFTWARE\MSEndpointMgr\CloudImaging\MediaBuilder`.
- Creates a Start menu shortcut for **all users**.
- Removes the app, the shortcut and the registry key on uninstall.
- On upgrade, carries the existing configuration forward: a newer MSI installed *without* the
  configuration properties reads the current values out of the registry and writes them back, so
  "deploy the new version" never blanks a working configuration. Properties supplied on the command
  line always win over the retained values.

Sign-in itself stays interactive per technician (MSAL loopback flow) and is unaffected by any of
the above.

---

## Phase 5: Operate Cloud Imaging

### Step 1: Generate Your First Boot Image

1. Open the **Cloud Imaging Media Builder** on a technician workstation with the Windows ADK
   + WinPE add-on installed (see [Installing the Windows ADK](#installing-the-windows-adk-on-technician-workstations))
2. Sign in with your Entra ID credentials (must have `CloudImaging.Administrator` or `CloudImaging.Technician` role)
3. Select **Generate Boot Image**
4. Choose **Auto-download** (fetches latest Cloud Imaging Client from GitHub) or specify a local path
5. Optionally check **Enable command prompt access** under **Support Tools** (see
   [Client support tools](#client-support-tools) below); off by default
6. Click **Generate**; the wizard produces a `.wim` file
7. Upload the WIM to the portal: **Boot Images** → **Upload**

> **Device Gateway URL is resolved automatically.** The Media Builder looks up the live Device
> Gateway API URL from the Operator API and stamps it into the Client's `appsettings.json` while
> building the WIM; there's nothing to configure manually, and every boot image build picks up
> the current URL even if the Device Gateway was redeployed or renamed since the Client binaries
> were built.

#### Client support tools

The Operation Selection screen offers two troubleshooting tools, both accessible without leaving
the always-on-top Cloud Imaging Client window:

- **Connect to Wi-Fi**: always available (no opt-in required). Opens a dedicated window that
  scans for visible networks via `netsh wlan` and lets the technician connect to an
  Open or WPA2/WPA3-Personal network by SSID + passphrase. Enterprise/802.1X networks are shown
  (greyed out, labeled "Not supported") but cannot be connected to via this flow. No Wi-Fi
  credential is ever persisted; the temporary WLAN profile (which embeds the passphrase in
  plain text, per the netsh profile schema) is deleted immediately after the connect attempt.
- **Command Prompt**: hidden unless the boot image was built with **Enable command prompt
  access** checked (off by default, per boot image). Launches an interactive `cmd.exe` for
  advanced troubleshooting, temporarily dropping the Client window's always-on-top behavior so
  the console isn't hidden behind it; the Client returns to always-on-top automatically once the
  console is closed.

> **Security note:** only enable command prompt access for boot images used in trusted,
> supervised environments (e.g. IT staging); an interactive shell in WinPE has full access to
> local disks and the network. Leave it unchecked for boot images that may be used unattended or
> by end users.

---

### Step 2: Prepare USB Media

1. In **Cloud Imaging Media Builder**, select **Prepare USB Storage Device**
2. Select the boot image to deploy
3. Insert a USB drive (32 GB+, USB 3.x)
4. Click **Prepare**; the drive will be partitioned and the WIM deployed

---

### Step 3: Image a Device

1. Boot the target device from the USB drive
2. The device auto-launches Cloud Imaging Client and displays a **passcode**
3. Sign in to the portal as a Technician
4. In the **Sessions** section, click **Couple Device** and enter the passcode
5. Click **Assign Image**, select an OS image, confirm
6. The device downloads and applies the image automatically

---

## Upgrading

Upgrading an existing deployment is a separate procedure with its own prerequisites, its own
dry-run step, and three release streams that are each upgraded differently. It has its own
guide: **[upgrade-instructions.md](upgrade-instructions.md)**.

---

## Getting Help

- **Upgrading an existing deployment**: [upgrade-instructions.md](upgrade-instructions.md)
- **Roles & access reference**: [roles-and-access.md](roles-and-access.md)
- **Troubleshooting**: [operations-runbook.md](operations-runbook.md)
- **Issues**: [GitHub Issues](https://github.com/MSEndpointMgr/CloudImaging/issues)
