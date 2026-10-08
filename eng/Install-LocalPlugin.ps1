#requires -Version 5.1

[CmdletBinding()]
param(
    [string] $Version = '1.0.3',
    [string] $CodexExecutable
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$archive = Join-Path $root "artifacts/release/$Version/validated-world-plugin-$Version.zip"
if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) { throw "Plugin archive not found: $archive" }

$marketplaceRoot = Join-Path $root "artifacts/local-plugin/$Version"
$marketplaceName = 'validated-world-local-' + ($Version -replace '[^A-Za-z0-9_-]', '-')
$pluginRoot = Join-Path $marketplaceRoot 'plugins/validated-world'
$marketplaceFile = Join-Path $marketplaceRoot '.agents/plugins/marketplace.json'
if (-not (Test-Path -LiteralPath $marketplaceRoot)) {
    New-Item -ItemType Directory -Path $pluginRoot -Force | Out-Null
    New-Item -ItemType Directory -Path (Split-Path -Parent $marketplaceFile) -Force | Out-Null
    Expand-Archive -LiteralPath $archive -DestinationPath $pluginRoot
}

$pluginManifest = Join-Path $pluginRoot '.codex-plugin/plugin.json'
$skillFile = Join-Path $pluginRoot 'skills/validated-world/SKILL.md'
if (-not (Test-Path -LiteralPath $pluginManifest -PathType Leaf) -or
    -not (Test-Path -LiteralPath $skillFile -PathType Leaf)) {
    throw 'The extracted archive is missing its plugin manifest or skill.'
}
$plugin = Get-Content -LiteralPath $pluginManifest -Raw | ConvertFrom-Json
if ($plugin.name -ne 'validated-world' -or $plugin.version -ne $Version) {
    throw "Unexpected plugin identity or version in $pluginManifest"
}

$marketplace = @{
    name = $marketplaceName
    interface = @{ displayName = 'ValidatedWorld Local' }
    plugins = @(@{
        name = 'validated-world'
        source = @{ source = 'local'; path = './plugins/validated-world' }
        policy = @{ installation = 'AVAILABLE'; authentication = 'ON_INSTALL' }
        category = 'Productivity'
    })
}
if (Test-Path -LiteralPath $marketplaceFile) {
    $existing = Get-Content -LiteralPath $marketplaceFile -Raw | ConvertFrom-Json
    if ($existing.name -ne $marketplace.name -or
        $existing.plugins.Count -ne 1 -or
        $existing.plugins[0].name -ne 'validated-world' -or
        $existing.plugins[0].source.path -ne './plugins/validated-world') {
        throw "Existing local marketplace does not match this candidate: $marketplaceFile"
    }
}
else {
    [IO.File]::WriteAllText($marketplaceFile, (($marketplace | ConvertTo-Json -Depth 8) + "`n"), [Text.UTF8Encoding]::new($false))
}

if ([string]::IsNullOrWhiteSpace($env:CODEX_HOME)) {
    $env:CODEX_HOME = Join-Path $env:USERPROFILE '.codex'
}
if ([string]::IsNullOrWhiteSpace($CodexExecutable)) {
    $onPath = Get-Command codex -ErrorAction SilentlyContinue
    if ($null -ne $onPath) {
        $CodexExecutable = $onPath.Source
    }
    else {
        $appBin = Join-Path $env:LOCALAPPDATA 'OpenAI/Codex/bin'
        $CodexExecutable = Get-ChildItem -LiteralPath $appBin -Directory -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            ForEach-Object { Join-Path $_.FullName 'codex.exe' } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
            Select-Object -First 1
    }
}
if ([string]::IsNullOrWhiteSpace($CodexExecutable) -or
    -not (Test-Path -LiteralPath $CodexExecutable -PathType Leaf)) {
    throw 'The desktop plugin CLI was not found. Pass its full path with -CodexExecutable.'
}
$configuredMarketplaces = & $CodexExecutable plugin marketplace list
if ($LASTEXITCODE -ne 0) { throw 'Could not inspect Codex plugin marketplaces.' }
if (-not ($configuredMarketplaces | Where-Object { $_ -match "^$([regex]::Escape($marketplaceName))\s+" })) {
    & $CodexExecutable plugin marketplace add $marketplaceRoot
    if ($LASTEXITCODE -ne 0) { throw 'Codex did not add the local marketplace.' }
}
& $CodexExecutable plugin add "validated-world@$marketplaceName"
if ($LASTEXITCODE -ne 0) { throw 'Codex did not install the plugin.' }
$localPluginRoots = [IO.Path]::GetFullPath((Join-Path $root 'artifacts/local-plugin')) + [IO.Path]::DirectorySeparatorChar
foreach ($line in $configuredMarketplaces) {
    if ($line -notmatch '^([A-Za-z0-9_-]+)\s+(.+)$') { continue }
    $oldName = $Matches[1]
    $oldRoot = $Matches[2].Trim()
    if ($oldName -eq $marketplaceName -or -not [IO.Path]::IsPathRooted($oldRoot)) { continue }
    $oldRoot = [IO.Path]::GetFullPath($oldRoot)
    if (-not $oldRoot.StartsWith($localPluginRoots, [StringComparison]::OrdinalIgnoreCase)) { continue }
    # Read the identity from this repository's old marketplace, including installs
    # made before the canonical package naming. Never guess another plugin's ID.
    $oldFile = Join-Path $oldRoot '.agents/plugins/marketplace.json'
    if (-not (Test-Path -LiteralPath $oldFile -PathType Leaf)) { continue }
    $oldMarketplace = Get-Content -LiteralPath $oldFile -Raw | ConvertFrom-Json
    if ($oldMarketplace.name -ne $oldName -or $oldMarketplace.plugins.Count -ne 1) { continue }
    $oldPlugin = $oldMarketplace.plugins[0]
    if ($oldPlugin.name -notmatch '^validated-world(?:-[a-z]+)?$' -or
        $oldPlugin.source.source -ne 'local' -or
        $oldPlugin.source.path -ne "./plugins/$($oldPlugin.name)") { continue }
    & $CodexExecutable plugin remove "$($oldPlugin.name)@$oldName"
    if ($LASTEXITCODE -ne 0) { throw "Installed the new plugin, but could not remove the old plugin from $oldName." }
    & $CodexExecutable plugin marketplace remove $oldName
    if ($LASTEXITCODE -ne 0) { throw "Installed the new plugin, but could not remove the old marketplace $oldName." }
}
Write-Output "Installed validated-world@$Version from $marketplaceRoot"
