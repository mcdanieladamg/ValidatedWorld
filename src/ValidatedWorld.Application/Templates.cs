using System.Collections.ObjectModel;
using ValidatedWorld.Core;
using ValidatedWorld.Serialization;

namespace ValidatedWorld.Application;

public sealed record TemplateDescriptor(string Id, string Description, int Version, int NodeCount, int EdgeCount);

public static class GraphTemplateCatalog
{
    public const string CodeDevelopment = "code-development";
    public const string ResearchNotebook = "research-notebook";
    private static readonly IReadOnlyDictionary<string, GraphTemplateDto> BuiltIns =
        new ReadOnlyDictionary<string, GraphTemplateDto>(new Dictionary<string, GraphTemplateDto>(StringComparer.Ordinal)
        {
            [CodeDevelopment] = CodeDevelopmentTemplate(),
            [ResearchNotebook] = ResearchTemplate(),
        });

    public static IReadOnlyList<TemplateDescriptor> List() => BuiltIns.Values.OrderBy(value => value.Id, StringComparer.Ordinal)
        .Select(Describe).ToArray();

    public static GraphTemplateDto Resolve(string nameOrPath)
    {
        if (BuiltIns.TryGetValue(nameOrPath, out var builtIn)) return builtIn;
        var fullPath = Path.GetFullPath(nameOrPath);
        if (!File.Exists(fullPath)) throw new ArgumentException($"Template '{nameOrPath}' is not a built-in name or existing JSON file.", nameof(nameOrPath));
        return TemplateProtocol.Parse(File.ReadAllText(fullPath));
    }

    public static TemplateDescriptor Describe(GraphTemplateDto value) =>
        new(value.Id, value.Description, value.Version, value.Nodes.Count, value.Edges.Count);

    public static ProjectGraph Instantiate(GraphTemplateDto template, ProjectId projectId, string title, string purposeText)
    {
        var purposeId = new EntityId(template.PurposeNodeId);
        if (template.Nodes.Any(node => node.Id == template.PurposeNodeId))
            throw new ArgumentException("Template nodes must not redefine the supplied purpose node.", nameof(template));
        var nodes = new[] { new GraphNode(purposeId, purposeText, "purpose") }
            .Concat(template.Nodes.Select(GraphProtocol.FromDto)).ToArray();
        var edges = template.Edges.Select(GraphProtocol.FromDto).ToArray();
        return new ProjectGraph(projectId, title, purposeId, nodes, edges);
    }

    private static GraphTemplateDto CodeDevelopmentTemplate()
    {
        var nodes = new List<NodeDto>();
        var edges = new List<EdgeDto>();
        void Scope(string id, string text)
        {
            nodes.Add(Node(id, text, "scope"));
            edges.Add(Edge($"{id}-scope", id, "purpose", "scope-parent"));
        }
        Scope("scope-architecture", "Implemented architecture and accepted technical decisions.");
        Scope("scope-contracts", "Public behavior, constraints, and compatibility contracts.");
        Scope("scope-evidence", "Source, tests, documentation, and other implementation evidence.");
        Scope("scope-uncertainties", "Open questions, uncertainty, and claims that still require evidence.");
        Scope("scope-roadmap", "Project lifecycle, status, and ordered implementation phases.");
        nodes.Add(Node("project-status", "The project is being documented. Evidence, uncertainty, and a governed roadmap are not yet complete.",
            "project-status", ["project:status", "status:planning"]));
        edges.Add(Edge("project-status-scope", "project-status", "scope-roadmap", "scope-parent"));

        AddView("phases", "{\"nodes\":{\"kind\":\"development-phase\",\"tagsAll\":[\"roadmap:phase\"]}}");
        AddView("current-phases", "{\"nodes\":{\"kind\":\"development-phase\",\"tagsAll\":[\"roadmap:phase\",\"status:current\"]}}");
        AddView("estimate-nodes", "{\"nodes\":{\"tagPrefix\":\"estimate:\"}}");
        AddView("valid-estimate-nodes", "{\"union\":[{\"nodes\":{\"tagsAll\":[\"estimate:small\"]}},{\"nodes\":{\"tagsAll\":[\"estimate:medium\"]}},{\"nodes\":{\"tagsAll\":[\"estimate:large\"]}},{\"nodes\":{\"tagsAll\":[\"estimate:gigantic\"]}}]}");
        AddView("pending-phases", "{\"nodes\":{\"kind\":\"development-phase\",\"tagsAll\":[\"roadmap:phase\",\"status:pending\"]}}");
        AddView("complete-phases", "{\"nodes\":{\"kind\":\"development-phase\",\"tagsAll\":[\"roadmap:phase\",\"status:complete\"]}}");
        AddView("precedes-edges", "{\"edges\":{\"relationship\":\"precedes\",\"sourceIn\":{\"view\":\"phases\"},\"targetIn\":{\"view\":\"phases\"}}}");
        AddRule("roadmap-phase-state", "Every roadmap phase must have exactly one pending, current, or complete state tag.",
            "{\"all\":{\"set\":{\"view\":\"phases\"},\"condition\":{\"tagCount\":{\"prefix\":\"status:\",\"compare\":\"eq\",\"value\":1}}}}");
        AddRule("roadmap-phase-id", "Every roadmap phase must have exactly one phase identifier tag.",
            "{\"all\":{\"set\":{\"view\":\"phases\"},\"condition\":{\"tagCount\":{\"prefix\":\"phase:\",\"compare\":\"eq\",\"value\":1}}}}");
        AddRule("roadmap-status-unique", "There must be one project-status node with exactly one lifecycle state tag.",
            "{\"and\":[{\"count\":{\"set\":{\"nodes\":{\"id\":\"project-status\",\"tagsAll\":[\"project:status\"]}},\"compare\":\"eq\",\"value\":1}},{\"all\":{\"set\":{\"nodes\":{\"id\":\"project-status\"}},\"condition\":{\"tagCount\":{\"prefix\":\"status:\",\"compare\":\"eq\",\"value\":1}}}}]}");
        AddRule("roadmap-current-lifecycle", "Planning has no current phase, active has exactly one, and finished has no unfinished phase.",
            "{\"or\":[{\"and\":[{\"exists\":{\"nodes\":{\"id\":\"project-status\",\"tagsAll\":[\"status:planning\"]}}},{\"count\":{\"set\":{\"view\":\"current-phases\"},\"compare\":\"eq\",\"value\":0}}]},{\"and\":[{\"exists\":{\"nodes\":{\"id\":\"project-status\",\"tagsAll\":[\"status:active\"]}}},{\"count\":{\"set\":{\"view\":\"current-phases\"},\"compare\":\"eq\",\"value\":1}}]},{\"and\":[{\"exists\":{\"nodes\":{\"id\":\"project-status\",\"tagsAll\":[\"status:finished\"]}}},{\"count\":{\"set\":{\"union\":[{\"view\":\"current-phases\"},{\"view\":\"pending-phases\"}]},\"compare\":\"eq\",\"value\":0}}]}]}");
        AddRule("roadmap-estimate-placement", "Only the current phase may carry an estimate tag.",
            "{\"subset\":[{\"view\":\"estimate-nodes\"},{\"view\":\"current-phases\"}]}");
        AddRule("roadmap-current-estimate", "Each current phase must carry exactly one estimate tag.",
            "{\"all\":{\"set\":{\"view\":\"current-phases\"},\"condition\":{\"tagCount\":{\"prefix\":\"estimate:\",\"compare\":\"eq\",\"value\":1}}}}");
        AddRule("roadmap-estimate-values", "Estimates use only small, medium, large, or gigantic.",
            "{\"subset\":[{\"view\":\"estimate-nodes\"},{\"view\":\"valid-estimate-nodes\"}]}");
        AddRule("roadmap-chain", "Roadmap phases and precedes edges must form one acyclic chain.",
            "{\"singleChain\":{\"nodes\":{\"view\":\"phases\"},\"edges\":{\"view\":\"precedes-edges\"}}}");
        AddRule("roadmap-current-pointer", "The current-phase edge is absent without a current phase, otherwise exactly one connects project-status to it.",
            "{\"or\":[{\"and\":[{\"count\":{\"set\":{\"view\":\"current-phases\"},\"compare\":\"eq\",\"value\":0}},{\"count\":{\"set\":{\"edges\":{\"relationship\":\"current-phase\"}},\"compare\":\"eq\",\"value\":0}}]},{\"and\":[{\"count\":{\"set\":{\"edges\":{\"relationship\":\"current-phase\"}},\"compare\":\"eq\",\"value\":1}},{\"count\":{\"set\":{\"edges\":{\"relationship\":\"current-phase\",\"sourceIn\":{\"nodes\":{\"id\":\"project-status\"}},\"targetIn\":{\"view\":\"current-phases\"}}},\"compare\":\"eq\",\"value\":1}}]}]}");
        AddRule("roadmap-order", "Complete phases precede the current phase and pending phases follow it.",
            "{\"or\":[{\"count\":{\"set\":{\"view\":\"current-phases\"},\"compare\":\"eq\",\"value\":0}},{\"and\":[{\"subset\":[{\"view\":\"complete-phases\"},{\"reachable\":{\"from\":{\"view\":\"current-phases\"},\"edges\":{\"view\":\"precedes-edges\"},\"direction\":\"incoming\"}}]},{\"subset\":[{\"view\":\"pending-phases\"},{\"reachable\":{\"from\":{\"view\":\"current-phases\"},\"edges\":{\"view\":\"precedes-edges\"},\"direction\":\"outgoing\"}}]}]}]}");
        AddRule("roadmap-pointer-tag", "The project-status current-phase tag must match the current phase's phase tag.",
            "{\"or\":[{\"count\":{\"set\":{\"view\":\"current-phases\"},\"compare\":\"eq\",\"value\":0}},{\"tagSuffixMatch\":{\"left\":{\"nodes\":{\"id\":\"project-status\"}},\"leftPrefix\":\"current-phase:\",\"right\":{\"view\":\"current-phases\"},\"rightPrefix\":\"phase:\"}}]}");
        return new GraphTemplateDto(1, CodeDevelopment,
            "Governed software-project knowledge with evidence, uncertainty, public contracts, architecture, and an ordered roadmap.",
            "purpose", nodes, edges);

        void AddView(string name, string expression)
        {
            var id = "view-" + name;
            nodes.Add(Node(id, $"Reusable selector for {name}.", RuleProtocol.ViewKind, [],
                [Attr("view:name", name), IntAttr("view:version", 1), Attr("view:expression", expression)]));
            edges.Add(Edge(id + "-scope", id, "scope-roadmap", "scope-parent"));
        }
        void AddRule(string idSuffix, string message, string expression)
        {
            var id = "rule-" + idSuffix;
            nodes.Add(Node(id, message, RuleProtocol.RuleKind, [RuleProtocol.ActiveTag],
                [IntAttr("rule:version", 1), Attr("rule:expression", expression)]));
            edges.Add(Edge(id + "-scope", id, "scope-roadmap", "scope-parent"));
        }
    }

    private static GraphTemplateDto ResearchTemplate()
    {
        var nodes = new[]
        {
            Node("scope-evidence", "Sources, observations, and measurements.", "scope"),
            Node("scope-claims", "Claims and conclusions supported by explicit evidence links.", "scope"),
            Node("scope-uncertainties", "Uncertainty, alternatives, and unresolved questions.", "scope"),
        };
        var edges = nodes.Select(node => Edge(node.Id + "-scope", node.Id, "purpose", "scope-parent")).ToArray();
        return new GraphTemplateDto(1, ResearchNotebook,
            "A domain-neutral research notebook separating evidence, claims, and uncertainty.", "purpose", nodes, edges);
    }

    private static NodeDto Node(string id, string text, string? kind, IReadOnlyList<string>? tags = null,
        IReadOnlyList<AttributeDto>? attributes = null) => new(id, text, kind, tags ?? [], attributes ?? []);
    private static EdgeDto Edge(string id, string source, string target, string relationship) =>
        new(id, source, target, relationship, ReviewDirection.None, null, [], []);
    private static AttributeDto Attr(string name, string value) => new(name, new ValueDto(GraphValueKind.Text, value, 0, false, null));
    private static AttributeDto IntAttr(string name, long value) => new(name, new ValueDto(GraphValueKind.Integer, null, value, false, null));
}

public sealed partial class ProjectApplication
{
    public IReadOnlyList<TemplateDescriptor> ListTemplates() => GraphTemplateCatalog.List();
    public GraphTemplateDto ReadTemplate(string nameOrPath) => GraphTemplateCatalog.Resolve(nameOrPath);

    public string ExportTemplate(string nameOrPath, string destinationPath)
    {
        var template = GraphTemplateCatalog.Resolve(nameOrPath);
        var fullPath = Path.GetFullPath(destinationPath);
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
            throw new ArgumentException($"The template destination already exists: '{fullPath}'.", nameof(destinationPath));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        using var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
        writer.Write(TemplateProtocol.Serialize(template));
        return fullPath;
    }

    public StoredProject InstantiateTemplate(string nameOrPath, string path, ProjectId projectId, string title, string purposeText)
    {
        var template = GraphTemplateCatalog.Resolve(nameOrPath);
        var graph = GraphTemplateCatalog.Instantiate(template, projectId, title, purposeText);
        var validation = ValidateGraph(graph);
        if (!validation.IsValid)
            throw new ProjectStorageException(ProjectStorageErrorCode.InvalidGraph,
                validation.Diagnostics.FirstOrDefault()?.Message ?? "The instantiated template is invalid.");
        return _store.Initialize(path, graph);
    }
}
