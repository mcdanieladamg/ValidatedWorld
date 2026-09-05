using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ValidatedWorld.Core;
using ValidatedWorld.Validation;

namespace ValidatedWorld.Application;

public enum ProjectQueryErrorCode
{
    ProjectMismatch,
    NodeNotFound,
    EdgeNotFound,
    InvalidCursor,
}

public sealed class ProjectQueryException : InvalidOperationException
{
    public ProjectQueryException(ProjectQueryErrorCode code, string message)
        : base(message) => Code = code;

    public ProjectQueryErrorCode Code { get; }
}

public enum QueryOmissionReason
{
    OutputLimit,
    TraversalDepthLimit,
    VisitedNodeLimit,
    Cancelled,
}

public sealed record QueryOmission(QueryOmissionReason Reason, int? RemainingCount, string Message);

public sealed class QueryPageRequest
{
    public const int DefaultLimit = 100;
    public const int MaximumLimit = 1_000;

    public QueryPageRequest(int limit = DefaultLimit, string? cursor = null)
    {
        if (limit is < 1 or > MaximumLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                $"A query page must contain between 1 and {MaximumLimit} items.");
        }

        Limit = limit;
        Cursor = cursor;
    }

    public int Limit { get; }

    public string? Cursor { get; }
}

public sealed class QueryTraversalOptions
{
    private int _maxDepth = 10_000;
    private int _maxVisitedNodes = 100_000;

    public int MaxDepth
    {
        get => _maxDepth;
        init => _maxDepth = Positive(value, nameof(MaxDepth));
    }

    public int MaxVisitedNodes
    {
        get => _maxVisitedNodes;
        init => _maxVisitedNodes = Positive(value, nameof(MaxVisitedNodes));
    }

    public CancellationToken CancellationToken { get; init; }

    private static int Positive(int value, string name) => value > 0
        ? value
        : throw new ArgumentOutOfRangeException(name, "The limit must be positive.");
}

public sealed class QueryPage<T>
{
    internal QueryPage(IEnumerable<T> items, int totalCount, string? nextCursor, QueryOmission? omission)
    {
        Items = new ReadOnlyCollection<T>(items.ToArray());
        TotalCount = totalCount;
        NextCursor = nextCursor;
        Omission = omission;
    }

    public IReadOnlyList<T> Items { get; }

    public int TotalCount { get; }

    public string? NextCursor { get; }

    public QueryOmission? Omission { get; }
}

public sealed record GraphSearchHit(GraphEntityKind EntityKind, EntityId EntityId, GraphNode? Node, GraphEdge? Edge);

public sealed record NeighborEntry(EntityId NodeId, GraphEdge Edge, bool IsOutgoing);

public sealed record DependencyEntry(ReviewArc Arc, bool IsOutgoing);

public sealed class ScopeQueryResult
{
    internal ScopeQueryResult(
        GraphNode node,
        IEnumerable<GraphNode> upstream,
        QueryPage<GraphNode> descendants,
        IEnumerable<QueryOmission> omissions)
    {
        Node = node;
        Upstream = new ReadOnlyCollection<GraphNode>(upstream.ToArray());
        Descendants = descendants;
        Omissions = new ReadOnlyCollection<QueryOmission>(omissions.ToArray());
    }

    public GraphNode Node { get; }

    public IReadOnlyList<GraphNode> Upstream { get; }

    public QueryPage<GraphNode> Descendants { get; }

    public IReadOnlyList<QueryOmission> Omissions { get; }
}

public sealed class DependencyPathResult
{
    internal DependencyPathResult(
        bool found,
        IEnumerable<EntityId> nodes,
        IEnumerable<EntityId> edges,
        IEnumerable<QueryOmission> omissions)
    {
        Found = found;
        Nodes = new ReadOnlyCollection<EntityId>(nodes.ToArray());
        Edges = new ReadOnlyCollection<EntityId>(edges.ToArray());
        Omissions = new ReadOnlyCollection<QueryOmission>(omissions.ToArray());
    }

    public bool Found { get; }

    public IReadOnlyList<EntityId> Nodes { get; }

    public IReadOnlyList<EntityId> Edges { get; }

    public IReadOnlyList<QueryOmission> Omissions { get; }
}

public sealed class ScopeContextResult
{
    internal ScopeContextResult(
        IEnumerable<EntityId> requestedNodeIds,
        IEnumerable<GraphNode> contextNodes,
        IEnumerable<QueryOmission> omissions)
    {
        RequestedNodeIds = new ReadOnlyCollection<EntityId>(requestedNodeIds.OrderBy(id => id).ToArray());
        ContextNodes = new ReadOnlyCollection<GraphNode>(contextNodes.OrderBy(node => node.Id).ToArray());
        Omissions = new ReadOnlyCollection<QueryOmission>(omissions.ToArray());
    }

    public IReadOnlyList<EntityId> RequestedNodeIds { get; }

    public IReadOnlyList<GraphNode> ContextNodes { get; }

    public IReadOnlyList<QueryOmission> Omissions { get; }
}

/// <summary>Bounded deterministic reads over one verified immutable project snapshot.</summary>
public sealed partial class ProjectQueries
{
    private readonly GraphIndex _index;

    internal ProjectQueries(StoredProject project)
    {
        Project = project;
        _index = new GraphIndex(project.Graph);
    }

    public StoredProject Project { get; }

    public GraphNode GetNode(EntityId nodeId) => _index.NodesById.TryGetValue(nodeId, out var node)
        ? node
        : throw new ProjectQueryException(
            ProjectQueryErrorCode.NodeNotFound,
            $"Node '{nodeId.Value}' does not exist in project '{Project.Graph.ProjectId.Value}'.");

    public GraphEdge GetEdge(EntityId edgeId) => _index.EdgesById.TryGetValue(edgeId, out var edge)
        ? edge
        : throw new ProjectQueryException(
            ProjectQueryErrorCode.EdgeNotFound,
            $"Edge '{edgeId.Value}' does not exist in project '{Project.Graph.ProjectId.Value}'.");

    public QueryPage<GraphNode> ListNodes(QueryPageRequest? request = null) =>
        Page(Project.Graph.Nodes, Signature("nodes", Project.StateFingerprint), request);

    public QueryPage<GraphEdge> ListEdges(QueryPageRequest? request = null) =>
        Page(Project.Graph.Edges, Signature("edges", Project.StateFingerprint), request);

    public QueryPage<GraphSearchHit> Search(string text, QueryPageRequest? request = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Search text cannot be empty or whitespace-only.", nameof(text));
        }

        if (text.Length > GraphLimits.TextMaxLength)
        {
            throw new ArgumentException(
                $"Search text cannot exceed {GraphLimits.TextMaxLength} characters.",
                nameof(text));
        }

        var hits = Project.Graph.Nodes
            .Where(node => Matches(node.Id.Value, text) || Matches(node.Text, text) ||
                           Matches(node.Kind, text) || node.Tags.Any(tag => Matches(tag, text)))
            .Select(node => new GraphSearchHit(GraphEntityKind.Node, node.Id, node, null))
            .Concat(Project.Graph.Edges
                .Where(edge => Matches(edge.Id.Value, text) || Matches(edge.Relationship, text) ||
                               Matches(edge.Rationale, text) || edge.Tags.Any(tag => Matches(tag, text)))
                .Select(edge => new GraphSearchHit(GraphEntityKind.Edge, edge.Id, null, edge)))
            .OrderBy(hit => hit.EntityId)
            .ThenBy(hit => hit.EntityKind)
            .ToArray();
        return Page(hits, Signature("search", Project.StateFingerprint + text), request);
    }

    public QueryPage<GraphSearchHit> SearchByTag(string tag, QueryPageRequest? request = null)
    {
        if (string.IsNullOrWhiteSpace(tag) || tag.Length > GraphLimits.MetadataNameMaxLength ||
            tag.Any(char.IsControl))
        {
            throw new ArgumentException(
                "A tag query must be non-empty, bounded, and free of control characters.",
                nameof(tag));
        }

        var hits = Project.Graph.Nodes
            .Where(node => node.Tags.Contains(tag, StringComparer.Ordinal))
            .Select(node => new GraphSearchHit(GraphEntityKind.Node, node.Id, node, null))
            .Concat(Project.Graph.Edges
                .Where(edge => edge.Tags.Contains(tag, StringComparer.Ordinal))
                .Select(edge => new GraphSearchHit(GraphEntityKind.Edge, edge.Id, null, edge)))
            .OrderBy(hit => hit.EntityId)
            .ThenBy(hit => hit.EntityKind)
            .ToArray();
        return Page(hits, Signature("tag", Project.StateFingerprint + tag), request);
    }

    public ScopeQueryResult GetScope(
        EntityId nodeId,
        QueryPageRequest? descendantsPage = null,
        QueryTraversalOptions? options = null)
    {
        var node = GetNode(nodeId);
        options ??= new QueryTraversalOptions();
        var omissions = new List<QueryOmission>();
        var upstreamIds = TraverseScopeUpstream(nodeId, options, omissions);
        var descendantIds = TraverseScopeDescendants(nodeId, options, omissions);
        var descendants = descendantIds.Select(GetNode).ToArray();
        var page = Page(
            descendants,
            Signature("scope-descendants", Project.StateFingerprint + nodeId.Value),
            descendantsPage);
        return new ScopeQueryResult(node, upstreamIds.Skip(1).Select(GetNode), page, omissions);
    }

    public QueryPage<NeighborEntry> GetNeighbors(EntityId nodeId, QueryPageRequest? request = null)
    {
        GetNode(nodeId);
        var entries = _index.GetEdgesFrom(nodeId)
            .Select(edge => new NeighborEntry(edge.Target, edge, true))
            .Concat(_index.GetEdgesTo(nodeId).Select(edge => new NeighborEntry(edge.Source, edge, false)))
            .OrderBy(entry => entry.NodeId)
            .ThenBy(entry => entry.Edge.Id)
            .ThenBy(entry => entry.IsOutgoing ? 0 : 1)
            .ToArray();
        return Page(entries, Signature("neighbors", Project.StateFingerprint + nodeId.Value), request);
    }

    public QueryPage<DependencyEntry> GetDependencies(EntityId nodeId, QueryPageRequest? request = null)
    {
        GetNode(nodeId);
        var entries = _index.ReviewArcs
            .Where(arc => arc.From == nodeId || arc.To == nodeId)
            .Select(arc => new DependencyEntry(arc, arc.From == nodeId))
            .OrderBy(entry => entry.IsOutgoing ? 0 : 1)
            .ThenBy(entry => entry.Arc.From)
            .ThenBy(entry => entry.Arc.To)
            .ThenBy(entry => entry.Arc.EdgeId)
            .ToArray();
        return Page(entries, Signature("dependencies", Project.StateFingerprint + nodeId.Value), request);
    }

    public DependencyPathResult FindDependencyPath(
        EntityId sourceId,
        EntityId targetId,
        QueryTraversalOptions? options = null)
    {
        GetNode(sourceId);
        GetNode(targetId);
        options ??= new QueryTraversalOptions();
        if (sourceId == targetId) return new DependencyPathResult(true, [sourceId], [], []);

        var omissions = new List<QueryOmission>();
        var queue = new Queue<EntityId>();
        var depth = new Dictionary<EntityId, int> { [sourceId] = 0 };
        var previous = new Dictionary<EntityId, (EntityId NodeId, EntityId EdgeId)>();
        queue.Enqueue(sourceId);
        while (queue.Count > 0)
        {
            if (options.CancellationToken.IsCancellationRequested)
            {
                omissions.Add(new QueryOmission(QueryOmissionReason.Cancelled, queue.Count, "Path traversal was cancelled."));
                break;
            }

            var current = queue.Dequeue();
            foreach (var arc in _index.GetReviewArcsFrom(current))
            {
                var nextDepth = depth[current] + 1;
                if (nextDepth > options.MaxDepth)
                {
                    AddOnce(omissions, QueryOmissionReason.TraversalDepthLimit, "Path traversal reached its depth limit.");
                    continue;
                }

                if (depth.ContainsKey(arc.To)) continue;
                if (depth.Count >= options.MaxVisitedNodes)
                {
                    AddOnce(omissions, QueryOmissionReason.VisitedNodeLimit, "Path traversal reached its visited-node limit.");
                    continue;
                }

                depth.Add(arc.To, nextDepth);
                previous.Add(arc.To, (current, arc.EdgeId));
                if (arc.To == targetId) return BuildPath(sourceId, targetId, previous, omissions);
                queue.Enqueue(arc.To);
            }
        }

        return new DependencyPathResult(false, [], [], omissions);
    }

    public ScopeContextResult GetContext(
        IEnumerable<EntityId> nodeIds,
        QueryTraversalOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        options ??= new QueryTraversalOptions();
        var requested = nodeIds.Distinct().OrderBy(id => id).ToArray();
        foreach (var nodeId in requested) GetNode(nodeId);
        var omissions = new List<QueryOmission>();
        var context = new HashSet<EntityId>();
        foreach (var nodeId in requested)
        {
            context.UnionWith(TraverseScopeUpstream(nodeId, options, omissions));
            if (context.Count > options.MaxVisitedNodes)
            {
                AddOnce(omissions, QueryOmissionReason.VisitedNodeLimit, "Context collection reached its node limit.");
                context = context.OrderBy(id => id).Take(options.MaxVisitedNodes).ToHashSet();
                break;
            }
        }

        return new ScopeContextResult(requested, context.Select(GetNode), omissions);
    }

    private IReadOnlyList<EntityId> TraverseScopeUpstream(
        EntityId start,
        QueryTraversalOptions options,
        List<QueryOmission> omissions)
    {
        var result = new List<EntityId>();
        var seen = new HashSet<EntityId>();
        var current = start;
        var depth = 0;
        while (seen.Add(current))
        {
            if (options.CancellationToken.IsCancellationRequested)
            {
                AddOnce(omissions, QueryOmissionReason.Cancelled, "Scope traversal was cancelled.");
                break;
            }

            if (seen.Count > options.MaxVisitedNodes)
            {
                AddOnce(omissions, QueryOmissionReason.VisitedNodeLimit, "Scope traversal reached its node limit.");
                break;
            }

            result.Add(current);
            var parents = _index.GetScopeParentEdges(current);
            if (parents.Count != 1) break;
            if (++depth > options.MaxDepth)
            {
                AddOnce(omissions, QueryOmissionReason.TraversalDepthLimit, "Scope traversal reached its depth limit.");
                break;
            }

            current = parents[0].Target;
        }

        return result;
    }

    private IReadOnlyList<EntityId> TraverseScopeDescendants(
        EntityId start,
        QueryTraversalOptions options,
        List<QueryOmission> omissions)
    {
        var result = new List<EntityId>();
        var seen = new HashSet<EntityId> { start };
        var queue = new Queue<(EntityId Id, int Depth)>(
            _index.GetScopeChildren(start).Select(id => (id, 1)));
        while (queue.Count > 0)
        {
            if (options.CancellationToken.IsCancellationRequested)
            {
                omissions.Add(new QueryOmission(QueryOmissionReason.Cancelled, queue.Count, "Scope traversal was cancelled."));
                break;
            }

            var (current, depth) = queue.Dequeue();
            if (depth > options.MaxDepth)
            {
                AddOnce(omissions, QueryOmissionReason.TraversalDepthLimit, "Scope traversal reached its depth limit.");
                continue;
            }

            if (!seen.Add(current)) continue;
            if (seen.Count > options.MaxVisitedNodes)
            {
                AddOnce(omissions, QueryOmissionReason.VisitedNodeLimit, "Scope traversal reached its node limit.");
                break;
            }

            result.Add(current);
            foreach (var child in _index.GetScopeChildren(current)) queue.Enqueue((child, depth + 1));
        }

        return result;
    }

    private static DependencyPathResult BuildPath(
        EntityId source,
        EntityId target,
        IReadOnlyDictionary<EntityId, (EntityId NodeId, EntityId EdgeId)> previous,
        IEnumerable<QueryOmission> omissions)
    {
        var nodes = new List<EntityId> { target };
        var edges = new List<EntityId>();
        var current = target;
        while (current != source)
        {
            var step = previous[current];
            edges.Add(step.EdgeId);
            current = step.NodeId;
            nodes.Add(current);
        }

        nodes.Reverse();
        edges.Reverse();
        return new DependencyPathResult(true, nodes, edges, omissions);
    }

    private static QueryPage<T> Page<T>(IReadOnlyList<T> values, string signature, QueryPageRequest? request)
    {
        request ??= new QueryPageRequest();
        var offset = DecodeCursor(request.Cursor, signature);
        if (offset > values.Count)
        {
            throw InvalidCursor();
        }

        var items = values.Skip(offset).Take(request.Limit).ToArray();
        var nextOffset = offset + items.Length;
        var hasMore = nextOffset < values.Count;
        return new QueryPage<T>(
            items,
            values.Count,
            hasMore ? EncodeCursor(signature, nextOffset) : null,
            hasMore
                ? new QueryOmission(
                    QueryOmissionReason.OutputLimit,
                    values.Count - nextOffset,
                    "Additional deterministic results are available through the next cursor.")
                : null);
    }

    private static bool Matches(string? value, string text) =>
        value?.Contains(text, StringComparison.OrdinalIgnoreCase) == true;

    private static string Signature(string kind, string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{kind.Length}:{kind}{value.Length}:{value}")))
            .ToLowerInvariant();

    private static string EncodeCursor(string signature, int offset) => Convert.ToBase64String(
        Encoding.UTF8.GetBytes($"{signature}:{offset.ToString(CultureInfo.InvariantCulture)}"));

    private static int DecodeCursor(string? cursor, string signature)
    {
        if (cursor is null) return 0;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var separator = decoded.LastIndexOf(':');
            if (separator < 0 || !StringComparer.Ordinal.Equals(decoded[..separator], signature) ||
                !int.TryParse(decoded[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var offset) ||
                offset < 0)
            {
                throw InvalidCursor();
            }

            return offset;
        }
        catch (FormatException)
        {
            throw InvalidCursor();
        }
    }

    private static ProjectQueryException InvalidCursor() => new(
        ProjectQueryErrorCode.InvalidCursor,
        "The cursor is malformed, out of range, or belongs to a different query.");

    private static void AddOnce(List<QueryOmission> omissions, QueryOmissionReason reason, string message)
    {
        if (omissions.All(omission => omission.Reason != reason))
        {
            omissions.Add(new QueryOmission(reason, null, message));
        }
    }
}
