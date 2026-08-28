using System.Text.Json.Nodes;
using ValidatedWorld.Application;
using ValidatedWorld.Persistence.Sqlite;

namespace ValidatedWorld.Cli.Tests;

[Collection("Live OpenAI")]
public sealed class LiveAuthoringTests
{
    [Fact]
    [Trait("Category", "LiveOpenAI")]
    public async Task Configured_live_author_handles_minimal_new_and_existing_projects()
    {
        var configuration = AiAuthoringConfiguration.Load();
        if (!configuration.LiveTests || !configuration.IsEffectivelyEnabled)
            return;

        using var workspace = new TemporaryWorkspace();
        using var httpClient = new HttpClient { BaseAddress = new Uri("https://api.openai.com/") };
        var requestLog = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "ai-authoring-live-request.json");
        var responseLog = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "ai-authoring-live-response.json");
        var provider = configuration.CreateProvider(httpClient, requestLog, responseLog)!;

        var newPath = Path.Combine(workspace.Path, "new-lore.vw.db");
        var newTranscript = await RunShell(
            provider,
            new ProjectApplication(new SqliteProjectStore()),
            newPath,
            "Create a new project with stable ID tiny-lore, title Tiny Lore, purpose ID purpose, and purpose text 'Keep a tiny coherent lore graph.' Do not begin another change.",
            approve: true);
        Assert.True(File.Exists(newPath), newTranscript);
        Assert.Contains("tiny-lore", newTranscript, StringComparison.OrdinalIgnoreCase);

        var existingPath = Path.Combine(workspace.Path, "technical.vw.db");
        var reviewer = new AllowReviewer();
        var existingApplication = new ProjectApplication(
            new SqliteProjectStore(),
            semanticReviewProvider: reviewer,
            semanticReviewOptions: new SemanticReviewRuntimeOptions(
                Enabled: true, Configured: true, Model: reviewer.Model));
        existingApplication.CreateSample(SampleProjectCatalog.TechnicalProject, existingPath);
        var original = existingApplication.Load(existingPath).Graph;
        _ = await RunShell(
            provider,
            existingApplication,
            existingPath,
            "Search first, then add exactly one note node with stable ID power-maintenance-note, text 'Inspect the power enclosure before maintenance.', kind note, no tags or attributes. Add exactly one scope-parent edge with stable ID power-maintenance-note-parent from that node to scope-power, review direction none, and no rationale, tags, or attributes. Make no other changes. Request approval and write the change.",
            approve: true);
        var updated = existingApplication.Load(existingPath).Graph;
        Assert.Contains(updated.Nodes, node =>
            node.Id.Value == "power-maintenance-note" &&
            node.Text == "Inspect the power enclosure before maintenance.");
        Assert.Contains(updated.Edges, edge =>
            edge.Id.Value == "power-maintenance-note-parent" &&
            edge.Source.Value == "power-maintenance-note" &&
            edge.Target.Value == "scope-power");
        Assert.Equal(original.Nodes.Count + 1, updated.Nodes.Count);
        Assert.Equal(original.Edges.Count + 1, updated.Edges.Count);
        Assert.All(original.Nodes, node => Assert.Contains(node, updated.Nodes));
        Assert.All(original.Edges, edge => Assert.Contains(edge, updated.Edges));
        Assert.Equal(1, reviewer.CallCount);
        Assert.Empty(existingApplication.GetExitWarnings());

        Assert.True(File.Exists(requestLog));
        Assert.True(File.Exists(responseLog));
        var outbound = JsonNode.Parse(await File.ReadAllTextAsync(requestLog))!;
        Assert.True(outbound["background"]!.GetValue<bool>());
        Assert.False(outbound["parallel_tool_calls"]!.GetValue<bool>());
        Assert.Contains(outbound["tools"]!.AsArray(), tool =>
            tool!["name"]!.GetValue<string>() == "search_graph" && tool["strict"]!.GetValue<bool>());
        Assert.Null(outbound["authorization"]);
        Assert.Null(outbound["apiKey"]);
    }

    private static async Task<string> RunShell(
        IAuthoringAgentProvider provider,
        ProjectApplication application,
        string path,
        string prompt,
        bool approve)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var shell = new AiAssistantShell(
            provider,
            new AuthoringToolHost(application, path, Guid.NewGuid().ToString("N")),
            new StringReader(prompt + Environment.NewLine + (approve ? "yes" + Environment.NewLine : string.Empty) +
                "exit" + Environment.NewLine),
            output,
            error,
            cancellationToken: timeout.Token);
        Assert.Equal(CliRunner.SuccessExitCode, await shell.RunAsync());
        Assert.DoesNotContain("error[", error.ToString(), StringComparison.OrdinalIgnoreCase);
        return output + error.ToString();
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        public TemporaryWorkspace()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"ValidatedWorld.T13.Live-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class AllowReviewer : ISemanticReviewProvider
    {
        public int CallCount { get; private set; }
        public string Provider => "offline";
        public string Model => "allow-reviewer";

        public Task<SemanticReviewProviderResult> ReviewAsync(
            SemanticReviewPlannedRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new SemanticReviewProviderResult(
                SemanticReviewStatus.Complete,
                SemanticReviewDecision.Allow,
                "The offline independent reviewer allows this exact proposal.",
                [],
                null,
                "offline-review-response",
                TimeSpan.Zero));
        }
    }
}
