<#
.SYNOPSIS
    Installs the Cloud Imaging application packages into a newly provisioned Azure environment.

.DESCRIPTION
    Run this immediately after the Cloud Imaging Template Spec deployment completes. The
    Template Spec provisions the Azure resources but leaves them empty, and this script
    installs the application code into them.

    Only the component packages that shipped in this release bundle are used, and they are
    expected to sit in the same folder as this script. Nothing is downloaded. Each Function
    App package is uploaded to the deployment's own storage account and read back by the
    app's managed identity, so the running solution never depends on a source outside the
    subscription.

    Components installed:
        Device Gateway API    DeviceGatewayApi.zip    run from package
        Operator API          OperatorApi.zip         run from package
        Imaging Core API      ImagingCoreApi.zip      run from package
        Portal backend        portal-backend.zip      zip deploy
        Portal frontend       portal-frontend.zip     Azure Static Web Apps CLI

    Resource names are resolved from the naming convention the Template Spec deploys, so no
    resource names have to be supplied.

    Requirements:
        Az PowerShell module. The script signs in with Connect-AzAccount if no session exists
        Contributor on the target resource group
        Azure Static Web Apps CLI, used to publish the portal frontend

    Prerequisites are checked before any deployment work starts, and the script is safe to
    re-run.

.PARAMETER ResourceGroupName
    Name of the resource group holding the Cloud Imaging resources.

.PARAMETER SubscriptionId
    Subscription holding the resource group. Required, so the installation can never run
    against whichever subscription the session happened to default to.

.EXAMPLE
    .\install.ps1 -ResourceGroupName "corp-prod-rg" -SubscriptionId "<subscription-id>"

.NOTES
    FileName:    install.ps1
    Author:      MSEndpointMgr
    Contact:     @MSEndpointMgr
    Created:     2026-09-13
    Updated:     2026-09-13

    Version history:
    1.0.0 - (2026-09-13) Initial release
#>
#Requires -Modules Az.Accounts, Az.Resources, Az.Storage, Az.Websites
[CmdletBinding(SupportsShouldProcess)]
param (
    [Parameter(Mandatory = $true, HelpMessage = "Resource group holding the Cloud Imaging resources.")]
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

    $InstallResults = New-Object -TypeName "System.Collections.Generic.List[System.Object]"
    $FunctionAppComponents = New-Object -TypeName "System.Collections.Generic.List[System.Object]"
    $CurrentStepCount = 0
    $FailureCount = 0
    $TotalStepCount = 5

    $RequiredPackages = @(
        "DeviceGatewayApi.zip"
        "OperatorApi.zip"
        "ImagingCoreApi.zip"
        "portal-backend.zip"
        "portal-frontend.zip"
    )

    function Set-InstallResult {
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
        $Existing = $InstallResults | Where-Object { $PSItem.Component -eq $Component } | Select-Object -First 1
        if ($null -eq $Existing) {
            $InstallResults.Add([PSCustomObject]@{
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
            throw "No Cloud Imaging deployment was found in '$($ResourceGroupName)'. Expected a Function App named '<prefix>-<environment>-ci-func-gateway'. Check the resource group name, and that the Template Spec deployment completed successfully."
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

    function Test-HttpEndpoint {
        param (
            [Parameter(Mandatory = $true, HelpMessage = "Absolute URI to probe.")]
            [ValidateNotNullOrEmpty()]
            [string] $Uri,
            [Parameter(Mandatory = $false, HelpMessage = "Attempts before giving up.")]
            [ValidateRange(1, 20)]
            [int] $Attempt = 6,
            [Parameter(Mandatory = $false, HelpMessage = "Seconds to wait between attempts.")]
            [ValidateRange(1, 120)]
            [int] $DelaySecond = 15
        )
        for ($Counter = 1; $Counter -le $Attempt; $Counter++) {
            try {
                $Response = Invoke-WebRequest -Uri $Uri -UseBasicParsing -TimeoutSec 30
                if ($Response.StatusCode -ge 200 -and $Response.StatusCode -lt 400) {
                    return $true
                }
            }
            catch [System.Exception] {
                # A cold App Service can refuse or time out several times before it answers.
                Write-Verbose -Message "Probe $($Counter) of $($Attempt) failed: $($_.Exception.Message)"
            }

            # Written out rather than reported through Write-Progress, which renders nothing in
            # some hosts and leaves the operator watching a still screen for minutes.
            if ($Counter -lt $Attempt) {
                Write-Output "    No response yet, retrying in $($DelaySecond) seconds (attempt $($Counter) of $($Attempt))"
                Start-Sleep -Seconds $DelaySecond
            }
        }

        return $false
    }
}
Process {
    try {
        Write-Output "Cloud Imaging installation"

        # Written into the bundle by the release workflow, so the operator can see which release
        # is being installed without inspecting the packages.
        $VersionFile = Join-Path -Path $PackageRoot -ChildPath "version.txt"
        if (Test-Path -Path $VersionFile) {
            Write-Output "Release: $((Get-Content -Path $VersionFile -Raw).Trim())"
        }

        Write-Output "Resource group: $($ResourceGroupName)"
        Write-Output ""

        # Checked up front so a missing prerequisite never leaves the environment half installed.
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

        # Confirms the account can actually reach the subscription that was asked for, rather
        # than silently continuing in whichever one the session defaulted to.
        if ($AzContext.Subscription.Id -ne $SubscriptionId) {
            throw "The signed-in account does not have access to subscription '$($SubscriptionId)'. Sign in with an account that does, then run this script again."
        }

        # All five packages have to be present before anything is deployed, so a truncated or
        # wrongly extracted bundle fails here instead of halfway through the install.
        foreach ($PackageFileName in $RequiredPackages) {
            if (-not (Test-Path -Path (Join-Path -Path $PackageRoot -ChildPath $PackageFileName))) {
                throw "Component package '$($PackageFileName)' was not found in '$($PackageRoot)'. Run this script from the folder you extracted the release bundle into, with the component packages alongside it."
            }
        }

        Write-Output "Signed in as $($AzContext.Account.Id)"
        Write-Output "Subscription: $($AzContext.Subscription.Name) ($($AzContext.Subscription.Id))"
        Write-Output ""

        # An install fails hard on anything missing, because it means the Template Spec
        # deployment did not complete and installing code on top would mask that.
        Write-Output "Resolving deployed resources"

        if ($null -eq (Get-AzResourceGroup -Name $ResourceGroupName -ErrorAction SilentlyContinue)) {
            throw "Resource group '$($ResourceGroupName)' was not found in subscription '$($AzContext.Subscription.Name)'."
        }

        # Resolve the naming prefix once, then derive every resource name from it.
        $Naming = Resolve-CloudImagingNaming -ResourceGroupName $ResourceGroupName
        Write-Output "Deployment name prefix: $($Naming.NamePrefix)"

        # One list call per resource type, so the matching below costs no further API calls.
        $WebApps = @(Get-AzWebApp -ResourceGroupName $ResourceGroupName)
        $FunctionApps = @($WebApps | Where-Object { $PSItem.Kind -like "*functionapp*" })
        $StaticWebApps = @(Get-AzStaticWebApp -ResourceGroupName $ResourceGroupName)
        $StorageAccounts = @(Get-AzStorageAccount -ResourceGroupName $ResourceGroupName)

        # Match on the exact derived names rather than a wildcard, so an unrelated workload in
        # the same resource group can never be picked up and deployed over.
        $PortalBackend = $WebApps | Where-Object { $PSItem.Name -eq $Naming.PortalBackend }
        $PortalFrontend = $StaticWebApps | Where-Object { $PSItem.Name -eq $Naming.PortalFrontend }
        $StorageApp = $StorageAccounts | Where-Object { $PSItem.StorageAccountName -eq $Naming.StorageApp }
        $StorageCore = $StorageAccounts | Where-Object { $PSItem.StorageAccountName -eq $Naming.StorageCore }

        # Checked as a set so the first failure names the specific resource that is missing.
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
                throw "$($ExpectedResource.Description) '$($ExpectedResource.Name)' was not found in '$($ResourceGroupName)'. The Template Spec deployment has not completed successfully."
            }
        }

        # StorageAccount selects which of the two package accounts the app's managed identity
        # has been granted read access to.
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
            if ($null -eq ($FunctionApps | Where-Object { $PSItem.Name -eq $ComponentDefinition.ResourceName })) {
                throw "Function App '$($ComponentDefinition.ResourceName)' was not found in '$($ResourceGroupName)'. The Template Spec deployment has not completed successfully."
            }

            $FunctionAppComponents.Add($ComponentDefinition)
        }

        Write-Output "Resolved all five components and both package storage accounts"
        Write-Output ""

        # Package uploads authenticate with the account key rather than a data plane role
        # assignment, because a freshly granted role can take minutes to propagate and would make
        # a first install fail intermittently. The apps themselves still read the package
        # keylessly with their own managed identity.
        $StorageContexts = @{}
        foreach ($StorageAccount in @($StorageApp, $StorageCore)) {
            $AccountKey = Get-AzStorageAccountKey -ResourceGroupName $ResourceGroupName -Name $StorageAccount.StorageAccountName | Select-Object -First 1
            $StorageContexts[$StorageAccount.StorageAccountName] = New-AzStorageContext -StorageAccountName $StorageAccount.StorageAccountName -StorageAccountKey $AccountKey.Value
        }

        Write-Output "Installing components"

        # Each Function App runs from its own package blob: upload, point the app setting at it,
        # then restart so the platform mounts the new package.
        foreach ($Component in $FunctionAppComponents) {
            $CurrentStepCount++
            Write-Progress -Activity "Installing Cloud Imaging" -Status "$($Component.Name) ($($Component.ResourceName))" -PercentComplete (($CurrentStepCount / $TotalStepCount) * 100)

            try {
                $StorageContext = $StorageContexts[$Component.StorageAccount.StorageAccountName]
                $BlobName = "$($Component.ResourceName)-$($Timestamp).zip"
                $PackageUri = "$($Component.StorageAccount.PrimaryEndpoints.Blob)$($ContainerName)/$($BlobName)"
                $PackagePath = Join-Path -Path $PackageRoot -ChildPath $Component.PackageFileName

                if ($PSCmdlet.ShouldProcess($Component.ResourceName, "Install $($Component.PackageFileName)")) {
                    Write-Output "  $($Component.Name) ($($Component.ResourceName))"

                    # The Template Spec creates the container, so this only covers a deployment
                    # where it was removed afterwards.
                    if ($null -eq (Get-AzStorageContainer -Name $ContainerName -Context $StorageContext -ErrorAction SilentlyContinue)) {
                        New-AzStorageContainer -Name $ContainerName -Context $StorageContext -Permission Off | Out-Null
                    }

                    # Blob names carry a timestamp, so a re-run never overwrites the package a
                    # running app is currently mounted on.
                    Write-Output "    Uploading $($Component.PackageFileName) ($([math]::Round((Get-Item $PackagePath).Length / 1MB, 1)) MB) to $($Component.StorageAccount.StorageAccountName)"
                    Set-AzStorageBlobContent -File $PackagePath -Container $ContainerName -Blob $BlobName -Context $StorageContext -Force | Out-Null

                    Write-Output "    Pointing the app at the package"
                    $AppSetting = Get-WebAppSetting -ResourceGroupName $ResourceGroupName -Name $Component.ResourceName
                    $AppSetting["WEBSITE_RUN_FROM_PACKAGE"] = $PackageUri
                    Set-WebAppSetting -ResourceGroupName $ResourceGroupName -Name $Component.ResourceName -AppSetting $AppSetting

                    Write-Output "    Restarting"
                    Restart-AzWebApp -ResourceGroupName $ResourceGroupName -Name $Component.ResourceName | Out-Null

                    Set-InstallResult -Component $Component.Name -Action "Run from package" -Status "Success" -Detail $Component.ResourceName
                }
            }
            catch [System.Exception] {
                $FailureCount++
                Write-Warning -Message "Failed to install $($Component.Name): $($_.Exception.Message)"
                Set-InstallResult -Component $Component.Name -Action "Run from package" -Status "Failed" -Detail $_.Exception.Message
            }
        }

        # The portal backend is a Linux App Service, so it takes an ordinary zip deploy rather
        # than run-from-package.
        $CurrentStepCount++
        Write-Progress -Activity "Installing Cloud Imaging" -Status "Portal backend ($($PortalBackend.Name))" -PercentComplete (($CurrentStepCount / $TotalStepCount) * 100)

        try {
            if ($PSCmdlet.ShouldProcess($PortalBackend.Name, "Install portal-backend.zip")) {
                $BackendPackage = Join-Path -Path $PackageRoot -ChildPath "portal-backend.zip"
                Write-Output "  Portal backend ($($PortalBackend.Name))"
                Write-Output "    Deploying portal-backend.zip ($([math]::Round((Get-Item $BackendPackage).Length / 1MB, 1)) MB), this takes a few minutes"
                Publish-AzWebApp -ResourceGroupName $ResourceGroupName -Name $PortalBackend.Name -ArchivePath $BackendPackage -Force | Out-Null

                Set-InstallResult -Component "Portal backend" -Action "Zip deploy" -Status "Success" -Detail $PortalBackend.Name
            }
        }
        catch [System.Exception] {
            $FailureCount++
            Write-Warning -Message "Failed to install the portal backend: $($_.Exception.Message)"
            Set-InstallResult -Component "Portal backend" -Action "Zip deploy" -Status "Failed" -Detail $_.Exception.Message
        }

        # Static Web Apps content can only be published with the Static Web Apps CLI, using a
        # deployment token read from the resource itself.
        $CurrentStepCount++
        Write-Progress -Activity "Installing Cloud Imaging" -Status "Portal frontend ($($PortalFrontend.Name))" -PercentComplete (($CurrentStepCount / $TotalStepCount) * 100)

        $FrontendDirectory = Join-Path -Path $env:TEMP -ChildPath "cloudimaging-frontend-$($Timestamp)"
        $PreviousNodeOptions = $env:NODE_OPTIONS
        try {
            if ($PSCmdlet.ShouldProcess($PortalFrontend.Name, "Install portal-frontend.zip")) {
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

                Set-InstallResult -Component "Portal frontend" -Action "Static Web Apps deploy" -Status "Success" -Detail $PortalFrontend.Name
            }
        }
        catch [System.Exception] {
            $FailureCount++
            Write-Warning -Message "Failed to install the portal frontend: $($_.Exception.Message)"
            Set-InstallResult -Component "Portal frontend" -Action "Static Web Apps deploy" -Status "Failed" -Detail $_.Exception.Message
        }
        finally {
            $env:NODE_OPTIONS = $PreviousNodeOptions
            if (Test-Path -Path $FrontendDirectory) {
                Remove-Item -Path $FrontendDirectory -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        Write-Output ""
        Write-Output "Verifying the installation"

        # Reads the setting back from Azure rather than trusting the write above, which catches a
        # deployment that reported success but left the app unbound. Only failures change the
        # component's row, so a verified component still reads as its install action.
        foreach ($Component in $FunctionAppComponents) {
            try {
                $AppSetting = Get-WebAppSetting -ResourceGroupName $ResourceGroupName -Name $Component.ResourceName
                if ([string]::IsNullOrWhiteSpace($AppSetting["WEBSITE_RUN_FROM_PACKAGE"])) {
                    $FailureCount++
                    Write-Output "  $($Component.Name): package not bound"
                    Set-InstallResult -Component $Component.Name -Status "Failed" -Detail "Package not bound after deployment"
                }
                else {
                    Write-Output "  $($Component.Name): running from package"
                }
            }
            catch [System.Exception] {
                $FailureCount++
                Write-Output "  $($Component.Name): could not be verified"
                Set-InstallResult -Component $Component.Name -Status "Failed" -Detail $_.Exception.Message
            }
        }

        # Proves the portal actually serves traffic, which is the only check that covers a
        # package that deployed cleanly but fails at startup.
        $PortalUrl = "https://$($PortalFrontend.DefaultHostname)"
        $EndpointChecks = @(
            [PSCustomObject]@{
                Component = "Portal backend"
                Uri = "https://$($PortalBackend.DefaultHostName)/api/health"
            }
            [PSCustomObject]@{
                Component = "Portal frontend"
                Uri = $PortalUrl
            }
        )

        foreach ($EndpointCheck in $EndpointChecks) {
            Write-Output "  $($EndpointCheck.Component): waiting for $($EndpointCheck.Uri)"
            if (Test-HttpEndpoint -Uri $EndpointCheck.Uri) {
                Write-Output "  $($EndpointCheck.Component): responded"
                continue
            }

            $FailureCount++
            Write-Output "  $($EndpointCheck.Component): no response"
            Set-InstallResult -Component $EndpointCheck.Component -Status "Failed" -Detail "No response from $($EndpointCheck.Uri)"
        }
    }
    catch [System.Exception] {
        $FailureCount++
        Write-Warning -Message "Cloud Imaging installation stopped: $($_.Exception.Message)"
    }
}
End {
    # Runs whether the installation succeeded or failed, so no progress bar is left on screen.
    Write-Progress -Activity "Installing Cloud Imaging" -Completed
    Write-Progress -Activity "Verifying Cloud Imaging" -Completed

    Write-Output ""
    Write-Output "Installation summary"

    if ($InstallResults.Count -gt 0) {
        Write-Output ($InstallResults | Format-Table -AutoSize -Wrap | Out-String).TrimEnd()
    }

    Write-Output ""

    if ($FailureCount -gt 0) {
        Write-Warning -Message "$($FailureCount) step(s) did not complete. Resolve the errors above and run this script again, it is safe to re-run."
    }
    else {
        Write-Output "All components installed and verified."
        Write-Output "Portal: $($PortalUrl)"
        Write-Output "Next step: run post-install.ps1 to complete the Microsoft Entra grants."
    }
}
