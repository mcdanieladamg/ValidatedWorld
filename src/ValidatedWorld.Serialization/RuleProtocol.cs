using System.Collections.ObjectModel;
using System.Text.Json;
using ValidatedWorld.Core;

namespace ValidatedWorld.Serialization;

public static class RuleProtocol
{
    public const int CurrentVersion = 1;
    public const int MaximumRules = 128;
    public const int MaximumViews = 128;
    public const int MaximumExpressionDepth = 32;
    public const string RuleKind = "validation-rule";
    public const string ViewKind = "validation-view";
    public const string ActiveTag = "rule:active";

    public static RuleDocument Parse(ProjectGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var rules = graph.Nodes.Where(node => node.Kind == RuleKind && node.Tags.Contains(ActiveTag, StringComparer.Ordinal)).ToArray();
        var views = graph.Nodes.Where(node => node.Kind == ViewKind).ToArray();
        if (rules.Length > MaximumRules) throw new RuleFormatException("rule-count-limit", $"The graph contains {rules.Length} active rules; the limit is {MaximumRules}.");
        if (views.Length > MaximumViews) throw new RuleFormatException("view-count-limit", $"The graph contains {views.Length} views; the limit is {MaximumViews}.");

        var parsedViews = new Dictionary<string, RuleSetExpression>(StringComparer.Ordinal);
        var viewOwners = new Dictionary<string, EntityId>(StringComparer.Ordinal);
        foreach (var node in views.OrderBy(node => node.Id))
        {
            RequireVersion(node, "view:version");
            var name = RequireText(node, "view:name");
            if (!parsedViews.TryAdd(name, ParseSet(RequireText(node, "view:expression"), node.Id)))
                throw new RuleFormatException("duplicate-view-name", $"View name '{name}' is duplicated.", node.Id);
            viewOwners.Add(name, node.Id);
        }

        var parsedRules = new List<GraphRule>();
        foreach (var node in rules.OrderBy(node => node.Id))
        {
            RequireVersion(node, "rule:version");
            parsedRules.Add(new GraphRule(node.Id, node.Text, ParseBoolean(RequireText(node, "rule:expression"), node.Id)));
        }

        ValidateViewReferences(parsedViews, viewOwners);
        return new RuleDocument(
            new ReadOnlyDictionary<string, RuleSetExpression>(parsedViews),
            new ReadOnlyCollection<GraphRule>(parsedRules));
    }

    private static void RequireVersion(GraphNode node, string attribute)
    {
        if (!node.TryGetAttribute(attribute, out var value) || value.Kind != GraphValueKind.Integer || value.IntegerValue != CurrentVersion)
            throw new RuleFormatException("unsupported-rule-version", $"Node '{node.Id.Value}' must declare {attribute}={CurrentVersion}.", node.Id);
    }

    private static string RequireText(GraphNode node, string attribute)
    {
        if (!node.TryGetAttribute(attribute, out var value) || value.Kind != GraphValueKind.Text)
            throw new RuleFormatException("missing-rule-attribute", $"Node '{node.Id.Value}' requires text attribute '{attribute}'.", node.Id);
        return value.TextValue;
    }

    private static RuleSetExpression ParseSet(string json, EntityId owner)
    {
        using var document = ParseJson(json, owner);
        return ParseSet(document.RootElement, owner, 1);
    }

    private static RuleBooleanExpression ParseBoolean(string json, EntityId owner)
    {
        using var document = ParseJson(json, owner);
        return ParseBoolean(document.RootElement, owner, 1);
    }

    private static JsonDocument ParseJson(string json, EntityId owner)
    {
        try
        {
            return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaximumExpressionDepth, CommentHandling = JsonCommentHandling.Disallow });
        }
        catch (JsonException exception)
        {
            throw new RuleFormatException("malformed-rule-expression", $"Rule expression on '{owner.Value}' is malformed JSON: {exception.Message}", owner, exception);
        }
    }

    private static RuleSetExpression ParseSet(JsonElement element, EntityId owner, int depth)
    {
        EnsureDepth(depth, owner);
        var property = SingleProperty(element, owner);
        return property.Name switch
        {
            "nodes" => new EntitySelectorExpression(GraphEntityKind.Node, ParseFilter(property.Value, owner, depth + 1)),
            "edges" => new EntitySelectorExpression(GraphEntityKind.Edge, ParseFilter(property.Value, owner, depth + 1)),
            "view" when property.Value.ValueKind == JsonValueKind.String => new ViewReferenceExpression(property.Value.GetString()!),
            "union" => new SetCompositionExpression(SetOperator.Union, ParseSetArray(property.Value, owner, depth + 1)),
            "intersect" => new SetCompositionExpression(SetOperator.Intersect, ParseSetArray(property.Value, owner, depth + 1)),
            "except" => new SetCompositionExpression(SetOperator.Except, ParseSetArray(property.Value, owner, depth + 1)),
            "reachable" => ParseReachable(property.Value, owner, depth + 1),
            _ => throw Unknown(property.Name, "set", owner),
        };
    }

    private static RuleBooleanExpression ParseBoolean(JsonElement element, EntityId owner, int depth)
    {
        EnsureDepth(depth, owner);
        var property = SingleProperty(element, owner);
        return property.Name switch
        {
            "and" => new BooleanCompositionExpression(BooleanOperator.And, ParseBooleanArray(property.Value, owner, depth + 1)),
            "or" => new BooleanCompositionExpression(BooleanOperator.Or, ParseBooleanArray(property.Value, owner, depth + 1)),
            "not" => new NotExpression(ParseBoolean(property.Value, owner, depth + 1)),
            "exists" => new CountExpression(ParseSet(property.Value, owner, depth + 1), ComparisonOperator.GreaterThan, 0),
            "count" => ParseCount(property.Value, owner, depth + 1),
            "subset" => new SetComparisonExpression(SetComparisonOperator.Subset, ParseSetPair(property.Value, owner, depth + 1)),
            "equalSets" => new SetComparisonExpression(SetComparisonOperator.Equal, ParseSetPair(property.Value, owner, depth + 1)),
            "all" => ParseAll(property.Value, owner, depth + 1),
            "acyclic" => ParseTopology(property.Value, owner, depth + 1, requireChain: false),
            "singleChain" => ParseTopology(property.Value, owner, depth + 1, requireChain: true),
            "tagSuffixMatch" => ParseTagSuffixMatch(property.Value, owner, depth + 1),
            _ => throw Unknown(property.Name, "Boolean", owner),
        };
    }

    private static EntityFilter ParseFilter(JsonElement element, EntityId owner, int depth)
    {
        EnsureObject(element, owner);
        string? id = null, kind = null, relationship = null, tagPrefix = null;
        var tagsAll = Array.Empty<string>();
        var attributes = Array.Empty<AttributeMatch>();
        RuleSetExpression? sourceIn = null, targetIn = null;
        foreach (var property in element.EnumerateObject())
        {
            switch (property.Name)
            {
                case "id": id = String(property.Value, property.Name, owner); break;
                case "kind": kind = String(property.Value, property.Name, owner); break;
                case "relationship": relationship = String(property.Value, property.Name, owner); break;
                case "tagPrefix": tagPrefix = String(property.Value, property.Name, owner); break;
                case "tagsAll": tagsAll = StringArray(property.Value, property.Name, owner); break;
                case "attributes": attributes = ParseAttributes(property.Value, owner); break;
                case "sourceIn": sourceIn = ParseSet(property.Value, owner, depth + 1); break;
                case "targetIn": targetIn = ParseSet(property.Value, owner, depth + 1); break;
                default: throw Unknown(property.Name, "selector", owner);
            }
        }
        return new EntityFilter(id, kind, relationship, tagPrefix, tagsAll, attributes, sourceIn, targetIn);
    }

    private static AttributeMatch[] ParseAttributes(JsonElement element, EntityId owner)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new RuleFormatException("invalid-rule-type", "Selector attributes must be an array.", owner);
        var result = new List<AttributeMatch>();
        foreach (var item in element.EnumerateArray())
        {
            EnsureObject(item, owner);
            var name = String(Required(item, "name", owner), "name", owner);
            var kind = String(Required(item, "kind", owner), "kind", owner);
            var raw = Required(item, "value", owner);
            GraphValue value;
            try
            {
                value = kind switch
                {
                    "text" => GraphValue.FromText(String(raw, "value", owner)),
                    "integer" when raw.TryGetInt64(out var integer) => GraphValue.FromInteger(integer),
                    "decimal" => GraphValue.FromDecimal(String(raw, "value", owner)),
                    "boolean" when raw.ValueKind is JsonValueKind.True or JsonValueKind.False => GraphValue.FromBoolean(raw.GetBoolean()),
                    "symbol" => GraphValue.FromSymbol(String(raw, "value", owner)),
                    "instant" => GraphValue.FromInstant(DateTimeOffset.Parse(String(raw, "value", owner),
                        System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind)),
                    _ => throw new RuleFormatException("invalid-rule-value", $"Unsupported or mismatched attribute kind '{kind}'.", owner),
                };
            }
            catch (ArgumentException exception)
            {
                throw new RuleFormatException("invalid-rule-value", $"Attribute match '{name}' has an invalid {kind} value.", owner, exception);
            }
            catch (FormatException exception)
            {
                throw new RuleFormatException("invalid-rule-value", $"Attribute match '{name}' has an invalid {kind} value.", owner, exception);
            }
            EnsureOnly(item, owner, "name", "kind", "value");
            result.Add(new AttributeMatch(name, value));
        }
        if (result.GroupBy(value => value.Name, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new RuleFormatException("duplicate-rule-attribute", "A selector cannot repeat an attribute name.", owner);
        return result.OrderBy(value => value.Name, StringComparer.Ordinal).ToArray();
    }

    private static CountExpression ParseCount(JsonElement element, EntityId owner, int depth)
    {
        EnsureObject(element, owner);
        var set = Required(element, "set", owner);
        var compare = ParseComparison(String(Required(element, "compare", owner), "compare", owner), owner);
        var value = Required(element, "value", owner);
        if (!value.TryGetInt32(out var number) || number < 0) throw new RuleFormatException("invalid-rule-value", "Count values must be non-negative integers.", owner);
        EnsureOnly(element, owner, "set", "compare", "value");
        return new CountExpression(ParseSet(set, owner, depth + 1), compare, number);
    }

    private static AllExpression ParseAll(JsonElement element, EntityId owner, int depth)
    {
        EnsureObject(element, owner);
        var set = ParseSet(Required(element, "set", owner), owner, depth + 1);
        var condition = Required(element, "condition", owner);
        var property = SingleProperty(condition, owner);
        EntityCondition parsed = property.Name switch
        {
            "hasTag" => new HasTagCondition(String(property.Value, property.Name, owner)),
            "tagCount" => ParseTagCount(property.Value, owner),
            _ => throw Unknown(property.Name, "entity condition", owner),
        };
        EnsureOnly(element, owner, "set", "condition");
        return new AllExpression(set, parsed);
    }

    private static TagCountCondition ParseTagCount(JsonElement element, EntityId owner)
    {
        EnsureObject(element, owner);
        var prefix = String(Required(element, "prefix", owner), "prefix", owner);
        var compare = ParseComparison(String(Required(element, "compare", owner), "compare", owner), owner);
        var value = Required(element, "value", owner);
        if (!value.TryGetInt32(out var number) || number < 0) throw new RuleFormatException("invalid-rule-value", "Tag counts must be non-negative integers.", owner);
        EnsureOnly(element, owner, "prefix", "compare", "value");
        return new TagCountCondition(prefix, compare, number);
    }

    private static TopologyExpression ParseTopology(JsonElement element, EntityId owner, int depth, bool requireChain)
    {
        EnsureObject(element, owner);
        var nodes = ParseSet(Required(element, "nodes", owner), owner, depth + 1);
        var edges = ParseSet(Required(element, "edges", owner), owner, depth + 1);
        EnsureOnly(element, owner, "nodes", "edges");
        return new TopologyExpression(nodes, edges, requireChain);
    }

    private static ReachableExpression ParseReachable(JsonElement element, EntityId owner, int depth)
    {
        EnsureObject(element, owner);
        var start = ParseSet(Required(element, "from", owner), owner, depth + 1);
        var edges = ParseSet(Required(element, "edges", owner), owner, depth + 1);
        var direction = String(Required(element, "direction", owner), "direction", owner);
        if (direction is not "outgoing" and not "incoming") throw new RuleFormatException("invalid-rule-value", "Reachable direction must be outgoing or incoming.", owner);
        var includeStart = element.TryGetProperty("includeStart", out var include) && include.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new RuleFormatException("invalid-rule-value", "includeStart must be Boolean.", owner),
        };
        EnsureOnly(element, owner, "from", "edges", "direction", "includeStart");
        return new ReachableExpression(start, edges, direction == "incoming", includeStart);
    }

    private static TagSuffixMatchExpression ParseTagSuffixMatch(JsonElement element, EntityId owner, int depth)
    {
        EnsureObject(element, owner);
        var left = ParseSet(Required(element, "left", owner), owner, depth + 1);
        var right = ParseSet(Required(element, "right", owner), owner, depth + 1);
        var leftPrefix = String(Required(element, "leftPrefix", owner), "leftPrefix", owner);
        var rightPrefix = String(Required(element, "rightPrefix", owner), "rightPrefix", owner);
        EnsureOnly(element, owner, "left", "right", "leftPrefix", "rightPrefix");
        return new TagSuffixMatchExpression(left, leftPrefix, right, rightPrefix);
    }

    private static IReadOnlyList<RuleSetExpression> ParseSetPair(JsonElement element, EntityId owner, int depth)
    {
        var values = ParseSetArray(element, owner, depth);
        if (values.Count != 2) throw new RuleFormatException("invalid-rule-arity", "Set comparisons require exactly two operands.", owner);
        return values;
    }

    private static IReadOnlyList<RuleSetExpression> ParseSetArray(JsonElement element, EntityId owner, int depth)
    {
        if (element.ValueKind != JsonValueKind.Array) throw new RuleFormatException("invalid-rule-type", "A set operand list must be an array.", owner);
        var result = element.EnumerateArray().Select(item => ParseSet(item, owner, depth + 1)).ToArray();
        if (result.Length == 0) throw new RuleFormatException("invalid-rule-arity", "A set operand list cannot be empty.", owner);
        return result;
    }

    private static IReadOnlyList<RuleBooleanExpression> ParseBooleanArray(JsonElement element, EntityId owner, int depth)
    {
        if (element.ValueKind != JsonValueKind.Array) throw new RuleFormatException("invalid-rule-type", "A Boolean operand list must be an array.", owner);
        var result = element.EnumerateArray().Select(item => ParseBoolean(item, owner, depth + 1)).ToArray();
        if (result.Length == 0) throw new RuleFormatException("invalid-rule-arity", "A Boolean operand list cannot be empty.", owner);
        return result;
    }

    private static JsonProperty SingleProperty(JsonElement element, EntityId owner)
    {
        EnsureObject(element, owner);
        var properties = element.EnumerateObject().ToArray();
        if (properties.Length != 1) throw new RuleFormatException("invalid-rule-shape", "Each expression must be an object with exactly one operator.", owner);
        return properties[0];
    }

    private static JsonElement Required(JsonElement element, string name, EntityId owner) =>
        element.TryGetProperty(name, out var value) ? value : throw new RuleFormatException("missing-rule-property", $"Expression property '{name}' is required.", owner);

    private static void EnsureOnly(JsonElement element, EntityId owner, params string[] names)
    {
        var allowed = names.ToHashSet(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name)) throw Unknown(property.Name, "expression", owner);
    }

    private static void EnsureObject(JsonElement element, EntityId owner)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new RuleFormatException("invalid-rule-type", "An expression must be a JSON object.", owner);
    }

    private static string String(JsonElement element, string name, EntityId owner) =>
        element.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(element.GetString())
            ? element.GetString()!
            : throw new RuleFormatException("invalid-rule-value", $"'{name}' must be a non-empty string.", owner);

    private static string[] StringArray(JsonElement element, string name, EntityId owner)
    {
        if (element.ValueKind != JsonValueKind.Array) throw new RuleFormatException("invalid-rule-type", $"'{name}' must be an array.", owner);
        return element.EnumerateArray().Select(item => String(item, name, owner)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static ComparisonOperator ParseComparison(string value, EntityId owner) => value switch
    {
        "eq" => ComparisonOperator.Equal, "ne" => ComparisonOperator.NotEqual,
        "lt" => ComparisonOperator.LessThan, "lte" => ComparisonOperator.LessThanOrEqual,
        "gt" => ComparisonOperator.GreaterThan, "gte" => ComparisonOperator.GreaterThanOrEqual,
        _ => throw new RuleFormatException("unknown-rule-operator", $"Unknown comparison '{value}'.", owner),
    };

    private static void EnsureDepth(int depth, EntityId owner)
    {
        if (depth > MaximumExpressionDepth) throw new RuleFormatException("rule-depth-limit", $"Rule expression exceeds depth {MaximumExpressionDepth}.", owner);
    }

    private static RuleFormatException Unknown(string name, string context, EntityId owner) =>
        new("unknown-rule-operator", $"Unknown {context} operator or property '{name}'.", owner);

    private static void ValidateViewReferences(IReadOnlyDictionary<string, RuleSetExpression> views, IReadOnlyDictionary<string, EntityId> owners)
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var name in views.Keys.Order(StringComparer.Ordinal)) Visit(name, new List<string>());
        void Visit(string name, List<string> path)
        {
            if (!views.ContainsKey(name)) throw new RuleFormatException("missing-rule-view", $"Referenced view '{name}' does not exist.");
            if (state.TryGetValue(name, out var current))
            {
                if (current == 2) return;
                var cycle = string.Join(" -> ", path.Concat([name]));
                throw new RuleFormatException("cyclic-rule-view", $"View references are cyclic: {cycle}.", owners[name]);
            }
            state[name] = 1;
            path.Add(name);
            foreach (var reference in References(views[name])) Visit(reference, path);
            path.RemoveAt(path.Count - 1);
            state[name] = 2;
        }
    }

    private static IEnumerable<string> References(RuleSetExpression expression) => expression switch
    {
        ViewReferenceExpression value => [value.Name],
        SetCompositionExpression value => value.Operands.SelectMany(References),
        ReachableExpression value => References(value.Start).Concat(References(value.Edges)),
        EntitySelectorExpression value => new[] { value.Filter.SourceIn, value.Filter.TargetIn }.Where(x => x is not null).SelectMany(x => References(x!)),
        _ => [],
    };
}

public sealed class RuleFormatException : Exception
{
    public RuleFormatException(string code, string message, EntityId? entityId = null, Exception? innerException = null) : base(message, innerException)
    { Code = code; EntityId = entityId; }
    public string Code { get; }
    public EntityId? EntityId { get; }
}
