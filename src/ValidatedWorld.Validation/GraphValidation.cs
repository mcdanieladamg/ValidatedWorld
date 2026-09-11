using System.Collections.ObjectModel;
using ValidatedWorld.Core;

namespace ValidatedWorld.Validation;

public enum ValidationStatus
{
    Valid,
    Invalid,
    Inconclusive,
}

/// <summary>Limits and cancellation used by deterministic graph validation.</summary>
public sealed class GraphValidationOptions
{
    private int _maxTraversalDepth = 100_000;
    private int _maxTraversalNodes = 1_000_000;
    private int _maxDiagnostics = 10_000;

    public int MaxTraversalDepth
    {
        get => _maxTraversalDepth;
        init => _maxTraversalDepth = ValidatePositive(value, nameof(MaxTraversalDepth));
    }

    public int MaxTraversalNodes
    {
        get => _maxTraversalNodes;
        init => _maxTraversalNodes = ValidatePositive(value, nameof(MaxTraversalNodes));
    }

    public int MaxDiagnostics
    {
        get => _maxDiagnostics;
        init => _maxDiagnostics = ValidatePositive(value, nameof(MaxDiagnostics));
    }

    public CancellationToken CancellationToken { get; init; }

    private static int ValidatePositive(int value, string parameterName) => value > 0
        ? value
        : throw new ArgumentOutOfRangeException(parameterName, "The limit must be positive.");
}

public sealed record ValidationDiagnostic(
    string Code,
    string Message,
    EntityId? EntityId = null,
    EntityId? RelatedEntityId = null,
    IReadOnlyList<EntityId>? Path = null,
    EntityId? RuleId = null,
    IReadOnlyList<EntityId>? OffendingEntityIds = null,
    int? TotalOffendingCount = null,
    int? OmittedOffendingCount = null);

public sealed class GraphValidationResult
{
    internal GraphValidationResult(
        ValidationStatus status,
        GraphIndex index,
        IEnumerable<ValidationDiagnostic> diagnostics)
    {
        Status = status;
        Index = index;
        Diagnostics = new ReadOnlyCollection<ValidationDiagnostic>(diagnostics.ToArray());
    }

    public ValidationStatus Status { get; }

    public GraphIndex Index { get; }

    public IReadOnlyList<ValidationDiagnostic> Diagnostics { get; }

    public bool IsValid => Status == ValidationStatus.Valid;

    public bool IsInvalid => Status == ValidationStatus.Invalid;

    public bool IsInconclusive => Status == ValidationStatus.Inconclusive;
}

/// <summary>Runs the common deterministic structural checks for a project graph.</summary>
public sealed class GraphValidator
{
    public static GraphValidationResult CombineRules(
        GraphValidationResult structural,
        RuleValidationResult rules)
    {
        ArgumentNullException.ThrowIfNull(structural);
        ArgumentNullException.ThrowIfNull(rules);
        var diagnostics = structural.Diagnostics.Concat(rules.Diagnostics.Select(diagnostic => new ValidationDiagnostic(
            diagnostic.Code,
            diagnostic.Message,
            diagnostic.RuleId,
            null,
            null,
            diagnostic.RuleId,
            diagnostic.OffendingEntityIds,
            diagnostic.TotalOffendingCount,
            diagnostic.OmittedOffendingCount)));
        var status = structural.Status == ValidationStatus.Inconclusive || rules.Status == ValidationStatus.Inconclusive
            ? ValidationStatus.Inconclusive
            : structural.Status == ValidationStatus.Invalid || rules.Status == ValidationStatus.Invalid
                ? ValidationStatus.Invalid
                : ValidationStatus.Valid;
        return new GraphValidationResult(status, structural.Index, diagnostics);
    }

    public GraphValidationResult Validate(
        ProjectGraph graph,
        GraphValidationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        options ??= new GraphValidationOptions();

        var index = new GraphIndex(graph);
        var collector = new DiagnosticCollector(options.MaxDiagnostics);
        var inconclusive = false;

        ValidateIdentityAndEndpoints(index, collector);
        ValidatePurposeAndScopeEdges(index, collector);
        ValidateScopeLineages(index, options, collector, ref inconclusive);

        if (collector.WasTruncated)
        {
            inconclusive = true;
            collector.Add(
                "diagnostic-limit-exceeded",
                $"Validation stopped after {options.MaxDiagnostics} diagnostics; additional diagnostics are omitted.");
        }

        var status = inconclusive
            ? ValidationStatus.Inconclusive
            : collector.Count == 0 ? ValidationStatus.Valid : ValidationStatus.Invalid;
        return new GraphValidationResult(status, index, collector.Ordered());
    }

    private static void ValidateIdentityAndEndpoints(GraphIndex index, DiagnosticCollector collector)
    {
        foreach (var pair in index.NodesByIdIncludingDuplicates)
        {
            if (pair.Value.Count > 1)
            {
                collector.Add(
                    "duplicate-node-id",
                    $"Node ID '{pair.Key.Value}' occurs {pair.Value.Count} times.",
                    pair.Key);
            }
        }

        foreach (var pair in index.EdgesByIdIncludingDuplicates)
        {
            if (pair.Value.Count > 1)
            {
                collector.Add(
                    "duplicate-edge-id",
                    $"Edge ID '{pair.Key.Value}' occurs {pair.Value.Count} times.",
                    pair.Key);
            }
        }

        foreach (var nodeId in index.NodesByIdIncludingDuplicates.Keys.Intersect(
                     index.EdgesByIdIncludingDuplicates.Keys).OrderBy(id => id))
        {
            collector.Add(
                "entity-id-collision",
                $"Entity ID '{nodeId.Value}' is used by both a node and an edge.",
                nodeId);
        }

        foreach (var edge in index.Graph.Edges)
        {
            if (!index.NodesByIdIncludingDuplicates.ContainsKey(edge.Source))
            {
                collector.Add(
                    "missing-edge-source",
                    $"Edge '{edge.Id.Value}' references missing source node '{edge.Source.Value}'.",
                    edge.Id,
                    edge.Source);
            }

            if (!index.NodesByIdIncludingDuplicates.ContainsKey(edge.Target))
            {
                collector.Add(
                    "missing-edge-target",
                    $"Edge '{edge.Id.Value}' references missing target node '{edge.Target.Value}'.",
                    edge.Id,
                    edge.Target);
            }
        }
    }

    private static void ValidatePurposeAndScopeEdges(GraphIndex index, DiagnosticCollector collector)
    {
        var purposeEntries = index.NodesByIdIncludingDuplicates.TryGetValue(
            index.Graph.PurposeNodeId,
            out var entries)
            ? entries
            : [];
        if (purposeEntries.Count == 0)
        {
            collector.Add(
                "missing-purpose-node",
                $"Purpose node '{index.Graph.PurposeNodeId.Value}' does not exist.",
                index.Graph.PurposeNodeId);
        }

        foreach (var pair in index.ScopeParentsByChild.OrderBy(pair => pair.Key))
        {
            if (pair.Value.Count > 1)
            {
                collector.Add(
                    "multiple-scope-parents",
                    $"Node '{pair.Key.Value}' has {pair.Value.Count} scope-parent edges; exactly one is required.",
                    pair.Key);
            }

            foreach (var edge in pair.Value)
            {
                if (edge.ReviewDirection != ReviewDirection.None)
                {
                    collector.Add(
                        "scope-parent-review-direction",
                        $"Scope-parent edge '{edge.Id.Value}' must use review direction None.",
                        edge.Id);
                }
            }
        }

        foreach (var purposeEdge in index.GetScopeParentEdges(index.Graph.PurposeNodeId))
        {
            collector.Add(
                "purpose-has-scope-parent",
                $"Purpose node '{index.Graph.PurposeNodeId.Value}' must not have a scope-parent edge.",
                purposeEdge.Id,
                index.Graph.PurposeNodeId);
        }

        foreach (var node in index.Graph.Nodes)
        {
            if (node.Id == index.Graph.PurposeNodeId) continue;

            var parentCount = index.GetScopeParentEdges(node.Id).Count;
            if (parentCount == 0)
            {
                collector.Add(
                    "missing-scope-parent",
                    $"Node '{node.Id.Value}' has no scope-parent edge; exactly one is required.",
                    node.Id);
            }
        }
    }

    private static void ValidateScopeLineages(
        GraphIndex index,
        GraphValidationOptions options,
        DiagnosticCollector collector,
        ref bool inconclusive)
    {
        var visitedNodes = 0;
        foreach (var node in index.Graph.Nodes)
        {
            if (options.CancellationToken.IsCancellationRequested)
            {
                inconclusive = true;
                collector.Add("validation-cancelled", "Validation was cancelled before all scope lineages were checked.");
                return;
            }

            var path = new List<EntityId>();
            var seen = new HashSet<EntityId>();
            var current = node.Id;
            var reachedPurpose = false;
            var depth = 0;

            while (true)
            {
                if (options.CancellationToken.IsCancellationRequested)
                {
                    inconclusive = true;
                    collector.Add(
                        "validation-cancelled",
                        "Validation was cancelled before all scope lineages were checked.",
                        node.Id,
                        Path: path);
                    return;
                }

                if (++visitedNodes > options.MaxTraversalNodes)
                {
                    inconclusive = true;
                    collector.Add(
                        "traversal-node-limit",
                        $"Scope validation reached the node limit of {options.MaxTraversalNodes}; remaining lineages are omitted.",
                        node.Id,
                        Path: path);
                    return;
                }

                path.Add(current);
                if (current == index.Graph.PurposeNodeId)
                {
                    reachedPurpose = true;
                    break;
                }

                if (!seen.Add(current))
                {
                    collector.Add(
                        "scope-cycle",
                        $"Scope lineage for '{node.Id.Value}' repeats node '{current.Value}'.",
                        node.Id,
                        current,
                        path);
                    break;
                }

                var parents = index.GetScopeParentEdges(current);
                if (parents.Count != 1) break;
                if (++depth > options.MaxTraversalDepth)
                {
                    inconclusive = true;
                    collector.Add(
                        "traversal-depth-limit",
                        $"Scope lineage for '{node.Id.Value}' exceeded depth limit {options.MaxTraversalDepth}; remaining lineage is omitted.",
                        node.Id,
                        Path: path);
                    break;
                }

                current = parents[0].Target;
                if (!index.NodesByIdIncludingDuplicates.ContainsKey(current)) break;
            }

            if (!reachedPurpose && path.Count > 0)
            {
                collector.Add(
                    "scope-does-not-reach-purpose",
                    $"Scope lineage for '{node.Id.Value}' does not reach purpose node '{index.Graph.PurposeNodeId.Value}'.",
                    node.Id,
                    index.Graph.PurposeNodeId,
                    path);
            }
        }
    }

    private sealed class DiagnosticCollector
    {
        private readonly int _limit;
        private readonly List<ValidationDiagnostic> _diagnostics = [];

        public DiagnosticCollector(int limit) => _limit = limit;

        public int Count => _diagnostics.Count;

        public bool WasTruncated { get; private set; }

        public void Add(
            string code,
            string message,
            EntityId? entityId = null,
            EntityId? relatedEntityId = null,
            IReadOnlyList<EntityId>? Path = null)
        {
            if (_diagnostics.Count >= _limit)
            {
                WasTruncated = true;
                return;
            }

            _diagnostics.Add(new ValidationDiagnostic(code, message, entityId, relatedEntityId, Path));
        }

        public bool HasDiagnostic(string code, EntityId entityId) =>
            _diagnostics.Any(diagnostic => diagnostic.Code == code && diagnostic.EntityId == entityId);

        public IReadOnlyList<ValidationDiagnostic> Ordered() => new ReadOnlyCollection<ValidationDiagnostic>(
            _diagnostics
                .OrderBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
                .ThenBy(diagnostic => diagnostic.EntityId)
                .ThenBy(diagnostic => diagnostic.RelatedEntityId)
                .ThenBy(diagnostic => diagnostic.Message, StringComparer.Ordinal)
                .ToArray());
    }
}
