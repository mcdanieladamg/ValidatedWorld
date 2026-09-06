using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using ValidatedWorld.Application;
using ValidatedWorld.Core;
using ValidatedWorld.Mcp;
using ValidatedWorld.Persistence.Sqlite;

namespace ValidatedWorld.Cli.Tests;

public sealed class McpWorkflowTests
{
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
        Assert.Contains(toolItems, tool => tool!["name"]!.GetValue<string>() == "read_context");
        Assert.Contains(toolItems, tool => tool!["name"]!.GetValue<string>() == "begin_change");
        Assert.Contains(toolItems, tool => tool!["name"]!.GetValue<string>() == "confirm_approval");
        Assert.DoesNotContain(toolItems, tool => tool!["name"]!.GetValue<string>().Contains("bypass", StringComparison.OrdinalIgnoreCase));
        var listNodesTool = toolItems.Single(tool => tool!["name"]!.GetValue<string>() == "list_nodes");
        Assert.Equal("integer", listNodesTool!["inputSchema"]!["properties"]!["limit"]!["type"]!.GetValue<string>());

        var hostStatus = await host.Call("host_status", new { });
        Assert.Equal(McpAssembly.ProductVersion, hostStatus["productVersion"]!.GetValue<string>());
        Assert.Equal("local-only", hostStatus["hostSupport"]!.GetValue<string>());
        Assert.Equal("stdio", hostStatus["transport"]!.GetValue<string>());
        Assert.False(hostStatus["semanticReview"]!["effective"]!.GetValue<bool>());
        Assert.Null(hostStatus["semanticReview"]!["apiKey"]);

        var status = await host.Call("project_status", new { });
        Assert.Equal("technical-project", status["projectId"]!.GetValue<string>());
        Assert.Equal(Path.GetFullPath(project), status["path"]!.GetValue<string>());

        var page = await host.Call("list_nodes", new { limit = 1 });
        Assert.Single(page["items"]!.AsArray());
        Assert.Equal(13, page["totalCount"]!.GetValue<int>());
        Assert.NotNull(page["nextCursor"]);

        var search = await host.Call("search", new { text = "battery", limit = 1 });
        Assert.Equal(4, search["totalCount"]!.GetValue<int>());
        Assert.Equal("battery-assumption", search["items"]![0]!["entityId"]!.GetValue<string>());

        var after = new SqliteProjectStore().Load(project);
        Assert.Equal(before.StateFingerprint, after.StateFingerprint);
        Assert.Equal(before.UpdatedUtc, after.UpdatedUtc);
    }

    [Fact]
    public async Task Reviewed_edit_requires_current_revision_and_human_token_then_reopens_written_graph()
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

        var preview = await host.Call("proposal_preview", new { expectedRevision = revision });
        Assert.Equal(2, preview["operationCount"]!.GetValue<int>());
        Assert.False(preview["readiness"]!["isReady"]!.GetValue<bool>());
        Assert.NotEmpty(preview["readiness"]!["pendingNodeIds"]!.AsArray());

        var requested = await host.Call("request_approval", new { expectedRevision = revision });
        Assert.True(requested["approvalRequired"]!.GetValue<bool>());
        var fakeApproval = await host.CallResult("confirm_approval", new
        {
            expectedRevision = revision,
            token = "yes",
        });
        Assert.True(fakeApproval["isError"]!.GetValue<bool>());

        var tokenLine = await host.ReadErrorUntil("One-time approval token", TimeSpan.FromSeconds(5));
        var token = tokenLine[(tokenLine.IndexOf(": ", StringComparison.Ordinal) + 2)..].Trim();
        var approved = await host.Call("confirm_approval", new { expectedRevision = revision, token });
        Assert.True(approved["approved"]!.GetValue<bool>());
        var approvedRevision = approved["revision"]!.GetValue<int>();
        Assert.True(approvedRevision > revision);

        var written = await host.Call("write_change", new { expectedRevision = approvedRevision });
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

        public static Task<McpProcess> Start(string? defaultProject = null)
        {
            var arguments = $"\"{typeof(McpAssembly).Assembly.Location}\"" +
                (defaultProject is null ? string.Empty : $" --project \"{defaultProject}\"");
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

        public async Task<string> ReadErrorUntil(string text, TimeSpan timeout)
        {
            using var cancellation = new CancellationTokenSource(timeout);
            while (true)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (errors.TryDequeue(out var line) && line.Contains(text, StringComparison.Ordinal)) return line;
                await Task.Delay(10, cancellation.Token);
            }
        }

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
