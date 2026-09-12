using ValidatedWorld.Core;
using ValidatedWorld.Persistence.Sqlite;
using ValidatedWorld.Serialization;
using ValidatedWorld.Validation;

namespace ValidatedWorld.Application.Tests;

public sealed class ProjectMergeTests
{
    [Fact]
    public void Merge_combines_independent_stable_id_changes_and_emits_ours_operations()
    {
        var @base = Graph(
            new GraphNode(new EntityId("purpose"), "Purpose"),
            new GraphNode(new EntityId("alpha"), "Alpha"),
            Scope("alpha-parent", "alpha", "purpose"));
        var ours = Graph(
            new GraphNode(new EntityId("purpose"), "Purpose"),
            new GraphNode(new EntityId("alpha"), "Alpha revised"),
            Scope("alpha-parent", "alpha", "purpose"));
        var theirs = Graph(
            new GraphNode(new EntityId("purpose"), "Purpose"),
            new GraphNode(new EntityId("alpha"), "Alpha"),
            new GraphNode(new EntityId("beta"), "Beta"),
            Scope("alpha-parent", "alpha", "purpose"),
            Scope("beta-parent", "beta", "purpose"));
        var application = Application(("base.vw.db", @base), ("ours.vw.db", ours), ("theirs.vw.db", theirs));

        var result = application.Merge("base.vw.db", "ours.vw.db", "theirs.vw.db");

        Assert.Equal(ProjectMergeStatus.Clean, result.Status);
        Assert.True(result.IsReadyToApply);
        Assert.Empty(result.Conflicts);
        Assert.NotNull(result.Validation);
        Assert.True(result.Validation.IsValid);
        Assert.Equal(["beta", "beta-parent"],
            result.Operations.Operations.Select(operation => operation.EntityId.Value));
        Assert.Equal(GraphOperationKind.Add, result.Operations.Operations[0].Kind);
        Assert.Equal(GraphOperationKind.Add, result.Operations.Operations[1].Kind);
        Assert.Equal("Alpha revised", result.MergedGraph!.Nodes.Single(node => node.Id.Value == "alpha").Text);
        Assert.Equal(GraphFingerprints.State(result.MergedGraph),
            GraphFingerprints.State(new GraphProjector().Project(ours, result.Operations).Graph));
    }

    [Fact]
    public void Merge_reports_same_id_divergence_and_delete_modify_as_explicit_conflicts()
    {
        var @base = Graph(
            new GraphNode(new EntityId("purpose"), "Purpose"),
            new GraphNode(new EntityId("alpha"), "Alpha"),
            new GraphNode(new EntityId("gone"), "Gone"),
            Scope("alpha-parent", "alpha", "purpose"),
            Scope("gone-parent", "gone", "purpose"));
        var ours = Graph(new GraphNode(new EntityId("purpose"), "Purpose"),
            new GraphNode(new EntityId("alpha"), "Ours"),
            Scope("alpha-parent", "alpha", "purpose"));
        var theirs = Graph(new GraphNode(new EntityId("purpose"), "Purpose"),
            new GraphNode(new EntityId("alpha"), "Theirs"),
            new GraphNode(new EntityId("gone"), "Gone revised"),
            Scope("alpha-parent", "alpha", "purpose"),
            Scope("gone-parent", "gone", "purpose"));
        AssertValid(@base, ours, theirs);
        var application = Application(("base.vw.db", @base), ("ours.vw.db", ours), ("theirs.vw.db", theirs));

        var result = application.Merge("base.vw.db", "ours.vw.db", "theirs.vw.db");

        Assert.Equal(ProjectMergeStatus.Conflicted, result.Status);
        Assert.False(result.IsReadyToApply);
        Assert.Null(result.MergedGraph);
        Assert.Null(result.Validation);
        Assert.Equal(
            [ProjectMergeConflictKind.ModifiedDifferently, ProjectMergeConflictKind.DeletedAndModified],
            result.Conflicts.Select(conflict => conflict.Kind));
        Assert.Equal(["alpha", "gone"], result.Conflicts.Select(conflict => conflict.EntityId));
        Assert.Contains("text", result.Conflicts[0].ChangedFields);
    }

    [Fact]
    public void Merge_rejects_project_metadata_and_entity_kind_changes_without_writing()
    {
        var @base = Graph(new GraphNode(new EntityId("purpose"), "Purpose"));
        var ours = Graph(nodes: [new GraphNode(new EntityId("purpose"), "Purpose")], title: "Ours title");
        var theirs = Graph(new GraphNode(new EntityId("purpose"), "Purpose"));
        var application = Application(("base.vw.db", @base), ("ours.vw.db", ours), ("theirs.vw.db", theirs));

        var metadata = application.Merge("base.vw.db", "ours.vw.db", "theirs.vw.db");

        Assert.Equal(ProjectMergeStatus.Conflicted, metadata.Status);
        Assert.Contains(metadata.Conflicts, conflict =>
            conflict.Kind == ProjectMergeConflictKind.ProjectMetadataChanged &&
            conflict.EntityId == "$project.title");

        var kindBase = Graph(
            new GraphNode(new EntityId("purpose"), "Purpose"),
            new GraphNode(new EntityId("anchor"), "Anchor"),
            Scope("anchor-parent", "anchor", "purpose"));
        var kindOurs = Graph(
            new GraphNode(new EntityId("purpose"), "Purpose"),
            new GraphNode(new EntityId("anchor"), "Anchor"),
            new GraphNode(new EntityId("shared"), "Node"),
            Scope("anchor-parent", "anchor", "purpose"),
            Scope("shared-parent", "shared", "purpose"));
        var kindTheirs = Graph(
            new GraphNode(new EntityId("purpose"), "Purpose"),
            new GraphNode(new EntityId("anchor"), "Anchor"),
            Scope("anchor-parent", "anchor", "purpose"),
            new GraphEdge(new EntityId("shared"), new EntityId("anchor"), new EntityId("purpose"),
                "informs", ReviewDirection.None));
        AssertValid(kindBase, kindOurs, kindTheirs);
        var kindApplication = Application(
            ("kind-base.vw.db", kindBase), ("kind-ours.vw.db", kindOurs), ("kind-theirs.vw.db", kindTheirs));

        var kind = kindApplication.Merge("kind-base.vw.db", "kind-ours.vw.db", "kind-theirs.vw.db");

        Assert.Equal(ProjectMergeStatus.Conflicted, kind.Status);
        Assert.Contains(kind.Conflicts, conflict =>
            conflict.Kind == ProjectMergeConflictKind.EntityKindChanged && conflict.EntityId == "shared");
    }

    [Fact]
    public void Merge_requires_all_snapshots_to_have_the_same_project_id()
    {
        var @base = Graph();
        var other = Graph(projectId: "other");
        var application = Application(("base.vw.db", @base), ("ours.vw.db", @base), ("theirs.vw.db", other));

        var exception = Assert.Throws<ProjectQueryException>(() =>
            application.Merge("base.vw.db", "ours.vw.db", "theirs.vw.db"));

        Assert.Equal(ProjectQueryErrorCode.ProjectMismatch, exception.Code);
    }

    [Fact]
    public void Merge_accepts_identical_additions_and_reports_divergent_additions()
    {
        var @base = Graph();
        var ours = Graph(
            new GraphNode(new EntityId("purpose"), "Purpose"),
            new GraphNode(new EntityId("note"), "Shared note"),
            Scope("note-parent", "note", "purpose"));
        var same = Graph(
            new GraphNode(new EntityId("purpose"), "Purpose"),
            new GraphNode(new EntityId("note"), "Shared note"),
            Scope("note-parent", "note", "purpose"));
        var different = Graph(
            new GraphNode(new EntityId("purpose"), "Purpose"),
            new GraphNode(new EntityId("note"), "Different note"),
            Scope("note-parent", "note", "purpose"));
        AssertValid(@base, ours, same, different);
        var application = Application(
            ("base.vw.db", @base),
            ("ours.vw.db", ours),
            ("same.vw.db", same),
            ("different.vw.db", different));

        var compatible = application.Merge("base.vw.db", "ours.vw.db", "same.vw.db");
        var conflicted = application.Merge("base.vw.db", "ours.vw.db", "different.vw.db");

        Assert.Equal(ProjectMergeStatus.Clean, compatible.Status);
        Assert.Empty(compatible.Operations.Operations);
        var conflict = Assert.Single(conflicted.Conflicts);
        Assert.Equal(ProjectMergeConflictKind.AddedDifferently, conflict.Kind);
        Assert.Equal("note", conflict.EntityId);
        Assert.Equal(["text"], conflict.ChangedFields);
    }

    [Fact]
    public void Merge_emits_explicit_removals_for_a_theirs_only_deletion()
    {
        var @base = Graph(
            new GraphNode(new EntityId("purpose"), "Purpose"),
            new GraphNode(new EntityId("retired"), "Retired"),
            Scope("retired-parent", "retired", "purpose"));
        var theirs = Graph();
        AssertValid(@base, theirs);
        var application = Application(
            ("base.vw.db", @base), ("ours.vw.db", @base), ("theirs.vw.db", theirs));

        var result = application.Merge("base.vw.db", "ours.vw.db", "theirs.vw.db");

        Assert.Equal(ProjectMergeStatus.Clean, result.Status);
        Assert.Equal(["retired", "retired-parent"],
            result.Operations.Operations.Select(operation => operation.EntityId.Value));
        Assert.All(result.Operations.Operations, operation =>
            Assert.Equal(GraphOperationKind.Remove, operation.Kind));
        Assert.Equal(theirs, new GraphProjector().Project(@base, result.Operations).Graph);
    }

    [Fact]
    public void Merge_reports_a_conflict_free_but_structurally_invalid_combination()
    {
        var @base = ScopedGraph();
        var ours = new ProjectGraph(
            @base.ProjectId,
            @base.Title,
            @base.PurposeNodeId,
            @base.Nodes.Where(node => node.Id.Value != "scope-b"),
            @base.Edges.Where(edge => edge.Id.Value != "scope-b-parent"));
        var theirs = new ProjectGraph(
            @base.ProjectId,
            @base.Title,
            @base.PurposeNodeId,
            @base.Nodes,
            @base.Edges.Select(edge => edge.Id.Value == "child-parent"
                ? Scope("child-parent", "child", "scope-b")
                : edge));
        AssertValid(@base, ours, theirs);
        var application = Application(("base.vw.db", @base), ("ours.vw.db", ours), ("theirs.vw.db", theirs));

        var result = application.Merge("base.vw.db", "ours.vw.db", "theirs.vw.db");

        Assert.Equal(ProjectMergeStatus.Invalid, result.Status);
        Assert.False(result.IsReadyToApply);
        Assert.NotNull(result.MergedGraph);
        Assert.NotNull(result.Validation);
        Assert.True(result.Validation.IsInvalid);
        Assert.Contains(result.Validation.Diagnostics, diagnostic =>
            diagnostic.Code is "missing-edge-target" or "scope-does-not-reach-purpose");
    }

    [Fact]
    public async Task Clean_merge_operations_flow_through_normal_review_and_atomic_write()
    {
        using var workspace = new TemporaryDirectory();
        var basePath = Path.Combine(workspace.Path, "base.vw.db");
        var oursPath = Path.Combine(workspace.Path, "ours.vw.db");
        var theirsPath = Path.Combine(workspace.Path, "theirs.vw.db");
        var @base = Graph(
            new GraphNode(new EntityId("purpose"), "Purpose"),
            new GraphNode(new EntityId("alpha"), "Alpha"),
            Scope("alpha-parent", "alpha", "purpose"));
        var ours = Graph(
            new GraphNode(new EntityId("purpose"), "Purpose"),
            new GraphNode(new EntityId("alpha"), "Alpha revised"),
            Scope("alpha-parent", "alpha", "purpose"));
        var theirs = Graph(
            new GraphNode(new EntityId("purpose"), "Purpose"),
            new GraphNode(new EntityId("alpha"), "Alpha"),
            new GraphNode(new EntityId("beta"), "Beta"),
            Scope("alpha-parent", "alpha", "purpose"),
            Scope("beta-parent", "beta", "purpose"));
        var store = new SqliteProjectStore();
        store.Initialize(basePath, @base);
        store.Initialize(oursPath, ours);
        store.Initialize(theirsPath, theirs);
        var application = new ProjectApplication(store);

        var merge = application.Merge(basePath, oursPath, theirsPath);
        var begun = application.BeginChange(oursPath, ours.ProjectId, "tester", "Apply the clean merge plan");
        var applied = application.ApplyChange(begun.Reference, merge.Operations);
        var reviewed = application.ReviewChange(
            applied.Reference,
            new ChangeReviewUpdate(
                applied.Affected.AffectedNodes.Select(node => new ReviewDisposition(
                    node.NodeId,
                    node.IsDirectChange ? ReviewDispositionKind.Updated : ReviewDispositionKind.ReviewedNoChange,
                    null)),
                applied.Affected.ScopeContext.Select(context => context.NodeId)));

        var written = await application.WriteChangeAsync(reviewed.Reference);

        Assert.Equal(ChangeWriteStatus.Written, written.Status);
        Assert.Equal(merge.MergedFingerprint, written.Project!.StateFingerprint);
        Assert.Equal(merge.MergedGraph, written.Project.Graph);
        Assert.Equal(@base, application.Load(basePath).Graph);
        Assert.Equal(theirs, application.Load(theirsPath).Graph);
    }

    private static ProjectApplication Application(params (string Path, ProjectGraph Graph)[] graphs) =>
        new(new DictionaryStore(graphs));

    private static ProjectGraph Graph(
        params object[] values)
    {
        var nodes = values.OfType<GraphNode>().ToArray();
        var edges = values.OfType<GraphEdge>().ToArray();
        return Graph(nodes, edges);
    }

    private static ProjectGraph Graph(
        GraphNode[]? nodes = null,
        GraphEdge[]? edges = null,
        string projectId = "project",
        string title = "Project")
    {
        nodes ??= [new GraphNode(new EntityId("purpose"), "Purpose")];
        return new ProjectGraph(new ProjectId(projectId), title, new EntityId("purpose"), nodes, edges ?? []);
    }

    private static GraphEdge Scope(string id, string child, string parent) => new(
        new EntityId(id), new EntityId(child), new EntityId(parent), "scope-parent", ReviewDirection.None);

    private static ProjectGraph ScopedGraph() => Graph(
        new GraphNode(new EntityId("purpose"), "Purpose"),
        new GraphNode(new EntityId("scope-a"), "Scope A", "scope"),
        new GraphNode(new EntityId("scope-b"), "Scope B", "scope"),
        new GraphNode(new EntityId("child"), "Child"),
        Scope("scope-a-parent", "scope-a", "purpose"),
        Scope("scope-b-parent", "scope-b", "purpose"),
        Scope("child-parent", "child", "scope-a"));

    private static void AssertValid(params ProjectGraph[] graphs)
    {
        var validator = new GraphValidator();
        Assert.All(graphs, graph => Assert.True(validator.Validate(graph).IsValid));
    }

    private sealed class DictionaryStore : IProjectStore
    {
        private readonly IReadOnlyDictionary<string, StoredProject> _projects;

        public DictionaryStore(IEnumerable<(string Path, ProjectGraph Graph)> projects) =>
            _projects = projects.ToDictionary(
                item => item.Path,
                item => new StoredProject(item.Path, item.Graph, GraphFingerprints.State(item.Graph),
                    DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
                StringComparer.Ordinal);

        public StoredProject Load(string path) => _projects[path];
        public StoredProject Initialize(string path, ProjectGraph graph) => throw new NotSupportedException();
        public ProjectStatus GetStatus(string path) => throw new NotSupportedException();
        public ProjectVerification Verify(string path) => throw new NotSupportedException();
        public StoredProject Backup(string sourcePath, string destinationPath) => throw new NotSupportedException();
        public ProjectSqlExport ExportSql(string path) => throw new NotSupportedException();
        public ProjectWriteResult Write(ProjectWriteRequest request) => throw new NotSupportedException();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"ValidatedWorld.Merge.Application.Tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
