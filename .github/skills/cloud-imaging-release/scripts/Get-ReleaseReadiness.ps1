<#
.SYNOPSIS
    Correlates release tags, release notes, docs and version stamping across the Cloud Imaging release streams before a release.

.DESCRIPTION
    Read-only pre-release check for the Backend/IaC, Media Builder and Client release streams. For each stream it resolves the last released tag, the commits since that tag and the target version, then reports anything that would make the release inconsistent:
    - a target tag that is already taken or not higher than the last release
    - release notes that are missing locally or on origin/main, link the wrong compare range, or reference tags that are neither released nor part of this release
    - notes drafts for versions that were never released and would be skipped
    - docs that reference unreleased tags, or an upgrade guide without a compatibility row for the new backend version
    - a missing Template Spec notice when Bicep or the deployment wizard changed
    - release workflows that no longer stamp the tag into an artifact, or no longer produce the alias releases and asset names the products download
    Nothing is changed: the script only reads the repository and the tags on origin.

.PARAMETER BackendVersion
    Target Backend/IaC version, e.g. 1.1.0. Defaults to the lowest notes draft above the last released tag.

.PARAMETER MediaBuilderVersion
    Target Media Builder version, e.g. 1.1.0. Defaults to the lowest notes draft above the last released tag.

.PARAMETER ClientVersion
    Target Client version, e.g. 1.1.0. Defaults to the lowest notes draft above the last released tag.

.PARAMETER SkipFetch
    Use the refs already present locally instead of fetching origin main and tags first.

.EXAMPLE
    .\Get-ReleaseReadiness.ps1

.EXAMPLE
    .\Get-ReleaseReadiness.ps1 -BackendVersion 1.1.0 -MediaBuilderVersion 1.1.0 -ClientVersion 1.1.0

.NOTES
    FileName:    Get-ReleaseReadiness.ps1
    Author:      MSEndpointMgr
    Contact:     @MSEndpointMgr
    Created:     2026-11-14
    Updated:     2026-11-14

    Version history:
    1.0.0 - (2026-11-14) Script created
    1.0.1 - (2026-11-14) Moved into the cloud-imaging-release skill; an unstamped Media Builder executable is now a Fail
    1.0.2 - (2026-11-14) Default target is the next unreleased draft, not the newest
#>
[CmdletBinding()]
param (
    [Parameter(Mandatory = $false, HelpMessage = "Target Backend/IaC version, e.g. 1.1.0. Defaults to the next unreleased notes draft.")]
    [ValidateNotNullOrEmpty()]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $BackendVersion,
    [Parameter(Mandatory = $false, HelpMessage = "Target Media Builder version, e.g. 1.1.0. Defaults to the next unreleased notes draft.")]
    [ValidateNotNullOrEmpty()]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $MediaBuilderVersion,
    [Parameter(Mandatory = $false, HelpMessage = "Target Client version, e.g. 1.1.0. Defaults to the next unreleased notes draft.")]
    [ValidateNotNullOrEmpty()]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $ClientVersion,
    [Parameter(Mandatory = $false, HelpMessage = "Use the refs already present locally instead of fetching origin main and tags first.")]
    [switch] $SkipFetch
)
Begin {
    $Findings = New-Object -TypeName "System.Collections.Generic.List[System.Object]"
    $StreamSummary = New-Object -TypeName "System.Collections.Generic.List[System.Object]"
    $ResolvedStreams = New-Object -TypeName "System.Collections.Generic.List[System.Object]"
    $PlannedTags = New-Object -TypeName "System.Collections.Generic.List[System.Object]"
    $UnknownDocTags = New-Object -TypeName "System.Collections.Generic.List[System.Object]"
    $UnstampedApis = New-Object -TypeName "System.Collections.Generic.List[System.Object]"
    $MissingContracts = New-Object -TypeName "System.Collections.Generic.List[System.Object]"
    $RepositoryRoot = git -C $PSScriptRoot rev-parse --show-toplevel
    $TagPattern = 'mse-ci-(?:client-|mediabuilder-)?v\d+\.\d+\.\d+'
    $SharedPaths = @("src/CloudImaging.Contracts")

    # Mirrors the release workflows. Listed in the order the tags must be pushed.
    $Streams = @(
        [PSCustomObject]@{
            Key = "backend"
            Name = "Backend/IaC"
            TagPrefix = "mse-ci-v"
            Paths = @("src/CloudImaging.DeviceGatewayApi", "src/CloudImaging.OperatorApi", "src/CloudImaging.ImagingCoreApi", "src/cloud-imaging-portal", "src/deploy")
            RequestedVersion = $BackendVersion
        },
        [PSCustomObject]@{
            Key = "mediabuilder"
            Name = "Media Builder"
            TagPrefix = "mse-ci-mediabuilder-v"
            Paths = @("src/CloudImaging.MediaBuilder", "src/CloudImaging.MediaBuilder.Installer")
            RequestedVersion = $MediaBuilderVersion
        },
        [PSCustomObject]@{
            Key = "client"
            Name = "Client"
            TagPrefix = "mse-ci-client-v"
            Paths = @("src/CloudImaging.Client")
            RequestedVersion = $ClientVersion
        }
    )

    # Strings other components or customers depend on: renaming any of them silently breaks a consumer.
    $ExpectedContracts = @(
        [PSCustomObject]@{ File = ".github/workflows/release-client.yml"; Text = "mse-ci-client-latest"; Why = "the alias Media Builder's Automatic download resolves" },
        [PSCustomObject]@{ File = ".github/workflows/release-client.yml"; Text = "cloud-imaging-client-x64.zip"; Why = "the x64 asset Media Builder downloads" },
        [PSCustomObject]@{ File = ".github/workflows/release-client.yml"; Text = "cloud-imaging-client-arm64.zip"; Why = "the arm64 asset Media Builder downloads" },
        [PSCustomObject]@{ File = ".github/workflows/release-client.yml"; Text = "cloud-imaging-client.zip"; Why = "the legacy asset older Media Builder versions download" },
        [PSCustomObject]@{ File = "src/CloudImaging.MediaBuilder/Services/GitHubReleasesClient.cs"; Text = "mse-ci-client-latest"; Why = "the alias the workflow moves" },
        [PSCustomObject]@{ File = "src/CloudImaging.MediaBuilder/Services/GitHubReleasesClient.cs"; Text = "cloud-imaging-client-"; Why = "the asset names the workflow publishes" },
        [PSCustomObject]@{ File = ".github/workflows/release-iac.yml"; Text = "mse-ci-iac-latest"; Why = "the alias the portal update check reads" },
        [PSCustomObject]@{ File = ".github/workflows/release-iac.yml"; Text = "(latest: "; Why = "the alias title the portal parses the available version from" },
        [PSCustomObject]@{ File = "src/cloud-imaging-portal/server/src/services/updateCheck.ts"; Text = "mse-ci-iac-latest"; Why = "the alias the workflow moves" }
    )

    function Add-Finding {
        param (
            [Parameter(Mandatory = $true, HelpMessage = "Fail, Warning, Info or OK.")]
            [ValidateSet("Fail", "Warning", "Info", "OK")]
            [string] $Status,
            [Parameter(Mandatory = $true, HelpMessage = "Stream the finding belongs to, or All.")]
            [ValidateNotNullOrEmpty()]
            [string] $Stream,
            [Parameter(Mandatory = $true, HelpMessage = "Area that was checked.")]
            [ValidateNotNullOrEmpty()]
            [string] $Area,
            [Parameter(Mandatory = $true, HelpMessage = "What was found and what to do about it.")]
            [ValidateNotNullOrEmpty()]
            [string] $Detail
        )
        $Findings.Add([PSCustomObject]@{
            Status = $Status
            Stream = $Stream
            Area = $Area
            Detail = $Detail
        })
    }

    function Get-PublishStep {
        param (
            [Parameter(Mandatory = $true, HelpMessage = "Content of the release workflow.")]
            [ValidateNotNullOrEmpty()]
            [string] $WorkflowText,
            [Parameter(Mandatory = $true, HelpMessage = "Project path passed to dotnet publish.")]
            [ValidateNotNullOrEmpty()]
            [string] $ProjectPath
        )

        # A publish command spans several continued lines and ends where its output is archived.
        $Start = $WorkflowText.IndexOf("dotnet publish $($ProjectPath)")
        if ($Start -lt 0) {
            return $null
        }
        $Stop = $WorkflowText.IndexOf("Compress-Archive", $Start)
        if ($Stop -lt 0) {
            $Stop = [Math]::Min($WorkflowText.Length, $Start + 400)
        }
        return $WorkflowText.Substring($Start, $Stop - $Start)
    }
}
Process {
    try {
        if (-not $SkipFetch) {
            git -C $RepositoryRoot fetch origin main --tags --quiet 2>$null
            if ($LASTEXITCODE -ne 0) {
                Write-Warning -Message "Could not fetch from origin; continuing with the refs already present locally."
            }
        }

        # Tags on origin, not local tags, define what has been released.
        $RemoteTags = @(git -C $RepositoryRoot ls-remote --tags origin 2>$null | ForEach-Object { (($_ -split "\s+")[1] -replace '^refs/tags/', '') -replace '\^\{\}$', '' } | Sort-Object -Unique)
        if ($RemoteTags.Count -eq 0) {
            Write-Warning -Message "Could not list the tags on origin; using local tags instead."
            $RemoteTags = @(git -C $RepositoryRoot tag --list "mse-ci-*")
        }

        $StreamIndex = 0
        foreach ($Stream in $Streams) {
            $StreamIndex++
            Write-Progress -Activity "Resolving release streams" -Status $Stream.Name -PercentComplete (($StreamIndex / $Streams.Count) * 100)
            $ReleasedPattern = "^$([regex]::Escape($Stream.TagPrefix))\d+\.\d+\.\d+$"
            $Released = @($RemoteTags | Where-Object { $_ -match $ReleasedPattern } | ForEach-Object { [version]($_ -replace '^.*v', '') } | Sort-Object -Descending)
            $LastVersion = $null
            $LastTag = $null
            if ($Released.Count -gt 0) {
                $LastVersion = $Released[0]
                $LastTag = "$($Stream.TagPrefix)$($LastVersion)"
            }

            # A notes draft above the last release is a version someone intended to ship.
            $NotesDirectory = Join-Path -Path $RepositoryRoot -ChildPath ".github/release-notes/$($Stream.Key)"
            $Drafts = @(Get-ChildItem -Path $NotesDirectory -Filter "v*.md" -File -ErrorAction SilentlyContinue | ForEach-Object { if ($_.Name -match '^v(\d+\.\d+\.\d+)\.md$') { [version]$Matches[1] } } | Where-Object { $null -eq $LastVersion -or $_ -gt $LastVersion } | Sort-Object)
            $Target = $null
            if ($Stream.RequestedVersion) {
                $Target = [version]$Stream.RequestedVersion
            }
            elseif ($Drafts.Count -gt 0) {
                $Target = $Drafts[0]
            }

            $Range = "origin/main"
            if ($LastTag) {
                $Range = "$($LastTag)..origin/main"
            }
            $Commits = [int](git -C $RepositoryRoot rev-list --count $Range -- $Stream.Paths)
            $SharedCommits = [int](git -C $RepositoryRoot rev-list --count $Range -- $SharedPaths)
            $TargetTag = $null
            $NotesPath = $null
            if ($Target) {
                $TargetTag = "$($Stream.TagPrefix)$($Target)"
                $NotesPath = ".github/release-notes/$($Stream.Key)/v$($Target).md"
                $PlannedTags.Add($TargetTag)
            }
            $ResolvedStreams.Add([PSCustomObject]@{
                Definition = $Stream
                LastVersion = $LastVersion
                LastTag = $LastTag
                Target = $Target
                TargetTag = $TargetTag
                NotesPath = $NotesPath
                Drafts = $Drafts
                Commits = $Commits
                SharedCommits = $SharedCommits
            })
        }
        Write-Progress -Activity "Resolving release streams" -Completed

        foreach ($Item in $ResolvedStreams) {
            $Name = $Item.Definition.Name
            $Since = $Item.LastTag ?? "the first commit"
            $NotesText = $null
            $NotesState = "-"

            if (-not $Item.Target) {
                if ($Item.Commits -gt 0 -or $Item.SharedCommits -gt 0) {
                    Add-Finding -Status Warning -Stream $Name -Area "Version" -Detail "$($Item.Commits) commits (plus $($Item.SharedCommits) in Contracts) since $($Since), but no target version. Pass one or draft release notes, or confirm the stream is left out."
                }
                else {
                    Add-Finding -Status Info -Stream $Name -Area "Version" -Detail "No changes since $($Since); not part of this release."
                }
            }
            else {
                if ($Item.LastVersion -and $Item.Target -le $Item.LastVersion) {
                    Add-Finding -Status Fail -Stream $Name -Area "Version" -Detail "Target $($Item.TargetTag) is not higher than the last release $($Item.LastTag)."
                }
                elseif ($RemoteTags -contains $Item.TargetTag) {
                    Add-Finding -Status Fail -Stream $Name -Area "Version" -Detail "$($Item.TargetTag) already exists on origin."
                }
                else {
                    Add-Finding -Status OK -Stream $Name -Area "Version" -Detail "$($Since) to $($Item.TargetTag): $($Item.Commits) commits, plus $($Item.SharedCommits) in Contracts."
                }
                if ($Item.Commits -eq 0 -and $Item.SharedCommits -eq 0) {
                    Add-Finding -Status Warning -Stream $Name -Area "Version" -Detail "No changes since $($Since): $($Item.TargetTag) would ship the same build under a new version."
                }

                $NotesFullPath = Join-Path -Path $RepositoryRoot -ChildPath $Item.NotesPath
                if (-not (Test-Path -Path $NotesFullPath)) {
                    $NotesState = "missing"
                    Add-Finding -Status Fail -Stream $Name -Area "Release notes" -Detail "$($Item.NotesPath) is missing. The release workflow stops before building without it."
                }
                else {
                    $NotesState = "present"
                    $NotesText = Get-Content -Path $NotesFullPath -Raw
                    git -C $RepositoryRoot cat-file -e "origin/main:$($Item.NotesPath)" 2>$null
                    if ($LASTEXITCODE -ne 0) {
                        Add-Finding -Status Warning -Stream $Name -Area "Release notes" -Detail "$($Item.NotesPath) is not on origin/main yet. Merge it before pushing $($Item.TargetTag)."
                    }
                    else {
                        git -C $RepositoryRoot diff --quiet origin/main -- $Item.NotesPath 2>$null
                        if ($LASTEXITCODE -ne 0) {
                            Add-Finding -Status Warning -Stream $Name -Area "Release notes" -Detail "Local $($Item.NotesPath) differs from origin/main. The release uses the origin/main copy."
                        }
                        else {
                            Add-Finding -Status OK -Stream $Name -Area "Release notes" -Detail "$($Item.NotesPath) is on origin/main."
                        }
                    }
                }

                # Drafts for other unreleased versions would never become a release body.
                foreach ($Draft in ($Item.Drafts | Where-Object { $_ -ne $Item.Target })) {
                    if ($Draft -lt $Item.Target) {
                        Add-Finding -Status Warning -Stream $Name -Area "Release notes" -Detail "v$($Draft).md was drafted but v$($Draft) was never released. Fold its content into v$($Item.Target).md and delete it, or it appears in no release."
                    }
                    else {
                        Add-Finding -Status Warning -Stream $Name -Area "Release notes" -Detail "v$($Draft).md is drafted above the target $($Item.TargetTag). The tag is cut from origin/main, so whatever of it is merged ships in v$($Item.Target): fold it into v$($Item.Target).md and delete it, and replace v$($Draft) in every reference."
                    }
                }

                if ($NotesText) {
                    $CompareMatch = [regex]::Match($NotesText, 'compare/(?<from>[^\s\)\]]+?)\.\.\.(?<to>[^\s\)\]]+)')
                    if (-not $CompareMatch.Success) {
                        Add-Finding -Status Warning -Stream $Name -Area "Release notes" -Detail "No Full changes compare link. Expected compare/$($Item.LastTag)...$($Item.TargetTag)."
                    }
                    elseif ($Item.LastTag -and ($CompareMatch.Groups['from'].Value -ne $Item.LastTag -or $CompareMatch.Groups['to'].Value -ne $Item.TargetTag)) {
                        Add-Finding -Status Fail -Stream $Name -Area "Release notes" -Detail "Compare link is $($CompareMatch.Groups['from'].Value)...$($CompareMatch.Groups['to'].Value); expected $($Item.LastTag)...$($Item.TargetTag)."
                    }
                    else {
                        Add-Finding -Status OK -Stream $Name -Area "Release notes" -Detail "Compare link covers $($Item.LastTag)...$($Item.TargetTag)."
                    }

                    # Minimum versions of other streams must point at tags that exist or ship in this release.
                    $Referenced = @([regex]::Matches($NotesText, $TagPattern) | ForEach-Object { $_.Value } | Sort-Object -Unique)
                    $Unknown = @($Referenced | Where-Object { $RemoteTags -notcontains $_ -and $PlannedTags -notcontains $_ })
                    if ($Unknown.Count -gt 0) {
                        Add-Finding -Status Fail -Stream $Name -Area "Cross-stream versions" -Detail "Notes reference tags that are neither released nor part of this release: $($Unknown -join ', '). Planned: $($PlannedTags -join ', ')."
                    }
                    else {
                        Add-Finding -Status OK -Stream $Name -Area "Cross-stream versions" -Detail "All $($Referenced.Count) referenced tags are released or planned."
                    }
                }

                # The upgrade script does not republish the Template Spec, so customers must be told.
                if ($Item.Definition.Key -eq "backend" -and $Item.LastTag) {
                    $TemplateChanges = @(git -C $RepositoryRoot diff --name-only $Item.LastTag origin/main -- src/deploy/bicep src/deploy/uiFormDefinition.json)
                    if ($TemplateChanges.Count -gt 0) {
                        if ($NotesText -and $NotesText -match 'Template Spec') {
                            Add-Finding -Status OK -Stream $Name -Area "Template Spec" -Detail "$($TemplateChanges.Count) Bicep or wizard files changed and the notes carry the Template Spec notice."
                        }
                        else {
                            Add-Finding -Status Fail -Stream $Name -Area "Template Spec" -Detail "$($TemplateChanges.Count) Bicep or wizard files changed since $($Item.LastTag), but the notes have no Template Spec notice."
                        }
                        if (@($TemplateChanges | Where-Object { $_ -like "*.bicep" }).Count -gt 0 -and $TemplateChanges -notcontains "src/deploy/bicep/main.json") {
                            Add-Finding -Status Warning -Stream $Name -Area "Template Spec" -Detail "Bicep changed but main.json did not. Run az bicep build for main.bicep and commit main.json if it changes."
                        }
                    }
                }
            }

            $StreamSummary.Add([PSCustomObject]@{
                Stream = $Name
                Released = $Item.LastTag ?? "-"
                Target = $Item.TargetTag ?? "-"
                Commits = $Item.Commits
                Contracts = $Item.SharedCommits
                Notes = $NotesState
                UnreleasedDrafts = (($Item.Drafts | ForEach-Object { "v$($_)" }) -join ", ")
            })
        }

        # Docs must not point customers at tags that do not exist.
        $DocFiles = @(Get-ChildItem -Path (Join-Path -Path $RepositoryRoot -ChildPath "docs") -Filter "*.md" -File) + @(Get-Item -Path (Join-Path -Path $RepositoryRoot -ChildPath "README.md"))
        foreach ($Doc in $DocFiles) {
            foreach ($Line in (Select-String -Path $Doc.FullName -Pattern $TagPattern -AllMatches)) {
                foreach ($Match in $Line.Matches) {
                    if ($RemoteTags -notcontains $Match.Value -and $PlannedTags -notcontains $Match.Value) {
                        $UnknownDocTags.Add("$($Doc.Name):$($Line.LineNumber) $($Match.Value)")
                    }
                }
            }
        }
        if ($UnknownDocTags.Count -gt 0) {
            Add-Finding -Status Warning -Stream "All" -Area "Docs" -Detail "Docs reference tags that are neither released nor planned: $($UnknownDocTags -join '; ')."
        }
        else {
            Add-Finding -Status OK -Stream "All" -Area "Docs" -Detail "Every tag referenced in docs and README is released or planned."
        }

        $Backend = $ResolvedStreams | Where-Object { $_.Definition.Key -eq "backend" }
        $UpgradeText = Get-Content -Path (Join-Path -Path $RepositoryRoot -ChildPath "docs/upgrade-instructions.md") -Raw
        $CompatibilitySection = [regex]::Match($UpgradeText, '(?ms)^#{2,4} Version Compatibility\s*$(?<body>.*?)(?=^#{1,4} |\z)')
        if (-not $CompatibilitySection.Success) {
            Add-Finding -Status Warning -Stream "All" -Area "Docs" -Detail "docs/upgrade-instructions.md has no Version Compatibility section. Add one under Before You Start with a row per backend version and the minimum Media Builder and Client versions."
        }
        elseif ($Backend.Target -and $CompatibilitySection.Groups['body'].Value -notmatch [regex]::Escape("$($Backend.Target)")) {
            Add-Finding -Status Warning -Stream "All" -Area "Docs" -Detail "Version Compatibility has no row for $($Backend.TargetTag)."
        }
        else {
            Add-Finding -Status OK -Stream "All" -Area "Docs" -Detail "Version Compatibility section is present and covers the backend target."
        }

        # Every artifact that reports a version must get it from the tag at build time.
        $ClientWorkflow = Get-Content -Path (Join-Path -Path $RepositoryRoot -ChildPath ".github/workflows/release-client.yml") -Raw
        $MediaBuilderWorkflow = Get-Content -Path (Join-Path -Path $RepositoryRoot -ChildPath ".github/workflows/release-mediabuilder.yml") -Raw
        $BackendWorkflow = Get-Content -Path (Join-Path -Path $RepositoryRoot -ChildPath ".github/workflows/release-iac.yml") -Raw

        $ClientPublish = Get-PublishStep -WorkflowText $ClientWorkflow -ProjectPath "src/CloudImaging.Client/CloudImaging.Client.csproj"
        if ($ClientPublish -and $ClientPublish -match '-p:Version=') {
            Add-Finding -Status OK -Stream "Client" -Area "Version stamping" -Detail "Client executable gets the tag version (-p:Version)."
        }
        else {
            Add-Finding -Status Fail -Stream "Client" -Area "Version stamping" -Detail "release-client.yml no longer passes -p:Version to the Client publish, so boot media and Autopilot records report the default version."
        }

        if ($MediaBuilderWorkflow -match 'MsiProductVersion=') {
            Add-Finding -Status OK -Stream "Media Builder" -Area "Version stamping" -Detail "MSI ProductVersion follows the tag."
        }
        else {
            Add-Finding -Status Fail -Stream "Media Builder" -Area "Version stamping" -Detail "release-mediabuilder.yml no longer passes MsiProductVersion, so every MSI is 0.0.1 and in-place upgrades stop working."
        }
        $MediaBuilderPublish = Get-PublishStep -WorkflowText $MediaBuilderWorkflow -ProjectPath "src/CloudImaging.MediaBuilder/CloudImaging.MediaBuilder.csproj"
        if ($MediaBuilderPublish -and $MediaBuilderPublish -match '-p:Version=') {
            Add-Finding -Status OK -Stream "Media Builder" -Area "Version stamping" -Detail "Media Builder executable gets the tag version (-p:Version)."
        }
        else {
            Add-Finding -Status Fail -Stream "Media Builder" -Area "Version stamping" -Detail "release-mediabuilder.yml no longer passes -p:Version to the Media Builder publish, so the executable and the tool version it records on every USB it prepares fall back to 1.0.0.0."
        }

        if ($BackendWorkflow -match 'DEPLOYED_VERSION' -and $BackendWorkflow -match 'version\.txt') {
            Add-Finding -Status OK -Stream "Backend/IaC" -Area "Version stamping" -Detail "Portal backend (DEPLOYED_VERSION) and bundle (version.txt) get the tag."
        }
        else {
            Add-Finding -Status Fail -Stream "Backend/IaC" -Area "Version stamping" -Detail "release-iac.yml no longer stamps DEPLOYED_VERSION or version.txt. The portal update check and publish-template-spec.ps1 depend on them."
        }
        foreach ($Api in @("DeviceGatewayApi", "OperatorApi", "ImagingCoreApi")) {
            $ApiPublish = Get-PublishStep -WorkflowText $BackendWorkflow -ProjectPath "src/CloudImaging.$($Api)/CloudImaging.$($Api).csproj"
            if ($ApiPublish -and $ApiPublish -notmatch '-p:Version=') {
                $UnstampedApis.Add($Api)
            }
        }
        if ($UnstampedApis.Count -gt 0) {
            Add-Finding -Status Info -Stream "Backend/IaC" -Area "Version stamping" -Detail "Function Apps are published without a version ($($UnstampedApis -join ', ')). Nothing reads it today."
        }

        foreach ($Contract in $ExpectedContracts) {
            $ContractPath = Join-Path -Path $RepositoryRoot -ChildPath $Contract.File
            if (-not (Test-Path -Path $ContractPath) -or -not (Select-String -Path $ContractPath -Pattern $Contract.Text -SimpleMatch -Quiet)) {
                $MissingContracts.Add("$($Contract.File) no longer contains '$($Contract.Text)' ($($Contract.Why))")
            }
        }
        if ($MissingContracts.Count -gt 0) {
            Add-Finding -Status Fail -Stream "All" -Area "Release contracts" -Detail ($MissingContracts -join '; ')
        }
        else {
            Add-Finding -Status OK -Stream "All" -Area "Release contracts" -Detail "Alias releases and asset names match what Media Builder and the portal update check read."
        }
    }
    catch [System.Exception] {
        Write-Warning -Message "Release readiness check failed: $($_.Exception.Message)"
    }
}
End {
    $StatusOrder = @{ Fail = 0; Warning = 1; Info = 2; OK = 3 }
    $FailCount = @($Findings | Where-Object { $_.Status -eq "Fail" }).Count
    $WarningCount = @($Findings | Where-Object { $_.Status -eq "Warning" }).Count
    Write-Output "Release streams"
    Write-Output ($StreamSummary | Format-Table -AutoSize | Out-String -Width 250).TrimEnd()
    Write-Output ""
    Write-Output "Findings"
    Write-Output ($Findings | Sort-Object -Property @{ Expression = { $StatusOrder[$_.Status] } }, Stream | Format-Table -Property Status, Stream, Area, Detail -AutoSize -Wrap | Out-String -Width 250).TrimEnd()
    Write-Output ""
    Write-Output "Result: $($FailCount) to fix before tagging, $($WarningCount) to review. Tag order: Backend/IaC, then Media Builder, then Client."
}
