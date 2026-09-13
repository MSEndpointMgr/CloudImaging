# Cloud Imaging Upgrade Instructions

**Audience**: IT administrators upgrading an existing Cloud Imaging deployment

This is the guide to follow when moving an existing deployment to a newer release. For a
first-time deployment, follow [setup-instructions.md](setup-instructions.md) instead.

---

## Before You Start

Cloud Imaging ships as **three independently versioned release streams**. They are cut on
their own cadence, so a new release in one stream does not imply a new release in the others,
and each is upgraded differently:

| Stream | Release tag | What it contains | How you upgrade it |
|---|---|---|---|
| Backend and infrastructure | `mse-ci-v#.#.#` | The three Function Apps, the portal (frontend and backend), Bicep templates, deploy scripts | [Part 1](#part-1-upgrade-the-azure-components) with `upgrade.ps1` |
| Cloud Imaging Client | `mse-ci-client-v#.#.#` | The application that runs on the device inside WinPE | [Part 3](#part-3-upgrade-the-cloud-imaging-client): regenerate boot images and rewrite USB media |
| Media Builder | `mse-ci-mediabuilder-v#.#.#` | The technician workstation application | [Part 4](#part-4-upgrade-the-media-builder): repackage and redistribute |

Check the [Releases page](https://github.com/MSEndpointMgr/CloudImaging/releases) to see which
streams have actually moved since your last upgrade. The page is a single flat list, so read
the tag prefix rather than assuming the newest entry applies to you.

> **Let the portal tell you instead.** **Configuration** → **Miscellaneous** → **Version** shows
> the release this deployment is running, and enabling **Check GitHub for new releases** makes
> the portal notify administrators when a newer backend release is published. It is off by
> default because it is the only outbound call the portal makes outside your tenant. It only
> ever tracks the backend and infrastructure stream, and it never installs anything: upgrading
> stays the deliberate, manual procedure below.

### What `upgrade.ps1` does and does not do

It is worth being precise about this, because the boundary catches people out.

**It does:**

- Deploy new application code to the three Function Apps (Device Gateway, Operator, Imaging Core)
- Deploy new application code to the portal backend App Service
- Deploy the new portal frontend to the Static Web App
- Re-apply the browser upload permission rules on the boot image storage account, which a
  code-only upgrade would otherwise miss

**It does not:**

- **Download anything.** It deploys the component packages sitting next to it in the bundle you
  extracted, so the bundle you download decides the version you get. There is no `-Version`
  switch: to move to a different release, extract that release's bundle and run the copy of the
  script inside it.
- **Re-run the Bicep templates.** No Azure resource is created, resized, or reconfigured. If a
  release adds or changes infrastructure, you also need [Part 2](#part-2-apply-infrastructure-changes).
- **Upgrade the Cloud Imaging Client or the Media Builder.** Those are separate streams and
  separate procedures.
- **Change any portal configuration.** Your OS image catalog, boot images, branding,
  certificate, and locations are all untouched.
- **Migrate data.** Session history and catalog entries carry over as they are.

### Expected disruption

Each Function App is restarted as it is upgraded, so requests fail for roughly 30 to 60 seconds
per component while the new package loads. A device that is **mid-imaging** keeps working: it
has already been issued a download link, and the client retries transient failures. Coupling a
new device or assigning an image during the upgrade window will fail and need retrying.

Run upgrades outside imaging hours where practical, and never at the same time as a boot media
certificate rotation.

---

## Prerequisites

| Requirement | Detail |
|---|---|
| Az PowerShell | `Install-Module Az.Accounts, Az.Resources, Az.Storage, Az.Websites -Scope CurrentUser` |
| Node.js | `winget install OpenJS.NodeJS.LTS`, then open a new terminal. Needed only to install the tool below |
| Azure Static Web Apps CLI | `npm install -g @azure/static-web-apps-cli`. Required: the portal frontend cannot be deployed without it |
| Azure role | Contributor on the deployment's resource group |
| Subscription ID | `upgrade.ps1` requires it, so an upgrade can never run against the wrong subscription |

`upgrade.ps1` signs you in with `Connect-AzAccount` if no session exists, then selects the
subscription you pass. Verify the Static Web Apps CLI first with `swa --version`.

> **Contributor is enough.** Component packages are uploaded to the deployment's own storage
> accounts using the account key, which is read through the control plane. Earlier releases
> granted the signed-in identity a data plane role first and therefore needed User Access
> Administrator; that step is gone.

---

## Part 1: Upgrade the Azure Components

### Step 1: Get the release bundle

Download **`cloud-imaging-<version>.zip`** from the backend and infrastructure release on the
[Releases page](https://github.com/MSEndpointMgr/CloudImaging/releases). It is the bundle
whose tag has no `-client-` or `-mediabuilder-` segment.

Extract it. `upgrade.ps1` sits at the root of the extracted folder, next to the component
archives. Open PowerShell there.

Run the script from that folder. It deploys the packages it finds beside itself, so running a
copy from somewhere else upgrades your deployment to whatever version that copy shipped with.

### Step 2: Do a dry run first

`upgrade.ps1` supports `-WhatIf`, which inspects your resource group and reports every action it
would take, without changing anything:

```powershell
.\upgrade.ps1 -ResourceGroupName "corp-prod-rg" -SubscriptionId "<subscription-id>" -WhatIf
```

Read the output before continuing. It confirms which Function Apps, App Service, and Static Web
App were found, which is the fastest way to catch a wrong resource group name or a missing
sign-in.

### Step 3: Run the upgrade

```powershell
.\upgrade.ps1 -ResourceGroupName "corp-prod-rg" -SubscriptionId "<subscription-id>"
```

What you should see, in order:

1. The release version this bundle contains
2. The prerequisite checks, confirming the Static Web Apps CLI and every component package
3. The resolved resource names for your deployment
4. One upload, settings change, and restart per Function App
5. The portal backend deployment
6. The Static Web App deployment
7. A summary table listing each component as upgraded, skipped, or failed

Components are upgraded independently. If one fails the rest still proceed, so read the summary
rather than assuming a single error aborted everything. A component whose package is missing
from the bundle is reported as skipped, not failed.

### Step 4: Verify the upgrade

1. Open the portal and confirm it loads and you can sign in.
2. Do a hard refresh (Ctrl+F5) the first time. The browser may otherwise serve the previous
   frontend from cache.
3. Check **Sessions**, **OS Images**, and **Boot Images** all populate. Empty lists where you
   expect data usually means a Function App has not finished restarting; wait a minute and
   refresh.
4. Open **Configuration** and confirm your settings and the boot media certificate are intact.
5. Couple a test device end to end if the release touched the imaging path.

If something looks wrong, the [operations runbook](operations-runbook.md) covers diagnosis, and
[Rollback](#rollback) below covers reverting.

---

## Part 2: Apply Infrastructure Changes

`upgrade.ps1` never runs Bicep. When a release note says infrastructure changed, meaning a new
Azure resource, a changed setting, a new role assignment, or a new network rule, you also need
to redeploy the Template Spec.

1. Publish the new Template Spec version from the same extracted bundle. The `scripts/` and
   `bicep/` folders sit at its root:

   ```powershell
   # Use the same region you published the Template Spec to originally, e.g.
   # westeurope / northeurope / swedencentral in Europe, eastus / eastus2 / westus2 in the US
   .\scripts\publish-template-spec.ps1 -ResourceGroupName "rg-cloudimaging-specs" -Location "<location>"
   ```

   The version is read from the bundle's `version.txt`, so it always matches the release you
   extracted. Pass `-Version` only if you need to override that.

2. Open the Template Spec in the Azure portal, click **Deploy**, and fill in the wizard with
   **exactly the same values you used originally**, including the same resource prefix,
   environment, and the three application client IDs. Changing the prefix or environment
   produces a second parallel deployment rather than upgrading the existing one.
3. Re-run `upgrade.ps1` afterwards. A Bicep deployment can reset application settings that point
   at the deployed code packages.

Deploying the Template Spec over an existing deployment is safe and does not delete data. Storage
accounts, Key Vault contents, and Table Storage state are all preserved.

---

## Part 3: Upgrade the Cloud Imaging Client

The Client runs on the device inside WinPE. It is embedded into boot images, so it is upgraded
by **rebuilding boot media**, not by any script.

1. On a technician workstation, open the Media Builder and sign in.
2. Select **Generate Boot Image**.
3. Leave the source set to **Automatic download**. It resolves the newest stable Client release
   directly, so you do not need to look up a version number.
4. Generate the image, then upload the resulting WIM file to the portal under **Boot Images**.
5. Re-prepare USB media from the new boot image, as covered next.

### USB media

Existing USB drives keep running the older Client until they are rewritten. There is no
over-the-air update for media already in the field.

Two things force a rewrite rather than merely suggesting one:

- **The boot media certificate was rotated.** Old media stops authenticating immediately. See
  the certificate rotation procedure in the [operations runbook](operations-runbook.md).
- **A release changes the device-facing contract.** Release notes call this out explicitly.

Otherwise, older media continues to work, and you can roll new media out at your own pace.

---

## Part 4: Upgrade the Media Builder

The Media Builder is a desktop application on technician workstations. It has no automatic
update mechanism, by design.

1. Download `CloudImaging.MediaBuilder.msi` from the new **Media Builder** release
   (tag `mse-ci-mediabuilder-v#.#.#`).
2. Rewrap it and publish it as an app update through your existing deployment tooling, following
   [setup-instructions.md](setup-instructions.md#deploying-the-media-builder-with-intune).
   Update the detection rule's MSI product version to the new version, otherwise Intune considers
   the app already installed and never deploys it.

The MSI is a major upgrade: it replaces the previous version in place and **carries the existing
tenant configuration forward**, so the install command does not have to repeat the
`ENTRAIDCLIENTID` / `ENTRAIDTENANTID` / `OPERATORAPICLIENTID` / `OPERATORAPIBASEURL` properties.
Pass them only if a value actually changed, which normally happens only if you redeployed to a new
resource group.

> **Upgrading a manual/xcopy install instead?** A fresh extract of
> `CloudImaging.MediaBuilder.zip` has no tenant configuration, so copy your existing
> `appsettings.json` in beside `CloudImaging.MediaBuilder.exe`. Otherwise the application starts
> but shows *"Entra ID sign-in is not configured"*.

---

## Rollback

Extract the bundle for the release you were previously on, and run the `upgrade.ps1` inside it:

```powershell
.\upgrade.ps1 -ResourceGroupName "corp-prod-rg" -SubscriptionId "<subscription-id>"
```

Keep the bundle for the release you are currently running, so a rollback never depends on the
Releases page being reachable. If you did not, download it again from the
[Releases page](https://github.com/MSEndpointMgr/CloudImaging/releases).

This works because Function Apps are deployed by pointing at a package in storage rather than
by overwriting files in place. Every package uploaded by every upgrade is retained in the
`app-packages` container, so previous versions remain available to roll back to. The container
grows slowly over time; prune old blobs during maintenance if it becomes untidy, but keep at
least the version you are currently running and the one before it.

Two limits worth knowing:

- If the failed upgrade also included [Part 2](#part-2-apply-infrastructure-changes), rolling
  back the code does not roll back the infrastructure. Redeploy the older Template Spec version
  as well.
- Rollback does not reverse data changes. If a release migrated stored data, its release notes
  will say so and state whether rollback is supported.

---

## Troubleshooting

| Symptom | Cause and resolution |
|---|---|
| A component package is missing | The bundle was extracted incompletely, or you are running the script from outside the extracted folder. The component packages must sit beside `upgrade.ps1`. |
| `The Azure Static Web Apps CLI ('swa') is not installed` | Run `npm install -g @azure/static-web-apps-cli` and re-run. The backend components upgraded before this point are already done, and re-running is safe. |
| `No Function Apps found in resource group` | Wrong resource group name, or the deployment lives in a different subscription than the one passed to `-SubscriptionId`. |
| Portal loads the old interface after upgrading | Browser cache. Hard refresh with Ctrl+F5. |
| Portal shows errors or empty lists right after upgrading | A Function App is still restarting. Wait a minute and refresh. If it persists, check Application Insights as described in the [operations runbook](operations-runbook.md). |
| Devices fail to authenticate after upgrading | Almost always a certificate rotation rather than the upgrade. Confirm whether the boot media certificate was regenerated; if so, boot media must be rebuilt. |

Re-running `upgrade.ps1` is safe at any point. Every step is repeatable, and a partial upgrade is
resolved by simply running it again.

---

## Getting Help

- **First-time deployment**: [setup-instructions.md](setup-instructions.md)
- **Day-to-day operations and troubleshooting**: [operations-runbook.md](operations-runbook.md)
- **Roles and access reference**: [roles-and-access.md](roles-and-access.md)
- **Issues**: [GitHub Issues](https://github.com/MSEndpointMgr/CloudImaging/issues)
