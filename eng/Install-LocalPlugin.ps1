#requires -Version 5.1
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$')]
    [string] $Version,
    [string] $ArtifactsDirectory,
    [string] $InstallRoot,
    [string] $CodexCommand
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseChecks.ps1')
$vwRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $ArtifactsDirectory) { $ArtifactsDirectory = Join-Path $vwRoot "artifacts/release/$Version" }
$vwArchiveName = "validated-world-plugin-$Version-win-x64.zip"
Test-VwReleaseHashes $ArtifactsDirectory @($vwArchiveName)

if (-not $CodexCommand) {
    $vwCommand = Get-Command codex -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $vwCommand) { $CodexCommand = $vwCommand.Source }
    else {
        $vwBin = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'OpenAI/Codex/bin'
        $vwCommand = Get-ChildItem -LiteralPath $vwBin -Filter codex.exe -File -Recurse -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
        if ($null -ne $vwCommand) { $CodexCommand = $vwCommand.FullName }
    }
}
if (-not $CodexCommand -or -not (Get-Command $CodexCommand -CommandType Application -ErrorAction SilentlyContinue)) {
    throw 'Codex CLI not found. Pass -CodexCommand with the full codex.exe path.'
}
function Invoke-VwCodex {
    param([string[]] $Arguments)
    $vwOutput = & $CodexCommand @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Codex command failed: $($Arguments -join ' '). The extracted package is retained; retry after fixing the reported error." }
    return ($vwOutput -join "`n" | ConvertFrom-Json)
}
$vwId = 'validated-world@validated-world-local'
$vwListing = Invoke-VwCodex @('plugin', 'list', '--json')
$vwExisting = @($vwListing.installed | Where-Object { $_.pluginId -ceq $vwId })
foreach ($vwItem in $vwExisting) {
    if ($vwItem.source.source -cne 'local' -or $vwItem.marketplaceSource.sourceType -cne 'local') {
        throw 'Refusing to replace a non-local ValidatedWorld installation.'
    }
}
$vwMarkets = Invoke-VwCodex @('plugin', 'marketplace', 'list', '--json')
$vwOld = @($vwMarkets.marketplaces | Where-Object { $_.name -ceq 'validated-world-local' })
if ($vwOld.Count -gt 1) { throw 'Ambiguous validated-world-local registration; inspect codex plugin marketplace list.' }
if (@($vwListing.installed | Where-Object { $_.marketplaceName -ceq 'validated-world-local' -and $_.pluginId -cne $vwId }).Count -gt 0) {
    throw 'Other installed plugins use validated-world-local. Resolve that shared marketplace manually.'
}
if (-not $InstallRoot) { $InstallRoot = Join-Path $vwRoot 'artifacts/local-plugin' }
$vwInstallRoot = [IO.Path]::GetFullPath($InstallRoot)
$vwDestination = Join-Path $vwInstallRoot "$Version-$([Guid]::NewGuid().ToString('N').Substring(0,8))"
if (-not $PSCmdlet.ShouldProcess($vwId, "Extract verified $Version to $vwDestination and replace only its local marketplace registration")) { return }
Expand-Archive -LiteralPath (Join-Path $ArtifactsDirectory $vwArchiveName) -DestinationPath $vwDestination
$vwPluginRoot = Join-Path $vwDestination 'plugins/validated-world'
$vwManifest = Get-Content -Raw -LiteralPath (Join-Path $vwPluginRoot '.codex-plugin/plugin.json') | ConvertFrom-Json
$vwMarketplace = Get-Content -Raw -LiteralPath (Join-Path $vwDestination '.agents/plugins/marketplace.json') | ConvertFrom-Json
if ($vwManifest.name -cne 'validated-world' -or $vwManifest.version -cne $Version -or
    $vwMarketplace.name -cne 'validated-world-local' -or @($vwMarketplace.plugins).Count -ne 1 -or
    $vwMarketplace.plugins[0].source.source -cne 'local' -or
    $vwMarketplace.plugins[0].source.path -cne './plugins/validated-world') {
    throw 'Package identity/version/layout mismatch. Existing installation was not changed.'
}
$vwBinary = Join-Path $vwPluginRoot 'bin/win-x64/ValidatedWorld.Mcp.exe'
$vwBinaryVersion = & $vwBinary --version
if ($LASTEXITCODE -ne 0 -or $vwBinaryVersion -cne "ValidatedWorld.Mcp $Version") {
    throw 'Packaged executable version mismatch. Existing installation was not changed.'
}
try {
    if ($vwExisting.Count -gt 0) { Invoke-VwCodex @('plugin', 'remove', $vwId, '--json') | Out-Null }
    if ($vwOld.Count -gt 0) { Invoke-VwCodex @('plugin', 'marketplace', 'remove', 'validated-world-local', '--json') | Out-Null }
    Invoke-VwCodex @('plugin', 'marketplace', 'add', $vwDestination, '--json') | Out-Null
    Invoke-VwCodex @('plugin', 'add', $vwId, '--json') | Out-Null
    $vwInstalled = Invoke-VwCodex @('plugin', 'list', '--json')
    $vwMatch = @($vwInstalled.installed | Where-Object { $_.pluginId -ceq $vwId -and $_.version -ceq $Version -and $_.enabled })
    if ($vwMatch.Count -ne 1) { throw 'Codex did not report the expected enabled plugin version.' }
}
catch {
    Write-Warning "Install did not finish. Package retained at $vwDestination. Rerun this script to recover."
    if ($vwOld.Count -eq 1) { Write-Warning "Previous marketplace source (retained): $($vwOld[0].root)" }
    throw
}
Write-Host "Installed $vwId version $Version. Restart the app and start a NEW task."
Write-Host "Ask the agent to call host_status and confirm productVersion=$Version."
Write-Host "Keep this marketplace directory: $vwDestination"
Write-Host 'Project databases and reviewer settings were not changed.'
