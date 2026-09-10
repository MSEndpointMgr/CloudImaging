# Cloud Imaging: Deployment Package

**Version**: See release tag  
**Spec reference**: FR-041, FR-044a, T107

---

## Package Contents

| File / Folder | Description |
|---|---|
| `DeviceGatewayApi.zip` | Azure Functions isolated-worker .NET 10 package |
| `OperatorApi.zip` | Azure Functions isolated-worker .NET 10 package |
| `ImagingCoreApi.zip` | Azure Functions isolated-worker .NET 10 package |
| `portal-backend.zip` | Node 22 / Express 5 App Service package |
| `portal-frontend.zip` | React 19 + Vite Static Web App output |
| `CloudImaging.Client.zip` | WinPE-runnable WPF executable (net10.0-windows, win-x64 self-contained) |
| `CloudImaging.MediaBuilder.zip` | Technician workstation WPF app (net10.0-windows, win-x64 self-contained) |
| `deploy/` | Bicep modules, parameter templates, and management scripts |
| `deploy/bicep/main.bicep` | Root Bicep template, passes to Template Spec wizard |
| `deploy/uiFormDefinition.json` | Azure Template Spec portal wizard UI definition |
| `deploy/parameters/dev.parameters.json` | Dev-environment parameter template |
| `deploy/parameters/test.parameters.json` | Test-environment parameter template |
| `deploy/parameters/prod.parameters.json` | Production parameter template |
| `deploy/scripts/publish-template-spec.ps1` | Publishes Bicep + uiFormDefinition as Azure Template Spec |
| `deploy/scripts/update.ps1` | Community upgrade script, zip-deploys all components |
| `deploy/scripts/assign-service-roles.ps1` | Assigns Entra app roles post-deployment |
| `deploy/scripts/grant-graph-permissions.ps1` | Grants Microsoft Graph permissions to ImagingCore MSI |
| `SHA256SUMS` | SHA-256 checksums for all ZIP artifacts |
| `update.ps1` | Shortcut alias to `deploy/scripts/update.ps1` |

---

## Prerequisites

| Requirement | Version / Notes |
|---|---|
| Azure subscription | Owner or Contributor + User Access Administrator on target resource group |
| Azure CLI | Latest stable (`az login` required for zip deploy) |
| Az PowerShell module | `Connect-AzAccount` required for Bicep deployment |
| Azure Static Web Apps CLI | `npm install -g @azure/static-web-apps-cli` |
| Entra ID tenant | Permissions to create/modify App Registrations |

---

## First-Time Deployment (Fresh Tenant)

### 1. Publish the Template Spec

```powershell
cd deploy/

.\scripts\publish-template-spec.ps1 `
  -ResourceGroupName rg-cloudimaging-prod `
  -Location eastus `
  -Version 1.0.0
```

The script outputs a portal URL. Open it to launch the wizard.

### 2. Fill in the Wizard

| Tab | Field | Value |
|---|---|---|
| Basics | Subscription | Your subscription |
| Basics | Resource group | Create or select |
| Configuration | Resource prefix | `mse` (≤ 4 alphanumeric) |
| Configuration | Environment | `prod` |
| Configuration | User Auth Client ID | App Registration A Client ID |
| Configuration | Operator API Client ID | App Registration B Client ID |
| Configuration | Directory (Tenant) ID | Auto-populated |
| Configuration | Azure region | e.g. `eastus` |
| Configuration | Deployment tier | Standard (WAF_v2 = Enterprise) |

Click **Review + Create** then **Create**.

### 3. Post-Deployment Scripts

Run once after Bicep completes:

```powershell
# Grant Microsoft Graph permission to ImagingCore managed identity
.\scripts\grant-graph-permissions.ps1 -ResourceGroupName rg-cloudimaging-prod

# Assign service-level Entra app roles
.\scripts\assign-service-roles.ps1 -ResourceGroupName rg-cloudimaging-prod
```

### 4. Configure GitHub Actions (core dev team only)

See `docs/setup-instructions.md` for OIDC federated credential setup.

---

## Upgrade (Existing Deployment)

```powershell
.\update.ps1 -ResourceGroupName rg-cloudimaging-prod

# Or with a specific release archive:
.\update.ps1 -ResourceGroupName rg-cloudimaging-prod `
             -ArchivePath C:\Downloads\cloud-imaging-v1.2.0.zip
```

The script:
1. Discovers deployed Function Apps and App Service by naming convention
2. Performs zip deploy for each component in sequence
3. Does **not** re-provision infrastructure

---

## Component Architecture

```
Internet
    │
    ▼
[Device Gateway API]  ← WinPE Cloud Imaging Client (mTLS)
    │ Private Link
    ▼
[Imaging Core API]  ← internal-only, no public endpoint
    │
    ├─ Table Storage (session state, images, boot images, branding)
    ├─ Blob Storage (OS image blobs, boot WIMs, branding logo)
    └─ Azure Key Vault (boot media certificate PFX)

[Operator API]  ← Portal backend (Entra-authenticated)
    │ Private Link
    └── Imaging Core API

[Portal Backend]  ← App Service, Entra ID B2C/MT
[Portal Frontend] ← Azure Static Web Apps

[Cloud Imaging Client]   → Device Gateway API (mTLS)
[Cloud Imaging Media Builder] → Operator API (Entra)
```

---

## Naming Convention

| Resource type | Pattern | Example |
|---|---|---|
| Function Apps | `{prefix}-{env}-func-{name}` | `mse-prod-func-gateway` |
| App Service | `{prefix}-{env}-app-portal` | `mse-prod-app-portal` |
| Static Web App | `{prefix}-{env}-swa-portal` | `mse-prod-swa-portal` |
| Storage Account | `{prefix}{env}st{purpose}` | `mseprodstapp` |
| Key Vault | `{prefix}-{env}-kv` | `mse-prod-kv` |
| VNet | `{prefix}-{env}-vnet` | `mse-prod-vnet` |
| Log Analytics | `{prefix}-{env}-law` | `mse-prod-law` |

---

## Checksums

Verify download integrity:

```powershell
Get-FileHash cloud-imaging-v1.0.0.zip -Algorithm SHA256
# Compare against SHA256SUMS
```

---

*For troubleshooting, see `docs/operations-runbook.md`. For quick start, see `docs/setup-instructions.md`.*
