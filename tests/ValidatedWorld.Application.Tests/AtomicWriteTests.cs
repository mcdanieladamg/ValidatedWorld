using System.Diagnostics;
using Microsoft.Data.Sqlite;
using ValidatedWorld.Application;
using ValidatedWorld.Core;
using ValidatedWorld.Persistence.Sqlite;
using ValidatedWorld.Validation;

namespace ValidatedWorld.Application.Tests;

public sealed class AtomicWriteTests
{
    private static readonly DateTimeOffset FixedUtc =
        new(2026, 8, 26, 14, 15, 16, TimeSpan.Zero);

    [Fact]
    public void Fully_reviewed_proposal_writes_the_expected_graph_and_resolves_the_session()
    {
        using var workspace = new TestWorkspace();
        var application = CreateApplication(workspace, "writer-1", out var path);
        var battery = application.Load(path).Graph.Nodes.Single(node => node.Id.Value == "battery-assumption");
        var replacement = new GraphNode(
            battery.Id,
            "The battery supports the revised target duty cycle",
            battery.Kind);

        var reviewed = ApplyAndReview(application, path, GraphOperation.ReplaceNode(replacement));
        var written = application.WriteChange(reviewed.Reference);

        Assert.Equal(ChangeWriteStatus.Written, written.Status);
        Assert.NotNull(written.Project);
        Assert.Equal(reviewed.Reference.ProposedFingerprint, written.Project!.StateFingerprint);
        Assert.Equal(replacement, written.Project.Graph.Nodes.Single(node => node.Id == battery.Id));
        Assert.Empty(application.GetExitWarnings());
        Assert.Equal(written.Project.Graph, application.Load(path).Graph);
    }

    [Fact]
    public void Explicit_edge_then_node_removals_are_written_in_foreign_key_safe_order()
    {
        using var workspace = new TestWorkspace();
        var application = CreateApplication(workspace, "writer-remove", out var path);
        var project = application.Load(path);
        var begun = application.BeginChange(path, project.Graph.ProjectId, "human", "Remove a retired battery assumption");
        var applied = application.ApplyChange(
            begun.Reference,
            new GraphOperationBatch(
            [
                GraphOperation.RemoveNode(new EntityId("battery-assumption")),
                GraphOperation.RemoveEdge(new EntityId("battery-scope-parent")),
                GraphOperation.RemoveEdge(new EntityId("battery-requires-test")),
            ]));
        var reviewed = application.ReviewChange(
            applied.Reference,
            new ChangeReviewUpdate(
                applied.Affected.AffectedNodes.Select(node => new ReviewDisposition(
                    node.NodeId,
                    node.IsDirectChange ? ReviewDispositionKind.Updated : ReviewDispositionKind.ReviewedNoChange,
                    null)),
                applied.Affected.ScopeContext.Select(context => context.NodeId)));

        var written = application.WriteChange(reviewed.Reference);

        Assert.Equal(ChangeWriteStatus.Written, written.Status);
        Assert.Equal(6, written.Project!.Graph.Nodes.Count);
        Assert.Equal(6, written.Project.Graph.Edges.Count);
        Assert.DoesNotContain(written.Project.Graph.Nodes, node => node.Id.Value == "battery-assumption");
        Assert.DoesNotContain(written.Project.Graph.Edges, edge => edge.Id.Value is "battery-scope-parent" or "battery-requires-test");
        Assert.True(application.Verify(path).IsValid);
    }

    [Fact]
    public void Pending_invalid_and_inconclusive_proposals_do_not_write()
    {
        using var workspace = new TestWorkspace();
        var application = CreateApplication(workspace, "writer-2", out var path);
        var projectId = new ProjectId(SampleProjectCatalog.TechnicalProject);
        var bytes = File.ReadAllBytes(path);

        var pending = application.BeginChange(path, projectId, "human", "Check pending review");
        var pendingApplied = application.ApplyChange(
            pending.Reference,
            new GraphOperationBatch([GraphOperation.ReplaceNode(new GraphNode(
                new EntityId("battery-assumption"),
                "The battery has unreviewed pending work",
                "assumption"))]));
        Assert.Equal(ChangeWriteStatus.ReviewNotReady, application.WriteChange(pendingApplied.Reference).Status);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        application.DiscardChange(pendingApplied.Reference);

        var invalid = application.BeginChange(path, projectId, "human", "Remove a node without its edges");
        var invalidApplied = application.ApplyChange(
            invalid.Reference,
            new GraphOperationBatch([GraphOperation.RemoveNode(new EntityId("battery-assumption"))]));
        Assert.DoesNotContain(invalidApplied.ProposedGraph.Nodes, node => node.Id.Value == "battery-assumption");
        Assert.Equal(ChangeWriteStatus.ReviewNotReady, application.WriteChange(invalidApplied.Reference).Status);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        application.DiscardChange(invalidApplied.Reference);

        var inconclusive = application.BeginChange(path, projectId, "human", "Bound the affected result");
        var bounded = application.ApplyChange(
            inconclusive.Reference,
            new GraphOperationBatch([GraphOperation.ReplaceNode(new GraphNode(
                new EntityId("battery-assumption"),
                "The battery has a bounded review result",
                "assumption"))]),
            new AffectedAnalysisOptions { MaxOutputItems = 1 });
        Assert.True(bounded.Affected.IsInconclusive);
        Assert.Equal(ChangeWriteStatus.ReviewNotReady, application.WriteChange(bounded.Reference).Status);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void Stale_and_busy_writes_are_structured_and_leave_the_session_available()
    {
        using var workspace = new TestWorkspace();
        var first = CreateApplication(workspace, "writer-3a", out var path);
        var originalBytes = File.ReadAllBytes(path);
        var staleCandidate = ApplyAndReview(
            first,
            path,
            GraphOperation.ReplaceNode(new GraphNode(
                new EntityId("battery-assumption"),
                "The battery has a stale proposal",
                "assumption")));

        var second = new ProjectApplication(
            new SqliteProjectStore(() => FixedUtc),
            utcNow: () => FixedUtc,
            sessionIdFactory: () => "writer-3b");
        var current = ApplyAndReview(
            second,
            path,
            GraphOperation.ReplaceNode(new GraphNode(
                new EntityId("battery-assumption"),
                "The battery has a current proposal",
                "assumption")));
        Assert.Equal(ChangeWriteStatus.Written, second.WriteChange(current.Reference).Status);
        var afterCurrentWrite = File.ReadAllBytes(path);

        Assert.Equal(ChangeWriteStatus.Stale, first.WriteChange(staleCandidate.Reference).Status);
        Assert.Equal(afterCurrentWrite, File.ReadAllBytes(path));
        Assert.Single(first.GetExitWarnings());
        Assert.False(originalBytes.SequenceEqual(afterCurrentWrite));

        var busy = CreateApplication(workspace, "writer-3c", out var busyPath);
        var busyCandidate = ApplyAndReview(
            busy,
            busyPath,
            GraphOperation.ReplaceNode(new GraphNode(
                new EntityId("battery-assumption"),
                "The battery has a busy proposal",
                "assumption")));
        var busyBytes = File.ReadAllBytes(busyPath);
        ChangeWriteResult busyResult;
        TimeSpan elapsed;
        using (var connection = OpenReadWrite(busyPath))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "BEGIN EXCLUSIVE";
            command.ExecuteNonQuery();
            var stopwatch = Stopwatch.StartNew();
            busyResult = busy.WriteChange(busyCandidate.Reference);
            stopwatch.Stop();
            elapsed = stopwatch.Elapsed;
            using var rollback = connection.CreateCommand();
            rollback.CommandText = "ROLLBACK";
            rollback.ExecuteNonQuery();
        }

        Assert.Equal(ChangeWriteStatus.Busy, busyResult.Status);
        Assert.True(elapsed < TimeSpan.FromSeconds(15));
        Assert.Equal(busyBytes, File.ReadAllBytes(busyPath));
        Assert.Single(busy.GetExitWarnings());
    }

    [Fact]
    public void Every_injected_write_boundary_rolls_back_all_rows_and_preserves_the_session()
    {
        foreach (var boundary in Enum.GetValues<SqliteWriteBoundary>())
        {
            using var workspace = new TestWorkspace();
            var store = new SqliteProjectStore(
                () => FixedUtc,
                observed => observed == boundary ? new InvalidOperationException("injected write failure") : null);
            var application = CreateApplication(workspace, $"fault-{boundary}", out var path, store);
            var before = File.ReadAllBytes(path);
            var reviewed = ApplyAndReview(
                application,
                path,
                GraphOperation.ReplaceNode(new GraphNode(
                    new EntityId("battery-assumption"),
                    $"The battery fault probe is {boundary}",
                    "assumption")));

            var failed = application.WriteChange(reviewed.Reference);

            Assert.Equal(ChangeWriteStatus.Failed, failed.Status);
            Assert.Equal(ProjectStorageErrorCode.MappingFailure, failed.StorageErrorCode);
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Single(application.GetExitWarnings());
        }
    }

    [Fact]
    public void A_retry_after_an_injected_failure_revalidates_and_writes_the_same_reviewed_proposal()
    {
        using var workspace = new TestWorkspace();
        var failOnce = true;
        var store = new SqliteProjectStore(
            () => FixedUtc,
            boundary => boundary == SqliteWriteBoundary.AfterEdgeUpserts && failOnce
                ? new InvalidOperationException("one-time write failure")
                : null);
        var application = CreateApplication(workspace, "writer-retry", out var path, store);
        var reviewed = ApplyAndReview(
            application,
            path,
            GraphOperation.ReplaceNode(new GraphNode(
                new EntityId("battery-assumption"),
                "The battery survives a retry after validation",
                "assumption")));

        var failed = application.WriteChange(reviewed.Reference);
        failOnce = false;
        var retried = application.WriteChange(reviewed.Reference);

        Assert.Equal(ChangeWriteStatus.Failed, failed.Status);
        Assert.Equal(ChangeWriteStatus.Written, retried.Status);
        Assert.Equal(reviewed.Reference.ProposedFingerprint, retried.Project!.StateFingerprint);
        Assert.Empty(application.GetExitWarnings());
    }

    private static ChangeSessionSnapshot ApplyAndReview(
        ProjectApplication application,
        string path,
        GraphOperation operation)
    {
        var project = application.Load(path);
        var begun = application.BeginChange(path, project.Graph.ProjectId, "human", "Review an atomic change");
        var applied = application.ApplyChange(begun.Reference, new GraphOperationBatch([operation]));
        return application.ReviewChange(
            applied.Reference,
            new ChangeReviewUpdate(
                applied.Affected.AffectedNodes.Select(node => new ReviewDisposition(
                    node.NodeId,
                    node.IsDirectChange ? ReviewDispositionKind.Updated : ReviewDispositionKind.ReviewedNoChange,
                    null)),
                applied.Affected.ScopeContext.Select(context => context.NodeId)));
    }

    private static ProjectApplication CreateApplication(
        TestWorkspace workspace,
        string sessionId,
        out string path,
        SqliteProjectStore? store = null)
    {
        store ??= new SqliteProjectStore(() => FixedUtc);
        var application = new ProjectApplication(store, utcNow: () => FixedUtc, sessionIdFactory: () => sessionId);
        path = workspace.PathFor($"{sessionId}.vw.db");
        if (!File.Exists(path))
        {
            application.CreateSample(SampleProjectCatalog.TechnicalProject, path);
        }

        return application;
    }

    private static SqliteConnection OpenReadWrite(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = true,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private sealed class TestWorkspace : IDisposable
    {
        public TestWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), $"ValidatedWorld-T8-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string PathFor(string name) => Path.Combine(Root, name);

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
