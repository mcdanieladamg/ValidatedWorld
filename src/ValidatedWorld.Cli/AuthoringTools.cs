using System.Globalization;
using System.Text.Json;
using ValidatedWorld.Application;
using ValidatedWorld.Core;
using ValidatedWorld.Serialization;
using ValidatedWorld.Validation;

namespace ValidatedWorld.Cli;

public sealed record AuthoringToolExecution(string Output);

public sealed class AuthoringToolHost
{
    public const int MaximumSearchResults = 50;
    public const int MaximumAffectedItems = 5_000;
    public const int MaximumOperations = 1_000;

    private readonly ProjectApplication _application;
    private readonly string _path;
    private ChangeSessionSnapshot? _session;
    private ProjectId? _projectId;
    private bool _searched;

    public AuthoringToolHost(
        ProjectApplication application,
        string path)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _path = System.IO.Path.GetFullPath(string.IsNullOrWhiteSpace(path)
            ? throw new ArgumentException("A database path is required.", nameof(path))
            : path);
    }

    public string Path => _path;
    public ChangeSessionSnapshot? Session => _session;

    public static IReadOnlyList<AuthoringToolDefinition> Definitions { get; } = BuildDefinitions();

    public async Task<AuthoringToolExecution> ExecuteAsync(
        string name,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return name switch
            {
                "project_status" => Result(ProjectStatus(arguments)),
                "initialize_project" => InitializeProject(arguments),
                "search_graph" => Result(Search(arguments)),
                "ranked_search_graph" => Result(RankedSearch(arguments)),
                "read_node" => Result(ReadNode(arguments)),
                "read_edge" => Result(ReadEdge(arguments)),
                "read_scope" => Result(ReadScope(arguments, cancellationToken)),
                "graph_health" => Result(GraphHealth(arguments, cancellationToken)),
                "begin_change" => Result(BeginChange(arguments)),
                "put_node" => Result(PutNode(arguments, cancellationToken)),
                "put_edge" => Result(PutEdge(arguments, cancellationToken)),
                "remove_entity" => Result(RemoveEntity(arguments, cancellationToken)),
                "proposal_preview" => Result(Preview(arguments)),
                "write_change" => Result(await WriteChange(arguments, cancellationToken)),
                "discard_change" => Result(DiscardChange(arguments)),
                _ => Result(new { ok = false, error = $"Unknown authoring tool '{name}'." }),
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Result(new { ok = false, error = exception.Message, errorType = exception.GetType().Name });
        }
    }

    public string CompletePreview()
    {
        var snapshot = RequireSession();
        return CliJson.Serialize(new
        {
            reference = CliDto.Reference(snapshot.Reference),
            operations = GraphProtocol.ToDto(snapshot.Operations),
            proposedProject = new
            {
                projectId = snapshot.ProposedGraph.ProjectId.Value,
                snapshot.ProposedGraph.Title,
                purposeNodeId = snapshot.ProposedGraph.PurposeNodeId.Value,
                nodeCount = snapshot.ProposedGraph.Nodes.Count,
                edgeCount = snapshot.ProposedGraph.Edges.Count,
            },
            affected = CliDto.Affected(snapshot.Affected),
            dispositions = snapshot.Dispositions.Select(value => new
            {
                nodeId = value.NodeId.Value,
                kind = value.Kind.ToString(),
                value.Rationale,
            }),
            presentedContextNodeIds = snapshot.PresentedContextNodeIds.Select(id => id.Value),
            readiness = new { snapshot.Readiness.IsReady, snapshot.Readiness.Blockers },
        });
    }

    private object ProjectStatus(JsonElement arguments)
    {
        Empty(arguments);
        if (!File.Exists(_path))
            return new { exists = false, path = _path, message = "No project exists at this path." };
        var status = _application.Status(_path);
        _projectId = status.ProjectId;
        return new
        {
            exists = true,
            project = CliDto.Status(status),
            semanticReview = CliDto.Availability(_application.SemanticReviewAvailability),
        };
    }

    private AuthoringToolExecution InitializeProject(JsonElement arguments)
    {
        if (File.Exists(_path)) throw new InvalidOperationException("A project already exists at this path.");
        var projectId = Required(arguments, "project_id");
        var title = Required(arguments, "title");
        var purposeId = Required(arguments, "purpose_id");
        var purposeText = Required(arguments, "purpose_text");
        var stored = _application.Initialize(
            _path,
            new ProjectId(projectId),
            title,
            new EntityId(purposeId),
            purposeText);
        _projectId = stored.Graph.ProjectId;
        return Result(new
        {
            initialized = true,
            project = CliDto.Stored(stored),
        });
    }

    private object Search(JsonElement arguments)
    {
        var text = Nullable(arguments, "text");
        var tag = Nullable(arguments, "tag");
        var limit = Integer(arguments, "limit", 1, MaximumSearchResults);
        if ((text is null) == (tag is null))
            throw new ArgumentException("Supply exactly one of text or tag.");
        var queries = Queries();
        _searched = true;
        return text is not null
            ? CliDto.Search(queries.Search(text, new QueryPageRequest(limit)))
            : CliDto.Search(queries.SearchByTag(tag!, new QueryPageRequest(limit)));
    }

    private object RankedSearch(JsonElement arguments)
    {
        var text = Required(arguments, "text");
        var limit = Integer(arguments, "limit", 1, MaximumSearchResults);
        _searched = true;
        return CliDto.RankedSearch(Queries().SearchRanked(text, new QueryPageRequest(limit)));
    }

    private object ReadNode(JsonElement arguments) =>
        GraphProtocol.ToDto(Queries().GetNode(new EntityId(Required(arguments, "node_id"))));

    private object ReadEdge(JsonElement arguments) =>
        GraphProtocol.ToDto(Queries().GetEdge(new EntityId(Required(arguments, "edge_id"))));

    private object ReadScope(JsonElement arguments, CancellationToken cancellationToken) =>
        CliDto.Scope(Queries().GetScope(
            new EntityId(Required(arguments, "node_id")),
            new QueryPageRequest(Integer(arguments, "limit", 1, MaximumSearchResults)),
            new QueryTraversalOptions { MaxDepth = 1_000, MaxVisitedNodes = 10_000, CancellationToken = cancellationToken }));

    private object GraphHealth(JsonElement arguments, CancellationToken cancellationToken) =>
        CliDto.GraphObservability(Queries().GetGraphObservability(new GraphObservabilityOptions
        {
            MaxItems = Integer(arguments, "limit", 1, MaximumSearchResults),
            CancellationToken = cancellationToken,
        }));

    private object BeginChange(JsonElement arguments)
    {
        if (_session is not null) throw new InvalidOperationException("This conversation already has an active change.");
        EnsureProject();
        var intent = Required(arguments, "intent");
        _session = _application.BeginChange(_path, _projectId!.Value, "ai-authoring-agent", intent);
        return SnapshotSummary(_session);
    }

    private object PutNode(JsonElement arguments, CancellationToken cancellationToken)
    {
        var mode = OperationMode(arguments);
        if (mode == GraphOperationKind.Add && !_searched)
            throw new InvalidOperationException("Search the existing graph before adding a node.");
        var node = new GraphNode(
            new EntityId(Required(arguments, "id")),
            Required(arguments, "text"),
            Nullable(arguments, "kind"),
            Strings(arguments, "tags"),
            Attributes(arguments));
        return Patch(new GraphOperation(mode, node), cancellationToken);
    }

    private object PutEdge(JsonElement arguments, CancellationToken cancellationToken)
    {
        var mode = OperationMode(arguments);
        if (mode == GraphOperationKind.Add && !_searched)
            throw new InvalidOperationException("Search the existing graph before adding an edge.");
        var edge = new GraphEdge(
            new EntityId(Required(arguments, "id")),
            new EntityId(Required(arguments, "source")),
            new EntityId(Required(arguments, "target")),
            Required(arguments, "relationship"),
            Enum.Parse<ReviewDirection>(Required(arguments, "review_direction"), ignoreCase: true),
            Nullable(arguments, "rationale"),
            Strings(arguments, "tags"),
            Attributes(arguments));
        return Patch(new GraphOperation(mode, edge), cancellationToken);
    }

    private object RemoveEntity(JsonElement arguments, CancellationToken cancellationToken)
    {
        var kind = Required(arguments, "entity_kind") switch
        {
            "node" => GraphEntityKind.Node,
            "edge" => GraphEntityKind.Edge,
            _ => throw new ArgumentException("entity_kind must be node or edge."),
        };
        return Patch(new GraphOperation(GraphOperationKind.Remove, kind,
            new EntityId(Required(arguments, "id"))), cancellationToken);
    }

    private object Patch(GraphOperation operation, CancellationToken cancellationToken)
    {
        var snapshot = RequireSession();
        if (snapshot.Operations.Operations.Count >= MaximumOperations &&
            snapshot.Operations.Operations.All(existing => existing.EntityId != operation.EntityId))
            throw new InvalidOperationException($"A proposal cannot exceed {MaximumOperations} operations.");
        _session = _application.PatchChange(
            snapshot.Reference,
            new GraphOperationBatch([operation]),
            new AffectedAnalysisOptions
            {
                MaxTraversalDepth = 10_000,
                MaxAffectedNodes = 100_000,
                MaxOutputItems = MaximumAffectedItems,
                CancellationToken = cancellationToken,
            });
        return SnapshotSummary(_session);
    }

    private object Preview(JsonElement arguments)
    {
        Empty(arguments);
        return JsonSerializer.Deserialize<JsonElement>(CompletePreview(), CliJson.Options);
    }

    private async Task<object> WriteChange(JsonElement arguments, CancellationToken cancellationToken)
    {
        Empty(arguments);
        var snapshot = RequireSession();
        if (snapshot.Operations.Operations.Count == 0)
            throw new InvalidOperationException("There is no proposal to write.");
        if (!snapshot.Affected.IsComplete || !snapshot.Affected.CurrentValidation.IsValid ||
            !snapshot.Affected.ProposedValidation.IsValid)
            throw new InvalidOperationException(
                "The proposal cannot be written while affected analysis is incomplete or graph validation is invalid.");

        var direct = snapshot.Affected.AffectedNodes
            .Where(node => node.IsDirectChange)
            .Select(node => node.NodeId)
            .ToHashSet();
        var dispositions = snapshot.Dispositions
            .Where(value => value.Kind == ReviewDispositionKind.Pending)
            .Select(value => new ReviewDisposition(
                value.NodeId,
                direct.Contains(value.NodeId)
                    ? ReviewDispositionKind.Updated
                    : ReviewDispositionKind.ReviewedNoChange,
                null))
            .ToArray();
        snapshot = _application.ReviewChange(
            snapshot.Reference,
            new ChangeReviewUpdate(
                dispositions,
                snapshot.Affected.ScopeContext.Select(value => value.NodeId)));
        _session = snapshot;
        if (!snapshot.Readiness.IsReady)
            throw new InvalidOperationException(string.Join(" ", snapshot.Readiness.Blockers));

        var result = await _application.WriteChangeAsync(
            snapshot.Reference,
            new ChangeWriteOptions(BypassAiReview: false),
            cancellationToken);
        if (result.Status == ChangeWriteStatus.Written)
        {
            _session = null;
        }
        return new { result = CliDto.Write(result) };
    }

    private object DiscardChange(JsonElement arguments)
    {
        Empty(arguments);
        var snapshot = RequireSession();
        var discarded = _application.DiscardChange(snapshot.Reference);
        _session = null;
        return new { discarded.ProjectId.Value, discarded.SessionId, discarded.DiscardedUtc };
    }

    private ProjectQueries Queries()
    {
        EnsureProject();
        return _application.Queries(_path, _projectId);
    }

    private void EnsureProject()
    {
        if (_projectId is not null) return;
        if (!File.Exists(_path)) throw new InvalidOperationException("Initialize the project first.");
        _projectId = _application.Status(_path).ProjectId;
    }

    private ChangeSessionSnapshot RequireSession() => _session ??
        throw new InvalidOperationException("Begin a change before using this tool.");

    private static object SnapshotSummary(ChangeSessionSnapshot snapshot) => new
    {
        reference = CliDto.Reference(snapshot.Reference),
        operationCount = snapshot.Operations.Operations.Count,
        affectedNodeCount = snapshot.Affected.AffectedNodes.Count,
        contextNodeCount = snapshot.Affected.ScopeContext.Count,
        pendingReviewCount = snapshot.Dispositions.Count(value => value.Kind == ReviewDispositionKind.Pending),
        validation = snapshot.Affected.ProposedValidation.Status,
        analysis = snapshot.Affected.Status,
        omissions = snapshot.Affected.Omissions.Select(value => value.Message),
        note = "Use proposal_preview for complete operations, affected evidence, and scope context.",
    };

    private static AuthoringToolExecution Result(object value) =>
        new(CliJson.Serialize(value));

    private static GraphOperationKind OperationMode(JsonElement arguments) => Required(arguments, "mode") switch
    {
        "add" => GraphOperationKind.Add,
        "replace" => GraphOperationKind.Replace,
        _ => throw new ArgumentException("mode must be add or replace."),
    };

    private static IEnumerable<KeyValuePair<string, GraphValue>> Attributes(JsonElement arguments)
    {
        foreach (var attribute in arguments.GetProperty("attributes").EnumerateArray())
        {
            var name = Required(attribute, "name");
            var kind = Required(attribute, "kind");
            var value = Required(attribute, "value");
            yield return new KeyValuePair<string, GraphValue>(name, kind switch
            {
                "text" => GraphValue.FromText(value),
                "integer" => GraphValue.FromInteger(long.Parse(value, CultureInfo.InvariantCulture)),
                "decimal" => GraphValue.FromDecimal(value),
                "boolean" => GraphValue.FromBoolean(bool.Parse(value)),
                "symbol" => GraphValue.FromSymbol(value),
                "instant" => GraphValue.FromInstant(DateTimeOffset.Parse(value, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind)),
                _ => throw new ArgumentException($"Unknown attribute kind '{kind}'."),
            });
        }
    }

    private static string[] Strings(JsonElement arguments, string name) =>
        arguments.GetProperty(name).EnumerateArray().Select(value => value.GetString() ??
            throw new ArgumentException($"{name} values must be strings.")).ToArray();

    private static string Required(JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new ArgumentException($"'{name}' is required.");
        return value.GetString()!;
    }

    private static string? Nullable(JsonElement arguments, string name)
    {
        var value = arguments.GetProperty(name);
        return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
    }

    private static int Integer(JsonElement arguments, string name, int minimum, int maximum)
    {
        var value = arguments.GetProperty(name).GetInt32();
        return value >= minimum && value <= maximum
            ? value
            : throw new ArgumentOutOfRangeException(name, $"{name} must be between {minimum} and {maximum}.");
    }

    private static void Empty(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || arguments.EnumerateObject().Any())
            throw new ArgumentException("This tool accepts an empty object.");
    }

    private static IReadOnlyList<AuthoringToolDefinition> BuildDefinitions()
    {
        static JsonElement Schema(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        var empty = Schema("""{"type":"object","properties":{},"required":[],"additionalProperties":false}""");
        var attribute = """{"type":"array","items":{"type":"object","properties":{"name":{"type":"string"},"kind":{"type":"string","enum":["text","integer","decimal","boolean","symbol","instant"]},"value":{"type":"string"}},"required":["name","kind","value"],"additionalProperties":false}}""";
        return
        [
            new("project_status", "Inspect the fixed project path and AI review availability without a provider call.", empty),
            new("initialize_project", "Create a new purpose-only project only when project_status reports that the fixed path does not exist. Existing destinations are never overwritten.",
                Schema("""{"type":"object","properties":{"project_id":{"type":"string"},"title":{"type":"string"},"purpose_id":{"type":"string"},"purpose_text":{"type":"string"}},"required":["project_id","title","purpose_id","purpose_text"],"additionalProperties":false}""")),
            new("search_graph", "Bounded text or exact-tag search. Search before creating and before changing closed-world claims.",
                Schema("""{"type":"object","properties":{"text":{"type":["string","null"]},"tag":{"type":["string","null"]},"limit":{"type":"integer","minimum":1,"maximum":50}},"required":["text","tag","limit"],"additionalProperties":false}""")),
            new("ranked_search_graph", "Bounded deterministic lexical search with explainable ranking for stable IDs, exact tags, phrases, tokens, and metadata.",
                Schema("""{"type":"object","properties":{"text":{"type":"string"},"limit":{"type":"integer","minimum":1,"maximum":50}},"required":["text","limit"],"additionalProperties":false}""")),
            new("read_node", "Read one node by stable ID.", Schema("""{"type":"object","properties":{"node_id":{"type":"string"}},"required":["node_id"],"additionalProperties":false}""")),
            new("read_edge", "Read one edge by stable ID.", Schema("""{"type":"object","properties":{"edge_id":{"type":"string"}},"required":["edge_id"],"additionalProperties":false}""")),
            new("read_scope", "Read one node's complete upstream scope path and bounded descendants.", Schema("""{"type":"object","properties":{"node_id":{"type":"string"},"limit":{"type":"integer","minimum":1,"maximum":50}},"required":["node_id","limit"],"additionalProperties":false}""")),
            new("graph_health", "Read bounded graph-quality diagnostics: scope coverage, unreachable nodes, review fan-out, isolated claims, missing rationales, and tag use.", Schema("""{"type":"object","properties":{"limit":{"type":"integer","minimum":1,"maximum":50}},"required":["limit"],"additionalProperties":false}""")),
            new("begin_change", "Begin the one process-local incremental change session.", Schema("""{"type":"object","properties":{"intent":{"type":"string"}},"required":["intent"],"additionalProperties":false}""")),
            new("put_node", "Add or replace one complete node. Adding is rejected until search_graph has run.",
                Schema("""{"type":"object","properties":{"mode":{"type":"string","enum":["add","replace"]},"id":{"type":"string"},"text":{"type":"string"},"kind":{"type":["string","null"]},"tags":{"type":"array","items":{"type":"string"}},"attributes":ATTRIBUTES},"required":["mode","id","text","kind","tags","attributes"],"additionalProperties":false}""".Replace("ATTRIBUTES", attribute, StringComparison.Ordinal))),
            new("put_edge", "Add or replace one complete explicit edge. scope-parent edges must use review direction none.",
                Schema("""{"type":"object","properties":{"mode":{"type":"string","enum":["add","replace"]},"id":{"type":"string"},"source":{"type":"string"},"target":{"type":"string"},"relationship":{"type":"string"},"review_direction":{"type":"string","enum":["none","sourceToTarget","targetToSource","both"]},"rationale":{"type":["string","null"]},"tags":{"type":"array","items":{"type":"string"}},"attributes":ATTRIBUTES},"required":["mode","id","source","target","relationship","review_direction","rationale","tags","attributes"],"additionalProperties":false}""".Replace("ATTRIBUTES", attribute, StringComparison.Ordinal))),
            new("remove_entity", "Remove one node or edge explicitly. Removing a node never cascades incident edges.", Schema("""{"type":"object","properties":{"entity_kind":{"type":"string","enum":["node","edge"]},"id":{"type":"string"}},"required":["entity_kind","id"],"additionalProperties":false}""")),
            new("proposal_preview", "Return the exact operations, affected paths, complete required scope context, validation, dispositions, and fingerprints.", empty),
            new("write_change", "Account for the exact current affected/context set and attempt the atomic write. Never bypasses independent semantic review.", empty),
            new("discard_change", "Discard the unresolved in-memory proposal.", empty),
        ];
    }
}
