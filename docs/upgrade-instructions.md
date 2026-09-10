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
| Backend and infrastructure | `mse-ci-v#.#.#` | The three Function Apps, the portal (frontend and backend), Bicep templates, deploy scripts | [Part 1](#part-1-upgrade-the-azure-components) with `update.ps1` |
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

### What `update.ps1` does and does not do

It is worth being precise about this, because the boundary catches people out.

**It does:**

- Download the release bundle, verify it against the release's published checksum, and extract it
- Deploy new application code to the three Function Apps (Device Gateway, Operator, Imaging Core)
- Deploy new application code to the portal backend App Service
- Deploy the new portal frontend to the Static Web App
- Repair two settings that a code-only upgrade would otherwise miss: the blob upload role
  assignment on the package storage accounts, and the browser upload permission rules on the
  boot image storage account

**It does not:**

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
| Az PowerShell | `Install-Module Az` |
| Azure CLI | [Install guide](https://learn.microsoft.com/azure/cli/install) |
| Azure Static Web Apps CLI | `npm install -g @azure/static-web-apps-cli`. Required: the portal frontend cannot be deployed without it |
| Azure role | Owner, or Contributor plus User Access Administrator, on the deployment's resource group |
| Signed in | Both `Connect-AzAccount` and `az login`, to the tenant and subscription holding the deployment |

Both sign-ins are needed because the script uses Az PowerShell to inspect the resource group
and the Azure CLI to perform the deployments.

> **Why User Access Administrator?** Function App code is deployed with Run-From-Package: the
> release archive is uploaded to a storage container and the app is pointed at it. That upload
> authenticates with Entra ID and requires the **Storage Blob Data Contributor** role, which
> resource-group Owner or Contributor alone does **not** include. `update.ps1` grants that role
> to your signed-in identity automatically on first run, which is itself a role assignment.
>
> If your account cannot assign roles, the script warns instead of failing. Ask an administrator
> to grant **Storage Blob Data Contributor** on the two storage accounts whose names end in
> `stapp` and `stcore`, then re-run.

---

## Part 1: Upgrade the Azure Components

### Step 1: Get the release bundle

Download **`cloud-imaging-<version>.zip`** from the backend and infrastructure release on the
[Releases page](https://github.com/MSEndpointMgr/CloudImaging/releases). It is the bundle
whose tag has no `-client-` or `-mediabuilder-` segment.

Extract it. `update.ps1` sits at the root of the extracted folder, next to the component
archives. Open PowerShell there.

You can skip the manual download entirely and let the script fetch the release for you, as
shown in the next step.

### Step 2: Do a dry run first

`update.ps1` supports `-WhatIf`, which walks the whole process, resolves the release, verifies
the archive, inspects your resource group, and reports every action it would take, without
changing anything:

```powershell
.\update.ps1 -ResourceGroupName "corp-prod-rg" -WhatIf
```

Read the output before continuing. It confirms which Function Apps, App Service, and Static Web
App were found, which is the fastest way to catch a wrong resource group name or a missing
sign-in.

### Step 3: Run the upgrade

```powershell
# Upgrade to the newest stable backend release
.\update.ps1 -ResourceGroupName "corp-prod-rg"

# Or pin an exact version
.\update.ps1 -ResourceGroupName "corp-prod-rg" -Version "1.2.0"

# Or use an archive you already downloaded (no internet access needed for the download step)
.\update.ps1 -ResourceGroupName "corp-prod-rg" -ArchivePath "C:\Downloads\cloud-imaging-mse-ci-v1.2.0.zip"
```

`-Version latest` (the default) resolves a dedicated `mse-ci-iac-latest` alias release rather
than whatever the repository published most recently, so it can never accidentally hand you a
Client or Media Builder release.

What you should see, in order:

1. The resolved release version and a link to it
2. `Integrity verified (SHA-256 matches the release manifest)`
3. `All required component artifacts present.`
4. The storage role and upload permission checks, each either granted or already in place
5. One upload, settings change, and restart per Function App
6. The portal backend deployment
7. The Static Web App deployment
8. `Cloud Imaging upgrade complete.`

If the archive fails its integrity check, the script deletes the download and stops. Do not work
around this. Re-run it, and if it fails a second time, raise an issue rather than deploying the
archive.

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

`update.ps1` never runs Bicep. When a release note says infrastructure changed, meaning a new
Azure resource, a changed setting, a new role assignment, or a new network rule, you also need
to redeploy the Template Spec.

1. Extract `deploy.zip` from the release bundle. This produces the `deploy/` folder.
2. Publish the new Template Spec version:

   ```powershell
   cd deploy
   .\scripts\publish-template-spec.ps1 `
     -ResourceGroupName "rg-cloudimaging-specs" `
     -Location "eastus" `
     -Version "1.2.0"
   ```

3. Open the Template Spec in the Azure portal, click **Deploy**, and fill in the wizard with
   **exactly the same values you used originally**, including the same resource prefix,
   environment, and the three application client IDs. Changing the prefix or environment
   produces a second parallel deployment rather than upgrading the existing one.
4. Re-run `update.ps1` afterwards. A Bicep deployment can reset application settings that point
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

Re-run `update.ps1` pinned to the release you were previously on:

```powershell
.\update.ps1 -ResourceGroupName "corp-prod-rg" -Version "1.1.0"
```

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
| `Could not resolve the 'mse-ci-iac-latest' release` | No stable backend release has been published yet, or the machine cannot reach github.com. Pass `-Version` with an explicit version, or download the bundle manually and use `-ArchivePath`. |
| `Integrity check FAILED` | The download is corrupt or has been tampered with. The script already deleted it. Re-run. If it recurs, report it rather than bypassing the check. |
| `The Azure Static Web Apps CLI ('swa') is not installed` | Run `npm install -g @azure/static-web-apps-cli` and re-run. The backend components upgraded before this point are already done, and re-running is safe. |
| `No Function Apps found in resource group` | Wrong resource group name, or the Azure CLI is signed in to a different subscription. Check with `az account show`. |
| Warnings about `Storage Blob Data Contributor` | Your account cannot assign roles. Ask an administrator to grant that role on the `*stapp` and `*stcore` storage accounts, then re-run. |
| Portal loads the old interface after upgrading | Browser cache. Hard refresh with Ctrl+F5. |
| Portal shows errors or empty lists right after upgrading | A Function App is still restarting. Wait a minute and refresh. If it persists, check Application Insights as described in the [operations runbook](operations-runbook.md). |
| Devices fail to authenticate after upgrading | Almost always a certificate rotation rather than the upgrade. Confirm whether the boot media certificate was regenerated; if so, boot media must be rebuilt. |

Re-running `update.ps1` is safe at any point. Every step is repeatable, and a partial upgrade is
resolved by simply running it again.

---

## Getting Help

- **First-time deployment**: [setup-instructions.md](setup-instructions.md)
- **Day-to-day operations and troubleshooting**: [operations-runbook.md](operations-runbook.md)
- **Roles and access reference**: [roles-and-access.md](roles-and-access.md)
- **Issues**: [GitHub Issues](https://github.com/MSEndpointMgr/CloudImaging/issues)
