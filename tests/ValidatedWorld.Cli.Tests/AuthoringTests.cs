using System.Text.Json;
using ValidatedWorld.Application;
using ValidatedWorld.Cli;
using ValidatedWorld.Core;
using ValidatedWorld.Persistence.Sqlite;

namespace ValidatedWorld.Cli.Tests;

public sealed class AuthoringTests
{
    [Fact]
    public void Defaults_are_ai_first_but_paid_tests_are_opt_in_and_tools_are_guarded()
    {
        Assert.True(AiAuthoringConfiguration.DefaultEnabled);
        Assert.False(AiAuthoringConfiguration.DefaultLiveTests);
        Assert.Equal(32, AiAuthoringConfiguration.DefaultMaxToolCallsPerTurn);

        var names = AuthoringToolHost.Definitions.Select(tool => tool.Name).ToArray();
        Assert.Contains("request_approval", names);
        Assert.Contains("write_change", names);
        Assert.DoesNotContain(names, name => name.Contains("sql", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("bypass", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("review_change", names);
        Assert.DoesNotContain(names, name => name.Contains("disposition", StringComparison.OrdinalIgnoreCase));
        Assert.All(AuthoringToolHost.Definitions, tool =>
        {
            Assert.False(tool.Parameters.GetProperty("additionalProperties").GetBoolean());
            Assert.Equal(JsonValueKind.Array, tool.Parameters.GetProperty("required").ValueKind);
        });
    }

    [Fact]
    public async Task Search_is_required_and_exact_approval_goes_stale_after_any_patch()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vw-authoring-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = System.IO.Path.Combine(root, "project.vw.db");
            var reviewer = new AllowReviewer();
            var application = new ProjectApplication(
                new SqliteProjectStore(),
                semanticReviewProvider: reviewer,
                semanticReviewOptions: new SemanticReviewRuntimeOptions(
                    Enabled: true, Configured: true, Model: reviewer.Model));
            application.CreateSample(SampleProjectCatalog.TechnicalProject, path);
            var host = new AuthoringToolHost(application, path, "conversation-test");

            Assert.True(Json(await host.ExecuteAsync("project_status", Object("{}"))).GetProperty("exists").GetBoolean());
            await host.ExecuteAsync("begin_change", Object("""{"intent":"Add a focused power note"}"""));
            var rejected = Json(await host.ExecuteAsync("put_node", Object(Node("add", "power-note", "Initial note"))));
            Assert.False(rejected.GetProperty("ok").GetBoolean());
            Assert.Contains("Search", rejected.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);

            await host.ExecuteAsync("search_graph", Object("""{"text":"power note","tag":null,"limit":10}"""));
            Assert.False(Json(await host.ExecuteAsync("put_node", Object(Node("add", "power-note", "Initial note"))))
                .TryGetProperty("ok", out _));
            await host.ExecuteAsync("put_edge", Object("""{"mode":"add","id":"power-note-parent","source":"power-note","target":"scope-power","relationship":"scope-parent","review_direction":"none","rationale":null,"tags":[],"attributes":[]}"""));

            var humanPreview = host.HumanPreview();
            Assert.Contains("Initial note", humanPreview, StringComparison.Ordinal);
            Assert.Contains("scope-parent", humanPreview, StringComparison.Ordinal);
            Assert.Contains("Base fingerprint", humanPreview, StringComparison.Ordinal);

            host.ApproveRequested();
            Assert.True(host.Session!.Readiness.IsReady);
            Assert.All(host.Session.Dispositions, value => Assert.NotEqual(
                ValidatedWorld.Validation.ReviewDispositionKind.Pending, value.Kind));

            await host.ExecuteAsync("put_node", Object(Node("replace", "power-note", "Revised after approval")));
            var staleWrite = Json(await host.ExecuteAsync("write_change", Object("{}")));
            Assert.False(staleWrite.GetProperty("ok").GetBoolean());
            Assert.Contains("approval", staleWrite.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, reviewer.CallCount);

            host.ApproveRequested();
            var written = Json(await host.ExecuteAsync("write_change", Object("{}")));
            Assert.Equal("written", written.GetProperty("result").GetProperty("status").GetString());
            Assert.False(written.GetProperty("result").GetProperty("aiReviewBypassed").GetBoolean());
            Assert.Equal(1, reviewer.CallCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Assistant_can_ask_a_material_question_without_mutating_the_project()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vw-question-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = System.IO.Path.Combine(root, "project.vw.db");
            var application = new ProjectApplication(new SqliteProjectStore());
            application.CreateSample(SampleProjectCatalog.TechnicalProject, path);
            var provider = new ScriptedProvider(new AuthoringAgentResponse(
                "response-1",
                "Should the new value replace the measured result or only its planning assumption?",
                null,
                null,
                TimeSpan.Zero));
            var input = new StringReader("Change the battery value\nexit\n");
            var output = new StringWriter();
            var error = new StringWriter();
            var shell = new AiAssistantShell(
                provider,
                new AuthoringToolHost(application, path, "question-conversation"),
                input,
                output,
                error);

            Assert.Equal(0, await shell.RunAsync());
            Assert.Contains("replace the measured result", output.ToString(), StringComparison.Ordinal);
            Assert.Empty(application.GetExitWarnings());
            Assert.Empty(error.ToString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Bounded_search_duplicate_protection_and_authoring_session_loss_are_explicit()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vw-authoring-bounds-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = System.IO.Path.Combine(root, "project.vw.db");
            var application = new ProjectApplication(new SqliteProjectStore());
            application.CreateSample(SampleProjectCatalog.TechnicalProject, path);
            var host = new AuthoringToolHost(application, path, "bounds-conversation");

            var overLimit = Json(await host.ExecuteAsync(
                "search_graph", Object("""{"text":"power","tag":null,"limit":51}""")));
            Assert.False(overLimit.GetProperty("ok").GetBoolean());
            Assert.Contains("between 1 and 50", overLimit.GetProperty("error").GetString(), StringComparison.Ordinal);

            await host.ExecuteAsync("search_graph", Object("""{"text":"battery-assumption","tag":null,"limit":10}"""));
            await host.ExecuteAsync("begin_change", Object("""{"intent":"Try an accidental duplicate"}"""));
            var duplicate = Json(await host.ExecuteAsync(
                "put_node", Object(Node("add", "battery-assumption", "Duplicate"))));
            Assert.False(duplicate.GetProperty("ok").GetBoolean());
            await host.ExecuteAsync("discard_change", Object("{}"));

            var provider = new ScriptedProvider(
                new AuthoringAgentResponse(
                    "response-begin", null,
                    new AuthoringToolCall("call-begin", "begin_change", Object("""{"intent":"Unfinished work"}""")),
                    null, TimeSpan.Zero),
                new AuthoringAgentResponse("response-done", "A change session is open.", null, null, TimeSpan.Zero));
            var error = new StringWriter();
            var shell = new AiAssistantShell(
                provider,
                new AuthoringToolHost(application, path, "session-loss-conversation"),
                new StringReader("Begin\nexit\n"),
                new StringWriter(),
                error);
            Assert.Equal(0, await shell.RunAsync());
            Assert.Contains("warning[session-loss]", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task New_project_is_only_created_after_the_application_receives_human_approval()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vw-new-project-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = System.IO.Path.Combine(root, "new.vw.db");
            var host = new AuthoringToolHost(
                new ProjectApplication(new SqliteProjectStore()), path, "new-project-conversation");
            var arguments = Object("""{"project_id":"tiny","title":"Tiny","purpose_id":"purpose","purpose_text":"Keep the graph coherent."}""");

            var prepared = await host.ExecuteAsync("initialize_project", arguments);
            Assert.True(prepared.ApprovalRequested);
            Assert.False(File.Exists(path));
            Assert.Contains("Keep the graph coherent", host.HumanPreview(), StringComparison.Ordinal);
            host.DeclineRequested();
            Assert.False(File.Exists(path));

            await host.ExecuteAsync("initialize_project", arguments);
            host.ApproveRequested();
            Assert.True(File.Exists(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Human_preview_includes_old_and_new_scope_lineages_and_path_edges()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vw-reparent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = System.IO.Path.Combine(root, "project.vw.db");
            var application = new ProjectApplication(new SqliteProjectStore());
            application.CreateSample(SampleProjectCatalog.TechnicalProject, path);
            var host = new AuthoringToolHost(application, path, "reparent-conversation");
            await host.ExecuteAsync("begin_change", Object("""{"intent":"Move the runtime test into privacy scope"}"""));
            await host.ExecuteAsync("put_edge", Object("""{"mode":"replace","id":"runtime-scope-parent","source":"runtime-test","target":"scope-privacy","relationship":"scope-parent","review_direction":"none","rationale":null,"tags":[],"attributes":[]}"""));

            var preview = host.HumanPreview();
            Assert.Contains("path edges:", preview, StringComparison.Ordinal);
            Assert.Contains("current path: runtime-test -> scope-power -> purpose", preview, StringComparison.Ordinal);
            Assert.Contains("proposed path: runtime-test -> scope-privacy -> purpose", preview, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Independent_block_requires_a_repaired_and_reapproved_proposal()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vw-block-repair-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = System.IO.Path.Combine(root, "project.vw.db");
            var reviewer = new BlockThenAllowReviewer();
            var application = new ProjectApplication(
                new SqliteProjectStore(),
                semanticReviewProvider: reviewer,
                semanticReviewOptions: new SemanticReviewRuntimeOptions(
                    Enabled: true, Configured: true, Model: reviewer.Model));
            application.CreateSample(SampleProjectCatalog.TechnicalProject, path);
            var host = new AuthoringToolHost(application, path, "block-repair-conversation");
            await host.ExecuteAsync("begin_change", Object("""{"intent":"Clarify the battery assumption"}"""));
            await host.ExecuteAsync("put_node", Object(Node(
                "replace", "battery-assumption", "The battery target needs clarification.")));
            host.ApproveRequested();

            var blocked = Json(await host.ExecuteAsync("write_change", Object("{}")));
            Assert.Equal("semanticReviewBlocked", blocked.GetProperty("result").GetProperty("status").GetString());
            Assert.Equal(1, reviewer.CallCount);

            await host.ExecuteAsync("put_node", Object(Node(
                "replace", "battery-assumption", "The battery lasts for the target duty cycle.")));
            var stale = Json(await host.ExecuteAsync("write_change", Object("{}")));
            Assert.False(stale.GetProperty("ok").GetBoolean());
            host.ApproveRequested();
            var written = Json(await host.ExecuteAsync("write_change", Object("{}")));
            Assert.Equal("written", written.GetProperty("result").GetProperty("status").GetString());
            Assert.Equal(2, reviewer.CallCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Tool_limit_allows_a_final_response_and_provider_failure_resets_only_conversation_context()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vw-turn-limit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = System.IO.Path.Combine(root, "project.vw.db");
            var application = new ProjectApplication(new SqliteProjectStore());
            application.CreateSample(SampleProjectCatalog.TechnicalProject, path);

            var boundedProvider = new ScriptedProvider(
                new AuthoringAgentResponse("response-1", null,
                    new AuthoringToolCall("call-1", "project_status", Object("{}")), null, TimeSpan.Zero),
                new AuthoringAgentResponse("response-2", "Inspection finished.", null, null, TimeSpan.Zero));
            var boundedOutput = new StringWriter();
            var boundedError = new StringWriter();
            var boundedShell = new AiAssistantShell(
                boundedProvider,
                new AuthoringToolHost(application, path, "bounded-conversation"),
                new StringReader("Inspect\nexit\n"),
                boundedOutput,
                boundedError,
                maxToolCallsPerTurn: 1);
            Assert.Equal(0, await boundedShell.RunAsync());
            Assert.Contains("Inspection finished", boundedOutput.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("tool-limit", boundedError.ToString(), StringComparison.Ordinal);

            var recoveringProvider = new RecoveringProvider();
            var recoveringError = new StringWriter();
            var recoveringShell = new AiAssistantShell(
                recoveringProvider,
                new AuthoringToolHost(application, path, "recovering-conversation"),
                new StringReader("Inspect\nContinue\nexit\n"),
                new StringWriter(),
                recoveringError);
            Assert.Equal(0, await recoveringShell.RunAsync());
            Assert.Null(recoveringProvider.RecoveryPreviousResponseId);
            Assert.Contains("warning[conversation-reset]", recoveringError.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Outbound_request_contains_standalone_instructions_and_strict_bounded_tools()
    {
        var provider = new OpenAiResponsesAuthoringProvider(
            new HttpClient(), "offline-key", pollInterval: TimeSpan.Zero);
        var request = new AuthoringAgentRequest(
            JsonSerializer.SerializeToElement("Inspect the project"),
            null,
            AuthoringToolHost.Definitions);

        var serialized = provider.SerializeOutboundRequest(request);
        Assert.Contains("search before creating", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("request_approval", serialized, StringComparison.Ordinal);
        Assert.Contains("\"parallel_tool_calls\":false", serialized, StringComparison.Ordinal);
        Assert.Contains("\"strict\":true", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("bypassAiReview", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("offline-key", serialized, StringComparison.Ordinal);
    }

    private static string Node(string mode, string id, string text) =>
        $$"""{"mode":"{{mode}}","id":"{{id}}","text":"{{text}}","kind":"note","tags":[],"attributes":[]}""";

    private static JsonElement Object(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static JsonElement Json(AuthoringToolExecution result)
    {
        using var document = JsonDocument.Parse(result.Output);
        return document.RootElement.Clone();
    }

    private sealed class ScriptedProvider(params AuthoringAgentResponse[] responses) : IAuthoringAgentProvider
    {
        private readonly Queue<AuthoringAgentResponse> _responses = new(responses);
        public string Provider => "scripted";
        public string Model => "offline";
        public Task<AuthoringAgentResponse> RespondAsync(
            AuthoringAgentRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(_responses.Dequeue());
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
                "The offline reviewer allows this exact proposal.",
                [],
                null,
                "offline-response",
                TimeSpan.Zero));
        }
    }

    private sealed class BlockThenAllowReviewer : ISemanticReviewProvider
    {
        public int CallCount { get; private set; }
        public string Provider => "offline";
        public string Model => "block-then-allow";

        public Task<SemanticReviewProviderResult> ReviewAsync(
            SemanticReviewPlannedRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            var decision = CallCount == 1 ? SemanticReviewDecision.Block : SemanticReviewDecision.Allow;
            return Task.FromResult(new SemanticReviewProviderResult(
                SemanticReviewStatus.Complete,
                decision,
                decision == SemanticReviewDecision.Block ? "Repair the ambiguous assumption." : "Repair accepted.",
                decision == SemanticReviewDecision.Block
                    ? [new SemanticReviewConcern("ambiguous", "Clarify the assumption.", [new EntityId("battery-assumption")])]
                    : [],
                null,
                $"offline-response-{CallCount}",
                TimeSpan.Zero));
        }
    }

    private sealed class RecoveringProvider : IAuthoringAgentProvider
    {
        private int _callCount;
        public string Provider => "scripted";
        public string Model => "offline";
        public string? RecoveryPreviousResponseId { get; private set; }

        public Task<AuthoringAgentResponse> RespondAsync(
            AuthoringAgentRequest request,
            CancellationToken cancellationToken = default)
        {
            _callCount++;
            return _callCount switch
            {
                1 => Task.FromResult(new AuthoringAgentResponse(
                    "response-before-failure", null,
                    new AuthoringToolCall("call-before-failure", "project_status", Object("{}")),
                    null, TimeSpan.Zero)),
                2 => throw new AuthoringProviderException("transport", "Simulated transport interruption."),
                _ => Recover(request),
            };
        }

        private Task<AuthoringAgentResponse> Recover(AuthoringAgentRequest request)
        {
            RecoveryPreviousResponseId = request.PreviousResponseId;
            return Task.FromResult(new AuthoringAgentResponse(
                "response-after-failure", "Recovered.", null, null, TimeSpan.Zero));
        }
    }
}
