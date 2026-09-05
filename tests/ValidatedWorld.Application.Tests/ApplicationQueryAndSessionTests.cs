using System.Diagnostics;
using ValidatedWorld.Core;
using ValidatedWorld.Persistence.Sqlite;
using ValidatedWorld.Serialization;
using ValidatedWorld.Validation;
using Xunit.Abstractions;

namespace ValidatedWorld.Application.Tests;

public sealed class ApplicationQueryAndSessionTests
{
    private readonly ITestOutputHelper _output;

    public ApplicationQueryAndSessionTests(ITestOutputHelper output) => _output = output;

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

        Assert.Equal(new[] { "accessibility-acceptance", "battery-assumption" }, first.Items.Select(node => node.Id.Value));
        Assert.Equal(13, first.TotalCount);
        Assert.Equal(11, first.Omission!.RemainingCount);
        Assert.NotNull(first.NextCursor);
        Assert.Equal(new[] { "power-design-anchor", "privacy-architecture" }, second.Items.Select(node => node.Id.Value));
        Assert.Equal(new[]
            {
                "battery-informs-power-anchor", "power-anchor-scope-parent", "power-design-anchor", "scope-power",
                "scope-power-parent",
            },
            search.Items.Select(hit => hit.EntityId.Value));
        Assert.Equal(new[]
            {
                GraphEntityKind.Edge, GraphEntityKind.Edge, GraphEntityKind.Node, GraphEntityKind.Node,
                GraphEntityKind.Edge,
            },
            search.Items.Select(hit => hit.EntityKind));
        Assert.Equal("Power behavior", queries.GetNode(new EntityId("scope-power")).Text);
        Assert.Equal(
            "requires",
            queries.GetEdge(new EntityId("battery-requires-runtime")).Relationship);

        var cursorError = Assert.Throws<ProjectQueryException>(() =>
            queries.ListEdges(new QueryPageRequest(2, first.NextCursor)));
        Assert.Equal(ProjectQueryErrorCode.InvalidCursor, cursorError.Code);
        Assert.Equal(
            ProjectQueryErrorCode.ProjectMismatch,
            Assert.Throws<ProjectQueryException>(() =>
                application.Queries(path, new ProjectId("other-project"))).Code);
    }

    [Fact]
    public void Exact_tag_search_is_case_sensitive_entity_complete_and_cursor_bound()
    {
        var graph = SampleProjectCatalog.Create(SampleProjectCatalog.TechnicalProject);
        var edge = graph.Edges.Single(candidate => candidate.Id.Value == "battery-requires-runtime");
        var taggedEdge = new GraphEdge(
            edge.Id,
            edge.Source,
            edge.Target,
            edge.Relationship,
            edge.ReviewDirection,
            edge.Rationale,
            ["artifact"]);
        var taggedGraph = new ProjectGraph(
            graph.ProjectId,
            graph.Title,
            graph.PurposeNodeId,
            graph.Nodes,
            graph.Edges.Where(candidate => candidate.Id != edge.Id).Append(taggedEdge));
        var application = new ProjectApplication(new MutableStore(taggedGraph));
        var queries = application.Queries("memory.vw.db");

        var first = queries.SearchByTag("artifact", new QueryPageRequest(2));
        var second = queries.SearchByTag("artifact", new QueryPageRequest(2, first.NextCursor));

        Assert.Equal(3, first.TotalCount);
        Assert.Equal(new[] { "battery-requires-runtime", "power-design-anchor" },
            first.Items.Select(hit => hit.EntityId.Value));
        Assert.Equal(new[] { GraphEntityKind.Edge, GraphEntityKind.Node },
            first.Items.Select(hit => hit.EntityKind));
        Assert.Equal(new[] { "privacy-documentation" }, second.Items.Select(hit => hit.EntityId.Value));
        Assert.Empty(queries.SearchByTag("Artifact").Items);
        Assert.Equal(
            ProjectQueryErrorCode.InvalidCursor,
            Assert.Throws<ProjectQueryException>(() =>
                queries.Search("artifact", new QueryPageRequest(2, first.NextCursor))).Code);
        Assert.Throws<ArgumentException>(() => queries.SearchByTag("  "));
    }

    [Fact]
    public void Ranked_search_explains_exact_ids_tags_phrases_tokens_and_metadata()
    {
        var graph = SampleProjectCatalog.Create(SampleProjectCatalog.TechnicalProject);
        var node = new GraphNode(
            new EntityId("maintenance-note"),
            "Inspect the power enclosure before maintenance",
            "note",
            ["operations"],
            [new KeyValuePair<string, GraphValue>("owner", GraphValue.FromText("Power Team"))]);
        var expanded = new ProjectGraph(
            graph.ProjectId,
            graph.Title,
            graph.PurposeNodeId,
            graph.Nodes.Append(node),
            graph.Edges.Append(new GraphEdge(
                new EntityId("maintenance-note-parent"),
                node.Id,
                new EntityId("scope-power"),
                "scope-parent",
                ReviewDirection.None)));
        var application = new ProjectApplication(new MutableStore(expanded));
        var queries = application.Queries("memory.vw.db");

        var exactId = queries.SearchRanked("maintenance-note");
        Assert.Equal(node.Id, exactId.Items[0].EntityId);
        Assert.Contains(exactId.Items[0].Matches, match =>
            match.Kind == SearchMatchKind.StableId && match.Field == "id");

        var exactTag = queries.SearchRanked("operations");
        Assert.Equal(node.Id, exactTag.Items[0].EntityId);
        Assert.Contains(exactTag.Items[0].Matches, match =>
            match.Kind == SearchMatchKind.ExactTag && match.Field == "tag");

        var phrase = queries.SearchRanked("\"power enclosure\"");
        Assert.Equal(node.Id, phrase.Items[0].EntityId);
        Assert.Contains(phrase.Items[0].Matches, match =>
            match.Kind == SearchMatchKind.Phrase && match.Field == "text" &&
            match.Term == "power enclosure");

        var metadata = queries.SearchRanked("Power Team");
        Assert.Equal(node.Id, metadata.Items[0].EntityId);
        Assert.Contains(metadata.Items[0].Matches, match =>
            match.Kind == SearchMatchKind.Metadata && match.Field == "attribute:owner");

        Assert.Throws<ArgumentException>(() => queries.SearchRanked("\"unclosed"));
    }

    [Fact]
    public void Ranked_search_is_cursor_bound_and_literal_search_contract_is_preserved()
    {
        using var workspace = new TestWorkspace();
        var application = CreateApplication(workspace, out var path);
        var queries = application.Queries(path);

        var ranked = queries.SearchRanked("power", new QueryPageRequest(2));
        Assert.NotNull(ranked.NextCursor);
        Assert.Equal(ranked.Items.Select(hit => hit.EntityId),
            ranked.Items.OrderByDescending(hit => hit.Score).ThenBy(hit => hit.EntityId)
                .Select(hit => hit.EntityId));
        Assert.Equal(
            new[]
            {
                "battery-informs-power-anchor", "power-anchor-scope-parent", "power-design-anchor",
                "scope-power", "scope-power-parent",
            },
            queries.Search("POWER", new QueryPageRequest(10)).Items.Select(hit => hit.EntityId.Value));
        Assert.Equal(
            ProjectQueryErrorCode.InvalidCursor,
            Assert.Throws<ProjectQueryException>(() =>
                queries.SearchRanked("other", new QueryPageRequest(2, ranked.NextCursor))).Code);
    }

    [Fact]
    public void Ranked_search_mechanical_probe_covers_a_larger_realistic_corpus()
    {
        var purpose = new GraphNode(new EntityId("purpose"), "An offline sensor project", "purpose");
        var nodes = Enumerable.Range(1, 5_000)
            .Select(index => new GraphNode(
                new EntityId($"requirement-{index:D4}"),
                $"Subsystem {index % 100:D2} requirement covers the offline sensor retention workflow",
                index % 2 == 0 ? "requirement" : "verification",
                [$"domain:{index % 10:D2}", index % 3 == 0 ? "reviewed" : "planned"],
                [new KeyValuePair<string, GraphValue>("priority", GraphValue.FromInteger(index % 5 + 1))]))
            .ToArray();
        var edges = nodes.Select(node => new GraphEdge(
            new EntityId($"{node.Id.Value}-parent"),
            node.Id,
            purpose.Id,
            "scope-parent",
            ReviewDirection.None)).ToArray();
        var graph = new ProjectGraph(
            new ProjectId("benchmark-project"),
            "Benchmark Project",
            purpose.Id,
            nodes.Prepend(purpose),
            edges);
        var application = new ProjectApplication(new MutableStore(graph));
        var stopwatch = Stopwatch.StartNew();
        var page = application.Queries("memory.vw.db").SearchRanked(
            "offline sensor retention", new QueryPageRequest(25));
        stopwatch.Stop();

        Assert.Equal(5_001, page.TotalCount);
        Assert.Equal(25, page.Items.Count);
        Assert.All(page.Items, hit => Assert.NotEmpty(hit.Matches));
        _output.WriteLine(
            $"ranked-search corpus nodes={graph.Nodes.Count} edges={graph.Edges.Count} " +
            $"results={page.TotalCount} elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F2}");
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
            new[] { "battery-assumption", "power-design-anchor", "runtime-test" },
            scope.Descendants.Items.Select(node => node.Id.Value));
        Assert.DoesNotContain(scope.Descendants.Items, node => node.Id.Value == "scope-privacy");
        Assert.Equal(
            new[] { "power-design-anchor", "runtime-test", "scope-power" },
            neighbors.Items.Select(entry => entry.NodeId.Value));
        Assert.Equal(2, dependencies.Items.Count);
        Assert.All(dependencies.Items, dependency => Assert.True(dependency.IsOutgoing));
        Assert.True(pathResult.Found);
        Assert.Equal(new[] { "battery-assumption", "runtime-test" }, pathResult.Nodes.Select(id => id.Value));
        Assert.Equal(new[] { "battery-requires-runtime" }, pathResult.Edges.Select(id => id.Value));
        Assert.Equal(
            new[] { "battery-assumption", "purpose", "retention-policy", "scope-power", "scope-privacy" },
            context.ContextNodes.Select(node => node.Id.Value));
        Assert.DoesNotContain(context.ContextNodes, node => node.Id.Value is "runtime-test" or "power-design-anchor");

        var bounded = queries.GetScope(
            new EntityId("purpose"),
            new QueryPageRequest(10),
            new QueryTraversalOptions { MaxDepth = 1, MaxVisitedNodes = 100 });
        Assert.Contains(bounded.Omissions, omission =>
            omission.Reason == QueryOmissionReason.TraversalDepthLimit);
        Assert.Equal(new[] { "scope-accessibility", "scope-documentation", "scope-power", "scope-privacy" },
            bounded.Descendants.Items.Select(node => node.Id.Value));

        var cancelled = queries.FindDependencyPath(
            battery,
            new EntityId("runtime-test"),
            new QueryTraversalOptions { CancellationToken = new CancellationToken(canceled: true) });
        Assert.False(cancelled.Found);
        Assert.Contains(cancelled.Omissions, omission => omission.Reason == QueryOmissionReason.Cancelled);
    }

    [Fact]
    public void Graph_observability_reports_scope_quality_dependencies_rationale_and_tag_use()
    {
        using var workspace = new TestWorkspace();
        var application = CreateApplication(workspace, out var path);

        var report = application.Queries(path).GetGraphObservability(new GraphObservabilityOptions
        {
            MaxItems = 1,
        });

        Assert.Equal(13, report.NodeCount);
        Assert.Equal(17, report.EdgeCount);
        Assert.Equal(5, report.SemanticReviewArcCount);
        Assert.Equal(13, report.ScopeCoverage.TotalNodeCount);
        Assert.Equal(12, report.ScopeCoverage.ScopeParentEdgeCount);
        Assert.Equal(12, report.ScopeCoverage.NodesWithExactlyOneScopeParent);
        Assert.Equal(13, report.ScopeCoverage.NodesReachingPurpose);
        Assert.Equal(100, report.ScopeCoverage.CoveragePercent);
        Assert.Equal(0, report.UnreachableNodeIds.TotalCount);
        Assert.Equal(3, report.ReviewFanOutHotspots.TotalCount);
        Assert.Equal("battery-assumption", report.ReviewFanOutHotspots.Items[0].NodeId.Value);
        Assert.Equal(3, report.ReviewFanOutHotspots.OmittedCount + report.ReviewFanOutHotspots.Items.Count);
        Assert.Equal(1, report.SuspiciouslyIsolatedClaims.TotalCount);
        Assert.Equal("accessibility-acceptance", report.SuspiciouslyIsolatedClaims.Items[0].NodeId.Value);
        Assert.Equal(5, report.MissingRationales.TotalCount);
        Assert.Single(report.MissingRationales.Items);
        Assert.Equal(1, report.TagUsage.TotalCount);
        Assert.Equal("artifact", report.TagUsage.Items[0].Tag);
        Assert.Equal(2, report.TagUsage.Items[0].NodeCount);
        Assert.Equal(11, report.UntaggedNodeCount);
        Assert.Equal(17, report.UntaggedEdgeCount);
    }

    [Fact]
    public void Graph_observability_flags_an_orphan_claim_and_never_creates_edges()
    {
        var graph = SampleProjectCatalog.Create(SampleProjectCatalog.TechnicalProject);
        var orphan = new GraphNode(new EntityId("orphan-claim"), "An orphan claim", "claim");
        var orphanGraph = new ProjectGraph(
            graph.ProjectId,
            graph.Title,
            graph.PurposeNodeId,
            graph.Nodes.Append(orphan),
            graph.Edges);
        var application = new ProjectApplication(new MutableStore(orphanGraph));

        var report = application.Queries("memory.vw.db").GetGraphObservability();

        Assert.Contains(new EntityId("orphan-claim"), report.UnreachableNodeIds.Items);
        Assert.Contains(report.SuspiciouslyIsolatedClaims.Items,
            item => item.NodeId == new EntityId("orphan-claim") && item.Kind == "claim");
        Assert.Equal(17, report.EdgeCount);
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

        Assert.Equal(new[] { "battery-assumption", "power-design-anchor", "runtime-test" },
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
                    new ReviewDisposition(new EntityId("power-design-anchor"), ReviewDispositionKind.ReviewedNoChange, null),
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
        Assert.Contains(complete.Affected.AffectedNodes, node =>
            node.NodeId == new EntityId("scope-power") && !node.IsDirectChange);

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
    public void Incremental_patch_accumulates_normalizes_and_can_return_an_entity_to_base()
    {
        using var workspace = new TestWorkspace();
        var application = CreateApplication(workspace, out var path);
        var projectId = new ProjectId(SampleProjectCatalog.TechnicalProject);
        var begun = application.BeginChange(path, projectId, "human", "Accumulate two focused edits");
        var battery = application.Queries(path).GetNode(new EntityId("battery-assumption"));
        var runtime = application.Queries(path).GetNode(new EntityId("runtime-test"));

        var first = application.PatchChange(
            begun.Reference,
            new GraphOperationBatch([GraphOperation.ReplaceNode(new GraphNode(
                battery.Id, "The battery lasts for the incremental target duty cycle", battery.Kind,
                battery.Tags, battery.Attributes.Select(attribute =>
                    new KeyValuePair<string, GraphValue>(attribute.Name, attribute.Value))))]));
        var second = application.PatchChange(
            first.Reference,
            new GraphOperationBatch([GraphOperation.ReplaceNode(new GraphNode(
                runtime.Id, "The runtime test covers the incremental target", runtime.Kind,
                runtime.Tags, runtime.Attributes.Select(attribute =>
                    new KeyValuePair<string, GraphValue>(attribute.Name, attribute.Value))))]));

        Assert.Equal(2, second.Operations.Operations.Count);
        Assert.Contains(second.ProposedGraph.Nodes, node =>
            node.Id == battery.Id && node.Text.Contains("incremental", StringComparison.Ordinal));
        Assert.Contains(second.ProposedGraph.Nodes, node =>
            node.Id == runtime.Id && node.Text.Contains("incremental", StringComparison.Ordinal));

        var normalized = application.PatchChange(
            second.Reference,
            new GraphOperationBatch([GraphOperation.ReplaceNode(battery)]));
        Assert.Single(normalized.Operations.Operations);
        Assert.Equal(runtime.Id, normalized.Operations.Operations[0].EntityId);
        Assert.Equal(battery, normalized.ProposedGraph.Nodes.Single(node => node.Id == battery.Id));
    }

    [Fact]
    public async Task Scope_parent_only_redirect_requires_review_before_write()
    {
        using var workspace = new TestWorkspace();
        var application = CreateApplication(workspace, out var path);
        var projectId = new ProjectId(SampleProjectCatalog.TechnicalProject);
        var begun = application.BeginChange(path, projectId, "human", "Move runtime verification to privacy scope");
        var current = application.Queries(path).GetEdge(new EntityId("runtime-scope-parent"));
        var replacement = new GraphEdge(
            current.Id,
            current.Source,
            new EntityId("scope-privacy"),
            current.Relationship,
            current.ReviewDirection);

        var applied = application.ApplyChange(
            begun.Reference,
            new GraphOperationBatch([GraphOperation.ReplaceEdge(replacement)]));

        Assert.False(applied.Readiness.IsReady);
        Assert.Equal(
            new[] { "runtime-test", "scope-power", "scope-privacy" },
            applied.Affected.AffectedNodes.Select(node => node.NodeId.Value));
        Assert.Equal(
            new[] { "runtime-test", "scope-power", "scope-privacy" },
            applied.Readiness.PendingNodeIds.Select(id => id.Value));
        Assert.Equal(new[] { "purpose" }, applied.Affected.ScopeContext.Select(entry => entry.NodeId.Value));
        Assert.Contains("Affected nodes still have pending review dispositions.", applied.Readiness.Blockers);

        var reviewed = application.ReviewChange(
            applied.Reference,
            new ChangeReviewUpdate(
                applied.Affected.AffectedNodes.Select(node => new ReviewDisposition(
                    node.NodeId,
                    ReviewDispositionKind.ReviewedNoChange,
                    null)).ToArray(),
                [new EntityId("purpose")]));
        Assert.True(reviewed.Readiness.IsReady);

        var written = await application.WriteChangeAsync(reviewed.Reference, new ChangeWriteOptions(true));
        Assert.Equal(ChangeWriteStatus.Written, written.Status);
        Assert.Equal(
            new EntityId("scope-privacy"),
            application.Queries(path).GetEdge(new EntityId("runtime-scope-parent")).Target);
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

        public ProjectSqlExport ExportSql(string path) => throw new NotSupportedException();

        public ProjectWriteResult Write(ProjectWriteRequest request) => throw new NotSupportedException();
    }
}
