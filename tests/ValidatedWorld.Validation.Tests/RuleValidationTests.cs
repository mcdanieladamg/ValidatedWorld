using ValidatedWorld.Core;
using ValidatedWorld.Serialization;
using ValidatedWorld.Validation;

namespace ValidatedWorld.Validation.Tests;

public sealed class RuleValidationTests
{
    [Fact]
    public void Versioned_rules_evaluate_complete_graph_with_bounded_offenders()
    {
        var graph = Graph(
            Node("one", "One", "claim", ["required"]),
            Node("two", "Two", "claim"),
            Rule("must-tag", "Every claim needs required.",
                "{\"all\":{\"set\":{\"nodes\":{\"kind\":\"claim\"}},\"condition\":{\"hasTag\":\"required\"}}}"));

        var result = new GraphRuleValidator().Validate(graph, RuleProtocol.Parse(graph),
            new RuleValidationOptions { MaxOffendingEntityIds = 1 });

        Assert.Equal(ValidationStatus.Invalid, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("must-tag", diagnostic.RuleId?.Value);
        Assert.Equal(new[] { "two" }, diagnostic.OffendingEntityIds.Select(id => id.Value));
        Assert.Equal(1, diagnostic.TotalOffendingCount);
    }

    [Fact]
    public void Views_compose_and_cycles_or_unknown_operators_are_inconclusive_inputs()
    {
        var valid = Graph(
            View("claims", "{\"nodes\":{\"kind\":\"claim\"}}"),
            Rule("one-claim", "Exactly one claim.",
                "{\"count\":{\"set\":{\"view\":\"claims\"},\"compare\":\"eq\",\"value\":1}}"),
            Node("claim", "Claim", "claim"));
        Assert.True(new GraphRuleValidator().Validate(valid, RuleProtocol.Parse(valid)).IsValid);

        var unknown = Graph(Rule("bad", "Bad", "{\"sql\":\"select 1\"}"));
        var error = Assert.Throws<RuleFormatException>(() => RuleProtocol.Parse(unknown));
        Assert.Equal("unknown-rule-operator", error.Code);

        var cycle = Graph(
            View("a", "{\"view\":\"b\"}"),
            View("b", "{\"view\":\"a\"}"));
        Assert.Equal("cyclic-rule-view", Assert.Throws<RuleFormatException>(() => RuleProtocol.Parse(cycle)).Code);
    }

    [Fact]
    public void Selectors_match_typed_scalar_attributes_without_coercion()
    {
        var graph = Graph(
            Node("ready", "Ready", "claim", null, [new("priority", GraphValue.FromInteger(2))]),
            Node("text", "Text", "claim", null, [new("priority", GraphValue.FromText("2"))]),
            Rule("integer-priority", "Exactly one claim has integer priority two.",
                "{\"count\":{\"set\":{\"nodes\":{\"kind\":\"claim\",\"attributes\":[{\"name\":\"priority\",\"kind\":\"integer\",\"value\":2}]}},\"compare\":\"eq\",\"value\":1}}"));

        Assert.True(new GraphRuleValidator().Validate(graph, RuleProtocol.Parse(graph)).IsValid);
    }

    private static ProjectGraph Graph(params GraphNode[] children)
    {
        var purpose = Node("purpose", "Purpose", "purpose");
        var edges = children.Select(child => new GraphEdge(new EntityId(child.Id.Value + "-scope"), child.Id,
            purpose.Id, "scope-parent", ReviewDirection.None));
        return new ProjectGraph(new ProjectId("rules"), "Rules", purpose.Id, new[] { purpose }.Concat(children), edges);
    }

    private static GraphNode Node(string id, string text, string kind, IEnumerable<string>? tags = null,
        IEnumerable<KeyValuePair<string, GraphValue>>? attributes = null) => new(new EntityId(id), text, kind, tags, attributes);
    private static GraphNode Rule(string id, string message, string expression) => Node(id, message,
        RuleProtocol.RuleKind, [RuleProtocol.ActiveTag],
        [new("rule:version", GraphValue.FromInteger(1)), new("rule:expression", GraphValue.FromText(expression))]);
    private static GraphNode View(string name, string expression) => Node("view-" + name, name,
        RuleProtocol.ViewKind, null,
        [new("view:version", GraphValue.FromInteger(1)), new("view:name", GraphValue.FromText(name)),
            new("view:expression", GraphValue.FromText(expression))]);
}
