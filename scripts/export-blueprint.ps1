# Regenerate the diff-friendly blueprint projection from the canonical database.
# The JSON output is derived review material; never edit it as a second source.

[CmdletBinding()]
param(
    [string]$DatabasePath,
    [string]$OutputPath,
    [switch]$Check
)

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($DatabasePath)) {
    $DatabasePath = Join-Path $repositoryRoot 'ValidatedWorld.Blueprint.vw.db'
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repositoryRoot 'samples\ValidatedWorldBlueprint\baseline.json'
}

$databaseFullPath = [System.IO.Path]::GetFullPath($DatabasePath)
$outputFullPath = [System.IO.Path]::GetFullPath($OutputPath)
if (-not (Test-Path -LiteralPath $databaseFullPath -PathType Leaf)) {
    throw "Canonical database not found: $databaseFullPath"
}

$cliProject = Join-Path $repositoryRoot 'src\ValidatedWorld.Cli\ValidatedWorld.Cli.csproj'
$request = @{
    version = 1
    command = 'project.open'
    payload = @{ path = $databaseFullPath }
} | ConvertTo-Json -Depth 100 -Compress

$lines = @($request | dotnet run --no-restore --project $cliProject -- ndjson)
if ($LASTEXITCODE -ne 0 -or $lines.Count -eq 0) {
    throw "ValidatedWorld could not open the canonical database."
}

$response = $lines[-1] | ConvertFrom-Json -Depth 100
if ($response.status -ne 'ok') {
    throw "ValidatedWorld rejected the canonical database: $($response.payload.message)"
}

$projection = [ordered]@{
    format = 'validatedworld.graph-snapshot'
    version = 1
    stateFingerprint = $response.payload.project.stateFingerprint
    graph = $response.payload.graph
}
$json = ($projection | ConvertTo-Json -Depth 100) -replace "`r`n", "`n"
$expectedText = $json.TrimEnd() + "`n"
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
if ($Check) {
    if (-not (Test-Path -LiteralPath $outputFullPath -PathType Leaf) -or
        [System.IO.File]::ReadAllText($outputFullPath) -cne $expectedText) {
        throw "Blueprint projection is missing or does not match the canonical database."
    }
}
else {
    [System.IO.File]::WriteAllText($outputFullPath, $expectedText, $utf8NoBom)
}

$roundTrip = Get-Content -Raw -LiteralPath $outputFullPath | ConvertFrom-Json -Depth 100
if ($roundTrip.stateFingerprint -cne $response.payload.project.stateFingerprint -or
    $roundTrip.graph.nodes.Count -ne $response.payload.graph.nodes.Count -or
    $roundTrip.graph.edges.Count -ne $response.payload.graph.edges.Count) {
    throw "Generated projection did not round-trip with the expected entity counts."
}

[pscustomobject]@{
    database = $databaseFullPath
    projection = $outputFullPath
    mode = $(if ($Check) { 'checked' } else { 'written' })
    stateFingerprint = $response.payload.project.stateFingerprint
    nodeCount = $roundTrip.graph.nodes.Count
    edgeCount = $roundTrip.graph.edges.Count
}
