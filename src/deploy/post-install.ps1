<#
.SYNOPSIS
    Completes a Cloud Imaging installation with the two Microsoft Entra grants that cannot be
    made from the Azure portal.

.DESCRIPTION
    Run this after install.ps1. It performs the two directory level grants that neither Bicep
    nor the Azure portal can make:

        1. Grants the Microsoft Graph application permission
           DeviceManagementServiceConfig.Read.All to the Imaging Core API managed identity,
           which the device pre-flight authorization check needs in order to read Windows
           Autopilot and Intune corporate identifiers.

        2. Assigns the CloudImaging.PortalAccess app role on the Operator API app registration
           to the portal backend's managed identity, so the portal backend can call the
           Operator API. The portal's own "Users and groups" picker only lists users and
           groups, never managed identities, so this assignment has to go through Microsoft
           Graph.

    Where install.ps1 needs Azure permissions, this script needs Microsoft Entra privileges:
    sign-in requires consent for AppRoleAssignment.ReadWrite.All and Application.Read.All,
    which a Privileged Role Administrator or Global Administrator holds. Those roles often
    belong to a different person, which is why this is a separate script.

    Both grants are idempotent and verified, so the script is safe to re-run.

    Microsoft Entra takes time to replicate role and permission changes. Allow several minutes
    before testing device pre-flight authorization.

.PARAMETER ResourceGroupName
    Name of the resource group holding the Cloud Imaging resources.

.PARAMETER SubscriptionId
    Subscription holding the resource group. Required, so the grants can never be resolved
    against whichever subscription the session happened to default to.

.PARAMETER OperatorApiClientId
    Application (client) ID of the Cloud Imaging Operator API app registration.

.EXAMPLE
    .\post-install.ps1 -ResourceGroupName "corp-prod-rg" -SubscriptionId "<subscription-id>" -OperatorApiClientId "<client-id>"

.NOTES
    FileName:    post-install.ps1
    Author:      MSEndpointMgr
    Contact:     @MSEndpointMgr
    Created:     2026-09-13
    Updated:     2026-09-13

    Version history:
    1.0.0 - (2026-09-13) Initial release, replacing grant-graph-permissions.ps1 and
                         assign-service-roles.ps1
    1.1.0 - (2026-09-14) Restarts the portal backend after assigning the Operator API app role,
                         so its cached token (issued without the role) is discarded
#>
#Requires -Modules Az.Accounts, Az.ManagedServiceIdentity, Az.Resources, Az.Websites, Microsoft.Graph.Authentication, Microsoft.Graph.Applications
[CmdletBinding(SupportsShouldProcess)]
param (
    [Parameter(Mandatory = $true, HelpMessage = "Resource group holding the Cloud Imaging resources.")]
    [ValidateNotNullOrEmpty()]
    [string] $ResourceGroupName,
    [Parameter(Mandatory = $true, HelpMessage = "Subscription holding the resource group.")]
    [ValidateNotNullOrEmpty()]
    [string] $SubscriptionId,
    [Parameter(Mandatory = $true, HelpMessage = "Client ID of the Operator API app registration.")]
    [ValidateNotNullOrEmpty()]
    [string] $OperatorApiClientId
)
Begin {
    $ErrorActionPreference = "Stop"

    # Keeps the operator's output to what this script reports. Az otherwise prints SDK
    # breaking-change banners, survey prompts, and performs a gallery version check that
    # raises its own confirmation prompt. Process scope, so the machine config is untouched.
    Update-AzConfig -DisplayBreakingChangeWarning $false -DisplaySurveyMessage $false -CheckForUpgrade $false -Scope Process | Out-Null

    $GraphAppId = "00000003-0000-0000-c000-000000000000"
    $GraphPermission = "DeviceManagementServiceConfig.Read.All"
    $OperatorApiRole = "CloudImaging.PortalAccess"
    $RequiredScopes = @("AppRoleAssignment.ReadWrite.All", "Application.Read.All")

    $GrantResults = New-Object -TypeName "System.Collections.Generic.List[System.Object]"
    $CurrentStepCount = 0
    $FailureCount = 0
    $TotalStepCount = 2

    function Add-GrantResult {
        param (
            [Parameter(Mandatory = $true, HelpMessage = "Grant the result applies to.")]
            [ValidateNotNullOrEmpty()]
            [string] $Grant,
            [Parameter(Mandatory = $true, HelpMessage = "Identity the grant was made to.")]
            [ValidateNotNullOrEmpty()]
            [string] $Identity,
            [Parameter(Mandatory = $true, HelpMessage = "Outcome of the grant.")]
            [ValidateSet("Granted", "Already present", "Failed")]
            [string] $Status,
            [Parameter(Mandatory = $false, HelpMessage = "Detail shown in the summary table.")]
            [string] $Detail = ""
        )
        $GrantResults.Add([PSCustomObject]@{
            Grant = $Grant
            Identity = $Identity
            Status = $Status
            Detail = $Detail
        })
    }

    function Get-CloudImagingIdentity {
        param (
            [Parameter(Mandatory = $true, HelpMessage = "Resource group to search.")]
            [ValidateNotNullOrEmpty()]
            [string] $ResourceGroupName,
            [Parameter(Mandatory = $true, HelpMessage = "Name filter, for example '*-ci-msi-core'.")]
            [ValidateNotNullOrEmpty()]
            [string] $NameFilter
        )
        $Identities = @(
            Get-AzUserAssignedIdentity -ResourceGroupName $ResourceGroupName |
                Where-Object { $PSItem.Name -like $NameFilter }
        )

        if ($Identities.Count -ne 1) {
            throw "Expected one managed identity matching '$($NameFilter)' in '$($ResourceGroupName)' but found $($Identities.Count). The Template Spec deployment has not completed successfully."
        }

        return $Identities[0]
    }

    function Get-AppRoleAssignmentMatch {
        param (
            [Parameter(Mandatory = $true, HelpMessage = "Principal ID of the managed identity.")]
            [ValidateNotNullOrEmpty()]
            [string] $PrincipalId,
            [Parameter(Mandatory = $true, HelpMessage = "Object ID of the resource service principal.")]
            [ValidateNotNullOrEmpty()]
            [string] $ResourceId,
            [Parameter(Mandatory = $true, HelpMessage = "ID of the app role.")]
            [ValidateNotNullOrEmpty()]
            [string] $AppRoleId
        )
        return Get-MgServicePrincipalAppRoleAssignment -ServicePrincipalId $PrincipalId -All |
            Where-Object { $PSItem.ResourceId -eq $ResourceId -and $PSItem.AppRoleId -eq $AppRoleId } |
            Select-Object -First 1
    }
}
Process {
    try {
        Write-Output "Cloud Imaging post-installation"
        Write-Output "Resource group: $($ResourceGroupName)"
        Write-Output ""

        Write-Output "Checking prerequisites"

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

        if ($null -eq (Get-AzResourceGroup -Name $ResourceGroupName -ErrorAction SilentlyContinue)) {
            throw "Resource group '$($ResourceGroupName)' was not found in subscription '$($AzContext.Subscription.Name)'."
        }

        $GraphContext = Get-MgContext -ErrorAction SilentlyContinue
        $MissingScopes = @()
        if ($null -ne $GraphContext) {
            $MissingScopes = @($RequiredScopes | Where-Object { $PSItem -notin $GraphContext.Scopes })
        }

        if ($null -eq $GraphContext -or $MissingScopes.Count -gt 0) {
            Write-Output "Connecting to Microsoft Graph, a browser sign-in will open"
            Connect-MgGraph -Scopes $RequiredScopes -NoWelcome
            $GraphContext = Get-MgContext
        }

        Write-Output "Azure account: $($AzContext.Account.Id)"
        Write-Output "Microsoft Graph account: $($GraphContext.Account)"
        Write-Output ""

        # Both identities are resolved from the naming convention, so neither the prefix nor the
        # environment has to be supplied.
        Write-Output "Resolving managed identities"
        $ImagingCoreIdentity = Get-CloudImagingIdentity -ResourceGroupName $ResourceGroupName -NameFilter "*-ci-msi-core"
        $PortalBackendIdentity = Get-CloudImagingIdentity -ResourceGroupName $ResourceGroupName -NameFilter "*-ci-msi-portal"
        Write-Output "Imaging Core API: $($ImagingCoreIdentity.Name)"
        Write-Output "Portal backend: $($PortalBackendIdentity.Name)"
        Write-Output ""
        Write-Output "Applying grants"

        # Grant 1: Microsoft Graph application permission for the device pre-flight check.
        $CurrentStepCount++
        Write-Progress -Activity "Completing Cloud Imaging setup" -Status $GraphPermission -PercentComplete (($CurrentStepCount / $TotalStepCount) * 100)

        try {
            $GraphServicePrincipal = Get-MgServicePrincipal -Filter "appId eq '$($GraphAppId)'" -Property @("id", "appRoles") | Select-Object -First 1
            if ($null -eq $GraphServicePrincipal) {
                throw "The Microsoft Graph service principal was not found in this tenant."
            }

            # Application rather than delegated, because the Imaging Core API calls Graph as
            # itself with no user present.
            $GraphAppRole = $GraphServicePrincipal.AppRoles | Where-Object { $PSItem.Value -eq $GraphPermission -and $PSItem.AllowedMemberTypes -contains "Application" } | Select-Object -First 1
            if ($null -eq $GraphAppRole) {
                throw "The Microsoft Graph application role $($GraphPermission) was not found."
            }

            $ExistingAssignment = Get-AppRoleAssignmentMatch -PrincipalId $ImagingCoreIdentity.PrincipalId -ResourceId $GraphServicePrincipal.Id -AppRoleId $GraphAppRole.Id
            if ($null -ne $ExistingAssignment) {
                Add-GrantResult -Grant $GraphPermission -Identity $ImagingCoreIdentity.Name -Status "Already present"
            }
            elseif ($PSCmdlet.ShouldProcess($ImagingCoreIdentity.Name, "Grant $($GraphPermission)")) {
                $AssignmentBody = @{
                    principalId = $ImagingCoreIdentity.PrincipalId
                    resourceId = $GraphServicePrincipal.Id
                    appRoleId = $GraphAppRole.Id
                }
                New-MgServicePrincipalAppRoleAssignment -ServicePrincipalId $ImagingCoreIdentity.PrincipalId -BodyParameter $AssignmentBody | Out-Null

                # Read back rather than trusting the create call, because a silently failed grant
                # only surfaces later as a pre-flight authorization failure.
                if ($null -eq (Get-AppRoleAssignmentMatch -PrincipalId $ImagingCoreIdentity.PrincipalId -ResourceId $GraphServicePrincipal.Id -AppRoleId $GraphAppRole.Id)) {
                    throw "The assignment was created but could not be verified."
                }

                Add-GrantResult -Grant $GraphPermission -Identity $ImagingCoreIdentity.Name -Status "Granted"
            }
        }
        catch [System.Exception] {
            $FailureCount++
            Write-Warning -Message "Could not grant $($GraphPermission): $($_.Exception.Message)"
            Add-GrantResult -Grant $GraphPermission -Identity $ImagingCoreIdentity.Name -Status "Failed" -Detail $_.Exception.Message
        }

        # Grant 2: Operator API service to service app role for the portal backend.
        $CurrentStepCount++
        Write-Progress -Activity "Completing Cloud Imaging setup" -Status $OperatorApiRole -PercentComplete (($CurrentStepCount / $TotalStepCount) * 100)

        try {
            $OperatorApiServicePrincipal = Get-MgServicePrincipal -Filter "appId eq '$($OperatorApiClientId)'" | Select-Object -First 1
            if ($null -eq $OperatorApiServicePrincipal) {
                throw "No service principal was found for application ID $($OperatorApiClientId). Check that the Operator API app registration exists in this tenant and that the client ID is correct."
            }

            $PortalAccessRole = $OperatorApiServicePrincipal.AppRoles | Where-Object { $PSItem.Value -eq $OperatorApiRole } | Select-Object -First 1
            if ($null -eq $PortalAccessRole) {
                throw "The app role $($OperatorApiRole) was not found on the Operator API app registration. Add it as described in the setup instructions, then run this script again."
            }

            $ExistingAssignment = Get-AppRoleAssignmentMatch -PrincipalId $PortalBackendIdentity.PrincipalId -ResourceId $OperatorApiServicePrincipal.Id -AppRoleId $PortalAccessRole.Id
            if ($null -ne $ExistingAssignment) {
                Add-GrantResult -Grant $OperatorApiRole -Identity $PortalBackendIdentity.Name -Status "Already present"
            }
            elseif ($PSCmdlet.ShouldProcess($PortalBackendIdentity.Name, "Assign $($OperatorApiRole)")) {
                $AssignmentBody = @{
                    principalId = $PortalBackendIdentity.PrincipalId
                    resourceId = $OperatorApiServicePrincipal.Id
                    appRoleId = $PortalAccessRole.Id
                }
                New-MgServicePrincipalAppRoleAssignment -ServicePrincipalId $PortalBackendIdentity.PrincipalId -BodyParameter $AssignmentBody | Out-Null

                if ($null -eq (Get-AppRoleAssignmentMatch -PrincipalId $PortalBackendIdentity.PrincipalId -ResourceId $OperatorApiServicePrincipal.Id -AppRoleId $PortalAccessRole.Id)) {
                    throw "The assignment was created but could not be verified."
                }

                Add-GrantResult -Grant $OperatorApiRole -Identity $PortalBackendIdentity.Name -Status "Granted"

                # The portal backend caches its Operator API token in process until the token
                # expires, and one issued before this assignment carries no roles claim. Without
                # a restart every portal page keeps getting 403 from the Operator API for hours
                # after the grant is actually in place.
                $PortalWebApp = @(Get-AzWebApp -ResourceGroupName $ResourceGroupName | Where-Object { $PSItem.Name -like "*-app-portal" })
                if ($PortalWebApp.Count -eq 1) {
                    Write-Output "Restarting $($PortalWebApp[0].Name) to discard its cached Operator API token"
                    Restart-AzWebApp -ResourceGroupName $ResourceGroupName -Name $PortalWebApp[0].Name | Out-Null
                }
                else {
                    Write-Warning -Message "Could not identify the portal backend App Service in '$($ResourceGroupName)'. Restart it manually, otherwise the portal keeps using a token issued before this assignment."
                }
            }
        }
        catch [System.Exception] {
            $FailureCount++
            Write-Warning -Message "Could not assign $($OperatorApiRole): $($_.Exception.Message)"
            Add-GrantResult -Grant $OperatorApiRole -Identity $PortalBackendIdentity.Name -Status "Failed" -Detail $_.Exception.Message
        }

        Write-Progress -Activity "Completing Cloud Imaging setup" -Completed
    }
    catch [System.Exception] {
        $FailureCount++
        Write-Warning -Message "Cloud Imaging post-installation stopped: $($_.Exception.Message)"
    }
}
End {
    # Runs whether the grants succeeded or failed, so no progress bar is left on screen.
    Write-Progress -Activity "Completing Cloud Imaging setup" -Completed

    Write-Output ""
    Write-Output "Post-installation summary"

    if ($GrantResults.Count -gt 0) {
        Write-Output ($GrantResults | Format-Table -AutoSize -Wrap | Out-String).TrimEnd()
    }

    Write-Output ""

    if ($FailureCount -gt 0) {
        Write-Warning -Message "$($FailureCount) grant(s) did not complete. Resolve the errors above and run this script again, it is safe to re-run."
    }
    else {
        Write-Output "Both grants are in place. Allow several minutes for Microsoft Entra to replicate them."
    }
}
