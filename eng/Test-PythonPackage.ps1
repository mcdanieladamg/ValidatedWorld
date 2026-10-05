#requires -Version 5.1

[CmdletBinding()]
param(
    [string] $PackagesDirectory,
    [string] $PythonExecutable
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($PackagesDirectory)) { $PackagesDirectory = Join-Path $root 'artifacts/python-release/0.3.0-dev' }
$packages = [IO.Path]::GetFullPath($PackagesDirectory)
$hashes = Join-Path $packages 'SHA256SUMS.txt'
if (-not (Test-Path -LiteralPath $hashes -PathType Leaf)) { throw "Missing package hashes: $hashes" }
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('ValidatedWorld-python-package-' + [Guid]::NewGuid().ToString('N'))
$oldPythonPath = $env:PYTHONPATH
New-Item -ItemType Directory -Force -Path $temporary | Out-Null
try {
    $python = if ([string]::IsNullOrWhiteSpace($PythonExecutable)) { (Get-Command python -ErrorAction Stop).Source } else { (Resolve-Path -LiteralPath $PythonExecutable).Path }
    # Avoid quote-bearing Python expressions here. Windows PowerShell 5.1's
    # native argument handling can remove the quotes inside an f-string.
    $version = & $python -c 'import sys; print(sys.version_info.major, sys.version_info.minor, sep=chr(46))'
    if ($LASTEXITCODE -ne 0 -or [version]$version -lt [version]'3.12') { throw "Python 3.12 or newer is required; found $version" }
    foreach ($archive in Get-ChildItem -LiteralPath $packages -Filter '*.zip' -File) {
        $destination = Join-Path $temporary $archive.BaseName
        Expand-Archive -LiteralPath $archive.FullName -DestinationPath $destination
        # Canonicalize this owned allocation: macOS temp paths traverse /var.
        $destination = & $python -c 'import pathlib, sys; print(pathlib.Path(sys.argv[1]).resolve())' $destination
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($destination)) { throw 'Cannot resolve extracted package path.' }
        $files = @(Get-ChildItem -LiteralPath $destination -File -Recurse -Force)
        if ($files.Count -eq 0) { throw "Empty Python package: $($archive.Name)" }
        $skillDirectory = if ($archive.Name -like '*plugin*') { Join-Path $destination 'skills/validated-world' } else { $destination }
        if (-not (Test-Path -LiteralPath (Join-Path $skillDirectory 'SKILL.md') -PathType Leaf)) { throw "Installable skill root missing: $($archive.Name)" }
        $metadata = [IO.File]::ReadAllText((Join-Path $skillDirectory 'pyproject.toml'))
        if ($metadata -notmatch '(?m)^readme = "([^"]+)"\r?$' -or -not (Test-Path -LiteralPath (Join-Path $skillDirectory $Matches[1]) -PathType Leaf)) { throw "Package metadata README missing: $($archive.Name)" }
        if ($files.Name -match '\.mcp\.json$|\.dll$|\.exe$|\.vw\.db$|(^|/)\.env($|\.)') { throw "Forbidden runtime or secret in $($archive.Name)" }
        if (-not (Get-ChildItem -LiteralPath $destination -Filter 'SKILL.md' -File -Recurse)) { throw "Skill instructions missing from $($archive.Name)" }
        if ($archive.Name -like '*plugin*' -and -not (Test-Path -LiteralPath (Join-Path $destination '.codex-plugin/plugin.json') -PathType Leaf)) { throw "Plugin manifest missing from $($archive.Name)" }
        if ($archive.Name -like '*plugin*') {
            $portable = Get-Content -LiteralPath (Join-Path $destination 'plugin.json') -Raw | ConvertFrom-Json
            $overlay = Get-Content -LiteralPath (Join-Path $destination '.codex-plugin/plugin.json') -Raw | ConvertFrom-Json
            if ($portable.'$schema' -ne 'https://agent-plugins.org/schemas/1.0.0/plugin.schema.json') { throw 'Unsupported portable plugin schema' }
            foreach ($field in @('name', 'version', 'description', 'homepage', 'repository', 'license')) {
                if ($portable.$field -ne $overlay.$field) { throw "Plugin manifests disagree on $field" }
            }
            if ($overlay.skills -ne './skills/') { throw 'Skill discovery path is incorrect' }
            & $python (Join-Path $PSScriptRoot 'verify_plugin_package.py') $destination
            if ($LASTEXITCODE -ne 0) { throw 'Plugin listing/branding verification failed.' }
        }
        if (Get-ChildItem -LiteralPath $destination -Directory -Filter '__pycache__' -Recurse -Force) { throw "Python cache directory leaked into $($archive.Name)" }
        $engine = Join-Path $skillDirectory 'src-python'
        if (-not (Test-Path -LiteralPath (Join-Path $engine 'validated_world') -PathType Container)) { throw "Python engine missing from $($archive.Name)" }
        $env:PYTHONPATH = $engine
        $expectedVersion = $null
        if ($archive.Name -match '-([0-9]+\.[0-9]+\.[0-9]+)-dev(?:\.([0-9]+))?\.zip$') {
            $developmentNumber = if ([string]::IsNullOrWhiteSpace($Matches[2])) { '0' } else { $Matches[2] }
            $expectedVersion = "$($Matches[1]).dev$developmentNumber"
        }
        $reportedVersion = & $python -m validated_world --version
        if ($LASTEXITCODE -ne 0) { throw "Extracted Python engine failed smoke launch: $($archive.Name)" }
        if ($null -ne $expectedVersion -and $reportedVersion -notmatch [regex]::Escape($expectedVersion)) { throw "Extracted Python engine version differs from archive: $($archive.Name) reports $reportedVersion" }
        $launcher = Get-Item -LiteralPath (Join-Path $skillDirectory 'scripts/validated_world.py')
        foreach ($reference in @('packet-review.md', 'question-worker.md', 'graph-authoring.md')) {
            $referencePath = Join-Path (Split-Path -Parent (Split-Path -Parent $launcher.FullName)) "references/$reference"
            if (-not (Test-Path -LiteralPath $referencePath -PathType Leaf)) { throw "Missing skill reference: $reference" }
        }
        $launcherVersion = & $python $launcher.FullName --version
        if ($LASTEXITCODE -ne 0) { throw "Extracted skill launcher failed smoke launch: $($archive.Name)" }
        if ($null -ne $expectedVersion -and $launcherVersion -notmatch [regex]::Escape($expectedVersion)) { throw "Extracted skill launcher version differs from archive: $($archive.Name) reports $launcherVersion" }
        # Model a folder-only installer, away from the archive, checkout and
        # PYTHONPATH. -I also excludes user-site packages and Python env settings.
        $isolatedParent = Join-Path $temporary ('isolated-' + $archive.BaseName)
        New-Item -ItemType Directory -Path $isolatedParent | Out-Null
        $isolatedSkill = Join-Path $isolatedParent 'validated-world'
        Copy-Item -LiteralPath $skillDirectory -Destination $isolatedSkill -Recurse
        $isolatedLauncher = Join-Path $isolatedSkill 'scripts/validated_world.py'
        if (-not (Test-Path -LiteralPath (Join-Path $isolatedSkill 'LICENSE') -PathType Leaf)) { throw 'Isolated skill license is missing.' }
        $isolatedVersion = & $python -I $isolatedLauncher --version
        if ($LASTEXITCODE -ne 0 -or $isolatedVersion -ne $launcherVersion) { throw "Folder-only skill installation failed: $($archive.Name)" }
        $trialDocs = Join-Path $isolatedParent 'smoke-project.html'
        $trialDb = Join-Path $isolatedParent 'smoke-working.vw.db'
        $null = & $python -I $isolatedLauncher sample create technical-project $trialDocs
        if ($LASTEXITCODE -ne 0) { throw "Packaged document creation failed: $($archive.Name)" }
        $trialLock = Join-Path $isolatedParent '.smoke-project.html.vw-lock'
        if (Test-Path -LiteralPath $trialLock) { throw "Packaged creation left a lock file: $($archive.Name)" }
        $verification = & $python -I $isolatedLauncher project verify $trialDocs | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or -not $verification.isValid) { throw "Packaged document verification failed: $($archive.Name)" }
        if (-not (Test-Path -LiteralPath $trialDocs -PathType Leaf)) { throw 'Documentation is not a single HTML file.' }
        $beforeHash = (Get-FileHash -LiteralPath $trialDocs -Algorithm SHA256).Hash
        $null = & $python -I $isolatedLauncher project import-html $trialDocs $trialDb
        if ($LASTEXITCODE -ne 0) { throw "Packaged HTML import failed: $($archive.Name)" }
        $null = & $python -I $isolatedLauncher project export-html $trialDb $trialDocs
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $trialDb)) { throw "Packaged HTML replacement or caller DB retention failed: $($archive.Name)" }
        if (Test-Path -LiteralPath $trialLock) { throw "Packaged export left a lock file: $($archive.Name)" }
        $afterHash = (Get-FileHash -LiteralPath $trialDocs -Algorithm SHA256).Hash
        if ($beforeHash -ne $afterHash) { throw "Packaged no-op round trip changed bytes: $($archive.Name)" }
        $finalVerification = & $python -I $isolatedLauncher project verify $trialDocs | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or -not $finalVerification.isValid -or $verification.stateFingerprint -ne $finalVerification.stateFingerprint) { throw "Packaged round trip changed graph meaning: $($archive.Name)" }
    }
    Write-Output "Python package smoke passed: $packages"
}
finally {
    $env:PYTHONPATH = $oldPythonPath
    $resolved = [IO.Path]::GetFullPath($temporary)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing unsafe temporary cleanup: $resolved" }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
