# Repository conventions only; not a general ValidatedWorld validation engine.
function Get-VwRoadmapErrors {
    param([object[]] $Nodes, [object[]] $Edges)

    $vwPhases = @($Nodes | Where-Object { $_.tags -ccontains 'roadmap:phase' })
    $vwCurrent = @($Nodes | Where-Object { $_.tags -ccontains 'status:current' })
    $vwStatus = @($Nodes | Where-Object { $_.tags -ccontains 'project:status' })
    if ($vwCurrent.Count -ne 1) { "Expected exactly one status:current node; found $($vwCurrent.Count)." }
    if ($vwStatus.Count -ne 1) { "Expected exactly one project:status node; found $($vwStatus.Count)." }
    foreach ($vwNode in $Nodes) {
        $vwEstimates = @($vwNode.tags | Where-Object { $_ -cmatch '^estimate:' })
        if ($vwNode.tags -ccontains 'status:current') {
            if ($vwNode.tags -cnotcontains 'roadmap:phase') { "$($vwNode.id): current node must be a roadmap phase." }
            if ($vwEstimates.Count -ne 1 -or $vwEstimates[0] -cnotmatch '^estimate:(small|medium|large|gigantic)$') {
                "$($vwNode.id): current phase needs exactly one valid estimate."
            }
        }
        elseif ($vwEstimates.Count -gt 0) { "$($vwNode.id): only the current phase may carry an estimate." }
    }
    $vwPhaseIds = @{}
    foreach ($vwPhase in $vwPhases) {
        $vwStates = @($vwPhase.tags | Where-Object { $_ -cmatch '^status:' })
        if ($vwStates.Count -ne 1 -or $vwStates[0] -cnotmatch '^status:(pending|current|complete)$') {
            "$($vwPhase.id): phase needs exactly one pending/current/complete status."
        }
        $vwTags = @($vwPhase.tags | Where-Object { $_ -cmatch '^phase:' })
        if ($vwTags.Count -ne 1) { "$($vwPhase.id): phase needs exactly one phase:<id> tag." }
        elseif ($vwPhaseIds.ContainsKey($vwTags[0])) { "Duplicate phase tag: $($vwTags[0])." }
        else { $vwPhaseIds[$vwTags[0]] = $vwPhase.id }
    }
    $vwCurrentEdges = @($Edges | Where-Object { $_.relationship -ceq 'current-phase' })
    if ($vwCurrent.Count -eq 1 -and $vwStatus.Count -eq 1) {
        $vwPhaseTags = @($vwCurrent[0].tags | Where-Object { $_ -cmatch '^phase:' })
        $vwPointers = @($vwStatus[0].tags | Where-Object { $_ -cmatch '^current-phase:' })
        if ($vwPhaseTags.Count -ne 1 -or $vwPointers.Count -ne 1 -or
            $vwPointers[0] -cne ('current-' + $vwPhaseTags[0])) {
            'project:status current-phase tag must match the current phase.'
        }
        if ($vwCurrentEdges.Count -ne 1 -or $vwCurrentEdges[0].source -cne $vwStatus[0].id -or
            $vwCurrentEdges[0].target -cne $vwCurrent[0].id) {
            'Exactly one current-phase edge must connect project:status to the current phase.'
        }
    }
}
