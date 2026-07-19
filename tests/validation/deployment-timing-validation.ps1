<#
.SYNOPSIS
    Deployment timing validation — SC-009: clean-tenant deploy in <= 120 minutes (T118).

.DESCRIPTION
    Times a complete clean-tenant deployment from a fresh resource group to all six
    components running. SC-009 target: <= 120 minutes.

.PARAMETER ResourceGroupName
    Target resource group (must not already contain Cloud Imaging resources).

.PARAMETER Location
    Azure region for the deployment.

.PARAMETER ParameterFile
    Path to the Bicep parameter file (e.g. src/deploy/parameters/dev.parameters.json).
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $ResourceGroupName,
    [Parameter(Mandatory)] [string] $Location,
    [string] $ParameterFile = 'src/deploy/parameters/dev.parameters.json'
)

$ErrorActionPreference = 'Stop'
$target = [TimeSpan]::FromMinutes(120)

Write-Host "Deployment Timing Validation — SC-009 target: <= $($target.TotalMinutes) min"
Write-Host "Resource group : $ResourceGroupName"
Write-Host "Location       : $Location"

$sw = [System.Diagnostics.Stopwatch]::StartNew()

try {
    if ($PSCmdlet.ShouldProcess($ResourceGroupName, 'Create resource group and deploy')) {
        # Step 1: Create resource group
        Write-Host "[$('{0:mm:ss}' -f $sw.Elapsed)] Creating resource group…"
        New-AzResourceGroup -Name $ResourceGroupName -Location $Location -Force | Out-Null

        # Step 2: Bicep deployment
        Write-Host "[$('{0:mm:ss}' -f $sw.Elapsed)] Running Bicep deployment…"
        $deployment = New-AzResourceGroupDeployment `
            -ResourceGroupName $ResourceGroupName `
            -TemplateFile 'src/deploy/bicep/main.bicep' `
            -TemplateParameterFile $ParameterFile `
            -Name "ci-timing-$(Get-Date -Format 'yyyyMMddHHmm')" `
            -ErrorAction Stop

        Write-Host "[$('{0:mm:ss}' -f $sw.Elapsed)] Bicep deployment complete: $($deployment.ProvisioningState)"
    }
} finally {
    $sw.Stop()
}

$elapsed = $sw.Elapsed
Write-Host ""
Write-Host "Total deployment time: $($elapsed.ToString('hh\:mm\:ss'))"
Write-Host "SC-009 target        : $($target.ToString('hh\:mm\:ss'))"

if ($elapsed -gt $target) {
    Write-Warning "SC-009 FAIL: $($elapsed.TotalMinutes.ToString('F1')) min exceeds $($target.TotalMinutes) min target."
    exit 1
} else {
    Write-Host "SC-009 PASS." -ForegroundColor Green
}
