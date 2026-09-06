using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ValidatedWorld.Application;
using ValidatedWorld.Core;
using ValidatedWorld.Serialization;
using ValidatedWorld.Validation;

namespace ValidatedWorld.Mcp;

internal sealed record McpProjectSelection(
    string Path,
    string ProjectId,
    string Title,
    string PurposeNodeId,
    int NodeCount,
    int EdgeCount,
    string StateFingerprint,
    int SchemaVersion,
    string SqliteVersion);

internal sealed record McpProjectSelectionResult(
    bool Selected,
    McpProjectSelection? Project,
    string Message);

internal sealed record McpProjectInitializationResult(
    McpProjectSelection Project,
    string Message);

internal sealed record McpSemanticReviewHostStatus(
    bool Enabled,
    bool Configured,
    bool Effective,
    string Provider,
    string Model,
    int TimeoutSeconds);

internal sealed record McpHostStatus(
    string ProductVersion,
    string HostSupport,
    string Transport,
    string OperatingSystem,
    string ProcessArchitecture,
    string Framework,
    string InstallationDirectory,
    McpSemanticReviewHostStatus SemanticReview);

internal sealed record McpReadResult<T>(
    T? Item,
    bool Complete,
    McpOmission? Omission);

internal sealed record McpBoundedResult<T>(
    T? Data,
    bool Complete,
    McpOmission? Omission);

internal sealed record McpPage<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    string? NextCursor,
    McpOmission? Omission);

internal sealed record McpSearchHit(
    string EntityKind,
    string EntityId,
    NodeDto? Node,
    EdgeDto? Edge);

internal sealed record McpOmission(
    string Reason,
    int? RemainingCount,
    string Message);

internal sealed record McpScopeResult(
    NodeDto Node,
    IReadOnlyList<NodeDto> Upstream,
    McpPage<NodeDto> Descendants,
    IReadOnlyList<McpOmission> Omissions);

internal sealed record McpPathResult(
    bool Found,
    IReadOnlyList<string> Nodes,
    IReadOnlyList<string> Edges,
    IReadOnlyList<McpOmission> Omissions);

internal sealed record McpContextResult(
    IReadOnlyList<string> RequestedNodeIds,
    IReadOnlyList<NodeDto> ContextNodes,
    IReadOnlyList<McpOmission> Omissions);

internal sealed record McpReportSection<T>(
    int TotalCount,
    IReadOnlyList<T> Items,
    int OmittedCount);

internal sealed record McpReviewFanOutHotspot(
    string NodeId,
    int OutgoingReviewArcCount,
    int IncomingReviewArcCount);

internal sealed record McpIsolatedClaim(string NodeId, string? Kind);

internal sealed record McpMissingRationale(
    string EdgeId,
    string Source,
    string Target,
    string Relationship);

internal sealed record McpTagUsage(string Tag, int NodeCount, int EdgeCount, int TotalCount);

internal sealed record McpHealthResult(
    int NodeCount,
    int EdgeCount,
    int SemanticReviewArcCount,
    object ScopeCoverage,
    McpReportSection<string> UnreachableNodeIds,
    McpReportSection<McpReviewFanOutHotspot> ReviewFanOutHotspots,
    McpReportSection<McpIsolatedClaim> SuspiciouslyIsolatedClaims,
    McpReportSection<McpMissingRationale> MissingRationales,
    McpReportSection<McpTagUsage> TagUsage,
    int UntaggedNodeCount,
    int UntaggedEdgeCount,
    bool WasCancelled,
    IReadOnlyList<McpOmission> Omissions);

internal sealed record McpChangeSummary(
    int Revision,
    string ProjectId,
    string Intent,
    int OperationCount,
    int AffectedNodeCount,
    int ContextNodeCount,
    int PendingReviewCount,
    string Analysis,
    string Validation,
    bool Ready,
    IReadOnlyList<string> Blockers,
    string Message);

internal sealed record McpChangeOperation(
    string Kind,
    string EntityKind,
    string EntityId,
    NodeDto? Node,
    EdgeDto? Edge);

internal sealed record McpAffectedNode(
    string NodeId,
    bool IsDirectChange,
    int Distance,
    IReadOnlyList<string> PathNodes,
    IReadOnlyList<string> PathEdges,
    NodeDto? CurrentNode,
    NodeDto? ProposedNode);

internal sealed record McpAffectedEdgeChange(
    McpChangeOperation Operation,
    EdgeDto? CurrentEdge,
    EdgeDto? ProposedEdge);

internal sealed record McpScopeLineage(
    string AffectedNodeId,
    IReadOnlyList<string> CurrentPath,
    IReadOnlyList<string> ProposedPath);

internal sealed record McpScopeContext(
    string NodeId,
    IReadOnlyList<McpScopeLineage> Lineages,
    NodeDto? CurrentNode,
    NodeDto? ProposedNode);

internal sealed record McpAffectedOmission(
    string Reason,
    int Count,
    IReadOnlyList<McpOmissionDetail> Sample,
    string DetailsFingerprint);

internal sealed record McpOmissionDetail(
    string? SourceNodeId,
    string? TargetNodeId,
    string? EdgeId,
    int? Depth,
    string Message);

internal sealed record McpReadiness(
    bool IsReady,
    string AnalysisStatus,
    string ProposedValidationStatus,
    IReadOnlyList<string> PendingNodeIds,
    IReadOnlyList<string> MissingContextNodeIds,
    IReadOnlyList<string> Blockers);

internal sealed record McpDisposition(string NodeId, string Kind, string? Rationale);

internal sealed record McpChangePreview(
    int Revision,
    string ProjectId,
    string Title,
    string Intent,
    int OperationCount,
    int ProposedNodeCount,
    int ProposedEdgeCount,
    IReadOnlyList<McpChangeOperation> Operations,
    IReadOnlyList<McpAffectedNode> AffectedNodes,
    IReadOnlyList<McpAffectedEdgeChange> EdgeChanges,
    IReadOnlyList<McpScopeContext> ScopeContext,
    IReadOnlyList<McpAffectedOmission> Omissions,
    IReadOnlyList<McpDisposition> Dispositions,
    IReadOnlyList<string> PresentedContextNodeIds,
    McpReadiness Readiness);

internal sealed record McpApprovalRequested(
    bool ApprovalRequired,
    int Revision,
    McpChangePreview Preview,
    string Message);

internal sealed record McpApprovalResult(
    bool Approved,
    int Revision,
    string ApprovalId,
    DateTimeOffset ExpiresUtc,
    McpReadiness Readiness,
    string Message);

internal sealed record McpSemanticReviewConcern(
    string Code,
    string Message,
    IReadOnlyList<string> Citations);

internal sealed record McpSemanticReview(
    string Status,
    string? Decision,
    string Summary,
    IReadOnlyList<McpSemanticReviewConcern> Concerns,
    bool IsCurrent);

internal sealed record McpChangeWrite(
    string Status,
    string ProjectId,
    string Message,
    bool AiReviewBypassed,
    McpProjectSelection? Project,
    McpSemanticReview? SemanticReview);

internal sealed record McpDiscardResult(
    string ProjectId,
    int Revision,
    DateTimeOffset DiscardedUtc,
    string Message);

internal sealed record McpAttributeInput(string Name, string Kind, string Value);

internal sealed record McpPendingApproval(string Token, int Revision, ChangeSessionReference Reference);

internal sealed class McpProjectService(
    ProjectApplication application,
    McpHostOptions hostOptions,
    McpSemanticReviewConfiguration reviewConfiguration)
{
    private const int MaximumPathLength = 4_096;
    private const int MaximumOutputBytes = 512 * 1_024;
    private readonly object _gate = new();
    private readonly AuthoringApprovalGate _approvalGate = new();
    private readonly string _conversationId = $"mcp-{Guid.NewGuid():N}";
    private McpProjectSelection? _selection;
    private bool _defaultWasAttempted;
    private ChangeSessionSnapshot? _session;
    private int _revision;
    private McpPendingApproval? _pendingApproval;

    public McpHostStatus HostStatus() => new(
        McpAssembly.ProductVersion,
        "local-only",
        "stdio",
        RuntimeInformation.OSDescription,
        RuntimeInformation.ProcessArchitecture.ToString(),
        RuntimeInformation.FrameworkDescription,
        AppContext.BaseDirectory,
        new McpSemanticReviewHostStatus(
            reviewConfiguration.Enabled,
            reviewConfiguration.IsConfigured,
            reviewConfiguration.IsEffectivelyEnabled,
            reviewConfiguration.Provider,
            reviewConfiguration.Model,
            reviewConfiguration.TimeoutSeconds));

    public McpProjectSelectionResult Select(string path)
    {
        var normalized = ProjectPathPolicy.Existing(path);
        var selected = ToSelection(application.Status(normalized));
        lock (_gate)
        {
            EnsureNoActiveSession();
            _selection = selected;
        }
        return new McpProjectSelectionResult(true, selected, "The project is selected for this MCP session.");
    }

    public McpProjectInitializationResult Initialize(
        string path,
        string projectId,
        string title,
        string purposeNodeId,
        string purposeText)
    {
        lock (_gate) EnsureNoActiveSession();
        var normalized = ProjectPathPolicy.New(path);
        var created = application.Initialize(
            normalized,
            new ProjectId(projectId),
            title,
            new EntityId(purposeNodeId),
            purposeText);
        var selected = ToSelection(application.Status(created.Path));
        lock (_gate)
        {
            EnsureNoActiveSession();
            _selection = selected;
        }
        return new McpProjectInitializationResult(
            selected,
            "The purpose-only project was initialized and selected. Add graph content through a reviewed MCP change session.");
    }

    public McpProjectSelection Status()
    {
        EnsureDefaultSelected();
        return Selection();
    }

    public ProjectQueries Queries()
    {
        EnsureDefaultSelected();
        var selected = Selection();
        return application.Queries(selected.Path, new ProjectId(selected.ProjectId));
    }

    public McpChangeSummary BeginChange(string intent)
    {
        lock (_gate)
        {
            EnsureDefaultSelected();
            EnsureNoActiveSession();
            var selected = Selection();
            _session = application.BeginChange(
                selected.Path,
                new ProjectId(selected.ProjectId),
                "mcp-agent",
                intent);
            _revision = 1;
            _pendingApproval = null;
            _approvalGate.Invalidate(_conversationId);
            return Summary(_session, "The in-memory MCP change session has begun.");
        }
    }

    public object PatchChange(int expectedRevision, GraphOperationBatch operations, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var session = RequireRevision(expectedRevision);
            if (operations.Operations.Count > 100)
                throw new ArgumentException("One MCP proposal batch cannot contain more than 100 operations.", nameof(operations));
            if (session.Operations.Operations.Count + operations.Operations.Count > 1_000 &&
                operations.Operations.Any(operation => !session.Operations.Operations.Any(
                    existing => existing.EntityId == operation.EntityId)))
                throw new ArgumentException("A proposal cannot exceed 1,000 operations.", nameof(operations));

            _approvalGate.Invalidate(_conversationId);
            _pendingApproval = null;
            _session = application.PatchChange(
                session.Reference,
                operations,
                AnalysisOptions(cancellationToken));
            _revision++;
            return Summary(_session, "The bounded operation batch was applied to the in-memory proposal.");
        }
    }

    public object ExpandChange(int expectedRevision, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var session = RequireRevision(expectedRevision);
            _session = application.ExpandChange(session.Reference, AnalysisOptions(cancellationToken));
            _revision++;
            return Summary(_session, "Affected analysis was refreshed for the current proposal.");
        }
    }

    public McpChangePreview PreviewChange(int expectedRevision)
    {
        lock (_gate) return Preview(RequireRevision(expectedRevision), _revision);
    }

    public McpApprovalRequested RequestApproval(int expectedRevision)
    {
        lock (_gate)
        {
            var session = RequireRevision(expectedRevision);
            if (session.Operations.Operations.Count == 0)
                throw new InvalidOperationException("There is no proposal to approve.");
            if (!session.Affected.IsComplete || !session.Affected.ProposedValidation.IsValid ||
                !session.Affected.CurrentValidation.IsValid)
                throw new InvalidOperationException(
                    "Approval is unavailable while affected analysis is incomplete or graph validation is invalid.");

            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            _pendingApproval = new McpPendingApproval(token, _revision, session.Reference);
            var preview = Preview(session, _revision);
            WriteHumanApprovalRequest(preview, token);
            return new McpApprovalRequested(
                true,
                _revision,
                preview,
                "The complete proposal was sent to the local MCP host for human review. The human must provide the one-time token shown by that host to confirm_approval; the token is intentionally not returned here.");
        }
    }

    public McpApprovalResult ConfirmApproval(int expectedRevision, string token)
    {
        lock (_gate)
        {
            var session = RequireRevision(expectedRevision);
            var pending = _pendingApproval;
            if (pending is null || pending.Revision != _revision ||
                !ReferencesEqual(pending.Reference, session.Reference) ||
                !FixedTokenEquals(pending.Token, token))
                throw new InvalidOperationException(
                    "The human approval token is missing, expired, or belongs to a different proposal revision.");

            var direct = session.Affected.AffectedNodes
                .Where(node => node.IsDirectChange)
                .Select(node => node.NodeId)
                .ToHashSet();
            var dispositions = session.Dispositions
                .Where(value => value.Kind == ReviewDispositionKind.Pending)
                .Select(value => new ReviewDisposition(
                    value.NodeId,
                    direct.Contains(value.NodeId)
                        ? ReviewDispositionKind.Updated
                        : ReviewDispositionKind.ReviewedNoChange,
                    null))
                .ToArray();
            _session = application.ReviewChange(
                session.Reference,
                new ChangeReviewUpdate(
                    dispositions,
                    session.Affected.ScopeContext.Select(value => value.NodeId)));
            _revision++;
            var approval = _approvalGate.Approve(_conversationId, _session);
            _pendingApproval = null;
            return new McpApprovalResult(
                true,
                _revision,
                approval.ApprovalId,
                approval.ExpiresUtc,
                Readiness(_session.Readiness),
                "The human approved the exact proposal and all displayed affected/context items. write_change may now be attempted with the current revision.");
        }
    }

    public async Task<McpChangeWrite> WriteChangeAsync(
        int expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ChangeSessionSnapshot session;
        AuthoringApproval approval;
        lock (_gate)
        {
            session = RequireRevision(expectedRevision);
            approval = _approvalGate.RequireCurrent(_conversationId, session.Path, session.Reference);
        }

        var result = await application.WriteChangeAsync(
            session.Reference,
            new ChangeWriteOptions(BypassAiReview: false),
            cancellationToken);
        lock (_gate)
        {
            if (result.Status == ChangeWriteStatus.Written)
            {
                _session = null;
                _revision = 0;
                _pendingApproval = null;
                _approvalGate.Invalidate(_conversationId);
                _selection = ToSelection(application.Status(session.Path));
            }

            return new McpChangeWrite(
                result.Status.ToString(),
                result.ProjectId.Value,
                result.Message,
                result.AiReviewBypassed,
                result.Project is null ? null : ToSelection(application.Status(result.Project.Path)),
                result.SemanticReview is null ? null : new McpSemanticReview(
                    result.SemanticReview.Status.ToString(),
                    result.SemanticReview.Decision?.ToString(),
                    result.SemanticReview.Summary,
                    result.SemanticReview.Concerns.Select(concern => new McpSemanticReviewConcern(
                        concern.Code,
                        concern.Message,
                        concern.Citations.Select(id => id.Value).ToArray())).ToArray(),
                    result.SemanticReview.IsCurrent));
        }
    }

    public McpDiscardResult DiscardChange(int expectedRevision)
    {
        lock (_gate)
        {
            var session = RequireRevision(expectedRevision);
            var discarded = application.DiscardChange(session.Reference);
            _session = null;
            _revision = 0;
            _pendingApproval = null;
            _approvalGate.Invalidate(_conversationId);
            return new McpDiscardResult(
                discarded.ProjectId.Value,
                expectedRevision,
                discarded.DiscardedUtc,
                "The unresolved in-memory MCP proposal was discarded and was not written.");
        }
    }

    private McpProjectSelection Selection()
    {
        lock (_gate)
        {
            return _selection ?? throw new InvalidOperationException(
                "No project is selected. Call select_project with an existing .vw.db path, or initialize_project first.");
        }
    }

    private void EnsureDefaultSelected()
    {
        string? path;
        lock (_gate)
        {
            if (_selection is not null || _defaultWasAttempted) return;
            _defaultWasAttempted = true;
            path = hostOptions.DefaultProjectPath;
        }

        if (path is not null) Select(path);
    }

    private void EnsureNoActiveSession()
    {
        if (_session is not null)
            throw new ChangeSessionException(
                ChangeSessionErrorCode.SessionAlreadyActive,
                "An unresolved MCP change session is active; preview, approve, write, or discard it before switching projects.");
    }

    private ChangeSessionSnapshot RequireRevision(int expectedRevision)
    {
        if (expectedRevision <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision), "The proposal revision must be positive.");
        var session = _session ?? throw new InvalidOperationException(
            "No MCP change session is active. Call begin_change first.");
        if (expectedRevision != _revision)
            throw new ChangeSessionException(
                ChangeSessionErrorCode.StaleProposalFingerprint,
                $"The proposal revision {expectedRevision} is stale; the current revision is {_revision}.");
        return session;
    }

    private static AffectedAnalysisOptions AnalysisOptions(CancellationToken cancellationToken) => new()
    {
        MaxTraversalDepth = 10_000,
        MaxAffectedNodes = 100_000,
        MaxOutputItems = 5_000,
        CancellationToken = cancellationToken,
    };

    private McpChangeSummary Summary(ChangeSessionSnapshot session, string message) => new(
        _revision,
        session.ProposedGraph.ProjectId.Value,
        session.Intent,
        session.Operations.Operations.Count,
        session.Affected.AffectedNodes.Count,
        session.Affected.ScopeContext.Count,
        session.Readiness.PendingNodeIds.Count,
        session.Affected.Status.ToString(),
        session.Affected.ProposedValidation.Status.ToString(),
        session.Readiness.IsReady,
        session.Readiness.Blockers,
        message);

    private static McpChangePreview Preview(ChangeSessionSnapshot session, int revision) => new(
        revision,
        session.ProposedGraph.ProjectId.Value,
        session.ProposedGraph.Title,
        session.Intent,
        session.Operations.Operations.Count,
        session.ProposedGraph.Nodes.Count,
        session.ProposedGraph.Edges.Count,
        session.Operations.Operations.Select(Operation).ToArray(),
        session.Affected.AffectedNodes.Select(node => new McpAffectedNode(
            node.NodeId.Value,
            node.IsDirectChange,
            node.Distance,
            node.Explanation.Nodes.Select(id => id.Value).ToArray(),
            node.Explanation.Edges.Select(id => id.Value).ToArray(),
            node.CurrentNode is null ? null : Node(node.CurrentNode),
            node.ProposedNode is null ? null : Node(node.ProposedNode))).ToArray(),
        session.Affected.EdgeChanges.Select(change => new McpAffectedEdgeChange(
            Operation(change.Operation),
            change.CurrentEdge is null ? null : Edge(change.CurrentEdge),
            change.ProposedEdge is null ? null : Edge(change.ProposedEdge))).ToArray(),
        session.Affected.ScopeContext.Select(context => new McpScopeContext(
            context.NodeId.Value,
            context.Lineages.Select(lineage => new McpScopeLineage(
                lineage.AffectedNodeId.Value,
                lineage.CurrentPath.Select(id => id.Value).ToArray(),
                lineage.ProposedPath.Select(id => id.Value).ToArray())).ToArray(),
            context.CurrentNode is null ? null : Node(context.CurrentNode),
            context.ProposedNode is null ? null : Node(context.ProposedNode))).ToArray(),
        session.Affected.Omissions.Select(omission => new McpAffectedOmission(
            omission.Reason.ToString(),
            omission.Count,
            omission.Sample.Select(sample => new McpOmissionDetail(
                sample.SourceNodeId?.Value,
                sample.TargetNodeId?.Value,
                sample.EdgeId?.Value,
                sample.Depth,
                sample.Message)).ToArray(),
            omission.DetailsFingerprint)).ToArray(),
        session.Dispositions.Select(disposition => new McpDisposition(
            disposition.NodeId.Value,
            disposition.Kind.ToString(),
            disposition.Rationale)).ToArray(),
        session.PresentedContextNodeIds.Select(id => id.Value).ToArray(),
        Readiness(session.Readiness));

    private void WriteHumanApprovalRequest(McpChangePreview preview, string token)
    {
        var output = JsonSerializer.Serialize(preview, Protocol.CreateJsonOptions());
        Console.Error.WriteLine("ValidatedWorld MCP human approval required.");
        Console.Error.WriteLine("Review this exact proposal before continuing:");
        Console.Error.WriteLine(output);
        Console.Error.WriteLine($"One-time approval token (not returned to the agent): {token}");
        Console.Error.WriteLine("Provide that token to the human-controlled MCP client, then call confirm_approval with the current revision.");
    }

    private static McpChangeOperation Operation(GraphOperation operation) => new(
        operation.Kind.ToString(),
        operation.EntityKind.ToString(),
        operation.EntityId.Value,
        operation.Node is null ? null : Node(operation.Node),
        operation.Edge is null ? null : Edge(operation.Edge));

    private static McpReadiness Readiness(ReviewReadinessResult readiness) => new(
        readiness.IsReady,
        readiness.AnalysisStatus.ToString(),
        readiness.ProposedValidationStatus.ToString(),
        readiness.PendingNodeIds.Select(id => id.Value).ToArray(),
        readiness.MissingContextNodeIds.Select(id => id.Value).ToArray(),
        readiness.Blockers);

    private static bool ReferencesEqual(ChangeSessionReference left, ChangeSessionReference right) =>
        left.ProjectId == right.ProjectId &&
        StringComparer.Ordinal.Equals(left.SessionId, right.SessionId) &&
        StringComparer.Ordinal.Equals(left.BaseFingerprint, right.BaseFingerprint) &&
        StringComparer.Ordinal.Equals(left.OperationFingerprint, right.OperationFingerprint) &&
        StringComparer.Ordinal.Equals(left.ProposedFingerprint, right.ProposedFingerprint) &&
        StringComparer.Ordinal.Equals(left.AffectedFingerprint, right.AffectedFingerprint) &&
        StringComparer.Ordinal.Equals(left.ReviewFingerprint, right.ReviewFingerprint);

    private static bool FixedTokenEquals(string expected, string? actual)
    {
        if (string.IsNullOrWhiteSpace(actual)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
    }

    public static GraphOperationBatch ParseOperations(OperationBatchDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        return GraphProtocol.FromDto(dto);
    }

    public static GraphOperation NodeOperation(
        string mode,
        string id,
        string text,
        string? kind,
        IEnumerable<string>? tags,
        IEnumerable<McpAttributeInput>? attributes) =>
        new(ParseKind(mode), new GraphNode(new EntityId(id), text, kind, tags, Attributes(attributes)));

    public static GraphOperation EdgeOperation(
        string mode,
        string id,
        string source,
        string target,
        string relationship,
        string reviewDirection,
        string? rationale,
        IEnumerable<string>? tags,
        IEnumerable<McpAttributeInput>? attributes) =>
        new(ParseKind(mode), new GraphEdge(
            new EntityId(id),
            new EntityId(source),
            new EntityId(target),
            relationship,
            Enum.Parse<ReviewDirection>(reviewDirection, ignoreCase: true),
            rationale,
            tags,
            Attributes(attributes)));

    public static GraphOperation RemoveOperation(string entityKind, string id) => new(
        GraphOperationKind.Remove,
        entityKind switch
        {
            "node" => GraphEntityKind.Node,
            "edge" => GraphEntityKind.Edge,
            _ => throw new ArgumentException("entityKind must be node or edge.", nameof(entityKind)),
        },
        new EntityId(id));

    private static GraphOperationKind ParseKind(string mode) => mode switch
    {
        "add" => GraphOperationKind.Add,
        "replace" => GraphOperationKind.Replace,
        _ => throw new ArgumentException("mode must be add or replace.", nameof(mode)),
    };

    private static IReadOnlyList<KeyValuePair<string, GraphValue>> Attributes(IEnumerable<McpAttributeInput>? values) =>
        (values ?? []).Select(attribute => new KeyValuePair<string, GraphValue>(
            attribute.Name,
            attribute.Kind switch
            {
                "text" => GraphValue.FromText(attribute.Value),
                "integer" => GraphValue.FromInteger(long.Parse(attribute.Value, CultureInfo.InvariantCulture)),
                "decimal" => GraphValue.FromDecimal(attribute.Value),
                "boolean" => GraphValue.FromBoolean(bool.Parse(attribute.Value)),
                "symbol" => GraphValue.FromSymbol(attribute.Value),
                "instant" => GraphValue.FromInstant(DateTimeOffset.Parse(
                    attribute.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)),
                _ => throw new ArgumentException($"Unknown attribute kind '{attribute.Kind}'."),
            })).ToArray();

    private static McpProjectSelection ToSelection(ProjectStatus status) => new(
        status.Path,
        status.ProjectId.Value,
        status.Title,
        status.PurposeNodeId.Value,
        status.NodeCount,
        status.EdgeCount,
        status.StateFingerprint,
        status.SchemaVersion,
        status.SqliteVersion);

    public static NodeDto Node(GraphNode node) => GraphProtocol.ToDto(node);

    public static EdgeDto Edge(GraphEdge edge) => GraphProtocol.ToDto(edge);

    public static McpPage<TOut> ProjectPage<TIn, TOut>(QueryPage<TIn> page, Func<TIn, TOut> projection)
    {
        var items = page.Items.Select(projection).ToArray();
        var result = new McpPage<TOut>(items, page.TotalCount, page.NextCursor, Omission(page.Omission));
        return Fits(result)
            ? result
            : new McpPage<TOut>([], page.TotalCount, null, new McpOmission(
                "output-byte-limit", page.TotalCount,
                $"The result exceeded the {MaximumOutputBytes} byte MCP output bound; request a narrower query."));
    }

    public static McpReadResult<T> Read<T>(T item)
    {
        var result = new McpReadResult<T>(item, true, null);
        return Fits(result)
            ? result
            : new McpReadResult<T>(default, false, new McpOmission(
                "output-byte-limit", null,
                $"The result exceeded the {MaximumOutputBytes} byte MCP output bound; request a narrower query."));
    }

    public static object Bound<T>(T item)
    {
        if (Fits(item)) return item!;
        return new McpBoundedResult<T>(default, false, new McpOmission(
            "output-byte-limit", null,
            $"The result exceeded the {MaximumOutputBytes} byte MCP output bound; request a narrower query."));
    }

    public static McpOmission? Omission(QueryOmission? omission) => omission is null
        ? null
        : new McpOmission(omission.Reason.ToString(), omission.RemainingCount, omission.Message);

    public static IReadOnlyList<McpOmission> Omissions(IEnumerable<QueryOmission> omissions) =>
        omissions.Select(omission => new McpOmission(
            omission.Reason.ToString(), omission.RemainingCount, omission.Message)).ToArray();

    private static bool Fits<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, Protocol.CreateJsonOptions()).Length <= MaximumOutputBytes;
}

internal static class ProjectPathPolicy
{
    public static string Existing(string path)
    {
        var normalized = Normalize(path);
        if (!File.Exists(normalized))
            throw new FileNotFoundException($"The selected project file does not exist: '{normalized}'.", normalized);

        var info = new FileInfo(normalized);
        if ((info.Attributes & FileAttributes.Directory) != 0)
            throw new ArgumentException("The selected project path must be a file.", nameof(path));

        var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
        return resolved is null ? normalized : Normalize(resolved.FullName);
    }

    public static string New(string path)
    {
        var normalized = Normalize(path);
        if (File.Exists(normalized) || Directory.Exists(normalized))
            throw new IOException($"The project destination already exists: '{normalized}'.");
        return normalized;
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4_096 || path.Any(char.IsControl))
            throw new ArgumentException("A project path must be non-empty, bounded, and free of control characters.", nameof(path));

        var fullPath = Path.GetFullPath(path);
        if (Path.GetPathRoot(fullPath) is null ||
            !fullPath.EndsWith(".vw.db", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A project path must be rooted after normalization and end with '.vw.db'.", nameof(path));

        var installPath = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (fullPath.StartsWith(installPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            StringComparer.OrdinalIgnoreCase.Equals(fullPath, installPath))
            throw new ArgumentException("Project data must be stored outside the MCP host installation directory.", nameof(path));

        return fullPath;
    }
}
