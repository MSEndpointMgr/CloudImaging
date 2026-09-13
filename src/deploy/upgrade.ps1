<#
.SYNOPSIS
    Upgrades an existing Cloud Imaging deployment to the release this bundle contains.

.DESCRIPTION
    Run this from the folder you extracted a newer Cloud Imaging release bundle into. The
    component packages that ship alongside this script are deployed over the existing Azure
    resources. Nothing is downloaded, and infrastructure is never re-provisioned: to choose a
    version, download that release's bundle and run the copy of this script inside it.

    Components upgraded:
        Device Gateway API    DeviceGatewayApi.zip    run from package
        Operator API          OperatorApi.zip         run from package
        Imaging Core API      ImagingCoreApi.zip      run from package
        Portal backend        portal-backend.zip      zip deploy
        Portal frontend       portal-frontend.zip     Azure Static Web Apps CLI

    The boot image storage CORS rule for the portal origin is re-applied as well. A routine
    upgrade does not re-run the Bicep template that normally sets it, so this keeps browser
    uploads of OS images working if the rule was ever lost.

    Requirements:
        Az PowerShell module. The script signs in with Connect-AzAccount if no session exists
        Contributor on the resource group holding the deployment
        Azure Static Web Apps CLI, used to publish the portal frontend

    Components are upgraded independently. If one fails the others still proceed, and the
    summary reports exactly what did and did not change. Safe to re-run.

.PARAMETER ResourceGroupName
    Name of the resource group holding the Cloud Imaging deployment.

.PARAMETER SubscriptionId
    Subscription holding the resource group. Required, so the upgrade can never run against
    whichever subscription the session happened to default to.

.EXAMPLE
    .\upgrade.ps1 -ResourceGroupName "corp-prod-rg" -SubscriptionId "<subscription-id>"

.NOTES
    FileName:    upgrade.ps1
    Author:      MSEndpointMgr
    Contact:     @MSEndpointMgr
    Created:     2026-09-13
    Updated:     2026-09-13

    Version history:
    1.0.0 - (2026-09-13) Replaces update.ps1. Packages now come from the bundle instead of
                         being downloaded, and all Azure calls use Az PowerShell
#>
#Requires -Modules Az.Accounts, Az.Resources, Az.Storage, Az.Websites
[CmdletBinding(SupportsShouldProcess)]
param (
    [Parameter(Mandatory = $true, HelpMessage = "Resource group holding the Cloud Imaging deployment.")]
    [ValidateNotNullOrEmpty()]
    [string] $ResourceGroupName,
    [Parameter(Mandatory = $true, HelpMessage = "Subscription holding the resource group.")]
    [ValidateNotNullOrEmpty()]
    [string] $SubscriptionId
)
Begin {
    $ErrorActionPreference = "Stop"

    # Keeps the operator's output to what this script reports. Az otherwise prints SDK
    # breaking-change banners, survey prompts, and performs a gallery version check that
    # raises its own confirmation prompt. Process scope, so the machine config is untouched.
    Update-AzConfig -DisplayBreakingChangeWarning $false -DisplaySurveyMessage $false -CheckForUpgrade $false -Scope Process | Out-Null

    $Timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $PackageRoot = $PSScriptRoot
    $ContainerName = "app-packages"
    $PortalUrl = ""

    $UpgradeResults = New-Object -TypeName "System.Collections.Generic.List[System.Object]"
    $FunctionAppComponents = New-Object -TypeName "System.Collections.Generic.List[System.Object]"
    $CurrentStepCount = 0
    $FailureCount = 0
    $TotalStepCount = 6

    $RequiredPackages = @(
        "DeviceGatewayApi.zip"
        "OperatorApi.zip"
        "ImagingCoreApi.zip"
        "portal-backend.zip"
        "portal-frontend.zip"
    )

    function Add-UpgradeResult {
        param (
            [Parameter(Mandatory = $true, HelpMessage = "Component the result applies to.")]
            [ValidateNotNullOrEmpty()]
            [string] $Component,
            [Parameter(Mandatory = $true, HelpMessage = "Action that was performed.")]
            [ValidateNotNullOrEmpty()]
            [string] $Action,
            [Parameter(Mandatory = $true, HelpMessage = "Outcome of the action.")]
            [ValidateSet("Success", "Skipped", "Failed")]
            [string] $Status,
            [Parameter(Mandatory = $false, HelpMessage = "Detail shown in the summary table.")]
            [string] $Detail = ""
        )
        $UpgradeResults.Add([PSCustomObject]@{
            Component = $Component
            Action = $Action
            Status = $Status
            Detail = $Detail
        })
    }

    function Get-WebAppSetting {
        param (
            [Parameter(Mandatory = $true, HelpMessage = "Resource group holding the app.")]
            [ValidateNotNullOrEmpty()]
            [string] $ResourceGroupName,
            [Parameter(Mandatory = $true, HelpMessage = "Name of the app.")]
            [ValidateNotNullOrEmpty()]
            [string] $Name
        )
        # Read straight from ARM rather than through Az.Functions, whose cmdlets resolve runtime
        # stacks over HTTP and raise an interactive parsing prompt on Windows PowerShell 5.1.
        $Path = "/subscriptions/$($SubscriptionId)/resourceGroups/$($ResourceGroupName)/providers/Microsoft.Web/sites/$($Name)/config/appsettings/list?api-version=2024-04-01"
        $Response = Invoke-AzRestMethod -Method POST -Path $Path
        if ($Response.StatusCode -ne 200) {
            throw "Could not read the app settings for '$($Name)' (HTTP $($Response.StatusCode))."
        }

        $AppSetting = @{}
        foreach ($Setting in ($Response.Content | ConvertFrom-Json).properties.PSObject.Properties) {
            $AppSetting[$Setting.Name] = $Setting.Value
        }
        return $AppSetting
    }

    function Set-WebAppSetting {
        param (
            [Parameter(Mandatory = $true, HelpMessage = "Resource group holding the app.")]
            [ValidateNotNullOrEmpty()]
            [string] $ResourceGroupName,
            [Parameter(Mandatory = $true, HelpMessage = "Name of the app.")]
            [ValidateNotNullOrEmpty()]
            [string] $Name,
            [Parameter(Mandatory = $true, HelpMessage = "Complete set of app settings to write.")]
            [hashtable] $AppSetting
        )
        # ARM only accepts app settings as a complete collection, so callers read, merge, then
        # write the whole set back.
        $Path = "/subscriptions/$($SubscriptionId)/resourceGroups/$($ResourceGroupName)/providers/Microsoft.Web/sites/$($Name)/config/appsettings?api-version=2024-04-01"
        $Payload = @{ properties = $AppSetting } | ConvertTo-Json -Depth 5
        $Response = Invoke-AzRestMethod -Method PUT -Path $Path -Payload $Payload
        if ($Response.StatusCode -ne 200) {
            throw "Could not update the app settings for '$($Name)' (HTTP $($Response.StatusCode))."
        }
    }

    function Resolve-CloudImagingNaming {
        param (
            [Parameter(Mandatory = $true, HelpMessage = "Resource group to resolve names from.")]
            [ValidateNotNullOrEmpty()]
            [string] $ResourceGroupName
        )
        # Every resource name follows the convention in main.bicep: '{prefix}-{env}-ci-{type}',
        # and '{prefix}{env}ci{purpose}' for storage accounts. Resolving the Device Gateway
        # Function App therefore yields the naming prefix for the whole deployment, so nothing
        # has to be guessed or passed in.
        $AnchorApps = @(
            Get-AzWebApp -ResourceGroupName $ResourceGroupName -ErrorAction SilentlyContinue |
                Where-Object { $PSItem.Name -like "*-ci-func-gateway" }
        )

        if ($AnchorApps.Count -eq 0) {
            throw "No Cloud Imaging deployment was found in '$($ResourceGroupName)'. Expected a Function App named '<prefix>-<environment>-ci-func-gateway'. Check the resource group name and the selected subscription."
        }

        if ($AnchorApps.Count -gt 1) {
            throw "Found $($AnchorApps.Count) Cloud Imaging deployments in '$($ResourceGroupName)': $($AnchorApps.Name -join ', '). Deploy each environment into its own resource group."
        }

        $NamePrefix = $AnchorApps[0].Name -replace "-func-gateway$", ""
        $StoragePrefix = $NamePrefix -replace "-", ""

        return [PSCustomObject]@{
            NamePrefix = $NamePrefix
            DeviceGatewayApi = "$($NamePrefix)-func-gateway"
            OperatorApi = "$($NamePrefix)-func-operator"
            ImagingCoreApi = "$($NamePrefix)-func-core"
            PortalBackend = "$($NamePrefix)-app-portal"
            PortalFrontend = "$($NamePrefix)-stapp-portal"
            StorageApp = "$($StoragePrefix)stapp"
            StorageCore = "$($StoragePrefix)stcore"
        }
    }
}
Process {
    try {
        Write-Output "Cloud Imaging upgrade"

        # Written into the bundle by the release workflow, so the operator can see which release
        # is being deployed without inspecting the packages.
        $VersionFile = Join-Path -Path $PackageRoot -ChildPath "version.txt"
        if (Test-Path -Path $VersionFile) {
            Write-Output "Release: $((Get-Content -Path $VersionFile -Raw).Trim())"
        }

        Write-Output "Resource group: $($ResourceGroupName)"
        Write-Output ""

        Write-Output "Checking prerequisites"

        if ($null -eq (Get-Command -Name "swa" -ErrorAction SilentlyContinue)) {
            throw "The Azure Static Web Apps CLI was not found on PATH. It is required to publish the portal frontend. Install Node.js, then run: npm install -g @azure/static-web-apps-cli"
        }

        $AzContext = Get-AzContext -ErrorAction SilentlyContinue
        if ($null -eq $AzContext) {
            Write-Output "No Azure session found, a browser sign-in will open"
            Connect-AzAccount -SubscriptionId $SubscriptionId | Out-Null
            $AzContext = Get-AzContext
        }

        if ($AzContext.Subscription.Id -ne $SubscriptionId) {
            Write-Output "Selecting subscription $($SubscriptionId)"
            $AzContext = (Set-AzContext -SubscriptionId $SubscriptionId).Context
        }

        if ($AzContext.Subscription.Id -ne $SubscriptionId) {
            throw "The signed-in account does not have access to subscription '$($SubscriptionId)'. Sign in with an account that does, then run this script again."
        }

        foreach ($PackageFileName in $RequiredPackages) {
            if (-not (Test-Path -Path (Join-Path -Path $PackageRoot -ChildPath $PackageFileName))) {
                throw "Component package '$($PackageFileName)' was not found in '$($PackageRoot)'. Run this script from the folder you extracted the release bundle into, with the component packages alongside it."
            }
        }

        Write-Output "Signed in as $($AzContext.Account.Id)"
        Write-Output "Subscription: $($AzContext.Subscription.Name) ($($AzContext.Subscription.Id))"
        Write-Output ""

        Write-Output "Resolving deployed resources"

        if ($null -eq (Get-AzResourceGroup -Name $ResourceGroupName -ErrorAction SilentlyContinue)) {
            throw "Resource group '$($ResourceGroupName)' was not found in subscription '$($AzContext.Subscription.Name)'."
        }

        $Naming = Resolve-CloudImagingNaming -ResourceGroupName $ResourceGroupName
        Write-Output "Deployment name prefix: $($Naming.NamePrefix)"

        $FunctionApps = @($WebApps | Where-Object { $PSItem.Kind -like "*functionapp*" })
        $WebApps = @(Get-AzWebApp -ResourceGroupName $ResourceGroupName)
        $StaticWebApps = @(Get-AzStaticWebApp -ResourceGroupName $ResourceGroupName)
        $StorageAccounts = @(Get-AzStorageAccount -ResourceGroupName $ResourceGroupName)

        $PortalBackend = $WebApps | Where-Object { $PSItem.Name -eq $Naming.PortalBackend }
        $PortalFrontend = $StaticWebApps | Where-Object { $PSItem.Name -eq $Naming.PortalFrontend }
        $StorageApp = $StorageAccounts | Where-Object { $PSItem.StorageAccountName -eq $Naming.StorageApp }
        $StorageCore = $StorageAccounts | Where-Object { $PSItem.StorageAccountName -eq $Naming.StorageCore }

        $ComponentDefinitions = @(
            [PSCustomObject]@{
                Name = "Device Gateway API"
                PackageFileName = "DeviceGatewayApi.zip"
                ResourceName = $Naming.DeviceGatewayApi
                StorageAccount = $StorageApp
            }
            [PSCustomObject]@{
                Name = "Operator API"
                PackageFileName = "OperatorApi.zip"
                ResourceName = $Naming.OperatorApi
                StorageAccount = $StorageApp
            }
            [PSCustomObject]@{
                Name = "Imaging Core API"
                PackageFileName = "ImagingCoreApi.zip"
                ResourceName = $Naming.ImagingCoreApi
                StorageAccount = $StorageCore
            }
        )

        foreach ($ComponentDefinition in $ComponentDefinitions) {
            $FunctionAppComponents.Add($ComponentDefinition)
        }

        # Package uploads authenticate with the account key rather than a data plane role
        # assignment, because a freshly granted role can take minutes to propagate. The apps
        # themselves still read the package keylessly with their own managed identity.
        $StorageContexts = @{}
        foreach ($StorageAccount in @($StorageApp, $StorageCore)) {
            if ($null -ne $StorageAccount) {
                $AccountKey = Get-AzStorageAccountKey -ResourceGroupName $ResourceGroupName -Name $StorageAccount.StorageAccountName | Select-Object -First 1
                $StorageContexts[$StorageAccount.StorageAccountName] = New-AzStorageContext -StorageAccountName $StorageAccount.StorageAccountName -StorageAccountKey $AccountKey.Value
            }
        }

        Write-Output ""
        Write-Output "Upgrading components"

        # Each component is handled on its own. A missing resource is skipped rather than fatal,
        # so one broken component never blocks the rest of the upgrade.
        foreach ($Component in $FunctionAppComponents) {
            $CurrentStepCount++
            Write-Progress -Activity "Upgrading Cloud Imaging" -Status "$($Component.Name) ($($Component.ResourceName))" -PercentComplete (($CurrentStepCount / $TotalStepCount) * 100)

            try {
                $MatchedApp = $FunctionApps | Where-Object { $PSItem.Name -eq $Component.ResourceName }
                if ($null -eq $MatchedApp -or $null -eq $Component.StorageAccount) {
                    Write-Warning -Message "$($Component.Name) was not found in the resource group, skipping it."
                    Add-UpgradeResult -Component $Component.Name -Action "Run from package" -Status "Skipped" -Detail "Resource not found"
                    continue
                }

                $StorageContext = $StorageContexts[$Component.StorageAccount.StorageAccountName]
                $BlobName = "$($Component.ResourceName)-$($Timestamp).zip"
                $PackageUri = "$($Component.StorageAccount.PrimaryEndpoints.Blob)$($ContainerName)/$($BlobName)"
                $PackagePath = Join-Path -Path $PackageRoot -ChildPath $Component.PackageFileName

                if ($PSCmdlet.ShouldProcess($Component.ResourceName, "Upgrade from $($Component.PackageFileName)")) {
                    Write-Output "  $($Component.Name) ($($Component.ResourceName))"

                    if ($null -eq (Get-AzStorageContainer -Name $ContainerName -Context $StorageContext -ErrorAction SilentlyContinue)) {
                        New-AzStorageContainer -Name $ContainerName -Context $StorageContext -Permission Off | Out-Null
                    }

                    # Blob names carry a timestamp, so the previous package stays in place and the
                    # app keeps serving from it until the restart below picks up the new one.
                    Write-Output "    Uploading $($Component.PackageFileName) ($([math]::Round((Get-Item $PackagePath).Length / 1MB, 1)) MB) to $($Component.StorageAccount.StorageAccountName)"
                    Set-AzStorageBlobContent -File $PackagePath -Container $ContainerName -Blob $BlobName -Context $StorageContext -Force | Out-Null

                    Write-Output "    Pointing the app at the package"
                    $AppSetting = Get-WebAppSetting -ResourceGroupName $ResourceGroupName -Name $Component.ResourceName
                    $AppSetting["WEBSITE_RUN_FROM_PACKAGE"] = $PackageUri
                    Set-WebAppSetting -ResourceGroupName $ResourceGroupName -Name $Component.ResourceName -AppSetting $AppSetting

                    Write-Output "    Restarting"
                    Restart-AzWebApp -ResourceGroupName $ResourceGroupName -Name $Component.ResourceName | Out-Null

                    Add-UpgradeResult -Component $Component.Name -Action "Run from package" -Status "Success" -Detail $Component.ResourceName
                }
            }
            catch [System.Exception] {
                $FailureCount++
                Write-Warning -Message "Failed to upgrade $($Component.Name): $($_.Exception.Message)"
                Add-UpgradeResult -Component $Component.Name -Action "Run from package" -Status "Failed" -Detail $_.Exception.Message
            }
        }

        # The portal backend is a Linux App Service, so it takes an ordinary zip deploy rather
        # than run-from-package.
        $CurrentStepCount++
        Write-Progress -Activity "Upgrading Cloud Imaging" -Status "Portal backend" -PercentComplete (($CurrentStepCount / $TotalStepCount) * 100)

        try {
            if ($null -eq $PortalBackend) {
                Write-Warning -Message "The portal backend App Service was not found, skipping it."
                Add-UpgradeResult -Component "Portal backend" -Action "Zip deploy" -Status "Skipped" -Detail "Resource not found"
            }
            elseif ($PSCmdlet.ShouldProcess($PortalBackend.Name, "Upgrade from portal-backend.zip")) {
                $BackendPackage = Join-Path -Path $PackageRoot -ChildPath "portal-backend.zip"
                Write-Output "  Portal backend ($($PortalBackend.Name))"
                Write-Output "    Deploying portal-backend.zip ($([math]::Round((Get-Item $BackendPackage).Length / 1MB, 1)) MB), this takes a few minutes"
                Publish-AzWebApp -ResourceGroupName $ResourceGroupName -Name $PortalBackend.Name -ArchivePath $BackendPackage -Force | Out-Null

                Add-UpgradeResult -Component "Portal backend" -Action "Zip deploy" -Status "Success" -Detail $PortalBackend.Name
            }
        }
        catch [System.Exception] {
            $FailureCount++
            Write-Warning -Message "Failed to upgrade the portal backend: $($_.Exception.Message)"
            Add-UpgradeResult -Component "Portal backend" -Action "Zip deploy" -Status "Failed" -Detail $_.Exception.Message
        }

        # Static Web Apps content can only be published with the Static Web Apps CLI, using a
        # deployment token read from the resource itself.
        $CurrentStepCount++
        Write-Progress -Activity "Upgrading Cloud Imaging" -Status "Portal frontend" -PercentComplete (($CurrentStepCount / $TotalStepCount) * 100)

        $FrontendDirectory = Join-Path -Path $env:TEMP -ChildPath "cloudimaging-frontend-$($Timestamp)"
        $PreviousNodeOptions = $env:NODE_OPTIONS
        try {
            if ($null -eq $PortalFrontend) {
                Write-Warning -Message "The Static Web App was not found, skipping the portal frontend."
                Add-UpgradeResult -Component "Portal frontend" -Action "Static Web Apps deploy" -Status "Skipped" -Detail "Resource not found"
            }
            elseif ($PSCmdlet.ShouldProcess($PortalFrontend.Name, "Upgrade from portal-frontend.zip")) {
                # swa deploy takes a directory of built assets, not an archive.
                $FrontendPackage = Join-Path -Path $PackageRoot -ChildPath "portal-frontend.zip"
                Write-Output "  Portal frontend ($($PortalFrontend.Name))"
                Write-Output "    Expanding portal-frontend.zip"
                Expand-Archive -Path $FrontendPackage -DestinationPath $FrontendDirectory -Force

                $SecretsPath = "/subscriptions/$($AzContext.Subscription.Id)/resourceGroups/$($ResourceGroupName)/providers/Microsoft.Web/staticSites/$($PortalFrontend.Name)/listSecrets?api-version=2023-01-01"
                $SecretsResponse = Invoke-AzRestMethod -Method POST -Path $SecretsPath
                if ($SecretsResponse.StatusCode -ne 200) {
                    throw "Could not read the Static Web App deployment token (HTTP $($SecretsResponse.StatusCode))."
                }

                $DeploymentToken = ($SecretsResponse.Content | ConvertFrom-Json).properties.apiKey
                Write-Output "    Publishing to the Static Web App"

                # Node prints a punycode deprecation warning the CLI cannot avoid, and the CLI
                # itself hints about api-language defaults unless they are stated. There is no
                # managed API here, so neither message is actionable.
                $env:NODE_OPTIONS = "--no-deprecation"
                swa deploy $FrontendDirectory --deployment-token $DeploymentToken --env production --api-language node --api-version 22
                if ($LASTEXITCODE -ne 0) {
                    throw "swa deploy exited with code $($LASTEXITCODE)."
                }

                Add-UpgradeResult -Component "Portal frontend" -Action "Static Web Apps deploy" -Status "Success" -Detail $PortalFrontend.Name
            }
        }
        catch [System.Exception] {
            $FailureCount++
            Write-Warning -Message "Failed to upgrade the portal frontend: $($_.Exception.Message)"
            Add-UpgradeResult -Component "Portal frontend" -Action "Static Web Apps deploy" -Status "Failed" -Detail $_.Exception.Message
        }
        finally {
            $env:NODE_OPTIONS = $PreviousNodeOptions
            if (Test-Path -Path $FrontendDirectory) {
                Remove-Item -Path $FrontendDirectory -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        # The portal uploads OS images straight from the browser to blob storage, so the blob
        # service has to answer the CORS preflight for the portal origin. The Template Spec sets
        # this, but an upgrade never re-runs it, so re-apply the rule if it went missing.
        $CurrentStepCount++
        Write-Progress -Activity "Upgrading Cloud Imaging" -Status "Boot image storage CORS" -PercentComplete (($CurrentStepCount / $TotalStepCount) * 100)

        try {
            if ($null -eq $PortalFrontend -or $null -eq $StorageCore) {
                Add-UpgradeResult -Component "Boot image storage" -Action "Verify CORS rule" -Status "Skipped" -Detail "Resource not found"
            }
            else {
                $PortalOrigin = "https://$($PortalFrontend.DefaultHostname)"
                $StorageContext = $StorageContexts[$StorageCore.StorageAccountName]
                $CorsRules = @(Get-AzStorageCORSRule -ServiceType Blob -Context $StorageContext)
                $HasRule = $CorsRules | Where-Object { $PSItem.AllowedOrigins -contains $PortalOrigin }

                if ($null -ne $HasRule) {
                    Add-UpgradeResult -Component "Boot image storage" -Action "Verify CORS rule" -Status "Success" -Detail "Already allows $($PortalOrigin)"
                }
                elseif ($PSCmdlet.ShouldProcess($StorageCore.StorageAccountName, "Add blob CORS rule for $($PortalOrigin)")) {
                    # Set-AzStorageCORSRule replaces the whole rule set, so the existing rules are
                    # carried over rather than dropped.
                    $UpdatedRules = New-Object -TypeName "System.Collections.Generic.List[System.Object]"
                    foreach ($CorsRule in $CorsRules) {
                        $UpdatedRules.Add($CorsRule)
                    }
                    $UpdatedRules.Add(@{
                        AllowedOrigins = @($PortalOrigin)
                        AllowedMethods = @("Get", "Head", "Put", "Options")
                        AllowedHeaders = @("content-type", "x-ms-blob-type", "x-ms-version", "x-ms-date")
                        ExposedHeaders = @("*")
                        MaxAgeInSeconds = 3600
                    })

                    Set-AzStorageCORSRule -ServiceType Blob -CorsRules $UpdatedRules.ToArray() -Context $StorageContext
                    Add-UpgradeResult -Component "Boot image storage" -Action "Add CORS rule" -Status "Success" -Detail $PortalOrigin
                }
            }
        }
        catch [System.Exception] {
            $FailureCount++
            Write-Warning -Message "Could not verify the boot image storage CORS rule: $($_.Exception.Message)"
            Add-UpgradeResult -Component "Boot image storage" -Action "Verify CORS rule" -Status "Failed" -Detail $_.Exception.Message
        }

        Write-Progress -Activity "Upgrading Cloud Imaging" -Completed

        if ($null -ne $PortalFrontend) {
            $PortalUrl = "https://$($PortalFrontend.DefaultHostname)"
        }
    }
    catch [System.Exception] {
        $FailureCount++
        Write-Warning -Message "Cloud Imaging upgrade stopped: $($_.Exception.Message)"
    }
}
End {
    # Runs whether the upgrade succeeded or failed, so no progress bar is left on screen.
    Write-Progress -Activity "Upgrading Cloud Imaging" -Completed

    Write-Output ""
    Write-Output "Upgrade summary"

    if ($UpgradeResults.Count -gt 0) {
        Write-Output ($UpgradeResults | Format-Table -AutoSize -Wrap | Out-String).TrimEnd()
    }

    Write-Output ""

    if ($FailureCount -gt 0) {
        Write-Warning -Message "$($FailureCount) step(s) did not complete. Resolve the errors above and run this script again, it is safe to re-run."
    }
    else {
        Write-Output "Upgrade complete."
        Write-Output "Verify the portal before notifying users: $($PortalUrl)"
    }
}
