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
$vwTemplatePath = Join-Path ([IO.Path]::GetTempPath()) ("validated-world-code-template-{0}.json" -f [guid]::NewGuid().ToString('N'))
try {
    $null = Read-VwCliJson @('template', 'export', 'code-development', $vwTemplatePath)
    $vwTemplate = Get-Content -Raw -LiteralPath $vwTemplatePath | ConvertFrom-Json
    $vwTemplateGovernance = @($vwTemplate.nodes | Where-Object {
        $_.kind -eq 'validation-rule' -or $_.kind -eq 'validation-view'
    })
    $vwBlueprintGovernance = @($vwNodes | Where-Object {
        $_.kind -eq 'validation-rule' -or $_.kind -eq 'validation-view'
    })

    function Get-VwNodeSignature {
        param($Node)
        $vwAttributes = @($Node.attributes | Sort-Object name | ForEach-Object {
            $vwAttribute = $_
            $vwKind = switch ([string]$vwAttribute.value.kind) { '0' { 'text' } '1' { 'integer' } default { [string]$vwAttribute.value.kind } }
            '{0}:{1}:{2}:{3}:{4}:{5}' -f $vwAttribute.name, $vwKind, $vwAttribute.value.text,
                $vwAttribute.value.integer, $vwAttribute.value.boolean, $vwAttribute.value.instant
        })
        return '{0}|{1}|{2}|{3}|{4}' -f $Node.id, $Node.kind, $Node.text,
            (@($Node.tags | Sort-Object) -join ','), ($vwAttributes -join ',')
    }

    $vwExpectedSignatures = @($vwTemplateGovernance | ForEach-Object { Get-VwNodeSignature $_ } | Sort-Object)
    $vwActualSignatures = @($vwBlueprintGovernance | ForEach-Object { Get-VwNodeSignature $_ } | Sort-Object)
    if (($vwExpectedSignatures -join "`n") -ne ($vwActualSignatures -join "`n")) {
        $vwDifference = Compare-Object $vwExpectedSignatures $vwActualSignatures | Out-String
        throw "Blueprint active roadmap views and rules differ from the code-development template baseline:`n$vwDifference"
    }

    $vwGovernanceIds = @($vwTemplateGovernance.id)
    $vwExpectedParents = @($vwTemplate.edges | Where-Object {
        $_.source -in $vwGovernanceIds -and $_.relationship -eq 'scope-parent'
    } | ForEach-Object { '{0}|{1}' -f $_.source, $_.target } | Sort-Object)
    $vwActualParents = @($vwEdges | Where-Object {
        $_.source -in $vwGovernanceIds -and $_.relationship -eq 'scope-parent'
    } | ForEach-Object { '{0}|{1}' -f $_.source, $_.target } | Sort-Object)
    if (($vwExpectedParents -join "`n") -ne ($vwActualParents -join "`n")) {
        throw 'Blueprint rule/view scope placement differs from the code-development template baseline.'
    }
}
finally {
    if (Test-Path -LiteralPath $vwTemplatePath) { Remove-Item -LiteralPath $vwTemplatePath -Force }
}
Write-Host 'Blueprint structure, roadmap rules, and code-development template parity passed.'
