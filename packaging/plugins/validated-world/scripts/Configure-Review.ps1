#requires -Version 5.1

[CmdletBinding(DefaultParameterSetName = 'Enable')]
param(
    [Parameter(ParameterSetName = 'Enable')]
    [switch] $Enable,

    [Parameter(Mandatory, ParameterSetName = 'Disable')]
    [switch] $Disable,

    [Parameter(Mandatory, ParameterSetName = 'Remove')]
    [switch] $RemoveKey,

    [Parameter(ParameterSetName = 'Enable')]
    [ValidateNotNullOrEmpty()]
    [string] $Model = 'gpt-5.6-terra'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$vwEnabledName = 'VW_AIREVIEW__ENABLED'
$vwProviderName = 'VW_AIREVIEW__PROVIDER'
$vwModelName = 'VW_AIREVIEW__MODEL'
$vwKeyName = 'VW_AIREVIEW__OPENAI__APIKEY'

if ($Disable) {
    [Environment]::SetEnvironmentVariable($vwEnabledName, 'false', 'User')
    Write-Host 'ValidatedWorld independent semantic review is disabled for future Codex processes.'
    Write-Host 'The configured API key was retained. Restart Codex to apply the change.'
    exit 0
}

if ($RemoveKey) {
    [Environment]::SetEnvironmentVariable($vwEnabledName, 'false', 'User')
    [Environment]::SetEnvironmentVariable($vwKeyName, $null, 'User')
    Write-Host 'ValidatedWorld independent semantic review is disabled and its user-level API key was removed.'
    Write-Host 'Restart Codex to apply the change.'
    exit 0
}

$vwSecureKey = Read-Host 'Enter your OpenAI API key' -AsSecureString
$vwKeyPointer = [IntPtr]::Zero
$vwPlainKey = $null
try {
    $vwKeyPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($vwSecureKey)
    $vwPlainKey = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($vwKeyPointer)
    if ([string]::IsNullOrWhiteSpace($vwPlainKey)) {
        throw 'An OpenAI API key is required to enable independent semantic review.'
    }

    [Environment]::SetEnvironmentVariable($vwProviderName, 'openai', 'User')
    [Environment]::SetEnvironmentVariable($vwModelName, $Model, 'User')
    [Environment]::SetEnvironmentVariable($vwKeyName, $vwPlainKey, 'User')
    [Environment]::SetEnvironmentVariable($vwEnabledName, 'true', 'User')
}
finally {
    $vwPlainKey = $null
    if ($vwKeyPointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($vwKeyPointer)
    }
}

Write-Host "ValidatedWorld independent semantic review is enabled with model '$Model' for future Codex processes."
Write-Host 'Restart Codex, start a new task, and ask the agent to report ValidatedWorld host_status.'
