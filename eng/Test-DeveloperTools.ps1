#requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'RoadmapChecks.ps1')
function Assert-Vw {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) { throw $Message }
}
function New-VwFixture {
    return [pscustomobject]@{
        Nodes = @(
            [pscustomobject]@{ id = 'status'; tags = @('project:status', 'current-phase:t2') },
            [pscustomobject]@{ id = 'old'; tags = @('roadmap:phase', 'phase:t1', 'status:complete') },
            [pscustomobject]@{ id = 'now'; tags = @('roadmap:phase', 'phase:t2', 'status:current', 'estimate:medium') },
            [pscustomobject]@{ id = 'later'; tags = @('roadmap:phase', 'phase:t3', 'status:pending') }
        )
        Edges = @([pscustomobject]@{ source = 'status'; target = 'now'; relationship = 'current-phase' })
    }
}
$vwGood = New-VwFixture
Assert-Vw (@(Get-VwRoadmapErrors $vwGood.Nodes $vwGood.Edges).Count -eq 0) 'Valid roadmap was rejected.'
foreach ($vwIndex in @(1, 3)) {
    $vwBad = New-VwFixture
    $vwBad.Nodes[$vwIndex].tags += 'estimate:large'
    Assert-Vw ((@(Get-VwRoadmapErrors $vwBad.Nodes $vwBad.Edges) -join ' ') -match 'only the current phase') 'Stray estimate was accepted.'
}
$vwMutations = @(
    { param($f) $f.Nodes[3].tags = @('roadmap:phase', 'phase:t3', 'status:current', 'estimate:large') },
    { param($f) $f.Nodes[2].tags = @('roadmap:phase', 'phase:t2', 'status:current') },
    { param($f) $f.Nodes[2].tags += 'status:pending' },
    { param($f) $f.Nodes[0].tags = @('project:status', 'current-phase:t3') },
    { param($f) $f.Edges[0].target = 'later' },
    { param($f) $f.Nodes[2].tags += 'estimate:small' },
    { param($f) $f.Nodes[2].tags = @('roadmap:phase', 'phase:t2', 'status:current', 'estimate:huge') },
    { param($f) $f.Nodes[3].tags += 'project:status' }
)
foreach ($vwMutation in $vwMutations) {
    $vwBad = New-VwFixture
    & $vwMutation $vwBad
    Assert-Vw (@(Get-VwRoadmapErrors $vwBad.Nodes $vwBad.Edges).Count -gt 0) 'Malformed roadmap was accepted.'
}
$global:LASTEXITCODE = 0
Write-Host 'Developer tooling regression checks passed (11 roadmap cases).'
