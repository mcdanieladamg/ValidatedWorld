using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using ValidatedWorld.Cli;

namespace ValidatedWorld.Cli.Tests;

public sealed class CliWorkflowTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [Fact]
    public async Task Help_drives_one_shot_reads_backup_and_deterministic_safe_sql_export()
    {
        using var temporary = new TemporaryDirectory();
        var project = Path.Combine(temporary.Path, "project with spaces.vw.db");
        var backup = Path.Combine(temporary.Path, "backup with spaces.vw.db");
        var quoted = Path.Combine(temporary.Path, "quoted project.vw.db");
        var restored = Path.Combine(temporary.Path, "restored export.vw.db");

        var help = await Run(["--help"]);
        Assert.Equal(CliRunner.SuccessExitCode, help.ExitCode);
        Assert.Contains("read      Run bounded graph queries", help.Output, StringComparison.Ordinal);
        Assert.Contains("ndjson", help.Output, StringComparison.Ordinal);

        var created = await Run(["sample", "create", "technical-project", project]);
        Assert.Equal(CliRunner.SuccessExitCode, created.ExitCode);
        Assert.Equal("technical-project", JsonNode.Parse(created.Output)!["projectId"]!.GetValue<string>());

        var opened = await Run(["project", "open", project]);
        Assert.Equal(13, JsonNode.Parse(opened.Output)!["graph"]!["nodes"]!.AsArray().Count);

        var search = await Run(["read", "search", project, "battery", "--limit", "1"]);
        Assert.Equal(CliRunner.SuccessExitCode, search.ExitCode);
        var searchJson = JsonNode.Parse(search.Output)!;
        Assert.Equal(4, searchJson["totalCount"]!.GetValue<int>());
        Assert.Equal("battery-assumption", searchJson["items"]![0]!["entityId"]!.GetValue<string>());

        var tagged = await Run(["read", "tag", project, "artifact"]);
        Assert.Equal(CliRunner.SuccessExitCode, tagged.ExitCode);
        var taggedJson = JsonNode.Parse(tagged.Output)!;
        Assert.Equal(2, taggedJson["totalCount"]!.GetValue<int>());
        Assert.All(taggedJson["items"]!.AsArray(), item =>
            Assert.Contains("artifact", item!["node"]!["tags"]!.AsArray()
                .Select(tag => tag!.GetValue<string>())));
        Assert.Equal(0, JsonNode.Parse((await Run(["read", "tag", project, "Artifact"])).Output)!
            ["totalCount"]!.GetValue<int>());

        Assert.Equal(2, JsonNode.Parse((await Run(["read", "nodes", project, "--limit", "2"])).Output)!
            ["items"]!.AsArray().Count);
        Assert.NotEmpty(JsonNode.Parse((await Run(["read", "edges", project])).Output)!["items"]!.AsArray());
        Assert.Equal("battery-requires-runtime",
            JsonNode.Parse((await Run(["read", "edge", project, "battery-requires-runtime"])).Output)!
                ["id"]!.GetValue<string>());
        Assert.Equal("scope-power",
            JsonNode.Parse((await Run(["read", "scope", project, "battery-assumption"])).Output)!
                ["upstream"]![0]!["id"]!.GetValue<string>());
        Assert.NotEmpty(JsonNode.Parse((await Run(["read", "neighbors", project, "battery-assumption"])).Output)!
            ["items"]!.AsArray());
        Assert.NotEmpty(JsonNode.Parse((await Run(["read", "dependencies", project, "battery-assumption"])).Output)!
            ["items"]!.AsArray());
        Assert.True(JsonNode.Parse((await Run([
            "read", "path", project, "battery-assumption", "runtime-test",
        ])).Output)!["found"]!.GetValue<bool>());
        Assert.Contains(JsonNode.Parse((await Run([
            "read", "context", project, "battery-assumption,retention-policy",
        ])).Output)!["contextNodes"]!.AsArray(), node => node!["id"]!.GetValue<string>() == "purpose");

        var backedUp = await Run(["project", "backup", project, backup]);
        Assert.Equal(CliRunner.SuccessExitCode, backedUp.ExitCode);
        var verified = await Run(["project", "verify", backup]);
        Assert.True(JsonNode.Parse(verified.Output)!["isValid"]!.GetValue<bool>());

        var initialized = await Run([
            "project", "init", quoted, "quoted-project", "O'Brien project", "purpose", "It's complete",
        ]);
        Assert.Equal(CliRunner.SuccessExitCode, initialized.ExitCode);
        var firstExport = await Run(["project", "export-sql", quoted]);
        var secondExport = await Run(["project", "export-sql", quoted]);
        Assert.Equal(firstExport.Output, secondExport.Output);
        Assert.Contains("'O''Brien project'", firstExport.Output, StringComparison.Ordinal);
        Assert.Contains("'It''s complete'", firstExport.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("session", firstExport.Output, StringComparison.OrdinalIgnoreCase);

        SQLitePCL.Batteries_V2.Init();
        var restoredConnection = new SqliteConnectionStringBuilder
        {
            DataSource = restored,
            Pooling = false,
        }.ToString();
        await using (var connection = new SqliteConnection(restoredConnection))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = firstExport.Output;
            await command.ExecuteNonQueryAsync();
        }

        var restoredStatus = await Run(["project", "status", restored]);
        Assert.Equal(CliRunner.SuccessExitCode, restoredStatus.ExitCode);
        Assert.Equal("quoted-project", JsonNode.Parse(restoredStatus.Output)!["projectId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Long_lived_ndjson_host_completes_reviewed_change_and_then_exits_cleanly()
    {
        using var temporary = new TemporaryDirectory();
        var project = Path.Combine(temporary.Path, "black box project.vw.db");
        var backup = Path.Combine(temporary.Path, "black box backup.vw.db");
        Assert.Equal(0, (await RunProcess(["sample", "create", "technical-project", project])).ExitCode);

        await using var host = await NdjsonProcess.Start();
        var help = await host.Send("host.help", new { });
        Assert.Equal("ok", help["status"]!.GetValue<string>());
        Assert.Contains("change.write", help["payload"]!["commands"]!.AsArray()
            .Select(value => value!.GetValue<string>()));
        Assert.Contains("read.tag", help["payload"]!["commands"]!.AsArray()
            .Select(value => value!.GetValue<string>()));

        var tagged = await host.Send("read.tag", new { path = project, tag = "artifact" });
        Assert.Equal(2, tagged["payload"]!["totalCount"]!.GetValue<int>());

        var begin = await host.Send("change.begin", new
        {
            path = project,
            projectId = "technical-project",
            author = "CLI smoke tester",
            intent = "Revise battery assumption",
        });
        var beginPayload = begin["payload"]!;
        var sessionId = beginPayload["reference"]!["sessionId"]!.GetValue<string>();
        var apply = await host.SendNode("change.apply", new JsonObject
        {
            ["reference"] = beginPayload["reference"]!.DeepClone(),
            ["operations"] = new JsonObject
            {
                ["operations"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["kind"] = "replace",
                        ["entityKind"] = "node",
                        ["entityId"] = "battery-assumption",
                        ["node"] = new JsonObject
                        {
                            ["id"] = "battery-assumption",
                            ["text"] = "The battery lasts for the revised target duty cycle",
                            ["kind"] = "assumption",
                            ["tags"] = new JsonArray(),
                            ["attributes"] = new JsonArray(),
                        },
                        ["edge"] = null,
                    },
                },
            },
        });
        Assert.Equal("complete", apply["payload"]!["affected"]!["status"]!.GetValue<string>());
        Assert.DoesNotContain(apply["payload"]!["affected"]!["affectedNodes"]!.AsArray(), node =>
            node!["nodeId"]!.GetValue<string>() == "retention-policy");

        var export = await host.Send("project.export-sql", new { path = project });
        var sql = export["payload"]!["sql"]!.GetValue<string>();
        Assert.DoesNotContain(sessionId, sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CLI smoke tester", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("Revise battery assumption", sql, StringComparison.Ordinal);

        var affectedNodes = apply["payload"]!["affected"]!["affectedNodes"]!.AsArray();
        var dispositions = new JsonArray(affectedNodes.Select(node => (JsonNode)new JsonObject
        {
            ["nodeId"] = node!["nodeId"]!.GetValue<string>(),
            ["kind"] = node["isDirectChange"]!.GetValue<bool>() ? "updated" : "reviewedNoChange",
        }).ToArray());
        var context = new JsonArray(apply["payload"]!["affected"]!["scopeContext"]!.AsArray()
            .Select(node => (JsonNode?)node!["nodeId"]!.GetValue<string>()).ToArray());
        var review = await host.SendNode("change.review", new JsonObject
        {
            ["reference"] = apply["payload"]!["reference"]!.DeepClone(),
            ["dispositions"] = dispositions,
            ["presentedContextNodeIds"] = context,
        });
        Assert.True(review["payload"]!["readiness"]!["isReady"]!.GetValue<bool>());

        var write = await host.SendNode("change.write", new JsonObject
        {
            ["reference"] = review["payload"]!["reference"]!.DeepClone(),
        });
        Assert.Equal("written", write["payload"]!["status"]!.GetValue<string>());
        var exit = await host.Send("host.exit", new { });
        Assert.Empty(exit["payload"]!["warnings"]!.AsArray());
        Assert.Equal(0, await host.WaitForExit());
        Assert.Equal(string.Empty, await host.ErrorText());

        var nodeResult = await RunProcess(["read", "node", project, "battery-assumption"]);
        Assert.Equal(0, nodeResult.ExitCode);
        Assert.Equal("The battery lasts for the revised target duty cycle",
            JsonNode.Parse(nodeResult.Output)!["text"]!.GetValue<string>());
        Assert.Equal(0, (await RunProcess(["project", "backup", project, backup])).ExitCode);
        Assert.Equal(0, (await RunProcess(["project", "verify", backup])).ExitCode);
    }

    [Fact]
    public async Task Ndjson_rejects_malformed_commands_continues_and_does_not_persist_sessions()
    {
        using var temporary = new TemporaryDirectory();
        var project = Path.Combine(temporary.Path, "session loss.vw.db");
        Assert.Equal(0, (await Run(["sample", "create", "technical-project", project])).ExitCode);

        var malformedInput = string.Join(Environment.NewLine,
            "not-json",
            Envelope("unknown.command", new { unexpected = true }),
            Envelope("host.exit", new { })) + Environment.NewLine;
        var malformed = await Run(["ndjson"], malformedInput);
        var malformedLines = Lines(malformed.Output);
        Assert.Equal(3, malformedLines.Length);
        Assert.Equal("error", JsonNode.Parse(malformedLines[0])!["status"]!.GetValue<string>());
        Assert.Equal("error", JsonNode.Parse(malformedLines[1])!["status"]!.GetValue<string>());
        Assert.Equal("ok", JsonNode.Parse(malformedLines[2])!["status"]!.GetValue<string>());

        var first = await Run(["ndjson"], Envelope("change.begin", new
        {
            path = project,
            projectId = "technical-project",
            author = "session tester",
            intent = "prove process lifetime",
        }) + Environment.NewLine);
        var firstPayload = JsonNode.Parse(Lines(first.Output).Single())!["payload"]!;
        var session = new
        {
            projectId = "technical-project",
            sessionId = firstPayload["reference"]!["sessionId"]!.GetValue<string>(),
        };
        Assert.Contains("warning[session-loss]", first.Error, StringComparison.Ordinal);

        var second = await Run(["ndjson"], string.Join(Environment.NewLine,
            Envelope("change.show", new { session }),
            Envelope("host.exit", new { })) + Environment.NewLine);
        var show = JsonNode.Parse(Lines(second.Output)[0])!;
        Assert.Equal("error", show["status"]!.GetValue<string>());
        Assert.Equal("change-session-not-found", show["payload"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Ndjson_exposes_focus_show_expand_affected_validate_and_discard()
    {
        using var temporary = new TemporaryDirectory();
        var project = Path.Combine(temporary.Path, "remaining commands.vw.db");
        Assert.Equal(0, (await RunProcess(["sample", "create", "technical-project", project])).ExitCode);

        await using var host = await NdjsonProcess.Start();
        var begin = await host.Send("change.begin", new
        {
            path = project,
            projectId = "technical-project",
            author = "command coverage tester",
            intent = "Add a scoped note",
        });
        var beginReference = begin["payload"]!["reference"]!;
        var locator = new JsonObject
        {
            ["projectId"] = "technical-project",
            ["sessionId"] = beginReference["sessionId"]!.GetValue<string>(),
        };
        var shown = await host.SendNode("change.show", new JsonObject { ["session"] = locator.DeepClone() });
        Assert.Equal("Add a scoped note", shown["payload"]!["intent"]!.GetValue<string>());

        var operations = new JsonObject
        {
            ["operations"] = new JsonArray
            {
                new JsonObject
                {
                    ["kind"] = "add",
                    ["entityKind"] = "node",
                    ["entityId"] = "operator-note",
                    ["node"] = new JsonObject
                    {
                        ["id"] = "operator-note",
                        ["text"] = "Operator guidance follows the power scope",
                        ["kind"] = "documentation",
                        ["tags"] = new JsonArray(),
                        ["attributes"] = new JsonArray(),
                    },
                    ["edge"] = null,
                },
            },
        };
        var focus = await host.SendNode("change.focus", new JsonObject
        {
            ["reference"] = beginReference.DeepClone(),
            ["operations"] = operations.DeepClone(),
            ["scopeParents"] = new JsonArray
            {
                new JsonObject
                {
                    ["childId"] = "operator-note",
                    ["parentId"] = "scope-power",
                    ["edgeId"] = "operator-note-scope-parent",
                },
            },
        });
        Assert.Equal(2, focus["payload"]!["expandedOperations"]!["operations"]!.AsArray().Count);

        var apply = await host.SendNode("change.apply", new JsonObject
        {
            ["reference"] = beginReference.DeepClone(),
            ["operations"] = focus["payload"]!["expandedOperations"]!.DeepClone(),
        });
        var expanded = await host.SendNode("change.expand", new JsonObject
        {
            ["reference"] = apply["payload"]!["reference"]!.DeepClone(),
        });
        var affected = await host.SendNode("change.affected", new JsonObject
        {
            ["session"] = locator.DeepClone(),
        });
        Assert.Contains(affected["payload"]!["affectedNodes"]!.AsArray(), node =>
            node!["nodeId"]!.GetValue<string>() == "operator-note");
        var validated = await host.SendNode("change.validate", new JsonObject
        {
            ["reference"] = expanded["payload"]!["reference"]!.DeepClone(),
        });
        Assert.False(validated["payload"]!["readiness"]!["isReady"]!.GetValue<bool>());
        var discarded = await host.SendNode("change.discard", new JsonObject
        {
            ["reference"] = validated["payload"]!["reference"]!.DeepClone(),
        });
        Assert.Equal("operator-note", focus["payload"]!["expandedOperations"]!["operations"]![0]!["entityId"]!
            .GetValue<string>());
        Assert.Equal(beginReference["sessionId"]!.GetValue<string>(),
            discarded["payload"]!["sessionId"]!.GetValue<string>());
        _ = await host.Send("host.exit", new { });
        Assert.Equal(0, await host.WaitForExit());

        var missing = await RunProcess(["read", "node", project, "operator-note"]);
        Assert.Equal(CliRunner.DomainErrorExitCode, missing.ExitCode);
    }

    [Fact]
    public async Task Exit_codes_cancellation_broken_pipe_and_provider_boundary_are_explicit()
    {
        Assert.Equal(CliRunner.UsageExitCode, (await Run(["unknown"])).ExitCode);
        Assert.Equal(CliRunner.DomainErrorExitCode,
            (await Run(["project", "status", Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.vw.db")]))
            .ExitCode);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = await CliRunner.RunAsync(
            ["ndjson"], new StringReader(string.Empty), new StringWriter(), new StringWriter(), cancellation.Token);
        Assert.Equal(CliRunner.CancelledExitCode, cancelled);

        var broken = await CliRunner.RunAsync(
            ["sample", "list"], new StringReader(string.Empty), new BrokenWriter(), new StringWriter());
        Assert.Equal(CliRunner.BrokenPipeExitCode, broken);

        Assert.DoesNotContain(typeof(CliRunner).Assembly.GetReferencedAssemblies(), reference =>
            reference.Name?.Contains("OpenAI", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static async Task<CliResult> Run(string[] arguments, string input = "")
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await CliRunner.RunAsync(arguments, new StringReader(input), output, error);
        return new CliResult(exitCode, output.ToString(), error.ToString());
    }

    private static async Task<CliResult> RunProcess(string[] arguments)
    {
        using var process = CreateProcess(arguments);
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        return new CliResult(process.ExitCode, await output, await error);
    }

    private static Process CreateProcess(IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(typeof(CliRunner).Assembly.Location);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return new Process { StartInfo = start };
    }

    private static string Envelope(string command, object payload) => JsonSerializer.Serialize(new
    {
        version = 1,
        command,
        payload,
    }, JsonOptions);

    private static string[] Lines(string value) => value.Split(
        ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private sealed record CliResult(int ExitCode, string Output, string Error);

    private sealed class BrokenWriter : StringWriter
    {
        public override Task WriteLineAsync(string? value) => throw new IOException("simulated broken pipe");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ValidatedWorld.Cli.Tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class NdjsonProcess : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _error;

        private NdjsonProcess(Process process)
        {
            _process = process;
            _error = process.StandardError.ReadToEndAsync();
        }

        public static Task<NdjsonProcess> Start()
        {
            var process = CreateProcess(["ndjson"]);
            process.Start();
            return Task.FromResult(new NdjsonProcess(process));
        }

        public Task<JsonNode> Send(string command, object payload) =>
            SendLine(Envelope(command, payload));

        public Task<JsonNode> SendNode(string command, JsonNode payload) =>
            SendLine(new JsonObject
            {
                ["version"] = 1,
                ["command"] = command,
                ["payload"] = payload,
            }.ToJsonString(JsonOptions));

        public async Task<int> WaitForExit()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _process.WaitForExitAsync(timeout.Token);
            return _process.ExitCode;
        }

        public Task<string> ErrorText() => _error;

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
            {
                await _process.StandardInput.DisposeAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await _process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }

            _process.Dispose();
        }

        private async Task<JsonNode> SendLine(string line)
        {
            await _process.StandardInput.WriteLineAsync(line);
            await _process.StandardInput.FlushAsync();
            var response = await _process.StandardOutput.ReadLineAsync();
            Assert.False(string.IsNullOrWhiteSpace(response));
            return JsonNode.Parse(response!)!;
        }
    }
}
