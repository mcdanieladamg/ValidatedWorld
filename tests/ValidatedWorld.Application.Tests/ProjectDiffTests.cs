using ValidatedWorld.Core;
using ValidatedWorld.Serialization;

namespace ValidatedWorld.Application.Tests;

public sealed class ProjectDiffTests
{
    private static readonly DateTimeOffset FixedUtc =
        new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Diff_reports_complete_deterministic_metadata_node_and_edge_changes()
    {
        var baseGraph = BaseGraph();
        var targetGraph = TargetGraph();
        var application = Application(("base.vw.db", baseGraph), ("target.vw.db", targetGraph));

        var result = application.Diff("base.vw.db", "target.vw.db", new QueryPageRequest(100));

        Assert.Equal(baseGraph.ProjectId, result.ProjectId);
        Assert.Equal(GraphFingerprints.State(baseGraph), result.BaseFingerprint);
        Assert.Equal(GraphFingerprints.State(targetGraph), result.TargetFingerprint);
        Assert.Equal(
            [new ProjectMetadataChange("title", "Base project", "Target project")],
            result.MetadataChanges);
        Assert.Equal(new ProjectDiffSummary(1, 1, 1, 1, 1, 1, 1), result.Summary);
        Assert.Equal(6, result.Summary.EntityChanges);
        Assert.Equal(7, result.Summary.TotalChanges);
        Assert.Equal(6, result.Changes.TotalCount);
        Assert.Null(result.Changes.NextCursor);
        Assert.Null(result.Changes.Omission);
        Assert.Equal(
            [
                "Node:add:added",
                "Node:replace:alpha",
                "Node:remove:removed",
                "Edge:add:scope-added",
                "Edge:remove:scope-removed",
                "Edge:replace:semantic-link",
            ],
            result.Changes.Items.Select(Describe));

        var added = result.Changes.Items[0];
        Assert.Null(added.OldNode);
        Assert.Equal("added", added.NewNode!.Id.Value);
        Assert.Empty(added.ChangedFields);

        var replacedNode = result.Changes.Items[1];
        Assert.Equal("Old alpha", replacedNode.OldNode!.Text);
        Assert.Equal("New alpha", replacedNode.NewNode!.Text);
        Assert.Equal(["text", "kind", "tags", "attributes"], replacedNode.ChangedFields);

        var removed = result.Changes.Items[2];
        Assert.Equal("removed", removed.OldNode!.Id.Value);
        Assert.Null(removed.NewNode);
        Assert.Empty(removed.ChangedFields);

        var replacedEdge = result.Changes.Items[5];
        Assert.Equal("alpha", replacedEdge.OldEdge!.Source.Value);
        Assert.Equal("removed", replacedEdge.OldEdge.Target.Value);
        Assert.Equal(ReviewDirection.SourceToTarget, replacedEdge.OldEdge.ReviewDirection);
        Assert.Equal("added", replacedEdge.NewEdge!.Source.Value);
        Assert.Equal("alpha", replacedEdge.NewEdge.Target.Value);
        Assert.Equal(ReviewDirection.Both, replacedEdge.NewEdge.ReviewDirection);
        Assert.Equal(
            ["source", "target", "relationship", "reviewDirection", "rationale", "tags", "attributes"],
            replacedEdge.ChangedFields);
    }

    [Fact]
    public void Diff_pages_reconstruct_full_order_and_cursor_is_bound_to_inputs_and_limit()
    {
        var application = Application(("base.vw.db", BaseGraph()), ("target.vw.db", TargetGraph()));
        var first = application.Diff("base.vw.db", "target.vw.db", new QueryPageRequest(2));
        var second = application.Diff(
            "base.vw.db", "target.vw.db", new QueryPageRequest(2, first.Changes.NextCursor));
        var third = application.Diff(
            "base.vw.db", "target.vw.db", new QueryPageRequest(2, second.Changes.NextCursor));

        Assert.Equal(6, first.Changes.TotalCount);
        Assert.Equal(4, first.Changes.Omission!.RemainingCount);
        Assert.Equal(2, second.Changes.Omission!.RemainingCount);
        Assert.Null(third.Changes.NextCursor);
        Assert.Null(third.Changes.Omission);
        Assert.Equal(
            application.Diff("base.vw.db", "target.vw.db", new QueryPageRequest(100))
                .Changes.Items.Select(Describe),
            first.Changes.Items.Concat(second.Changes.Items).Concat(third.Changes.Items).Select(Describe));

        var changedLimit = Assert.Throws<ProjectQueryException>(() => application.Diff(
            "base.vw.db", "target.vw.db", new QueryPageRequest(3, first.Changes.NextCursor)));
        Assert.Equal(ProjectQueryErrorCode.InvalidCursor, changedLimit.Code);

        var reversed = Assert.Throws<ProjectQueryException>(() => application.Diff(
            "target.vw.db", "base.vw.db", new QueryPageRequest(2, first.Changes.NextCursor)));
        Assert.Equal(ProjectQueryErrorCode.InvalidCursor, reversed.Code);

        var malformed = Assert.Throws<ProjectQueryException>(() => application.Diff(
            "base.vw.db", "target.vw.db", new QueryPageRequest(2, "not-base64")));
        Assert.Equal(ProjectQueryErrorCode.InvalidCursor, malformed.Code);

        var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(first.Changes.NextCursor!));
        var signature = decoded[..decoded.LastIndexOf(':')];
        var exactEndCursor = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{signature}:6"));
        var emptyEnd = application.Diff(
            "base.vw.db", "target.vw.db", new QueryPageRequest(2, exactEndCursor));
        Assert.Empty(emptyEnd.Changes.Items);
        Assert.Null(emptyEnd.Changes.NextCursor);
        Assert.Null(emptyEnd.Changes.Omission);
    }

    [Fact]
    public void Reversed_identical_mismatched_and_purpose_change_cases_are_explicit()
    {
        var baseGraph = BaseGraph();
        var targetGraph = TargetGraph();
        var otherProject = new ProjectGraph(
            new ProjectId("other"), targetGraph.Title, targetGraph.PurposeNodeId,
            targetGraph.Nodes, targetGraph.Edges);
        var purposeTarget = PurposeChangedGraph();
        var application = Application(
            ("base.vw.db", baseGraph),
            ("target.vw.db", targetGraph),
            ("other.vw.db", otherProject),
            ("purpose.vw.db", purposeTarget));

        var reversed = application.Diff("target.vw.db", "base.vw.db", new QueryPageRequest(100));
        Assert.Equal(new ProjectDiffSummary(1, 1, 1, 1, 1, 1, 1), reversed.Summary);
        Assert.Equal("Target project", reversed.MetadataChanges.Single().OldValue);
        Assert.Equal("Base project", reversed.MetadataChanges.Single().NewValue);
        Assert.Equal(GraphOperationKind.Remove,
            reversed.Changes.Items.Single(change => change.EntityId.Value == "added").Kind);
        Assert.Equal(GraphOperationKind.Add,
            reversed.Changes.Items.Single(change => change.EntityId.Value == "removed").Kind);
        var reversedAlpha = reversed.Changes.Items.Single(change => change.EntityId.Value == "alpha");
        Assert.Equal("New alpha", reversedAlpha.OldNode!.Text);
        Assert.Equal("Old alpha", reversedAlpha.NewNode!.Text);

        var identical = application.Diff("base.vw.db", "base.vw.db");
        Assert.Empty(identical.MetadataChanges);
        Assert.Empty(identical.Changes.Items);
        Assert.Equal(0, identical.Summary.TotalChanges);

        var mismatch = Assert.Throws<ProjectQueryException>(() =>
            application.Diff("base.vw.db", "other.vw.db"));
        Assert.Equal(ProjectQueryErrorCode.ProjectMismatch, mismatch.Code);
        Assert.Contains("does not match", mismatch.Message, StringComparison.Ordinal);

        var purpose = application.Diff("base.vw.db", "purpose.vw.db", new QueryPageRequest(100));
        Assert.Contains(purpose.MetadataChanges, change =>
            change.Field == "purposeNodeId" && change.OldValue == "purpose" && change.NewValue == "new-purpose");
        Assert.Contains(purpose.Changes.Items, change =>
            change.EntityId.Value == "purpose-parent" && change.Kind == GraphOperationKind.Add);
    }

    private static ProjectApplication Application(params (string Path, ProjectGraph Graph)[] graphs) =>
        new(new DictionaryStore(graphs));

    private static ProjectGraph BaseGraph()
    {
        var purpose = new GraphNode(new EntityId("purpose"), "Purpose", "thesis");
        var alpha = new GraphNode(
            new EntityId("alpha"), "Old alpha", "assumption", ["old"],
            [new("value", GraphValue.FromInteger(1))]);
        var removed = new GraphNode(new EntityId("removed"), "Removed node", "claim");
        return new ProjectGraph(
            new ProjectId("project"), "Base project", purpose.Id,
            [purpose, alpha, removed],
            [
                Scope("scope-alpha", alpha.Id, purpose.Id),
                Scope("scope-removed", removed.Id, purpose.Id),
                new GraphEdge(
                    new EntityId("semantic-link"), alpha.Id, removed.Id, "informs",
                    ReviewDirection.SourceToTarget, "Old rationale", ["old"],
                    [new("weight", GraphValue.FromDecimal("1.5"))]),
            ]);
    }

    private static ProjectGraph TargetGraph()
    {
        var purpose = new GraphNode(new EntityId("purpose"), "Purpose", "thesis");
        var alpha = new GraphNode(
            new EntityId("alpha"), "New alpha", "decision", ["new"],
            [new("value", GraphValue.FromInteger(2))]);
        var added = new GraphNode(new EntityId("added"), "Added node", "claim");
        return new ProjectGraph(
            new ProjectId("project"), "Target project", purpose.Id,
            [purpose, alpha, added],
            [
                Scope("scope-alpha", alpha.Id, purpose.Id),
                Scope("scope-added", added.Id, purpose.Id),
                new GraphEdge(
                    new EntityId("semantic-link"), added.Id, alpha.Id, "constrains",
                    ReviewDirection.Both, "New rationale", ["new"],
                    [new("weight", GraphValue.FromBoolean(true))]),
            ]);
    }

    private static ProjectGraph PurposeChangedGraph()
    {
        var original = BaseGraph();
        var newPurpose = new GraphNode(new EntityId("new-purpose"), "New purpose", "thesis");
        return new ProjectGraph(
            original.ProjectId,
            original.Title,
            newPurpose.Id,
            original.Nodes.Append(newPurpose),
            original.Edges.Append(Scope("purpose-parent", original.PurposeNodeId, newPurpose.Id)));
    }

    private static GraphEdge Scope(string id, EntityId child, EntityId parent) =>
        new(new EntityId(id), child, parent, "scope-parent", ReviewDirection.None);

    private static string Describe(ProjectDiffEntry entry) =>
        $"{entry.EntityKind}:{entry.Kind.ToString().ToLowerInvariant()}:{entry.EntityId.Value}";

    private sealed class DictionaryStore : IProjectStore
    {
        private readonly IReadOnlyDictionary<string, StoredProject> _projects;

        public DictionaryStore(IEnumerable<(string Path, ProjectGraph Graph)> projects) =>
            _projects = projects.ToDictionary(
                item => item.Path,
                item => new StoredProject(
                    item.Path,
                    item.Graph,
                    GraphFingerprints.State(item.Graph),
                    FixedUtc,
                    FixedUtc),
                StringComparer.Ordinal);

        public StoredProject Load(string path) => _projects[path];

        public StoredProject Initialize(string path, ProjectGraph graph) => throw new NotSupportedException();

        public ProjectStatus GetStatus(string path) => throw new NotSupportedException();

        public ProjectVerification Verify(string path) => throw new NotSupportedException();

        public StoredProject Backup(string sourcePath, string destinationPath) => throw new NotSupportedException();

        public ProjectSqlExport ExportSql(string path) => throw new NotSupportedException();

        public ProjectWriteResult Write(ProjectWriteRequest request) => throw new NotSupportedException();
    }
}
