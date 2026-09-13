using System.Text.Json;
using System.Text.Json.Serialization;
using ValidatedWorld.Core;
using ValidatedWorld.Persistence.Sqlite;
using ValidatedWorld.Serialization;
using ValidatedWorld.Validation;

namespace ValidatedWorld.Application.Tests;

public sealed class BulkImportPlanningTests
{
    [Fact]
    public async Task Jsonl_manifest_is_streamed_into_validated_resumable_chunks_and_one_atomic_write()
    {
        using var workspace = new TestWorkspace();
        var application = new ProjectApplication(new SqliteProjectStore());
        var path = System.IO.Path.Combine(workspace.Path, "project.vw.db");
        var stored = application.Initialize(
            path,
            new ProjectId("bulk-project"),
            "Bulk project",
            new EntityId("purpose"),
            "Keep the imported graph coherent.");
        var manifest = System.IO.Path.Combine(workspace.Path, "import.jsonl");
        var operations = new[]
        {
            GraphOperation.AddNode(new GraphNode(new EntityId("scope-power"), "Power", "scope")),
            GraphOperation.AddEdge(new GraphEdge(
                new EntityId("scope-power-parent"), new EntityId("scope-power"),
                new EntityId("purpose"), "scope-parent", ReviewDirection.None)),
            GraphOperation.AddNode(new GraphNode(new EntityId("battery"), "Battery target", "assumption")),
            GraphOperation.AddEdge(new GraphEdge(
                new EntityId("battery-parent"), new EntityId("battery"),
                new EntityId("scope-power"), "scope-parent", ReviewDirection.None)),
        };
        WriteManifest(manifest, stored, operations);

        var first = application.PlanBulkImport(path, manifest, new BulkImportPlanOptions(2));
        Assert.Equal(4, first.OperationCount);
        Assert.Equal(2, first.ChunkCount);
        Assert.Equal(0, first.ChunkIndex);
        Assert.Equal(2, first.ChunkOperationCount);
        Assert.NotNull(first.NextCursor);

        var second = application.PlanBulkImport(
            path, manifest, new BulkImportPlanOptions(2, first.NextCursor));
        Assert.Equal(first.ManifestFingerprint, second.ManifestFingerprint);
        Assert.Equal(1, second.ChunkIndex);
        Assert.Equal(2, second.OperationStart);
        Assert.Equal(2, second.ChunkOperationCount);
        Assert.Null(second.NextCursor);

        var session = application.BeginChange(path, stored.Graph.ProjectId, "bulk-test", first.Intent);
        session = application.PatchChange(session.Reference, first.Operations);
        session = application.PatchChange(session.Reference, second.Operations);
        session = ReviewAll(application, session);
        var written = await application.WriteChangeAsync(session.Reference);

        Assert.Equal(ChangeWriteStatus.Written, written.Status);
        Assert.Equal(3, written.Project!.Graph.Nodes.Count);
        Assert.Equal(2, written.Project.Graph.Edges.Count);
    }

    [Fact]
    public void Cursor_and_checkpoint_validation_reject_stale_or_unsafe_manifests()
    {
        using var workspace = new TestWorkspace();
        var application = new ProjectApplication(new SqliteProjectStore());
        var path = System.IO.Path.Combine(workspace.Path, "project.vw.db");
        var stored = application.Initialize(
            path,
            new ProjectId("bulk-project"),
            "Bulk project",
            new EntityId("purpose"),
            "Keep the imported graph coherent.");
        var manifest = System.IO.Path.Combine(workspace.Path, "import.jsonl");
        WriteManifest(manifest, stored, [
            GraphOperation.AddNode(new GraphNode(new EntityId("unsafe"), "Missing scope", "claim")),
        ]);

        var exception = Assert.Throws<BulkImportException>(() =>
            application.PlanBulkImport(path, manifest, new BulkImportPlanOptions(1)));
        Assert.Equal(ProjectStorageErrorCode.InvalidBulkManifest, exception.Code);
        Assert.Contains("checkpoint", exception.Message, StringComparison.OrdinalIgnoreCase);

        WriteManifest(manifest, stored, [
            GraphOperation.AddNode(new GraphNode(new EntityId("scope"), "Scope", "scope")),
            GraphOperation.AddEdge(new GraphEdge(
                new EntityId("scope-parent"), new EntityId("scope"), new EntityId("purpose"),
                "scope-parent", ReviewDirection.None)),
            GraphOperation.AddNode(new GraphNode(new EntityId("scope-two"), "Scope two", "scope")),
            GraphOperation.AddEdge(new GraphEdge(
                new EntityId("scope-two-parent"), new EntityId("scope-two"), new EntityId("purpose"),
                "scope-parent", ReviewDirection.None)),
        ]);
        var first = application.PlanBulkImport(path, manifest, new BulkImportPlanOptions(2));
        Assert.NotNull(first.NextCursor);
        WriteManifest(manifest, stored, [
            GraphOperation.AddNode(new GraphNode(new EntityId("scope"), "Scope", "scope")),
            GraphOperation.AddEdge(new GraphEdge(
                new EntityId("scope-parent"), new EntityId("scope"), new EntityId("purpose"),
                "scope-parent", ReviewDirection.None)),
            GraphOperation.AddNode(new GraphNode(new EntityId("scope-two"), "Scope two changed", "scope")),
            GraphOperation.AddEdge(new GraphEdge(
                new EntityId("scope-two-parent"), new EntityId("scope-two"), new EntityId("purpose"),
                "scope-parent", ReviewDirection.None)),
        ]);
        var stale = Assert.Throws<BulkImportException>(() =>
            application.PlanBulkImport(path, manifest, new BulkImportPlanOptions(2, first.NextCursor)));
        Assert.Equal(ProjectStorageErrorCode.InvalidBulkManifest, stale.Code);
        Assert.Contains("stale", stale.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ChangeSessionSnapshot ReviewAll(
        ProjectApplication application,
        ChangeSessionSnapshot session)
    {
        var direct = session.Affected.AffectedNodes
            .Where(node => node.IsDirectChange)
            .Select(node => new ReviewDisposition(node.NodeId, ReviewDispositionKind.Updated, null));
        var indirect = session.Affected.AffectedNodes
            .Where(node => !node.IsDirectChange)
            .Select(node => new ReviewDisposition(node.NodeId, ReviewDispositionKind.ReviewedNoChange, null));
        return application.ReviewChange(session.Reference, new ChangeReviewUpdate(
            direct.Concat(indirect), session.Affected.ScopeContext.Select(value => value.NodeId)));
    }

    private static void WriteManifest(
        string path,
        StoredProject project,
        IEnumerable<GraphOperation> operations)
    {
        var options = Protocol.CreateJsonOptions();
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        using var writer = new StreamWriter(path, append: false);
        writer.WriteLine(JsonSerializer.Serialize(new BulkImportManifestHeader(
            BulkImportContract.Version,
            BulkImportContract.Format,
            project.Graph.ProjectId.Value,
            project.StateFingerprint,
            "Import a bounded technical-project graph."), options));
        foreach (var operation in operations)
        {
            writer.WriteLine(JsonSerializer.Serialize(GraphProtocol.ToDto(operation), options));
        }
    }

    private sealed class TestWorkspace : IDisposable
    {
        public TestWorkspace()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"ValidatedWorld.Bulk.Application.Tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
