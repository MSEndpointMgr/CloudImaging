<#
.SYNOPSIS
    Assign the CloudImaging.PortalAccess Operator API app role to the Portal
    backend's managed identity.

.DESCRIPTION
    Performs the one post-deployment Entra ID role assignment that a script is
    required for: granting CloudImaging.PortalAccess (on the Cloud Imaging
    Operator API app registration) to the Portal backend's managed identity,
    so the Portal backend can call the Operator API.

    This can't be done from the Azure Portal: the "Enterprise applications ->
    Users and groups -> Add user/group" picker only lists users and groups,
    never managed identities, so a managed identity's app role assignment must
    be created through Microsoft Graph instead.

    (The CloudImaging.MediaBuilderAccess role, by contrast, is assigned
    directly to people in the Azure Portal -- see Step 5 in
    setup-instructions.md -- because it's the signed-in technician's own
    delegated token that the Operator API checks, not anything belonging to
    the Media Builder application itself.)

    Run this script ONCE after the Bicep deployment completes.
    It is safe to re-run — the existing assignment is skipped.

.PARAMETER ResourceGroupName
    The Azure resource group containing the deployed resources.

.PARAMETER OperatorApiClientId
    Application (client) ID of the Cloud Imaging Operator API app registration.

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
        -ResourcePrefix      "<prefix>" `
        -Environment         "<env>"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ResourceGroupName,

    [Parameter(Mandatory)]
    [string] $OperatorApiClientId,

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

# ── 5. Resolve role definition ────────────────────────────────────────────────

$portalRole = $operatorApiSp.AppRoles | Where-Object { $_.Value -eq 'CloudImaging.PortalAccess' }

if (-not $portalRole)  { throw "Role 'CloudImaging.PortalAccess' not found on the Operator API app registration. Verify the role was created in Registration 2." }

# ── 6. Assign CloudImaging.PortalAccess → Portal backend managed identity ───

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

Write-Host ""
Write-Host "=== Service-level role assignment complete ==="
Write-Host "The Portal backend can now authenticate against the Operator API."
Write-Host "Remember: CloudImaging.MediaBuilderAccess still needs to be assigned to your Media"
Write-Host "Builder users/groups manually -- see Step 5 in setup-instructions.md."
