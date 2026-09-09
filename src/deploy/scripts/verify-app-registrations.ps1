<#
.SYNOPSIS
    Verify the three Entra ID app registrations from Step 1 are configured
    correctly before continuing to Phase 2 (deploying the Azure resources).

.DESCRIPTION
    Read-only sanity check for the manual app registration steps in
    setup-instructions.md. Confirms each registration exists, has the right
    sign-in audience, redirect URI platform, exposed API scope, and app
    roles, and flags the most common mistakes that produce the AADSTS errors
    called out in the guide (AADSTS9002326, AADSTS500011, AADSTS650057).

    This is optional and makes no changes -- it only reads via Microsoft
    Graph. Some things it can't verify from a script and calls out instead:
    admin consent status (shown as a green checkmark on the API permissions
    page) and the Media Builder -> Operator API delegated permission grant
    (Registration 3, step 7), since checking consent status needs Graph
    permissions beyond simple read access.

.PARAMETER PortalClientId
    Application (client) ID of the Cloud Imaging Portal app registration (Registration 1).

.PARAMETER OperatorApiClientId
    Application (client) ID of the Cloud Imaging Operator API app registration (Registration 2).

.PARAMETER MediaBuilderClientId
    Application (client) ID of the Cloud Imaging Media Builder app registration (Registration 3).

.EXAMPLE
    .\verify-app-registrations.ps1 `
        -PortalClientId       "00000000-0000-0000-0000-000000000000" `
        -OperatorApiClientId  "11111111-1111-1111-1111-111111111111" `
        -MediaBuilderClientId "22222222-2222-2222-2222-222222222222"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PortalClientId,

    [Parameter(Mandatory)]
    [string] $OperatorApiClientId,

    [Parameter(Mandatory)]
    [string] $MediaBuilderClientId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:failCount = 0
$script:warnCount = 0

function Test-Check {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [bool]   $Condition,
        [string] $FailureHint = ''
    )
    if ($Condition) {
        Write-Host "  [OK]   $Name"
    } else {
        Write-Host "  [FAIL] $Name" -ForegroundColor Red
        if ($FailureHint) { Write-Host "         $FailureHint" -ForegroundColor Red }
        $script:failCount++
    }
}

function Write-Note {
    param([Parameter(Mandatory)] [string] $Message)
    Write-Host "  [MANUAL CHECK] $Message" -ForegroundColor Yellow
    $script:warnCount++
}

# ── Ensure Graph connection (read-only) ───────────────────────────────────────

try {
    $ctx = Get-MgContext
    if (-not $ctx -or 'Application.Read.All' -notin $ctx.Scopes) {
        throw "Not connected with the required scope"
    }
    Write-Host "Already connected to Microsoft Graph as $($ctx.Account)"
} catch {
    Write-Host "Connecting to Microsoft Graph (browser sign-in will open)..."
    Connect-MgGraph -Scopes 'Application.Read.All'
}

function Get-AppOrFail {
    param([Parameter(Mandatory)] [string] $ClientId, [Parameter(Mandatory)] [string] $Label)
    $app = Get-MgApplication -Filter "appId eq '$ClientId'"
    if (-not $app) {
        Write-Host "  [FAIL] $Label app registration not found for client ID $ClientId" -ForegroundColor Red
        $script:failCount++
    }
    return $app
}

# ── Registration 1: Cloud Imaging Portal ──────────────────────────────────────

Write-Host ""
Write-Host "=== Registration 1: Cloud Imaging Portal ==="
$portalApp = Get-AppOrFail -ClientId $PortalClientId -Label 'Portal'
if ($portalApp) {
    Test-Check "Single tenant" ($portalApp.SignInAudience -eq 'AzureADMyOrg') `
        "Supported account types should be 'Single tenant' (AzureADMyOrg)."
    Test-Check "Has a Single-page application redirect URI" ($portalApp.Spa.RedirectUris.Count -gt 0) `
        "Add a platform -> Single-page application with at least a placeholder redirect URI."
    Test-Check "No Mobile/desktop platform added" ($portalApp.PublicClient.RedirectUris.Count -eq 0) `
        "A Mobile/desktop platform on this registration causes AADSTS9002326. Remove it."
    Test-Check "'Allow public client flows' is No" (-not $portalApp.IsFallbackPublicClient) `
        "Authentication -> Advanced settings -> Allow public client flows must be No (also AADSTS9002326)."
    Test-Check "Application ID URI is set" ($portalApp.IdentifierUris.Count -gt 0) `
        "Expose an API -> set the Application ID URI (accept the default api://<clientId>)."
    $portalScope = $portalApp.Api.Oauth2PermissionScopes | Where-Object { $_.Value -eq 'user_impersonation' -and $_.IsEnabled }
    Test-Check "'user_impersonation' scope exposed and enabled" ($null -ne $portalScope) `
        "Expose an API -> Add a scope named 'user_impersonation', state Enabled. Missing this causes AADSTS500011."
    foreach ($role in 'CloudImaging.Administrator', 'CloudImaging.Technician', 'CloudImaging.Reader') {
        $r = $portalApp.AppRoles | Where-Object { $_.Value -eq $role }
        Test-Check "App role '$role' exists (Users/Groups)" ($r -and $r.AllowedMemberTypes -contains 'User') `
            "App roles -> add '$role', allowed for Users/Groups."
    }
}

# ── Registration 2: Cloud Imaging Operator API ────────────────────────────────

Write-Host ""
Write-Host "=== Registration 2: Cloud Imaging Operator API ==="
$operatorApp = Get-AppOrFail -ClientId $OperatorApiClientId -Label 'Operator API'
if ($operatorApp) {
    Test-Check "Single tenant" ($operatorApp.SignInAudience -eq 'AzureADMyOrg') `
        "Supported account types should be 'Single tenant' (AzureADMyOrg)."
    Test-Check "Application ID URI is set" ($operatorApp.IdentifierUris.Count -gt 0) `
        "Expose an API -> set the Application ID URI (accept the default api://<clientId>)."
    $operatorScope = $operatorApp.Api.Oauth2PermissionScopes | Where-Object { $_.Value -eq 'user_impersonation' -and $_.IsEnabled }
    Test-Check "'user_impersonation' scope exposed and enabled" ($null -ne $operatorScope) `
        "Expose an API -> Add a scope named 'user_impersonation', state Enabled. Missing this causes AADSTS650057."
    $portalAccessRole = $operatorApp.AppRoles | Where-Object { $_.Value -eq 'CloudImaging.PortalAccess' }
    Test-Check "App role 'CloudImaging.PortalAccess' exists (Applications)" ($portalAccessRole -and $portalAccessRole.AllowedMemberTypes -contains 'Application') `
        "App roles -> add 'CloudImaging.PortalAccess', allowed for Applications."
    $mediaBuilderAccessRole = $operatorApp.AppRoles | Where-Object { $_.Value -eq 'CloudImaging.MediaBuilderAccess' }
    Test-Check "App role 'CloudImaging.MediaBuilderAccess' exists (Users/Groups + Applications)" `
        ($mediaBuilderAccessRole -and $mediaBuilderAccessRole.AllowedMemberTypes -contains 'User' -and $mediaBuilderAccessRole.AllowedMemberTypes -contains 'Application') `
        "App roles -> add 'CloudImaging.MediaBuilderAccess', allowed for Both (Users/Groups + Applications). Must include Users/Groups or Media Builder calls return 403."
}

# ── Registration 3: Cloud Imaging Media Builder ───────────────────────────────

Write-Host ""
Write-Host "=== Registration 3: Cloud Imaging Media Builder ==="
$mediaBuilderApp = Get-AppOrFail -ClientId $MediaBuilderClientId -Label 'Media Builder'
if ($mediaBuilderApp) {
    Test-Check "Single tenant" ($mediaBuilderApp.SignInAudience -eq 'AzureADMyOrg') `
        "Supported account types should be 'Single tenant' (AzureADMyOrg)."
    Test-Check "Has a Mobile/desktop redirect URI of http://localhost" ($mediaBuilderApp.PublicClient.RedirectUris -contains 'http://localhost') `
        "Add a platform -> Mobile and desktop applications -> redirect URI http://localhost."
    foreach ($role in 'CloudImaging.Administrator', 'CloudImaging.Technician') {
        $r = $mediaBuilderApp.AppRoles | Where-Object { $_.Value -eq $role }
        Test-Check "App role '$role' exists (Users/Groups)" ($r -and $r.AllowedMemberTypes -contains 'User') `
            "App roles -> add '$role', allowed for Users/Groups."
    }
    if ($operatorApp) {
        $hasPermission = $mediaBuilderApp.RequiredResourceAccess |
            Where-Object { $_.ResourceAppId -eq $operatorApp.AppId } |
            ForEach-Object { $_.ResourceAccess } |
            Where-Object { $operatorScope -and $_.Id -eq $operatorScope.Id }
        Test-Check "Requests the Operator API's 'user_impersonation' permission" ($null -ne $hasPermission) `
            "Registration 3, step 7: API permissions -> Add a permission -> My APIs -> Cloud Imaging Operator API -> user_impersonation."
    }
}

# ── Things this script can't verify ────────────────────────────────────────────

Write-Host ""
Write-Host "=== Can't be checked automatically ==="
Write-Note "Admin consent is actually granted for every delegated permission above (each API permissions page should show a green checkmark under Status, not just be listed)."
Write-Note "Registration 3, step 7's 'Grant admin consent for <your tenant>' button was clicked after adding the Operator API permission."

# ── Summary ────────────────────────────────────────────────────────────────────

Write-Host ""
if ($script:failCount -eq 0) {
    Write-Host "=== All automated checks passed ($script:warnCount manual check(s) above still to confirm) ===" -ForegroundColor Green
} else {
    Write-Host "=== $script:failCount check(s) failed -- fix these before continuing to Phase 2 ===" -ForegroundColor Red
    exit 1
}
