using ValidatedWorld.Application;
using ValidatedWorld.Core;
using ValidatedWorld.Persistence.Sqlite;
using ValidatedWorld.Serialization;
using ValidatedWorld.Validation;

namespace ValidatedWorld.Persistence.Sqlite.Tests;

public sealed class RuleAndTemplatePersistenceTests
{
    [Fact]
    public void Every_initialization_path_creates_the_single_supported_project_schema()
    {
        using var workspace = new RuleWorkspace();
        var application = new ProjectApplication(new SqliteProjectStore());
        var templatePath = workspace.PathFor("code.vw.db");

        var created = application.InstantiateTemplate("code-development", templatePath,
            new ProjectId("code"), "Code", "Maintain a code project.");

        Assert.True(application.Verify(templatePath).IsValid);
        Assert.Contains(created.Graph.Nodes, node => node.Kind == RuleProtocol.RuleKind);

        var purposeOnlyPath = workspace.PathFor("purpose-only.vw.db");
        application.Initialize(purposeOnlyPath, new ProjectId("purpose-only"), "Purpose only", new EntityId("purpose"), "Purpose");
        Assert.True(application.Verify(purposeOnlyPath).IsValid);
    }

    [Fact]
    public void Store_rejects_rule_invalid_candidate_before_writing()
    {
        using var workspace = new RuleWorkspace();
        var store = new SqliteProjectStore();
        var application = new ProjectApplication(store);
        var path = workspace.PathFor("rules.vw.db");
        var project = application.InstantiateTemplate("code-development", path,
            new ProjectId("rules"), "Rules", "Maintain rules.");
        var phase = new GraphNode(new EntityId("phase-one"), "First", "development-phase",
            ["roadmap:phase", "status:current"]);
        var scope = new GraphEdge(new EntityId("phase-one-scope"), phase.Id, new EntityId("scope-roadmap"),
            "scope-parent", ReviewDirection.None);
        var operations = new GraphOperationBatch([GraphOperation.AddNode(phase), GraphOperation.AddEdge(scope)]);
        var proposed = new GraphProjector().Project(project.Graph, operations).Graph;

        var result = store.Write(new ProjectWriteRequest(path, project.Graph.ProjectId,
            project.StateFingerprint, operations, GraphFingerprints.Proposed(proposed)));

        Assert.Equal(ProjectWriteOutcome.Failed, result.Outcome);
        Assert.Equal(project.StateFingerprint, application.Status(path).StateFingerprint);
    }

    [Fact]
    public void A_rule_invalid_baseline_can_be_opened_and_atomically_repaired()
    {
        using var workspace = new RuleWorkspace();
        var store = new SqliteProjectStore();
        var path = workspace.PathFor("repair.vw.db");
        var purpose = new GraphNode(new EntityId("purpose"), "Purpose", "purpose");
        var claim = new GraphNode(new EntityId("claim"), "Claim", "claim");
        var rule = new GraphNode(new EntityId("rule"), "Every claim must be confirmed.", RuleProtocol.RuleKind,
            [RuleProtocol.ActiveTag], [
                new("rule:version", GraphValue.FromInteger(1)),
                new("rule:expression", GraphValue.FromText("{\"all\":{\"set\":{\"nodes\":{\"kind\":\"claim\"}},\"condition\":{\"hasTag\":\"confirmed\"}}}"))]);
        var graph = new ProjectGraph(new ProjectId("repair"), "Repair", purpose.Id, [purpose, claim, rule], [
            new GraphEdge(new EntityId("claim-scope"), claim.Id, purpose.Id, "scope-parent", ReviewDirection.None),
            new GraphEdge(new EntityId("rule-scope"), rule.Id, purpose.Id, "scope-parent", ReviewDirection.None)]);
        var baseline = store.Initialize(path, graph);
        Assert.False(store.Verify(path).IsValid);

        var replacement = new GraphNode(claim.Id, claim.Text, claim.Kind, ["confirmed"]);
        var operations = new GraphOperationBatch([GraphOperation.ReplaceNode(replacement)]);
        var proposed = new GraphProjector().Project(graph, operations).Graph;
        var result = store.Write(new ProjectWriteRequest(path, graph.ProjectId, baseline.StateFingerprint,
            operations, GraphFingerprints.Proposed(proposed)));

        Assert.Equal(ProjectWriteOutcome.Written, result.Outcome);
        Assert.True(store.Verify(path).IsValid);
    }

    private sealed class RuleWorkspace : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "validated-world-rules-" + Guid.NewGuid().ToString("N"));
        public RuleWorkspace() => Directory.CreateDirectory(_path);
        public string PathFor(string name) => Path.Combine(_path, name);
        public void Dispose() => Directory.Delete(_path, recursive: true);
    }
}
