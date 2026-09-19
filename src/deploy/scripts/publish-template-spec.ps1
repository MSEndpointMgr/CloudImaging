<#
.SYNOPSIS
    Publishes the Cloud Imaging deployment template as an Azure Template Spec.

.DESCRIPTION
    Publishes the compiled ARM template and the portal wizard definition that ship in this
    bundle as a Template Spec in your own subscription, then prints the portal link that
    launches the deployment wizard.

    The pre-compiled template is published rather than the Bicep source, so the Bicep CLI is
    not required.

    The Template Spec version defaults to the release version recorded in the bundle, so it
    matches the packages it was shipped with. Pass -Version only when publishing from a source
    checkout, where that file does not exist.

    Publish into the same resource group the solution will be deployed into, so the Template
    Spec travels with the deployment it created.

.PARAMETER ResourceGroupName
    Name of the resource group to publish the Template Spec into.

.PARAMETER Location
    Azure region short name, for example westeurope or eastus.

.PARAMETER Version
    Template Spec version. Defaults to the release version recorded in the bundle.

.EXAMPLE
    .\publish-template-spec.ps1 -ResourceGroupName "<resource-group>" -Location "westeurope"

.NOTES
    FileName:    publish-template-spec.ps1
    Author:      MSEndpointMgr
    Contact:     @MSEndpointMgr
    Created:     2026-09-13
    Updated:     2026-09-13

    Version history:
    1.0.0 - (2026-09-13) Rewritten to the repository standard. Publishes the compiled ARM
                         template and defaults the version to the bundled release version
#>
#Requires -Modules Az.Accounts, Az.Resources
[CmdletBinding(SupportsShouldProcess)]
param (
    [Parameter(Mandatory = $true, HelpMessage = "Resource group to publish the Template Spec into.")]
    [ValidateNotNullOrEmpty()]
    [string] $ResourceGroupName,
    [Parameter(Mandatory = $true, HelpMessage = "Azure region short name, for example westeurope.")]
    [ValidateNotNullOrEmpty()]
    [string] $Location,
    [Parameter(Mandatory = $false, HelpMessage = "Template Spec version. Defaults to the bundled release version.")]
    [ValidateNotNullOrEmpty()]
    [string] $Version
)
Begin {
    $ErrorActionPreference = "Stop"

    # Keeps the operator's output to what this script reports. Az otherwise prints SDK
    # breaking-change banners, survey prompts, and performs a gallery version check that
    # raises its own confirmation prompt. Process scope, so the machine config is untouched.
    Update-AzConfig -DisplayBreakingChangeWarning $false -DisplaySurveyMessage $false -CheckForUpgrade $false -Scope Process | Out-Null

    # This script lives in scripts\, everything it publishes sits at the bundle root above it.
    $BundleRoot = Split-Path -Path $PSScriptRoot -Parent
    $TemplateFile = Join-Path -Path $BundleRoot -ChildPath "bicep\main.json"
    $UIFormFile = Join-Path -Path $BundleRoot -ChildPath "uiFormDefinition.json"
    $VersionFile = Join-Path -Path $BundleRoot -ChildPath "version.txt"
    $TemplateSpecName = "CloudImaging"
    $PortalUrl = ""
}
Process {
    try {
        # The release workflow records the version in the bundle, so the Template Spec version
        # matches the component packages sitting next to it.
        if (-not $PSBoundParameters.ContainsKey("Version")) {
            if (-not (Test-Path -Path $VersionFile)) {
                throw "No version.txt was found in '$($BundleRoot)'. Run this from an extracted release bundle, or pass -Version explicitly."
            }
            $Version = (Get-Content -Path $VersionFile -Raw).Trim()
        }

        Write-Output "Cloud Imaging Template Spec"
        Write-Output "Version: $($Version)"
        Write-Output "Resource group: $($ResourceGroupName)"
        Write-Output ""

        if (-not (Test-Path -Path $TemplateFile)) {
            throw "The compiled template was not found at '$($TemplateFile)'. Run this from an extracted release bundle."
        }

        if (-not (Test-Path -Path $UIFormFile)) {
            throw "The wizard definition was not found at '$($UIFormFile)'. Run this from an extracted release bundle."
        }

        $AzContext = Get-AzContext -ErrorAction SilentlyContinue
        if ($null -eq $AzContext) {
            Write-Output "No Azure session found, a browser sign-in will open"
            Connect-AzAccount | Out-Null
            $AzContext = Get-AzContext
        }

        if ($null -eq (Get-AzResourceGroup -Name $ResourceGroupName -ErrorAction SilentlyContinue)) {
            throw "Resource group '$($ResourceGroupName)' was not found in subscription '$($AzContext.Subscription.Name)'. Create it first with New-AzResourceGroup."
        }

        Write-Output "Signed in as $($AzContext.Account.Id)"
        Write-Output "Subscription: $($AzContext.Subscription.Name) ($($AzContext.Subscription.Id))"
        Write-Output ""

        if ($PSCmdlet.ShouldProcess($ResourceGroupName, "Publish Template Spec $($TemplateSpecName) version $($Version)")) {
            Write-Output "Publishing"
            $TemplateSpec = New-AzTemplateSpec -Name $TemplateSpecName -ResourceGroupName $ResourceGroupName -Location $Location -Version $Version -TemplateFile $TemplateFile -UIFormDefinitionFile $UIFormFile -Force

            # The marketplace '#create' URL does not work for Template Specs, so link to the
            # resource itself, where the Deploy button opens the wizard.
            $PortalUrl = "https://portal.azure.com/#@/resource$($TemplateSpec.Id -replace '/versions/[^/]+$', '')"
        }
    }
    catch [System.Exception] {
        Write-Warning -Message "Could not publish the Template Spec: $($_.Exception.Message)"
    }
}
End {
    if ([string]::IsNullOrWhiteSpace($PortalUrl)) {
        return
    }

    Write-Output ""
    Write-Output "Published '$($TemplateSpecName)' version $($Version) to $($ResourceGroupName)."
    Write-Output "Deploy from: $($PortalUrl)"
}