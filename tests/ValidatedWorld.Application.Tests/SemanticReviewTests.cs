using System.Net;
using System.Text;
using System.Text.Json;
using ValidatedWorld.Application;
using ValidatedWorld.Core;
using ValidatedWorld.Serialization;
using ValidatedWorld.Validation;

namespace ValidatedWorld.Application.Tests;

public sealed class SemanticReviewTests
{
    private static readonly DateTimeOffset FixedUtc =
        new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Planner_is_deterministic_complete_and_scope_aware_for_compact_lore_cases()
    {
        var (application, snapshot, _) = LoreSession(provider: null);

        var first = SemanticReviewPlanner.Plan(snapshot);
        var second = SemanticReviewPlanner.Plan(snapshot);

        Assert.Equal(first.SerializedRequest, second.SerializedRequest);
        Assert.Equal(first.RequestFingerprint, second.RequestFingerprint);
        Assert.Equal(snapshot.Operations.Operations.Count, first.Request.Manifest.OperationCount);
        Assert.Equal(snapshot.Affected.AffectedNodes.Count, first.Request.Manifest.AffectedNodeCount);
        Assert.Equal(snapshot.Affected.ScopeContext.Count, first.Request.Manifest.ContextNodeCount);
        Assert.Contains(first.Request.Operations, item => item.Operation.EntityId == "member-six");
        Assert.Contains(first.Request.Operations, item => item.Operation.EntityId == "roster");
        Assert.Contains(first.Request.Operations, item => item.Operation.EntityId == "canonical-name");
        Assert.Contains(first.Request.Operations, item => item.Operation.EntityId == "local-fact");
        Assert.Contains("\"kind\":\"replace\"", first.SerializedRequest, StringComparison.Ordinal);
        Assert.Contains("\"entityKind\":\"node\"", first.SerializedRequest, StringComparison.Ordinal);
        Assert.Contains("\"role\":\"directEdit\"", first.SerializedRequest, StringComparison.Ordinal);
        Assert.Contains("\"role\":\"semanticConsequence\"", first.SerializedRequest, StringComparison.Ordinal);
        Assert.Contains("\"role\":\"contextOnlyAncestor\"", first.SerializedRequest, StringComparison.Ordinal);
        Assert.Contains("\"reviewDirection\":\"sourceToTarget\"", first.SerializedRequest,
            StringComparison.Ordinal);
        Assert.Equal(2, first.Request.ScopeTopologyChanges.Count);
        var reparent = Assert.Single(first.Request.ScopeTopologyChanges, change => change.EdgeId == "local-parent");
        Assert.Equal("local-parent", reparent.EdgeId);
        Assert.Equal("scope-north", reparent.CurrentParentId);
        Assert.Equal("scope-south", reparent.ProposedParentId);
        Assert.Equal(new[] { "local-fact" }, reparent.CurrentChildSubtreeIds);
        Assert.Equal(new[] { "local-fact" }, reparent.ProposedChildSubtreeIds);
        Assert.Equal(new[] { "scope-north", "purpose" }, reparent.CurrentParentLineage);
        Assert.Equal(new[] { "scope-south", "purpose" }, reparent.ProposedParentLineage);
        Assert.Contains("north-parent", first.Request.Manifest.AllowedCitationIds);
        Assert.Contains("south-parent", first.Request.Manifest.AllowedCitationIds);
        Assert.Contains("local-parent", first.Request.Manifest.AllowedCitationIds);
        Assert.Contains("roster-summary", first.Request.Manifest.AffectedNodeIds);
        Assert.Contains("name-consumer", first.Request.Manifest.AffectedNodeIds);
        Assert.DoesNotContain("unrelated-sibling", first.Request.Manifest.AffectedNodeIds);
        Assert.Contains("all", first.Request.Instructions, StringComparison.Ordinal);
        Assert.Contains("aliases", first.Request.Instructions, StringComparison.Ordinal);
        Assert.Contains("Tags are metadata only", first.Request.Instructions, StringComparison.Ordinal);
        Assert.Contains(first.Request.ContextNodes, item => item.Role == SemanticReviewItemRole.ContextOnlyAncestor);
        Assert.False(application.SemanticReviewAvailability.Enabled);
        Assert.False(application.SemanticReviewAvailability.Configured);
    }

    [Fact]
    public async Task Blocked_write_is_cached_and_one_write_can_explicitly_bypass_the_gate()
    {
        var provider = new CountingProvider(SemanticReviewDecision.Block);
        var (application, snapshot, store) = LoreSession(provider);
        var reviewed = ReviewEverything(application, snapshot);

        var blocked = await application.WriteChangeAsync(reviewed.Reference);
        Assert.Equal(ChangeWriteStatus.SemanticReviewBlocked, blocked.Status);
        Assert.Equal(SemanticReviewDecision.Block, blocked.SemanticReview!.Decision);
        Assert.True(blocked.SemanticReview.IsCurrent);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(0, store.WriteCount);
        Assert.True(application.ShowChange(new(reviewed.Reference.ProjectId, reviewed.Reference.SessionId))
            .SemanticReview!.IsCurrent);

        var cachedBlock = await application.WriteChangeAsync(reviewed.Reference);
        Assert.Equal(ChangeWriteStatus.SemanticReviewBlocked, cachedBlock.Status);
        Assert.Equal(1, provider.CallCount);

        var bypassed = await application.WriteChangeAsync(
            reviewed.Reference,
            new ChangeWriteOptions(BypassAiReview: true));
        Assert.Equal(ChangeWriteStatus.Failed, bypassed.Status);
        Assert.True(bypassed.AiReviewBypassed);
        Assert.Equal(1, store.WriteCount);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task Allow_decision_authorizes_the_exact_write_attempt()
    {
        var provider = new CountingProvider(SemanticReviewDecision.Allow);
        var (application, snapshot, store) = LoreSession(provider);
        var reviewed = ReviewEverything(application, snapshot);

        var result = await application.WriteChangeAsync(reviewed.Reference);

        Assert.Equal(ChangeWriteStatus.Failed, result.Status);
        Assert.Equal(SemanticReviewDecision.Allow, result.SemanticReview!.Decision);
        Assert.False(result.AiReviewBypassed);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(1, store.WriteCount);
    }

    [Fact]
    public async Task Unconfigured_runtime_uses_the_manual_write_path_without_a_provider_call()
    {
        var (application, snapshot, store) = LoreSession(provider: null);
        var reviewed = ReviewEverything(application, snapshot);

        var result = await application.WriteChangeAsync(reviewed.Reference);

        Assert.Equal(ChangeWriteStatus.Failed, result.Status);
        Assert.Null(result.SemanticReview);
        Assert.False(result.AiReviewBypassed);
        Assert.Equal(1, store.WriteCount);
    }

    [Fact]
    public async Task Provider_failure_blocks_without_writing_and_can_be_retried_explicitly()
    {
        var provider = new FailingProvider();
        var (application, snapshot, store) = LoreSession(provider);
        var reviewed = ReviewEverything(application, snapshot);

        var first = await application.WriteChangeAsync(reviewed.Reference);
        var second = await application.WriteChangeAsync(reviewed.Reference);

        Assert.Equal(ChangeWriteStatus.SemanticReviewBlocked, first.Status);
        Assert.Equal(SemanticReviewStatus.Inconclusive, first.SemanticReview!.Status);
        Assert.Equal("provider-failure", first.SemanticReview.FailureCode);
        Assert.Equal(ChangeWriteStatus.SemanticReviewBlocked, second.Status);
        Assert.Equal(2, provider.CallCount);
        Assert.Equal(0, store.WriteCount);
        Assert.Null(application.ShowChange(new(reviewed.Reference.ProjectId, reviewed.Reference.SessionId))
            .SemanticReview);
    }

    [Fact]
    public void Strict_output_requires_known_citations()
    {
        var (_, snapshot, _) = LoreSession(provider: null);
        var plan = SemanticReviewPlanner.Plan(snapshot);
        var output = new SemanticReviewModelOutputDto(
            "block",
            "Review completed.",
            [new SemanticReviewConcernDto(
                "unknown-citation",
                "This concern cites data that was not supplied.",
                [new SemanticReviewCitationDto("invented-id")])]);

        Assert.Throws<JsonException>(() => SemanticReviewOutputValidator.Validate(
            output, plan.Request.Manifest, null, "response", TimeSpan.Zero));
    }

    [Fact]
    public void Strict_output_enforces_decision_concern_consistency()
    {
        var (_, snapshot, _) = LoreSession(provider: null);
        var plan = SemanticReviewPlanner.Plan(snapshot);
        var citation = plan.Request.Manifest.AllowedCitationIds[0];
        var concern = new SemanticReviewConcernDto(
            "known-concern",
            "A cited concern remains.",
            [new SemanticReviewCitationDto(citation)]);

        Assert.Throws<JsonException>(() => SemanticReviewOutputValidator.Validate(
            new SemanticReviewModelOutputDto("allow", "Allowed.", [concern]),
            plan.Request.Manifest, null, "response", TimeSpan.Zero));
        Assert.Throws<JsonException>(() => SemanticReviewOutputValidator.Validate(
            new SemanticReviewModelOutputDto("block", "Blocked.", []),
            plan.Request.Manifest, null, "response", TimeSpan.Zero));
        var allowed = SemanticReviewOutputValidator.Validate(
            new SemanticReviewModelOutputDto("allow", "No blocking concerns.", []),
            plan.Request.Manifest, null, "response", TimeSpan.Zero);
        Assert.Equal(SemanticReviewDecision.Allow, allowed.Decision);
    }

    [Fact]
    public async Task Responses_client_polls_once_parses_usage_and_never_logs_credentials()
    {
        var (_, snapshot, _) = LoreSession(provider: null);
        var plan = SemanticReviewPlanner.Plan(snapshot);
        var modelOutput = Protocol.Serialize(new SemanticReviewModelOutputDto(
            "block",
            "The roster wording conflicts with its consumer.",
            [new SemanticReviewConcernDto(
                "closed-world-count",
                "The changed complete roster and its summary should be reconciled.",
                [new SemanticReviewCitationDto("roster"), new SemanticReviewCitationDto("roster-summary")])])) ;
        var handler = new SequenceHandler(
            JsonResponse("""{"id":"resp_test","status":"in_progress"}"""),
            JsonResponse(Completed(modelOutput)));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/") };
        string? logged = null;
        string? responseLogged = null;
        var provider = new OpenAiResponsesSemanticReviewProvider(
            http, "test-secret", timeout: TimeSpan.FromSeconds(2), pollInterval: TimeSpan.Zero,
            serializedRequestLogger: value => logged = value,
            serializedResponseLogger: value => responseLogged = value);

        var result = await provider.ReviewAsync(plan);

        Assert.Equal(SemanticReviewStatus.Complete, result.Status);
        Assert.Equal(SemanticReviewDecision.Block, result.Decision);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal(HttpMethod.Get, handler.Requests[1].Method);
        Assert.Equal(11, result.Usage!.InputTokens);
        Assert.Equal(7, result.Usage.OutputTokens);
        Assert.DoesNotContain("test-secret", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("test-secret", responseLogged, StringComparison.Ordinal);
        Assert.Contains("\"background\":true", logged, StringComparison.Ordinal);
        Assert.Contains("\"tool_choice\":\"none\"", logged, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"completed\"", responseLogged, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("refusal", SemanticReviewStatus.Refused, "refusal")]
    [InlineData("malformed", SemanticReviewStatus.Inconclusive, "malformed-response")]
    public async Task Responses_client_returns_manual_fallback_for_refusal_or_malformed_output(
        string scenario,
        SemanticReviewStatus expectedStatus,
        string expectedFailure)
    {
        var (_, snapshot, _) = LoreSession(provider: null);
        var plan = SemanticReviewPlanner.Plan(snapshot);
        var response = scenario == "refusal"
            ? """{"id":"resp_test","status":"completed","output":[{"content":[{"type":"refusal","refusal":"Declined."}]}],"usage":{"input_tokens":1,"output_tokens":1,"total_tokens":2}}"""
            : Completed("not-json");
        using var http = new HttpClient(new SequenceHandler(JsonResponse(response)))
        {
            BaseAddress = new Uri("https://api.openai.com/"),
        };
        var provider = new OpenAiResponsesSemanticReviewProvider(http, "test-secret");

        var result = await provider.ReviewAsync(plan);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(expectedFailure, result.FailureCode);
    }

    [Fact]
    public async Task Responses_client_timeout_is_inconclusive_without_a_second_create()
    {
        var (_, snapshot, _) = LoreSession(provider: null);
        var plan = SemanticReviewPlanner.Plan(snapshot);
        var handler = new DelayedHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/") };
        var provider = new OpenAiResponsesSemanticReviewProvider(
            http, "test-secret", timeout: TimeSpan.FromMilliseconds(10));

        var result = await provider.ReviewAsync(plan);

        Assert.Equal(SemanticReviewStatus.Inconclusive, result.Status);
        Assert.Equal("timeout", result.FailureCode);
        Assert.Equal(1, handler.CallCount);
    }

    private static (ProjectApplication Application, ChangeSessionSnapshot Snapshot, MemoryStore Store) LoreSession(
        ISemanticReviewProvider? provider)
    {
        var graph = LoreGraph();
        var store = new MemoryStore(graph);
        var application = new ProjectApplication(
            store,
            utcNow: () => FixedUtc,
            sessionIdFactory: () => "lore-session",
            semanticReviewProvider: provider,
            semanticReviewOptions: new SemanticReviewRuntimeOptions(
                Enabled: provider is not null,
                Configured: provider is not null,
                Provider: provider?.Provider ?? "openai",
                Model: provider?.Model ?? "gpt-5.6-terra"));
        var begun = application.BeginChange("lore.vw.db", graph.ProjectId, "tester", "Compact T12 lore cases");
        var memberSix = new GraphNode(new EntityId("member-six"), "Fara is the sixth council member", "character");
        var operations = new GraphOperationBatch(
        [
            GraphOperation.ReplaceNode(new GraphNode(
                new EntityId("roster"),
                "All six council members are Ada, Bea, Cora, Dara, Eira, and Fara.",
                "closed-list",
                ["council:roster"])),
            GraphOperation.AddNode(memberSix),
            GraphOperation.AddEdge(Scope("member-six-parent", memberSix.Id, new EntityId("scope-north"))),
            GraphOperation.AddEdge(new GraphEdge(
                new EntityId("member-six-roster"), memberSix.Id, new EntityId("roster"),
                "member-of", ReviewDirection.SourceToTarget)),
            GraphOperation.ReplaceNode(new GraphNode(
                new EntityId("canonical-name"), "The hero's canonical name is Myra.", "canonical-name")),
            GraphOperation.ReplaceNode(new GraphNode(
                new EntityId("local-fact"), "Myra now prefers coffee.", "local-fact")),
            GraphOperation.ReplaceEdge(Scope(
                "local-parent", new EntityId("local-fact"), new EntityId("scope-south"))),
        ]);
        return (application, application.ApplyChange(begun.Reference, operations), store);
    }

    private static ChangeSessionSnapshot ReviewEverything(
        ProjectApplication application,
        ChangeSessionSnapshot snapshot) => application.ReviewChange(
            snapshot.Reference,
            new ChangeReviewUpdate(
                snapshot.Affected.AffectedNodes.Select(node => new ReviewDisposition(
                    node.NodeId,
                    node.IsDirectChange ? ReviewDispositionKind.Updated : ReviewDispositionKind.ReviewedNoChange,
                    null)),
                snapshot.Affected.ScopeContext.Select(entry => entry.NodeId)));

    private static ProjectGraph LoreGraph()
    {
        var purpose = Node("purpose", "Maintain a coherent small council world.", "purpose");
        var north = Node("scope-north", "Northern council scope.", "scope");
        var south = Node("scope-south", "Southern council scope.", "scope");
        var roster = new GraphNode(new EntityId("roster"),
            "All five council members are Ada, Bea, Cora, Dara, and Eira.", "closed-list", ["council:roster"]);
        var summary = Node("roster-summary", "The council has only five members.", "summary");
        var name = Node("canonical-name", "The hero's canonical name is Mira.", "canonical-name");
        var consumer = Node("name-consumer", "Mira leads the northern watch.", "narrative");
        var local = Node("local-fact", "Mira prefers tea.", "local-fact");
        var unrelated = Node("unrelated-sibling", "Southern harbor lamps are blue.", "local-fact");
        var members = new[]
        {
            Node("member-one", "Ada is a council member.", "character"),
            Node("member-two", "Bea is a council member.", "character"),
            Node("member-three", "Cora is a council member.", "character"),
            Node("member-four", "Dara is a council member.", "character"),
            Node("member-five", "Eira is a council member.", "character"),
        };
        var nodes = new[] { purpose, north, south, roster, summary, name, consumer, local, unrelated }
            .Concat(members).ToArray();
        var edges = new List<GraphEdge>
        {
            Scope("north-parent", north.Id, purpose.Id),
            Scope("south-parent", south.Id, purpose.Id),
            Scope("roster-parent", roster.Id, north.Id),
            Scope("summary-parent", summary.Id, north.Id),
            Scope("name-parent", name.Id, north.Id),
            Scope("consumer-parent", consumer.Id, north.Id),
            Scope("local-parent", local.Id, north.Id),
            Scope("unrelated-parent", unrelated.Id, south.Id),
            new(new EntityId("roster-summary-edge"), roster.Id, summary.Id,
                "summarized-by", ReviewDirection.SourceToTarget),
            new(new EntityId("name-consumer-edge"), name.Id, consumer.Id,
                "used-by", ReviewDirection.SourceToTarget),
        };
        for (var index = 0; index < members.Length; index++)
        {
            edges.Add(Scope($"member-{index + 1}-parent", members[index].Id, north.Id));
            edges.Add(new GraphEdge(new EntityId($"member-{index + 1}-roster"), members[index].Id, roster.Id,
                "member-of", ReviewDirection.SourceToTarget));
        }
        return new ProjectGraph(new ProjectId("lore"), "Small Lore", purpose.Id, nodes, edges);
    }

    private static GraphNode Node(string id, string text, string kind) => new(new EntityId(id), text, kind);

    private static GraphEdge Scope(string id, EntityId child, EntityId parent) => new(
        new EntityId(id), child, parent, "scope-parent", ReviewDirection.None);

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static string Completed(string modelOutput) => JsonSerializer.Serialize(new
    {
        id = "resp_test",
        status = "completed",
        output = new[]
        {
            new
            {
                content = new[] { new { type = "output_text", text = modelOutput } },
            },
        },
        usage = new { input_tokens = 11, output_tokens = 7, total_tokens = 18 },
    });

    private sealed class CountingProvider(SemanticReviewDecision decision) : ISemanticReviewProvider
    {
        public int CallCount { get; private set; }
        public string Provider => "fake";
        public string Model => "fake-reviewer";

        public Task<SemanticReviewProviderResult> ReviewAsync(
            SemanticReviewPlannedRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            var citation = new EntityId(request.Request.Manifest.AffectedNodeIds[0]);
            return Task.FromResult(new SemanticReviewProviderResult(
                SemanticReviewStatus.Complete,
                decision,
                "Offline fake review completed.",
                decision == SemanticReviewDecision.Block
                    ? [new SemanticReviewConcern("test", "Offline concern.", [citation])]
                    : [],
                new SemanticReviewUsage(10, 5, 15),
                "fake-response",
                TimeSpan.FromMilliseconds(1)));
        }
    }

    private sealed class FailingProvider : ISemanticReviewProvider
    {
        public int CallCount { get; private set; }
        public string Provider => "fake";
        public string Model => "fake-reviewer";

        public Task<SemanticReviewProviderResult> ReviewAsync(
            SemanticReviewPlannedRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new SemanticReviewProviderResult(
                SemanticReviewStatus.Inconclusive,
                null,
                "The provider failed before producing a decision.",
                [],
                null,
                null,
                TimeSpan.FromMilliseconds(1),
                "provider-failure"));
        }
    }

    private sealed class SequenceHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new HttpRequestMessage(request.Method, request.RequestUri));
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class DelayedHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            return JsonResponse("{}.");
        }
    }

    private sealed class MemoryStore(ProjectGraph graph) : IProjectStore
    {
        private readonly StoredProject _project = new(
            "lore.vw.db", graph, GraphFingerprints.State(graph), FixedUtc, FixedUtc);

        public int WriteCount { get; private set; }
        public StoredProject Load(string path) => _project;
        public StoredProject Initialize(string path, ProjectGraph value) => throw new NotSupportedException();
        public ProjectStatus GetStatus(string path) => throw new NotSupportedException();
        public ProjectVerification Verify(string path) => throw new NotSupportedException();
        public StoredProject Backup(string sourcePath, string destinationPath) => throw new NotSupportedException();
        public ProjectSqlExport ExportSql(string path) => throw new NotSupportedException();
        public ProjectWriteResult Write(ProjectWriteRequest request)
        {
            WriteCount++;
            return new ProjectWriteResult(ProjectWriteOutcome.Failed, null, ProjectStorageErrorCode.StorageFailure,
                "Writes are not expected in semantic-review tests.");
        }
    }
}
