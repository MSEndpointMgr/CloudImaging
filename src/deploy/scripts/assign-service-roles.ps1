<#
.SYNOPSIS
    Assign service-level Operator API app roles to the Portal backend
    managed identity and the Media Builder service principal.

.DESCRIPTION
    Performs the post-deployment Entra ID role assignments that enable
    service-to-service authentication against the Operator API:

    1. Assigns CloudImaging.PortalAccess to the Portal backend managed
       identity so the Portal backend can call the Operator API.

    2. Assigns CloudImaging.MediaBuilderAccess to the Media Builder
       app registration service principal so the Media Builder can call
       the Operator API on behalf of signed-in users.

    Run this script ONCE after the Bicep deployment completes.
    It is safe to re-run — existing assignments are skipped.

.PARAMETER ResourceGroupName
    The Azure resource group containing the deployed resources.

.PARAMETER OperatorApiClientId
    Application (client) ID of the Cloud Imaging Operator API app registration.

.PARAMETER MediaBuilderClientId
    Application (client) ID of the Cloud Imaging Media Builder app registration
    (native/public client). Its service principal receives CloudImaging.MediaBuilderAccess.

.PARAMETER ResourcePrefix
    Resource prefix used when the environment was deployed (e.g. 'mse').
    Used to derive the Portal backend managed identity name.
    Defaults to the first segment of the resource group name.

.PARAMETER Environment
    Environment label (e.g. 'dev', 'prod').
    Used to derive the Portal backend managed identity name.
    Defaults to 'dev'.

.EXAMPLE
    .\assign-service-roles.ps1 `
        -ResourceGroupName  "rg-<prefix>-<env>-cloudimaging" `
        -OperatorApiClientId "00000000-0000-0000-0000-000000000000" `
        -MediaBuilderClientId "11111111-1111-1111-1111-111111111111" `
        -ResourcePrefix      "<prefix>" `
        -Environment         "<env>"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ResourceGroupName,

    [Parameter(Mandatory)]
    [string] $OperatorApiClientId,

    [Parameter(Mandatory)]
    [string] $MediaBuilderClientId,

    [string] $ResourcePrefix = '',
    [string] $Environment    = 'dev'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── 1. Ensure Graph connection ────────────────────────────────────────────────

$requiredScopes = @('AppRoleAssignment.ReadWrite.All', 'Application.Read.All')

try {
    $ctx = Get-MgContext
    if (-not $ctx -or -not $ctx.Scopes) {
        throw "Not connected"
    }
    $missing = $requiredScopes | Where-Object { $_ -notin $ctx.Scopes }
    if ($missing) {
        Write-Host "Re-connecting to Graph with additional scopes: $($missing -join ', ')"
        Connect-MgGraph -Scopes $requiredScopes
    } else {
        Write-Host "Already connected to Microsoft Graph as $($ctx.Account)"
    }
} catch {
    Write-Host "Connecting to Microsoft Graph (browser sign-in will open)..."
    Connect-MgGraph -Scopes $requiredScopes
}

# ── 2. Ensure Az connection ───────────────────────────────────────────────────

try {
    Get-AzContext -ErrorAction Stop | Out-Null
} catch {
    Write-Host "Not connected to Azure. Running Connect-AzAccount..."
    Connect-AzAccount
}

# ── 3. Derive MSI name from naming convention if prefix not provided ──────────

if (-not $ResourcePrefix) {
    # Fallback: extract from resource group name (e.g. 'corp-prod-rg' → 'corp')
    $ResourcePrefix = ($ResourceGroupName -split '-')[0]
    Write-Host "ResourcePrefix not provided; derived '$ResourcePrefix' from resource group name."
}

$portalMsiName = "$ResourcePrefix-$Environment-ci-msi-portal"
Write-Host ""
Write-Host "Looking up Portal backend managed identity: $portalMsiName"

$msi = Get-AzUserAssignedIdentity -ResourceGroupName $ResourceGroupName -Name $portalMsiName
$portalMsiPrincipalId = $msi.PrincipalId
Write-Host "  → Principal ID: $portalMsiPrincipalId"

# ── 4. Resolve Operator API service principal ────────────────────────────────

Write-Host "Looking up Operator API service principal (appId: $OperatorApiClientId)..."
$operatorApiSp = Get-MgServicePrincipal -Filter "appId eq '$OperatorApiClientId'"
if (-not $operatorApiSp) {
    throw "Operator API service principal not found. Verify the app registration exists in this tenant and the client ID is correct."
}
Write-Host "  → Object ID: $($operatorApiSp.Id)"

# ── 5. Resolve Media Builder service principal ────────────────────────────

Write-Host "Looking up Media Builder service principal (appId: $MediaBuilderClientId)..."
$mediaBuilderSp = Get-MgServicePrincipal -Filter "appId eq '$MediaBuilderClientId'"
if (-not $mediaBuilderSp) {
    throw "Media Builder service principal not found. Verify the app registration exists in this tenant and the client ID is correct."
}
Write-Host "  → Object ID: $($mediaBuilderSp.Id)"

# ── 6. Resolve role definitions ───────────────────────────────────────────────

$portalRole = $operatorApiSp.AppRoles | Where-Object { $_.Value -eq 'CloudImaging.PortalAccess' }
$mbRole     = $operatorApiSp.AppRoles | Where-Object { $_.Value -eq 'CloudImaging.MediaBuilderAccess' }

if (-not $portalRole)  { throw "Role 'CloudImaging.PortalAccess' not found on the Operator API app registration. Verify the role was created in Registration B." }
if (-not $mbRole)      { throw "Role 'CloudImaging.MediaBuilderAccess' not found on the Operator API app registration. Verify the role was created in Registration B." }

# ── 7. Assign CloudImaging.PortalAccess → Portal backend MSI ────────────────

Write-Host ""
Write-Host "Assigning CloudImaging.PortalAccess to Portal backend MSI ($portalMsiName)..."
try {
    New-MgServicePrincipalAppRoleAssignment `
        -ServicePrincipalId $portalMsiPrincipalId `
        -BodyParameter @{
            principalId = $portalMsiPrincipalId
            resourceId  = $operatorApiSp.Id
            appRoleId   = $portalRole.Id
        } | Out-Null
    Write-Host "  [OK] CloudImaging.PortalAccess assigned."
} catch {
    if ($_.Exception.Message -like '*Permission being assigned already exists*') {
        Write-Host "  [SKIP] Assignment already exists."
    } else {
        throw
    }
}

# ── 8. Assign CloudImaging.MediaBuilderAccess → Media Builder service principal ─

Write-Host "Assigning CloudImaging.MediaBuilderAccess to Media Builder service principal..."
try {
    New-MgServicePrincipalAppRoleAssignment `
        -ServicePrincipalId $mediaBuilderSp.Id `
        -BodyParameter @{
            principalId = $mediaBuilderSp.Id
            resourceId  = $operatorApiSp.Id
            appRoleId   = $mbRole.Id
        } | Out-Null
    Write-Host "  [OK] CloudImaging.MediaBuilderAccess assigned."
} catch {
    if ($_.Exception.Message -like '*Permission being assigned already exists*') {
        Write-Host "  [SKIP] Assignment already exists."
    } else {
        throw
    }
}

Write-Host ""
Write-Host "=== Service-level role assignments complete ==="
Write-Host "The Portal backend and Media Builder can now authenticate against the Operator API."
