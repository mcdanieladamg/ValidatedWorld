namespace ValidatedWorld.Core;

public sealed record RuleDocument(IReadOnlyDictionary<string, RuleSetExpression> Views, IReadOnlyList<GraphRule> Rules);
public sealed record GraphRule(EntityId Id, string Message, RuleBooleanExpression Expression);
public abstract record RuleSetExpression;
public sealed record EntitySelectorExpression(GraphEntityKind EntityKind, EntityFilter Filter) : RuleSetExpression;
public sealed record ViewReferenceExpression(string Name) : RuleSetExpression;
public sealed record SetCompositionExpression(SetOperator Operator, IReadOnlyList<RuleSetExpression> Operands) : RuleSetExpression;
public sealed record ReachableExpression(RuleSetExpression Start, RuleSetExpression Edges, bool Reverse, bool IncludeStart) : RuleSetExpression;
public sealed record EntityFilter(string? Id, string? Kind, string? Relationship, string? TagPrefix,
    IReadOnlyList<string> TagsAll, IReadOnlyList<AttributeMatch> Attributes,
    RuleSetExpression? SourceIn, RuleSetExpression? TargetIn);
public sealed record AttributeMatch(string Name, GraphValue Value);
public enum SetOperator { Union, Intersect, Except }

public abstract record RuleBooleanExpression;
public sealed record BooleanCompositionExpression(BooleanOperator Operator, IReadOnlyList<RuleBooleanExpression> Operands) : RuleBooleanExpression;
public sealed record NotExpression(RuleBooleanExpression Operand) : RuleBooleanExpression;
public sealed record CountExpression(RuleSetExpression Set, ComparisonOperator Comparison, int Value) : RuleBooleanExpression;
public sealed record SetComparisonExpression(SetComparisonOperator Operator, IReadOnlyList<RuleSetExpression> Operands) : RuleBooleanExpression;
public sealed record AllExpression(RuleSetExpression Set, EntityCondition Condition) : RuleBooleanExpression;
public sealed record TopologyExpression(RuleSetExpression Nodes, RuleSetExpression Edges, bool RequireSingleChain) : RuleBooleanExpression;
public sealed record TagSuffixMatchExpression(RuleSetExpression Left, string LeftPrefix, RuleSetExpression Right, string RightPrefix) : RuleBooleanExpression;
public enum BooleanOperator { And, Or }
public enum ComparisonOperator { Equal, NotEqual, LessThan, LessThanOrEqual, GreaterThan, GreaterThanOrEqual }
public enum SetComparisonOperator { Subset, Equal }
public abstract record EntityCondition;
public sealed record HasTagCondition(string Tag) : EntityCondition;
public sealed record TagCountCondition(string Prefix, ComparisonOperator Comparison, int Value) : EntityCondition;
