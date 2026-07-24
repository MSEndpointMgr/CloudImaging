<#
.SYNOPSIS
    Release gate: verifies no environment-specific values or secrets are committed
    before publishing a public release.

.DESCRIPTION
    Scans committed configuration files that ship with a public release (currently the
    WPF Media Builder / Client appsettings.json files) and fails if any of the known
    placeholder settings contain a value. Real dev values must live only in the
    git-ignored appsettings.Local.json / appsettings.Development.json overlays.

    Also asserts that no *.Local.json / *.Development.json overlay would be tracked by git.

    Exit code 0 = clean (safe to release). Non-zero = values found (do NOT release).

.EXAMPLE
    pwsh -File src/deploy/scripts/verify-release-secrets.ps1
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..'))
)

$ErrorActionPreference = 'Stop'
$violations = [System.Collections.Generic.List[string]]::new()

# Committed appsettings.json files that ship publicly and MUST contain only empty placeholders.
$mustBeEmpty = @(
    'src/CloudImaging.MediaBuilder/appsettings.json',
    'src/CloudImaging.Client/appsettings.json'
)

# JSON leaf properties that must remain empty strings in the committed files.
$mustBeEmptyKeys = @('ClientId', 'TenantId', 'OperatorApiScope', 'BaseUrl')

function Test-EmptyLeaves {
    param([object]$Node, [string]$Path, [string]$File)

    if ($Node -is [System.Management.Automation.PSCustomObject]) {
        foreach ($prop in $Node.PSObject.Properties) {
            $childPath = if ($Path) { "$Path.$($prop.Name)" } else { $prop.Name }
            if ($mustBeEmptyKeys -contains $prop.Name -and -not [string]::IsNullOrWhiteSpace([string]$prop.Value)) {
                $script:violations.Add("$File -> '$childPath' is not empty ('$($prop.Value)'). Clear it before release.")
            }
            Test-EmptyLeaves -Node $prop.Value -Path $childPath -File $File
        }
    }
}

foreach ($rel in $mustBeEmpty) {
    $full = Join-Path $RepoRoot $rel
    if (-not (Test-Path $full)) { continue }
    $json = Get-Content $full -Raw | ConvertFrom-Json
    Test-EmptyLeaves -Node $json -Path '' -File $rel
}

# Ensure no local/dev overlay is tracked by git (would leak real values).
Push-Location $RepoRoot
try {
    $tracked = & git ls-files '*appsettings.Local.json' '*appsettings.Development.json' '*.env' '*local.settings.json' 2>$null
    foreach ($t in $tracked) {
        if ($t) { $violations.Add("Tracked secret/overlay file committed: '$t'. It must be git-ignored, not committed.") }
    }
}
finally {
    Pop-Location
}

if ($violations.Count -gt 0) {
    Write-Host "Release secret verification FAILED:" -ForegroundColor Red
    $violations | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}

Write-Host "Release secret verification PASSED: no committed environment values or secrets found." -ForegroundColor Green
exit 0
