using System.Text.Json;
using System.Text.Json.Serialization;
using ValidatedWorld.Application;
using ValidatedWorld.Core;
using ValidatedWorld.Serialization;
using ValidatedWorld.Validation;

namespace ValidatedWorld.Cli;

internal static class CliJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Payload<T>(JsonElement payload) => payload.Deserialize<T>(Options)
        ?? throw new JsonException("The command payload cannot be null.");

    private static JsonSerializerOptions CreateOptions()
    {
        var options = Protocol.CreateJsonOptions();
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

internal sealed record EmptyRequest;
internal sealed record PathRequest(string Path);
internal sealed record ProjectInitRequest(
    string Path,
    string ProjectId,
    string Title,
    string PurposeNodeId,
    string PurposeText);
internal sealed record ProjectBackupRequest(string SourcePath, string DestinationPath);
internal sealed record ProjectDiffRequest(
    string BasePath,
    string TargetPath,
    int Limit = QueryPageRequest.DefaultLimit,
    string? Cursor = null);
internal sealed record SampleCreateRequest(string SampleName, string Path);
internal sealed record ReadEntityRequest(string Path, string EntityId, string? ExpectedProjectId = null);
internal sealed record ReadPageRequest(
    string Path,
    int Limit = QueryPageRequest.DefaultLimit,
    string? Cursor = null,
    string? ExpectedProjectId = null);
internal sealed record ReadEntityPageRequest(
    string Path,
    string EntityId,
    int Limit = QueryPageRequest.DefaultLimit,
    string? Cursor = null,
    string? ExpectedProjectId = null);
internal sealed record SearchRequest(
    string Path,
    string Text,
    int Limit = QueryPageRequest.DefaultLimit,
    string? Cursor = null,
    string? ExpectedProjectId = null);
internal sealed record TagRequest(
    string Path,
    string Tag,
    int Limit = QueryPageRequest.DefaultLimit,
    string? Cursor = null,
    string? ExpectedProjectId = null);
internal sealed record ScopeRequest(
    string Path,
    string NodeId,
    int Limit = QueryPageRequest.DefaultLimit,
    string? Cursor = null,
    int MaxDepth = 10_000,
    int MaxVisitedNodes = 100_000,
    string? ExpectedProjectId = null);
internal sealed record PathQueryRequest(
    string Path,
    string SourceNodeId,
    string TargetNodeId,
    int MaxDepth = 10_000,
    int MaxVisitedNodes = 100_000,
    string? ExpectedProjectId = null);
internal sealed record ContextRequest(
    string Path,
    IReadOnlyList<string> NodeIds,
    int MaxDepth = 10_000,
    int MaxVisitedNodes = 100_000,
    string? ExpectedProjectId = null);

internal sealed record SessionBeginRequest(
    string Path,
    string ProjectId,
    string Author,
    string Intent,
    bool IncludeOperations = true,
    bool IncludeProposedGraph = true);
internal sealed record SessionLocatorDto(string ProjectId, string SessionId);
internal sealed record SessionReferenceDto(
    string ProjectId,
    string SessionId,
    string BaseFingerprint,
    string OperationFingerprint,
    string ProposedFingerprint,
    string AffectedFingerprint,
    string ReviewFingerprint);
internal sealed record SessionLocatorRequest(SessionLocatorDto Session);
internal sealed record SessionShowRequest(
    SessionLocatorDto Session,
    bool IncludeOperations = true,
    bool IncludeProposedGraph = true);
internal sealed record SessionReferenceRequest(SessionReferenceDto Reference);
internal sealed record SessionValidateRequest(
    SessionReferenceDto Reference,
    bool IncludeOperations = true,
    bool IncludeProposedGraph = true);
internal sealed record ChangeWriteRequest(SessionReferenceDto Reference, bool BypassAiReview = false);
internal sealed record ScopeParentDto(string ChildId, string ParentId, string EdgeId);
internal sealed record FocusRequest(
    SessionReferenceDto Reference,
    OperationBatchDto Operations,
    IReadOnlyList<ScopeParentDto> ScopeParents);
internal sealed record ChangeOperationsRequest(
    SessionReferenceDto Reference,
    OperationBatchDto Operations,
    int MaxTraversalDepth = 100_000,
    int MaxAffectedNodes = 1_000_000,
    int MaxOutputItems = 1_000_000,
    bool IncludeOperations = true,
    bool IncludeProposedGraph = true);
internal sealed record ExpandRequest(
    SessionReferenceDto Reference,
    int MaxTraversalDepth = 100_000,
    int MaxAffectedNodes = 1_000_000,
    int MaxOutputItems = 1_000_000,
    bool IncludeOperations = true,
    bool IncludeProposedGraph = true);
internal sealed record ReviewDispositionDto(string NodeId, ReviewDispositionKind Kind, string? Rationale = null);
internal sealed record ReviewRequest(
    SessionReferenceDto Reference,
    IReadOnlyList<ReviewDispositionDto> Dispositions,
    IReadOnlyList<string> PresentedContextNodeIds,
    bool IncludeOperations = true,
    bool IncludeProposedGraph = true);

internal sealed record ErrorDto(string Code, string Message);
internal sealed record StoredProjectDto(
    string Path,
    string ProjectId,
    string Title,
    string PurposeNodeId,
    int NodeCount,
    int EdgeCount,
    string StateFingerprint,
    string CreatedUtc,
    string UpdatedUtc);
internal sealed record LoadedProjectDto(StoredProjectDto Project, GraphDto Graph);
internal sealed record ProjectStatusDto(
    string Path,
    string ProjectId,
    string Title,
    string PurposeNodeId,
    int NodeCount,
    int EdgeCount,
    string StateFingerprint,
    int SchemaVersion,
    string SqliteVersion);
internal sealed record ProjectVerificationDto(
    string Path,
    bool IsValid,
    string StateFingerprint,
    int NodeCount,
    int EdgeCount,
    IReadOnlyList<string> Checks);
internal sealed record SqlExportDto(string Path, string StateFingerprint, string Sql);
internal sealed record ProjectMetadataChangeDto(string Field, string OldValue, string NewValue);
internal sealed record ProjectDiffSummaryDto(
    int MetadataChanges,
    int NodesAdded,
    int NodesReplaced,
    int NodesRemoved,
    int EdgesAdded,
    int EdgesReplaced,
    int EdgesRemoved,
    int EntityChanges,
    int TotalChanges);
internal sealed record ProjectDiffEntryDto(
    GraphOperationKind Kind,
    GraphEntityKind EntityKind,
    string EntityId,
    NodeDto? OldNode,
    NodeDto? NewNode,
    EdgeDto? OldEdge,
    EdgeDto? NewEdge,
    IReadOnlyList<string> ChangedFields);
internal sealed record ProjectDiffDto(
    string BasePath,
    string TargetPath,
    string ProjectId,
    string BaseFingerprint,
    string TargetFingerprint,
    IReadOnlyList<ProjectMetadataChangeDto> MetadataChanges,
    ProjectDiffSummaryDto Summary,
    IReadOnlyList<ProjectDiffEntryDto> Items,
    int TotalCount,
    string? NextCursor,
    QueryOmissionDto? Omission);
internal sealed record PageDto<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    string? NextCursor,
    QueryOmissionDto? Omission);
internal sealed record QueryOmissionDto(
    QueryOmissionReason Reason,
    int? RemainingCount,
    string Message);
internal sealed record SearchHitDto(GraphEntityKind EntityKind, string EntityId, NodeDto? Node, EdgeDto? Edge);
internal sealed record NeighborDto(string NodeId, EdgeDto Edge, bool IsOutgoing);
internal sealed record DependencyDto(string EdgeId, string From, string To, bool IsOutgoing);
internal sealed record ScopeResultDto(
    NodeDto Node,
    IReadOnlyList<NodeDto> Upstream,
    PageDto<NodeDto> Descendants,
    IReadOnlyList<QueryOmissionDto> Omissions);
internal sealed record DependencyPathDto(
    bool Found,
    IReadOnlyList<string> Nodes,
    IReadOnlyList<string> Edges,
    IReadOnlyList<QueryOmissionDto> Omissions);
internal sealed record ScopeContextDto(
    IReadOnlyList<string> RequestedNodeIds,
    IReadOnlyList<NodeDto> ContextNodes,
    IReadOnlyList<QueryOmissionDto> Omissions);
internal sealed record ValidationResultDto(ValidationStatus Status, IReadOnlyList<DiagnosticDto> Diagnostics);
internal sealed record AffectedPathDto(IReadOnlyList<string> Nodes, IReadOnlyList<string> Edges);
internal sealed record AffectedNodeDto(
    string NodeId,
    bool IsDirectChange,
    int Distance,
    AffectedPathDto Explanation,
    NodeDto? CurrentNode,
    NodeDto? ProposedNode);
internal sealed record AffectedEdgeChangeDto(
    OperationDto Operation,
    EdgeDto? CurrentEdge,
    EdgeDto? ProposedEdge);
internal sealed record ScopeLineageDto(
    string AffectedNodeId,
    IReadOnlyList<string> CurrentPath,
    IReadOnlyList<string> ProposedPath);
internal sealed record ScopeContextEntryDto(
    string NodeId,
    IReadOnlyList<ScopeLineageDto> Lineages,
    NodeDto? CurrentNode,
    NodeDto? ProposedNode);
internal sealed record AffectedOmissionDto(
    AffectedOmissionReason Reason,
    string? SourceNodeId,
    string? TargetNodeId,
    string? EdgeId,
    int? Depth,
    string Message);
internal sealed record AffectedDto(
    AffectedAnalysisStatus Status,
    ValidationResultDto CurrentValidation,
    ValidationResultDto ProposedValidation,
    IReadOnlyList<string> DirectNodeIds,
    IReadOnlyList<string> SeedNodeIds,
    IReadOnlyList<AffectedNodeDto> AffectedNodes,
    IReadOnlyList<AffectedEdgeChangeDto> EdgeChanges,
    IReadOnlyList<ScopeContextEntryDto> ScopeContext,
    IReadOnlyList<AffectedOmissionDto> Omissions);
internal sealed record DispositionDto(string NodeId, ReviewDispositionKind Kind, string? Rationale);
internal sealed record ReadinessDto(
    bool IsReady,
    AffectedAnalysisStatus AnalysisStatus,
    ValidationStatus ProposedValidationStatus,
    IReadOnlyList<string> PendingNodeIds,
    IReadOnlyList<string> MissingContextNodeIds,
    IReadOnlyList<string> Blockers);
internal sealed record RefreshDto(
    IReadOnlyList<string> InvalidatedDispositionNodeIds,
    IReadOnlyList<string> InvalidatedContextNodeIds);
internal sealed record SessionSnapshotDto(
    string Path,
    string Author,
    string Intent,
    string CreatedUtc,
    string UpdatedUtc,
    SessionReferenceDto Reference,
    int OperationCount,
    int ProposedNodeCount,
    int ProposedEdgeCount,
    OperationBatchDto? Operations,
    GraphDto? ProposedGraph,
    AffectedDto Affected,
    IReadOnlyList<DispositionDto> Dispositions,
    IReadOnlyList<string> PresentedContextNodeIds,
    ReadinessDto Readiness,
    RefreshDto? Refresh,
    SemanticReviewResultDto? SemanticReview);
internal sealed record FocusResultDto(
    OperationBatchDto ExpandedOperations,
    string OperationFingerprint,
    string ProposedFingerprint);
internal sealed record WriteResultDto(
    ChangeWriteStatus Status,
    string ProjectId,
    string SessionId,
    StoredProjectDto? Project,
    ProjectStorageErrorCode? StorageErrorCode,
    string Message,
    SemanticReviewResultDto? SemanticReview,
    bool AiReviewBypassed);
internal sealed record DiscardResultDto(string ProjectId, string SessionId, string DiscardedUtc);
internal sealed record ExitWarningDto(
    string ProjectId,
    string SessionId,
    string Path,
    int OperationCount,
    int PendingReviewCount,
    string Message);
internal sealed record AiReviewAvailabilityDto(
    bool Enabled,
    bool Configured,
    string Provider,
    string Model,
    int TimeoutSeconds,
    bool LiveTests,
    string Message);
internal sealed record SemanticReviewUsageDto(int InputTokens, int OutputTokens, int TotalTokens);
internal sealed record SemanticReviewConcernResultDto(
    string Code,
    string Message,
    IReadOnlyList<string> Citations);
internal sealed record SemanticReviewResultDto(
    SemanticReviewStatus Status,
    SemanticReviewDecision? Decision,
    string Provider,
    string Model,
    string RequestFingerprint,
    SemanticReviewBindingDto Binding,
    string Summary,
    IReadOnlyList<SemanticReviewConcernResultDto> Concerns,
    SemanticReviewUsageDto? Usage,
    string? ResponseId,
    double DurationMilliseconds,
    string CompletedUtc,
    bool IsCurrent,
    string? FailureCode);

internal static class CliDto
{
    public static StoredProjectDto Stored(StoredProject value) => new(
        value.Path,
        value.Graph.ProjectId.Value,
        value.Graph.Title,
        value.Graph.PurposeNodeId.Value,
        value.Graph.Nodes.Count,
        value.Graph.Edges.Count,
        value.StateFingerprint,
        Utc(value.CreatedUtc),
        Utc(value.UpdatedUtc));

    public static ProjectStatusDto Status(ProjectStatus value) => new(
        value.Path, value.ProjectId.Value, value.Title, value.PurposeNodeId.Value,
        value.NodeCount, value.EdgeCount, value.StateFingerprint, value.SchemaVersion, value.SqliteVersion);

    public static ProjectVerificationDto Verification(ProjectVerification value) => new(
        value.Path, value.IsValid, value.StateFingerprint, value.NodeCount, value.EdgeCount, value.Checks);

    public static ProjectDiffDto Diff(ProjectDiffResult value) => new(
        value.BasePath,
        value.TargetPath,
        value.ProjectId.Value,
        value.BaseFingerprint,
        value.TargetFingerprint,
        value.MetadataChanges.Select(change => new ProjectMetadataChangeDto(
            change.Field, change.OldValue, change.NewValue)).ToArray(),
        new ProjectDiffSummaryDto(
            value.Summary.MetadataChanges,
            value.Summary.NodesAdded,
            value.Summary.NodesReplaced,
            value.Summary.NodesRemoved,
            value.Summary.EdgesAdded,
            value.Summary.EdgesReplaced,
            value.Summary.EdgesRemoved,
            value.Summary.EntityChanges,
            value.Summary.TotalChanges),
        value.Changes.Items.Select(change => new ProjectDiffEntryDto(
            change.Kind,
            change.EntityKind,
            change.EntityId.Value,
            change.OldNode is null ? null : GraphProtocol.ToDto(change.OldNode),
            change.NewNode is null ? null : GraphProtocol.ToDto(change.NewNode),
            change.OldEdge is null ? null : GraphProtocol.ToDto(change.OldEdge),
            change.NewEdge is null ? null : GraphProtocol.ToDto(change.NewEdge),
            change.ChangedFields)).ToArray(),
        value.Changes.TotalCount,
        value.Changes.NextCursor,
        value.Changes.Omission is null ? null : Omission(value.Changes.Omission));

    public static PageDto<NodeDto> Nodes(QueryPage<GraphNode> page) => new(
        page.Items.Select(GraphProtocol.ToDto).ToArray(), page.TotalCount, page.NextCursor,
        page.Omission is null ? null : Omission(page.Omission));

    public static PageDto<EdgeDto> Edges(QueryPage<GraphEdge> page) => new(
        page.Items.Select(GraphProtocol.ToDto).ToArray(), page.TotalCount, page.NextCursor,
        page.Omission is null ? null : Omission(page.Omission));

    public static PageDto<SearchHitDto> Search(QueryPage<GraphSearchHit> page) => new(
        page.Items.Select(hit => new SearchHitDto(
            hit.EntityKind, hit.EntityId.Value,
            hit.Node is null ? null : GraphProtocol.ToDto(hit.Node),
            hit.Edge is null ? null : GraphProtocol.ToDto(hit.Edge))).ToArray(),
        page.TotalCount, page.NextCursor, page.Omission is null ? null : Omission(page.Omission));

    public static PageDto<NeighborDto> Neighbors(QueryPage<NeighborEntry> page) => new(
        page.Items.Select(entry => new NeighborDto(
            entry.NodeId.Value, GraphProtocol.ToDto(entry.Edge), entry.IsOutgoing)).ToArray(),
        page.TotalCount, page.NextCursor, page.Omission is null ? null : Omission(page.Omission));

    public static PageDto<DependencyDto> Dependencies(QueryPage<DependencyEntry> page) => new(
        page.Items.Select(entry => new DependencyDto(
            entry.Arc.EdgeId.Value, entry.Arc.From.Value, entry.Arc.To.Value, entry.IsOutgoing)).ToArray(),
        page.TotalCount, page.NextCursor, page.Omission is null ? null : Omission(page.Omission));

    public static ScopeResultDto Scope(ScopeQueryResult value) => new(
        GraphProtocol.ToDto(value.Node),
        value.Upstream.Select(GraphProtocol.ToDto).ToArray(),
        Nodes(value.Descendants),
        value.Omissions.Select(Omission).ToArray());

    public static DependencyPathDto Path(DependencyPathResult value) => new(
        value.Found,
        value.Nodes.Select(id => id.Value).ToArray(),
        value.Edges.Select(id => id.Value).ToArray(),
        value.Omissions.Select(Omission).ToArray());

    public static ScopeContextDto Context(ScopeContextResult value) => new(
        value.RequestedNodeIds.Select(id => id.Value).ToArray(),
        value.ContextNodes.Select(GraphProtocol.ToDto).ToArray(),
        value.Omissions.Select(Omission).ToArray());

    public static SessionReferenceDto Reference(ChangeSessionReference value) => new(
        value.ProjectId.Value, value.SessionId, value.BaseFingerprint, value.OperationFingerprint,
        value.ProposedFingerprint, value.AffectedFingerprint, value.ReviewFingerprint);

    public static SessionSnapshotDto Snapshot(
        ChangeSessionSnapshot value,
        bool includeOperations = true,
        bool includeProposedGraph = true) => new(
        value.Path, value.Author, value.Intent, Utc(value.CreatedUtc), Utc(value.UpdatedUtc),
        Reference(value.Reference), value.Operations.Operations.Count,
        value.ProposedGraph.Nodes.Count, value.ProposedGraph.Edges.Count,
        includeOperations ? GraphProtocol.ToDto(value.Operations) : null,
        includeProposedGraph ? GraphProtocol.ToDto(value.ProposedGraph) : null,
        Affected(value.Affected),
        value.Dispositions.Select(disposition => new DispositionDto(
            disposition.NodeId.Value, disposition.Kind, disposition.Rationale)).ToArray(),
        value.PresentedContextNodeIds.Select(id => id.Value).ToArray(),
        Readiness(value.Readiness),
        value.Refresh is null ? null : new RefreshDto(
            value.Refresh.InvalidatedDispositionNodeIds.Select(id => id.Value).ToArray(),
            value.Refresh.InvalidatedContextNodeIds.Select(id => id.Value).ToArray()),
        value.SemanticReview is null ? null : SemanticReview(value.SemanticReview));

    public static AffectedDto Affected(AffectedAnalysis value) => new(
        value.Status,
        Validation(value.CurrentValidation),
        Validation(value.ProposedValidation),
        value.DirectNodeIds.Select(id => id.Value).ToArray(),
        value.SeedNodeIds.Select(id => id.Value).ToArray(),
        value.AffectedNodes.Select(node => new AffectedNodeDto(
            node.NodeId.Value, node.IsDirectChange, node.Distance,
            new AffectedPathDto(
                node.Explanation.Nodes.Select(id => id.Value).ToArray(),
                node.Explanation.Edges.Select(id => id.Value).ToArray()),
            node.CurrentNode is null ? null : GraphProtocol.ToDto(node.CurrentNode),
            node.ProposedNode is null ? null : GraphProtocol.ToDto(node.ProposedNode))).ToArray(),
        value.EdgeChanges.Select(change => new AffectedEdgeChangeDto(
            GraphProtocol.ToDto(change.Operation),
            change.CurrentEdge is null ? null : GraphProtocol.ToDto(change.CurrentEdge),
            change.ProposedEdge is null ? null : GraphProtocol.ToDto(change.ProposedEdge))).ToArray(),
        value.ScopeContext.Select(entry => new ScopeContextEntryDto(
            entry.NodeId.Value,
            entry.Lineages.Select(lineage => new ScopeLineageDto(
                lineage.AffectedNodeId.Value,
                lineage.CurrentPath.Select(id => id.Value).ToArray(),
                lineage.ProposedPath.Select(id => id.Value).ToArray())).ToArray(),
            entry.CurrentNode is null ? null : GraphProtocol.ToDto(entry.CurrentNode),
            entry.ProposedNode is null ? null : GraphProtocol.ToDto(entry.ProposedNode))).ToArray(),
        value.Omissions.Select(omission => new AffectedOmissionDto(
            omission.Reason,
            omission.SourceNodeId?.Value,
            omission.TargetNodeId?.Value,
            omission.EdgeId?.Value,
            omission.Depth,
            omission.Message)).ToArray());

    public static ReadinessDto Readiness(ReviewReadinessResult value) => new(
        value.IsReady, value.AnalysisStatus, value.ProposedValidationStatus,
        value.PendingNodeIds.Select(id => id.Value).ToArray(),
        value.MissingContextNodeIds.Select(id => id.Value).ToArray(), value.Blockers);

    public static WriteResultDto Write(ChangeWriteResult value) => new(
        value.Status, value.ProjectId.Value, value.SessionId,
        value.Project is null ? null : Stored(value.Project),
        value.StorageErrorCode, value.Message,
        value.SemanticReview is null ? null : SemanticReview(value.SemanticReview),
        value.AiReviewBypassed);

    public static ExitWarningDto Warning(ChangeExitWarning value) => new(
        value.ProjectId.Value, value.SessionId, value.Path,
        value.OperationCount, value.PendingReviewCount, value.Message);

    public static AiReviewAvailabilityDto Availability(SemanticReviewAvailability value) => new(
        value.Enabled, value.Configured, value.Provider, value.Model,
        value.TimeoutSeconds, value.LiveTests, value.Message);

    public static SemanticReviewResultDto SemanticReview(SemanticReviewResult value) => new(
        value.Status,
        value.Decision,
        value.Provider,
        value.Model,
        value.RequestFingerprint,
        value.Binding,
        value.Summary,
        value.Concerns.Select(concern => new SemanticReviewConcernResultDto(
            concern.Code,
            concern.Message,
            concern.Citations.Select(id => id.Value).ToArray())).ToArray(),
        value.Usage is null ? null : new SemanticReviewUsageDto(
            value.Usage.InputTokens, value.Usage.OutputTokens, value.Usage.TotalTokens),
        value.ResponseId,
        value.Duration.TotalMilliseconds,
        Utc(value.CompletedUtc),
        value.IsCurrent,
        value.FailureCode);

    public static ChangeSessionLocator Locator(SessionLocatorDto value) =>
        new(new ProjectId(value.ProjectId), Required(value.SessionId, nameof(value.SessionId)));

    public static ChangeSessionReference Reference(SessionReferenceDto value) => new(
        new ProjectId(value.ProjectId),
        Required(value.SessionId, nameof(value.SessionId)),
        Required(value.BaseFingerprint, nameof(value.BaseFingerprint)),
        Required(value.OperationFingerprint, nameof(value.OperationFingerprint)),
        Required(value.ProposedFingerprint, nameof(value.ProposedFingerprint)),
        Required(value.AffectedFingerprint, nameof(value.AffectedFingerprint)),
        Required(value.ReviewFingerprint, nameof(value.ReviewFingerprint)));

    public static QueryPageRequest Page(int limit, string? cursor) => new(limit, cursor);

    public static QueryTraversalOptions Traversal(
        int maxDepth,
        int maxVisitedNodes,
        CancellationToken cancellationToken) => new()
        {
            MaxDepth = maxDepth,
            MaxVisitedNodes = maxVisitedNodes,
            CancellationToken = cancellationToken,
        };

    public static AffectedAnalysisOptions AffectedOptions(
        int maxTraversalDepth,
        int maxAffectedNodes,
        int maxOutputItems,
        CancellationToken cancellationToken) => new()
        {
            MaxTraversalDepth = maxTraversalDepth,
            MaxAffectedNodes = maxAffectedNodes,
            MaxOutputItems = maxOutputItems,
            CancellationToken = cancellationToken,
        };

    private static QueryOmissionDto Omission(QueryOmission value) =>
        new(value.Reason, value.RemainingCount, value.Message);

    private static ValidationResultDto Validation(GraphValidationResult value) => new(
        value.Status,
        ValidationProtocol.ToDto(value).Diagnostics);

    private static string Utc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private static string Required(string? value, string name) =>
        !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException($"'{name}' is required.", name);
}
