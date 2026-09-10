#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$')]
    [string] $Version,
    [string] $CodexCommand,
    [switch] $NoRestore
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$vwRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'ReleaseChecks.ps1')
$Version = Get-VwReleaseVersion $vwRoot $Version
if (Test-Path -LiteralPath (Join-Path $vwRoot "artifacts/release/$Version")) {
    throw "Release output for $Version already exists. Install those artifacts, or deliberately remove that exact output directory before rebuilding."
}
Push-Location $vwRoot
try {
    if (-not $NoRestore) {
        & dotnet restore ValidatedWorld.slnx
        if ($LASTEXITCODE -ne 0) { throw 'Solution restore failed.' }
    }
    & dotnet build ValidatedWorld.slnx --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Solution build failed.' }
    & dotnet test ValidatedWorld.slnx --no-build --no-restore --filter 'Category!=LiveOpenAI'
    if ($LASTEXITCODE -ne 0) { throw 'Solution tests failed.' }
    & (Join-Path $PSScriptRoot 'Test-DeveloperTools.ps1')
    & (Join-Path $PSScriptRoot 'Test-Blueprint.ps1')
    & (Join-Path $PSScriptRoot 'Build-Release.ps1') -Version $Version -NoRestore:$NoRestore
    & (Join-Path $PSScriptRoot 'Test-Release.ps1') -Version $Version -CodexCommand $CodexCommand -RequireCodex
    Write-Host "Prepared and tested $Version. To install it in your actual ChatGPT Desktop/Codex CLI profile, run:"
    Write-Host ".\eng\Install-LocalPlugin.ps1 -Version $Version"
    Write-Host 'No publishing or actual Codex installation was performed.'
}
finally { Pop-Location }
