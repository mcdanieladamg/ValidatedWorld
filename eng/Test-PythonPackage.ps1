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
    $version = & $python -c 'import sys; print(f"{sys.version_info.major}.{sys.version_info.minor}")'
    if ($LASTEXITCODE -ne 0 -or [version]$version -lt [version]'3.12') { throw "Python 3.12 or newer is required; found $version" }
    foreach ($archive in Get-ChildItem -LiteralPath $packages -Filter '*.zip' -File) {
        $destination = Join-Path $temporary $archive.BaseName
        Expand-Archive -LiteralPath $archive.FullName -DestinationPath $destination
        $files = @(Get-ChildItem -LiteralPath $destination -File -Recurse -Force)
        if ($files.Count -eq 0) { throw "Empty Python package: $($archive.Name)" }
        if ($files.Name -match '\.mcp\.json$|\.dll$|\.exe$|\.vw\.db$|(^|/)\.env($|\.)') { throw "Forbidden runtime or secret in $($archive.Name)" }
        if (-not (Get-ChildItem -LiteralPath $destination -Filter 'SKILL.md' -File -Recurse)) { throw "Skill instructions missing from $($archive.Name)" }
        if ($archive.Name -like '*plugin*' -and -not (Test-Path -LiteralPath (Join-Path $destination '.codex-plugin/plugin.json') -PathType Leaf)) { throw "Plugin manifest missing from $($archive.Name)" }
        if (Get-ChildItem -LiteralPath $destination -Directory -Filter '__pycache__' -Recurse -Force) { throw "Python cache directory leaked into $($archive.Name)" }
        $engine = Join-Path $destination 'src-python'
        if (-not (Test-Path -LiteralPath (Join-Path $engine 'validated_world') -PathType Container)) { throw "Python engine missing from $($archive.Name)" }
        $env:PYTHONPATH = $engine
        & $python -m validated_world --version | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Extracted Python engine failed smoke launch: $($archive.Name)" }
        $launcher = Get-ChildItem -LiteralPath $destination -Filter 'validated_world.py' -File -Recurse | Select-Object -First 1
        if ($null -eq $launcher) { throw "Skill launcher missing from $($archive.Name)" }
        & $python $launcher.FullName --version | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Extracted skill launcher failed smoke launch: $($archive.Name)" }
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
