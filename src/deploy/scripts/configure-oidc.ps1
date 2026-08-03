<#
.SYNOPSIS
    Configure Azure Workload Identity Federation for GitHub Actions OIDC (T159).

.DESCRIPTION
    Creates or updates a federated identity credential on the "Cloud Imaging GitHub Actions"
    App Registration so that GitHub Actions CI/CD pipelines can authenticate to Azure
    without long-lived secrets (OIDC passwordless auth).

    The subject claim matches GitHub Actions workflows targeting the 'azure-dev' environment:
        repo:MSEndpointMgr/CloudImaging:environment:azure-dev

    NOTE: The subject's environment segment must match the 'environment:' value used by
    the deploy workflow (.github/workflows/ci.yml uses 'azure-dev'). If they differ,
    azure/login fails with AADSTS700213 (no matching federated identity record).

.PARAMETER TenantId
    Your Entra ID tenant ID.

.PARAMETER SubscriptionId
    Azure subscription ID that hosts the Cloud Imaging resources.

.PARAMETER ResourceGroupName
    Resource group name (used to assign Contributor role to the GitHub Actions App Registration).

.PARAMETER AppDisplayName
    Display name of the App Registration to create / update.
    Defaults to "Cloud Imaging GitHub Actions".

.EXAMPLE
    .\configure-oidc.ps1 `
        -TenantId 00000000-0000-0000-0000-000000000000 `
        -SubscriptionId 11111111-1111-1111-1111-111111111111 `
        -ResourceGroupName mse-az-cloud-imaging-dev
#>
[CmdletBinding(SupportsShouldProcess)]
param (
    [Parameter(Mandatory)] [string] $TenantId,
    [Parameter(Mandatory)] [string] $SubscriptionId,
    [Parameter(Mandatory)] [string] $ResourceGroupName,
    [string] $AppDisplayName = 'Cloud Imaging GitHub Actions',
    [string] $GitHubOrg      = 'MSEndpointMgr',
    [string] $GitHubRepo     = 'CloudImaging',
    [string] $Environment    = 'azure-dev'
)

$ErrorActionPreference = 'Stop'
Write-Host "Configuring Azure Workload Identity Federation for GitHub Actions OIDC"
Write-Host "App  : $AppDisplayName"
Write-Host "Repo : $GitHubOrg/$GitHubRepo (environment: $Environment)"

# ── Step 1: Connect ───────────────────────────────────────────────────────────

Connect-AzAccount -TenantId $TenantId -SubscriptionId $SubscriptionId

# ── Step 2: Ensure the App Registration exists ────────────────────────────────

$app = Get-AzADApplication -DisplayName $AppDisplayName -ErrorAction SilentlyContinue

if ($null -eq $app) {
    Write-Host "Creating App Registration '$AppDisplayName'…"
    if ($PSCmdlet.ShouldProcess($AppDisplayName, 'Create App Registration')) {
        $app = New-AzADApplication -DisplayName $AppDisplayName
        Write-Host "Created: $($app.AppId)"
    }
} else {
    Write-Host "App Registration found: $($app.AppId)"
}

# ── Step 3: Create Service Principal ─────────────────────────────────────────

$sp = Get-AzADServicePrincipal -ApplicationId $app.AppId -ErrorAction SilentlyContinue
if ($null -eq $sp) {
    Write-Host "Creating Service Principal…"
    if ($PSCmdlet.ShouldProcess($app.AppId, 'Create Service Principal')) {
        $sp = New-AzADServicePrincipal -ApplicationId $app.AppId
    }
}

# ── Step 4: Add Federated Credential ─────────────────────────────────────────

$subject   = "repo:$GitHubOrg/${GitHubRepo}:environment:$Environment"
$credName  = "github-actions-$Environment"
$issuer    = 'https://token.actions.githubusercontent.com'

$existing = Get-AzADAppFederatedCredential `
    -ApplicationObjectId $app.Id `
    -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -eq $credName }

if ($null -eq $existing) {
    Write-Host "Adding federated identity credential…"
    if ($PSCmdlet.ShouldProcess($credName, 'Add Federated Credential')) {
        New-AzADAppFederatedCredential `
            -ApplicationObjectId $app.Id `
            -Audience            @('api://AzureADTokenExchange') `
            -Issuer              $issuer `
            -Name                $credName `
            -Subject             $subject
        Write-Host "✓ Federated credential created: $subject"
    }
} else {
    Write-Host "✓ Federated credential already exists: $subject"
}

# ── Step 5: Assign Contributor role to the resource group ────────────────────

$scope = "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroupName"
$roleExists = Get-AzRoleAssignment `
    -ObjectId $sp.Id `
    -RoleDefinitionName Contributor `
    -Scope $scope `
    -ErrorAction SilentlyContinue

if ($null -eq $roleExists) {
    Write-Host "Assigning Contributor role on $ResourceGroupName…"
    if ($PSCmdlet.ShouldProcess($scope, 'Assign Contributor role')) {
        New-AzRoleAssignment -ObjectId $sp.Id -RoleDefinitionName Contributor -Scope $scope
        Write-Host "✓ Contributor role assigned."
    }
} else {
    Write-Host "✓ Contributor role already assigned."
}

# ── Step 6: Assign User Access Administrator (for role assignments) ───────────

$uaaExists = Get-AzRoleAssignment `
    -ObjectId $sp.Id `
    -RoleDefinitionName 'User Access Administrator' `
    -Scope $scope `
    -ErrorAction SilentlyContinue

if ($null -eq $uaaExists) {
    Write-Host "Assigning User Access Administrator role…"
    if ($PSCmdlet.ShouldProcess($scope, 'Assign User Access Administrator role')) {
        New-AzRoleAssignment -ObjectId $sp.Id -RoleDefinitionName 'User Access Administrator' -Scope $scope
        Write-Host "✓ User Access Administrator role assigned."
    }
} else {
    Write-Host "✓ User Access Administrator role already assigned."
}

# ── Step 6b: Assign Storage Blob Data Contributor (run-from-package uploads) ──
# The deploy pipeline uploads Function App packages to blob storage using AAD
# auth (`az storage blob upload --auth-mode login`) and sets
# WEBSITE_RUN_FROM_PACKAGE to the blob URL. This data-plane operation requires a
# blob data role — Contributor on the resource group is NOT sufficient. Assign at
# resource-group scope so it applies to every storage account (msedev*stapp /
# msedev*stcore) regardless of setup ordering.

$blobExists = Get-AzRoleAssignment `
    -ObjectId $sp.Id `
    -RoleDefinitionName 'Storage Blob Data Contributor' `
    -Scope $scope `
    -ErrorAction SilentlyContinue

if ($null -eq $blobExists) {
    Write-Host "Assigning Storage Blob Data Contributor role…"
    if ($PSCmdlet.ShouldProcess($scope, 'Assign Storage Blob Data Contributor role')) {
        New-AzRoleAssignment -ObjectId $sp.Id -RoleDefinitionName 'Storage Blob Data Contributor' -Scope $scope
        Write-Host "✓ Storage Blob Data Contributor role assigned."
    }
} else {
    Write-Host "✓ Storage Blob Data Contributor role already assigned."
}

# ── Step 7: Output GitHub Actions secrets ─────────────────────────────────────

Write-Host ""
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
Write-Host "Set these secrets in GitHub → Settings → Environments → dev → Secrets"
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
Write-Host "AZURE_CLIENT_ID       = $($app.AppId)"
Write-Host "AZURE_TENANT_ID       = $TenantId"
Write-Host "AZURE_SUBSCRIPTION_ID = $SubscriptionId"
Write-Host "AZURE_RESOURCE_GROUP  = $ResourceGroupName"
Write-Host ""
Write-Host "Also set the following per-component secrets:"
Write-Host "FUNCTION_APP_DEVICE_GATEWAY = <gateway function app name>"
Write-Host "FUNCTION_APP_OPERATOR       = <operator function app name>"
Write-Host "FUNCTION_APP_IMAGING_CORE   = <core function app name>"
Write-Host "APP_SERVICE_PORTAL_BACKEND  = <portal backend app service name>"
Write-Host "STATIC_WEB_APP_PORTAL_TOKEN = <static web app deployment token>"
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
Write-Host "✅ OIDC Workload Identity Federation configuration complete."
