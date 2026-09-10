#requires -Version 5.1

[CmdletBinding()]
param(
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$')]
    [string] $Version,

    [ValidateSet('win-x64')]
    [string] $RuntimeIdentifier = 'win-x64',

    [string] $OutputDirectory,

    [switch] $NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Use the same ZIP implementation locally and on Windows CI, even from pwsh.
if ($PSVersionTable.PSEdition -ne 'Desktop') {
    $vwWindowsPowerShell = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
    $vwForward = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath)
    foreach ($vwParameter in $PSBoundParameters.GetEnumerator()) {
        if ($vwParameter.Key -eq 'NoRestore') { if ($vwParameter.Value) { $vwForward += '-NoRestore' } }
        else { $vwForward += @(('-' + $vwParameter.Key), [string] $vwParameter.Value) }
    }
    & $vwWindowsPowerShell @vwForward
    if ($LASTEXITCODE -ne 0) { throw "Windows package build failed ($LASTEXITCODE)." }
    return
}

$vwRepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'ReleaseChecks.ps1')
$Version = Get-VwReleaseVersion $vwRepositoryRoot $Version
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $vwRepositoryRoot "artifacts/release/$Version"
}
$vwOutputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $vwOutputRoot) {
    throw "Release output already exists: $vwOutputRoot. Choose a new version or remove that exact directory deliberately."
}

function ConvertTo-VwCommandLineArgument {
    param([AllowEmptyString()][string] $Argument)

    if ($Argument.Length -gt 0 -and $Argument -notmatch '[\s"]') { return $Argument }

    $vwBuilder = [Text.StringBuilder]::new()
    [void] $vwBuilder.Append('"')
    $vwBackslashes = 0
    foreach ($vwCharacter in $Argument.ToCharArray()) {
        if ($vwCharacter -eq '\') {
            $vwBackslashes++
            continue
        }
        if ($vwCharacter -eq '"') {
            [void] $vwBuilder.Append(('\' * (($vwBackslashes * 2) + 1)))
            [void] $vwBuilder.Append('"')
            $vwBackslashes = 0
            continue
        }
        if ($vwBackslashes -gt 0) {
            [void] $vwBuilder.Append(('\' * $vwBackslashes))
            $vwBackslashes = 0
        }
        [void] $vwBuilder.Append($vwCharacter)
    }
    if ($vwBackslashes -gt 0) { [void] $vwBuilder.Append(('\' * ($vwBackslashes * 2))) }
    [void] $vwBuilder.Append('"')
    return $vwBuilder.ToString()
}

function Invoke-VwNative {
    param([string] $FilePath, [string[]] $Arguments)

    $vwStart = [Diagnostics.ProcessStartInfo]::new()
    $vwStart.FileName = $FilePath
    $vwStart.Arguments = (($Arguments | ForEach-Object { ConvertTo-VwCommandLineArgument $_ }) -join ' ')
    $vwStart.WorkingDirectory = $vwRepositoryRoot
    $vwStart.UseShellExecute = $false
    $vwStart.RedirectStandardOutput = $true
    $vwStart.RedirectStandardError = $true
    $vwProcess = [Diagnostics.Process]::new()
    $vwProcess.StartInfo = $vwStart
    [void] $vwProcess.Start()
    $vwStandardOutput = $vwProcess.StandardOutput.ReadToEndAsync()
    $vwStandardError = $vwProcess.StandardError.ReadToEndAsync()
    $vwProcess.WaitForExit()
    $vwOut = $vwStandardOutput.GetAwaiter().GetResult()
    $vwErr = $vwStandardError.GetAwaiter().GetResult()
    $vwExitCode = $vwProcess.ExitCode
    $vwProcess.Dispose()
    if ($vwExitCode -ne 0) {
        throw "Command failed ($vwExitCode): $FilePath $($Arguments -join ' ')`n$vwOut`n$vwErr"
    }
    if (-not [string]::IsNullOrWhiteSpace($vwOut)) { Write-Host $vwOut.TrimEnd() }
    if (-not [string]::IsNullOrWhiteSpace($vwErr)) { Write-Host $vwErr.TrimEnd() }
    return $vwOut.Trim()
}

function Write-VwUtf8 {
    param([string] $Path, [string] $Content)
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function Expand-VwTemplate {
    param([string] $Source, [string] $Destination)
    $vwText = [IO.File]::ReadAllText($Source)
    $vwText = $vwText.Replace('{{VERSION}}', $Version).Replace('{{RUNTIME}}', $RuntimeIdentifier).Replace("`r`n", "`n")
    Write-VwUtf8 $Destination $vwText
}

function New-VwDeterministicZip {
    param([string] $SourceDirectory, [string] $DestinationArchive)

    $vwSourceRoot = [IO.Path]::GetFullPath($SourceDirectory).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $vwArchiveStream = [IO.File]::Open($DestinationArchive, [IO.FileMode]::CreateNew)
    try {
        $vwArchive = [IO.Compression.ZipArchive]::new(
            $vwArchiveStream,
            [IO.Compression.ZipArchiveMode]::Create,
            $false)
        try {
            [string[]] $vwFiles = @(Get-ChildItem -LiteralPath $SourceDirectory -File -Recurse -Force | ForEach-Object { $_.FullName })
            [Array]::Sort($vwFiles, [StringComparer]::Ordinal)
            foreach ($vwFileName in $vwFiles) {
                $vwFile = Get-Item -LiteralPath $vwFileName
                $vwFilePath = [IO.Path]::GetFullPath($vwFile.FullName)
                if (-not $vwFilePath.StartsWith($vwSourceRoot, [StringComparison]::OrdinalIgnoreCase)) {
                    throw "Archive input is outside its source directory: $vwFilePath"
                }
                $vwRelative = $vwFilePath.Substring($vwSourceRoot.Length).Replace('\', '/')
                # No-compression mode and a fixed packaging host keep ZIP encoding stable.
                $vwEntry = $vwArchive.CreateEntry($vwRelative, [IO.Compression.CompressionLevel]::NoCompression)
                $vwEntry.ExternalAttributes = 0
                $vwEntry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
                $vwInput = $vwFile.OpenRead()
                $vwOutput = $vwEntry.Open()
                try { $vwInput.CopyTo($vwOutput) }
                finally {
                    $vwOutput.Dispose()
                    $vwInput.Dispose()
                }
            }
        }
        finally { $vwArchive.Dispose() }
    }
    finally { $vwArchiveStream.Dispose() }
}

Add-Type -AssemblyName System.IO.Compression
[IO.Directory]::CreateDirectory($vwOutputRoot) | Out-Null
$vwStagingRoot = Join-Path $vwOutputRoot '.staging'
[IO.Directory]::CreateDirectory($vwStagingRoot) | Out-Null

try {
    $vwCliProject = Join-Path $vwRepositoryRoot 'src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj'
    $vwMcpProject = Join-Path $vwRepositoryRoot 'src/ValidatedWorld.Mcp/ValidatedWorld.Mcp.csproj'
    if (-not $NoRestore) {
        Invoke-VwNative 'dotnet' @('restore', $vwCliProject, '-r', $RuntimeIdentifier) | Out-Null
        Invoke-VwNative 'dotnet' @('restore', $vwMcpProject, '-r', $RuntimeIdentifier) | Out-Null
    }

    $vwCliPublish = Join-Path $vwStagingRoot 'publish-cli'
    $vwMcpPublish = Join-Path $vwStagingRoot 'publish-mcp'
    $vwPublishProperties = @(
        '--self-contained', 'true', '--no-restore',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '-p:Deterministic=true',
        '-p:EnableSourceControlManagerQueries=false',
        '-p:EnableSourceLink=false',
        "-p:PathMap=$vwRepositoryRoot=/_/",
        '-p:IncludeSourceRevisionInInformationalVersion=false',
        "-p:Version=$Version",
        "-p:InformationalVersion=$Version"
    )
    Invoke-VwNative 'dotnet' (@('publish', $vwCliProject, '-c', 'Release', '-r', $RuntimeIdentifier, '-o', $vwCliPublish) + $vwPublishProperties) | Out-Null
    Invoke-VwNative 'dotnet' (@('publish', $vwMcpProject, '-c', 'Release', '-r', $RuntimeIdentifier, '-o', $vwMcpPublish) + $vwPublishProperties) | Out-Null

    $vwCliExecutable = Join-Path $vwCliPublish 'ValidatedWorld.Cli.exe'
    $vwMcpExecutable = Join-Path $vwMcpPublish 'ValidatedWorld.Mcp.exe'
    foreach ($vwRequiredFile in @($vwCliExecutable, $vwMcpExecutable)) {
        if (-not (Test-Path -LiteralPath $vwRequiredFile -PathType Leaf)) {
            throw "Self-contained publish did not produce $vwRequiredFile"
        }
    }

    $vwCliVersion = Invoke-VwNative $vwCliExecutable @('--version')
    $vwMcpVersion = Invoke-VwNative $vwMcpExecutable @('--version')
    if ($vwCliVersion -ne "ValidatedWorld.Cli $Version") { throw "CLI version mismatch: $vwCliVersion" }
    if ($vwMcpVersion -ne "ValidatedWorld.Mcp $Version") { throw "MCP version mismatch: $vwMcpVersion" }

    $vwCliPackage = Join-Path $vwStagingRoot 'cli-package'
    [IO.Directory]::CreateDirectory($vwCliPackage) | Out-Null
    Copy-Item -LiteralPath $vwCliExecutable -Destination $vwCliPackage
    Copy-Item -LiteralPath (Join-Path $vwRepositoryRoot 'LICENSE') -Destination $vwCliPackage
    Expand-VwTemplate (Join-Path $vwRepositoryRoot 'packaging/CLI_INSTALL.md') (Join-Path $vwCliPackage 'INSTALL.md')

    $vwPluginPackage = Join-Path $vwStagingRoot 'plugin-package'
    [IO.Directory]::CreateDirectory($vwPluginPackage) | Out-Null
    Copy-Item -LiteralPath (Join-Path $vwRepositoryRoot 'packaging/.agents') -Destination $vwPluginPackage -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $vwRepositoryRoot 'packaging/plugins') -Destination $vwPluginPackage -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $vwRepositoryRoot 'LICENSE') -Destination $vwPluginPackage
    Expand-VwTemplate (Join-Path $vwRepositoryRoot 'packaging/PLUGIN_INSTALL.md') (Join-Path $vwPluginPackage 'INSTALL.md')

    $vwPackagedPlugin = Join-Path $vwPluginPackage 'plugins/validated-world'
    $vwPackagedManifestPath = Join-Path $vwPackagedPlugin '.codex-plugin/plugin.json'
    # Preserve source formatting; ConvertTo-Json differs between PowerShell 5 and 7.
    $vwManifestText = [IO.File]::ReadAllText($vwPackagedManifestPath)
    if ([regex]::Matches($vwManifestText, '"version"\s*:\s*"[^"]*"').Count -ne 1) {
        throw 'Expected exactly one plugin manifest version.'
    }
    $vwManifestText = [regex]::Replace($vwManifestText, '("version"\s*:\s*")[^"]*(")', ('${1}' + $Version + '${2}'))
    Write-VwUtf8 $vwPackagedManifestPath $vwManifestText
    $vwPluginBin = Join-Path $vwPackagedPlugin "bin/$RuntimeIdentifier"
    [IO.Directory]::CreateDirectory($vwPluginBin) | Out-Null
    Copy-Item -LiteralPath $vwMcpExecutable -Destination $vwPluginBin
    Copy-Item -LiteralPath (Join-Path $vwRepositoryRoot 'LICENSE') -Destination $vwPackagedPlugin

    $vwManifestCheck = Get-Content -Raw -LiteralPath $vwPackagedManifestPath | ConvertFrom-Json
    if ($vwManifestCheck.name -ne 'validated-world' -or $vwManifestCheck.version -ne $Version) {
        throw 'Packaged plugin manifest identity/version mismatch.'
    }
    if ($vwManifestCheck.mcpServers -ne './.mcp.json' -or $vwManifestCheck.skills -ne './skills/') {
        throw 'Packaged plugin manifest component paths are invalid.'
    }

    # All non-binary package inputs are repository text; normalize checkout EOLs.
    foreach ($vwPackageRoot in @($vwCliPackage, $vwPluginPackage)) {
        Get-ChildItem -LiteralPath $vwPackageRoot -File -Recurse -Force |
            Where-Object { $_.Extension -ne '.exe' } | ForEach-Object {
                Write-VwUtf8 $_.FullName ([IO.File]::ReadAllText($_.FullName).Replace("`r`n", "`n"))
            }
    }
    # Detect a changed HEAD or newly dirty tree before labeling automatic artifacts.
    Get-VwReleaseVersion $vwRepositoryRoot $Version | Out-Null
    $vwCliArchive = Join-Path $vwOutputRoot "validated-world-cli-$Version-$RuntimeIdentifier.zip"
    $vwPluginArchive = Join-Path $vwOutputRoot "validated-world-plugin-$Version-$RuntimeIdentifier.zip"
    New-VwDeterministicZip $vwCliPackage $vwCliArchive
    New-VwDeterministicZip $vwPluginPackage $vwPluginArchive
    Expand-VwTemplate (Join-Path $vwRepositoryRoot 'packaging/RELEASE_NOTES.md') (Join-Path $vwOutputRoot "RELEASE_NOTES-$Version.md")

    $vwChecksumTargets = Get-ChildItem -LiteralPath $vwOutputRoot -File |
        Where-Object Name -ne 'SHA256SUMS.txt' |
        Sort-Object -Property Name
    $vwChecksumLines = foreach ($vwTarget in $vwChecksumTargets) {
        $vwHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $vwTarget.FullName).Hash.ToLowerInvariant()
        "$vwHash  $($vwTarget.Name)"
    }
    Write-VwUtf8 (Join-Path $vwOutputRoot 'SHA256SUMS.txt') (($vwChecksumLines -join "`n") + "`n")
}
finally {
    if (Test-Path -LiteralPath $vwStagingRoot) {
        $vwResolvedStaging = [IO.Path]::GetFullPath($vwStagingRoot)
        if (-not $vwResolvedStaging.StartsWith($vwOutputRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing unsafe staging cleanup: $vwResolvedStaging"
        }
        Remove-Item -LiteralPath $vwResolvedStaging -Recurse -Force
    }
}

Write-Host "Prepared release artifacts: $vwOutputRoot"
