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
        - Storage Blob Data Contributor on the app-package storage accounts
          (the *stapp and *stcore accounts) so the release ZIPs can be uploaded
          for Run-From-Package deployment

.PARAMETER ResourceGroupName
    Required. The Azure resource group that hosts the Cloud Imaging deployment.

.PARAMETER Version
    Optional. The release version to upgrade to (e.g. "1.2.0").
    Defaults to "latest" — the newest published GitHub Release.

.PARAMETER ArchivePath
    Optional. Local path to a pre-downloaded release ZIP archive.
    When specified, download from GitHub is skipped.

.EXAMPLE
    .\update.ps1 -ResourceGroupName rg-<prefix>-<env>-cloudimaging

.EXAMPLE
    .\update.ps1 -ResourceGroupName rg-<prefix>-<env>-cloudimaging -Version 1.2.0

.EXAMPLE
    .\update.ps1 -ResourceGroupName rg-<prefix>-<env>-cloudimaging -ArchivePath C:\Downloads\cloud-imaging-1.2.0.zip
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

# Resolve the package storage accounts. Function Apps deploy via Run-From-Package: the
# release ZIP is uploaded to the 'app-packages' container and WEBSITE_RUN_FROM_PACKAGE is
# pointed at the blob URL (read back with each app's managed identity). Operator + Gateway
# share the '*stapp' account; Core (private) uses its own '*stcore' account.
$storageAccounts   = Get-AzStorageAccount -ResourceGroupName $ResourceGroupName -ErrorAction SilentlyContinue
$storageAppName     = ($storageAccounts | Where-Object { $_.StorageAccountName -like '*stapp'  } | Select-Object -First 1).StorageAccountName
$storageCoreName    = ($storageAccounts | Where-Object { $_.StorageAccountName -like '*stcore' } | Select-Object -First 1).StorageAccountName

# Blob-name label for traceability. $resolvedVersion is set when downloading from GitHub;
# for a local -ArchivePath it is unset, so fall back to 'local'. Sanitize for blob naming.
$packageLabel = if ($resolvedVersion) { $resolvedVersion -replace '[^A-Za-z0-9._-]', '-' } else { 'local' }

# ── Step 4b: Ensure the deploying identity can upload packages ─────────────────────
# Run-From-Package uploads use Entra ID data-plane auth (`az storage blob upload
# --auth-mode login`). That path needs a *blob data* role — resource-group Owner/Contributor
# is NOT sufficient. Grant Storage Blob Data Contributor to the signed-in identity on each
# package storage account so upgrades work out of the box. Idempotent, and best-effort: if
# the caller cannot assign roles, warn with guidance rather than failing the whole upgrade.
function Grant-BlobUploadRole {
    param([string[]] $StorageAccountName)

    $accounts = $StorageAccountName | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique
    if (-not $accounts) { return }

    # Resolve the signed-in az CLI identity (user or service principal) — this is the
    # principal that performs the blob upload below, so it is the one that needs the role.
    try {
        $acct = az account show --query "{type:user.type, name:user.name}" -o json 2>$null | ConvertFrom-Json
        if ($acct.type -eq 'servicePrincipal') {
            $principalId   = az ad sp show --id $acct.name --query id -o tsv 2>$null
            $principalType = 'ServicePrincipal'
        } else {
            $principalId   = az ad signed-in-user show --query id -o tsv 2>$null
            $principalType = 'User'
        }
    } catch { $principalId = $null }

    if ([string]::IsNullOrWhiteSpace($principalId)) {
        Write-Warning "Could not resolve the signed-in identity to verify blob-upload permissions. If the upload step below fails with 'Storage Blob Data' permission errors, assign the 'Storage Blob Data Contributor' role on the storage account(s) to your identity and re-run."
        return
    }

    foreach ($name in $accounts) {
        $scope = az storage account show --name $name --resource-group $ResourceGroupName --query id -o tsv 2>$null
        if ([string]::IsNullOrWhiteSpace($scope)) { continue }

        $existing = az role assignment list --assignee $principalId --scope $scope --role 'Storage Blob Data Contributor' --query "[0].id" -o tsv 2>$null
        if (-not [string]::IsNullOrWhiteSpace($existing)) {
            Write-Host "✓ Storage Blob Data Contributor already assigned on $name."
            continue
        }

        if ($PSCmdlet.ShouldProcess($name, 'Assign Storage Blob Data Contributor')) {
            $null = az role assignment create `
                --assignee-object-id $principalId `
                --assignee-principal-type $principalType `
                --role 'Storage Blob Data Contributor' `
                --scope $scope `
                --output none 2>&1
            if ($LASTEXITCODE -eq 0) {
                Write-Host "✓ Granted Storage Blob Data Contributor on $name."
            } else {
                Write-Warning "Could not assign 'Storage Blob Data Contributor' on $name (need User Access Administrator/Owner). Ask an administrator to grant it, then re-run the upgrade."
            }
        }
    }
}

Grant-BlobUploadRole -StorageAccountName @($storageAppName, $storageCoreName)

# ── Step 5: Deploy each component ─────────────────────────────────────────────────

function Deploy-FunctionApp {
    param(
        [string]$Name,
        [string]$ZipPath,
        [string]$StorageAccount,
        [string]$Component
    )
    if (-not (Test-Path $ZipPath)) { Write-Warning "Skipping $Name — ZIP not found at $ZipPath"; return }
    $app = $functionApps | Where-Object { $_.Name -eq $Name } | Select-Object -First 1
    if ($null -eq $app) { Write-Warning "Function App '$Name' not found in $ResourceGroupName — skipping"; return }
    if ([string]::IsNullOrWhiteSpace($StorageAccount)) {
        Write-Warning "No package storage account resolved for $Name — skipping"; return
    }

    if ($PSCmdlet.ShouldProcess($app.Name, "Deploy Function App from $ZipPath via Run-From-Package")) {
        # Deploy over the storage data plane only (upload blob + set app setting + restart).
        # This never touches Kudu/SCM, so a private Function App (Core, publicNetworkAccess
        # Disabled) deploys without any temporary public-access toggle.
        $blobName = "$Component-$packageLabel-$(Get-Date -Format 'yyyyMMddHHmmss').zip"
        $blobUrl  = "https://$StorageAccount.blob.core.windows.net/app-packages/$blobName"

        # Ensure the container exists (idempotent — no-op if IaC already created it).
        az storage container create `
            --account-name $StorageAccount `
            --auth-mode login `
            --name app-packages `
            --output none

        Write-Host "Uploading package for $($app.Name) → $StorageAccount/app-packages/$blobName…"
        az storage blob upload `
            --account-name $StorageAccount `
            --auth-mode login `
            --container-name app-packages `
            --name $blobName `
            --file $ZipPath `
            --overwrite true `
            --output none

        Write-Host "Pointing $($app.Name) at the package and restarting…"
        az functionapp config appsettings set `
            --resource-group $ResourceGroupName `
            --name $app.Name `
            --settings "WEBSITE_RUN_FROM_PACKAGE=$blobUrl" `
            --output none
        az functionapp restart `
            --resource-group $ResourceGroupName `
            --name $app.Name `
            --output none
        Write-Host "✓ $($app.Name) deployed (Run-From-Package)."
    }
}

function Deploy-WebApp {
    param([string]$NamePattern, [string]$ZipPath)
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
                   -ZipPath (Join-Path $extractDir 'DeviceGatewayApi.zip')  `
                   -StorageAccount $storageAppName -Component 'gateway'

Deploy-FunctionApp -Name (($functionApps | Where-Object { $_.Name -like '*operator*' }).Name)  `
                   -ZipPath (Join-Path $extractDir 'OperatorApi.zip')  `
                   -StorageAccount $storageAppName -Component 'operator'

Deploy-FunctionApp -Name (($functionApps | Where-Object { $_.Name -like '*core*' }).Name)      `
                   -ZipPath (Join-Path $extractDir 'ImagingCoreApi.zip')  `
                   -StorageAccount $storageCoreName -Component 'core'

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
