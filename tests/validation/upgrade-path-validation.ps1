<#
.SYNOPSIS
    In-place upgrade path validation.

.DESCRIPTION
    Validates that upgrade.ps1 moves an existing Cloud Imaging deployment onto the release
    contained in a newer bundle, without any manual intervention in the Azure portal.

    The check records the package each Function App is running from, runs the upgrade script out
    of the supplied bundle, then confirms every app is pointing at a newly uploaded package and
    that the portal still answers its health endpoint.

.PARAMETER ResourceGroupName
    Resource group containing the existing Cloud Imaging deployment.

.PARAMETER SubscriptionId
    Subscription holding the resource group.

.PARAMETER BundlePath
    Folder a newer release bundle was extracted into. upgrade.ps1 sits at its root.

.EXAMPLE
    .\upgrade-path-validation.ps1 -ResourceGroupName "corp-prod-rg" -SubscriptionId "<subscription-id>" -BundlePath "C:\Downloads\cloud-imaging-mse-ci-v1.2.0"

.NOTES
    FileName:    upgrade-path-validation.ps1
    Author:      MSEndpointMgr
    Contact:     @MSEndpointMgr
    Created:     2026-09-13
    Updated:     2026-09-14

    Version history:
    1.0.0 - (2026-09-13) Initial release
    1.1.0 - (2026-09-14) Rewritten for upgrade.ps1. Bundle replaces the archive parameter, and
                         run-from-package settings replace tag comparison
#>
#Requires -Modules Az.Accounts, Az.Resources, Az.Websites
[CmdletBinding(SupportsShouldProcess)]
param (
    [Parameter(Mandatory = $true, HelpMessage = "Resource group containing the existing deployment.")]
    [ValidateNotNullOrEmpty()]
    [string] $ResourceGroupName,
    [Parameter(Mandatory = $true, HelpMessage = "Subscription holding the resource group.")]
    [ValidateNotNullOrEmpty()]
    [string] $SubscriptionId,
    [Parameter(Mandatory = $true, HelpMessage = "Folder a newer release bundle was extracted into.")]
    [ValidateNotNullOrEmpty()]
    [string] $BundlePath
)
Begin {
    $ErrorActionPreference = "Stop"

    # Keeps the output to what this script reports rather than Az SDK banners and prompts.
    Update-AzConfig -DisplayBreakingChangeWarning $false -DisplaySurveyMessage $false -CheckForUpgrade $false -Scope Process | Out-Null

    $Timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $FailureCount = 0
    $ValidationResults = New-Object -TypeName "System.Collections.Generic.List[System.Object]"

    function Add-ValidationResult {
        param (
            [Parameter(Mandatory = $true, HelpMessage = "Check the result applies to.")]
            [ValidateNotNullOrEmpty()]
            [string] $Check,
            [Parameter(Mandatory = $true, HelpMessage = "Outcome of the check.")]
            [ValidateSet("Pass", "Fail")]
            [string] $Status,
            [Parameter(Mandatory = $false, HelpMessage = "Detail shown in the summary table.")]
            [string] $Detail = ""
        )
        $ValidationResults.Add([PSCustomObject]@{
            Check = $Check
            Status = $Status
            Detail = $Detail
        })
    }

    # Az.Websites returns app settings masked, and Az.Functions is deliberately avoided because
    # its cmdlets call Invoke-WebRequest without -UseBasicParsing, which blocks on a script
    # execution prompt under Windows PowerShell. ARM returns the values directly.
    function Get-RunFromPackageUri {
        param (
            [Parameter(Mandatory = $true, HelpMessage = "Name of the Function App to read.")]
            [ValidateNotNullOrEmpty()]
            [string] $Name
        )
        $Path = "/subscriptions/$($SubscriptionId)/resourceGroups/$($ResourceGroupName)/providers/Microsoft.Web/sites/$($Name)/config/appsettings/list?api-version=2024-04-01"
        $Response = Invoke-AzRestMethod -Path $Path -Method POST
        if ($Response.StatusCode -ne 200) {
            throw "Reading application settings for $($Name) failed with HTTP $($Response.StatusCode)."
        }
        $Properties = ($Response.Content | ConvertFrom-Json).properties
        if ($null -eq $Properties.WEBSITE_RUN_FROM_PACKAGE) {
            return ""
        }
        return [string]$Properties.WEBSITE_RUN_FROM_PACKAGE
    }
}
Process {
    try {
        Write-Output "Upgrade path validation"
        Write-Output "Resource group: $($ResourceGroupName)"
        Write-Output "Bundle: $($BundlePath)"
        Write-Output ""

        # The upgrade script deploys the packages sitting beside it, so the bundle it is run from
        # decides the resulting version. A copy invoked from anywhere else invalidates the test.
        $UpgradeScript = Join-Path -Path $BundlePath -ChildPath "upgrade.ps1"
        if (-not (Test-Path -Path $UpgradeScript)) {
            throw "upgrade.ps1 was not found at the root of $($BundlePath). Point -BundlePath at an extracted release bundle."
        }

        $VersionFile = Join-Path -Path $BundlePath -ChildPath "version.txt"
        $TargetVersion = if (Test-Path -Path $VersionFile) { (Get-Content -Path $VersionFile -Raw).Trim() } else { "unknown" }
        Write-Output "Target release: $($TargetVersion)"

        $AzContext = Get-AzContext
        if ($null -eq $AzContext) {
            Connect-AzAccount -Subscription $SubscriptionId | Out-Null
            $AzContext = Get-AzContext
        }
        if ($AzContext.Subscription.Id -ne $SubscriptionId) {
            $AzContext = (Set-AzContext -Subscription $SubscriptionId).Context
        }
        if ($AzContext.Subscription.Id -ne $SubscriptionId) {
            throw "Could not select subscription $($SubscriptionId)."
        }

        Write-Output ""
        Write-Output "Recording the packages currently in use"
        $FunctionApps = Get-AzWebApp -ResourceGroupName $ResourceGroupName | Where-Object { $PSItem.Kind -like "*functionapp*" }
        if ($FunctionApps.Count -eq 0) {
            throw "No Function Apps were found in $($ResourceGroupName)."
        }

        $PreUpgradePackages = @{}
        foreach ($FunctionApp in $FunctionApps) {
            $PreUpgradePackages[$FunctionApp.Name] = Get-RunFromPackageUri -Name $FunctionApp.Name
            Write-Output "  $($FunctionApp.Name): $(if ([string]::IsNullOrEmpty($PreUpgradePackages[$FunctionApp.Name])) { 'no package set' } else { Split-Path -Path $PreUpgradePackages[$FunctionApp.Name] -Leaf })"
        }

        Write-Output ""
        Write-Output "Running the upgrade"
        if ($PSCmdlet.ShouldProcess($ResourceGroupName, "Run upgrade.ps1 from $($BundlePath)")) {
            & $UpgradeScript -ResourceGroupName $ResourceGroupName -SubscriptionId $SubscriptionId
            if ($LASTEXITCODE -ne 0 -and $null -ne $LASTEXITCODE) {
                Add-ValidationResult -Check "Upgrade script" -Status "Fail" -Detail "Exited with code $($LASTEXITCODE)"
                $FailureCount++
            }
            else {
                Add-ValidationResult -Check "Upgrade script" -Status "Pass" -Detail "Completed without terminating"
            }
        }

        Write-Output ""
        Write-Output "Verifying each Function App moved to a new package"
        foreach ($FunctionApp in $FunctionApps) {
            $PostUpgradePackage = Get-RunFromPackageUri -Name $FunctionApp.Name
            if ([string]::IsNullOrEmpty($PostUpgradePackage)) {
                Write-Output "  $($FunctionApp.Name): no package set"
                Add-ValidationResult -Check $FunctionApp.Name -Status "Fail" -Detail "WEBSITE_RUN_FROM_PACKAGE is not set"
                $FailureCount++
            }
            elseif ($PostUpgradePackage -eq $PreUpgradePackages[$FunctionApp.Name]) {
                Write-Output "  $($FunctionApp.Name): unchanged"
                Add-ValidationResult -Check $FunctionApp.Name -Status "Fail" -Detail "Still running the pre-upgrade package"
                $FailureCount++
            }
            else {
                Write-Output "  $($FunctionApp.Name): $(Split-Path -Path $PostUpgradePackage -Leaf)"
                Add-ValidationResult -Check $FunctionApp.Name -Status "Pass" -Detail (Split-Path -Path $PostUpgradePackage -Leaf)
            }
        }

        Write-Output ""
        Write-Output "Smoke testing the portal health endpoint"
        $PortalBackend = Get-AzWebApp -ResourceGroupName $ResourceGroupName | Where-Object { $PSItem.Name -like "*-ci-app-portal" } | Select-Object -First 1
        if ($null -eq $PortalBackend) {
            Add-ValidationResult -Check "Portal health" -Status "Fail" -Detail "Portal backend App Service was not found"
            $FailureCount++
        }
        else {
            try {
                # The App Service restarts as the new package is extracted, so a cold start can
                # outlast the first request. Retry rather than reporting a false failure.
                $HealthUri = "https://$($PortalBackend.DefaultHostName)/api/health"
                $Attempt = 0
                $Healthy = $false
                while ($Attempt -lt 10 -and -not $Healthy) {
                    $Attempt++
                    try {
                        $HealthResponse = Invoke-WebRequest -Uri $HealthUri -UseBasicParsing -TimeoutSec 30
                        if ($HealthResponse.StatusCode -eq 200) {
                            $Healthy = $true
                        }
                    }
                    catch [System.Exception] {
                        if ($Attempt -lt 10) {
                            Write-Output "  Attempt $($Attempt) of 10 failed, retrying in 15 seconds"
                            Start-Sleep -Seconds 15
                        }
                    }
                }
                if ($Healthy) {
                    Write-Output "  HTTP 200 from $($HealthUri)"
                    Add-ValidationResult -Check "Portal health" -Status "Pass" -Detail $HealthUri
                }
                else {
                    Add-ValidationResult -Check "Portal health" -Status "Fail" -Detail "No successful response from $($HealthUri)"
                    $FailureCount++
                }
            }
            catch [System.Exception] {
                Write-Warning -Message "Portal health check failed: $($_.Exception.Message)"
                Add-ValidationResult -Check "Portal health" -Status "Fail" -Detail $_.Exception.Message
                $FailureCount++
            }
        }
    }
    catch [System.Exception] {
        Write-Warning -Message "Upgrade path validation failed: $($_.Exception.Message)"
        Add-ValidationResult -Check "Validation run" -Status "Fail" -Detail $_.Exception.Message
        $FailureCount++
    }
}
End {
    Write-Output ""
    Write-Output "Validation summary ($($Timestamp))"
    $ValidationResults | Format-Table -AutoSize

    if ($FailureCount -gt 0) {
        Write-Warning -Message "Upgrade path validation: FAIL. $($FailureCount) check(s) did not pass."
        exit 1
    }

    Write-Output "Upgrade path validation: PASS"
}
