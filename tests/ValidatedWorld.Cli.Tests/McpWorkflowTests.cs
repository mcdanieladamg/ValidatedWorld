using System.Diagnostics;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ValidatedWorld.Application;
using ValidatedWorld.Core;
using ValidatedWorld.Mcp;
using ValidatedWorld.Persistence.Sqlite;
using ValidatedWorld.Serialization;

namespace ValidatedWorld.Cli.Tests;

public sealed class McpWorkflowTests
{
    [Fact]
    public async Task Large_atomic_import_and_read_pages_are_not_limited_by_adapter_ceilings()
    {
        using var temporary = new TemporaryDirectory();
        var project = Path.Combine(temporary.Path, "large-import.vw.db");
        var application = new ProjectApplication(new SqliteProjectStore());
        application.Initialize(project, new ProjectId("large"), "Large", new EntityId("purpose"), "Maintain the corpus.");
        var operations = Enumerable.Range(0, 5_001).SelectMany(index => new[]
        {
            GraphOperation.AddNode(new GraphNode(new EntityId($"claim-{index}"),
                $"Requirement {index}: " + new string('x', 110), "claim")),
            GraphOperation.AddEdge(new GraphEdge(new EntityId($"parent-{index}"), new EntityId($"claim-{index}"),
                new EntityId("purpose"), "scope-parent", ReviewDirection.None)),
        });
        await using var host = await InitializedHost(project);
        var begun = await host.Call("begin_change", new { intent = "Import one coherent corpus atomically." });
        var staged = await host.Call("patch_change", new
        {
            expectedRevision = begun["revision"]!.GetValue<int>(),
            operations = GraphProtocol.ToDto(new GraphOperationBatch(operations)),
        });
        var revision = staged["revision"]!.GetValue<int>();
        Assert.Equal(10_002, staged["operationCount"]!.GetValue<int>());
        var preview = await host.Call("proposal_preview", new { expectedRevision = revision, limit = 257 });
        // Added scope edges also select their existing purpose endpoint. The complete
        // evidence is paged instead of relying on one host-sized response.
        Assert.Equal(5_002, preview["affectedNodeCount"]!.GetValue<int>());
        Assert.Equal(0, preview["omissionCount"]!.GetValue<int>());
        var pageCount = 1;
        var cursor = preview["reviewPage"]!["nextCursor"]?.GetValue<string>();
        while (cursor is not null)
        {
            preview = await host.Call("proposal_preview", new { expectedRevision = revision, limit = 257, cursor });
            cursor = preview["reviewPage"]!["nextCursor"]?.GetValue<string>();
            pageCount++;
        }
        Assert.True(pageCount > 1);
        Assert.True(preview["reviewPage"]!["allEvidencePresented"]!.GetValue<bool>());
        Assert.Equal(1, application.Status(project).NodeCount);
        var result = await host.Call("write_change", new { expectedRevision = revision });
        Assert.Equal("Written", result["status"]!.GetValue<string>());
        var page = await host.Call("list_nodes", new { limit = int.MaxValue });
        Assert.Equal(5_002, page["items"]!.AsArray().Count);
        Assert.Null(page["nextCursor"]);
        Assert.Null(page["omission"]);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(page.ToJsonString()) > 512 * 1024);
        Assert.Equal(5_002, application.Status(project).NodeCount);
    }

    [Fact]
    public async Task Revisions_from_discarded_or_written_sessions_cannot_authorize_a_later_change()
    {
        using var temporary = new TemporaryDirectory();
        var project = Path.Combine(temporary.Path, "revision-lifetime.vw.db");
        var application = new ProjectApplication(new SqliteProjectStore());
        application.Initialize(project, new ProjectId("revisions"), "Revisions", new EntityId("purpose"), "Original purpose.");
        await using var host = await InitializedHost(project);

        var first = await ReplacePurpose(host, "Abandoned purpose.");
        var oldRevision = first["revision"]!.GetValue<int>();
        await host.Call("discard_change", new { expectedRevision = oldRevision });
        var second = await ReplacePurpose(host, "Intended purpose.");
        var revision = second["revision"]!.GetValue<int>();
        var stale = await host.CallResult("write_change", new { expectedRevision = oldRevision });
        Assert.True(stale["isError"]?.GetValue<bool>() == true);
        Assert.Equal("Original purpose.", application.Queries(project).GetNode(new EntityId("purpose")).Text);

        await host.Call("proposal_preview", new { expectedRevision = revision });
        await host.Call("write_change", new { expectedRevision = revision });
        var third = await ReplacePurpose(host, "Next purpose.");
        var staleDiscard = await host.CallResult("discard_change", new { expectedRevision = revision });
        Assert.True(staleDiscard["isError"]?.GetValue<bool>() == true);
        await host.Call("discard_change", new { expectedRevision = third["revision"]!.GetValue<int>() });
    }

    [Fact]
    public async Task Overlapping_identical_writes_share_one_review_and_a_patch_preserves_the_replacement_session()
    {
        using var temporary = new TemporaryDirectory();
        var project = Path.Combine(temporary.Path, "overlap.vw.db");
        var other = Path.Combine(temporary.Path, "other.vw.db");
        var forbiddenInitialization = Path.Combine(temporary.Path, "must-not-exist.vw.db");
        var store = new SqliteProjectStore();
        var reviewer = new ControlledReviewer();
        var application = ReviewedApplication(store, reviewer);
        application.Initialize(project, new ProjectId("overlap"), "Overlap", new EntityId("purpose"), "Original.");
        application.Initialize(other, new ProjectId("other"), "Other", new EntityId("purpose"), "Other.");
        var service = ReviewedService(application, project);

        var begun = service.BeginChange("First proposal");
        var staged = (McpChangeSummary)service.PatchChange(begun.Revision, new GraphOperationBatch([
            GraphOperation.ReplaceNode(new GraphNode(new EntityId("purpose"), "First proposal.", "purpose"))]));
        PreviewAll(service, staged.Revision, 1);

        var firstWrite = service.WriteChangeAsync(staged.Revision);
        await reviewer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var duplicateWrite = service.WriteChangeAsync(staged.Revision);
        Assert.Same(firstWrite, duplicateWrite);
        Assert.Throws<ChangeSessionException>(() => service.Select(other));
        Assert.Throws<ChangeSessionException>(() => service.Initialize(
            forbiddenInitialization, "forbidden", "Forbidden", "purpose", "Forbidden."));
        Assert.False(File.Exists(forbiddenInitialization));

        var replacement = (McpChangeSummary)service.PatchChange(staged.Revision, new GraphOperationBatch([
            GraphOperation.ReplaceNode(new GraphNode(new EntityId("purpose"), "Replacement proposal.", "purpose"))]));
        reviewer.Release.TrySetResult();
        Assert.Equal("Stale", (await firstWrite).Status);
        Assert.Equal(1, reviewer.CallCount);

        PreviewAll(service, replacement.Revision, 1);
        Assert.Equal("Written", (await service.WriteChangeAsync(replacement.Revision)).Status);
        Assert.Equal("Replacement proposal.", application.Queries(project).GetNode(new EntityId("purpose")).Text);
    }

    [Fact]
    public async Task Discard_during_review_leaves_no_write_and_does_not_erase_a_later_session()
    {
        using var temporary = new TemporaryDirectory();
        var project = Path.Combine(temporary.Path, "discard-overlap.vw.db");
        var reviewer = new ControlledReviewer();
        var application = ReviewedApplication(new SqliteProjectStore(), reviewer);
        application.Initialize(project, new ProjectId("discard-overlap"), "Discard", new EntityId("purpose"), "Original.");
        var service = ReviewedService(application, project);
        var begun = service.BeginChange("Discard while reviewing");
        var staged = (McpChangeSummary)service.PatchChange(begun.Revision, new GraphOperationBatch([
            GraphOperation.ReplaceNode(new GraphNode(new EntityId("purpose"), "Discarded.", "purpose"))]));
        PreviewAll(service, staged.Revision, 2);
        var write = service.WriteChangeAsync(staged.Revision);
        await reviewer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        service.DiscardChange(staged.Revision);
        var later = service.BeginChange("Later session must survive");
        reviewer.Release.TrySetResult();
        Assert.Equal("Stale", (await write).Status);
        Assert.Equal("Original.", application.Queries(project).GetNode(new EntityId("purpose")).Text);
        service.DiscardChange(later.Revision);
    }

    [Fact]
    public async Task Project_status_refreshes_after_an_external_write()
    {
        using var temporary = new TemporaryDirectory();
        var project = Path.Combine(temporary.Path, "external-write.vw.db");
        var store = new SqliteProjectStore();
        var application = new ProjectApplication(store);
        var original = application.Initialize(project, new ProjectId("external"), "External", new EntityId("purpose"), "Original purpose.");
        await using var host = await InitializedHost(project);
        var before = await host.Call("project_status", new { });

        var operations = new GraphOperationBatch([GraphOperation.ReplaceNode(
            new GraphNode(new EntityId("purpose"), "Externally updated purpose.", "purpose"))]);
        var proposed = new ValidatedWorld.Validation.GraphProjector().Project(original.Graph, operations).Graph;
        var write = store.Write(new ProjectWriteRequest(project, original.Graph.ProjectId,
            original.StateFingerprint, operations, GraphFingerprints.Proposed(proposed)));
        Assert.Equal(ProjectWriteOutcome.Written, write.Outcome);

        var after = await host.Call("project_status", new { });
        Assert.NotEqual(before["stateFingerprint"]!.GetValue<string>(), after["stateFingerprint"]!.GetValue<string>());
        Assert.Equal(application.Status(project).StateFingerprint, after["stateFingerprint"]!.GetValue<string>());
    }

    [Fact]
    public async Task Mcp_can_repair_a_rule_invalid_baseline_without_disabling_its_rule()
    {
        using var temporary = new TemporaryDirectory();
        var project = Path.Combine(temporary.Path, "repair.vw.db");
        var purpose = new GraphNode(new EntityId("purpose"), "Maintain confirmed claims.", "purpose");
        var claim = new GraphNode(new EntityId("claim"), "A confirmed claim.", "claim");
        var rule = new GraphNode(new EntityId("rule"), "Every claim must be confirmed.", RuleProtocol.RuleKind,
            [RuleProtocol.ActiveTag], [
                new("rule:version", GraphValue.FromInteger(1)),
                new("rule:expression", GraphValue.FromText("{\"all\":{\"set\":{\"nodes\":{\"kind\":\"claim\"}},\"condition\":{\"hasTag\":\"confirmed\"}}}"))]);
        var store = new SqliteProjectStore();
        store.Initialize(project, new ProjectGraph(new ProjectId("repair"), "Repair", purpose.Id, [purpose, claim, rule], [
            new GraphEdge(new EntityId("claim-scope"), claim.Id, purpose.Id, "scope-parent", ReviewDirection.None),
            new GraphEdge(new EntityId("rule-scope"), rule.Id, purpose.Id, "scope-parent", ReviewDirection.None)]));
        Assert.False(store.Verify(project).IsValid);
        await using var host = await InitializedHost(project);
        var begun = await host.Call("begin_change", new { intent = "Confirm the claim while preserving the rule." });
        var patched = await host.Call("put_node", new
        {
            expectedRevision = begun["revision"]!.GetValue<int>(), mode = "replace", id = "claim",
            text = claim.Text, kind = "claim", tags = new[] { "confirmed" }, attributes = Array.Empty<object>(),
        });
        var revision = patched["revision"]!.GetValue<int>();
        var preview = await host.Call("proposal_preview", new { expectedRevision = revision });
        Assert.Equal("Invalid", preview["currentValidation"]!["status"]!.GetValue<string>());
        Assert.Equal("Valid", preview["proposedValidation"]!["status"]!.GetValue<string>());
        var result = await host.Call("write_change", new { expectedRevision = revision });
        Assert.Equal("Written", result["status"]!.GetValue<string>());
        Assert.True(store.Verify(project).IsValid);
    }

    [Fact]
    public async Task Workflow_mistakes_return_actionable_diagnostics()
    {
        await using var host = await InitializedHost();
        var unselected = await host.CallResult("project_status", new { });
        Assert.Contains("select_project", unselected["content"]![0]!["text"]!.GetValue<string>());
        var noSession = await host.CallResult("write_change", new { expectedRevision = 1 });
        Assert.Contains("begin_change", noSession["content"]![0]!["text"]!.GetValue<string>());
    }

    private static async Task<McpProcess> InitializedHost(
        string? project = null,
        IReadOnlyList<string>? artifactRoots = null)
    {
        var host = await McpProcess.Start(project, artifactRoots);
        try
        {
            await host.Request("initialize", new
            {
                protocolVersion = "2024-11-05", capabilities = new { },
                clientInfo = new { name = "Release review regression", version = "1" },
            });
            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    private static async Task<JsonNode> ReplacePurpose(McpProcess host, string text)
    {
        var begun = await host.Call("begin_change", new { intent = text });
        return await host.Call("put_node", new
        {
            expectedRevision = begun["revision"]!.GetValue<int>(), mode = "replace", id = "purpose",
            text, kind = "purpose", tags = Array.Empty<string>(), attributes = Array.Empty<object>(),
        });
    }

    private static async Task PreviewAll(McpProcess host, int revision, int limit = 100)
    {
        string? cursor = null;
        do
        {
            var preview = await host.Call("proposal_preview", new { expectedRevision = revision, limit, cursor });
            cursor = preview["reviewPage"]!["nextCursor"]?.GetValue<string>();
        } while (cursor is not null);
    }

    private static void PreviewAll(McpProjectService service, int revision, int limit)
    {
        string? cursor = null;
        do
        {
            var preview = service.PreviewChange(revision, limit, cursor);
            cursor = preview.ReviewPage.NextCursor;
        } while (cursor is not null);
    }

    private static ProjectApplication ReviewedApplication(IProjectStore store, ISemanticReviewProvider reviewer) =>
        new(store, semanticReviewProvider: reviewer,
            semanticReviewOptions: new SemanticReviewRuntimeOptions(Enabled: true, Configured: true));

    private static McpProjectService ReviewedService(ProjectApplication application, string project) =>
        new(application, new McpHostOptions(project, [], false, false),
            new McpSemanticReviewConfiguration(true, "openai", "gpt-5.6-terra", 1200, false, "fake"));

    private sealed class ControlledReviewer : ISemanticReviewProvider
    {
        private int callCount;

        public string Provider => "openai";
        public string Model => "controlled";
        public int CallCount => Volatile.Read(ref callCount);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<SemanticReviewProviderResult> ReviewAsync(
            SemanticReviewPlannedRequest request,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref callCount) == 1)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return new SemanticReviewProviderResult(
                SemanticReviewStatus.Complete, SemanticReviewDecision.Allow,
                "Controlled review allowed the proposal.", [], null, null, TimeSpan.Zero);
        }
    }

    [Fact]
    public async Task Stdio_initializes_discovers_bounded_read_tools_and_keeps_reads_read_only()
    {
        using var temporary = new TemporaryDirectory();
        var project = Path.Combine(temporary.Path, "existing project.vw.db");
        new ProjectApplication(new SqliteProjectStore()).CreateSample("technical-project", project);
        var before = new SqliteProjectStore().Load(project);

        await using var host = await McpProcess.Start(project);
        var initialize = await host.Request("initialize", new
        {
            protocolVersion = "2024-11-05",
            capabilities = new { },
            clientInfo = new { name = "ValidatedWorld tests", version = "1" },
        });
        Assert.Equal("2024-11-05", initialize["result"]!["protocolVersion"]!.GetValue<string>());

        var tools = await host.Request("tools/list", new { });
        var toolItems = tools["result"]!["tools"]!.AsArray();
        Assert.Contains(toolItems, tool => tool!["name"]!.GetValue<string>() == "host_status");
        Assert.Contains(toolItems, tool => tool!["name"]!.GetValue<string>() == "select_project");
        Assert.Contains(toolItems, tool => tool!["name"]!.GetValue<string>() == "initialize_project");
        Assert.Contains(toolItems, tool => tool!["name"]!.GetValue<string>() == "initialize_from_template");
        Assert.Contains(toolItems, tool => tool!["name"]!.GetValue<string>() == "validate_project");
        Assert.Contains(toolItems, tool => tool!["name"]!.GetValue<string>() == "check_artifacts");
        Assert.Contains(toolItems, tool => tool!["name"]!.GetValue<string>() == "plan_bulk_import");
        Assert.Contains(toolItems, tool => tool!["name"]!.GetValue<string>() == "merge_projects");
        Assert.Contains(toolItems, tool => tool!["name"]!.GetValue<string>() == "read_context");
        Assert.Contains(toolItems, tool => tool!["name"]!.GetValue<string>() == "begin_change");
        Assert.DoesNotContain(toolItems, tool => tool!["name"]!.GetValue<string>().Contains("bypass", StringComparison.OrdinalIgnoreCase));
        var listNodesTool = toolItems.Single(tool => tool!["name"]!.GetValue<string>() == "list_nodes");
        Assert.Equal("integer", listNodesTool!["inputSchema"]!["properties"]!["limit"]!["type"]!.GetValue<string>());
        Assert.True(listNodesTool["annotations"]!["readOnlyHint"]!.GetValue<bool>());
        Assert.False(listNodesTool["annotations"]!["destructiveHint"]!.GetValue<bool>());
        var writeTool = toolItems.Single(tool => tool!["name"]!.GetValue<string>() == "write_change");
        Assert.Null(writeTool!["annotations"]!["readOnlyHint"]);
        Assert.True(writeTool["annotations"]!["destructiveHint"]!.GetValue<bool>());
        Assert.False(writeTool["annotations"]!["openWorldHint"]!.GetValue<bool>());
        var mergeTool = toolItems.Single(tool => tool!["name"]!.GetValue<string>() == "merge_projects");
        Assert.True(mergeTool!["annotations"]!["readOnlyHint"]!.GetValue<bool>());
        Assert.False(mergeTool["annotations"]!["destructiveHint"]!.GetValue<bool>());

        var hostStatus = await host.Call("host_status", new { });
        Assert.Equal(McpAssembly.ProductVersion, hostStatus["productVersion"]!.GetValue<string>());
        Assert.Equal("local-only", hostStatus["hostSupport"]!.GetValue<string>());
        Assert.Equal("stdio", hostStatus["transport"]!.GetValue<string>());
        Assert.Equal("English", hostStatus["supportedProductLanguage"]!.GetValue<string>());
        Assert.Contains("Unicode text", hostStatus["graphTextSupport"]!.GetValue<string>());
        Assert.Contains("non-English workflows are unsupported", hostStatus["graphTextSupport"]!.GetValue<string>());
        Assert.False(hostStatus["semanticReview"]!["effective"]!.GetValue<bool>());
        Assert.Null(hostStatus["semanticReview"]!["apiKey"]);

        var status = await host.Call("project_status", new { });
        Assert.Equal("technical-project", status["projectId"]!.GetValue<string>());
        Assert.Equal(Path.GetFullPath(project), status["path"]!.GetValue<string>());

        var merge = await host.Call("merge_projects", new
        {
            basePath = project,
            oursPath = project,
            theirsPath = project,
        });
        Assert.Equal("Clean", merge["status"]!.GetValue<string>());
        Assert.True(merge["isReadyToApply"]!.GetValue<bool>());
        Assert.Equal(0, merge["operationCount"]!.GetValue<int>());
        Assert.NotNull(merge["mergedFingerprint"]);
        Assert.Null(merge["mergedGraph"]);

        var page = await host.Call("list_nodes", new { limit = 1 });
        Assert.Single(page["items"]!.AsArray());
        Assert.Equal(13, page["totalCount"]!.GetValue<int>());
        Assert.NotNull(page["nextCursor"]);

        var search = await host.Call("search", new { text = "battery", limit = 1 });
        Assert.Equal(4, search["totalCount"]!.GetValue<int>());
        Assert.Equal("battery-assumption", search["items"]![0]!["entityId"]!.GetValue<string>());

        var artifacts = await host.Call("check_artifacts", new { maxAnchors = 1 });
        Assert.Equal(2, artifacts["totalAnchorCount"]!.GetValue<int>());
        Assert.False(artifacts["isComplete"]!.GetValue<bool>());
        Assert.Equal("InvalidAnchor", artifacts["items"]![0]!["status"]!.GetValue<string>());

        var after = new SqliteProjectStore().Load(project);
        Assert.Equal(before.StateFingerprint, after.StateFingerprint);
        Assert.Equal(before.UpdatedUtc, after.UpdatedUtc);
    }

    [Fact]
    public async Task Mcp_artifact_roots_are_host_owned_and_deny_reads_by_default()
    {
        using var temporary = new TemporaryDirectory();
        var project = Path.Combine(temporary.Path, "artifact-authority.vw.db");
        var artifact = Path.Combine(temporary.Path, "artifact.txt");
        File.WriteAllText(artifact, "authorized bytes");
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(artifact))).ToLowerInvariant();
        var purpose = new GraphNode(new EntityId("purpose"), "Purpose", "purpose");
        var anchor = new GraphNode(new EntityId("anchor"), "Artifact", "external-anchor", ["artifact"], [
            new(ArtifactAnchorMetadata.Path, GraphValue.FromText("artifact.txt")),
            new(ArtifactAnchorMetadata.Sha256, GraphValue.FromText(hash))]);
        new SqliteProjectStore().Initialize(project,
            new ProjectGraph(new ProjectId("artifact-authority"), "Artifacts", purpose.Id, [purpose, anchor], [
                new GraphEdge(new EntityId("anchor-scope"), anchor.Id, purpose.Id, "scope-parent", ReviewDirection.None)]));

        await using (var deniedHost = await InitializedHost(project))
        {
            var denied = await deniedHost.Call("check_artifacts", new { });
            Assert.Equal("Unauthorized", denied["items"]![0]!["status"]!.GetValue<string>());
            Assert.Null(denied["items"]![0]!["contentSampleBase64"]);
        }

        await using (var allowedHost = await InitializedHost(project, [temporary.Path]))
        {
            var status = await allowedHost.Call("host_status", new { });
            Assert.Contains(temporary.Path, status["artifactAllowedRoots"]!.AsArray()
                .Select(item => item!.GetValue<string>()));
            var allowed = await allowedHost.Call("check_artifacts", new { });
            Assert.Equal("Matched", allowed["items"]![0]!["status"]!.GetValue<string>());
            Assert.NotNull(allowed["items"]![0]!["contentSampleBase64"]);
        }
    }

    [Fact]
    public async Task Mcp_discovers_and_instantiates_governed_templates()
    {
        using var temporary = new TemporaryDirectory();
        var project = Path.Combine(temporary.Path, "templated.vw.db");
        await using var host = await McpProcess.Start();
        _ = await host.Request("initialize", new
        {
            protocolVersion = "2024-11-05",
            capabilities = new { },
            clientInfo = new { name = "ValidatedWorld tests", version = "1" },
        });

        var templates = await host.Call("list_templates", new { });
        Assert.Contains(templates.AsArray(), item => item!["id"]!.GetValue<string>() == "code-development");
        var description = await host.Call("describe_template", new { nameOrPath = "code-development" });
        Assert.Equal("purpose", description["purposeNodeId"]!.GetValue<string>());
        var initialized = await host.Call("initialize_from_template", new
        {
            nameOrPath = "code-development", path = project, projectId = "templated",
            title = "Templated", purposeText = "Maintain this codebase coherently.",
        });
        var validation = await host.Call("validate_project", new { });
        Assert.True(validation["isValid"]!.GetValue<bool>());

        var begun = await host.Call("begin_change", new { intent = "Introduce an invalid phase outside any semantic dependency." });
        var added = await host.Call("put_node", new
        {
            expectedRevision = begun["revision"]!.GetValue<int>(), mode = "add", id = "phase-bad",
            text = "Bad phase", kind = "development-phase",
            tags = new[] { "roadmap:phase", "phase:bad", "status:current" }, attributes = Array.Empty<object>(),
        });
        var edge = await host.Call("put_edge", new
        {
            expectedRevision = added["revision"]!.GetValue<int>(), mode = "add", id = "phase-bad-scope",
            source = "phase-bad", target = "scope-roadmap", relationship = "scope-parent",
            reviewDirection = "None", rationale = (string?)null, tags = Array.Empty<string>(), attributes = Array.Empty<object>(),
        });
        var preview = await host.Call("proposal_preview", new { expectedRevision = edge["revision"]!.GetValue<int>() });
        Assert.Equal("Invalid", preview["proposedValidation"]!["status"]!.GetValue<string>());
        Assert.Contains(preview["reviewPage"]!["items"]!.AsArray(), item =>
            item!["kind"]!.GetValue<string>() == "proposedValidationDiagnostic" &&
            item["diagnostic"]!["code"]!.GetValue<string>() == "rule-violation");
    }

    [Fact]
    public async Task Edit_writes_the_exact_current_revision_and_reopens_graph()
    {
        using var temporary = new TemporaryDirectory();
        var project = Path.Combine(temporary.Path, "reviewed-edit.vw.db");
        new ProjectApplication(new SqliteProjectStore()).CreateSample("technical-project", project);

        await using var host = await McpProcess.Start(project);
        _ = await host.Request("initialize", new
        {
            protocolVersion = "2024-11-05",
            capabilities = new { },
            clientInfo = new { name = "ValidatedWorld tests", version = "1" },
        });

        var begun = await host.Call("begin_change", new { intent = "Add a maintenance note" });
        Assert.Equal(1, begun["revision"]!.GetValue<int>());
        var added = await host.Call("put_node", new
        {
            expectedRevision = 1,
            mode = "add",
            id = "maintenance-note",
            text = "Inspect the power enclosure before maintenance.",
            kind = "note",
            tags = Array.Empty<string>(),
            attributes = Array.Empty<object>(),
        });
        var nodeRevision = added["revision"]!.GetValue<int>();
        Assert.Equal(2, nodeRevision);
        var edgeAdded = await host.Call("put_edge", new
        {
            expectedRevision = nodeRevision,
            mode = "add",
            id = "maintenance-note-parent",
            source = "maintenance-note",
            target = "scope-power",
            relationship = "scope-parent",
            reviewDirection = "None",
            rationale = (string?)null,
            tags = Array.Empty<string>(),
            attributes = Array.Empty<object>(),
        });
        var revision = edgeAdded["revision"]!.GetValue<int>();
        Assert.Equal(3, revision);

        var stale = await host.CallResult("write_change", new { expectedRevision = nodeRevision });
        Assert.True(stale["isError"]!.GetValue<bool>());
        Assert.Contains(
            "proposal revision 2 is stale; the current revision is 3",
            stale["content"]![0]!["text"]!.GetValue<string>(),
            StringComparison.Ordinal);

        var missingPreview = await host.CallResult("write_change", new { expectedRevision = revision });
        Assert.True(missingPreview["isError"]!.GetValue<bool>());
        Assert.Contains("proposal_preview", missingPreview["content"]![0]!["text"]!.GetValue<string>());
        await PreviewAll(host, revision);
        var written = await host.Call("write_change", new { expectedRevision = revision });
        Assert.Equal("Written", written["status"]!.GetValue<string>());
        Assert.False(written["aiReviewBypassed"]!.GetValue<bool>());

        var reopened = new SqliteProjectStore().Load(project);
        Assert.Contains(reopened.Graph.Nodes, node => node.Id.Value == "maintenance-note");
    }

    [Fact]
    public async Task Selection_is_explicit_isolated_and_initialization_is_purpose_only_and_non_overwriting()
    {
        using var temporary = new TemporaryDirectory();
        var first = Path.Combine(temporary.Path, "first.vw.db");
        var second = Path.Combine(temporary.Path, "second.vw.db");
        var created = Path.Combine(temporary.Path, "created.vw.db");
        var application = new ProjectApplication(new SqliteProjectStore());
        application.CreateSample("technical-project", first);
        application.Initialize(second, new ProjectId("second-project"), "Second", new EntityId("second-purpose"), "Second purpose");

        await using var host = await McpProcess.Start();
        _ = await host.Request("initialize", new
        {
            protocolVersion = "2024-11-05",
            capabilities = new { },
            clientInfo = new { name = "ValidatedWorld tests", version = "1" },
        });

        var unselected = await host.CallResult("project_status", new { });
        Assert.True(unselected["isError"]!.GetValue<bool>());

        var invalid = await host.CallResult("select_project", new { path = Path.Combine(temporary.Path, "missing.vw.db") });
        Assert.True(invalid["isError"]!.GetValue<bool>());

        var denied = await host.CallResult("initialize_project", new
        {
            path = Path.Combine(Path.GetDirectoryName(typeof(McpAssembly).Assembly.Location)!, "denied.vw.db"),
            projectId = "denied-project",
            title = "Denied",
            purposeNodeId = "purpose",
            purposeText = "Must remain outside the host installation directory",
        });
        Assert.True(denied["isError"]!.GetValue<bool>());

        var selected = await host.Call("select_project", new { path = first });
        Assert.Equal("technical-project", selected["project"]!["projectId"]!.GetValue<string>());
        var switchResult = await host.Call("select_project", new { path = second });
        Assert.Equal("second-project", switchResult["project"]!["projectId"]!.GetValue<string>());
        var crossProjectRead = await host.CallResult("read_node", new { nodeId = "purpose" });
        Assert.True(crossProjectRead["isError"]!.GetValue<bool>());
        var secondRead = await host.Call("read_node", new { nodeId = "second-purpose" });
        Assert.Equal("second-purpose", secondRead["item"]!["id"]!.GetValue<string>());

        var initialized = await host.Call("initialize_project", new
        {
            path = created,
            projectId = "created-project",
            title = "Created",
            purposeNodeId = "purpose",
            purposeText = "Only the governing purpose",
        });
        Assert.Equal("created-project", initialized["project"]!["projectId"]!.GetValue<string>());
        var createdNodes = await host.Call("list_nodes", new { });
        Assert.Equal(1, createdNodes["totalCount"]!.GetValue<int>());
        Assert.Equal("purpose", createdNodes["items"]![0]!["id"]!.GetValue<string>());

        var overwrite = await host.CallResult("initialize_project", new
        {
            path = created,
            projectId = "different-project",
            title = "Different",
            purposeNodeId = "purpose",
            purposeText = "Must not replace",
        });
        Assert.True(overwrite["isError"]!.GetValue<bool>());
        Assert.Equal("created-project", (await host.Call("project_status", new { }))["projectId"]!.GetValue<string>());
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ValidatedWorld.Mcp.Tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class McpProcess : IAsyncDisposable
    {
        private readonly Process process;
        private readonly ConcurrentQueue<string> errors = new();

        private McpProcess(Process process)
        {
            this.process = process;
        }

        public static Task<McpProcess> Start(
            string? defaultProject = null,
            IReadOnlyList<string>? artifactRoots = null)
        {
            var arguments = $"\"{typeof(McpAssembly).Assembly.Location}\"" +
                (defaultProject is null ? string.Empty : $" --project \"{defaultProject}\"") +
                string.Concat((artifactRoots ?? []).Select(root => $" --artifact-root \"{root}\""));
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            process.StartInfo.Environment["VW_AIREVIEW__ENABLED"] = "false";
            var host = new McpProcess(process);
            process.ErrorDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data is not null) host.errors.Enqueue(eventArgs.Data);
            };
            process.Start();
            process.BeginErrorReadLine();
            return Task.FromResult(host);
        }

        public async Task<JsonNode> Request(string method, object parameters)
        {
            var id = Guid.NewGuid().ToString("N");
            await process.StandardInput.WriteLineAsync($"{{\"jsonrpc\":\"2.0\",\"id\":\"{id}\",\"method\":\"{method}\",\"params\":{System.Text.Json.JsonSerializer.Serialize(parameters)}}}");
            await process.StandardInput.FlushAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            Assert.False(string.IsNullOrWhiteSpace(line));
            return JsonNode.Parse(line!)!;
        }

        public async Task<JsonNode> Call(string name, object arguments)
        {
            var response = await Request("tools/call", new { name, arguments });
            var structured = response["result"]?["structuredContent"];
            if (structured is null)
            {
                await Task.Delay(100);
                Assert.Fail(response.ToJsonString() + " stderr=" + string.Join(" | ", errors));
            }
            return structured["result"] ?? structured;
        }

        public async Task<JsonNode> CallResult(string name, object arguments) =>
            (await Request("tools/call", new { name, arguments }))["result"]!;

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}
