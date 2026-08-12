<#
.SYNOPSIS
    Grant DeviceManagementServiceConfig.Read.All application permission to
    the Imaging Core API managed identity (T148, FR-026).

.DESCRIPTION
    This step is required to enable the pre-flight device authorization check
    against Microsoft Graph (Autopilot V1 and Intune Corporate Identifiers).
    Bicep cannot natively grant Microsoft Graph app roles; this script handles
    the post-deploy grant.

.PARAMETER ResourceGroupName
    The resource group containing the Imaging Core API managed identity.

.PARAMETER ImagingCoreMsiName
    Name of the Imaging Core API managed identity (defaults to the naming convention).

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

# Resolve MSI name from naming convention if not provided
if (-not $ImagingCoreMsiName) {
    if (-not $ResourcePrefix) {
        $ResourcePrefix = ($ResourceGroupName -split '-')[1]
    }
    $ImagingCoreMsiName = "$ResourcePrefix-$Environment-ci-msi-core"
}

Write-Host "Resolving Imaging Core API managed identity: $ImagingCoreMsiName"
$msi = Get-AzUserAssignedIdentity -ResourceGroupName $ResourceGroupName -Name $ImagingCoreMsiName
$principalId = $msi.PrincipalId
Write-Host "MSI principal ID: $principalId"

# DeviceManagementServiceConfig.Read.All (Microsoft Graph app role)
$graphAppId = '00000003-0000-0000-c000-000000000000'
$roleId = 'dc377aa6-52d8-4e23-b271-2a7ae04cedf3'  # DeviceManagementServiceConfig.Read.All

# Connect-MgGraph must be called before using the Microsoft.Graph module
# Ensure the caller has Application.ReadWrite.All or AppRoleAssignment.ReadWrite.All
Write-Host "Granting DeviceManagementServiceConfig.Read.All to $principalId ..."

$graphSp = Get-MgServicePrincipal -Filter "appId eq '$graphAppId'"
$body = @{
    principalId = $principalId
    resourceId  = $graphSp.Id
    appRoleId   = $roleId
}

try {
    New-MgServicePrincipalAppRoleAssignment -ServicePrincipalId $principalId -BodyParameter $body | Out-Null
    Write-Host "[OK] DeviceManagementServiceConfig.Read.All granted to $ImagingCoreMsiName"
} catch {
    if ($_.Exception.Message -like '*already exists*') {
        Write-Host "[SKIP] Role assignment already exists."
    } else {
        throw
    }
}
