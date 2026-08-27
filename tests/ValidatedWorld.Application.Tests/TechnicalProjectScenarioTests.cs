using System.Text.Json;
using ValidatedWorld.Application;
using ValidatedWorld.Core;
using ValidatedWorld.Persistence.Sqlite;
using ValidatedWorld.Serialization;
using ValidatedWorld.Validation;

namespace ValidatedWorld.Application.Tests;

public sealed class TechnicalProjectScenarioTests
{
    private static readonly DateTimeOffset FixedUtc =
        new(2026, 8, 26, 18, 0, 0, TimeSpan.Zero);

    public static IEnumerable<object[]> ScenarioNames()
    {
        foreach (var name in new[]
                 {
                     "power-change", "privacy-change", "edge-redirect", "scope-change", "purpose-change",
                 })
        {
            yield return [name];
        }
    }

    [Fact]
    public void Built_in_sample_matches_the_reviewed_text_only_baseline_asset()
    {
        var reviewed = GraphProtocol.FromDto(Protocol.Deserialize<GraphDto>(AssetText("baseline.json")));
        var builtIn = SampleProjectCatalog.Create(SampleProjectCatalog.TechnicalProject);

        Assert.Equal(reviewed, builtIn);
        Assert.True(new GraphValidator().Validate(builtIn).IsValid);
        Assert.Equal(13, builtIn.Nodes.Count);
        Assert.Equal(17, builtIn.Edges.Count);
    }

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public async Task Reviewed_operation_golden_drives_complete_public_application_write(string scenarioName)
    {
        using var workspace = new ScenarioWorkspace();
        var application = CreateApplication(workspace, scenarioName, out var path);
        var scenario = LoadScenario(scenarioName);

        var applied = BeginAndApply(application, path, scenario.Operations);
        AssertGolden(scenario, applied);

        var reviewed = ReviewEverything(application, applied);
        var written = await application.WriteChangeAsync(reviewed.Reference, new ChangeWriteOptions(true));

        Assert.Equal(ChangeWriteStatus.Written, written.Status);
        Assert.NotNull(written.Project);
        Assert.Equal(reviewed.Reference.ProposedFingerprint, written.Project!.StateFingerprint);
        Assert.True(application.Verify(path).IsValid);
        Assert.Empty(application.GetExitWarnings());
    }

    [Fact]
    public async Task Incomplete_review_is_a_bounded_clear_blocker_and_preserves_the_database()
    {
        using var workspace = new ScenarioWorkspace();
        var application = CreateApplication(workspace, "incomplete", out var path);
        var before = File.ReadAllBytes(path);
        var applied = BeginAndApply(application, path, LoadScenario("power-change").Operations);
        var direct = applied.Affected.AffectedNodes.Single(node => node.IsDirectChange);

        var incomplete = application.ReviewChange(
            applied.Reference,
            new ChangeReviewUpdate(
                [new ReviewDisposition(direct.NodeId, ReviewDispositionKind.Updated, null)],
                applied.Affected.ScopeContext.Select(entry => entry.NodeId)));

        Assert.False(incomplete.Readiness.IsReady);
        Assert.Equal(new[] { "power-design-anchor", "runtime-test" },
            incomplete.Readiness.PendingNodeIds.Select(id => id.Value));
        Assert.Contains("pending", string.Join(" ", incomplete.Readiness.Blockers), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ChangeWriteStatus.ReviewNotReady,
            (await application.WriteChangeAsync(incomplete.Reference, new ChangeWriteOptions(true))).Status);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Stale_and_injected_rollback_scenarios_leave_the_prior_state_unchanged()
    {
        using var workspace = new ScenarioWorkspace();
        var first = CreateApplication(workspace, "stale-first", out var path);
        var firstReviewed = ReviewEverything(first, BeginAndApply(first, path, LoadScenario("power-change").Operations));
        var second = new ProjectApplication(
            new SqliteProjectStore(() => FixedUtc),
            utcNow: () => FixedUtc,
            sessionIdFactory: () => "stale-second");
        var secondReviewed = ReviewEverything(second, BeginAndApply(second, path, LoadScenario("privacy-change").Operations));
        Assert.Equal(ChangeWriteStatus.Written,
            (await second.WriteChangeAsync(secondReviewed.Reference, new ChangeWriteOptions(true))).Status);
        var afterSecondWrite = File.ReadAllBytes(path);

        Assert.Equal(ChangeWriteStatus.Stale,
            (await first.WriteChangeAsync(firstReviewed.Reference, new ChangeWriteOptions(true))).Status);
        Assert.Equal(afterSecondWrite, File.ReadAllBytes(path));

        var rollbackStore = new SqliteProjectStore(
            () => FixedUtc,
            boundary => boundary == SqliteWriteBoundary.AfterNodeUpserts
                ? new InvalidOperationException("scenario rollback probe")
                : null);
        var rollback = new ProjectApplication(
            rollbackStore,
            utcNow: () => FixedUtc,
            sessionIdFactory: () => "rollback");
        var rollbackReviewed = ReviewEverything(rollback, BeginAndApply(rollback, path, LoadScenario("edge-redirect").Operations));
        var beforeRollback = File.ReadAllBytes(path);

        var failed = await rollback.WriteChangeAsync(rollbackReviewed.Reference, new ChangeWriteOptions(true));

        Assert.Equal(ChangeWriteStatus.Failed, failed.Status);
        Assert.Equal(ProjectStorageErrorCode.MappingFailure, failed.StorageErrorCode);
        Assert.Equal(beforeRollback, File.ReadAllBytes(path));
        Assert.Single(rollback.GetExitWarnings());
    }

    [Fact]
    public async Task Backup_and_bounded_diagnostic_scenarios_are_verified_without_tracking_a_database()
    {
        using var workspace = new ScenarioWorkspace();
        var application = CreateApplication(workspace, "backup", out var path);
        var backup = workspace.PathFor("verified backup.vw.db");
        var original = application.Load(path);
        var copied = application.Backup(path, backup);

        Assert.Equal(original.Graph, copied.Graph);
        Assert.Equal(original.StateFingerprint, copied.StateFingerprint);
        Assert.True(application.Verify(backup).IsValid);
        Assert.DoesNotContain(Directory.GetFiles(AppContext.BaseDirectory, "*.vw.db", SearchOption.AllDirectories),
            value => value.Contains("TechnicalProject", StringComparison.Ordinal));

        var bounded = BeginAndApply(
            application,
            path,
            LoadScenario("purpose-change").Operations,
            new AffectedAnalysisOptions { MaxOutputItems = 1 });
        Assert.True(bounded.Affected.IsInconclusive);
        Assert.NotEmpty(bounded.Affected.Omissions);
        Assert.Equal(ChangeWriteStatus.ReviewNotReady,
            (await application.WriteChangeAsync(bounded.Reference, new ChangeWriteOptions(true))).Status);
        application.DiscardChange(bounded.Reference);
    }

    private static ChangeSessionSnapshot BeginAndApply(
        ProjectApplication application,
        string path,
        GraphOperationBatch operations,
        AffectedAnalysisOptions? options = null)
    {
        var project = application.Load(path);
        var begun = application.BeginChange(path, project.Graph.ProjectId, "scenario tester", "Run reviewed scenario asset");
        return application.ApplyChange(begun.Reference, operations, options);
    }

    private static ChangeSessionSnapshot ReviewEverything(ProjectApplication application, ChangeSessionSnapshot snapshot) =>
        application.ReviewChange(
            snapshot.Reference,
            new ChangeReviewUpdate(
                snapshot.Affected.AffectedNodes.Select(node => new ReviewDisposition(
                    node.NodeId,
                    node.IsDirectChange ? ReviewDispositionKind.Updated : ReviewDispositionKind.ReviewedNoChange,
                    null)),
                snapshot.Affected.ScopeContext.Select(entry => entry.NodeId)));

    private static void AssertGolden(ScenarioAsset scenario, ChangeSessionSnapshot applied)
    {
        Assert.Equal(scenario.Expected.AffectedNodeIds, applied.Affected.AffectedNodes.Select(node => node.NodeId.Value));
        Assert.Equal(scenario.Expected.ScopeContextNodeIds, applied.Affected.ScopeContext.Select(node => node.NodeId.Value));
        Assert.Equal(scenario.Expected.ExpectedEdgeChangeIds, applied.Affected.EdgeChanges.Select(edge => edge.EdgeId.Value));
        foreach (var excluded in scenario.Expected.ExcludedNodeIds)
        {
            Assert.DoesNotContain(applied.Affected.AffectedNodes, node => node.NodeId.Value == excluded);
        }

        Assert.True(applied.Affected.IsComplete, scenario.Goal);
    }

    private static ScenarioAsset LoadScenario(string name)
    {
        var dto = Protocol.Deserialize<ScenarioAssetDto>(AssetText($"{name}.json"));
        return new ScenarioAsset(dto.Goal, GraphProtocol.FromDto(dto.Operations), dto.Expected);
    }

    private static ProjectApplication CreateApplication(ScenarioWorkspace workspace, string sessionId, out string path)
    {
        path = workspace.PathFor($"{sessionId}.vw.db");
        var application = new ProjectApplication(
            new SqliteProjectStore(() => FixedUtc),
            utcNow: () => FixedUtc,
            sessionIdFactory: () => sessionId);
        application.CreateSample(SampleProjectCatalog.TechnicalProject, path);
        return application;
    }

    private static string AssetText(string name) => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "TechnicalProject", name));

    public sealed record ScenarioAsset(string Goal, GraphOperationBatch Operations, ScenarioExpected Expected);

    public sealed record ScenarioAssetDto(string Goal, OperationBatchDto Operations, ScenarioExpected Expected);

    public sealed record ScenarioExpected(
        IReadOnlyList<string> AffectedNodeIds,
        IReadOnlyList<string> ScopeContextNodeIds,
        IReadOnlyList<string> ExcludedNodeIds,
        IReadOnlyList<string> ExpectedEdgeChangeIds);

    private sealed class ScenarioWorkspace : IDisposable
    {
        public ScenarioWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), $"ValidatedWorld-T10-{Guid.NewGuid():N}");
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
