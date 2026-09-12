using System.Collections.ObjectModel;
using ValidatedWorld.Core;
using ValidatedWorld.Serialization;
using ValidatedWorld.Validation;

namespace ValidatedWorld.Application;

public enum ProjectMergeStatus
{
    Clean,
    Conflicted,
    Invalid,
}

public enum ProjectMergeConflictKind
{
    ProjectMetadataChanged,
    EntityKindChanged,
    AddedDifferently,
    ModifiedDifferently,
    DeletedAndModified,
}

public sealed record ProjectMergeConflict(
    ProjectMergeConflictKind Kind,
    string EntityId,
    GraphEntityKind? EntityKind,
    IReadOnlyList<string> ChangedFields,
    string Message);

/// <summary>
/// A deterministic, read-only three-way merge plan. Operations are relative to
/// the ours snapshot and can be submitted to a normal change session for
/// affected analysis, review, and atomic write.
/// </summary>
public sealed class ProjectMergeResult
{
    internal ProjectMergeResult(
        string basePath,
        string oursPath,
        string theirsPath,
        ProjectId projectId,
        string baseFingerprint,
        string oursFingerprint,
        string theirsFingerprint,
        ProjectMergeStatus status,
        ProjectGraph? mergedGraph,
        GraphOperationBatch operations,
        GraphValidationResult? validation,
        IEnumerable<ProjectMergeConflict> conflicts)
    {
        BasePath = basePath;
        OursPath = oursPath;
        TheirsPath = theirsPath;
        ProjectId = projectId;
        BaseFingerprint = baseFingerprint;
        OursFingerprint = oursFingerprint;
        TheirsFingerprint = theirsFingerprint;
        Status = status;
        MergedGraph = mergedGraph;
        Operations = operations;
        Validation = validation;
        Conflicts = new ReadOnlyCollection<ProjectMergeConflict>(conflicts.ToArray());
    }

    public string BasePath { get; }

    public string OursPath { get; }

    public string TheirsPath { get; }

    public ProjectId ProjectId { get; }

    public string BaseFingerprint { get; }

    public string OursFingerprint { get; }

    public string TheirsFingerprint { get; }

    public ProjectMergeStatus Status { get; }

    public bool IsReadyToApply => Status == ProjectMergeStatus.Clean;

    public ProjectGraph? MergedGraph { get; }

    public string? MergedFingerprint => MergedGraph is null
        ? null
        : GraphFingerprints.State(MergedGraph);

    public GraphOperationBatch Operations { get; }

    public GraphValidationResult? Validation { get; }

    public IReadOnlyList<ProjectMergeConflict> Conflicts { get; }
}

public sealed partial class ProjectApplication
{
    /// <summary>
    /// Computes a graph-aware three-way merge without modifying any input or
    /// creating an in-memory change session. The returned operations are based
    /// on the ours snapshot.
    /// </summary>
    public ProjectMergeResult Merge(
        string basePath,
        string oursPath,
        string theirsPath)
    {
        var baseProject = _store.Load(basePath);
        var oursProject = _store.Load(oursPath);
        var theirsProject = _store.Load(theirsPath);
        EnsureSameProject(baseProject, oursProject, "base", "ours");
        EnsureSameProject(baseProject, theirsProject, "base", "theirs");

        var conflicts = new List<ProjectMergeConflict>();
        AddMetadataConflict(conflicts, "title", baseProject.Graph.Title,
            oursProject.Graph.Title, theirsProject.Graph.Title);
        AddMetadataConflict(conflicts, "purposeNodeId", baseProject.Graph.PurposeNodeId.Value,
            oursProject.Graph.PurposeNodeId.Value, theirsProject.Graph.PurposeNodeId.Value);

        var mergedNodes = new List<GraphNode>();
        var mergedEdges = new List<GraphEdge>();
        var baseNodes = baseProject.Graph.Nodes.ToDictionary(node => node.Id);
        var oursNodes = oursProject.Graph.Nodes.ToDictionary(node => node.Id);
        var theirsNodes = theirsProject.Graph.Nodes.ToDictionary(node => node.Id);
        var baseEdges = baseProject.Graph.Edges.ToDictionary(edge => edge.Id);
        var oursEdges = oursProject.Graph.Edges.ToDictionary(edge => edge.Id);
        var theirsEdges = theirsProject.Graph.Edges.ToDictionary(edge => edge.Id);
        var crossKindIds = baseNodes.Keys.Union(oursNodes.Keys).Union(theirsNodes.Keys)
            .Intersect(baseEdges.Keys.Union(oursEdges.Keys).Union(theirsEdges.Keys))
            .OrderBy(id => id)
            .ToHashSet();
        foreach (var id in crossKindIds)
        {
            conflicts.Add(new ProjectMergeConflict(
                ProjectMergeConflictKind.EntityKindChanged,
                id.Value,
                null,
                ["entityKind"],
                $"Entity '{id.Value}' is a node in one snapshot and an edge in another."));
        }

        MergeEntities(
            GraphEntityKind.Node,
            baseNodes,
            oursNodes,
            theirsNodes,
            mergedNodes,
            mergedEdges,
            conflicts,
            crossKindIds);

        MergeEntities(
            GraphEntityKind.Edge,
            baseEdges,
            oursEdges,
            theirsEdges,
            mergedNodes,
            mergedEdges,
            conflicts,
            crossKindIds);

        if (conflicts.Count > 0)
        {
            return new ProjectMergeResult(
                baseProject.Path,
                oursProject.Path,
                theirsProject.Path,
                baseProject.Graph.ProjectId,
                baseProject.StateFingerprint,
                oursProject.StateFingerprint,
                theirsProject.StateFingerprint,
                ProjectMergeStatus.Conflicted,
                null,
                GraphOperationBatch.Empty,
                null,
                conflicts);
        }

        var mergedGraph = new ProjectGraph(
            baseProject.Graph.ProjectId,
            baseProject.Graph.Title,
            baseProject.Graph.PurposeNodeId,
            mergedNodes,
            mergedEdges);
        var validation = ValidateGraph(mergedGraph);
        if (!validation.IsValid)
        {
            return new ProjectMergeResult(
                baseProject.Path,
                oursProject.Path,
                theirsProject.Path,
                baseProject.Graph.ProjectId,
                baseProject.StateFingerprint,
                oursProject.StateFingerprint,
                theirsProject.StateFingerprint,
                ProjectMergeStatus.Invalid,
                mergedGraph,
                OperationsBetween(oursProject.Graph, mergedGraph),
                validation,
                []);
        }

        return new ProjectMergeResult(
            baseProject.Path,
            oursProject.Path,
            theirsProject.Path,
            baseProject.Graph.ProjectId,
            baseProject.StateFingerprint,
            oursProject.StateFingerprint,
            theirsProject.StateFingerprint,
            ProjectMergeStatus.Clean,
            mergedGraph,
            OperationsBetween(oursProject.Graph, mergedGraph),
            validation,
            []);
    }

    private static void EnsureSameProject(
        StoredProject baseProject,
        StoredProject otherProject,
        string baseName,
        string otherName)
    {
        if (baseProject.Graph.ProjectId != otherProject.Graph.ProjectId)
        {
            throw new ProjectQueryException(
                ProjectQueryErrorCode.ProjectMismatch,
                $"The {baseName} project '{baseProject.Graph.ProjectId.Value}' does not match the " +
                $"{otherName} project '{otherProject.Graph.ProjectId.Value}'.");
        }
    }

    private static void AddMetadataConflict(
        ICollection<ProjectMergeConflict> conflicts,
        string field,
        string baseValue,
        string oursValue,
        string theirsValue)
    {
        if (StringComparer.Ordinal.Equals(baseValue, oursValue) &&
            StringComparer.Ordinal.Equals(baseValue, theirsValue))
        {
            return;
        }

        conflicts.Add(new ProjectMergeConflict(
            ProjectMergeConflictKind.ProjectMetadataChanged,
            $"$project.{field}",
            null,
            [field],
            $"Project metadata '{field}' changed and cannot be applied by the current graph-entity change contract. " +
            $"Base='{baseValue}', ours='{oursValue}', theirs='{theirsValue}'."));
    }

    private static void MergeEntities<T>(
        GraphEntityKind entityKind,
        IReadOnlyDictionary<EntityId, T> baseEntities,
        IReadOnlyDictionary<EntityId, T> oursEntities,
        IReadOnlyDictionary<EntityId, T> theirsEntities,
        ICollection<GraphNode> mergedNodes,
        ICollection<GraphEdge> mergedEdges,
        ICollection<ProjectMergeConflict> conflicts,
        IReadOnlySet<EntityId> excludedIds)
        where T : class
    {
        foreach (var id in baseEntities.Keys.Union(oursEntities.Keys).Union(theirsEntities.Keys).OrderBy(id => id))
        {
            if (excludedIds.Contains(id)) continue;
            baseEntities.TryGetValue(id, out var baseValue);
            oursEntities.TryGetValue(id, out var oursValue);
            theirsEntities.TryGetValue(id, out var theirsValue);
            var selected = SelectValue(baseValue, oursValue, theirsValue);
            if (selected.HasConflict)
            {
                conflicts.Add(new ProjectMergeConflict(
                    ClassifyConflict(baseValue, oursValue, theirsValue),
                    id.Value,
                    entityKind,
                    ChangedFields(entityKind, baseValue, oursValue, theirsValue),
                    ConflictMessage(entityKind, id, baseValue, oursValue, theirsValue)));
                continue;
            }

            if (selected.Value is GraphNode node) mergedNodes.Add(node);
            if (selected.Value is GraphEdge edge) mergedEdges.Add(edge);
        }
    }

    private static Selection<T> SelectValue<T>(T? baseValue, T? oursValue, T? theirsValue)
        where T : class
    {
        if (Equals(oursValue, theirsValue)) return new Selection<T>(oursValue, false);
        if (Equals(oursValue, baseValue)) return new Selection<T>(theirsValue, false);
        if (Equals(theirsValue, baseValue)) return new Selection<T>(oursValue, false);
        return new Selection<T>(null, true);
    }

    private static ProjectMergeConflictKind ClassifyConflict<T>(T? baseValue, T? oursValue, T? theirsValue)
        where T : class
    {
        if (baseValue is null) return ProjectMergeConflictKind.AddedDifferently;
        if (oursValue is null || theirsValue is null) return ProjectMergeConflictKind.DeletedAndModified;
        return ProjectMergeConflictKind.ModifiedDifferently;
    }

    private static string ConflictMessage<T>(
        GraphEntityKind entityKind,
        EntityId id,
        T? baseValue,
        T? oursValue,
        T? theirsValue)
        where T : class
    {
        var label = entityKind == GraphEntityKind.Node ? "node" : "edge";
        if (baseValue is null)
        {
            return $"The {label} '{id.Value}' was added differently in ours and theirs.";
        }

        if (oursValue is null || theirsValue is null)
        {
            return $"The {label} '{id.Value}' was deleted in one branch and modified in the other.";
        }

        return $"The {label} '{id.Value}' was modified differently in ours and theirs.";
    }

    private static IReadOnlyList<string> ChangedFields<T>(
        GraphEntityKind entityKind,
        T? baseValue,
        T? oursValue,
        T? theirsValue)
        where T : class
    {
        if (oursValue is null || theirsValue is null) return [];
        if (entityKind == GraphEntityKind.Node)
        {
            var ours = (GraphNode)(object)oursValue;
            var theirs = (GraphNode)(object)theirsValue;
            return NodeFields(ours, theirs);
        }

        var oursEdge = (GraphEdge)(object)oursValue;
        var theirsEdge = (GraphEdge)(object)theirsValue;
        return EdgeFields(oursEdge, theirsEdge);
    }

    private static IReadOnlyList<string> NodeFields(GraphNode ours, GraphNode theirs)
    {
        var fields = new List<string>(4);
        if (!StringComparer.Ordinal.Equals(ours.Text, theirs.Text)) fields.Add("text");
        if (!StringComparer.Ordinal.Equals(ours.Kind, theirs.Kind)) fields.Add("kind");
        if (!ours.Tags.SequenceEqual(theirs.Tags, StringComparer.Ordinal)) fields.Add("tags");
        if (!ours.Attributes.SequenceEqual(theirs.Attributes)) fields.Add("attributes");
        return fields;
    }

    private static IReadOnlyList<string> EdgeFields(GraphEdge ours, GraphEdge theirs)
    {
        var fields = new List<string>(7);
        if (ours.Source != theirs.Source) fields.Add("source");
        if (ours.Target != theirs.Target) fields.Add("target");
        if (!StringComparer.Ordinal.Equals(ours.Relationship, theirs.Relationship)) fields.Add("relationship");
        if (ours.ReviewDirection != theirs.ReviewDirection) fields.Add("reviewDirection");
        if (!StringComparer.Ordinal.Equals(ours.Rationale, theirs.Rationale)) fields.Add("rationale");
        if (!ours.Tags.SequenceEqual(theirs.Tags, StringComparer.Ordinal)) fields.Add("tags");
        if (!ours.Attributes.SequenceEqual(theirs.Attributes)) fields.Add("attributes");
        return fields;
    }

    private static GraphOperationBatch OperationsBetween(ProjectGraph ours, ProjectGraph merged)
    {
        var operations = new List<GraphOperation>();
        AddOperations(ours.Nodes.ToDictionary(node => node.Id), merged.Nodes.ToDictionary(node => node.Id), operations);
        AddOperations(ours.Edges.ToDictionary(edge => edge.Id), merged.Edges.ToDictionary(edge => edge.Id), operations);
        return new GraphOperationBatch(operations);
    }

    private static void AddOperations<T>(
        IReadOnlyDictionary<EntityId, T> ours,
        IReadOnlyDictionary<EntityId, T> merged,
        ICollection<GraphOperation> operations)
        where T : class
    {
        foreach (var id in ours.Keys.Union(merged.Keys).OrderBy(id => id))
        {
            ours.TryGetValue(id, out var oursValue);
            merged.TryGetValue(id, out var mergedValue);
            if (oursValue is null && mergedValue is not null)
            {
                operations.Add(mergedValue switch
                {
                    GraphNode node => GraphOperation.AddNode(node),
                    GraphEdge edge => GraphOperation.AddEdge(edge),
                    _ => throw new InvalidOperationException("Unsupported graph entity.")
                });
            }
            else if (oursValue is not null && mergedValue is null)
            {
                operations.Add(typeof(T) == typeof(GraphNode)
                    ? GraphOperation.RemoveNode(id)
                    : GraphOperation.RemoveEdge(id));
            }
            else if (!Equals(oursValue, mergedValue))
            {
                operations.Add(mergedValue switch
                {
                    GraphNode node => GraphOperation.ReplaceNode(node),
                    GraphEdge edge => GraphOperation.ReplaceEdge(edge),
                    _ => throw new InvalidOperationException("Unsupported graph entity.")
                });
            }
        }
    }

    private readonly record struct Selection<T>(T? Value, bool HasConflict) where T : class;
}
