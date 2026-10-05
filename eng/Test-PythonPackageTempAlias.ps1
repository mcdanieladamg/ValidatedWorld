#requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $PackagesDirectory,
    [Parameter(Mandatory = $true)] [string] $PythonExecutable
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$packages = (Resolve-Path -LiteralPath $PackagesDirectory).Path
$python = (Resolve-Path -LiteralPath $PythonExecutable).Path
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('vw-package-alias-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
$temporary = & $python -c 'import pathlib, sys; print(pathlib.Path(sys.argv[1]).resolve())' $temporary
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($temporary)) { throw 'Cannot resolve alias-test allocation.' }
$physical = Join-Path $temporary 'physical'
$alias = Join-Path $temporary 'alias'
$previous = @{}
foreach ($name in @('TEMP', 'TMP', 'TMPDIR')) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    New-Item -ItemType Directory -Path $physical | Out-Null
    $linkType = if ($env:OS -eq 'Windows_NT') { 'Junction' } else { 'SymbolicLink' }
    New-Item -ItemType $linkType -Path $alias -Target $physical | Out-Null
    if (-not ((Get-Item -LiteralPath $alias).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Alias regression requires an actual directory link.' }
    foreach ($name in @('TEMP', 'TMP', 'TMPDIR')) { [Environment]::SetEnvironmentVariable($name, $alias, 'Process') }
    # Use a fresh shell so its runtime observes the aliased OS temp environment.
    $shellName = if ($env:OS -ne 'Windows_NT') { 'pwsh' } elseif ($PSVersionTable.PSEdition -eq 'Core') { 'pwsh.exe' } else { 'powershell.exe' }
    $shell = Join-Path $PSHOME $shellName
    & $shell -NoProfile -File (Join-Path $PSScriptRoot 'Test-PythonPackage.ps1') -PackagesDirectory $packages -PythonExecutable $python
    if ($LASTEXITCODE -ne 0) { throw 'Package smoke through an aliased OS temp directory failed.' }
    Write-Output 'Package temp-alias regression passed.'
}
finally {
    foreach ($name in @('TEMP', 'TMP', 'TMPDIR')) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    # Remove the owned link itself before recursively cleaning the physical tree.
    if (Test-Path -LiteralPath $alias) { Remove-Item -LiteralPath $alias -Force }
    $resolved = [IO.Path]::GetFullPath($temporary)
    $tempRoot = & $python -c 'import pathlib, sys; print(pathlib.Path(sys.argv[1]).resolve())' ([IO.Path]::GetTempPath())
    if ($LASTEXITCODE -ne 0) { throw "Cannot verify cleanup containment: $resolved" }
    $tempRoot = $tempRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing unsafe alias-test cleanup: $resolved" }
    if (Get-ChildItem -LiteralPath $resolved -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw "Unexpected remaining link; preserving alias test: $resolved" }
    Get-ChildItem -LiteralPath $resolved -Recurse -Force | Out-Null
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
