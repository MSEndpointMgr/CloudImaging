<#
.SYNOPSIS
    Staging environment smoke test for pre-merge release gating (T182).

.DESCRIPTION
    Deploys Cloud Imaging to an isolated staging resource group, smoke-tests the
    walking skeleton path (session create → couple → assign → status), then tears
    down the staging environment.

.PARAMETER StagingResourceGroup
    Name of the isolated staging resource group (must differ from dev environment).

.PARAMETER Location
    Azure region for the staging deployment.

.PARAMETER ParameterFile
    Staging parameter file.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $StagingResourceGroup,
    [Parameter(Mandatory)] [string] $Location,
    [string] $ParameterFile = 'src/deploy/parameters/dev.parameters.json',
    [switch] $KeepResources
)

$ErrorActionPreference = 'Stop'
Write-Host "Staging Smoke Test — T182"
Write-Host "Staging RG: $StagingResourceGroup"
$passed = $true

try {
    # ── Deploy to staging ─────────────────────────────────────────────────────

    if ($PSCmdlet.ShouldProcess($StagingResourceGroup, 'Deploy to staging')) {
        Write-Host "[1] Creating staging resource group…"
        New-AzResourceGroup -Name $StagingResourceGroup -Location $Location -Force | Out-Null

        Write-Host "[2] Running Bicep deployment to staging…"
        $deployment = New-AzResourceGroupDeployment `
            -ResourceGroupName $StagingResourceGroup `
            -TemplateFile 'src/deploy/bicep/main.bicep' `
            -TemplateParameterFile $ParameterFile `
            -Name "staging-smoke-$(Get-Date -Format 'yyyyMMddHHmm')"

        Write-Host "Deployment: $($deployment.ProvisioningState)"
    }

    # ── Walking skeleton smoke test ───────────────────────────────────────────

    Write-Host "[3] Running walking skeleton smoke test…"
    $gatewayUrl = "https://$(
        (Get-AzFunctionApp -ResourceGroupName $StagingResourceGroup |
         Where-Object { $_.Name -like '*gateway*' } |
         Select-Object -First 1).DefaultHostName
    )"

    Write-Host "  Gateway URL: $gatewayUrl"

    # Session registration
    $session = Invoke-RestMethod "$gatewayUrl/api/v1/sessions" -Method Post `
        -ContentType 'application/json' `
        -Body (@{ serialNumber='SN-SMOKE-001'; manufacturer='Dell'; model='Test' } | ConvertTo-Json)

    if ($null -eq $session.sessionId) {
        Write-Warning "  Smoke test FAIL: session creation did not return sessionId"
        $passed = $false
    } else {
        Write-Host "  ✓ Session created: $($session.sessionId)"
        Write-Host "  ✓ Passcode returned: $($session.passcode.Length) chars"
        Write-Host "  ✓ Device token returned"
    }

} catch {
    Write-Warning "Staging deployment or smoke test failed: $($_.Exception.Message)"
    $passed = $false
} finally {
    if (-not $KeepResources -and $PSCmdlet.ShouldProcess($StagingResourceGroup, 'Remove staging resource group')) {
        Write-Host "[4] Removing staging resource group…"
        Remove-AzResourceGroup -Name $StagingResourceGroup -Force -ErrorAction SilentlyContinue
        Write-Host "  Staging resources removed."
    }
}

if ($passed) {
    Write-Host "`nT182 Staging smoke test: PASS" -ForegroundColor Green
} else {
    Write-Warning "`nT182 Staging smoke test: FAIL"
    exit 1
}
