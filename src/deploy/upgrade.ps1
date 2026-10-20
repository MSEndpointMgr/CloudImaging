<#
.SYNOPSIS
    Upgrades an existing Cloud Imaging deployment to the release this bundle contains.

.DESCRIPTION
    Run this from the folder you extracted a newer Cloud Imaging release bundle into. The
    component packages that ship alongside this script are deployed over the existing Azure
    resources. Nothing is downloaded, and infrastructure is never re-provisioned: to choose a
    version, download that release's bundle and run the copy of this script inside it.

    Components upgraded, in this order, so a caller is never upgraded ahead of the API it calls:
        Imaging Core API      ImagingCoreApi.zip      run from package
        Operator API          OperatorApi.zip         run from package
        Device Gateway API    DeviceGatewayApi.zip    run from package
        Portal backend        portal-backend.zip      zip deploy
        Portal frontend       portal-frontend.zip     Azure Static Web Apps CLI

    The boot image storage CORS rule for the portal origin is re-applied as well. A routine
    upgrade does not re-run the Bicep template that normally sets it, so this keeps browser
    uploads of OS images working if the rule was ever lost.

    Requirements:
        Az PowerShell module. The script signs in with Connect-AzAccount if no session exists
        Contributor on the resource group holding the deployment
        Azure Static Web Apps CLI, used to publish the portal frontend

    Every resource is resolved before anything changes, so a wrong resource group or an
    incomplete deployment stops the run with nothing touched. Components are then upgraded
    independently: if one fails the others still proceed. Afterwards each Function App is
    checked to be bound to its new package, and the portal backend has to report the bundle's
    release. The summary reports exactly what did and did not change. Safe to re-run.

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
    Updated:     2026-10-17

    Version history:
    1.0.0 - (2026-09-13) Replaces update.ps1. Packages now come from the bundle instead of
                         being downloaded, and all Azure calls use Az PowerShell
    1.1.0 - (2026-10-17) Fixed the Function Apps always being skipped, and -WhatIf failing to
                         sign in or select the subscription. All resources are now resolved
                         before anything changes, APIs upgrade dependency first, the SWA token
                         no longer appears on a command line, and the result is verified
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
    # -WhatIf:$false here and on the sign-in calls: under -WhatIf they would otherwise be skipped,
    # leaving no session or the wrong subscription for the dry run to inspect.
    Update-AzConfig -DisplayBreakingChangeWarning $false -DisplaySurveyMessage $false -CheckForUpgrade $false -Scope Process -WhatIf:$false | Out-Null

    $Timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $PackageRoot = $PSScriptRoot
    $ContainerName = "app-packages"
    $PortalUrl = ""
    $BundleVersion = ""

    $UpgradeResults = New-Object -TypeName "System.Collections.Generic.List[System.Object]"
    $FunctionAppComponents = New-Object -TypeName "System.Collections.Generic.List[System.Object]"
    $DeployedPackages = [ordered]@{}
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

    function Set-UpgradeResult {
        param (
            [Parameter(Mandatory = $true, HelpMessage = "Component the result applies to.")]
            [ValidateNotNullOrEmpty()]
            [string] $Component,
            [Parameter(Mandatory = $false, HelpMessage = "Action that was performed. Keeps the existing action when omitted.")]
            [string] $Action = "",
            [Parameter(Mandatory = $true, HelpMessage = "Outcome of the action.")]
            [ValidateSet("Success", "Failed")]
            [string] $Status,
            [Parameter(Mandatory = $false, HelpMessage = "Detail shown in the summary table. Keeps the existing detail when omitted.")]
            [string] $Detail = ""
        )
        # One row per component, so deployment and verification of the same component update a
        # single line instead of reporting it twice.
        $Existing = $UpgradeResults | Where-Object { $PSItem.Component -eq $Component } | Select-Object -First 1
        if ($null -eq $Existing) {
            $UpgradeResults.Add([PSCustomObject]@{
                Component = $Component
                Action = $Action
                Status = $Status
                Detail = $Detail
            })
            return
        }

        if (-not [string]::IsNullOrWhiteSpace($Action)) {
            $Existing.Action = $Action
        }
        if (-not [string]::IsNullOrWhiteSpace($Detail)) {
            $Existing.Detail = $Detail
        }
        $Existing.Status = $Status
    }

    function Test-HttpEndpoint {
        param (
            [Parameter(Mandatory = $true, HelpMessage = "Absolute URI to probe.")]
            [ValidateNotNullOrEmpty()]
            [string] $Uri,
            [Parameter(Mandatory = $false, HelpMessage = "Release the JSON response must report in its 'version' field. Any successful response passes when omitted.")]
            [string] $ExpectedVersion = "",
            [Parameter(Mandatory = $false, HelpMessage = "Attempts before giving up.")]
            [ValidateRange(1, 40)]
            [int] $Attempt = 12,
            [Parameter(Mandatory = $false, HelpMessage = "Seconds to wait between attempts.")]
            [ValidateRange(1, 120)]
            [int] $DelaySecond = 15
        )
        for ($Counter = 1; $Counter -le $Attempt; $Counter++) {
            try {
                $Response = Invoke-WebRequest -Uri $Uri -UseBasicParsing -TimeoutSec 30
                if ([string]::IsNullOrEmpty($ExpectedVersion)) {
                    return $true
                }

                # The previous build keeps answering until the restarted app takes over, so only
                # the new release tag proves the upgrade is live.
                $ReportedVersion = ($Response.Content | ConvertFrom-Json).version -replace "^mse-ci-", ""
                if ($ReportedVersion -eq $ExpectedVersion) {
                    return $true
                }
                Write-Verbose -Message "Probe $($Counter) of $($Attempt) reported release '$($ReportedVersion)'."
            }
            catch [System.Exception] {
                # A cold App Service can refuse or time out several times before it answers.
                Write-Verbose -Message "Probe $($Counter) of $($Attempt) failed: $($_.Exception.Message)"
            }

            # Information stream, not output: output would become part of the boolean returned.
            if ($Counter -lt $Attempt) {
                Write-Information -MessageData "    Not ready yet, retrying in $($DelaySecond) seconds (attempt $($Counter) of $($Attempt))" -InformationAction Continue
                Start-Sleep -Seconds $DelaySecond
            }
        }

        return $false
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
            $BundleVersion = (Get-Content -Path $VersionFile -Raw).Trim()
            Write-Output "Release: $($BundleVersion)"
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
            Connect-AzAccount -SubscriptionId $SubscriptionId -WhatIf:$false | Out-Null
            $AzContext = Get-AzContext
        }

        if ($AzContext.Subscription.Id -ne $SubscriptionId) {
            Write-Output "Selecting subscription $($SubscriptionId)"
            $AzContext = (Set-AzContext -SubscriptionId $SubscriptionId -WhatIf:$false).Context
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

        $WebApps = @(Get-AzWebApp -ResourceGroupName $ResourceGroupName)
        $FunctionApps = @($WebApps | Where-Object { $PSItem.Kind -like "*functionapp*" })
        $StaticWebApps = @(Get-AzStaticWebApp -ResourceGroupName $ResourceGroupName)
        $StorageAccounts = @(Get-AzStorageAccount -ResourceGroupName $ResourceGroupName)

        $PortalBackend = $WebApps | Where-Object { $PSItem.Name -eq $Naming.PortalBackend }
        $PortalFrontend = $StaticWebApps | Where-Object { $PSItem.Name -eq $Naming.PortalFrontend }
        $StorageApp = $StorageAccounts | Where-Object { $PSItem.StorageAccountName -eq $Naming.StorageApp }
        $StorageCore = $StorageAccounts | Where-Object { $PSItem.StorageAccountName -eq $Naming.StorageCore }

        # Dependency first: Operator and Device Gateway both call Imaging Core, so a new caller
        # never runs against an old API it may depend on.
        $ComponentDefinitions = @(
            [PSCustomObject]@{
                Name = "Imaging Core API"
                PackageFileName = "ImagingCoreApi.zip"
                ResourceName = $Naming.ImagingCoreApi
                StorageAccount = $StorageCore
            }
            [PSCustomObject]@{
                Name = "Operator API"
                PackageFileName = "OperatorApi.zip"
                ResourceName = $Naming.OperatorApi
                StorageAccount = $StorageApp
            }
            [PSCustomObject]@{
                Name = "Device Gateway API"
                PackageFileName = "DeviceGatewayApi.zip"
                ResourceName = $Naming.DeviceGatewayApi
                StorageAccount = $StorageApp
            }
        )

        # Everything is resolved before anything changes. Upgrading only part of a deployment
        # whose resources are missing would leave components on mismatched releases.
        $ExpectedResources = @(
            [PSCustomObject]@{
                Description = "Portal backend App Service"
                Name = $Naming.PortalBackend
                Resource = $PortalBackend
            }
            [PSCustomObject]@{
                Description = "Static Web App"
                Name = $Naming.PortalFrontend
                Resource = $PortalFrontend
            }
            [PSCustomObject]@{
                Description = "Package storage account"
                Name = $Naming.StorageApp
                Resource = $StorageApp
            }
            [PSCustomObject]@{
                Description = "Package storage account"
                Name = $Naming.StorageCore
                Resource = $StorageCore
            }
        )

        foreach ($ExpectedResource in $ExpectedResources) {
            if ($null -eq $ExpectedResource.Resource) {
                throw "$($ExpectedResource.Description) '$($ExpectedResource.Name)' was not found in '$($ResourceGroupName)'. Nothing was changed."
            }
        }

        foreach ($ComponentDefinition in $ComponentDefinitions) {
            if ($null -eq ($FunctionApps | Where-Object { $PSItem.Name -eq $ComponentDefinition.ResourceName })) {
                throw "Function App '$($ComponentDefinition.ResourceName)' was not found in '$($ResourceGroupName)'. Nothing was changed."
            }

            $FunctionAppComponents.Add($ComponentDefinition)
        }

        Write-Output "Resolved all five components and both package storage accounts"

        # Shown so the operator knows which bundle to keep for a rollback. The portal backend
        # reports the release stamped into its build on this anonymous endpoint.
        try {
            $DeployedConfig = Invoke-RestMethod -Uri "https://$($PortalBackend.DefaultHostName)/api/config" -UseBasicParsing -TimeoutSec 30
            $DeployedVersion = "$($DeployedConfig.version)" -replace "^mse-ci-", ""
            if ([string]::IsNullOrWhiteSpace($DeployedVersion)) {
                $DeployedVersion = "unknown, it predates release reporting"
            }
            Write-Output "Currently deployed release: $($DeployedVersion)"
        }
        catch [System.Exception] {
            Write-Output "Currently deployed release: unknown, the portal backend did not respond"
        }

        # Package uploads authenticate with the account key rather than a data plane role
        # assignment, because a freshly granted role can take minutes to propagate. The apps
        # themselves still read the package keylessly with their own managed identity.
        $StorageContexts = @{}
        foreach ($StorageAccount in @($StorageApp, $StorageCore)) {
            $AccountKey = Get-AzStorageAccountKey -ResourceGroupName $ResourceGroupName -Name $StorageAccount.StorageAccountName | Select-Object -First 1
            $StorageContexts[$StorageAccount.StorageAccountName] = New-AzStorageContext -StorageAccountName $StorageAccount.StorageAccountName -StorageAccountKey $AccountKey.Value
        }

        Write-Output ""
        Write-Output "Upgrading components"

        # Each component is handled on its own, so one failure never blocks the rest.
        foreach ($Component in $FunctionAppComponents) {
            $CurrentStepCount++
            Write-Progress -Activity "Upgrading Cloud Imaging" -Status "$($Component.Name) ($($Component.ResourceName))" -PercentComplete (($CurrentStepCount / $TotalStepCount) * 100)

            try {
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

                    $DeployedPackages[$Component.Name] = $PackageUri
                    Set-UpgradeResult -Component $Component.Name -Action "Run from package" -Status "Success" -Detail $Component.ResourceName
                }
            }
            catch [System.Exception] {
                $FailureCount++
                Write-Warning -Message "Failed to upgrade $($Component.Name): $($_.Exception.Message)"
                Set-UpgradeResult -Component $Component.Name -Action "Run from package" -Status "Failed" -Detail $_.Exception.Message
            }
        }

        # The portal backend is a Linux App Service, so it takes an ordinary zip deploy rather
        # than run-from-package.
        $CurrentStepCount++
        Write-Progress -Activity "Upgrading Cloud Imaging" -Status "Portal backend ($($PortalBackend.Name))" -PercentComplete (($CurrentStepCount / $TotalStepCount) * 100)

        try {
            if ($PSCmdlet.ShouldProcess($PortalBackend.Name, "Upgrade from portal-backend.zip")) {
                $BackendPackage = Join-Path -Path $PackageRoot -ChildPath "portal-backend.zip"
                Write-Output "  Portal backend ($($PortalBackend.Name))"
                Write-Output "    Deploying portal-backend.zip ($([math]::Round((Get-Item $BackendPackage).Length / 1MB, 1)) MB), this takes a few minutes"
                Publish-AzWebApp -ResourceGroupName $ResourceGroupName -Name $PortalBackend.Name -ArchivePath $BackendPackage -Force | Out-Null

                Set-UpgradeResult -Component "Portal backend" -Action "Zip deploy" -Status "Success" -Detail $PortalBackend.Name
            }
        }
        catch [System.Exception] {
            $FailureCount++
            Write-Warning -Message "Failed to upgrade the portal backend: $($_.Exception.Message)"
            Set-UpgradeResult -Component "Portal backend" -Action "Zip deploy" -Status "Failed" -Detail $_.Exception.Message
        }

        # Static Web Apps content can only be published with the Static Web Apps CLI, using a
        # deployment token read from the resource itself.
        $CurrentStepCount++
        Write-Progress -Activity "Upgrading Cloud Imaging" -Status "Portal frontend ($($PortalFrontend.Name))" -PercentComplete (($CurrentStepCount / $TotalStepCount) * 100)

        $FrontendDirectory = Join-Path -Path $env:TEMP -ChildPath "cloudimaging-frontend-$($Timestamp)"
        $PreviousNodeOptions = $env:NODE_OPTIONS
        try {
            if ($PSCmdlet.ShouldProcess($PortalFrontend.Name, "Upgrade from portal-frontend.zip")) {
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
                # managed API here, so neither message is actionable. The token goes through the
                # environment so it never shows up in the process list.
                $env:NODE_OPTIONS = "--no-deprecation"
                $env:SWA_CLI_DEPLOYMENT_TOKEN = $DeploymentToken
                swa deploy $FrontendDirectory --env production --api-language node --api-version 22
                if ($LASTEXITCODE -ne 0) {
                    throw "swa deploy exited with code $($LASTEXITCODE)."
                }

                Set-UpgradeResult -Component "Portal frontend" -Action "Static Web Apps deploy" -Status "Success" -Detail $PortalFrontend.Name
            }
        }
        catch [System.Exception] {
            $FailureCount++
            Write-Warning -Message "Failed to upgrade the portal frontend: $($_.Exception.Message)"
            Set-UpgradeResult -Component "Portal frontend" -Action "Static Web Apps deploy" -Status "Failed" -Detail $_.Exception.Message
        }
        finally {
            $env:NODE_OPTIONS = $PreviousNodeOptions
            Remove-Item -Path "Env:\SWA_CLI_DEPLOYMENT_TOKEN" -ErrorAction SilentlyContinue -WhatIf:$false
            if (Test-Path -Path $FrontendDirectory) {
                Remove-Item -Path $FrontendDirectory -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        # The portal uploads OS images straight from the browser to blob storage, so the blob
        # service has to answer the CORS preflight for the portal origin. The Template Spec sets
        # this, but an upgrade never re-runs it, so re-apply the rule if it went missing.
        $CurrentStepCount++
        Write-Progress -Activity "Upgrading Cloud Imaging" -Status "Boot image storage CORS" -PercentComplete (($CurrentStepCount / $TotalStepCount) * 100)

        $PortalUrl = "https://$($PortalFrontend.DefaultHostname)"
        try {
            $StorageContext = $StorageContexts[$StorageCore.StorageAccountName]
            $CorsRules = @(Get-AzStorageCORSRule -ServiceType Blob -Context $StorageContext)
            $HasRule = $CorsRules | Where-Object { $PSItem.AllowedOrigins -contains $PortalUrl }

            if ($null -ne $HasRule) {
                Set-UpgradeResult -Component "Boot image storage" -Action "Verify CORS rule" -Status "Success" -Detail "Already allows $($PortalUrl)"
            }
            elseif ($PSCmdlet.ShouldProcess($StorageCore.StorageAccountName, "Add blob CORS rule for $($PortalUrl)")) {
                # Set-AzStorageCORSRule replaces the whole rule set, so the existing rules are
                # carried over rather than dropped.
                $UpdatedRules = New-Object -TypeName "System.Collections.Generic.List[System.Object]"
                foreach ($CorsRule in $CorsRules) {
                    $UpdatedRules.Add($CorsRule)
                }
                $UpdatedRules.Add(@{
                    AllowedOrigins = @($PortalUrl)
                    AllowedMethods = @("Get", "Head", "Put", "Options")
                    AllowedHeaders = @("content-type", "x-ms-blob-type", "x-ms-version", "x-ms-date")
                    ExposedHeaders = @("*")
                    MaxAgeInSeconds = 3600
                })

                Set-AzStorageCORSRule -ServiceType Blob -CorsRules $UpdatedRules.ToArray() -Context $StorageContext
                Set-UpgradeResult -Component "Boot image storage" -Action "Add CORS rule" -Status "Success" -Detail $PortalUrl
            }
        }
        catch [System.Exception] {
            $FailureCount++
            Write-Warning -Message "Could not verify the boot image storage CORS rule: $($_.Exception.Message)"
            Set-UpgradeResult -Component "Boot image storage" -Action "Verify CORS rule" -Status "Failed" -Detail $_.Exception.Message
        }

        Write-Progress -Activity "Upgrading Cloud Imaging" -Completed

        # A dry run deployed nothing, so there is nothing to verify.
        if (-not $WhatIfPreference) {
            Write-Output ""
            Write-Output "Verifying the upgrade"

            # Read back from Azure rather than trusting the write above, which only proves the
            # request was accepted.
            foreach ($ComponentName in $DeployedPackages.Keys) {
                $Component = $FunctionAppComponents | Where-Object { $PSItem.Name -eq $ComponentName }
                try {
                    $AppSetting = Get-WebAppSetting -ResourceGroupName $ResourceGroupName -Name $Component.ResourceName
                    if ($AppSetting["WEBSITE_RUN_FROM_PACKAGE"] -eq $DeployedPackages[$ComponentName]) {
                        Write-Output "  $($ComponentName): running from the new package"
                        continue
                    }

                    $FailureCount++
                    Write-Output "  $($ComponentName): not bound to the new package"
                    Set-UpgradeResult -Component $ComponentName -Status "Failed" -Detail "Not bound to the new package after deployment"
                }
                catch [System.Exception] {
                    $FailureCount++
                    Write-Output "  $($ComponentName): could not be verified"
                    Set-UpgradeResult -Component $ComponentName -Status "Failed" -Detail $_.Exception.Message
                }
            }

            # Only the release reported by the running backend proves the new build started, as
            # the previous one keeps answering while the deployment settles.
            $EndpointChecks = New-Object -TypeName "System.Collections.Generic.List[System.Object]"
            if (($UpgradeResults | Where-Object { $PSItem.Component -eq "Portal backend" }).Status -eq "Success") {
                $EndpointChecks.Add([PSCustomObject]@{
                    Component = "Portal backend"
                    Uri = "https://$($PortalBackend.DefaultHostName)/api/config"
                    ExpectedVersion = $BundleVersion
                })
            }
            if (($UpgradeResults | Where-Object { $PSItem.Component -eq "Portal frontend" }).Status -eq "Success") {
                $EndpointChecks.Add([PSCustomObject]@{
                    Component = "Portal frontend"
                    Uri = $PortalUrl
                    ExpectedVersion = ""
                })
            }

            foreach ($EndpointCheck in $EndpointChecks) {
                Write-Output "  $($EndpointCheck.Component): waiting for $($EndpointCheck.Uri)"
                if (Test-HttpEndpoint -Uri $EndpointCheck.Uri -ExpectedVersion $EndpointCheck.ExpectedVersion) {
                    Write-Output "  $($EndpointCheck.Component): responded"
                    continue
                }

                $FailureCount++
                Write-Output "  $($EndpointCheck.Component): no response from the new release"
                Set-UpgradeResult -Component $EndpointCheck.Component -Status "Failed" -Detail "No response from the new release at $($EndpointCheck.Uri)"
            }
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
    elseif ($WhatIfPreference) {
        Write-Output "Dry run complete. Nothing was changed."
    }
    else {
        Write-Output "Upgrade complete and verified."
        Write-Output "Sign in to the portal before notifying users: $($PortalUrl)"
    }
}
