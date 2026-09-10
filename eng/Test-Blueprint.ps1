#requires -Version 5.1
[CmdletBinding()]
param([string] $Path)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $Path) { $Path = Join-Path $PSScriptRoot '../ValidatedWorld.Blueprint.vw.db' }
. (Join-Path $PSScriptRoot 'RoadmapChecks.ps1')
$vwCliProject = Join-Path $PSScriptRoot '../src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj'
function Read-VwCliJson {
    param([string[]] $Arguments)
    $vwOutput = & dotnet run --no-restore --no-build --project $vwCliProject -- @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Blueprint read failed: $($Arguments -join ' ')" }
    return ($vwOutput -join "`n" | ConvertFrom-Json)
}
function Read-VwPages {
    param([string] $Kind)
    $vwCursor = $null
    do {
        $vwArguments = @('read', $Kind, $Path, '--limit', '100')
        if ($null -ne $vwCursor) { $vwArguments += @('--cursor', $vwCursor) }
        $vwPage = Read-VwCliJson $vwArguments
        if ($null -ne $vwPage.omission -and
            ($vwPage.omission.reason -ne 'outputLimit' -or $null -eq $vwPage.nextCursor)) {
            throw 'Blueprint query was incomplete without a continuation.'
        }
        $vwPage.items
        $vwCursor = $vwPage.nextCursor
    } while ($null -ne $vwCursor)
}
$vwBefore = Read-VwCliJson @('project', 'verify', $Path)
if (-not $vwBefore.isValid) { throw 'Blueprint structural verification failed.' }
# Full evaluation is local; only diagnostics leave this script. Every CLI page is bounded.
$vwNodes = @(Read-VwPages 'nodes')
$vwEdges = @(Read-VwPages 'edges')
$vwAfter = Read-VwCliJson @('project', 'verify', $Path)
if ($vwBefore.stateFingerprint -ne $vwAfter.stateFingerprint) { throw 'Blueprint changed during validation; run again against a stable file.' }
$vwErrors = @(Get-VwRoadmapErrors $vwNodes $vwEdges)
if ($vwErrors.Count -gt 0) { throw ("Blueprint roadmap check failed:`n" + ($vwErrors -join "`n")) }
Write-Host 'Blueprint structure and roadmap conventions passed (one current phase, one estimate, matching status pointers).'
