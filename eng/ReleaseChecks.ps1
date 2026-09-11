function Get-VwReleaseVersion {
    param([string] $RepositoryRoot, [string] $Version)
    if ($Version -and $Version -cnotmatch '-dev\.g[0-9a-f]+$') { return $Version }
    $vwRevision = & git -C $RepositoryRoot rev-parse --verify HEAD
    if ($LASTEXITCODE -ne 0 -or $vwRevision -cnotmatch '^(?:[0-9a-f]{40}|[0-9a-f]{64})$') {
        throw 'Automatic versioning requires a Git checkout with a committed HEAD.'
    }
    $vwChanges = & git -C $RepositoryRoot status --porcelain --untracked-files=normal
    if ($LASTEXITCODE -ne 0) { throw 'Could not check the Git working tree.' }
    if ($vwChanges) {
        throw 'Automatic versioning requires a clean checkout. Commit/merge your changes first, or use an explicit -Version such as 0.2.0-local.1 for an uncommitted test build.'
    }
    [xml] $vwProps = Get-Content -Raw -LiteralPath (Join-Path $RepositoryRoot 'Directory.Build.props')
    $vwPrefix = [string] $vwProps.Project.PropertyGroup.VersionPrefix
    if ($vwPrefix -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
        throw 'Directory.Build.props must contain one numeric VersionPrefix.'
    }
    $vwComputed = "$vwPrefix-dev.g$vwRevision"
    if ($Version -and $Version -cne $vwComputed) { throw 'Commit-derived version does not match this checkout.' }
    return $vwComputed
}

function Test-VwReleaseHashes {
    param([string] $Directory, [string[]] $RequiredNames)
    $vwHashes = @{}
    foreach ($vwLine in Get-Content -LiteralPath (Join-Path $Directory 'SHA256SUMS.txt')) {
        if ($vwLine -cnotmatch '^([0-9a-f]{64})  ([A-Za-z0-9][A-Za-z0-9._-]*)$') {
            throw 'Invalid checksum entry: expected SHA-256 and a plain filename.'
        }
        $vwName = $Matches[2]
        if ($vwHashes.ContainsKey($vwName)) { throw "Duplicate checksum entry: $vwName" }
        $vwHashes[$vwName] = $Matches[1]
    }
    foreach ($vwName in $RequiredNames) {
        if (-not $vwHashes.ContainsKey($vwName)) { throw "Missing checksum entry: $vwName" }
    }
    foreach ($vwName in $vwHashes.Keys) {
        $vwActual = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $Directory $vwName)).Hash.ToLowerInvariant()
        if ($vwActual -cne $vwHashes[$vwName]) { throw "Checksum mismatch: $vwName" }
    }
}
