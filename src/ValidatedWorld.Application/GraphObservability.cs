using System.Collections.ObjectModel;
using ValidatedWorld.Core;
using ValidatedWorld.Validation;

namespace ValidatedWorld.Application;

/// <summary>Bounds the item sections returned by a graph observability report.</summary>
public sealed class GraphObservabilityOptions
{
    private int _maxItems = 100;

    public int MaxItems
    {
        get => _maxItems;
        init => _maxItems = value is >= 1 and <= QueryPageRequest.MaximumLimit
            ? value
            : throw new ArgumentOutOfRangeException(
                nameof(MaxItems),
                $"An observability report must return between 1 and {QueryPageRequest.MaximumLimit} items per section.");
    }

    public CancellationToken CancellationToken { get; init; }
}

public sealed record GraphReportSection<T>(
    int TotalCount,
    IReadOnlyList<T> Items,
    int OmittedCount);

public sealed record ScopeCoverageSummary(
    int TotalNodeCount,
    int ScopeParentEdgeCount,
    int NodesWithExactlyOneScopeParent,
    int NodesReachingPurpose,
    double CoveragePercent);

public sealed record ReviewFanOutHotspot(
    EntityId NodeId,
    int OutgoingReviewArcCount,
    int IncomingReviewArcCount);

public sealed record IsolatedClaim(EntityId NodeId, string? Kind);

public sealed record MissingRationale(
    EntityId EdgeId,
    EntityId Source,
    EntityId Target,
    string Relationship);

public sealed record TagUsage(string Tag, int NodeCount, int EdgeCount)
{
    public int TotalCount => NodeCount + EdgeCount;
}

/// <summary>
/// Read-only, deterministic graph-quality indicators. These are author
/// diagnostics and heuristics; they do not validate semantic truth or add
/// dependency edges.
/// </summary>
public sealed record GraphObservabilityReport(
    int NodeCount,
    int EdgeCount,
    int SemanticReviewArcCount,
    ScopeCoverageSummary ScopeCoverage,
    GraphReportSection<EntityId> UnreachableNodeIds,
    GraphReportSection<ReviewFanOutHotspot> ReviewFanOutHotspots,
    GraphReportSection<IsolatedClaim> SuspiciouslyIsolatedClaims,
    GraphReportSection<MissingRationale> MissingRationales,
    GraphReportSection<TagUsage> TagUsage,
    int UntaggedNodeCount,
    int UntaggedEdgeCount,
    bool WasCancelled)
{
    public IReadOnlyList<QueryOmission> Omissions => WasCancelled
        ? new ReadOnlyCollection<QueryOmission>([
            new QueryOmission(
                QueryOmissionReason.Cancelled,
                null,
                "Graph observability analysis was cancelled before all diagnostics were computed."),
        ])
        : Array.Empty<QueryOmission>();
}

public sealed partial class ProjectQueries
{
    /// <summary>
    /// Builds bounded graph-health summaries from the immutable project
    /// snapshot. Items are deterministic and truncated independently per
    /// report section.
    /// </summary>
    public GraphObservabilityReport GetGraphObservability(GraphObservabilityOptions? options = null)
    {
        options ??= new GraphObservabilityOptions();
        var graph = Project.Graph;
        var nodes = graph.Nodes;
        var edges = graph.Edges;
        var semanticEdges = edges.Where(edge => !IsScopeParent(edge)).ToArray();
        var arcs = _index.ReviewArcs;
        var wasCancelled = options.CancellationToken.IsCancellationRequested;

        var scopePaths = nodes
            .GroupBy(node => node.Id)
            .ToDictionary(group => group.Key, group => _index.GetScopeUpstreamPath(group.Key));
        var nodesWithExactlyOneParent = nodes.Count(node => _index.GetScopeParentEdges(node.Id).Count == 1);
        var nodesReachingPurpose = scopePaths.Count(pair => pair.Value.LastOrDefault() == graph.PurposeNodeId);
        var coveragePercent = nodes.Count == 0
            ? 100d
            : Math.Round(nodesReachingPurpose * 100d / nodes.Count, 2, MidpointRounding.AwayFromZero);
        var scopeCoverage = new ScopeCoverageSummary(
            nodes.Count,
            edges.Count(IsScopeParent),
            nodesWithExactlyOneParent,
            nodesReachingPurpose,
            coveragePercent);

        var unreachable = nodes
            .Where(node => !scopePaths.TryGetValue(node.Id, out var path) ||
                          path.LastOrDefault() != graph.PurposeNodeId)
            .Select(node => node.Id)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();

        var outgoing = arcs.GroupBy(arc => arc.From).ToDictionary(group => group.Key, group => group.Count());
        var incoming = arcs.GroupBy(arc => arc.To).ToDictionary(group => group.Key, group => group.Count());
        var fanOut = outgoing
            .Select(pair => new ReviewFanOutHotspot(
                pair.Key,
                pair.Value,
                incoming.GetValueOrDefault(pair.Key)))
            .OrderByDescending(item => item.OutgoingReviewArcCount)
            .ThenBy(item => item.NodeId)
            .ToArray();

        var connected = arcs
            .SelectMany(arc => new[] { arc.From, arc.To })
            .ToHashSet();
        var isolated = nodes
            .Where(node => node.Id != graph.PurposeNodeId &&
                          !StringComparer.Ordinal.Equals(node.Kind, "scope") &&
                          !connected.Contains(node.Id))
            .Select(node => new IsolatedClaim(node.Id, node.Kind))
            .OrderBy(item => item.NodeId)
            .ToArray();

        var missingRationales = semanticEdges
            .Where(edge => string.IsNullOrWhiteSpace(edge.Rationale))
            .Select(edge => new MissingRationale(edge.Id, edge.Source, edge.Target, edge.Relationship))
            .OrderBy(item => item.EdgeId)
            .ToArray();

        var nodeTagCounts = nodes
            .SelectMany(node => node.Tags.Select(tag => (Tag: tag, Node: 1)))
            .GroupBy(item => item.Tag, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var edgeTagCounts = edges
            .SelectMany(edge => edge.Tags.Select(tag => (Tag: tag, Edge: 1)))
            .GroupBy(item => item.Tag, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var tagUsage = nodeTagCounts.Keys
            .Union(edgeTagCounts.Keys, StringComparer.Ordinal)
            .Select(tag => new TagUsage(tag, nodeTagCounts.GetValueOrDefault(tag), edgeTagCounts.GetValueOrDefault(tag)))
            .OrderByDescending(item => item.TotalCount)
            .ThenBy(item => item.Tag, StringComparer.Ordinal)
            .ToArray();

        return new GraphObservabilityReport(
            nodes.Count,
            edges.Count,
            arcs.Count,
            scopeCoverage,
            Section(unreachable, options.MaxItems),
            Section(fanOut, options.MaxItems),
            Section(isolated, options.MaxItems),
            Section(missingRationales, options.MaxItems),
            Section(tagUsage, options.MaxItems),
            nodes.Count(node => node.Tags.Count == 0),
            edges.Count(edge => edge.Tags.Count == 0),
            wasCancelled);
    }

    public GraphObservabilityReport GetGraphHealth(GraphObservabilityOptions? options = null) =>
        GetGraphObservability(options);

    private static GraphReportSection<T> Section<T>(IReadOnlyList<T> values, int maxItems) =>
        new(values.Count, new ReadOnlyCollection<T>(values.Take(maxItems).ToArray()), Math.Max(0, values.Count - maxItems));

    private static bool IsScopeParent(GraphEdge edge) =>
        StringComparer.Ordinal.Equals(edge.Relationship, "scope-parent");
}
