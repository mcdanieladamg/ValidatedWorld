using System.Globalization;
using ValidatedWorld.Core;

namespace ValidatedWorld.Core.Tests;

public sealed class GraphModelTests
{
    [Fact]
    public void Stable_ids_are_ordinal_and_reject_invalid_values()
    {
        var upper = new EntityId("A");
        var lower = new EntityId("a");

        Assert.NotEqual(upper, lower);
        Assert.True(upper < lower);
        Assert.Equal("A", upper.Value);
        Assert.Throws<ArgumentException>(() => new EntityId("  "));
        Assert.Throws<ArgumentException>(() => new EntityId("a\n"));
        Assert.Equal(new string('x', 1024), new EntityId(new string('x', 1024)).Value);
        Assert.Throws<ArgumentException>(() => new ProjectId(""));
        Assert.Throws<ArgumentNullException>(() => new ProjectId(null!));
    }

    [Fact]
    public void Scalar_values_preserve_kind_and_reject_noncanonical_forms()
    {
        Assert.Equal(GraphValueKind.Text, GraphValue.FromText("hello").Kind);
        Assert.Equal(42, GraphValue.FromInteger(42).IntegerValue);
        Assert.Equal("0.25", GraphValue.FromDecimal("0.25").DecimalValue);
        Assert.True(GraphValue.FromBoolean(true).BooleanValue);
        var largeDecimal = "1" + new string('0', 4096);
        Assert.Equal(largeDecimal, GraphValue.FromDecimal(largeDecimal).DecimalValue);
        Assert.Equal("requirement", GraphValue.FromSymbol("requirement").SymbolValue);

        var instant = new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(instant, GraphValue.FromInstant(instant).InstantValue);
        Assert.Equal("true", GraphValue.FromBoolean(true).ToString());
        Assert.Equal("42", GraphValue.FromInteger(42).ToString());

        foreach (var value in new[] { "01", "+1", "1.0", "1e2", "-0", "-0.0", "00.5", "1\n" })
        {
            Assert.Throws<ArgumentException>(() => GraphValue.FromDecimal(value));
        }

        Assert.Throws<ArgumentException>(() => GraphValue.FromInstant(instant.ToOffset(TimeSpan.FromHours(1))));
        Assert.Equal(GraphValue.FromDecimal("10.5"), GraphValue.FromDecimal("10.5"));
        Assert.Throws<ArgumentException>(() => GraphValue.FromDecimal("10.50"));
    }

    [Fact]
    public void Nodes_and_edges_canonicalize_metadata_without_mutable_aliases()
    {
        var tags = new[] { "zeta", "alpha" };
        var attributes = new[]
        {
            new KeyValuePair<string, GraphValue>("z", GraphValue.FromInteger(2)),
            new KeyValuePair<string, GraphValue>("a", GraphValue.FromText("one")),
        };
        var node = new GraphNode(new EntityId("node"), "A concept", "unknown-kind", tags, attributes);
        tags[0] = "changed";

        Assert.Equal(new[] { "alpha", "zeta" }, node.Tags);
        Assert.Equal(new[] { "a", "z" }, node.Attributes.Select(attribute => attribute.Name));
        Assert.True(node.TryGetAttribute("a", out var value));
        Assert.Equal(GraphValue.FromText("one"), value);
        Assert.Equal("unknown-kind", node.Kind);
        Assert.Throws<ArgumentException>(() => new GraphNode(new EntityId("bad"), "   "));
        Assert.Throws<ArgumentException>(() => new GraphNode(
            new EntityId("duplicate"),
            "text",
            tags: new[] { "tag", "tag" }));
        Assert.Throws<ArgumentException>(() => new GraphNode(
            new EntityId("duplicate-attribute"),
            "text",
            attributes: new[]
            {
                new KeyValuePair<string, GraphValue>("same", GraphValue.FromBoolean(true)),
                new KeyValuePair<string, GraphValue>("same", GraphValue.FromBoolean(false)),
            }));

        var edge = new GraphEdge(
            new EntityId("edge"),
            new EntityId("node"),
            new EntityId("other"),
            "supports unknown relationship",
            ReviewDirection.SourceToTarget,
            rationale: "because");
        Assert.Equal(ReviewDirection.SourceToTarget, edge.ReviewDirection);
        Assert.Equal("node", edge.Source.Value);
        Assert.Equal("other", edge.Target.Value);
    }

    [Fact]
    public void Project_graph_is_easy_to_construct_and_sorted_by_stable_id()
    {
        var purpose = new GraphNode(new EntityId("purpose"), "The purpose");
        var child = new GraphNode(new EntityId("child"), "A child");
        var graph = new ProjectGraph(
            new ProjectId("technical-project"),
            "Technical Project",
            purpose.Id,
            new[] { child, purpose },
            new[]
            {
                new GraphEdge(new EntityId("scope"), child.Id, purpose.Id, "scope-parent", ReviewDirection.None),
            });

        Assert.Equal(
            new[] { "purpose", "child" }.Order(StringComparer.Ordinal),
            graph.Nodes.Select(node => node.Id.Value));
        Assert.Single(graph.Edges);
        Assert.Equal(purpose.Id, graph.PurposeNodeId);
        Assert.Empty(new GraphNode(new EntityId("plain"), "plain").Tags);
        Assert.Empty(new GraphNode(new EntityId("plain-2"), "plain").Attributes);
    }

    [Fact]
    public void Technical_project_builder_uses_only_public_core_model()
    {
        var graph = TechnicalProjectGraphBuilder.Create();

        Assert.Equal(new ProjectId("technical-project"), graph.ProjectId);
        Assert.Equal(new EntityId("purpose"), graph.PurposeNodeId);
        Assert.True(graph.Nodes.Count >= 6);
        Assert.Contains(graph.Nodes, node => node.Kind == "external-anchor");
        Assert.Contains(graph.Edges, edge =>
            edge.Relationship == "scope-parent" && edge.ReviewDirection == ReviewDirection.None);
        Assert.Contains(graph.Edges, edge =>
            edge.Relationship == "requires" && edge.ReviewDirection == ReviewDirection.SourceToTarget);
        Assert.Contains(graph.Edges, edge =>
            edge.Relationship == "informs" && edge.ReviewDirection == ReviewDirection.TargetToSource);
    }
}

public static class TechnicalProjectGraphBuilder
{
    public static ProjectGraph Create()
    {
        var purpose = new GraphNode(new EntityId("purpose"), "An offline privacy-preserving sensor");
        var power = new GraphNode(new EntityId("scope-power"), "Power behavior", "scope");
        var privacy = new GraphNode(new EntityId("scope-privacy"), "Privacy behavior", "scope");
        var battery = new GraphNode(
            new EntityId("battery-assumption"),
            "The battery lasts for the target duty cycle",
            "assumption");
        var retention = new GraphNode(
            new EntityId("retention-policy"),
            "Collected data is retained only for the required interval",
            "requirement");
        var test = new GraphNode(
            new EntityId("runtime-test"),
            "Runtime behavior is verified on the target device",
            "verification");
        var document = new GraphNode(
            new EntityId("design-anchor"),
            "Design record for the sensor",
            "external-anchor",
            new[] { "artifact" });

        var nodes = new[] { purpose, power, privacy, battery, retention, test, document };
        var edges = new[]
        {
            new GraphEdge(
                new EntityId("scope-power-parent"), power.Id, purpose.Id, "scope-parent", ReviewDirection.None),
            new GraphEdge(
                new EntityId("scope-privacy-parent"), privacy.Id, purpose.Id, "scope-parent", ReviewDirection.None),
            new GraphEdge(
                new EntityId("battery-scope-parent"), battery.Id, power.Id, "scope-parent", ReviewDirection.None),
            new GraphEdge(
                new EntityId("retention-scope-parent"), retention.Id, privacy.Id, "scope-parent", ReviewDirection.None),
            new GraphEdge(
                new EntityId("runtime-scope-parent"), test.Id, power.Id, "scope-parent", ReviewDirection.None),
            new GraphEdge(
                new EntityId("anchor-scope-parent"), document.Id, power.Id, "scope-parent", ReviewDirection.None),
            new GraphEdge(
                new EntityId("battery-requires-test"), battery.Id, test.Id, "requires", ReviewDirection.SourceToTarget),
            new GraphEdge(
                new EntityId("retention-informs-design"),
                retention.Id,
                document.Id,
                "informs",
                ReviewDirection.TargetToSource),
        };

        return new ProjectGraph(
            new ProjectId("technical-project"),
            "Technical Project",
            purpose.Id,
            nodes,
            edges);
    }
}
