using System.Collections.ObjectModel;
using ValidatedWorld.Core;

namespace ValidatedWorld.Validation;

public enum AffectedAnalysisStatus
{
    Complete,
    Inconclusive,
}

public enum AffectedOmissionReason
{
    TraversalDepthLimit,
    AffectedNodeLimit,
    OutputLimit,
    Cancelled,
}

public sealed record AffectedOmission(
    AffectedOmissionReason Reason,
    EntityId? SourceNodeId,
    EntityId? TargetNodeId,
    EntityId? EdgeId,
    int? Depth,
    string Message);

public sealed class AffectedAnalysisOptions
{
    private int _maxTraversalDepth = 100_000;
    private int _maxAffectedNodes = 1_000_000;
    private int _maxOutputItems = 1_000_000;

    public int MaxTraversalDepth
    {
        get => _maxTraversalDepth;
        init => _maxTraversalDepth = ValidatePositive(value, nameof(MaxTraversalDepth));
    }

    public int MaxAffectedNodes
    {
        get => _maxAffectedNodes;
        init => _maxAffectedNodes = ValidatePositive(value, nameof(MaxAffectedNodes));
    }

    public int MaxOutputItems
    {
        get => _maxOutputItems;
        init => _maxOutputItems = ValidatePositive(value, nameof(MaxOutputItems));
    }

    public CancellationToken CancellationToken { get; init; }

    private static int ValidatePositive(int value, string name) => value > 0
        ? value
        : throw new ArgumentOutOfRangeException(name, "The limit must be positive.");
}

public sealed class AffectedPath
{
    public AffectedPath(IEnumerable<EntityId> nodes, IEnumerable<EntityId> edges)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);
        Nodes = new ReadOnlyCollection<EntityId>(nodes.ToArray());
        Edges = new ReadOnlyCollection<EntityId>(edges.ToArray());
        if (Nodes.Count == 0 || Edges.Count != Nodes.Count - 1)
        {
            throw new ArgumentException("A path must contain one more node than edge.");
        }
    }

    public IReadOnlyList<EntityId> Nodes { get; }

    public IReadOnlyList<EntityId> Edges { get; }
}

public sealed class AffectedNode
{
    internal AffectedNode(
        EntityId nodeId,
        bool isDirectChange,
        int distance,
        AffectedPath explanation,
        GraphNode? currentNode,
        GraphNode? proposedNode)
    {
        NodeId = nodeId;
        IsDirectChange = isDirectChange;
        Distance = distance;
        Explanation = explanation;
        CurrentNode = currentNode;
        ProposedNode = proposedNode;
    }

    public EntityId NodeId { get; }

    public bool IsDirectChange { get; }

    public int Distance { get; }

    public AffectedPath Explanation { get; }

    public GraphNode? CurrentNode { get; }

    public GraphNode? ProposedNode { get; }
}

public sealed class ScopeLineageEvidence
{
    internal ScopeLineageEvidence(
        EntityId affectedNodeId,
        IReadOnlyList<EntityId> currentPath,
        IReadOnlyList<EntityId> proposedPath)
    {
        AffectedNodeId = affectedNodeId;
        CurrentPath = new ReadOnlyCollection<EntityId>(currentPath.ToArray());
        ProposedPath = new ReadOnlyCollection<EntityId>(proposedPath.ToArray());
    }

    public EntityId AffectedNodeId { get; }

    public IReadOnlyList<EntityId> CurrentPath { get; }

    public IReadOnlyList<EntityId> ProposedPath { get; }
}

public sealed class ScopeContextEntry
{
    internal ScopeContextEntry(
        EntityId nodeId,
        IEnumerable<ScopeLineageEvidence> lineages,
        GraphNode? currentNode,
        GraphNode? proposedNode)
    {
        NodeId = nodeId;
        Lineages = new ReadOnlyCollection<ScopeLineageEvidence>(lineages.ToArray());
        CurrentNode = currentNode;
        ProposedNode = proposedNode;
    }

    public EntityId NodeId { get; }

    public IReadOnlyList<ScopeLineageEvidence> Lineages { get; }

    public GraphNode? CurrentNode { get; }

    public GraphNode? ProposedNode { get; }
}

public sealed class AffectedEdgeChange
{
    internal AffectedEdgeChange(
        GraphOperation operation,
        GraphEdge? currentEdge,
        GraphEdge? proposedEdge)
    {
        Operation = operation;
        CurrentEdge = currentEdge;
        ProposedEdge = proposedEdge;
    }

    public GraphOperation Operation { get; }

    public EntityId EdgeId => Operation.EntityId;

    public GraphEdge? CurrentEdge { get; }

    public GraphEdge? ProposedEdge { get; }
}

public sealed class AffectedAnalysis
{
    internal AffectedAnalysis(
        ProjectGraph currentGraph,
        ProjectGraph proposedGraph,
        GraphValidationResult currentValidation,
        GraphValidationResult proposedValidation,
        GraphOperationBatch operations,
        AffectedAnalysisStatus status,
        IEnumerable<EntityId> directNodeIds,
        IEnumerable<EntityId> seedNodeIds,
        IEnumerable<AffectedNode> affectedNodes,
        IEnumerable<AffectedEdgeChange> edgeChanges,
        IEnumerable<ScopeContextEntry> scopeContext,
        IEnumerable<AffectedOmission> omissions)
    {
        CurrentGraph = currentGraph;
        ProposedGraph = proposedGraph;
        CurrentValidation = currentValidation;
        ProposedValidation = proposedValidation;
        Operations = operations;
        Status = status;
        DirectNodeIds = Sorted(directNodeIds);
        SeedNodeIds = Sorted(seedNodeIds);
        AffectedNodes = new ReadOnlyCollection<AffectedNode>(affectedNodes.OrderBy(node => node.NodeId).ToArray());
        EdgeChanges = new ReadOnlyCollection<AffectedEdgeChange>(edgeChanges.OrderBy(change => change.EdgeId).ToArray());
        ScopeContext = new ReadOnlyCollection<ScopeContextEntry>(scopeContext.OrderBy(entry => entry.NodeId).ToArray());
        Omissions = new ReadOnlyCollection<AffectedOmission>(omissions
            .OrderBy(omission => omission.Reason)
            .ThenBy(omission => omission.SourceNodeId)
            .ThenBy(omission => omission.TargetNodeId)
            .ThenBy(omission => omission.EdgeId)
            .ThenBy(omission => omission.Depth)
            .ToArray());
    }

    public ProjectGraph CurrentGraph { get; }

    public ProjectGraph ProposedGraph { get; }

    public GraphValidationResult CurrentValidation { get; }

    public GraphValidationResult ProposedValidation { get; }

    public GraphOperationBatch Operations { get; }

    public AffectedAnalysisStatus Status { get; }

    public IReadOnlyList<EntityId> DirectNodeIds { get; }

    public IReadOnlyList<EntityId> SeedNodeIds { get; }

    public IReadOnlyList<AffectedNode> AffectedNodes { get; }

    public IReadOnlyList<AffectedEdgeChange> EdgeChanges { get; }

    /// <summary>Only ancestors not already in AffectedNodes require coverage.</summary>
    public IReadOnlyList<ScopeContextEntry> ScopeContext { get; }

    public IReadOnlyList<AffectedOmission> Omissions { get; }

    public bool IsComplete => Status == AffectedAnalysisStatus.Complete;

    public bool IsInconclusive => Status == AffectedAnalysisStatus.Inconclusive;

    public AffectedReviewSession CreateReviewSession() => new(this);

    private static IReadOnlyList<EntityId> Sorted(IEnumerable<EntityId> ids) =>
        new ReadOnlyCollection<EntityId>(ids.Distinct().OrderBy(id => id).ToArray());
}

public sealed class AffectedAnalyzer
{
    public AffectedAnalysis Analyze(
        ProjectGraph currentGraph,
        GraphProjectionResult projection,
        AffectedAnalysisOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(currentGraph);
        ArgumentNullException.ThrowIfNull(projection);
        options ??= new AffectedAnalysisOptions();

        var currentValidation = new GraphValidator().Validate(currentGraph);
        var proposedGraph = projection.Graph;
        var proposedValidation = projection.Validation;
        var currentIndex = currentValidation.Index;
        var proposedIndex = proposedValidation.Index;
        var operations = projection.Operations;
        var directNodeIds = operations.Operations
            .Where(operation => operation.EntityKind == GraphEntityKind.Node)
            .Select(operation => operation.EntityId)
            .ToHashSet();
        var seedCandidates = new List<SeedCandidate>();
        foreach (var nodeId in directNodeIds.OrderBy(id => id))
        {
            seedCandidates.Add(new SeedCandidate(
                nodeId,
                true,
                new AffectedPath([nodeId], [])));
        }

        var edgeChanges = operations.Operations
            .Where(operation => operation.EntityKind == GraphEntityKind.Edge)
            .Select(operation => new AffectedEdgeChange(
                operation,
                currentIndex.EdgesById.TryGetValue(operation.EntityId, out var currentEdge) ? currentEdge : null,
                proposedIndex.EdgesById.TryGetValue(operation.EntityId, out var proposedEdge) ? proposedEdge : null))
            .ToArray();
        var currentArcs = currentIndex.ReviewArcs;
        var proposedArcs = proposedIndex.ReviewArcs;
        var unionArcs = currentArcs.Concat(proposedArcs).Distinct().ToArray();
        var unionArcsBySource = unionArcs
            .GroupBy(arc => arc.From)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(arc => arc.To).ThenBy(arc => arc.EdgeId).ToArray());

        foreach (var change in edgeChanges)
        {
            foreach (var arc in unionArcs.Where(arc => arc.EdgeId == change.EdgeId).OrderBy(arc => arc.From).ThenBy(arc => arc.To))
            {
                seedCandidates.Add(new SeedCandidate(
                    arc.From,
                    false,
                    new AffectedPath([arc.From], [])));
            }
        }

        var scopeExpansionNodes = directNodeIds.ToHashSet();
        foreach (var nodeId in directNodeIds)
        {
            scopeExpansionNodes.UnionWith(currentIndex.GetScopeDescendants(nodeId));
            scopeExpansionNodes.UnionWith(proposedIndex.GetScopeDescendants(nodeId));
        }

        var scopeArcs = currentIndex.Graph.Edges
            .Concat(proposedIndex.Graph.Edges)
            .Where(edge => GraphIndex.IsScopeParent(edge) && scopeExpansionNodes.Contains(edge.Target))
            .Select(edge => new ReviewArc(edge.Id, edge.Target, edge.Source))
            .Distinct()
            .OrderBy(arc => arc.From)
            .ThenBy(arc => arc.To)
            .ThenBy(arc => arc.EdgeId)
            .ToArray();
        foreach (var arc in scopeArcs)
        {
            if (!unionArcsBySource.TryGetValue(arc.From, out var existing))
            {
                unionArcsBySource[arc.From] = [arc];
            }
            else
            {
                unionArcsBySource[arc.From] = existing
                    .Concat([arc])
                    .Distinct()
                    .OrderBy(candidate => candidate.To)
                    .ThenBy(candidate => candidate.EdgeId)
                    .ToArray();
            }
        }

        var omissions = new List<AffectedOmission>();
        var candidates = seedCandidates
            .OrderByDescending(seed => seed.IsDirectChange)
            .ThenBy(seed => seed.NodeId)
            .ToArray();
        var best = new Dictionary<EntityId, AffectedCandidate>();
        var queue = new Queue<EntityId>();
        var affectedLimit = Math.Min(options.MaxAffectedNodes, options.MaxOutputItems);
        foreach (var candidate in candidates)
        {
            if (best.ContainsKey(candidate.NodeId))
            {
                if (candidate.IsDirectChange) best[candidate.NodeId] = best[candidate.NodeId] with { IsDirectChange = true };
                continue;
            }

            if (best.Count >= affectedLimit)
            {
                omissions.Add(new AffectedOmission(
                    options.MaxOutputItems <= options.MaxAffectedNodes
                        ? AffectedOmissionReason.OutputLimit
                        : AffectedOmissionReason.AffectedNodeLimit,
                    null,
                    candidate.NodeId,
                    null,
                    0,
                    "A direct or edge-derived seed was omitted by the affected-node output bound."));
                continue;
            }

            best.Add(candidate.NodeId, new AffectedCandidate(
                candidate.NodeId,
                candidate.IsDirectChange,
                candidate.Path.Nodes.Count - 1,
                candidate.Path));
            queue.Enqueue(candidate.NodeId);
        }

        var cancelled = false;
        while (queue.Count > 0)
        {
            if (options.CancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                omissions.Add(new AffectedOmission(
                    AffectedOmissionReason.Cancelled,
                    queue.Peek(),
                    null,
                    null,
                    best[queue.Peek()].Distance,
                    "Affected traversal was cancelled before all queued nodes were expanded."));
                break;
            }

            var sourceId = queue.Dequeue();
            var source = best[sourceId];
            if (!unionArcsBySource.TryGetValue(sourceId, out var arcs)) continue;
            foreach (var arc in arcs)
            {
                var depth = source.Distance + 1;
                if (depth > options.MaxTraversalDepth)
                {
                    omissions.Add(new AffectedOmission(
                        AffectedOmissionReason.TraversalDepthLimit,
                        sourceId,
                        arc.To,
                        arc.EdgeId,
                        depth,
                        $"Propagation through edge '{arc.EdgeId.Value}' exceeded depth limit {options.MaxTraversalDepth}."));
                    continue;
                }

                if (best.ContainsKey(arc.To)) continue;
                if (best.Count >= affectedLimit)
                {
                    omissions.Add(new AffectedOmission(
                        options.MaxOutputItems <= options.MaxAffectedNodes
                            ? AffectedOmissionReason.OutputLimit
                            : AffectedOmissionReason.AffectedNodeLimit,
                        sourceId,
                        arc.To,
                        arc.EdgeId,
                        depth,
                        "An affected node was omitted by the configured affected/output bound."));
                    continue;
                }

                var pathNodes = source.Path.Nodes.Concat([arc.To]);
                var pathEdges = source.Path.Edges.Concat([arc.EdgeId]);
                best.Add(arc.To, new AffectedCandidate(
                    arc.To,
                    false,
                    depth,
                    new AffectedPath(pathNodes, pathEdges)));
                queue.Enqueue(arc.To);
            }
        }

        if (cancelled)
        {
            while (queue.Count > 0)
            {
                var omitted = queue.Dequeue();
                omissions.Add(new AffectedOmission(
                    AffectedOmissionReason.Cancelled,
                    omitted,
                    null,
                    null,
                    best[omitted].Distance,
                    "A queued affected node was not expanded after cancellation."));
            }
        }

        var affectedNodes = best.Values
            .Select(candidate => new AffectedNode(
                candidate.NodeId,
                candidate.IsDirectChange,
                candidate.Distance,
                candidate.Path,
                currentIndex.NodesById.TryGetValue(candidate.NodeId, out var currentNode) ? currentNode : null,
                proposedIndex.NodesById.TryGetValue(candidate.NodeId, out var proposedNode) ? proposedNode : null))
            .ToArray();

        var contextByNode = new Dictionary<EntityId, List<ScopeLineageEvidence>>();
        foreach (var affectedNode in affectedNodes.OrderBy(node => node.NodeId))
        {
            var currentPath = currentIndex.NodesByIdIncludingDuplicates.ContainsKey(affectedNode.NodeId)
                ? currentIndex.GetScopeUpstreamPath(affectedNode.NodeId)
                : [];
            var proposedPath = proposedIndex.NodesByIdIncludingDuplicates.ContainsKey(affectedNode.NodeId)
                ? proposedIndex.GetScopeUpstreamPath(affectedNode.NodeId)
                : [];
            var lineage = new ScopeLineageEvidence(affectedNode.NodeId, currentPath, proposedPath);
            foreach (var contextId in currentPath.Concat(proposedPath).Distinct().Where(id => !best.ContainsKey(id)))
            {
                if (!contextByNode.TryGetValue(contextId, out var lineages))
                {
                    lineages = [];
                    contextByNode.Add(contextId, lineages);
                }

                lineages.Add(lineage);
            }
        }

        var scopeContext = new List<ScopeContextEntry>();
        foreach (var context in contextByNode.OrderBy(pair => pair.Key))
        {
            if (affectedNodes.Length + scopeContext.Count >= options.MaxOutputItems)
            {
                omissions.Add(new AffectedOmission(
                    AffectedOmissionReason.OutputLimit,
                    null,
                    context.Key,
                    null,
                    null,
                    "A required scope-context node was omitted by the configured output bound."));
                continue;
            }

            scopeContext.Add(new ScopeContextEntry(
                context.Key,
                context.Value.OrderBy(lineage => lineage.AffectedNodeId),
                currentIndex.NodesById.TryGetValue(context.Key, out var currentNode) ? currentNode : null,
                proposedIndex.NodesById.TryGetValue(context.Key, out var proposedNode) ? proposedNode : null));
        }

        var status = omissions.Count == 0
            ? AffectedAnalysisStatus.Complete
            : AffectedAnalysisStatus.Inconclusive;
        return new AffectedAnalysis(
            currentGraph,
            proposedGraph,
            currentValidation,
            proposedValidation,
            operations,
            status,
            directNodeIds,
            best.Keys,
            affectedNodes,
            edgeChanges,
            scopeContext,
            omissions);
    }

    private sealed record SeedCandidate(EntityId NodeId, bool IsDirectChange, AffectedPath Path);

    private sealed record AffectedCandidate(
        EntityId NodeId,
        bool IsDirectChange,
        int Distance,
        AffectedPath Path);
}

public enum ReviewDispositionKind
{
    Pending,
    Updated,
    ReviewedNoChange,
    NotApplicable,
}

public sealed record ReviewDisposition(
    EntityId NodeId,
    ReviewDispositionKind Kind,
    string? Rationale);

public sealed record ReviewRefreshResult(
    IReadOnlyList<EntityId> InvalidatedDispositionNodeIds,
    IReadOnlyList<EntityId> InvalidatedContextNodeIds);

public sealed class ReviewReadinessResult
{
    internal ReviewReadinessResult(
        bool isReady,
        AffectedAnalysisStatus analysisStatus,
        ValidationStatus proposedValidationStatus,
        IEnumerable<EntityId> pendingNodeIds,
        IEnumerable<EntityId> missingContextNodeIds,
        IEnumerable<string> blockers)
    {
        IsReady = isReady;
        AnalysisStatus = analysisStatus;
        ProposedValidationStatus = proposedValidationStatus;
        PendingNodeIds = new ReadOnlyCollection<EntityId>(pendingNodeIds.OrderBy(id => id).ToArray());
        MissingContextNodeIds = new ReadOnlyCollection<EntityId>(missingContextNodeIds.OrderBy(id => id).ToArray());
        Blockers = new ReadOnlyCollection<string>(blockers.OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }

    public bool IsReady { get; }

    public AffectedAnalysisStatus AnalysisStatus { get; }

    public ValidationStatus ProposedValidationStatus { get; }

    public IReadOnlyList<EntityId> PendingNodeIds { get; }

    public IReadOnlyList<EntityId> MissingContextNodeIds { get; }

    public IReadOnlyList<string> Blockers { get; }
}

/// <summary>
/// Process-local manual review state. It is never persisted with the graph.
/// </summary>
public sealed class AffectedReviewSession
{
    private AffectedAnalysis _analysis;
    private readonly Dictionary<EntityId, ReviewDisposition> _dispositions = [];
    private readonly HashSet<EntityId> _presentedContext = [];

    internal AffectedReviewSession(AffectedAnalysis analysis)
    {
        _analysis = analysis;
        ResetDispositions(analysis.AffectedNodes.Select(node => node.NodeId));
    }

    public AffectedAnalysis Analysis => _analysis;

    public IReadOnlyList<ReviewDisposition> Dispositions => new ReadOnlyCollection<ReviewDisposition>(
        _dispositions.Values.OrderBy(disposition => disposition.NodeId).ToArray());

    public IReadOnlyList<EntityId> PresentedContextNodeIds => new ReadOnlyCollection<EntityId>(
        _presentedContext.OrderBy(id => id).ToArray());

    public IReadOnlyList<EntityId> PendingNodeIds => new ReadOnlyCollection<EntityId>(
        _dispositions.Values
            .Where(disposition => disposition.Kind == ReviewDispositionKind.Pending)
            .Select(disposition => disposition.NodeId)
            .OrderBy(id => id)
            .ToArray());

    public void SetDisposition(EntityId nodeId, ReviewDispositionKind kind, string? rationale = null)
    {
        if (!_dispositions.ContainsKey(nodeId))
        {
            throw new ArgumentException($"Node '{nodeId.Value}' is not in the affected set.", nameof(nodeId));
        }

        var affected = _analysis.AffectedNodes.Single(node => node.NodeId == nodeId);
        if (kind == ReviewDispositionKind.Updated && !affected.IsDirectChange)
        {
            throw new ArgumentException("Only directly changed nodes may use the Updated disposition.", nameof(kind));
        }

        if (kind == ReviewDispositionKind.NotApplicable && string.IsNullOrWhiteSpace(rationale))
        {
            throw new ArgumentException("Not-applicable requires a rationale.", nameof(rationale));
        }

        if (kind != ReviewDispositionKind.NotApplicable) rationale = null;
        _dispositions[nodeId] = new ReviewDisposition(nodeId, kind, rationale);
    }

    public void MarkContextPresented(EntityId nodeId)
    {
        if (!_analysis.ScopeContext.Any(entry => entry.NodeId == nodeId))
        {
            throw new ArgumentException($"Node '{nodeId.Value}' is not a context-only node.", nameof(nodeId));
        }

        _presentedContext.Add(nodeId);
    }

    public ReviewReadinessResult EvaluateReadiness()
    {
        var pending = PendingNodeIds;
        var requiredContext = _analysis.ScopeContext.Select(entry => entry.NodeId).ToHashSet();
        var missingContext = requiredContext.Except(_presentedContext).OrderBy(id => id).ToArray();
        var blockers = new List<string>();
        if (!_analysis.IsComplete) blockers.Add("Affected analysis is inconclusive.");
        if (!_analysis.ProposedValidation.IsValid) blockers.Add("The proposed graph is not structurally valid.");
        if (!_analysis.CurrentValidation.IsValid) blockers.Add("The current graph is not structurally valid.");
        if (pending.Count > 0) blockers.Add("Affected nodes still have pending review dispositions.");
        if (missingContext.Length > 0) blockers.Add("Required scope context has not been presented.");
        return new ReviewReadinessResult(
            blockers.Count == 0,
            _analysis.Status,
            _analysis.ProposedValidation.Status,
            pending,
            missingContext,
            blockers);
    }

    public ReviewRefreshResult Refresh(AffectedAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        var oldAffected = _analysis.AffectedNodes.ToDictionary(node => node.NodeId);
        var oldContext = _analysis.ScopeContext.ToDictionary(entry => entry.NodeId);
        var newAffected = analysis.AffectedNodes.ToDictionary(node => node.NodeId);
        var newContext = analysis.ScopeContext.ToDictionary(entry => entry.NodeId);
        var invalidatedDispositions = new List<EntityId>();
        var invalidatedContext = new List<EntityId>();

        foreach (var nodeId in _dispositions.Keys.ToArray())
        {
            if (!newAffected.TryGetValue(nodeId, out var current) ||
                !oldAffected.TryGetValue(nodeId, out var previous) ||
                !EvidenceEquals(previous, current))
            {
                _dispositions.Remove(nodeId);
                invalidatedDispositions.Add(nodeId);
            }
        }

        ResetDispositions(newAffected.Keys);
        foreach (var entry in newContext)
        {
            if (!_presentedContext.Contains(entry.Key)) continue;
            if (!oldContext.TryGetValue(entry.Key, out var previous) ||
                !ContextEquals(previous, entry.Value))
            {
                _presentedContext.Remove(entry.Key);
                invalidatedContext.Add(entry.Key);
            }
        }

        foreach (var nodeId in _presentedContext.Where(id => !newContext.ContainsKey(id)).ToArray())
        {
            _presentedContext.Remove(nodeId);
            if (newAffected.ContainsKey(nodeId)) invalidatedContext.Add(nodeId);
        }

        _analysis = analysis;
        return new ReviewRefreshResult(
            new ReadOnlyCollection<EntityId>(invalidatedDispositions.Distinct().OrderBy(id => id).ToArray()),
            new ReadOnlyCollection<EntityId>(invalidatedContext.Distinct().OrderBy(id => id).ToArray()));
    }

    private void ResetDispositions(IEnumerable<EntityId> nodeIds)
    {
        foreach (var nodeId in nodeIds)
        {
            if (!_dispositions.ContainsKey(nodeId))
            {
                _dispositions[nodeId] = new ReviewDisposition(nodeId, ReviewDispositionKind.Pending, null);
            }
        }
    }

    private static bool EvidenceEquals(AffectedNode left, AffectedNode right) =>
        left.NodeId == right.NodeId &&
        left.IsDirectChange == right.IsDirectChange &&
        left.Distance == right.Distance &&
        Equals(left.CurrentNode, right.CurrentNode) &&
        Equals(left.ProposedNode, right.ProposedNode) &&
        PathsEqual(left.Explanation, right.Explanation);

    private static bool ContextEquals(ScopeContextEntry left, ScopeContextEntry right) =>
        left.NodeId == right.NodeId &&
        Equals(left.CurrentNode, right.CurrentNode) &&
        Equals(left.ProposedNode, right.ProposedNode) &&
        left.Lineages.Count == right.Lineages.Count &&
        left.Lineages.Zip(right.Lineages).All(pair =>
            pair.First.AffectedNodeId == pair.Second.AffectedNodeId &&
            pair.First.CurrentPath.SequenceEqual(pair.Second.CurrentPath) &&
            pair.First.ProposedPath.SequenceEqual(pair.Second.ProposedPath));

    private static bool PathsEqual(AffectedPath left, AffectedPath right) =>
        left.Nodes.SequenceEqual(right.Nodes) && left.Edges.SequenceEqual(right.Edges);
}
