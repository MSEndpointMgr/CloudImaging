<#
.SYNOPSIS
    Release gate: fails if any shipped dependency has a known vulnerability at or
    above a severity threshold.

.DESCRIPTION
    Scans NuGet dependencies (direct and transitive) for the given .NET projects and
    npm dependencies for the given package directories, and throws if anything at or
    above -MinimumSeverity is reported. Used by the release workflows so a vulnerable
    dependency halts the run before any artifact is published.

    Only projects that actually ship should be passed in. Test projects are excluded
    on purpose: they are never part of a release artifact, so an advisory in a test
    framework should not block shipping a security fix.

    `dotnet list package --vulnerable` always exits 0, even when it reports
    advisories, so its JSON output is parsed here rather than relying on the exit
    code. `npm audit` does set an exit code, but its JSON is parsed too so both
    ecosystems produce one consistent report.

.PARAMETER Project
    Paths to .csproj/.sln files to scan with `dotnet list package`. Requires a prior
    restore (the workflows restore before calling this).

.PARAMETER NpmDirectory
    Directories containing a package.json/package-lock.json to scan with `npm audit`.

.PARAMETER MinimumSeverity
    Lowest severity that fails the run. Defaults to High, so High and Critical block
    a release while Moderate and Low are reported for visibility only.

.EXAMPLE
    ./.github/scripts/Assert-NoVulnerableDependencies.ps1 `
        -Project src/CloudImaging.OperatorApi/CloudImaging.OperatorApi.csproj `
        -NpmDirectory src/cloud-imaging-portal/server
#>
[CmdletBinding()]
param(
    [string[]] $Project = @(),

    [string[]] $NpmDirectory = @(),

    [ValidateSet('Low', 'Moderate', 'High', 'Critical')]
    [string] $MinimumSeverity = 'High'
)

$ErrorActionPreference = 'Stop'

# npm reports 'info' as well; NuGet uses Low/Moderate/High/Critical. Unknown values
# rank highest so an unrecognised severity is never silently ignored.
$severityRank = @{
    'info'     = 0
    'low'      = 1
    'moderate' = 2
    'high'     = 3
    'critical' = 4
}

function Get-SeverityRank {
    param([string] $Severity)

    $key = "$Severity".ToLowerInvariant()
    if ($severityRank.ContainsKey($key)) { return $severityRank[$key] }
    return [int]::MaxValue
}

$threshold = Get-SeverityRank $MinimumSeverity
$blocking = [System.Collections.Generic.List[object]]::new()
$informational = [System.Collections.Generic.List[object]]::new()

function Add-Finding {
    param(
        [string] $Ecosystem,
        [string] $Scope,
        [string] $Package,
        [string] $Version,
        [string] $Severity,
        [string] $Advisory
    )

    $finding = [pscustomobject]@{
        Ecosystem = $Ecosystem
        Scope     = $Scope
        Package   = $Package
        Version   = $Version
        Severity  = $Severity
        Advisory  = $Advisory
    }

    if ((Get-SeverityRank $Severity) -ge $threshold) { $blocking.Add($finding) }
    else { $informational.Add($finding) }
}

function ConvertFrom-CommandJson {
    param([string[]] $Output)

    # Both tools can precede their JSON with restore/progress lines, so start at the
    # first line that opens the document rather than assuming pure JSON on stdout.
    $text = ($Output -join [Environment]::NewLine)
    $start = $text.IndexOf('{')
    if ($start -lt 0) {
        throw "Expected JSON output but got:`n$text"
    }

    return $text.Substring($start) | ConvertFrom-Json
}

# ── NuGet ────────────────────────────────────────────────────────────────────────
foreach ($path in $Project) {
    Write-Host "Scanning NuGet dependencies: $path"

    $output = & dotnet list $path package --vulnerable --include-transitive --format json 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet list package failed for '$path' (exit $LASTEXITCODE):`n$($output -join [Environment]::NewLine)"
    }

    $report = ConvertFrom-CommandJson -Output $output

    foreach ($reportedProject in @($report.projects)) {
        $projectName = [System.IO.Path]::GetFileNameWithoutExtension($reportedProject.path)

        foreach ($framework in @($reportedProject.frameworks)) {
            $packages = @($framework.topLevelPackages) + @($framework.transitivePackages) |
                Where-Object { $null -ne $_ }

            foreach ($package in $packages) {
                foreach ($vulnerability in @($package.vulnerabilities)) {
                    Add-Finding -Ecosystem 'NuGet' `
                        -Scope $projectName `
                        -Package $package.id `
                        -Version $package.resolvedVersion `
                        -Severity $vulnerability.severity `
                        -Advisory $vulnerability.advisoryurl
                }
            }
        }
    }
}

# ── npm ──────────────────────────────────────────────────────────────────────────
foreach ($directory in $NpmDirectory) {
    Write-Host "Scanning npm dependencies: $directory"

    Push-Location $directory
    try {
        # npm audit exits non-zero when it finds anything at or above --audit-level,
        # which would abort this script before the report is assembled. The exit code
        # is deliberately ignored; the parsed JSON below decides what blocks.
        $output = & npm audit --json 2>&1
        $report = ConvertFrom-CommandJson -Output $output
    }
    finally {
        Pop-Location
    }

    if ($report.error) {
        throw "npm audit failed for '$directory': $($report.error.summary)"
    }

    foreach ($name in $report.vulnerabilities.PSObject.Properties.Name) {
        $vulnerability = $report.vulnerabilities.$name

        # `via` holds either advisory objects (direct finding) or the names of the
        # dependencies that pull the vulnerability in (indirect finding). Only the
        # advisory objects carry a URL to report.
        $advisories = @($vulnerability.via) | Where-Object { $_ -isnot [string] }
        $url = ($advisories | ForEach-Object { $_.url } | Select-Object -Unique) -join ', '

        Add-Finding -Ecosystem 'npm' `
            -Scope $directory `
            -Package $name `
            -Version "$($vulnerability.range)" `
            -Severity $vulnerability.severity `
            -Advisory $url
    }
}

# ── Report ───────────────────────────────────────────────────────────────────────
if ($informational.Count -gt 0) {
    Write-Host ''
    Write-Host "Advisories below the $MinimumSeverity threshold (not blocking):"
    $informational | Format-Table -AutoSize | Out-String | Write-Host
}

if ($blocking.Count -gt 0) {
    Write-Host ''
    $blocking | Format-Table -AutoSize | Out-String | Write-Host

    foreach ($finding in $blocking) {
        Write-Host "::error title=Vulnerable dependency::$($finding.Ecosystem) $($finding.Package) $($finding.Version) in $($finding.Scope) - $($finding.Severity) - $($finding.Advisory)"
    }

    throw "Release halted: $($blocking.Count) dependency advisor$(if ($blocking.Count -eq 1) { 'y' } else { 'ies' }) at or above $MinimumSeverity severity. Update the affected packages (pin transitive dependencies in Directory.Packages.props) and re-run."
}

Write-Host ''
Write-Host "No dependency advisories at or above $MinimumSeverity severity."
