using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Buffers.Binary;
using ValidatedWorld.Core;
using ValidatedWorld.Validation;

namespace ValidatedWorld.Serialization;

public static class Protocol
{
    public const int CurrentVersion = 1;

    public static JsonSerializerOptions CreateJsonOptions() => new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        RespectRequiredConstructorParameters = true,
        WriteIndented = false,
    };

    public static string Serialize<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return JsonSerializer.Serialize(value, CreateJsonOptions());
    }

    public static T Deserialize<T>(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var value = JsonSerializer.Deserialize<T>(json, CreateJsonOptions())
            ?? throw new JsonException("The JSON value cannot be null.");
        if (value is IVersionedProtocol versioned && versioned.Version != CurrentVersion)
            throw new JsonException($"Unsupported protocol version {versioned.Version}; expected {CurrentVersion}.");
        return value;
    }
}

public interface IVersionedProtocol { int Version { get; } }

public sealed record ProtocolRequest(int Version, string Command, JsonElement Payload) : IVersionedProtocol
{
}

public sealed record ProtocolResult(int Version, string Command, string Status, JsonElement? Payload = null) : IVersionedProtocol
{
}

public sealed record GraphDto(
    string ProjectId,
    string Title,
    string PurposeNodeId,
    IReadOnlyList<NodeDto> Nodes,
    IReadOnlyList<EdgeDto> Edges);

public sealed record NodeDto(
    string Id,
    string Text,
    string? Kind,
    IReadOnlyList<string> Tags,
    IReadOnlyList<AttributeDto> Attributes);

public sealed record EdgeDto(
    string Id,
    string Source,
    string Target,
    string Relationship,
    ReviewDirection ReviewDirection,
    string? Rationale,
    IReadOnlyList<string> Tags,
    IReadOnlyList<AttributeDto> Attributes);

public sealed record AttributeDto(string Name, ValueDto Value);

public sealed record ValueDto(GraphValueKind Kind, string? Text, long Integer, bool Boolean, string? Instant);

public sealed record DiagnosticDto(
    string Code,
    string Message,
    string? EntityId,
    string? RelatedEntityId,
    IReadOnlyList<string> Path);

public sealed record ValidationDto(ValidationStatus Status, IReadOnlyList<DiagnosticDto> Diagnostics);

public static class ValidationProtocol
{
    public const int DefaultDiagnosticLimit = 10_000;

    public static ValidationDto ToDto(GraphValidationResult result, int maxDiagnostics = DefaultDiagnosticLimit)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (maxDiagnostics <= 0) throw new ArgumentOutOfRangeException(nameof(maxDiagnostics));
        if (result.Diagnostics.Count > maxDiagnostics)
            throw new InvalidOperationException("The diagnostic output exceeds the configured protocol bound.");

        return new(result.Status, result.Diagnostics.Select(diagnostic => new DiagnosticDto(
            diagnostic.Code, diagnostic.Message, diagnostic.EntityId?.Value,
            diagnostic.RelatedEntityId?.Value, diagnostic.Path?.Select(id => id.Value).ToArray() ?? [])).ToArray());
    }
}

public sealed record OperationDto(
    GraphOperationKind Kind,
    GraphEntityKind EntityKind,
    string EntityId,
    NodeDto? Node,
    EdgeDto? Edge);

public sealed record OperationBatchDto(IReadOnlyList<OperationDto> Operations);

public static class GraphProtocol
{
    public static GraphDto ToDto(ProjectGraph graph) => new(
        graph.ProjectId.Value, graph.Title, graph.PurposeNodeId.Value,
        graph.Nodes.Select(node => ToDto(node)).ToArray(), graph.Edges.Select(edge => ToDto(edge)).ToArray());

    public static ProjectGraph FromDto(GraphDto dto) => new(
        new ProjectId(dto.ProjectId), dto.Title, new EntityId(dto.PurposeNodeId),
        dto.Nodes.Select(FromDto), dto.Edges.Select(FromDto));

    public static OperationBatchDto ToDto(GraphOperationBatch batch) =>
        new(batch.Operations.Select(operation => ToDto(operation)).ToArray());

    public static GraphOperationBatch FromDto(OperationBatchDto dto) =>
        new(dto.Operations.Select(FromDto));

    public static NodeDto ToDto(GraphNode node) => new(node.Id.Value, node.Text, node.Kind,
        node.Tags.ToArray(), node.Attributes.Select(ToDto).ToArray());

    public static EdgeDto ToDto(GraphEdge edge) => new(edge.Id.Value, edge.Source.Value, edge.Target.Value,
        edge.Relationship, edge.ReviewDirection, edge.Rationale, edge.Tags.ToArray(),
        edge.Attributes.Select(ToDto).ToArray());

    public static OperationDto ToDto(GraphOperation operation) => new(
        operation.Kind, operation.EntityKind, operation.EntityId.Value,
        operation.Node is null ? null : ToDto(operation.Node),
        operation.Edge is null ? null : ToDto(operation.Edge));

    private static AttributeDto ToDto(GraphAttribute attribute) => new(attribute.Name, ToDto(attribute.Value));

    private static ValueDto ToDto(GraphValue value) => value.Kind switch
    {
        GraphValueKind.Text => new(value.Kind, value.TextValue, 0, false, null),
        GraphValueKind.Integer => new(value.Kind, null, value.IntegerValue, false, null),
        GraphValueKind.Decimal => new(value.Kind, value.DecimalValue, 0, false, null),
        GraphValueKind.Boolean => new(value.Kind, null, 0, value.BooleanValue, null),
        GraphValueKind.Symbol => new(value.Kind, value.SymbolValue, 0, false, null),
        GraphValueKind.Instant => new(value.Kind, null, 0, false, value.InstantValue.ToString("O")),
        _ => throw new JsonException("The graph value is uninitialized.")
    };

    public static GraphOperation FromDto(OperationDto dto)
    {
        var id = new EntityId(dto.EntityId);
        return dto.Kind == GraphOperationKind.Remove
            ? new GraphOperation(dto.Kind, dto.EntityKind, id)
            : dto.EntityKind == GraphEntityKind.Node
                ? new GraphOperation(dto.Kind, FromDto(dto.Node ?? throw new JsonException("Node is required.")))
                : new GraphOperation(dto.Kind, FromDto(dto.Edge ?? throw new JsonException("Edge is required.")));
    }

    public static GraphNode FromDto(NodeDto dto) => new(new EntityId(dto.Id), dto.Text, dto.Kind, dto.Tags,
        dto.Attributes.Select(attribute => new KeyValuePair<string, GraphValue>(attribute.Name, FromDto(attribute.Value))));

    public static GraphEdge FromDto(EdgeDto dto) => new(new EntityId(dto.Id), new EntityId(dto.Source),
        new EntityId(dto.Target), dto.Relationship, dto.ReviewDirection, dto.Rationale, dto.Tags,
        dto.Attributes.Select(attribute => new KeyValuePair<string, GraphValue>(attribute.Name, FromDto(attribute.Value))));

    private static GraphAttribute FromDto(AttributeDto dto) => new(dto.Name, FromDto(dto.Value));

    private static GraphValue FromDto(ValueDto dto) => dto.Kind switch
    {
        GraphValueKind.Text => GraphValue.FromText(dto.Text ?? throw new JsonException("Text is required.")),
        GraphValueKind.Integer => GraphValue.FromInteger(dto.Integer),
        GraphValueKind.Decimal => GraphValue.FromDecimal(dto.Text ?? throw new JsonException("Decimal is required.")),
        GraphValueKind.Boolean => GraphValue.FromBoolean(dto.Boolean),
        GraphValueKind.Symbol => GraphValue.FromSymbol(dto.Text ?? throw new JsonException("Symbol is required.")),
        GraphValueKind.Instant => GraphValue.FromInstant(DateTimeOffset.Parse(dto.Instant ?? throw new JsonException("Instant is required."), null, System.Globalization.DateTimeStyles.RoundtripKind)),
        _ => throw new JsonException("Unknown graph value kind.")
    };
}

public static class GraphFingerprints
{
    public static string State(ProjectGraph graph) => Hash(EncodingForGraph(graph));

    public static string Proposed(ProjectGraph graph) => State(graph);

    public static string Operations(string baseFingerprint, GraphOperationBatch operations) =>
        Hash(Join("operations", baseFingerprint, EncodingForOperations(operations)));

    public static string Affected(AffectedAnalysis analysis) => Hash(Join(
        "affected", State(analysis.CurrentGraph), State(analysis.ProposedGraph),
        EncodingForOperations(analysis.Operations),
        EncodingForAffected(analysis)));

    public static string Dispositions(AffectedAnalysis analysis, IEnumerable<ReviewDisposition> dispositions) => Hash(
        Join("dispositions", Affected(analysis), EncodingForDispositions(dispositions)));

    private static byte[] EncodingForGraph(ProjectGraph graph)
    {
        var e = new CanonicalEncoder();
        e.String("graph"); e.String(graph.ProjectId.Value); e.String(graph.Title); e.String(graph.PurposeNodeId.Value);
        e.Int(graph.Nodes.Count);
        foreach (var node in graph.Nodes) EncodeNode(e, node);
        e.Int(graph.Edges.Count);
        foreach (var edge in graph.Edges) EncodeEdge(e, edge);
        return e.ToArray();
    }

    private static byte[] EncodingForOperations(GraphOperationBatch batch)
    {
        var e = new CanonicalEncoder(); e.String("operation-batch"); e.Int(batch.Operations.Count);
        foreach (var operation in batch.Operations)
        {
            e.Int((int)operation.Kind); e.Int((int)operation.EntityKind); e.String(operation.EntityId.Value);
            if (operation.Node is not null) EncodeNode(e, operation.Node);
            if (operation.Edge is not null) EncodeEdge(e, operation.Edge);
        }
        return e.ToArray();
    }

    private static byte[] EncodingForAffected(AffectedAnalysis a)
    {
        var e = new CanonicalEncoder(); e.Int((int)a.Status);
        foreach (var id in a.DirectNodeIds) e.String(id.Value);
        foreach (var id in a.SeedNodeIds) e.String(id.Value);
        foreach (var n in a.AffectedNodes) { e.String(n.NodeId.Value); e.Bool(n.IsDirectChange); e.Int(n.Distance); EncodePath(e, n.Explanation); }
        foreach (var c in a.ScopeContext) { e.String(c.NodeId.Value); foreach (var l in c.Lineages) { e.String(l.AffectedNodeId.Value); EncodeIds(e, l.CurrentPath); EncodeIds(e, l.ProposedPath); } }
        foreach (var o in a.Omissions) { e.Int((int)o.Reason); e.Nullable(o.SourceNodeId?.Value); e.Nullable(o.TargetNodeId?.Value); e.Nullable(o.EdgeId?.Value); e.Nullable(o.Depth); e.String(o.Message); }
        return e.ToArray();
    }

    private static byte[] EncodingForDispositions(IEnumerable<ReviewDisposition> values)
    { var e = new CanonicalEncoder(); foreach (var d in values.OrderBy(x => x.NodeId)) { e.String(d.NodeId.Value); e.Int((int)d.Kind); e.Nullable(d.Rationale); } return e.ToArray(); }
    private static void EncodePath(CanonicalEncoder e, AffectedPath p) { EncodeIds(e, p.Nodes); EncodeIds(e, p.Edges); }
    private static void EncodeIds(CanonicalEncoder e, IEnumerable<EntityId> ids) { var a = ids.ToArray(); e.Int(a.Length); foreach (var id in a) e.String(id.Value); }
    private static byte[] Join(params object[] values) { var e = new CanonicalEncoder(); foreach (var v in values) { if (v is byte[] b) e.Bytes(b); else e.String((string)v); } return e.ToArray(); }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void EncodeNode(CanonicalEncoder e, GraphNode n) { e.String(n.Id.Value); e.String(n.Text); e.Nullable(n.Kind); EncodeStrings(e, n.Tags); EncodeAttributes(e, n.Attributes); }
    private static void EncodeEdge(CanonicalEncoder e, GraphEdge x) { e.String(x.Id.Value); e.String(x.Source.Value); e.String(x.Target.Value); e.String(x.Relationship); e.Int((int)x.ReviewDirection); e.Nullable(x.Rationale); EncodeStrings(e, x.Tags); EncodeAttributes(e, x.Attributes); }
    private static void EncodeStrings(CanonicalEncoder e, IEnumerable<string> values) { var a = values.ToArray(); e.Int(a.Length); foreach (var v in a) e.String(v); }
    private static void EncodeAttributes(CanonicalEncoder e, IEnumerable<GraphAttribute> values) { var a = values.ToArray(); e.Int(a.Length); foreach (var x in a) { e.String(x.Name); e.Int((int)x.Value.Kind); e.String(x.Value.ToString()); } }
}

internal sealed class CanonicalEncoder
{
    private readonly MemoryStream _stream = new();
    public void String(string value) => Bytes(Encoding.UTF8.GetBytes(value));
    public void Nullable(string? value) { if (value is null) { Int(-1); } else String(value); }
    public void Nullable(int? value) { if (value is null) Int(-1); else Int(value.Value); }
    public void Int(int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        _stream.Write(bytes);
    }
    public void Bool(bool value) => Int(value ? 1 : 0);
    public void Bytes(byte[] value) { Int(value.Length); _stream.Write(value); }
    public byte[] ToArray() => _stream.ToArray();
}
