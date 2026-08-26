using ValidatedWorld.Core;
using ValidatedWorld.Persistence.Sqlite;
using ValidatedWorld.Serialization;
using ValidatedWorld.Validation;

namespace ValidatedWorld.Application.Tests;

public sealed class ApplicationQueryAndSessionTests
{
    private static readonly DateTimeOffset FixedUtc =
        new(2026, 8, 26, 16, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Queries_are_bounded_cursor_stable_and_search_is_deterministic()
    {
        using var workspace = new TestWorkspace();
        var application = CreateApplication(workspace, out var path);
        var queries = application.Queries(path, new ProjectId(SampleProjectCatalog.TechnicalProject));

        var first = queries.ListNodes(new QueryPageRequest(2));
        var second = queries.ListNodes(new QueryPageRequest(2, first.NextCursor));
        var search = queries.Search("POWER", new QueryPageRequest(10));

        Assert.Equal(new[] { "battery-assumption", "design-anchor" }, first.Items.Select(node => node.Id.Value));
        Assert.Equal(7, first.TotalCount);
        Assert.Equal(5, first.Omission!.RemainingCount);
        Assert.NotNull(first.NextCursor);
        Assert.Equal(new[] { "purpose", "retention-policy" }, second.Items.Select(node => node.Id.Value));
        Assert.Equal(new[] { "scope-power", "scope-power-parent" },
            search.Items.Select(hit => hit.EntityId.Value));
        Assert.Equal(new[] { GraphEntityKind.Node, GraphEntityKind.Edge },
            search.Items.Select(hit => hit.EntityKind));
        Assert.Equal("Power behavior", queries.GetNode(new EntityId("scope-power")).Text);
        Assert.Equal(
            "requires",
            queries.GetEdge(new EntityId("battery-requires-test")).Relationship);

        var cursorError = Assert.Throws<ProjectQueryException>(() =>
            queries.ListEdges(new QueryPageRequest(2, first.NextCursor)));
        Assert.Equal(ProjectQueryErrorCode.InvalidCursor, cursorError.Code);
        Assert.Equal(
            ProjectQueryErrorCode.ProjectMismatch,
            Assert.Throws<ProjectQueryException>(() =>
                application.Queries(path, new ProjectId("other-project"))).Code);
    }

    [Fact]
    public void Scope_neighbor_dependency_path_and_context_queries_exclude_unrelated_siblings()
    {
        using var workspace = new TestWorkspace();
        var application = CreateApplication(workspace, out var path);
        var queries = application.Queries(path);
        var battery = new EntityId("battery-assumption");

        var scope = queries.GetScope(new EntityId("scope-power"), new QueryPageRequest(10));
        var neighbors = queries.GetNeighbors(battery);
        var dependencies = queries.GetDependencies(battery);
        var pathResult = queries.FindDependencyPath(battery, new EntityId("runtime-test"));
        var context = queries.GetContext([battery, new EntityId("retention-policy")]);

        Assert.Equal(new[] { "purpose" }, scope.Upstream.Select(node => node.Id.Value));
        Assert.Equal(
            new[] { "battery-assumption", "design-anchor", "runtime-test" },
            scope.Descendants.Items.Select(node => node.Id.Value));
        Assert.DoesNotContain(scope.Descendants.Items, node => node.Id.Value == "scope-privacy");
        Assert.Equal(
            new[] { "runtime-test", "scope-power" },
            neighbors.Items.Select(entry => entry.NodeId.Value));
        Assert.Single(dependencies.Items);
        Assert.True(dependencies.Items[0].IsOutgoing);
        Assert.True(pathResult.Found);
        Assert.Equal(new[] { "battery-assumption", "runtime-test" }, pathResult.Nodes.Select(id => id.Value));
        Assert.Equal(new[] { "battery-requires-test" }, pathResult.Edges.Select(id => id.Value));
        Assert.Equal(
            new[] { "battery-assumption", "purpose", "retention-policy", "scope-power", "scope-privacy" },
            context.ContextNodes.Select(node => node.Id.Value));
        Assert.DoesNotContain(context.ContextNodes, node => node.Id.Value is "runtime-test" or "design-anchor");

        var bounded = queries.GetScope(
            new EntityId("purpose"),
            new QueryPageRequest(10),
            new QueryTraversalOptions { MaxDepth = 1, MaxVisitedNodes = 100 });
        Assert.Contains(bounded.Omissions, omission =>
            omission.Reason == QueryOmissionReason.TraversalDepthLimit);
        Assert.Equal(new[] { "scope-power", "scope-privacy" },
            bounded.Descendants.Items.Select(node => node.Id.Value));

        var cancelled = queries.FindDependencyPath(
            battery,
            new EntityId("runtime-test"),
            new QueryTraversalOptions { CancellationToken = new CancellationToken(canceled: true) });
        Assert.False(cancelled.Found);
        Assert.Contains(cancelled.Omissions, omission => omission.Reason == QueryOmissionReason.Cancelled);
    }

    [Fact]
    public void Realistic_change_review_is_process_local_fingerprint_guarded_and_never_writes_sqlite()
    {
        using var workspace = new TestWorkspace();
        var application = CreateApplication(workspace, out var path);
        var bytesBefore = File.ReadAllBytes(path);
        var projectId = new ProjectId(SampleProjectCatalog.TechnicalProject);

        var begun = application.BeginChange(path, projectId, "human", "Revise the battery assumption");
        Assert.Equal(FixedUtc, begun.CreatedUtc);
        Assert.Empty(begun.Operations.Operations);
        Assert.Single(application.GetExitWarnings());
        Assert.Equal(
            ChangeSessionErrorCode.SessionAlreadyActive,
            Assert.Throws<ChangeSessionException>(() =>
                application.BeginChange(path, projectId, "human", "A second proposal")).Code);

        var battery = application.Queries(path).GetNode(new EntityId("battery-assumption"));
        var replacement = new GraphNode(battery.Id, "The battery lasts for a revised target duty cycle", battery.Kind);
        var applied = application.ApplyChange(
            begun.Reference,
            new GraphOperationBatch([GraphOperation.ReplaceNode(replacement)]));

        Assert.Equal(new[] { "battery-assumption", "runtime-test" },
            applied.Affected.AffectedNodes.Select(node => node.NodeId.Value));
        Assert.Equal(new[] { "purpose", "scope-power" },
            applied.Affected.ScopeContext.Select(entry => entry.NodeId.Value));
        Assert.False(applied.Readiness.IsReady);
        Assert.Equal(
            ChangeSessionErrorCode.StaleOperationFingerprint,
            Assert.Throws<ChangeSessionException>(() =>
                application.ValidateChange(begun.Reference)).Code);

        var reviewed = application.ReviewChange(
            applied.Reference,
            new ChangeReviewUpdate(
                [
                    new ReviewDisposition(battery.Id, ReviewDispositionKind.Updated, null),
                    new ReviewDisposition(new EntityId("runtime-test"), ReviewDispositionKind.ReviewedNoChange, null),
                ],
                [new EntityId("purpose"), new EntityId("scope-power")]));
        Assert.True(reviewed.Readiness.IsReady);
        Assert.Empty(reviewed.Readiness.Blockers);
        Assert.Equal(0, application.GetExitWarnings()[0].PendingReviewCount);

        var revisedAgain = new GraphNode(battery.Id, "The battery supports the revised offline duty cycle", battery.Kind);
        var replacedAgain = application.ApplyChange(
            reviewed.Reference,
            new GraphOperationBatch([GraphOperation.ReplaceNode(revisedAgain)]));
        Assert.Equal(new[] { "battery-assumption" },
            replacedAgain.Refresh!.InvalidatedDispositionNodeIds.Select(id => id.Value));
        Assert.Contains(replacedAgain.Dispositions, disposition =>
            disposition.NodeId.Value == "runtime-test" &&
            disposition.Kind == ReviewDispositionKind.ReviewedNoChange);
        Assert.False(replacedAgain.Readiness.IsReady);

        Assert.Equal(bytesBefore, File.ReadAllBytes(path));
        var independentProcess = new ProjectApplication(new SqliteProjectStore(() => FixedUtc));
        Assert.Empty(independentProcess.GetExitWarnings());
        Assert.Equal(revisedAgain, application.ShowChange(
            new ChangeSessionLocator(projectId, "session-1")).ProposedGraph.Nodes.Single(node => node.Id == battery.Id));
        Assert.Equal(
            ChangeSessionErrorCode.ProjectMismatch,
            Assert.Throws<ChangeSessionException>(() => application.ShowChange(
                new ChangeSessionLocator(new ProjectId("wrong-project"), "session-1"))).Code);

        var discarded = application.DiscardChange(replacedAgain.Reference);
        Assert.Equal("session-1", discarded.SessionId);
        Assert.Empty(application.GetExitWarnings());
        Assert.Equal(
            ChangeSessionErrorCode.SessionNotFound,
            Assert.Throws<ChangeSessionException>(() => application.ShowChange(
                new ChangeSessionLocator(projectId, "session-1"))).Code);
        Assert.Equal(bytesBefore, File.ReadAllBytes(path));
    }

    [Fact]
    public void Focus_expand_wrong_session_and_stale_reference_paths_are_explicit()
    {
        using var workspace = new TestWorkspace();
        var application = CreateApplication(workspace, out var path);
        var projectId = new ProjectId(SampleProjectCatalog.TechnicalProject);
        var begun = application.BeginChange(path, projectId, "human", "Add a power requirement");
        var added = new GraphNode(new EntityId("power-budget"), "The design has an explicit power budget", "requirement");

        var focus = application.FocusChange(
            begun.Reference,
            new GraphOperationBatch([GraphOperation.AddNode(added)]),
            [new ScopeParentSelection(added.Id, new EntityId("scope-power"), new EntityId("power-budget-parent"))]);
        Assert.Equal(2, focus.ExpandedOperations.Operations.Count);
        Assert.Empty(application.ShowChange(
            new ChangeSessionLocator(projectId, "session-1")).Operations.Operations);

        var applied = application.ApplyChange(begun.Reference, focus.ExpandedOperations);
        var bounded = application.ExpandChange(
            applied.Reference,
            new AffectedAnalysisOptions { MaxAffectedNodes = 100, MaxOutputItems = 1 });
        Assert.True(bounded.Affected.IsInconclusive);
        Assert.False(bounded.Readiness.IsReady);
        var complete = application.ExpandChange(bounded.Reference);
        Assert.True(complete.Affected.IsComplete);

        var staleProposal = complete.Reference with { ProposedFingerprint = new string('0', 64) };
        Assert.Equal(
            ChangeSessionErrorCode.StaleProposalFingerprint,
            Assert.Throws<ChangeSessionException>(() => application.ValidateChange(staleProposal)).Code);
        Assert.Equal(
            ChangeSessionErrorCode.SessionNotFound,
            Assert.Throws<ChangeSessionException>(() => application.GetAffected(
                new ChangeSessionLocator(projectId, "missing"))).Code);
    }

    [Fact]
    public void Canonical_state_change_makes_an_active_session_explicitly_stale()
    {
        var graph = SampleProjectCatalog.Create(SampleProjectCatalog.TechnicalProject);
        var store = new MutableStore(graph);
        var application = new ProjectApplication(
            store,
            utcNow: () => FixedUtc,
            sessionIdFactory: () => "session-stale");
        var begun = application.BeginChange("memory.vw.db", graph.ProjectId, "human", "Check stale state");
        var changed = new ProjectGraph(
            graph.ProjectId,
            "Externally changed title",
            graph.PurposeNodeId,
            graph.Nodes,
            graph.Edges);
        store.SetGraph(changed);

        var exception = Assert.Throws<ChangeSessionException>(() =>
            application.ValidateChange(begun.Reference));
        Assert.Equal(ChangeSessionErrorCode.StaleBaseFingerprint, exception.Code);
        Assert.Single(application.GetExitWarnings());
    }

    private static ProjectApplication CreateApplication(TestWorkspace workspace, out string path)
    {
        var store = new SqliteProjectStore(() => FixedUtc);
        var application = new ProjectApplication(
            store,
            utcNow: () => FixedUtc,
            sessionIdFactory: () => "session-1");
        path = workspace.PathFor("project with spaces", "Technical Project.vw.db");
        application.CreateSample(SampleProjectCatalog.TechnicalProject, path);
        return application;
    }

    private sealed class TestWorkspace : IDisposable
    {
        public TestWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), $"ValidatedWorld-T7-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string PathFor(params string[] segments)
        {
            var path = segments.Aggregate(Root, Path.Combine);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class MutableStore : IProjectStore
    {
        private StoredProject _project;

        public MutableStore(ProjectGraph graph) => _project = Stored(graph);

        public void SetGraph(ProjectGraph graph) => _project = Stored(graph);

        private static StoredProject Stored(ProjectGraph graph) => new(
            "memory.vw.db",
            graph,
            GraphFingerprints.State(graph),
            FixedUtc,
            FixedUtc);

        public StoredProject Load(string path) => _project;

        public StoredProject Initialize(string path, ProjectGraph graph) => throw new NotSupportedException();

        public ProjectStatus GetStatus(string path) => throw new NotSupportedException();

        public ProjectVerification Verify(string path) => throw new NotSupportedException();

        public StoredProject Backup(string sourcePath, string destinationPath) => throw new NotSupportedException();

        public ProjectWriteResult Write(ProjectWriteRequest request) => throw new NotSupportedException();
    }
}
