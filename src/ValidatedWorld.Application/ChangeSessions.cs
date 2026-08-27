using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using ValidatedWorld.Core;
using ValidatedWorld.Serialization;
using ValidatedWorld.Validation;

namespace ValidatedWorld.Application;

public enum ChangeSessionErrorCode
{
    ProjectMismatch,
    SessionNotFound,
    SessionAlreadyActive,
    SessionIdCollision,
    StaleBaseFingerprint,
    StaleOperationFingerprint,
    StaleProposalFingerprint,
    StaleAffectedFingerprint,
    StaleReviewFingerprint,
}

public sealed class ChangeSessionException : InvalidOperationException
{
    public ChangeSessionException(ChangeSessionErrorCode code, string message)
        : base(message) => Code = code;

    public ChangeSessionErrorCode Code { get; }
}

public sealed record ChangeSessionLocator(ProjectId ProjectId, string SessionId);

public sealed record ChangeSessionReference(
    ProjectId ProjectId,
    string SessionId,
    string BaseFingerprint,
    string OperationFingerprint,
    string ProposedFingerprint,
    string AffectedFingerprint,
    string ReviewFingerprint);

public sealed record ChangeReviewUpdate(
    IReadOnlyList<ReviewDisposition> Dispositions,
    IReadOnlyList<EntityId> PresentedContextNodeIds)
{
    public ChangeReviewUpdate(
        IEnumerable<ReviewDisposition>? dispositions = null,
        IEnumerable<EntityId>? presentedContextNodeIds = null)
        : this(
            new ReadOnlyCollection<ReviewDisposition>((dispositions ?? []).ToArray()),
            new ReadOnlyCollection<EntityId>((presentedContextNodeIds ?? []).ToArray()))
    {
    }
}

public sealed record ChangeExitWarning(
    ProjectId ProjectId,
    string SessionId,
    string Path,
    int OperationCount,
    int PendingReviewCount,
    string Message);

public sealed record DiscardedChange(ProjectId ProjectId, string SessionId, DateTimeOffset DiscardedUtc);

public enum ChangeWriteStatus
{
    Written,
    ReviewNotReady,
    SemanticReviewBlocked,
    Stale,
    Busy,
    Failed,
}

public sealed record ChangeWriteOptions(bool BypassAiReview = false);

/// <summary>The result of attempting to persist a reviewed in-memory proposal.</summary>
public sealed record ChangeWriteResult(
    ChangeWriteStatus Status,
    ProjectId ProjectId,
    string SessionId,
    StoredProject? Project,
    ProjectStorageErrorCode? StorageErrorCode,
    string Message,
    SemanticReviewResult? SemanticReview,
    bool AiReviewBypassed);

public sealed record ChangeFocusResult(
    GraphOperationBatch ExpandedOperations,
    string OperationFingerprint,
    string ProposedFingerprint);

public sealed class ChangeSessionSnapshot
{
    internal ChangeSessionSnapshot(
        string path,
        string author,
        string intent,
        DateTimeOffset createdUtc,
        DateTimeOffset updatedUtc,
        ChangeSessionReference reference,
        GraphOperationBatch operations,
        ProjectGraph proposedGraph,
        AffectedAnalysis affected,
        IReadOnlyList<ReviewDisposition> dispositions,
        IReadOnlyList<EntityId> presentedContextNodeIds,
        ReviewReadinessResult readiness,
        ReviewRefreshResult? refresh,
        SemanticReviewResult? semanticReview)
    {
        Path = path;
        Author = author;
        Intent = intent;
        CreatedUtc = createdUtc;
        UpdatedUtc = updatedUtc;
        Reference = reference;
        Operations = operations;
        ProposedGraph = proposedGraph;
        Affected = affected;
        Dispositions = new ReadOnlyCollection<ReviewDisposition>(dispositions.ToArray());
        PresentedContextNodeIds = new ReadOnlyCollection<EntityId>(presentedContextNodeIds.ToArray());
        Readiness = readiness;
        Refresh = refresh;
        SemanticReview = semanticReview;
    }

    public string Path { get; }

    public string Author { get; }

    public string Intent { get; }

    public DateTimeOffset CreatedUtc { get; }

    public DateTimeOffset UpdatedUtc { get; }

    public ChangeSessionReference Reference { get; }

    public GraphOperationBatch Operations { get; }

    public ProjectGraph ProposedGraph { get; }

    public AffectedAnalysis Affected { get; }

    public IReadOnlyList<ReviewDisposition> Dispositions { get; }

    public IReadOnlyList<EntityId> PresentedContextNodeIds { get; }

    public ReviewReadinessResult Readiness { get; }

    public ReviewRefreshResult? Refresh { get; }

    public SemanticReviewResult? SemanticReview { get; }
}

public sealed partial class ProjectApplication
{
    private readonly object _sessionLock = new();
    private readonly Dictionary<ProjectId, ActiveChangeSession> _activeSessions = [];

    public ChangeSessionSnapshot BeginChange(
        string path,
        ProjectId expectedProjectId,
        string author,
        string intent)
    {
        author = ValidateSessionText(author, nameof(author));
        intent = ValidateSessionText(intent, nameof(intent));
        var project = _store.Load(path);
        if (project.Graph.ProjectId != expectedProjectId)
        {
            throw new ChangeSessionException(
                ChangeSessionErrorCode.ProjectMismatch,
                $"Project '{project.Graph.ProjectId.Value}' does not match expected project '{expectedProjectId.Value}'.");
        }

        var calculatedBase = GraphFingerprints.State(project.Graph);
        if (!StringComparer.Ordinal.Equals(calculatedBase, project.StateFingerprint))
        {
            throw new ChangeSessionException(
                ChangeSessionErrorCode.StaleBaseFingerprint,
                "The loaded graph does not match its verified state fingerprint.");
        }

        lock (_sessionLock)
        {
            if (_activeSessions.ContainsKey(expectedProjectId))
            {
                throw new ChangeSessionException(
                    ChangeSessionErrorCode.SessionAlreadyActive,
                    $"Project '{expectedProjectId.Value}' already has an unresolved in-memory change session.");
            }

            var sessionId = ValidateSessionId(_sessionIdFactory());
            if (_activeSessions.Values.Any(session =>
                    StringComparer.Ordinal.Equals(session.SessionId, sessionId)))
            {
                throw new ChangeSessionException(
                    ChangeSessionErrorCode.SessionIdCollision,
                    $"Session ID '{sessionId}' is already active.");
            }

            var now = UtcNow();
            var projection = new GraphProjector().Project(project.Graph, GraphOperationBatch.Empty);
            var affected = new AffectedAnalyzer().Analyze(project.Graph, projection);
            var state = new ActiveChangeSession(
                project,
                sessionId,
                author,
                intent,
                now,
                projection,
                affected,
                affected.CreateReviewSession());
            _activeSessions.Add(expectedProjectId, state);
            return Snapshot(state, refresh: null);
        }
    }

    public ChangeSessionSnapshot ShowChange(ChangeSessionLocator locator)
    {
        ArgumentNullException.ThrowIfNull(locator);
        lock (_sessionLock)
        {
            return Snapshot(Find(locator), refresh: null);
        }
    }

    public ChangeFocusResult FocusChange(
        ChangeSessionReference reference,
        GraphOperationBatch operations,
        IEnumerable<ScopeParentSelection> scopeParents)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(scopeParents);
        lock (_sessionLock)
        {
            var state = FindAndVerify(reference);
            VerifyBaseUnchanged(state);
            var expanded = GraphOperationFocus.ExpandScopeParents(
                state.BaseProject.Graph,
                operations,
                scopeParents);
            var projection = new GraphProjector().Project(state.BaseProject.Graph, expanded);
            return new ChangeFocusResult(
                expanded,
                GraphFingerprints.Operations(state.BaseProject.StateFingerprint, expanded),
                GraphFingerprints.Proposed(projection.Graph));
        }
    }

    public ChangeSessionSnapshot ApplyChange(
        ChangeSessionReference reference,
        GraphOperationBatch operations,
        AffectedAnalysisOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(operations);
        lock (_sessionLock)
        {
            var state = FindAndVerify(reference);
            VerifyBaseUnchanged(state);
            var projection = new GraphProjector().Project(state.BaseProject.Graph, operations);
            var affected = new AffectedAnalyzer().Analyze(state.BaseProject.Graph, projection, options);
            var refresh = state.Review.Refresh(affected);
            state.Projection = projection;
            state.Affected = affected;
            state.UpdatedUtc = UtcNow();
            return Snapshot(state, refresh);
        }
    }

    public ChangeSessionSnapshot ExpandChange(
        ChangeSessionReference reference,
        AffectedAnalysisOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        lock (_sessionLock)
        {
            var state = FindAndVerify(reference);
            VerifyBaseUnchanged(state);
            var affected = new AffectedAnalyzer().Analyze(state.BaseProject.Graph, state.Projection, options);
            var refresh = state.Review.Refresh(affected);
            state.Affected = affected;
            state.UpdatedUtc = UtcNow();
            return Snapshot(state, refresh);
        }
    }

    public AffectedAnalysis GetAffected(ChangeSessionLocator locator)
    {
        ArgumentNullException.ThrowIfNull(locator);
        lock (_sessionLock)
        {
            return Find(locator).Affected;
        }
    }

    public ChangeSessionSnapshot ReviewChange(ChangeSessionReference reference, ChangeReviewUpdate update)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(update);
        lock (_sessionLock)
        {
            var state = FindAndVerify(reference);
            VerifyBaseUnchanged(state);
            VerifyReviewUpdate(state, update);
            foreach (var disposition in update.Dispositions)
            {
                state.Review.SetDisposition(disposition.NodeId, disposition.Kind, disposition.Rationale);
            }

            foreach (var nodeId in update.PresentedContextNodeIds)
            {
                state.Review.MarkContextPresented(nodeId);
            }

            state.UpdatedUtc = UtcNow();
            return Snapshot(state, refresh: null);
        }
    }

    public ChangeSessionSnapshot ValidateChange(ChangeSessionReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        lock (_sessionLock)
        {
            var state = FindAndVerify(reference);
            VerifyBaseUnchanged(state);
            return Snapshot(state, refresh: null);
        }
    }

    public async Task<ChangeWriteResult> WriteChangeAsync(
        ChangeSessionReference reference,
        ChangeWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        options ??= new ChangeWriteOptions();
        var reviewEnabled = SemanticReviewAvailability.Enabled;
        var bypassUsed = reviewEnabled && options.BypassAiReview;
        lock (_sessionLock)
        {
            var state = FindAndVerify(reference);
            var readiness = state.Review.EvaluateReadiness();
            if (!readiness.IsReady)
            {
                return new ChangeWriteResult(
                    ChangeWriteStatus.ReviewNotReady,
                    state.BaseProject.Graph.ProjectId,
                    state.SessionId,
                    null,
                    null,
                    string.Join(" ", readiness.Blockers),
                    CurrentSemanticReview(state),
                    false);
            }

            if (reviewEnabled && !bypassUsed)
            {
                try
                {
                    VerifyBaseUnchanged(state);
                }
                catch (ChangeSessionException exception) when (
                    exception.Code is ChangeSessionErrorCode.StaleBaseFingerprint or
                        ChangeSessionErrorCode.ProjectMismatch)
                {
                    return new ChangeWriteResult(
                        ChangeWriteStatus.Stale,
                        state.BaseProject.Graph.ProjectId,
                        state.SessionId,
                        null,
                        null,
                        exception.Message,
                        CurrentSemanticReview(state),
                        false);
                }
                catch (ProjectStorageException exception)
                {
                    return new ChangeWriteResult(
                        ChangeWriteStatus.Failed,
                        state.BaseProject.Graph.ProjectId,
                        state.SessionId,
                        null,
                        exception.Code,
                        exception.Message,
                        CurrentSemanticReview(state),
                        false);
                }
            }
        }

        SemanticReviewResult? semanticReview;
        if (reviewEnabled && !bypassUsed)
        {
            semanticReview = await ReviewSemanticsForWriteAsync(reference, cancellationToken);
            if (!semanticReview.IsCurrent)
            {
                return new ChangeWriteResult(
                    ChangeWriteStatus.Stale,
                    reference.ProjectId,
                    reference.SessionId,
                    null,
                    null,
                    "The proposal changed while semantic review was running.",
                    semanticReview,
                    false);
            }

            if (!semanticReview.AllowsWrite)
            {
                return new ChangeWriteResult(
                    ChangeWriteStatus.SemanticReviewBlocked,
                    reference.ProjectId,
                    reference.SessionId,
                    null,
                    null,
                    semanticReview.Summary,
                    semanticReview,
                    false);
            }
        }
        else
        {
            lock (_sessionLock)
            {
                semanticReview = CurrentSemanticReview(FindAndVerify(reference));
            }
        }

        lock (_sessionLock)
        {
            var state = FindAndVerify(reference);
            var readiness = state.Review.EvaluateReadiness();
            if (!readiness.IsReady)
            {
                return new ChangeWriteResult(
                    ChangeWriteStatus.ReviewNotReady,
                    state.BaseProject.Graph.ProjectId,
                    state.SessionId,
                    null,
                    null,
                    string.Join(" ", readiness.Blockers),
                    semanticReview,
                    false);
            }

            var write = _store.Write(new ProjectWriteRequest(
                state.BaseProject.Path,
                state.BaseProject.Graph.ProjectId,
                state.BaseProject.StateFingerprint,
                state.Projection.Operations,
                GraphFingerprints.Proposed(state.Projection.Graph)));
            var result = new ChangeWriteResult(
                write.Outcome switch
                {
                    ProjectWriteOutcome.Written => ChangeWriteStatus.Written,
                    ProjectWriteOutcome.Stale => ChangeWriteStatus.Stale,
                    ProjectWriteOutcome.Busy => ChangeWriteStatus.Busy,
                    _ => ChangeWriteStatus.Failed,
                },
                state.BaseProject.Graph.ProjectId,
                state.SessionId,
                write.Project,
                write.ErrorCode,
                write.Message,
                semanticReview,
                bypassUsed);
            if (result.Status == ChangeWriteStatus.Written)
            {
                _activeSessions.Remove(state.BaseProject.Graph.ProjectId);
            }

            return result;
        }
    }

    public DiscardedChange DiscardChange(ChangeSessionReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        lock (_sessionLock)
        {
            var state = FindAndVerify(reference);
            _activeSessions.Remove(state.BaseProject.Graph.ProjectId);
            return new DiscardedChange(
                state.BaseProject.Graph.ProjectId,
                state.SessionId,
                UtcNow());
        }
    }

    public IReadOnlyList<ChangeExitWarning> GetExitWarnings()
    {
        lock (_sessionLock)
        {
            return new ReadOnlyCollection<ChangeExitWarning>(_activeSessions.Values
                .OrderBy(state => state.BaseProject.Graph.ProjectId)
                .Select(state => new ChangeExitWarning(
                    state.BaseProject.Graph.ProjectId,
                    state.SessionId,
                    state.BaseProject.Path,
                    state.Projection.Operations.Operations.Count,
                    state.Review.PendingNodeIds.Count,
                    "Exiting now will permanently lose this unresolved in-memory change session."))
                .ToArray());
        }
    }

    private ActiveChangeSession Find(ChangeSessionLocator locator)
    {
        if (_activeSessions.TryGetValue(locator.ProjectId, out var state))
        {
            if (StringComparer.Ordinal.Equals(state.SessionId, locator.SessionId)) return state;
            throw new ChangeSessionException(
                ChangeSessionErrorCode.SessionNotFound,
                $"Session '{locator.SessionId}' is not active for project '{locator.ProjectId.Value}'.");
        }

        if (_activeSessions.Values.Any(session =>
                StringComparer.Ordinal.Equals(session.SessionId, locator.SessionId)))
        {
            throw new ChangeSessionException(
                ChangeSessionErrorCode.ProjectMismatch,
                $"Session '{locator.SessionId}' belongs to a different project.");
        }

        throw new ChangeSessionException(
            ChangeSessionErrorCode.SessionNotFound,
            $"Session '{locator.SessionId}' is not active.");
    }

    private ActiveChangeSession FindAndVerify(ChangeSessionReference reference)
    {
        var state = Find(new ChangeSessionLocator(reference.ProjectId, reference.SessionId));
        var current = BuildReference(state);
        EnsureEqual(
            reference.BaseFingerprint,
            current.BaseFingerprint,
            ChangeSessionErrorCode.StaleBaseFingerprint,
            "base state");
        EnsureEqual(
            reference.OperationFingerprint,
            current.OperationFingerprint,
            ChangeSessionErrorCode.StaleOperationFingerprint,
            "operation batch");
        EnsureEqual(
            reference.ProposedFingerprint,
            current.ProposedFingerprint,
            ChangeSessionErrorCode.StaleProposalFingerprint,
            "proposed graph");
        EnsureEqual(
            reference.AffectedFingerprint,
            current.AffectedFingerprint,
            ChangeSessionErrorCode.StaleAffectedFingerprint,
            "affected analysis");
        EnsureEqual(
            reference.ReviewFingerprint,
            current.ReviewFingerprint,
            ChangeSessionErrorCode.StaleReviewFingerprint,
            "review state");
        return state;
    }

    private void VerifyBaseUnchanged(ActiveChangeSession state)
    {
        var current = _store.Load(state.BaseProject.Path);
        if (current.Graph.ProjectId != state.BaseProject.Graph.ProjectId)
        {
            throw new ChangeSessionException(
                ChangeSessionErrorCode.ProjectMismatch,
                "The project file now contains a different project identity.");
        }

        if (!StringComparer.Ordinal.Equals(current.StateFingerprint, state.BaseProject.StateFingerprint))
        {
            throw new ChangeSessionException(
                ChangeSessionErrorCode.StaleBaseFingerprint,
                "The canonical project changed after this session began; discard it and begin again.");
        }
    }

    private static void VerifyReviewUpdate(ActiveChangeSession state, ChangeReviewUpdate update)
    {
        var duplicateDisposition = update.Dispositions
            .GroupBy(disposition => disposition.NodeId)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateDisposition is not null)
        {
            throw new ArgumentException(
                $"Node '{duplicateDisposition.Key.Value}' has more than one disposition update.",
                nameof(update));
        }

        var duplicateContext = update.PresentedContextNodeIds
            .GroupBy(id => id)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateContext is not null)
        {
            throw new ArgumentException(
                $"Context node '{duplicateContext.Key.Value}' is presented more than once.",
                nameof(update));
        }

        var affected = state.Affected.AffectedNodes.ToDictionary(node => node.NodeId);
        foreach (var disposition in update.Dispositions)
        {
            if (!Enum.IsDefined(disposition.Kind) || !affected.TryGetValue(disposition.NodeId, out var node))
            {
                throw new ArgumentException(
                    $"Node '{disposition.NodeId.Value}' is not an affected node with a valid disposition.",
                    nameof(update));
            }

            if (disposition.Kind == ReviewDispositionKind.Updated && !node.IsDirectChange)
            {
                throw new ArgumentException("Only directly changed nodes may be marked Updated.", nameof(update));
            }

            if (disposition.Kind == ReviewDispositionKind.NotApplicable &&
                string.IsNullOrWhiteSpace(disposition.Rationale))
            {
                throw new ArgumentException("Not-applicable requires a rationale.", nameof(update));
            }
        }

        var contextIds = state.Affected.ScopeContext.Select(entry => entry.NodeId).ToHashSet();
        foreach (var contextId in update.PresentedContextNodeIds)
        {
            if (!contextIds.Contains(contextId))
            {
                throw new ArgumentException(
                    $"Node '{contextId.Value}' is not required context for the current proposal.",
                    nameof(update));
            }
        }
    }

    private static ChangeSessionSnapshot Snapshot(ActiveChangeSession state, ReviewRefreshResult? refresh)
    {
        return new ChangeSessionSnapshot(
            state.BaseProject.Path,
            state.Author,
            state.Intent,
            state.CreatedUtc,
            state.UpdatedUtc,
            BuildReference(state),
            state.Projection.Operations,
            state.Projection.Graph,
            state.Affected,
            state.Review.Dispositions,
            state.Review.PresentedContextNodeIds,
            state.Review.EvaluateReadiness(),
            refresh,
            CurrentSemanticReview(state));
    }

    private static SemanticReviewResult? CurrentSemanticReview(ActiveChangeSession state)
    {
        if (state.SemanticReview is null) return null;
        return state.SemanticReview with
        {
            IsCurrent = SameBinding(state.SemanticReview.Binding, BuildReference(state)),
        };
    }

    private static ChangeSessionReference BuildReference(ActiveChangeSession state)
    {
        var operation = GraphFingerprints.Operations(
            state.BaseProject.StateFingerprint,
            state.Projection.Operations);
        var proposed = GraphFingerprints.Proposed(state.Projection.Graph);
        var affected = GraphFingerprints.Affected(state.Affected);
        var disposition = GraphFingerprints.Dispositions(state.Affected, state.Review.Dispositions);
        var review = ReviewStateFingerprint(disposition, state.Review.PresentedContextNodeIds);
        return new ChangeSessionReference(
            state.BaseProject.Graph.ProjectId,
            state.SessionId,
            state.BaseProject.StateFingerprint,
            operation,
            proposed,
            affected,
            review);
    }

    private static string ReviewStateFingerprint(string disposition, IEnumerable<EntityId> contextIds)
    {
        var text = new StringBuilder(disposition);
        foreach (var id in contextIds.OrderBy(id => id))
        {
            text.Append('|').Append(id.Value.Length).Append(':').Append(id.Value);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    private DateTimeOffset UtcNow() => _utcNow().ToUniversalTime();

    private static string ValidateSessionId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > GraphLimits.IdentifierMaxLength ||
            value.Any(char.IsControl))
        {
            throw new ArgumentException("A generated session ID must be a bounded nonempty value.", nameof(value));
        }

        return value;
    }

    private static string ValidateSessionText(string? value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value) || value.Length > GraphLimits.TextMaxLength || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"Session {parameterName} must be nonempty, contain no control characters, and fit the text bound.",
                parameterName);
        }

        return value;
    }

    private static void EnsureEqual(
        string expected,
        string actual,
        ChangeSessionErrorCode code,
        string description)
    {
        if (!StringComparer.Ordinal.Equals(expected, actual))
        {
            throw new ChangeSessionException(code, $"The supplied {description} fingerprint is stale.");
        }
    }

    private sealed class ActiveChangeSession
    {
        public ActiveChangeSession(
            StoredProject baseProject,
            string sessionId,
            string author,
            string intent,
            DateTimeOffset createdUtc,
            GraphProjectionResult projection,
            AffectedAnalysis affected,
            AffectedReviewSession review)
        {
            BaseProject = baseProject;
            SessionId = sessionId;
            Author = author;
            Intent = intent;
            CreatedUtc = createdUtc;
            UpdatedUtc = createdUtc;
            Projection = projection;
            Affected = affected;
            Review = review;
        }

        public StoredProject BaseProject { get; }

        public string SessionId { get; }

        public string Author { get; }

        public string Intent { get; }

        public DateTimeOffset CreatedUtc { get; }

        public DateTimeOffset UpdatedUtc { get; set; }

        public GraphProjectionResult Projection { get; set; }

        public AffectedAnalysis Affected { get; set; }

        public AffectedReviewSession Review { get; }

        public SemanticReviewResult? SemanticReview { get; set; }
    }
}
