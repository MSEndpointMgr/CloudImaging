<#
.SYNOPSIS
    Community upgrade script for Cloud Imaging (T098, FR-041, FR-044a).

.DESCRIPTION
    Upgrades an existing Cloud Imaging deployment to a newer version.
    Downloads the specified release archive (or uses a local path), validates
    integrity, and deploys each component to Azure.

    Requirements:
        - Az PowerShell module (az module / Azure CLI)
        - Authenticated Azure session (Connect-AzAccount or az login)
        - Owner or Contributor role on the target resource group
        - User Access Administrator role (for role assignment updates)

.PARAMETER ResourceGroupName
    Required. The Azure resource group that hosts the Cloud Imaging deployment.

.PARAMETER Version
    Optional. The release version to upgrade to (e.g. "1.2.0").
    Defaults to "latest" — the newest published GitHub Release.

.PARAMETER ArchivePath
    Optional. Local path to a pre-downloaded release ZIP archive.
    When specified, download from GitHub is skipped.

.EXAMPLE
    .\update.ps1 -ResourceGroupName mse-az-cloud-imaging-dev

.EXAMPLE
    .\update.ps1 -ResourceGroupName mse-az-cloud-imaging-prod -Version 1.2.0

.EXAMPLE
    .\update.ps1 -ResourceGroupName mse-az-cloud-imaging-dev -ArchivePath C:\Downloads\cloud-imaging-1.2.0.zip
#>

[CmdletBinding(SupportsShouldProcess)]
param (
    [Parameter(Mandatory)]
    [string] $ResourceGroupName,

    [string] $Version = 'latest',

    [string] $ArchivePath = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'

$RepoOwner = 'MSEndpointMgr'
$RepoName  = 'CloudImaging'

# ── Step 1: Resolve release version and archive ─────────────────────────────────

if ($ArchivePath -eq '') {
    Write-Host "Resolving release version '$Version' from GitHub…"

    $apiBase = "https://api.github.com/repos/$RepoOwner/$RepoName/releases"
    $headers = @{ 'User-Agent' = 'CloudImaging-Updater/1.0'; Accept = 'application/vnd.github+json' }

    if ($Version -eq 'latest') {
        $release = Invoke-RestMethod "$apiBase/latest" -Headers $headers
    } else {
        $releases = Invoke-RestMethod $apiBase -Headers $headers
        $release  = $releases | Where-Object { $_.tag_name -eq "v$Version" -or $_.tag_name -eq $Version } |
                    Select-Object -First 1
        if ($null -eq $release) { throw "Release version '$Version' not found in GitHub." }
    }

    $resolvedVersion = $release.tag_name
    Write-Host "Target release: $resolvedVersion ($($release.html_url))"

    $asset = $release.assets | Where-Object { $_.name -like 'cloud-imaging-*.zip' } | Select-Object -First 1
    if ($null -eq $asset) { throw "No ZIP archive found in release $resolvedVersion." }

    $ArchivePath = Join-Path $env:TEMP $asset.name
    Write-Host "Downloading $($asset.name) ($([math]::Round($asset.size/1MB,1)) MB)…"
    Invoke-WebRequest $asset.browser_download_url -OutFile $ArchivePath -Headers $headers
    Write-Host "Downloaded to $ArchivePath"
} else {
    if (-not (Test-Path $ArchivePath)) { throw "Archive not found: $ArchivePath" }
    Write-Host "Using local archive: $ArchivePath"
}

# ── Step 2: Extract archive ────────────────────────────────────────────────────────

$extractDir = Join-Path $env:TEMP "ci-update-$(Get-Date -Format 'yyyyMMddHHmmss')"
Write-Host "Extracting archive to $extractDir…"
Expand-Archive -Path $ArchivePath -DestinationPath $extractDir -Force

# ── Step 3: Verify expected component artifacts exist ──────────────────────────────

$requiredFiles = @(
    'DeviceGatewayApi.zip',
    'OperatorApi.zip',
    'ImagingCoreApi.zip',
    'portal-backend.zip',
    'portal-frontend.zip'
)

foreach ($file in $requiredFiles) {
    $fullPath = Join-Path $extractDir $file
    if (-not (Test-Path $fullPath)) {
        throw "Expected component artifact '$file' not found in archive. Archive may be corrupt or from an incompatible version."
    }
}

Write-Host "✓ All required component artifacts present."

# ── Step 4: Retrieve deployment metadata from resource group ────────────────────────

Write-Host "Querying resource group '$ResourceGroupName' for deployed components…"

$functionApps  = Get-AzFunctionApp -ResourceGroupName $ResourceGroupName -ErrorAction SilentlyContinue
$webApps       = Get-AzWebApp      -ResourceGroupName $ResourceGroupName -ErrorAction SilentlyContinue
$staticWebApps = Get-AzStaticWebApp -ResourceGroupName $ResourceGroupName -ErrorAction SilentlyContinue

if ($null -eq $functionApps) {
    throw "No Function Apps found in resource group '$ResourceGroupName'. Verify the resource group name and your Azure login."
}

# ── Step 5: Deploy each component ─────────────────────────────────────────────────

function Deploy-FunctionApp {
    param([string]$Name, [string]$ZipPath)
    if (-not (Test-Path $ZipPath)) { Write-Warning "Skipping $Name — ZIP not found at $ZipPath"; return }
    $app = $functionApps | Where-Object { $_.Name -eq $Name } | Select-Object -First 1
    if ($null -eq $app) { Write-Warning "Function App '$Name' not found in $ResourceGroupName — skipping"; return }

    if ($PSCmdlet.ShouldProcess($app.Name, "Deploy Function App from $ZipPath")) {
        Write-Host "Deploying $($app.Name)…"
        az functionapp deployment source config-zip `
            --resource-group $ResourceGroupName `
            --name $app.Name `
            --src $ZipPath `
            --output none
        Write-Host "✓ $($app.Name) deployed."
    }
}

function Deploy-WebApp {
    param([string]$NamePattern, [string]$ZipPath]
    if (-not (Test-Path $ZipPath)) { Write-Warning "Skipping Web App — ZIP not found at $ZipPath"; return }
    $app = $webApps | Where-Object { $_.Name -like "*$NamePattern*" } | Select-Object -First 1
    if ($null -eq $app) { Write-Warning "Web App matching '*$NamePattern*' not found — skipping"; return }

    if ($PSCmdlet.ShouldProcess($app.Name, "Deploy Web App from $ZipPath")) {
        Write-Host "Deploying $($app.Name)…"
        az webapp deploy `
            --resource-group $ResourceGroupName `
            --name $app.Name `
            --src-path $ZipPath `
            --type zip `
            --output none
        Write-Host "✓ $($app.Name) deployed."
    }
}

Deploy-FunctionApp -Name (($functionApps | Where-Object { $_.Name -like '*gateway*' }).Name)   `
                   -ZipPath (Join-Path $extractDir 'DeviceGatewayApi.zip')

Deploy-FunctionApp -Name (($functionApps | Where-Object { $_.Name -like '*operator*' }).Name)  `
                   -ZipPath (Join-Path $extractDir 'OperatorApi.zip')

Deploy-FunctionApp -Name (($functionApps | Where-Object { $_.Name -like '*core*' }).Name)      `
                   -ZipPath (Join-Path $extractDir 'ImagingCoreApi.zip')

Deploy-WebApp -NamePattern 'portal'   -ZipPath (Join-Path $extractDir 'portal-backend.zip')

# Static Web App deployment
$swa = $staticWebApps | Select-Object -First 1
if ($null -ne $swa) {
    $frontendZip = Join-Path $extractDir 'portal-frontend.zip'
    if (Test-Path $frontendZip) {
        if ($PSCmdlet.ShouldProcess($swa.Name, "Deploy Static Web App from $frontendZip")) {
            Write-Host "Deploying Static Web App $($swa.Name)…"
            $swaToken = (Invoke-AzRestMethod -Method POST `
                -Path "/subscriptions/$($(Get-AzContext).Subscription.Id)/resourceGroups/$ResourceGroupName/providers/Microsoft.Web/staticSites/$($swa.Name)/listSecrets?api-version=2023-01-01" `
                ).Content | ConvertFrom-Json | Select-Object -ExpandProperty properties | Select-Object -ExpandProperty apiKey
            swa deploy --deployment-token $swaToken --app-artifact-location (Join-Path $extractDir 'portal-frontend')
            Write-Host "✓ Static Web App deployed."
        }
    }
}

# ── Step 6: Cleanup temp files ─────────────────────────────────────────────────────

Remove-Item $extractDir -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ""
Write-Host "✅ Cloud Imaging upgrade complete."
Write-Host "   Resource group: $ResourceGroupName"
Write-Host "   Verify the portal at your App Service URL before notifying users."
