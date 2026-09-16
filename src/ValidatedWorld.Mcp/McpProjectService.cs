using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
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
    string SqliteVersion);

internal sealed record McpProjectSelectionResult(
    bool Selected,
    McpProjectSelection? Project,
    string Message);

internal sealed record McpProjectInitializationResult(
    McpProjectSelection Project,
    string Message);

internal sealed record McpBulkImportPlan(
    string ManifestPath,
    string ProjectId,
    string BaseFingerprint,
    string ManifestFingerprint,
    string Intent,
    int OperationCount,
    int ChunkSize,
    int ChunkIndex,
    int OperationStart,
    int ChunkOperationCount,
    int ChunkCount,
    OperationBatchDto Operations,
    string? NextCursor);

internal sealed record McpArtifactCheckItem(
    string NodeId,
    string? Path,
    string? ResolvedPath,
    string? AdapterId,
    int? AdapterVersion,
    string Status,
    string Message,
    string? ExpectedSha256,
    string? ActualSha256,
    string? ContentSampleBase64,
    bool ContentSampleTruncated);

internal sealed record McpArtifactCheckReport(
    string ProjectPath,
    int TotalAnchorCount,
    IReadOnlyList<McpArtifactCheckItem> Items,
    int MatchedCount,
    int DriftedCount,
    int MissingCount,
    int InvalidAnchorCount,
    int UnsupportedAdapterCount,
    int UnauthorizedCount,
    int UnreadableCount,
    bool IsComplete,
    string? OmissionMessage);

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
    string SupportedProductLanguage,
    string GraphTextSupport,
    string OperatingSystem,
    string ProcessArchitecture,
    string Framework,
    string InstallationDirectory,
    IReadOnlyList<string> ArtifactAllowedRoots,
    McpSemanticReviewHostStatus SemanticReview);

internal sealed record McpReadResult<T>(
    T? Item,
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
    int PendingNodeCount,
    int MissingContextNodeCount,
    IReadOnlyList<string> Blockers);

internal sealed record McpDisposition(string NodeId, string Kind, string? Rationale);

internal sealed record McpValidationSummary(string Status, int DiagnosticCount);

internal sealed record McpReviewItem(
    int Ordinal,
    string Kind,
    McpChangeOperation? Operation = null,
    McpAffectedNode? AffectedNode = null,
    McpAffectedEdgeChange? EdgeChange = null,
    McpScopeContext? ScopeContext = null,
    McpAffectedOmission? Omission = null,
    McpDisposition? Disposition = null,
    DiagnosticDto? Diagnostic = null);

internal sealed record McpReviewPage(
    int Offset,
    int Limit,
    int TotalCount,
    IReadOnlyList<McpReviewItem> Items,
    string? NextCursor,
    bool IsComplete,
    bool AllEvidencePresented);

internal sealed record McpChangePreview(
    int Revision,
    string ProjectId,
    string Title,
    string Intent,
    int OperationCount,
    int ProposedNodeCount,
    int ProposedEdgeCount,
    int AffectedNodeCount,
    int EdgeChangeCount,
    int ScopeContextCount,
    int OmissionCount,
    int DispositionCount,
    McpReviewPage ReviewPage,
    McpReadiness Readiness,
    McpValidationSummary CurrentValidation,
    McpValidationSummary ProposedValidation);

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

// Only deliberately user-facing workflow failures cross the MCP error boundary.
internal sealed class McpWorkflowException(string message) : InvalidOperationException(message);

internal sealed class McpProjectService(
    ProjectApplication application,
    McpHostOptions hostOptions,
    McpSemanticReviewConfiguration reviewConfiguration)
{
    private readonly object _gate = new();
    private McpProjectSelection? _selection;
    private bool _defaultWasAttempted;
    private ChangeSessionSnapshot? _session;
    private int _revision;
    private int _previewCoverageRevision;
    private readonly HashSet<int> _presentedReviewOrdinals = [];
    private Task<McpChangeWrite>? _activeWrite;
    private int _activeWriteRevision;

    public McpHostStatus HostStatus() => new(
        McpAssembly.ProductVersion,
        "local-only",
        "stdio",
        "English",
        "Unicode text is stored without language interpretation; non-English workflows are unsupported and unvalidated.",
        RuntimeInformation.OSDescription,
        RuntimeInformation.ProcessArchitecture.ToString(),
        RuntimeInformation.FrameworkDescription,
        AppContext.BaseDirectory,
        hostOptions.ArtifactAllowedRoots,
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
        lock (_gate)
        {
            EnsureNoActiveSession();
            var normalized = ProjectPathPolicy.New(path);
            var created = application.Initialize(
                normalized,
                new ProjectId(projectId),
                title,
                new EntityId(purposeNodeId),
                purposeText);
            var selected = ToSelection(application.Status(created.Path));
            _selection = selected;
            return new McpProjectInitializationResult(
                selected,
                "The purpose-only project was initialized and selected. Add graph content through a reviewed MCP change session.");
        }
    }

    public IReadOnlyList<TemplateDescriptor> ListTemplates() => application.ListTemplates();

    public object DescribeTemplate(string nameOrPath)
    {
        var template = application.ReadTemplate(nameOrPath);
        return new
        {
            descriptor = GraphTemplateCatalog.Describe(template),
            template.PurposeNodeId,
            requiredInputs = new[] { "path", "projectId", "title", "purposeText" },
            workflow = "Inspect repository evidence and uncertainty, instantiate explicitly, then use ordinary reviewed changes to populate and activate the roadmap.",
        };
    }

    public McpProjectInitializationResult InitializeTemplate(
        string nameOrPath,
        string path,
        string projectId,
        string title,
        string purposeText)
    {
        lock (_gate)
        {
            EnsureNoActiveSession();
            var normalized = ProjectPathPolicy.New(path);
            var created = application.InstantiateTemplate(nameOrPath, normalized, new ProjectId(projectId), title, purposeText);
            var selected = ToSelection(application.Status(created.Path));
            _selection = selected;
            return new McpProjectInitializationResult(selected,
                "The selected template was instantiated and selected. Attached active rules govern subsequent reviewed changes.");
        }
    }

    public McpProjectSelection Status()
    {
        EnsureDefaultSelected();
        lock (_gate)
        {
            var selected = Selection();
            var current = ToSelection(application.Status(selected.Path));
            if (!StringComparer.Ordinal.Equals(current.ProjectId, selected.ProjectId))
                throw new ProjectQueryException(ProjectQueryErrorCode.ProjectMismatch,
                    "The selected file now contains a different project. Call select_project explicitly.");
            _selection = current;
            return current;
        }
    }

    public object Merge(string basePath, string oursPath, string theirsPath)
    {
        var result = application.Merge(
            ProjectPathPolicy.Existing(basePath),
            ProjectPathPolicy.Existing(oursPath),
            ProjectPathPolicy.Existing(theirsPath));
        return new
        {
            result.BasePath,
            result.OursPath,
            result.TheirsPath,
            projectId = result.ProjectId.Value,
            result.BaseFingerprint,
            result.OursFingerprint,
            result.TheirsFingerprint,
            result.Status,
            result.IsReadyToApply,
            result.MergedFingerprint,
            operationCount = result.Operations.Operations.Count,
            operations = GraphProtocol.ToDto(result.Operations),
            validation = result.Validation is null ? null : ValidationProtocol.ToDto(result.Validation),
            conflicts = result.Conflicts,
        };
    }

    public object PlanBulkImport(
        string manifestPath,
        int chunkSize,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var selection = Status();
        var plan = application.PlanBulkImport(
            selection.Path,
            manifestPath,
            new BulkImportPlanOptions(chunkSize, cursor),
            cancellationToken);
        return new McpBulkImportPlan(
            plan.ManifestPath,
            plan.ProjectId,
            plan.BaseFingerprint,
            plan.ManifestFingerprint,
            plan.Intent,
            plan.OperationCount,
            plan.ChunkSize,
            plan.ChunkIndex,
            plan.OperationStart,
            plan.ChunkOperationCount,
            plan.ChunkCount,
            GraphProtocol.ToDto(plan.Operations),
            plan.NextCursor);
    }

    public ProjectQueries Queries()
    {
        EnsureDefaultSelected();
        var selected = Selection();
        return application.Queries(selected.Path, new ProjectId(selected.ProjectId));
    }

    public object ValidateProject()
    {
        var selection = Status();
        var result = application.Verify(selection.Path);
        return new
        {
            result.IsValid,
            result.RuleStatus,
            ruleDiagnostics = (result.RuleDiagnostics ?? []).Select(item => new
            {
                item.Code, item.Message, ruleId = item.RuleId?.Value,
                offendingEntityIds = item.OffendingEntityIds.Select(id => id.Value).ToArray(),
                item.TotalOffendingCount, item.OmittedOffendingCount,
            }).ToArray(),
            result.Checks,
        };
    }

    public object CheckArtifacts(string? nodeId, int maxAnchors, int maxSampleBytes, CancellationToken cancellationToken)
    {
        var selection = Status();
        var report = application.CheckArtifacts(
            selection.Path,
            nodeId is null ? null : new EntityId(nodeId),
            new ArtifactCheckOptions(maxAnchors, maxSampleBytes, hostOptions.ArtifactAllowedRoots),
            cancellationToken);
        return new McpArtifactCheckReport(
            report.ProjectPath,
            report.TotalAnchorCount,
            report.Items.Select(item => new McpArtifactCheckItem(
                item.NodeId.Value,
                item.Path,
                item.ResolvedPath,
                item.AdapterId,
                item.AdapterVersion,
                item.Status.ToString(),
                item.Message,
                item.ExpectedSha256,
                item.ActualSha256,
                item.ContentSampleBase64,
                item.ContentSampleTruncated)).ToArray(),
            report.MatchedCount,
            report.DriftedCount,
            report.MissingCount,
            report.InvalidAnchorCount,
            report.UnsupportedAdapterCount,
            report.UnauthorizedCount,
            report.UnreadableCount,
            report.IsComplete,
            report.OmissionMessage);
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
            // Revisions identify proposals across the entire MCP process, including
            // discarded sessions and project switches. Never reuse an old token.
            _revision = checked(_revision + 1);
            ResetPreviewCoverage();
            return Summary(_session, "The in-memory MCP change session has begun.");
        }
    }

    public object PatchChange(int expectedRevision, GraphOperationBatch operations, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var session = RequireRevision(expectedRevision);
            _session = application.PatchChange(
                session.Reference,
                operations,
                AnalysisOptions(cancellationToken));
            _revision = checked(_revision + 1);
            ResetPreviewCoverage();
            return Summary(_session, "The operation batch was applied to the in-memory proposal.");
        }
    }

    public object ExpandChange(int expectedRevision, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var session = RequireRevision(expectedRevision);
            _session = application.ExpandChange(session.Reference, AnalysisOptions(cancellationToken));
            _revision = checked(_revision + 1);
            ResetPreviewCoverage();
            return Summary(_session, "Affected analysis was refreshed for the current proposal.");
        }
    }

    public McpChangePreview PreviewChange(int expectedRevision, int limit, string? cursor)
    {
        lock (_gate) return Preview(RequireRevision(expectedRevision), _revision, limit, cursor);
    }

    public Task<McpChangeWrite> WriteChangeAsync(
        int expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ChangeSessionSnapshot session;
        lock (_gate)
        {
            session = RequireRevision(expectedRevision);
            if (_activeWrite is not null && _activeWriteRevision == expectedRevision)
                return _activeWrite;
            EnsureReviewPresented(session, expectedRevision);
            session = CompleteReviewForWrite(session);
            _session = session;
            _activeWriteRevision = expectedRevision;
            _activeWrite = RunWriteAsync(session, expectedRevision, cancellationToken);
            return _activeWrite;
        }
    }

    private async Task<McpChangeWrite> RunWriteAsync(
        ChangeSessionSnapshot session,
        int revision,
        CancellationToken cancellationToken)
    {
        // Ensure the shared task is installed under _gate before provider work can complete.
        await Task.Yield();
        try
        {
            var result = await application.WriteChangeAsync(
                session.Reference,
                new ChangeWriteOptions(BypassAiReview: false),
                cancellationToken);
            lock (_gate)
            {
                if (result.Status == ChangeWriteStatus.Written &&
                    _revision == revision && _session?.Reference == session.Reference)
                {
                    _session = null;
                    ResetPreviewCoverage();
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
        finally
        {
            lock (_gate)
            {
                // Completing agent dispositions changes the private Application
                // reference without changing the public proposal revision. If the
                // write did not consume the session, require a fresh presentation
                // before any retry so coverage still describes the exact snapshot.
                if (_revision == revision && _session?.Reference == session.Reference)
                    ResetPreviewCoverage();
                if (_activeWriteRevision == revision)
                {
                    _activeWrite = null;
                    _activeWriteRevision = 0;
                }
            }
        }
    }

    public McpDiscardResult DiscardChange(int expectedRevision)
    {
        lock (_gate)
        {
            var session = RequireRevision(expectedRevision);
            var discarded = application.DiscardChange(session.Reference);
            _session = null;
            ResetPreviewCoverage();
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
            return _selection ?? throw new McpWorkflowException(
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
                "An unresolved MCP change session is active; preview, write, or discard it before switching projects.");
    }

    private ChangeSessionSnapshot RequireRevision(int expectedRevision)
    {
        if (expectedRevision <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision), "The proposal revision must be positive.");
        var session = _session ?? throw new McpWorkflowException(
            "No MCP change session is active. Call begin_change first.");
        if (expectedRevision != _revision)
            throw new ChangeSessionException(
                ChangeSessionErrorCode.StaleProposalFingerprint,
                $"The proposal revision {expectedRevision} is stale; the current revision is {_revision}.");
        return session;
    }

    private static AffectedAnalysisOptions AnalysisOptions(CancellationToken cancellationToken) => new()
    {
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

    private McpChangePreview Preview(ChangeSessionSnapshot session, int revision, int limit, string? cursor)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit), "The review page size must be positive.");
        var items = ReviewItems(session);
        var offset = DecodePreviewCursor(cursor, revision, limit);
        if (offset < 0 || offset > items.Count)
            throw new McpWorkflowException("The proposal preview cursor is invalid for this revision and page size.");
        var pageItems = items.Skip(offset).Take(limit).ToArray();
        if (_previewCoverageRevision != revision)
        {
            _previewCoverageRevision = revision;
            _presentedReviewOrdinals.Clear();
        }
        foreach (var item in pageItems) _presentedReviewOrdinals.Add(item.Ordinal);
        var nextOffset = checked(offset + pageItems.Length);
        var nextCursor = nextOffset < items.Count ? EncodePreviewCursor(revision, limit, nextOffset) : null;
        var allPresented = _presentedReviewOrdinals.Count == items.Count;
        return new McpChangePreview(
            revision,
            session.ProposedGraph.ProjectId.Value,
            session.ProposedGraph.Title,
            session.Intent,
            session.Operations.Operations.Count,
            session.ProposedGraph.Nodes.Count,
            session.ProposedGraph.Edges.Count,
            session.Affected.AffectedNodes.Count,
            session.Affected.EdgeChanges.Count,
            session.Affected.ScopeContext.Count,
            session.Affected.Omissions.Count,
            session.Dispositions.Count,
            new McpReviewPage(offset, limit, items.Count, pageItems, nextCursor,
                nextCursor is null, allPresented),
            Readiness(session.Readiness),
            new McpValidationSummary(session.Affected.CurrentValidation.Status.ToString(),
                session.Affected.CurrentValidation.Diagnostics.Count),
            new McpValidationSummary(session.Affected.ProposedValidation.Status.ToString(),
                session.Affected.ProposedValidation.Diagnostics.Count));
    }

    private static IReadOnlyList<McpReviewItem> ReviewItems(ChangeSessionSnapshot session)
    {
        var items = new List<McpReviewItem>();
        void Add(string kind, McpChangeOperation? operation = null, McpAffectedNode? affected = null,
            McpAffectedEdgeChange? edgeChange = null, McpScopeContext? context = null,
            McpAffectedOmission? omission = null, McpDisposition? disposition = null,
            DiagnosticDto? diagnostic = null) =>
            items.Add(new McpReviewItem(items.Count, kind, operation, affected, edgeChange, context,
                omission, disposition, diagnostic));

        foreach (var operation in session.Operations.Operations) Add("operation", operation: Operation(operation));
        foreach (var node in session.Affected.AffectedNodes) Add("affectedNode", affected: new McpAffectedNode(
            node.NodeId.Value,
            node.IsDirectChange,
            node.Distance,
            node.Explanation.Nodes.Select(id => id.Value).ToArray(),
            node.Explanation.Edges.Select(id => id.Value).ToArray(),
            node.CurrentNode is null ? null : Node(node.CurrentNode),
            node.ProposedNode is null ? null : Node(node.ProposedNode)));
        foreach (var change in session.Affected.EdgeChanges) Add("edgeChange", edgeChange: new McpAffectedEdgeChange(
            Operation(change.Operation), change.CurrentEdge is null ? null : Edge(change.CurrentEdge),
            change.ProposedEdge is null ? null : Edge(change.ProposedEdge)));
        foreach (var context in session.Affected.ScopeContext) Add("scopeContext", context: new McpScopeContext(
            context.NodeId.Value,
            context.Lineages.Select(lineage => new McpScopeLineage(
                lineage.AffectedNodeId.Value,
                lineage.CurrentPath.Select(id => id.Value).ToArray(),
                lineage.ProposedPath.Select(id => id.Value).ToArray())).ToArray(),
            context.CurrentNode is null ? null : Node(context.CurrentNode),
            context.ProposedNode is null ? null : Node(context.ProposedNode)));
        foreach (var omission in session.Affected.Omissions) Add("omission", omission: new McpAffectedOmission(
            omission.Reason.ToString(),
            omission.Count,
            omission.Sample.Select(sample => new McpOmissionDetail(
                sample.SourceNodeId?.Value,
                sample.TargetNodeId?.Value,
                sample.EdgeId?.Value,
                sample.Depth,
                sample.Message)).ToArray(),
            omission.DetailsFingerprint));
        foreach (var disposition in session.Dispositions) Add("disposition", disposition: new McpDisposition(
            disposition.NodeId.Value, disposition.Kind.ToString(), disposition.Rationale));
        foreach (var diagnostic in ValidationProtocol.ToDto(session.Affected.CurrentValidation).Diagnostics)
            Add("currentValidationDiagnostic", diagnostic: diagnostic);
        foreach (var diagnostic in ValidationProtocol.ToDto(session.Affected.ProposedValidation).Diagnostics)
            Add("proposedValidationDiagnostic", diagnostic: diagnostic);
        return items;
    }

    private void EnsureReviewPresented(ChangeSessionSnapshot session, int revision)
    {
        var total = ReviewItems(session).Count;
        if (_previewCoverageRevision != revision || _presentedReviewOrdinals.Count != total)
            throw new McpWorkflowException(
                $"The exact proposal review evidence has not been fully presented for revision {revision}. " +
                "Call proposal_preview and follow every nextCursor before write_change.");
    }

    private void ResetPreviewCoverage()
    {
        _previewCoverageRevision = 0;
        _presentedReviewOrdinals.Clear();
    }

    private static string EncodePreviewCursor(int revision, int limit, int offset) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"mcp-preview-v1:{revision}:{limit}:{offset}"));

    private static int DecodePreviewCursor(string? cursor, int revision, int limit)
    {
        if (cursor is null) return 0;
        try
        {
            var value = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var parts = value.Split(':');
            if (parts.Length != 4 || parts[0] != "mcp-preview-v1" ||
                !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var cursorRevision) ||
                !int.TryParse(parts[2], CultureInfo.InvariantCulture, out var cursorLimit) ||
                !int.TryParse(parts[3], CultureInfo.InvariantCulture, out var offset) ||
                cursorRevision != revision || cursorLimit != limit)
                throw new FormatException();
            return offset;
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            throw new McpWorkflowException("The proposal preview cursor is invalid for this revision and page size.");
        }
    }

    private ChangeSessionSnapshot CompleteReviewForWrite(ChangeSessionSnapshot session)
    {
        if (session.Operations.Operations.Count == 0)
            throw new McpWorkflowException("There is no proposal to write. Add changes or call discard_change.");
        // A structurally valid baseline may violate an attached rule. Require
        // the complete candidate to pass, so the agent can repair that baseline.
        if (!session.Affected.IsComplete || !session.Affected.ProposedValidation.IsValid)
            throw new McpWorkflowException(
                "The proposal cannot be written while affected analysis is incomplete or proposed graph validation is invalid. Call proposal_preview to inspect diagnostics and repair the proposal.");

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
        var reviewed = application.ReviewChange(
            session.Reference,
            new ChangeReviewUpdate(
                dispositions,
                session.Affected.ScopeContext.Select(value => value.NodeId)));
        if (!reviewed.Readiness.IsReady)
            throw new McpWorkflowException(string.Join(" ", reviewed.Readiness.Blockers));
        return reviewed;
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
        readiness.PendingNodeIds.Count,
        readiness.MissingContextNodeIds.Count,
        readiness.Blockers);

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
        status.SqliteVersion);

    public static NodeDto Node(GraphNode node) => GraphProtocol.ToDto(node);

    public static EdgeDto Edge(GraphEdge edge) => GraphProtocol.ToDto(edge);

    public static McpPage<TOut> ProjectPage<TIn, TOut>(QueryPage<TIn> page, Func<TIn, TOut> projection) =>
        new(page.Items.Select(projection).ToArray(), page.TotalCount, page.NextCursor, Omission(page.Omission));

    public static McpReadResult<T> Read<T>(T item) => new(item, true, null);

    public static McpOmission? Omission(QueryOmission? omission) => omission is null
        ? null
        : new McpOmission(omission.Reason.ToString(), omission.RemainingCount, omission.Message);

    public static IReadOnlyList<McpOmission> Omissions(IEnumerable<QueryOmission> omissions) =>
        omissions.Select(omission => new McpOmission(
            omission.Reason.ToString(), omission.RemainingCount, omission.Message)).ToArray();

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
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl))
            throw new ArgumentException("A project path must be non-empty and free of control characters.", nameof(path));

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
