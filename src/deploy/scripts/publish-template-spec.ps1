<#
.SYNOPSIS
    Publish the CloudImaging Bicep template as an Azure Template Spec
    for community self-hosting deployment (T166, FR-041, FR-044a).

.DESCRIPTION
    Wraps New-AzTemplateSpec to publish main.bicep + uiFormDefinition.json
    to an Azure Template Spec in the administrator's own subscription.
    After publishing, open the printed portal URL to launch the Form View wizard.

.PARAMETER ResourceGroupName
    Target resource group for the Template Spec resource.

.PARAMETER Location
    Azure region.

.PARAMETER Version
    Semantic version string (e.g., "1.0.0").

.EXAMPLE
    .\publish-template-spec.ps1 -ResourceGroupName "rg-mse-dev-cloudimaging" -Location "eastus" -Version "1.0.0"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ResourceGroupName,

    [Parameter(Mandatory)]
    [string] $Location,

    [Parameter(Mandatory)]
    [string] $Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDir    = $PSScriptRoot
$bicepFile    = Join-Path $scriptDir '..\bicep\main.bicep'
$uiFile       = Join-Path $scriptDir '..\uiFormDefinition.json'

if (-not (Test-Path $bicepFile)) { throw "main.bicep not found at: $bicepFile" }
if (-not (Test-Path $uiFile))    { throw "uiFormDefinition.json not found at: $uiFile" }

Write-Host "Publishing CloudImaging Template Spec v$Version to $ResourceGroupName ($Location) ..."

$spec = New-AzTemplateSpec `
    -Name                 'CloudImaging' `
    -ResourceGroupName    $ResourceGroupName `
    -Location             $Location `
    -Version              $Version `
    -TemplateFile         $bicepFile `
    -UIFormDefinitionFile $uiFile `
    -Force

$versionResourceId = $spec.Versions[0].Id
$resourceGroupName = $ResourceGroupName

# The portal #create marketplace URL does not work for Template Specs.
# Navigate manually: Azure Portal → Resource groups → $ResourceGroupName → CloudImaging (Template Spec) → Deploy
$portalRgUrl = "https://portal.azure.com/#@/resource$(($spec.Id -replace '/versions/[^/]+$', ''))"

Write-Host ""
Write-Host "[OK] Template Spec 'CloudImaging' v$Version published to $ResourceGroupName."
Write-Host ""
Write-Host "To deploy, navigate in the Azure Portal to:"
Write-Host "  Resource groups > $ResourceGroupName > CloudImaging (Template Spec resource) > Deploy"
Write-Host ""
Write-Host "Or open this direct link (signed in to the target tenant):"
Write-Host "  $portalRgUrl"
