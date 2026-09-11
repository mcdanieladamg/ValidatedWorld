#requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'RoadmapChecks.ps1')
. (Join-Path $PSScriptRoot 'ReleaseChecks.ps1')
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
$vwTemp = Join-Path ([IO.Path]::GetTempPath()) ('vw-tool-check-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($vwTemp) | Out-Null
try {
    Set-Content -LiteralPath (Join-Path $vwTemp 'package.zip') -Value 'test package'
    $vwHash = (Get-FileHash -LiteralPath (Join-Path $vwTemp 'package.zip') -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath (Join-Path $vwTemp 'SHA256SUMS.txt') -Value "$vwHash  package.zip"
    Test-VwReleaseHashes $vwTemp @('package.zip')
    foreach ($vwContents in @('', "$vwHash  ../package.zip", "$vwHash  package.zip`n$vwHash  package.zip", "$(('0' * 64))  package.zip")) {
        Set-Content -LiteralPath (Join-Path $vwTemp 'SHA256SUMS.txt') -Value $vwContents
        $vwRejected = $false
        try { Test-VwReleaseHashes $vwTemp @('package.zip') } catch { $vwRejected = $true }
        Assert-Vw $vwRejected 'Missing, duplicate, escaping, or incorrect checksum was accepted.'
    }
}
finally {
    $vwResolved = [IO.Path]::GetFullPath($vwTemp)
    $vwParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $vwResolved.StartsWith($vwParent, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe temporary cleanup path.' }
    Remove-Item -LiteralPath $vwResolved -Recurse -Force
}
# Read-only Git results are mocked: no test creates commits or changes the real index.
$vwTestRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$vwTestRevision = 'a' * 40
$vwTestDirty = $false
$vwTestGitFailure = $false
function git {
    param([Parameter(ValueFromRemainingArguments)] [string[]] $Arguments)
    $global:LASTEXITCODE = 0
    if ($vwTestGitFailure) { $global:LASTEXITCODE = 1; return }
    if ($Arguments -contains 'rev-parse') { return $vwTestRevision }
    if ($Arguments -contains 'status') { if ($vwTestDirty) { return ' M README.md' }; return }
    throw 'Unexpected Git command in version test.'
}
try {
    $vwFirst = Get-VwReleaseVersion $vwTestRoot
    Assert-Vw ($vwFirst -ceq (Get-VwReleaseVersion $vwTestRoot)) 'Same commit produced different versions.'
    Assert-Vw ($vwFirst.EndsWith('-dev.g' + ('a' * 40))) 'Version omitted the full commit ID.'
    $vwTestRevision = 'b' * 40
    Assert-Vw ($vwFirst -cne (Get-VwReleaseVersion $vwTestRoot)) 'Different commits shared a version.'
    $vwTestDirty = $true
    $vwRejected = $false
    try { Get-VwReleaseVersion $vwTestRoot | Out-Null } catch { $vwRejected = $true }
    Assert-Vw $vwRejected 'Dirty checkout received an automatic commit version.'
    Assert-Vw ((Get-VwReleaseVersion $vwTestRoot '0.2.0-local.1') -ceq '0.2.0-local.1') 'Explicit local test version was rejected.'
    $vwTestDirty = $false
    $vwRejected = $false
    try { Get-VwReleaseVersion $vwTestRoot $vwFirst | Out-Null } catch { $vwRejected = $true }
    Assert-Vw $vwRejected 'A different commit version was accepted.'
    $vwTestGitFailure = $true
    $vwRejected = $false
    try { Get-VwReleaseVersion $vwTestRoot | Out-Null } catch { $vwRejected = $true }
    Assert-Vw $vwRejected 'Missing Git HEAD received an automatic version.'
}
finally {
    Remove-Item Function:\git
    $global:LASTEXITCODE = 0
}
Write-Host 'Developer tooling regression checks passed (11 roadmap cases, 5 checksum cases, 7 version checks).'
