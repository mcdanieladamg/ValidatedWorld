#requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$')]
    [string] $Version,

    [ValidateSet('win-x64')]
    [string] $RuntimeIdentifier = 'win-x64',

    [string] $ArtifactsDirectory,

    [string] $CodexCommand,

    [switch] $RequireCodex
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$vwRepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($ArtifactsDirectory)) {
    $ArtifactsDirectory = Join-Path $vwRepositoryRoot "artifacts/release/$Version"
}
$vwArtifactsRoot = [IO.Path]::GetFullPath($ArtifactsDirectory)
$vwCliArchive = Join-Path $vwArtifactsRoot "validated-world-cli-$Version-$RuntimeIdentifier.zip"
$vwPluginArchive = Join-Path $vwArtifactsRoot "validated-world-plugin-$Version-$RuntimeIdentifier.zip"
$vwChecksums = Join-Path $vwArtifactsRoot 'SHA256SUMS.txt'

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

function Invoke-VwProcess {
    param(
        [string] $FilePath,
        [string[]] $Arguments,
        [string] $WorkingDirectory
    )

    $vwStart = [Diagnostics.ProcessStartInfo]::new()
    $vwStart.FileName = $FilePath
    $vwStart.Arguments = (($Arguments | ForEach-Object { ConvertTo-VwCommandLineArgument $_ }) -join ' ')
    $vwStart.WorkingDirectory = $WorkingDirectory
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
    if (-not [string]::IsNullOrWhiteSpace($vwErr)) { Write-Host $vwErr.TrimEnd() }
    return $vwOut.Trim()
}

function Assert-VwPluginVersion {
    param([string] $ManifestPath, [string] $ExecutablePath)
    $vwManifestVersion = (Get-Content -Raw -LiteralPath $ManifestPath | ConvertFrom-Json).version
    $vwExecutableVersion = Invoke-VwProcess $ExecutablePath @('--version') (Split-Path -Parent $ExecutablePath)
    if ($vwExecutableVersion -ne "ValidatedWorld.Mcp $vwManifestVersion") {
        throw "Plugin manifest/binary version mismatch: manifest=$vwManifestVersion binary=$vwExecutableVersion"
    }
}

function Resolve-VwCodexExecutable {
    if (-not [string]::IsNullOrWhiteSpace($CodexCommand)) {
        $vwExplicitCodex = Get-Command $CodexCommand -CommandType Application -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($null -eq $vwExplicitCodex) {
            throw "Codex executable was not found: $CodexCommand"
        }
        return $vwExplicitCodex.Source
    }

    $vwPathCodex = Get-Command codex -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -ne $vwPathCodex) { return $vwPathCodex.Source }

    $vwLocalAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    if (-not [string]::IsNullOrWhiteSpace($vwLocalAppData)) {
        $vwDesktopBin = Join-Path $vwLocalAppData 'OpenAI\Codex\bin'
        if (Test-Path -LiteralPath $vwDesktopBin -PathType Container) {
            $vwDesktopCodex = Get-ChildItem -LiteralPath $vwDesktopBin -Filter 'codex.exe' -File -Recurse |
                Sort-Object -Property LastWriteTimeUtc -Descending |
                Select-Object -First 1
            if ($null -ne $vwDesktopCodex) { return $vwDesktopCodex.FullName }
        }
    }

    return $null
}

foreach ($vwRequiredFile in @($vwCliArchive, $vwPluginArchive, $vwChecksums)) {
    if (-not (Test-Path -LiteralPath $vwRequiredFile -PathType Leaf)) { throw "Missing release artifact: $vwRequiredFile" }
}

$vwExpectedHashes = @{}
foreach ($vwLine in Get-Content -LiteralPath $vwChecksums) {
    if ($vwLine -notmatch '^([0-9a-f]{64})  (.+)$') { throw "Invalid checksum line: $vwLine" }
    $vwExpectedHashes[$Matches[2]] = $Matches[1]
}
foreach ($vwName in $vwExpectedHashes.Keys) {
    $vwTargetPath = Join-Path $vwArtifactsRoot $vwName
    $vwActualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $vwTargetPath).Hash.ToLowerInvariant()
    if ($vwActualHash -ne $vwExpectedHashes[$vwName]) { throw "Checksum mismatch: $vwName" }
}

$vwTemporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("ValidatedWorld release QA $([Guid]::NewGuid().ToString('N'))")
[IO.Directory]::CreateDirectory($vwTemporaryRoot) | Out-Null
$vwPreviousReviewSetting = [Environment]::GetEnvironmentVariable('VW_AIREVIEW__ENABLED', 'Process')
$vwPreviousCodexHome = [Environment]::GetEnvironmentVariable('CODEX_HOME', 'Process')
try {
    [Environment]::SetEnvironmentVariable('VW_AIREVIEW__ENABLED', 'false', 'Process')
    $vwCliInstall = Join-Path $vwTemporaryRoot 'ordinary user cli install'
    $vwMarketplaceInstall = Join-Path $vwTemporaryRoot 'ordinary user plugin marketplace'
    Expand-Archive -LiteralPath $vwCliArchive -DestinationPath $vwCliInstall
    Expand-Archive -LiteralPath $vwPluginArchive -DestinationPath $vwMarketplaceInstall

    $vwCliExecutable = Join-Path $vwCliInstall 'ValidatedWorld.Cli.exe'
    $vwMcpExecutable = Join-Path $vwMarketplaceInstall "plugins/validated-world/bin/$RuntimeIdentifier/ValidatedWorld.Mcp.exe"
    $vwPluginManifest = Join-Path $vwMarketplaceInstall 'plugins/validated-world/.codex-plugin/plugin.json'
    $vwLauncher = Join-Path $vwMarketplaceInstall 'plugins/validated-world/scripts/launch-mcp.cmd'

    $vwCliVersion = Invoke-VwProcess $vwCliExecutable @('--version') $vwCliInstall
    if ($vwCliVersion -ne "ValidatedWorld.Cli $Version") { throw "CLI version mismatch: $vwCliVersion" }
    Assert-VwPluginVersion $vwPluginManifest $vwMcpExecutable
    $vwLauncherVersion = Invoke-VwProcess 'cmd.exe' @('/d', '/s', '/c', 'call', $vwLauncher, '--version') (Split-Path -Parent $vwLauncher)
    if ($vwLauncherVersion -ne "ValidatedWorld.Mcp $Version") { throw "MCP launcher failed in a path with spaces: $vwLauncherVersion" }

    $vwDataDirectory = Join-Path $vwTemporaryRoot 'retained user data outside installs'
    [IO.Directory]::CreateDirectory($vwDataDirectory) | Out-Null
    $vwDatabase = Join-Path $vwDataDirectory 'important project.vw.db'
    $vwInitialized = Invoke-VwProcess $vwCliExecutable @('project', 'init', $vwDatabase, 'release-smoke', 'Release smoke', 'purpose', 'Prove portable install and retained data.') $vwDataDirectory
    if (($vwInitialized | ConvertFrom-Json).projectId -ne 'release-smoke') { throw 'Packaged CLI project initialization failed.' }
    $vwVerified = Invoke-VwProcess $vwCliExecutable @('project', 'verify', $vwDatabase) $vwDataDirectory
    if (-not ($vwVerified | ConvertFrom-Json).isValid) { throw 'Packaged CLI SQLite verification failed.' }

    $vwMcpStart = [Diagnostics.ProcessStartInfo]::new()
    $vwMcpStart.FileName = $vwMcpExecutable
    $vwMcpStart.WorkingDirectory = Split-Path -Parent $vwMcpExecutable
    $vwMcpStart.UseShellExecute = $false
    $vwMcpStart.RedirectStandardInput = $true
    $vwMcpStart.RedirectStandardOutput = $true
    $vwMcpStart.RedirectStandardError = $true
    $vwMcpStart.EnvironmentVariables['VW_AIREVIEW__ENABLED'] = 'false'
    $vwMcpProcess = [Diagnostics.Process]::new()
    $vwMcpProcess.StartInfo = $vwMcpStart
    [void] $vwMcpProcess.Start()
    try {
        $vwMcpProcess.StandardInput.WriteLine('{"jsonrpc":"2.0","id":"init","method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"release-smoke","version":"1"}}}')
        $vwMcpProcess.StandardInput.Flush()
        $vwInitializeResponse = $vwMcpProcess.StandardOutput.ReadLine() | ConvertFrom-Json
        if ($vwInitializeResponse.result.protocolVersion -ne '2024-11-05') { throw 'Packaged MCP initialization failed.' }
        $vwMcpProcess.StandardInput.WriteLine('{"jsonrpc":"2.0","id":"status","method":"tools/call","params":{"name":"host_status","arguments":{}}}')
        $vwMcpProcess.StandardInput.Flush()
        $vwHostResponse = $vwMcpProcess.StandardOutput.ReadLine() | ConvertFrom-Json
        if ($null -eq $vwHostResponse.PSObject.Properties['result']) {
            throw "Packaged MCP host_status failed: $($vwHostResponse | ConvertTo-Json -Depth 20 -Compress)"
        }
        $vwHostStatus = $vwHostResponse.result.structuredContent
        if ($null -ne $vwHostStatus.PSObject.Properties['result']) { $vwHostStatus = $vwHostStatus.result }
        if ($vwHostStatus.productVersion -ne $Version -or $vwHostStatus.hostSupport -ne 'local-only' -or $vwHostStatus.transport -ne 'stdio') {
            throw 'Packaged MCP host_status returned incompatible identity or support metadata.'
        }
        if ($vwHostStatus.semanticReview.effective) { throw 'Release smoke review configuration should be disabled.' }
    }
    finally {
        if (-not $vwMcpProcess.HasExited) { $vwMcpProcess.Kill() }
        $vwMcpProcess.WaitForExit()
        $vwMcpProcess.Dispose()
    }

    $vwOriginalManifest = [IO.File]::ReadAllText($vwPluginManifest)
    $vwMismatched = $vwOriginalManifest | ConvertFrom-Json
    $vwMismatched.version = '9.9.9-test-mismatch'
    [IO.File]::WriteAllText($vwPluginManifest, (($vwMismatched | ConvertTo-Json -Depth 20) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
    $vwMismatchRejected = $false
    try { Assert-VwPluginVersion $vwPluginManifest $vwMcpExecutable }
    catch { $vwMismatchRejected = $true }
    if (-not $vwMismatchRejected) { throw 'Deliberate plugin version mismatch was not rejected.' }
    [IO.File]::WriteAllText($vwPluginManifest, $vwOriginalManifest, [Text.UTF8Encoding]::new($false))

    $vwCodexExecutable = Resolve-VwCodexExecutable
    $vwCodexVersion = $null
    if ($null -eq $vwCodexExecutable) {
        if ($RequireCodex) {
            throw 'Codex CLI is required for plugin lifecycle checks. Install Codex, add codex.exe to PATH, or pass -CodexCommand with its full path.'
        }
        Write-Warning 'Codex CLI was not found; plugin install/reinstall/uninstall checks were skipped. Use -RequireCodex for strict release acceptance.'
    }
    else {
        $vwIsolatedCodexHome = Join-Path $vwTemporaryRoot 'isolated codex home'
        [IO.Directory]::CreateDirectory($vwIsolatedCodexHome) | Out-Null
        [Environment]::SetEnvironmentVariable('CODEX_HOME', $vwIsolatedCodexHome, 'Process')
        Invoke-VwProcess $vwCodexExecutable @('plugin', 'marketplace', 'add', $vwMarketplaceInstall, '--json') $vwTemporaryRoot | Out-Null
        Invoke-VwProcess $vwCodexExecutable @('plugin', 'add', 'validated-world@validated-world-local', '--json') $vwTemporaryRoot | Out-Null
        $vwInstalled = Invoke-VwProcess $vwCodexExecutable @('plugin', 'list', '--json') $vwTemporaryRoot | ConvertFrom-Json
        if ($vwInstalled.installed.Count -ne 1 -or (($vwInstalled.installed | ConvertTo-Json -Depth 20) -notmatch 'validated-world')) {
            throw 'Codex did not report the local plugin as installed.'
        }
        Invoke-VwProcess $vwCodexExecutable @('plugin', 'add', 'validated-world@validated-world-local', '--json') $vwTemporaryRoot | Out-Null
        Invoke-VwProcess $vwCodexExecutable @('plugin', 'remove', 'validated-world@validated-world-local', '--json') $vwTemporaryRoot | Out-Null
        $vwRemoved = Invoke-VwProcess $vwCodexExecutable @('plugin', 'list', '--json') $vwTemporaryRoot | ConvertFrom-Json
        if ($vwRemoved.installed.Count -ne 0) { throw 'Codex still reports a plugin after uninstall.' }
        $vwCodexVersion = Invoke-VwProcess $vwCodexExecutable @('--version') $vwTemporaryRoot
    }

    if (-not (Test-Path -LiteralPath $vwDatabase -PathType Leaf)) { throw 'Plugin uninstall removed external user data.' }
    $vwVerifiedAfterRemoval = Invoke-VwProcess $vwCliExecutable @('project', 'verify', $vwDatabase) $vwDataDirectory
    if (-not ($vwVerifiedAfterRemoval | ConvertFrom-Json).isValid) { throw 'External database was invalid after plugin uninstall.' }

    if ($null -eq $vwCodexVersion) {
        Write-Host "Release package smoke passed: $Version $RuntimeIdentifier; Codex plugin lifecycle skipped"
    }
    else {
        Write-Host "Release smoke passed: $Version $RuntimeIdentifier; $vwCodexVersion"
    }
}
finally {
    [Environment]::SetEnvironmentVariable('VW_AIREVIEW__ENABLED', $vwPreviousReviewSetting, 'Process')
    [Environment]::SetEnvironmentVariable('CODEX_HOME', $vwPreviousCodexHome, 'Process')
    if (Test-Path -LiteralPath $vwTemporaryRoot) {
        $vwResolvedTemporary = [IO.Path]::GetFullPath($vwTemporaryRoot)
        $vwSystemTemporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        if (-not $vwResolvedTemporary.StartsWith($vwSystemTemporary, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing unsafe temporary cleanup: $vwResolvedTemporary"
        }
        Remove-Item -LiteralPath $vwResolvedTemporary -Recurse -Force
    }
}
