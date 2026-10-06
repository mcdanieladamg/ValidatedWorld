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
            [pscustomobject]@{ id = 'status'; tags = @('project:status', 'status:active', 'current-phase:t2') },
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
    { param($f) $f.Nodes[0].tags = @('project:status', 'status:active', 'current-phase:t3') },
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
function New-VwFinishedFixture {
    $vwFixture = New-VwFixture
    $vwFixture.Nodes[0].tags = @('project:status', 'status:finished')
    $vwFixture.Nodes[2].tags = @('roadmap:phase', 'phase:t2', 'status:complete')
    $vwFixture.Nodes[3].tags = @('roadmap:phase', 'phase:t3', 'status:complete')
    $vwFixture.Edges = @()
    return $vwFixture
}
$vwFinished = New-VwFinishedFixture
Assert-Vw (@(Get-VwRoadmapErrors $vwFinished.Nodes $vwFinished.Edges).Count -eq 0) 'Finished roadmap was rejected.'
$vwFinishedMutations = @(
    { param($f) $f.Nodes[3].tags = @('roadmap:phase', 'phase:t3', 'status:pending') },
    { param($f) $f.Nodes[2].tags = @('roadmap:phase', 'phase:t2', 'status:current', 'estimate:medium') },
    { param($f) $f.Nodes[0].tags += 'current-phase:t2' },
    { param($f) $f.Edges = @([pscustomobject]@{ source = 'status'; target = 'now'; relationship = 'current-phase' }) },
    { param($f) $f.Nodes[0].tags = @('project:status', 'status:active') },
    { param($f) $f.Nodes[0].tags = @('project:status') },
    { param($f) $f.Nodes[0].tags += 'status:active' }
)
foreach ($vwMutation in $vwFinishedMutations) {
    $vwBad = New-VwFinishedFixture
    & $vwMutation $vwBad
    Assert-Vw (@(Get-VwRoadmapErrors $vwBad.Nodes $vwBad.Edges).Count -gt 0) 'Malformed finished roadmap was accepted.'
}
$vwPlanning = New-VwFinishedFixture
$vwPlanning.Nodes[0].tags = @('project:status', 'status:planning')
$vwPlanning.Nodes[3].tags = @('roadmap:phase', 'phase:t3', 'status:pending')
Assert-Vw (@(Get-VwRoadmapErrors $vwPlanning.Nodes $vwPlanning.Edges).Count -eq 0) 'Planning roadmap was rejected.'
$vwPlanning.Nodes[2].tags = @('roadmap:phase', 'phase:t2', 'status:current', 'estimate:medium')
Assert-Vw (@(Get-VwRoadmapErrors $vwPlanning.Nodes $vwPlanning.Edges).Count -gt 0) 'Planning roadmap with a current phase was accepted.'
$global:LASTEXITCODE = 0
Write-Host 'Developer tooling regression checks passed (21 roadmap cases).'
