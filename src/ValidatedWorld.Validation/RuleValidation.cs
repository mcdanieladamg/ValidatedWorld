using System.Collections.ObjectModel;
using ValidatedWorld.Core;

namespace ValidatedWorld.Validation;

public sealed class RuleValidationOptions
{
    public int MaxEvaluationWork { get; init; } = 1_000_000;
    public int MaxOffendingEntityIds { get; init; } = 20;
    public CancellationToken CancellationToken { get; init; }
}

public sealed record RuleDiagnostic(
    string Code,
    string Message,
    EntityId? RuleId,
    IReadOnlyList<EntityId> OffendingEntityIds,
    int TotalOffendingCount,
    int OmittedOffendingCount);

public sealed class RuleValidationResult
{
    public RuleValidationResult(ValidationStatus status, IEnumerable<RuleDiagnostic> diagnostics)
    { Status = status; Diagnostics = new ReadOnlyCollection<RuleDiagnostic>(diagnostics.ToArray()); }
    public ValidationStatus Status { get; }
    public IReadOnlyList<RuleDiagnostic> Diagnostics { get; }
    public bool IsValid => Status == ValidationStatus.Valid;
}

public sealed class GraphRuleValidator
{
    public RuleValidationResult Validate(ProjectGraph graph, RuleDocument document, RuleValidationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        options ??= new RuleValidationOptions();
        if (options.MaxEvaluationWork <= 0 || options.MaxOffendingEntityIds <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Rule evaluation limits must be positive.");

        var context = new EvaluationContext(graph, document.Views, options);
        var diagnostics = new List<RuleDiagnostic>();
        try
        {
            foreach (var rule in document.Rules)
            {
                context.ThrowIfStopped();
                var outcome = context.Boolean(rule.Expression);
                if (!outcome.Value)
                {
                    var ids = outcome.Offenders.OrderBy(id => id).ToArray();
                    diagnostics.Add(new RuleDiagnostic(
                        "rule-violation", rule.Message, rule.Id,
                        ids.Take(options.MaxOffendingEntityIds).ToArray(), ids.Length,
                        Math.Max(0, ids.Length - options.MaxOffendingEntityIds)));
                }
            }
        }
        catch (OperationCanceledException)
        {
            diagnostics.Add(new RuleDiagnostic("rule-evaluation-cancelled", "Rule evaluation was cancelled; rules did not pass.", null, [], 0, 0));
            return new RuleValidationResult(ValidationStatus.Inconclusive, diagnostics);
        }
        catch (RuleEvaluationException exception)
        {
            diagnostics.Add(new RuleDiagnostic(exception.Code, exception.Message, exception.RuleId, [], 0, 0));
            return new RuleValidationResult(ValidationStatus.Inconclusive, diagnostics);
        }

        return new RuleValidationResult(diagnostics.Count == 0 ? ValidationStatus.Valid : ValidationStatus.Invalid, diagnostics);
    }

    private sealed class EvaluationContext(
        ProjectGraph graph,
        IReadOnlyDictionary<string, RuleSetExpression> views,
        RuleValidationOptions options)
    {
        private readonly Dictionary<string, HashSet<EntityId>> _viewCache = new(StringComparer.Ordinal);
        private int _work;

        public void ThrowIfStopped()
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            if (++_work > options.MaxEvaluationWork)
                throw new RuleEvaluationException("rule-work-limit", $"Rule evaluation exceeded the work limit of {options.MaxEvaluationWork}.");
        }

        public BooleanOutcome Boolean(RuleBooleanExpression expression)
        {
            ThrowIfStopped();
            return expression switch
            {
                BooleanCompositionExpression value => Compose(value),
                NotExpression value => Negate(Boolean(value.Operand)),
                CountExpression value => Count(value),
                SetComparisonExpression value => SetComparison(value),
                AllExpression value => All(value),
                TopologyExpression value => Topology(value),
                TagSuffixMatchExpression value => TagSuffixMatch(value),
                _ => throw new RuleEvaluationException("unknown-rule-expression", "The parsed Boolean expression is unsupported."),
            };
        }

        private BooleanOutcome Compose(BooleanCompositionExpression expression)
        {
            var outcomes = expression.Operands.Select(Boolean).ToArray();
            var value = expression.Operator == BooleanOperator.And
                ? outcomes.All(item => item.Value)
                : outcomes.Any(item => item.Value);
            var relevant = expression.Operator == BooleanOperator.And
                ? outcomes.Where(item => !item.Value)
                : value ? [] : outcomes;
            return new BooleanOutcome(value, relevant.SelectMany(item => item.Offenders).ToHashSet());
        }

        private static BooleanOutcome Negate(BooleanOutcome value) =>
            new(!value.Value, value.Value ? value.Offenders : []);

        private BooleanOutcome Count(CountExpression expression)
        {
            var set = Set(expression.Set);
            var passes = Compare(set.Count, expression.Comparison, expression.Value);
            return new BooleanOutcome(passes, passes ? [] : set);
        }

        private BooleanOutcome SetComparison(SetComparisonExpression expression)
        {
            var left = Set(expression.Operands[0]);
            var right = Set(expression.Operands[1]);
            var offenders = expression.Operator == SetComparisonOperator.Subset
                ? left.Except(right).ToHashSet()
                : left.SymmetricExcept(right);
            return new BooleanOutcome(offenders.Count == 0, offenders);
        }

        private BooleanOutcome All(AllExpression expression)
        {
            var offenders = Set(expression.Set).Where(id => !Condition(id, expression.Condition)).ToHashSet();
            return new BooleanOutcome(offenders.Count == 0, offenders);
        }

        private bool Condition(EntityId id, EntityCondition condition)
        {
            ThrowIfStopped();
            var tags = graph.Nodes.FirstOrDefault(node => node.Id == id)?.Tags ??
                graph.Edges.FirstOrDefault(edge => edge.Id == id)?.Tags ?? [];
            return condition switch
            {
                HasTagCondition value => tags.Contains(value.Tag, StringComparer.Ordinal),
                TagCountCondition value => Compare(tags.Count(tag => tag.StartsWith(value.Prefix, StringComparison.Ordinal)), value.Comparison, value.Value),
                _ => throw new RuleEvaluationException("unknown-rule-condition", "The parsed entity condition is unsupported."),
            };
        }

        private BooleanOutcome Topology(TopologyExpression expression)
        {
            var nodeIds = Set(expression.Nodes);
            var edgeIds = Set(expression.Edges);
            var selectedEdges = graph.Edges.Where(edge => edgeIds.Contains(edge.Id) && nodeIds.Contains(edge.Source) && nodeIds.Contains(edge.Target)).ToArray();
            var indegree = nodeIds.ToDictionary(id => id, _ => 0);
            var outgoing = nodeIds.ToDictionary(id => id, _ => new List<EntityId>());
            foreach (var edge in selectedEdges)
            {
                ThrowIfStopped();
                indegree[edge.Target]++;
                outgoing[edge.Source].Add(edge.Target);
            }
            var queue = new Queue<EntityId>(indegree.Where(pair => pair.Value == 0).Select(pair => pair.Key).OrderBy(id => id));
            var visited = new HashSet<EntityId>();
            while (queue.Count > 0)
            {
                var id = queue.Dequeue();
                if (!visited.Add(id)) continue;
                foreach (var target in outgoing[id].OrderBy(value => value)) if (--indegree[target] == 0) queue.Enqueue(target);
            }
            var offenders = nodeIds.Except(visited).ToHashSet();
            if (expression.RequireSingleChain)
            {
                foreach (var id in nodeIds)
                {
                    var incomingCount = selectedEdges.Count(edge => edge.Target == id);
                    var outgoingCount = selectedEdges.Count(edge => edge.Source == id);
                    if (incomingCount > 1 || outgoingCount > 1) offenders.Add(id);
                }
                if (nodeIds.Count > 0 && (selectedEdges.Length != nodeIds.Count - 1 || visited.Count != nodeIds.Count))
                    offenders.UnionWith(nodeIds);
            }
            return new BooleanOutcome(offenders.Count == 0, offenders);
        }

        private HashSet<EntityId> Set(RuleSetExpression expression)
        {
            ThrowIfStopped();
            return expression switch
            {
                ViewReferenceExpression value => View(value.Name),
                EntitySelectorExpression value => Select(value),
                SetCompositionExpression value => ComposeSet(value),
                ReachableExpression value => Reachable(value),
                _ => throw new RuleEvaluationException("unknown-rule-expression", "The parsed set expression is unsupported."),
            };
        }

        private HashSet<EntityId> View(string name)
        {
            if (_viewCache.TryGetValue(name, out var cached)) return new HashSet<EntityId>(cached);
            if (!views.TryGetValue(name, out var expression)) throw new RuleEvaluationException("missing-rule-view", $"Referenced view '{name}' does not exist.");
            var result = Set(expression);
            _viewCache.Add(name, result);
            return new HashSet<EntityId>(result);
        }

        private HashSet<EntityId> Select(EntitySelectorExpression selector)
        {
            var sourceIds = selector.Filter.SourceIn is null ? null : Set(selector.Filter.SourceIn);
            var targetIds = selector.Filter.TargetIn is null ? null : Set(selector.Filter.TargetIn);
            if (selector.EntityKind == GraphEntityKind.Node)
                return graph.Nodes.Where(node => Match(node.Id, node.Kind, null, node.Tags, node.Attributes, selector.Filter, sourceIds, targetIds, null, null)).Select(node => node.Id).ToHashSet();
            return graph.Edges.Where(edge => Match(edge.Id, null, edge.Relationship, edge.Tags, edge.Attributes, selector.Filter, sourceIds, targetIds, edge.Source, edge.Target)).Select(edge => edge.Id).ToHashSet();
        }

        private bool Match(EntityId id, string? kind, string? relationship, IReadOnlyList<string> tags,
            IReadOnlyList<GraphAttribute> attributes, EntityFilter filter,
            HashSet<EntityId>? sourceIds, HashSet<EntityId>? targetIds, EntityId? source, EntityId? target)
        {
            ThrowIfStopped();
            return (filter.Id is null || id.Value == filter.Id) &&
                (filter.Kind is null || kind == filter.Kind) &&
                (filter.Relationship is null || relationship == filter.Relationship) &&
                (filter.TagPrefix is null || tags.Any(tag => tag.StartsWith(filter.TagPrefix, StringComparison.Ordinal))) &&
                filter.TagsAll.All(tag => tags.Contains(tag, StringComparer.Ordinal)) &&
                filter.Attributes.All(match => attributes.Any(attribute =>
                    attribute.Name == match.Name && attribute.Value == match.Value)) &&
                (sourceIds is null || source is { } sourceId && sourceIds.Contains(sourceId)) &&
                (targetIds is null || target is { } targetId && targetIds.Contains(targetId));
        }

        private HashSet<EntityId> ComposeSet(SetCompositionExpression expression)
        {
            var result = Set(expression.Operands[0]);
            foreach (var operand in expression.Operands.Skip(1))
            {
                var next = Set(operand);
                if (expression.Operator == SetOperator.Union) result.UnionWith(next);
                else if (expression.Operator == SetOperator.Intersect) result.IntersectWith(next);
                else result.ExceptWith(next);
            }
            return result;
        }

        private HashSet<EntityId> Reachable(ReachableExpression expression)
        {
            var starts = Set(expression.Start);
            var edgeIds = Set(expression.Edges);
            var adjacency = new Dictionary<EntityId, List<EntityId>>();
            foreach (var edge in graph.Edges.Where(edge => edgeIds.Contains(edge.Id)))
            {
                var from = expression.Reverse ? edge.Target : edge.Source;
                var to = expression.Reverse ? edge.Source : edge.Target;
                if (!adjacency.TryGetValue(from, out var targets)) adjacency[from] = targets = [];
                targets.Add(to);
            }
            var result = expression.IncludeStart ? new HashSet<EntityId>(starts) : [];
            var seen = new HashSet<EntityId>(starts);
            var queue = new Queue<EntityId>(starts.OrderBy(id => id));
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!adjacency.TryGetValue(current, out var targets)) continue;
                foreach (var target in targets.OrderBy(id => id))
                {
                    ThrowIfStopped();
                    if (seen.Add(target)) queue.Enqueue(target);
                    result.Add(target);
                }
            }
            return result;
        }

        private BooleanOutcome TagSuffixMatch(TagSuffixMatchExpression expression)
        {
            var left = Set(expression.Left);
            var right = Set(expression.Right);
            var offenders = left.Concat(right).ToHashSet();
            if (left.Count != 1 || right.Count != 1) return new BooleanOutcome(false, offenders);
            var leftTags = Tags(left.Single()).Where(tag => tag.StartsWith(expression.LeftPrefix, StringComparison.Ordinal))
                .Select(tag => tag[expression.LeftPrefix.Length..]).ToArray();
            var rightTags = Tags(right.Single()).Where(tag => tag.StartsWith(expression.RightPrefix, StringComparison.Ordinal))
                .Select(tag => tag[expression.RightPrefix.Length..]).ToArray();
            var passes = leftTags.Length == 1 && rightTags.Length == 1 && leftTags[0] == rightTags[0];
            return new BooleanOutcome(passes, passes ? [] : offenders);
        }

        private IReadOnlyList<string> Tags(EntityId id) =>
            graph.Nodes.FirstOrDefault(node => node.Id == id)?.Tags ??
            graph.Edges.FirstOrDefault(edge => edge.Id == id)?.Tags ?? [];

        private static bool Compare(int left, ComparisonOperator op, int right) => op switch
        {
            ComparisonOperator.Equal => left == right, ComparisonOperator.NotEqual => left != right,
            ComparisonOperator.LessThan => left < right, ComparisonOperator.LessThanOrEqual => left <= right,
            ComparisonOperator.GreaterThan => left > right, ComparisonOperator.GreaterThanOrEqual => left >= right,
            _ => false,
        };
    }

    private sealed record BooleanOutcome(bool Value, HashSet<EntityId> Offenders);
    private sealed class RuleEvaluationException(string code, string message, EntityId? ruleId = null) : Exception(message)
    { public string Code { get; } = code; public EntityId? RuleId { get; } = ruleId; }
}

internal static class SetExtensions
{
    public static HashSet<T> SymmetricExcept<T>(this IEnumerable<T> left, IEnumerable<T> right)
    {
        var result = left.ToHashSet();
        result.SymmetricExceptWith(right);
        return result;
    }
}
