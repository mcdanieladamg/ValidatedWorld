using ValidatedWorld.Application;
using ValidatedWorld.Core;
using ValidatedWorld.Serialization;
using ValidatedWorld.Validation;

namespace ValidatedWorld.Application.Tests;

public sealed class CodeDevelopmentTemplateTests
{
    [Fact]
    public void Code_template_accepts_planning_active_and_finished_states_and_rejects_roadmap_defects()
    {
        var template = GraphTemplateCatalog.Resolve(GraphTemplateCatalog.CodeDevelopment);
        var planning = GraphTemplateCatalog.Instantiate(template, new ProjectId("code"), "Code", "Purpose");
        AssertValid(planning);

        var active = Project(planning,
            ReplaceStatus(planning, ["project:status", "status:active", "current-phase:t1"]),
            GraphOperation.AddNode(Node("phase-1", "First", "development-phase", ["roadmap:phase", "phase:t1", "status:current", "estimate:small"])),
            GraphOperation.AddEdge(Scope("phase-1")),
            GraphOperation.AddNode(Node("phase-2", "Second", "development-phase", ["roadmap:phase", "phase:t2", "status:pending"])),
            GraphOperation.AddEdge(Scope("phase-2")),
            GraphOperation.AddEdge(new GraphEdge(new EntityId("phase-1-before-2"), new EntityId("phase-1"), new EntityId("phase-2"), "precedes", ReviewDirection.None)),
            GraphOperation.AddEdge(new GraphEdge(new EntityId("current-phase"), new EntityId("project-status"), new EntityId("phase-1"), "current-phase", ReviewDirection.None)));
        AssertValid(active);

        AssertInvalid(Project(active,
            GraphOperation.ReplaceNode(Node("phase-2", "Second", "development-phase", ["roadmap:phase", "phase:t2", "status:pending", "status:current"]))));
        AssertInvalid(Project(active,
            GraphOperation.ReplaceNode(Node("phase-2", "Second", "development-phase", ["roadmap:phase", "phase:t2", "status:current", "estimate:medium"]))));
        AssertInvalid(Project(active,
            GraphOperation.ReplaceNode(Node("project-status", "Status", "project-status", ["project:status", "status:active", "current-phase:wrong"]))));
        AssertInvalid(Project(active,
            GraphOperation.ReplaceNode(Node("phase-1", "First", "development-phase", ["roadmap:phase", "phase:t1", "status:current", "estimate:unknown"]))));

        var finished = Project(active,
            GraphOperation.ReplaceNode(Node("project-status", "Finished", "project-status", ["project:status", "status:finished"])),
            GraphOperation.ReplaceNode(Node("phase-1", "First", "development-phase", ["roadmap:phase", "phase:t1", "status:complete"])),
            GraphOperation.ReplaceNode(Node("phase-2", "Second", "development-phase", ["roadmap:phase", "phase:t2", "status:complete"])),
            GraphOperation.RemoveEdge(new EntityId("current-phase")));
        AssertValid(finished);
    }

    [Fact]
    public void A_user_authored_template_is_strictly_parsed_and_keeps_its_custom_rule()
    {
        var json = """
            {"version":1,"id":"custom-notes","description":"One required note.","purposeNodeId":"purpose",
             "nodes":[
               {"id":"notes","text":"Notes","kind":"scope","tags":[],"attributes":[]},
               {"id":"note","text":"Required note","kind":"note","tags":[],"attributes":[]},
               {"id":"rule-note","text":"Exactly one note is required.","kind":"validation-rule","tags":["rule:active"],"attributes":[
                 {"name":"rule:expression","value":{"kind":0,"text":"{\"count\":{\"set\":{\"nodes\":{\"kind\":\"note\"}},\"compare\":\"eq\",\"value\":1}}","integer":0,"boolean":false,"instant":null}},
                 {"name":"rule:version","value":{"kind":1,"text":null,"integer":1,"boolean":false,"instant":null}}]}],
             "edges":[
               {"id":"notes-scope","source":"notes","target":"purpose","relationship":"scope-parent","reviewDirection":0,"rationale":null,"tags":[],"attributes":[]},
               {"id":"note-scope","source":"note","target":"notes","relationship":"scope-parent","reviewDirection":0,"rationale":null,"tags":[],"attributes":[]},
               {"id":"rule-scope","source":"rule-note","target":"notes","relationship":"scope-parent","reviewDirection":0,"rationale":null,"tags":[],"attributes":[]}]}
            """;
        var template = TemplateProtocol.Parse(json);
        var graph = GraphTemplateCatalog.Instantiate(template, new ProjectId("notes"), "Notes", "Take notes");
        AssertValid(graph);
        AssertInvalid(Project(graph,
            GraphOperation.RemoveEdge(new EntityId("note-scope")),
            GraphOperation.RemoveNode(new EntityId("note"))));
    }

    private static GraphOperation ReplaceStatus(ProjectGraph graph, IReadOnlyList<string> tags)
    {
        var old = graph.Nodes.Single(node => node.Id.Value == "project-status");
        return GraphOperation.ReplaceNode(new GraphNode(old.Id, old.Text, old.Kind, tags, old.Attributes.Select(a => new KeyValuePair<string, GraphValue>(a.Name, a.Value))));
    }
    private static GraphNode Node(string id, string text, string kind, IReadOnlyList<string> tags) => new(new EntityId(id), text, kind, tags);
    private static GraphEdge Scope(string child) => new(new EntityId(child + "-scope"), new EntityId(child), new EntityId("scope-roadmap"), "scope-parent", ReviewDirection.None);
    private static ProjectGraph Project(ProjectGraph graph, params GraphOperation[] operations) => new GraphProjector().Project(graph, operations).Graph;
    private static void AssertValid(ProjectGraph graph) => Assert.True(new GraphRuleValidator().Validate(graph, RuleProtocol.Parse(graph)).IsValid);
    private static void AssertInvalid(ProjectGraph graph) => Assert.Equal(ValidationStatus.Invalid, new GraphRuleValidator().Validate(graph, RuleProtocol.Parse(graph)).Status);
}
