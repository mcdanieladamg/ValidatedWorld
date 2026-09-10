#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Executable,
    [Parameter(Mandatory)][string] $Database,
    [Parameter(Mandatory)][string] $Version
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$vwStart = [Diagnostics.ProcessStartInfo]::new()
$vwStart.FileName = $Executable
$vwStart.WorkingDirectory = Split-Path -Parent $Executable
$vwStart.UseShellExecute = $false
$vwStart.RedirectStandardInput = $true
$vwStart.RedirectStandardOutput = $true
$vwStart.RedirectStandardError = $true
$vwStart.EnvironmentVariables['VW_AIREVIEW__ENABLED'] = 'false'
$vwProcess = [Diagnostics.Process]::new()
$vwProcess.StartInfo = $vwStart
[void] $vwProcess.Start()
$vwStderr = $vwProcess.StandardError.ReadToEndAsync()
function Invoke-VwRpc {
    param([string] $Method, [object] $Parameters)
    $vwId = [Guid]::NewGuid().ToString('N')
    $vwRequest = @{ jsonrpc = '2.0'; id = $vwId; method = $Method; params = $Parameters } | ConvertTo-Json -Depth 30 -Compress
    $vwProcess.StandardInput.WriteLine($vwRequest)
    $vwProcess.StandardInput.Flush()
    $vwRead = $vwProcess.StandardOutput.ReadLineAsync()
    if (-not $vwRead.Wait(15000)) { throw "MCP response timeout: $Method" }
    $vwResponse = $vwRead.Result | ConvertFrom-Json
    if ($null -eq $vwResponse -or $vwResponse.id -cne $vwId -or $null -eq $vwResponse.PSObject.Properties['result']) {
        throw "Invalid MCP response: $Method"
    }
    return $vwResponse.result
}
function Invoke-VwTool {
    param([string] $Name, [object] $Arguments, [switch] $ExpectError)
    $vwResult = Invoke-VwRpc 'tools/call' @{ name = $Name; arguments = $Arguments }
    $vwIsError = $null -ne $vwResult.PSObject.Properties['isError'] -and $vwResult.isError
    if ($ExpectError) {
        if (-not $vwIsError) { throw "$Name should have failed." }
        return $vwResult
    }
    if ($vwIsError) { throw "$Name failed: $($vwResult.content[0].text)" }
    $vwData = $vwResult.structuredContent
    if ($null -ne $vwData.PSObject.Properties['result']) { return $vwData.result }
    return $vwData
}
try {
    Invoke-VwRpc 'initialize' @{
        protocolVersion = '2024-11-05'; capabilities = @{}; clientInfo = @{ name = 'packaged-workflow'; version = '1' }
    } | Out-Null
    $vwProcess.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $vwProcess.StandardInput.Flush()
    $vwHost = Invoke-VwTool 'host_status' @{}
    if ($vwHost.productVersion -cne $Version -or $vwHost.semanticReview.effective) { throw 'Wrong host version or review mode.' }
    $vwSelection = Invoke-VwTool 'select_project' @{ path = $Database }
    $vwPurpose = Invoke-VwTool 'read_node' @{ nodeId = 'purpose' }
    $vwNoteId = 'smoke-' + [Guid]::NewGuid().ToString('N')
    $vwSession = Invoke-VwTool 'begin_change' @{ intent = 'Record a realistic release acceptance note' }
    $vwNode = Invoke-VwTool 'put_node' @{
        expectedRevision = $vwSession.revision; mode = 'add'; id = $vwNoteId
        text = 'A reader can reopen this project after updating the plugin.'; kind = 'acceptance-criterion'; tags = @(); attributes = @()
    }
    # Natural mistake: a new node without a scope parent must not be writable.
    Invoke-VwTool 'write_change' @{ expectedRevision = $vwNode.revision } -ExpectError | Out-Null
    $vwEdge = Invoke-VwTool 'put_edge' @{
        expectedRevision = $vwNode.revision; mode = 'add'; id = "$vwNoteId-parent"
        source = $vwNoteId; target = 'purpose'; relationship = 'scope-parent'; reviewDirection = 'None'
        rationale = $null; tags = @(); attributes = @()
    }
    Invoke-VwTool 'write_change' @{ expectedRevision = $vwNode.revision } -ExpectError | Out-Null
    $vwPreview = Invoke-VwTool 'proposal_preview' @{ expectedRevision = $vwEdge.revision }
    if ($vwPreview.operationCount -ne 2) { throw 'Unexpected preview operation count.' }
    $vwWrite = Invoke-VwTool 'write_change' @{ expectedRevision = $vwEdge.revision }
    if ($vwWrite.status -cne 'Written') { throw 'Exact previewed write failed.' }
    Invoke-VwTool 'select_project' @{ path = $Database } | Out-Null
    $vwReopened = Invoke-VwTool 'read_node' @{ nodeId = $vwNoteId }
    if (-not $vwReopened.complete -or $vwReopened.item.id -cne $vwNoteId) { throw 'Written node did not survive reopening.' }
    $vwDiscard = Invoke-VwTool 'begin_change' @{ intent = 'Try and abandon an alternate edit' }
    Invoke-VwTool 'discard_change' @{ expectedRevision = $vwDiscard.revision } | Out-Null
    Write-Host "Packaged MCP workflow passed: $Version; missing-parent recovery, stale revision, preview/write/reopen, discard."
}
finally {
    if (-not $vwProcess.HasExited) { $vwProcess.Kill() }
    $vwProcess.WaitForExit()
    $vwProcess.Dispose()
}
