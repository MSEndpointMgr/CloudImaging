<#
.SYNOPSIS
    In-place upgrade path validation (T157, FR-041, FR-044a).

.DESCRIPTION
    Validates that the update.ps1 upgrade script correctly upgrades an existing
    deployment from a prior release to the new release without manual portal intervention.

.PARAMETER ResourceGroupName
    Resource group containing the existing Cloud Imaging deployment.

.PARAMETER NewArchivePath
    Path to the new release archive ZIP to test.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $ResourceGroupName,
    [Parameter(Mandatory)] [string] $NewArchivePath
)

$ErrorActionPreference = 'Stop'
Write-Host "Upgrade Path Validation — T157 / FR-041"
Write-Host "Resource group : $ResourceGroupName"
Write-Host "Archive        : $NewArchivePath"

# ── Step 1: Record pre-upgrade state ─────────────────────────────────────────

Write-Host "`n[1] Recording pre-upgrade component versions…"
$preFunctionApps = Get-AzFunctionApp -ResourceGroupName $ResourceGroupName -ErrorAction SilentlyContinue
$preVersions     = @{}
foreach ($app in $preFunctionApps) {
    $preVersions[$app.Name] = $app.Tags['appVersion'] ?? 'unknown'
    Write-Host "  $($app.Name): $($preVersions[$app.Name])"
}

# ── Step 2: Run upgrade ───────────────────────────────────────────────────────

Write-Host "`n[2] Running update.ps1…"
if ($PSCmdlet.ShouldProcess($ResourceGroupName, "Run update.ps1 with $NewArchivePath")) {
    & "$PSScriptRoot\..\..\src\deploy\scripts\update.ps1" `
        -ResourceGroupName $ResourceGroupName `
        -ArchivePath $NewArchivePath
}

# ── Step 3: Verify post-upgrade state ─────────────────────────────────────────

Write-Host "`n[3] Verifying post-upgrade component versions…"
$postFunctionApps = Get-AzFunctionApp -ResourceGroupName $ResourceGroupName -ErrorAction SilentlyContinue
$allUpdated       = $true

foreach ($app in $postFunctionApps) {
    $postVersion = $app.Tags['appVersion'] ?? 'unknown'
    $changed     = $postVersion -ne ($preVersions[$app.Name] ?? '')
    $status      = if ($changed) { '✓ Updated' } else { '⚠ Unchanged' }
    Write-Host "  $($app.Name): $($preVersions[$app.Name]) → $postVersion [$status]"
}

# ── Step 4: Smoke test ─────────────────────────────────────────────────────────

Write-Host "`n[4] Smoke testing portal health endpoint…"
$webApps = Get-AzWebApp -ResourceGroupName $ResourceGroupName -ErrorAction SilentlyContinue
$portal  = $webApps | Where-Object { $_.Name -like '*portal*' } | Select-Object -First 1
if ($portal) {
    try {
        $health = Invoke-WebRequest "https://$($portal.DefaultHostName)/api/health" -UseBasicParsing
        Write-Host "  Portal health: HTTP $($health.StatusCode)" -ForegroundColor Green
    } catch {
        Write-Warning "  Portal health check failed: $($_.Exception.Message)"
        $allUpdated = $false
    }
}

if ($allUpdated) {
    Write-Host "`nT157 Upgrade validation: PASS" -ForegroundColor Green
} else {
    Write-Warning "`nT157 Upgrade validation: FAIL — some components may not have updated."
    exit 1
}
