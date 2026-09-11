<#
.SYNOPSIS
    Grant DeviceManagementServiceConfig.Read.All application permission to
    the Imaging Core API managed identity (T148, FR-026).

.DESCRIPTION
    This step is required to enable the pre-flight device authorization check
    against Microsoft Graph (Autopilot V1 and Intune Corporate Identifiers).
    Bicep cannot natively grant Microsoft Graph app roles; this script handles
    the post-deploy grant. The operation is idempotent and verifies the resulting
    assignment before returning.

.PARAMETER ResourceGroupName
    The resource group containing the Imaging Core API managed identity.

.PARAMETER ImagingCoreMsiName
    Name of the Imaging Core API managed identity. When omitted, the script uses
    ResourcePrefix and Environment, or discovers the single *-ci-msi-core identity
    in the resource group.

.EXAMPLE
    .\grant-graph-permissions.ps1 -ResourceGroupName "rg-<prefix>-<env>-cloudimaging"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ResourceGroupName,

    [string] $ImagingCoreMsiName = '',

    [string] $Environment = 'dev',

    [string] $ResourcePrefix = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

foreach ($command in @(
    'Connect-MgGraph',
    'Get-MgContext',
    'Get-MgServicePrincipal',
    'Get-MgServicePrincipalAppRoleAssignment',
    'New-MgServicePrincipalAppRoleAssignment',
    'Get-AzContext',
    'Connect-AzAccount',
    'Get-AzUserAssignedIdentity'
)) {
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
        throw "Required command '$command' is unavailable. Install Az.ManagedServiceIdentity, Microsoft.Graph.Authentication, and Microsoft.Graph.Applications."
    }
}

try {
    $azContext = Get-AzContext -ErrorAction Stop
    if (-not $azContext) {
        throw "Not connected"
    }
} catch {
    Write-Host "Not connected to Azure. Running Connect-AzAccount..."
    Connect-AzAccount | Out-Null
}

if (-not $ImagingCoreMsiName -and $ResourcePrefix) {
    $ImagingCoreMsiName = "$ResourcePrefix-$Environment-ci-msi-core"
}

if (-not $ImagingCoreMsiName) {
    $candidates = @(Get-AzUserAssignedIdentity -ResourceGroupName $ResourceGroupName |
        Where-Object { $_.Name -like '*-ci-msi-core' })

    if ($candidates.Count -ne 1) {
        throw "Could not identify one Imaging Core managed identity in '$ResourceGroupName'. Supply -ImagingCoreMsiName, or -ResourcePrefix and -Environment."
    }

    $ImagingCoreMsiName = $candidates[0].Name
}

Write-Host "Resolving Imaging Core API managed identity: $ImagingCoreMsiName"
$msi = Get-AzUserAssignedIdentity -ResourceGroupName $ResourceGroupName -Name $ImagingCoreMsiName
$principalId = $msi.PrincipalId
Write-Host "MSI principal ID: $principalId"

$graphAppId = '00000003-0000-0000-c000-000000000000'
$requiredScopes = @('AppRoleAssignment.ReadWrite.All', 'Application.Read.All')

try {
    $ctx = Get-MgContext
    if (-not $ctx -or -not $ctx.Scopes) {
        throw "Not connected"
    }
    $missingScopes = $requiredScopes | Where-Object { $_ -notin $ctx.Scopes }
    if ($missingScopes) {
        Write-Host "Re-connecting to Microsoft Graph with additional scopes: $($missingScopes -join ', ')"
        Connect-MgGraph -Scopes $requiredScopes | Out-Null
    } else {
        Write-Host "Already connected to Microsoft Graph as $($ctx.Account)"
    }
} catch {
    Write-Host "Connecting to Microsoft Graph (browser sign-in will open)..."
    Connect-MgGraph -Scopes $requiredScopes | Out-Null
}

$graphSp = @(Get-MgServicePrincipal `
    -Filter "appId eq '$graphAppId'" `
    -Property @('id', 'appId', 'appRoles')) | Select-Object -First 1
if (-not $graphSp) {
    throw "Microsoft Graph service principal was not found in the current tenant."
}

$role = $graphSp.AppRoles | Where-Object {
    $_.Value -eq 'DeviceManagementServiceConfig.Read.All' -and
    $_.AllowedMemberTypes -contains 'Application'
} | Select-Object -First 1
if (-not $role) {
    throw "Microsoft Graph application role DeviceManagementServiceConfig.Read.All was not found."
}

$existing = @(Get-MgServicePrincipalAppRoleAssignment `
    -ServicePrincipalId $principalId `
    -All) | Where-Object {
        $_.ResourceId -eq $graphSp.Id -and $_.AppRoleId -eq $role.Id
    } | Select-Object -First 1

Write-Host "Granting DeviceManagementServiceConfig.Read.All to $principalId ..."
$body = @{
    principalId = $principalId
    resourceId  = $graphSp.Id
    appRoleId   = $role.Id
}

if ($existing) {
    Write-Host "[SKIP] DeviceManagementServiceConfig.Read.All is already assigned."
} else {
    New-MgServicePrincipalAppRoleAssignment -ServicePrincipalId $principalId -BodyParameter $body | Out-Null
}

$verified = @(Get-MgServicePrincipalAppRoleAssignment `
    -ServicePrincipalId $principalId `
    -All) | Where-Object {
        $_.ResourceId -eq $graphSp.Id -and $_.AppRoleId -eq $role.Id
    } | Select-Object -First 1

if (-not $verified) {
    throw "DeviceManagementServiceConfig.Read.All assignment could not be verified."
}

Write-Host "[OK] DeviceManagementServiceConfig.Read.All verified for $ImagingCoreMsiName"
