using System.Diagnostics;
using ValidatedWorld.Core;

namespace ValidatedWorld.Validation;

public sealed class GraphOperationException : InvalidOperationException
{
    public GraphOperationException(string code, string message)
        : base(message) => Code = code;

    public string Code { get; }
}

/// <summary>The immutable result of applying a batch to a base graph.</summary>
public sealed class GraphProjectionResult
{
    internal GraphProjectionResult(
        ProjectGraph graph,
        GraphOperationBatch operations,
        GraphValidationResult validation)
    {
        Graph = graph;
        Operations = operations;
        Validation = validation;
    }

    public ProjectGraph Graph { get; }

    public GraphOperationBatch Operations { get; }

    public GraphValidationResult Validation { get; }

    public bool IsValid => Validation.IsValid;

    public bool IsInvalid => Validation.IsInvalid;

    public bool IsInconclusive => Validation.IsInconclusive;

    public IReadOnlyList<ValidationDiagnostic> Diagnostics => Validation.Diagnostics;
}

/// <summary>Projects a final operation batch without changing its base graph.</summary>
public sealed class GraphProjector
{
    public GraphProjectionResult Project(ProjectGraph baseGraph, IEnumerable<GraphOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        return Project(baseGraph, new GraphOperationBatch(operations));
    }

    public GraphProjectionResult Project(ProjectGraph baseGraph, GraphOperationBatch operations)
        => Project(baseGraph, operations, graph => new GraphValidator().Validate(graph));

    public GraphProjectionResult Project(
        ProjectGraph baseGraph,
        GraphOperationBatch operations,
        Func<ProjectGraph, GraphValidationResult> validate)
    {
        ArgumentNullException.ThrowIfNull(baseGraph);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(validate);

        var nodes = BuildUniqueMap(baseGraph.Nodes, node => node.Id, "base-duplicate-node-id");
        var edges = BuildUniqueMap(baseGraph.Edges, edge => edge.Id, "base-duplicate-edge-id");
        foreach (var id in nodes.Keys.Intersect(edges.Keys).OrderBy(id => id))
        {
            throw new GraphOperationException(
                "base-entity-id-collision",
                $"Base graph uses entity ID '{id.Value}' for both a node and an edge.");
        }

        foreach (var operation in operations.Operations)
        {
            Apply(operation, nodes, edges);
        }

        var graph = new ProjectGraph(
            baseGraph.ProjectId,
            baseGraph.Title,
            baseGraph.PurposeNodeId,
            nodes.Values,
            edges.Values);
        var validation = validate(graph);
        return new GraphProjectionResult(graph, operations, validation);
    }

    private static void Apply(
        GraphOperation operation,
        IDictionary<EntityId, GraphNode> nodes,
        IDictionary<EntityId, GraphEdge> edges)
    {
        if (operation.EntityKind == GraphEntityKind.Node)
        {
            if (edges.ContainsKey(operation.EntityId))
            {
                throw WrongEntityKind(operation, GraphEntityKind.Edge);
            }

            ApplyNode(operation, nodes);
            return;
        }

        if (nodes.ContainsKey(operation.EntityId))
        {
            throw WrongEntityKind(operation, GraphEntityKind.Node);
        }

        ApplyEdge(operation, edges);
    }

    private static void ApplyNode(GraphOperation operation, IDictionary<EntityId, GraphNode> nodes)
    {
        switch (operation.Kind)
        {
            case GraphOperationKind.Add:
                if (nodes.ContainsKey(operation.EntityId))
                {
                    throw new GraphOperationException(
                        "add-precondition",
                        $"Cannot add existing node '{operation.EntityId.Value}'.");
                }

                nodes.Add(operation.EntityId, operation.Node!);
                break;
            case GraphOperationKind.Replace:
                if (!nodes.ContainsKey(operation.EntityId))
                {
                    throw new GraphOperationException(
                        "replace-precondition",
                        $"Cannot replace missing node '{operation.EntityId.Value}'.");
                }

                nodes[operation.EntityId] = operation.Node!;
                break;
            case GraphOperationKind.Remove:
                if (!nodes.Remove(operation.EntityId))
                {
                    throw new GraphOperationException(
                        "remove-precondition",
                        $"Cannot remove missing node '{operation.EntityId.Value}'.");
                }

                break;
            default:
                throw new UnreachableException();
        }
    }

    private static void ApplyEdge(GraphOperation operation, IDictionary<EntityId, GraphEdge> edges)
    {
        switch (operation.Kind)
        {
            case GraphOperationKind.Add:
                if (edges.ContainsKey(operation.EntityId))
                {
                    throw new GraphOperationException(
                        "add-precondition",
                        $"Cannot add existing edge '{operation.EntityId.Value}'.");
                }

                edges.Add(operation.EntityId, operation.Edge!);
                break;
            case GraphOperationKind.Replace:
                if (!edges.ContainsKey(operation.EntityId))
                {
                    throw new GraphOperationException(
                        "replace-precondition",
                        $"Cannot replace missing edge '{operation.EntityId.Value}'.");
                }

                edges[operation.EntityId] = operation.Edge!;
                break;
            case GraphOperationKind.Remove:
                if (!edges.Remove(operation.EntityId))
                {
                    throw new GraphOperationException(
                        "remove-precondition",
                        $"Cannot remove missing edge '{operation.EntityId.Value}'.");
                }

                break;
            default:
                throw new UnreachableException();
        }
    }

    private static GraphOperationException WrongEntityKind(GraphOperation operation, GraphEntityKind actual)
    {
        return new GraphOperationException(
            "wrong-entity-kind",
            $"Operation for '{operation.EntityId.Value}' declares {operation.EntityKind}, but the base graph contains {actual}.");
    }

    private static Dictionary<EntityId, T> BuildUniqueMap<T>(
        IEnumerable<T> values,
        Func<T, EntityId> idSelector,
        string duplicateCode)
    {
        var map = new Dictionary<EntityId, T>();
        foreach (var value in values)
        {
            var id = idSelector(value);
            if (!map.TryAdd(id, value))
            {
                throw new GraphOperationException(
                    duplicateCode,
                    $"Base graph contains duplicate entity ID '{id.Value}'.");
            }
        }

        return map;
    }
}

/// <summary>An explicit parent and edge ID for a newly added scope node.</summary>
public sealed record ScopeParentSelection(EntityId ChildId, EntityId ParentId, EntityId EdgeId);

/// <summary>
/// Expands a batch with only explicitly supplied, unambiguous scope-parent
/// edges. It never creates semantic cross-links or guesses an edge ID.
/// </summary>
public static class GraphOperationFocus
{
    public static GraphOperationBatch ExpandScopeParents(
        ProjectGraph baseGraph,
        GraphOperationBatch operations,
        IEnumerable<ScopeParentSelection> selections)
    {
        ArgumentNullException.ThrowIfNull(baseGraph);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(selections);

        var selected = selections.ToArray();
        var duplicateChild = selected
            .GroupBy(selection => selection.ChildId)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateChild is not null)
        {
            throw new ArgumentException(
                $"New node '{duplicateChild.Key.Value}' has ambiguous scope-parent selections.",
                nameof(selections));
        }

        var operationById = operations.Operations.ToDictionary(operation => operation.EntityId);
        var addedNodes = operations.Operations
            .Where(operation => operation.Kind == GraphOperationKind.Add && operation.EntityKind == GraphEntityKind.Node)
            .Select(operation => operation.EntityId)
            .ToHashSet();
        var additions = new List<GraphOperation>();

        foreach (var selection in selected.OrderBy(selection => selection.ChildId))
        {
            if (!addedNodes.Contains(selection.ChildId))
            {
                throw new ArgumentException(
                    $"Scope-parent selection '{selection.ChildId.Value}' must target an added node.",
                    nameof(selections));
            }

            if (operationById.TryGetValue(selection.EdgeId, out var existingOperation))
            {
                throw new ArgumentException(
                    $"Scope-parent edge ID '{selection.EdgeId.Value}' is already used by an operation.",
                    nameof(selections));
            }

            if (baseGraph.Nodes.Any(node => node.Id == selection.ParentId) == false &&
                !addedNodes.Contains(selection.ParentId))
            {
                throw new ArgumentException(
                    $"Scope-parent '{selection.ParentId.Value}' is not an existing or added node.",
                    nameof(selections));
            }

            additions.Add(GraphOperation.AddEdge(new GraphEdge(
                selection.EdgeId,
                selection.ChildId,
                selection.ParentId,
                "scope-parent",
                ReviewDirection.None)));
        }

        return new GraphOperationBatch(operations.Operations.Concat(additions));
    }

    public static GraphOperationBatch ExpandScopeParents(
        ProjectGraph baseGraph,
        IEnumerable<GraphOperation> operations,
        IEnumerable<ScopeParentSelection> selections) =>
        ExpandScopeParents(baseGraph, new GraphOperationBatch(operations), selections);
}
