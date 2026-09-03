using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ValidatedWorld.Core;

namespace ValidatedWorld.Application;

public sealed record ProjectMetadataChange(string Field, string OldValue, string NewValue);

public sealed class ProjectDiffEntry
{
    internal ProjectDiffEntry(
        GraphOperationKind kind,
        GraphEntityKind entityKind,
        EntityId entityId,
        GraphNode? oldNode,
        GraphNode? newNode,
        GraphEdge? oldEdge,
        GraphEdge? newEdge,
        IEnumerable<string> changedFields)
    {
        Kind = kind;
        EntityKind = entityKind;
        EntityId = entityId;
        OldNode = oldNode;
        NewNode = newNode;
        OldEdge = oldEdge;
        NewEdge = newEdge;
        ChangedFields = new ReadOnlyCollection<string>(changedFields.ToArray());
    }

    public GraphOperationKind Kind { get; }

    public GraphEntityKind EntityKind { get; }

    public EntityId EntityId { get; }

    public GraphNode? OldNode { get; }

    public GraphNode? NewNode { get; }

    public GraphEdge? OldEdge { get; }

    public GraphEdge? NewEdge { get; }

    public IReadOnlyList<string> ChangedFields { get; }
}

public sealed record ProjectDiffSummary(
    int MetadataChanges,
    int NodesAdded,
    int NodesReplaced,
    int NodesRemoved,
    int EdgesAdded,
    int EdgesReplaced,
    int EdgesRemoved)
{
    public int EntityChanges =>
        NodesAdded + NodesReplaced + NodesRemoved + EdgesAdded + EdgesReplaced + EdgesRemoved;

    public int TotalChanges => MetadataChanges + EntityChanges;
}

public sealed class ProjectDiffResult
{
    internal ProjectDiffResult(
        string basePath,
        string targetPath,
        ProjectId projectId,
        string baseFingerprint,
        string targetFingerprint,
        IEnumerable<ProjectMetadataChange> metadataChanges,
        ProjectDiffSummary summary,
        QueryPage<ProjectDiffEntry> changes)
    {
        BasePath = basePath;
        TargetPath = targetPath;
        ProjectId = projectId;
        BaseFingerprint = baseFingerprint;
        TargetFingerprint = targetFingerprint;
        MetadataChanges = new ReadOnlyCollection<ProjectMetadataChange>(metadataChanges.ToArray());
        Summary = summary;
        Changes = changes;
    }

    public string BasePath { get; }

    public string TargetPath { get; }

    public ProjectId ProjectId { get; }

    public string BaseFingerprint { get; }

    public string TargetFingerprint { get; }

    public IReadOnlyList<ProjectMetadataChange> MetadataChanges { get; }

    public ProjectDiffSummary Summary { get; }

    public QueryPage<ProjectDiffEntry> Changes { get; }
}

public sealed partial class ProjectApplication
{
    /// <summary>Compares two verified immutable snapshots without writing either project.</summary>
    public ProjectDiffResult Diff(
        string basePath,
        string targetPath,
        QueryPageRequest? request = null)
    {
        var baseProject = _store.Load(basePath);
        var targetProject = _store.Load(targetPath);
        if (baseProject.Graph.ProjectId != targetProject.Graph.ProjectId)
        {
            throw new ProjectQueryException(
                ProjectQueryErrorCode.ProjectMismatch,
                $"Base project '{baseProject.Graph.ProjectId.Value}' does not match target project " +
                $"'{targetProject.Graph.ProjectId.Value}'.");
        }

        request ??= new QueryPageRequest();
        var metadataChanges = MetadataChanges(baseProject.Graph, targetProject.Graph);
        var entries = EntityChanges(baseProject.Graph, targetProject.Graph);
        var summary = Summary(metadataChanges.Count, entries);
        var signature = Signature(
            baseProject.Graph.ProjectId,
            baseProject.StateFingerprint,
            targetProject.StateFingerprint,
            request.Limit);
        var offset = DecodeCursor(request.Cursor, signature);
        if (offset > entries.Count)
        {
            throw InvalidCursor();
        }

        var items = entries.Skip(offset).Take(request.Limit).ToArray();
        var nextOffset = offset + items.Length;
        var hasMore = nextOffset < entries.Count;
        var page = new QueryPage<ProjectDiffEntry>(
            items,
            entries.Count,
            hasMore ? EncodeCursor(signature, nextOffset) : null,
            hasMore
                ? new QueryOmission(
                    QueryOmissionReason.OutputLimit,
                    entries.Count - nextOffset,
                    "Additional deterministic diff entries are available through the next cursor.")
                : null);

        return new ProjectDiffResult(
            baseProject.Path,
            targetProject.Path,
            baseProject.Graph.ProjectId,
            baseProject.StateFingerprint,
            targetProject.StateFingerprint,
            metadataChanges,
            summary,
            page);
    }

    private static IReadOnlyList<ProjectMetadataChange> MetadataChanges(
        ProjectGraph baseGraph,
        ProjectGraph targetGraph)
    {
        var changes = new List<ProjectMetadataChange>(2);
        if (!StringComparer.Ordinal.Equals(baseGraph.Title, targetGraph.Title))
        {
            changes.Add(new ProjectMetadataChange("title", baseGraph.Title, targetGraph.Title));
        }

        if (baseGraph.PurposeNodeId != targetGraph.PurposeNodeId)
        {
            changes.Add(new ProjectMetadataChange(
                "purposeNodeId",
                baseGraph.PurposeNodeId.Value,
                targetGraph.PurposeNodeId.Value));
        }

        return changes;
    }

    private static IReadOnlyList<ProjectDiffEntry> EntityChanges(
        ProjectGraph baseGraph,
        ProjectGraph targetGraph)
    {
        var entries = new List<ProjectDiffEntry>();
        var baseNodes = baseGraph.Nodes.ToDictionary(node => node.Id);
        var targetNodes = targetGraph.Nodes.ToDictionary(node => node.Id);
        foreach (var id in baseNodes.Keys.Union(targetNodes.Keys).OrderBy(id => id))
        {
            baseNodes.TryGetValue(id, out var oldNode);
            targetNodes.TryGetValue(id, out var newNode);
            if (oldNode is null)
            {
                entries.Add(new ProjectDiffEntry(
                    GraphOperationKind.Add, GraphEntityKind.Node, id,
                    null, newNode, null, null, []));
            }
            else if (newNode is null)
            {
                entries.Add(new ProjectDiffEntry(
                    GraphOperationKind.Remove, GraphEntityKind.Node, id,
                    oldNode, null, null, null, []));
            }
            else if (!oldNode.Equals(newNode))
            {
                entries.Add(new ProjectDiffEntry(
                    GraphOperationKind.Replace, GraphEntityKind.Node, id,
                    oldNode, newNode, null, null, ChangedNodeFields(oldNode, newNode)));
            }
        }

        var baseEdges = baseGraph.Edges.ToDictionary(edge => edge.Id);
        var targetEdges = targetGraph.Edges.ToDictionary(edge => edge.Id);
        foreach (var id in baseEdges.Keys.Union(targetEdges.Keys).OrderBy(id => id))
        {
            baseEdges.TryGetValue(id, out var oldEdge);
            targetEdges.TryGetValue(id, out var newEdge);
            if (oldEdge is null)
            {
                entries.Add(new ProjectDiffEntry(
                    GraphOperationKind.Add, GraphEntityKind.Edge, id,
                    null, null, null, newEdge, []));
            }
            else if (newEdge is null)
            {
                entries.Add(new ProjectDiffEntry(
                    GraphOperationKind.Remove, GraphEntityKind.Edge, id,
                    null, null, oldEdge, null, []));
            }
            else if (!oldEdge.Equals(newEdge))
            {
                entries.Add(new ProjectDiffEntry(
                    GraphOperationKind.Replace, GraphEntityKind.Edge, id,
                    null, null, oldEdge, newEdge, ChangedEdgeFields(oldEdge, newEdge)));
            }
        }

        return entries;
    }

    private static IReadOnlyList<string> ChangedNodeFields(GraphNode oldNode, GraphNode newNode)
    {
        var fields = new List<string>(4);
        if (!StringComparer.Ordinal.Equals(oldNode.Text, newNode.Text)) fields.Add("text");
        if (!StringComparer.Ordinal.Equals(oldNode.Kind, newNode.Kind)) fields.Add("kind");
        if (!oldNode.Tags.SequenceEqual(newNode.Tags, StringComparer.Ordinal)) fields.Add("tags");
        if (!oldNode.Attributes.SequenceEqual(newNode.Attributes)) fields.Add("attributes");
        return fields;
    }

    private static IReadOnlyList<string> ChangedEdgeFields(GraphEdge oldEdge, GraphEdge newEdge)
    {
        var fields = new List<string>(7);
        if (oldEdge.Source != newEdge.Source) fields.Add("source");
        if (oldEdge.Target != newEdge.Target) fields.Add("target");
        if (!StringComparer.Ordinal.Equals(oldEdge.Relationship, newEdge.Relationship))
            fields.Add("relationship");
        if (oldEdge.ReviewDirection != newEdge.ReviewDirection) fields.Add("reviewDirection");
        if (!StringComparer.Ordinal.Equals(oldEdge.Rationale, newEdge.Rationale)) fields.Add("rationale");
        if (!oldEdge.Tags.SequenceEqual(newEdge.Tags, StringComparer.Ordinal)) fields.Add("tags");
        if (!oldEdge.Attributes.SequenceEqual(newEdge.Attributes)) fields.Add("attributes");
        return fields;
    }

    private static ProjectDiffSummary Summary(int metadataChanges, IReadOnlyList<ProjectDiffEntry> entries) => new(
        metadataChanges,
        entries.Count(entry => entry.EntityKind == GraphEntityKind.Node && entry.Kind == GraphOperationKind.Add),
        entries.Count(entry => entry.EntityKind == GraphEntityKind.Node && entry.Kind == GraphOperationKind.Replace),
        entries.Count(entry => entry.EntityKind == GraphEntityKind.Node && entry.Kind == GraphOperationKind.Remove),
        entries.Count(entry => entry.EntityKind == GraphEntityKind.Edge && entry.Kind == GraphOperationKind.Add),
        entries.Count(entry => entry.EntityKind == GraphEntityKind.Edge && entry.Kind == GraphOperationKind.Replace),
        entries.Count(entry => entry.EntityKind == GraphEntityKind.Edge && entry.Kind == GraphOperationKind.Remove));

    private static string Signature(
        ProjectId projectId,
        string baseFingerprint,
        string targetFingerprint,
        int limit)
    {
        var value = $"project-diff-v1:{projectId.Value.Length}:{projectId.Value}:" +
            $"{baseFingerprint}:{targetFingerprint}:{limit.ToString(CultureInfo.InvariantCulture)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

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
                !int.TryParse(decoded[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture,
                    out var offset) ||
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
        "The diff cursor is malformed, out of range, stale, or belongs to different inputs or options.");
}
