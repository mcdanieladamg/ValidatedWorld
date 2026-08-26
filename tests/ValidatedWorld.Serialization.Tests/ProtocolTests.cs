using System.Text.Json;
using ValidatedWorld.Core;
using ValidatedWorld.Serialization;
using ValidatedWorld.Validation;

namespace ValidatedWorld.Serialization.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void Graph_round_trip_preserves_values_and_strict_json_rejects_unknown_members()
    {
        var graph = Graph();
        var json = Protocol.Serialize(GraphProtocol.ToDto(graph));
        var restored = GraphProtocol.FromDto(Protocol.Deserialize<GraphDto>(json));

        Assert.Equal(graph, restored);
        Assert.Throws<JsonException>(() => Protocol.Deserialize<GraphDto>(json[..^1] + ",\"extra\":1}"));
    }

    [Fact]
    public void Versioned_envelopes_reject_unknown_protocol_versions()
    {
        using var document = JsonDocument.Parse("{\"version\":99,\"command\":\"status\",\"payload\":{}}");
        var request = new ProtocolRequest(Protocol.CurrentVersion, "status", document.RootElement.GetProperty("payload"));
        var json = Protocol.Serialize(request).Replace("\"version\":1", "\"version\":99", StringComparison.Ordinal);

        Assert.Throws<JsonException>(() => Protocol.Deserialize<ProtocolRequest>(json));
    }

    [Fact]
    public void Fingerprints_are_order_independent_but_change_when_content_changes()
    {
        var first = Graph();
        var second = new ProjectGraph(first.ProjectId, first.Title, first.PurposeNodeId,
            first.Nodes.Reverse(), first.Edges.Reverse());

        Assert.Equal(GraphFingerprints.State(first), GraphFingerprints.State(second));

        var changed = new ProjectGraph(first.ProjectId, first.Title, first.PurposeNodeId,
            [new GraphNode(new EntityId("purpose"), "Changed purpose")], []);
        Assert.NotEqual(GraphFingerprints.State(first), GraphFingerprints.State(changed));
    }

    [Fact]
    public void Operation_affected_and_disposition_fingerprints_are_deterministic()
    {
        var current = Graph();
        var replacement = new GraphNode(new EntityId("purpose"), "Changed purpose");
        var batch = new GraphOperationBatch([GraphOperation.ReplaceNode(replacement)]);
        var projection = new GraphProjector().Project(current, batch);
        var analysis = new AffectedAnalyzer().Analyze(current, projection);
        var session = analysis.CreateReviewSession();
        session.SetDisposition(new EntityId("purpose"), ReviewDispositionKind.Updated);

        Assert.Equal(GraphFingerprints.Operations("base", batch),
            GraphFingerprints.Operations("base", new GraphOperationBatch(batch.Operations.Reverse())));
        Assert.NotEqual(GraphFingerprints.Affected(analysis), GraphFingerprints.Dispositions(analysis, session.Dispositions));
    }

    private static ProjectGraph Graph()
    {
        var purpose = new GraphNode(new EntityId("purpose"), "The project purpose", "thesis",
            ["root"], [new("answer", GraphValue.FromBoolean(true))]);
        var child = new GraphNode(new EntityId("child"), "A child concept", "concept",
            ["one", "two"], [new("number", GraphValue.FromInteger(3))]);
        var scope = new GraphEdge(new EntityId("child-scope"), child.Id, purpose.Id, "scope-parent", ReviewDirection.None);
        return new ProjectGraph(new ProjectId("project"), "A project", purpose.Id, [child, purpose], [scope]);
    }
}
