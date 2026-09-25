#requires -Version 5.1

[CmdletBinding()]
param(
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$')]
    [string] $Version = '0.3.0-dev',
    [string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $root "artifacts/python-release/$Version" }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw "Release output already exists: $output" }
$stage = Join-Path $output 'staging'
New-Item -ItemType Directory -Force -Path $stage | Out-Null
$pythonVersion = $Version
if ($Version -match '^(.+)-dev(?:\.([0-9]+))?$') {
    $developmentNumber = if ([string]::IsNullOrWhiteSpace($Matches[2])) { '0' } else { $Matches[2] }
    $pythonVersion = "$($Matches[1]).dev$developmentNumber"
}

function Copy-PythonEngine([string] $destination) {
    New-Item -ItemType Directory -Force -Path (Join-Path $destination 'src-python') | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'src-python/validated_world') -Destination (Join-Path $destination 'src-python') -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $root 'pyproject.toml') -Destination $destination -Force
    Copy-Item -LiteralPath (Join-Path $root 'docs/python_usage.md') -Destination $destination -Force
    Get-ChildItem -LiteralPath (Join-Path $destination 'src-python') -Directory -Filter '__pycache__' -Recurse -Force | Remove-Item -Recurse -Force
    $projectFile = Join-Path $destination 'pyproject.toml'
    $projectText = [IO.File]::ReadAllText($projectFile)
    $projectText = [regex]::Replace($projectText, '(?m)^version = "[^"]+"$', "version = `"$pythonVersion`"")
    [IO.File]::WriteAllText($projectFile, $projectText, [Text.UTF8Encoding]::new($false))
    $initFile = Join-Path $destination 'src-python/validated_world/__init__.py'
    $initText = [IO.File]::ReadAllText($initFile)
    $initText = [regex]::Replace($initText, '(?m)^__version__ = "[^"]+"$', "__version__ = `"$pythonVersion`"")
    [IO.File]::WriteAllText($initFile, $initText, [Text.UTF8Encoding]::new($false))
}

$skill = Join-Path $stage 'validated-world-skill'
New-Item -ItemType Directory -Force -Path $skill | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'skills/validated-world') -Destination $skill -Recurse -Force
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $skill -Force
Copy-PythonEngine $skill

$plugin = Join-Path $stage 'validated-world-python-plugin'
Copy-Item -LiteralPath (Join-Path $root 'packaging/python-plugin') -Destination $plugin -Recurse -Force
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $plugin 'LICENSE') -Force
Copy-PythonEngine $plugin
$manifestPath = Join-Path $plugin '.codex-plugin/plugin.json'
$manifestText = [IO.File]::ReadAllText($manifestPath)
$manifestText = [regex]::Replace($manifestText, '"version":\s*"[^"]+"', "`"version`": `"$Version`"")
[IO.File]::WriteAllText($manifestPath, $manifestText, [Text.UTF8Encoding]::new($false))
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.version -ne $Version) { throw "Plugin manifest version does not match requested version: $($manifest.version)" }

$forbidden = @('*.mcp.json', '*.dll', '*.exe', '*.vw.db', '*.key', '.env*')
foreach ($file in Get-ChildItem -LiteralPath $stage -File -Recurse -Force) {
    foreach ($pattern in $forbidden) {
        if ($file.Name -like $pattern -and $file.Name -ne '.env.example') { throw "Forbidden release file: $($file.FullName)" }
    }
}
New-Item -ItemType Directory -Force -Path $output | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($skill, (Join-Path $output "validated-world-skill-$Version.zip"), [IO.Compression.CompressionLevel]::Optimal, $false)
[IO.Compression.ZipFile]::CreateFromDirectory($plugin, (Join-Path $output "validated-world-python-plugin-$Version.zip"), [IO.Compression.CompressionLevel]::Optimal, $false)
$hashLines = @(Get-ChildItem -LiteralPath $output -Filter '*.zip' | Get-FileHash -Algorithm SHA256 | ForEach-Object { "$($_.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($_.Path))" })
[IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'), (($hashLines -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
Write-Output "Python packages created in $output"
