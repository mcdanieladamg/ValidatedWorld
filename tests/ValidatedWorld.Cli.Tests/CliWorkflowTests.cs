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
    public void Ai_review_default_requires_a_key_and_the_kill_switch_still_disables_it()
    {
        Assert.True(AiReviewConfiguration.DefaultEnabled);
        Assert.False(AiReviewConfiguration.DefaultLiveTests);
        var withoutKey = new AiReviewConfiguration(
            true, "openai", "test-model", 30, false, null);
        var disabled = new AiReviewConfiguration(
            false, "openai", "test-model", 30, false, "offline-test-key");

        Assert.False(withoutKey.IsConfigured);
        Assert.False(withoutKey.RuntimeOptions().Enabled);
        Assert.True(disabled.IsConfigured);
        Assert.False(disabled.RuntimeOptions().Enabled);
    }

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
        Assert.Contains("shell     Run the stateful flag-based interface", help.Output, StringComparison.Ordinal);
        Assert.Contains("ai-assistant-shell", help.Output, StringComparison.Ordinal);
        Assert.Contains("ndjson", help.Output, StringComparison.Ordinal);
        var shellHelp = await Run(["shell", "--help"]);
        Assert.Contains("commit --bypass-ai-review", shellHelp.Output, StringComparison.Ordinal);
        var assistantHelp = await Run(["ai-assistant-shell", "--help"]);
        Assert.Contains("cannot use raw SQL", assistantHelp.Output, StringComparison.OrdinalIgnoreCase);

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

        var health = await Run(["read", "health", project, "--limit", "1"]);
        Assert.Equal(CliRunner.SuccessExitCode, health.ExitCode);
        var healthJson = JsonNode.Parse(health.Output)!;
        Assert.Equal(13, healthJson["scopeCoverage"]!["totalNodeCount"]!.GetValue<int>());
        Assert.Equal(13, healthJson["scopeCoverage"]!["nodesReachingPurpose"]!.GetValue<int>());
        Assert.Single(healthJson["reviewFanOutHotspots"]!["items"]!.AsArray());
        Assert.Equal(3, healthJson["reviewFanOutHotspots"]!["totalCount"]!.GetValue<int>());
        Assert.Equal(3, healthJson["reviewFanOutHotspots"]!["omittedCount"]!.GetValue<int>() +
            healthJson["reviewFanOutHotspots"]!["items"]!.AsArray().Count);
        Assert.Equal("battery-assumption",
            healthJson["reviewFanOutHotspots"]!["items"]![0]!["nodeId"]!.GetValue<string>());
        var reportAlias = await Run(["read", "report", project, "--limit", "1"]);
        Assert.Equal(health.Output, reportAlias.Output);

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
    public async Task Public_initializers_create_only_a_purpose_and_ndjson_rejects_populated_graphs()
    {
        using var temporary = new TemporaryDirectory();
        var cliProject = Path.Combine(temporary.Path, "cli project.vw.db");
        var ndjsonProject = Path.Combine(temporary.Path, "ndjson project.vw.db");
        var populatedPath = Path.Combine(temporary.Path, "rejected project.vw.db");

        var cli = await Run(["project", "init", cliProject, "cli-project", "CLI Project", "purpose", "Keep it coherent"]);
        Assert.Equal(CliRunner.SuccessExitCode, cli.ExitCode);
        Assert.Equal(1, JsonNode.Parse((await Run(["project", "status", cliProject])).Output)!["nodeCount"]!.GetValue<int>());

        var ndjson = await Run(["ndjson"], string.Join(Environment.NewLine,
            Envelope("project.init", new
            {
                path = ndjsonProject,
                projectId = "ndjson-project",
                title = "NDJSON Project",
                purposeNodeId = "purpose",
                purposeText = "Keep the boundary clear",
            }),
            Envelope("project.open", new { path = ndjsonProject }),
            Envelope("host.exit", new { })) + Environment.NewLine);
        var ndjsonLines = Lines(ndjson.Output);
        Assert.Equal(3, ndjsonLines.Length);
        Assert.Equal("ok", JsonNode.Parse(ndjsonLines[0])!["status"]!.GetValue<string>());
        Assert.Single(JsonNode.Parse(ndjsonLines[1])!["payload"]!["graph"]!["nodes"]!.AsArray());

        var rejected = await Run(["ndjson"], Envelope("project.init", new
        {
            path = populatedPath,
            graph = new
            {
                projectId = "populated-project",
                title = "Populated Project",
                purposeNodeId = "purpose",
                nodes = new[]
                {
                    new { id = "purpose", text = "Purpose", kind = "purpose", tags = Array.Empty<string>(), attributes = Array.Empty<object>() },
                    new { id = "child", text = "Child", kind = "scope", tags = Array.Empty<string>(), attributes = Array.Empty<object>() },
                },
                edges = Array.Empty<object>(),
            },
        }) + Environment.NewLine);
        var rejectedJson = JsonNode.Parse(rejected.Output)!;
        Assert.Equal("error", rejectedJson["status"]!.GetValue<string>());
        Assert.Equal("malformed-json", rejectedJson["payload"]!["code"]!.GetValue<string>());
        Assert.False(File.Exists(populatedPath));
    }

    [Fact]
    public async Task Purpose_bootstrap_then_first_child_uses_reviewed_change_and_reopens()
    {
        using var temporary = new TemporaryDirectory();
        var project = Path.Combine(temporary.Path, "reviewed bootstrap.vw.db");
        await using var host = await NdjsonProcess.Start();

        var initialized = await host.Send("project.init", new
        {
            path = project,
            projectId = "reviewed-bootstrap",
            title = "Reviewed Bootstrap",
            purposeNodeId = "purpose",
            purposeText = "Keep the project coherent",
        });
        Assert.Equal("ok", initialized["status"]!.GetValue<string>());

        var begun = await host.Send("change.begin", new
        {
            path = project,
            projectId = "reviewed-bootstrap",
            author = "smoke tester",
            intent = "Add the first scope under the purpose",
        });
        var reference = begun["payload"]!["reference"]!.DeepClone();
        var patched = await host.SendNode("change.patch", new JsonObject
        {
            ["reference"] = reference,
            ["operations"] = new JsonObject
            {
                ["operations"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["kind"] = "add",
                        ["entityKind"] = "node",
                        ["entityId"] = "first-scope",
                        ["node"] = new JsonObject
                        {
                            ["id"] = "first-scope",
                            ["text"] = "The first scope",
                            ["kind"] = "scope",
                            ["tags"] = new JsonArray(),
                            ["attributes"] = new JsonArray(),
                        },
                        ["edge"] = null,
                    },
                    new JsonObject
                    {
                        ["kind"] = "add",
                        ["entityKind"] = "edge",
                        ["entityId"] = "first-scope-parent",
                        ["node"] = null,
                        ["edge"] = new JsonObject
                        {
                            ["id"] = "first-scope-parent",
                            ["source"] = "first-scope",
                            ["target"] = "purpose",
                            ["relationship"] = "scope-parent",
                            ["reviewDirection"] = "none",
                            ["rationale"] = null,
                            ["tags"] = new JsonArray(),
                            ["attributes"] = new JsonArray(),
                        },
                    },
                },
            },
        });
        var patchedPayload = patched["payload"]!;
        Assert.Equal("complete", patchedPayload["affected"]!["status"]!.GetValue<string>());
        var dispositions = new JsonArray(patchedPayload["affected"]!["affectedNodes"]!.AsArray()
            .Select(node => (JsonNode)new JsonObject
            {
                ["nodeId"] = node!["nodeId"]!.GetValue<string>(),
                ["kind"] = node["isDirectChange"]!.GetValue<bool>() ? "updated" : "reviewedNoChange",
            }).ToArray());
        var context = new JsonArray(patchedPayload["affected"]!["scopeContext"]!.AsArray()
            .Select(node => (JsonNode?)node!["nodeId"]!.GetValue<string>()).ToArray());
        var reviewed = await host.SendNode("change.review", new JsonObject
        {
            ["reference"] = patchedPayload["reference"]!.DeepClone(),
            ["dispositions"] = dispositions,
            ["presentedContextNodeIds"] = context,
        });
        Assert.True(reviewed["payload"]!["readiness"]!["isReady"]!.GetValue<bool>());

        var written = await host.SendNode("change.write", new JsonObject
        {
            ["reference"] = reviewed["payload"]!["reference"]!.DeepClone(),
        });
        Assert.Equal("written", written["payload"]!["status"]!.GetValue<string>());
        _ = await host.Send("host.exit", new { });
        Assert.Equal(0, await host.WaitForExit());

        var reopened = await Run(["project", "open", project]);
        Assert.Equal(2, JsonNode.Parse(reopened.Output)!["graph"]!["nodes"]!.AsArray().Count);
        Assert.Equal("The first scope",
            JsonNode.Parse((await Run(["read", "node", project, "first-scope"])).Output)!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Human_shell_accumulates_small_flag_based_edits_and_commits_with_one_line()
    {
        using var temporary = new TemporaryDirectory();
        var project = Path.Combine(temporary.Path, "human shell.vw.db");
        Assert.Equal(0, (await Run(["sample", "create", "technical-project", project])).ExitCode);
        var commands = string.Join(Environment.NewLine,
            "help",
            "begin --author \"Human tester\" --intent \"Update two related nodes\"",
            "cd missing-node",
            "node select --id battery-assumption",
            "node set --text \"The battery lasts for the shell target duty cycle\"",
            "node select --id runtime-test",
            "node set --text \"The runtime test verifies the shell target duty cycle\"",
            "changes",
            "affected",
            "review --id battery-assumption --as updated",
            "review --id runtime-test --as updated",
            "review --id power-design-anchor --as reviewed-no-change",
            "context mark --id purpose",
            "context mark --id scope-power",
            "validate",
            "commit --bypass-ai-review",
            "status",
            "exit") + Environment.NewLine;

        var result = await RunProcess(["shell", project], commands, enableAiReview: true);

        Assert.Equal(CliRunner.SuccessExitCode, result.ExitCode);
        Assert.Contains("Pending operations: 2", result.Output, StringComparison.Ordinal);
        Assert.Contains("replace node battery-assumption", result.Output, StringComparison.Ordinal);
        Assert.Contains("replace node runtime-test", result.Output, StringComparison.Ordinal);
        Assert.Contains("Ready to commit.", result.Output, StringComparison.Ordinal);
        Assert.Contains("Commit written", result.Output, StringComparison.Ordinal);
        Assert.Contains("No active change.", result.Output, StringComparison.Ordinal);
        Assert.Contains("Node 'missing-node' does not exist", result.Error, StringComparison.Ordinal);
        Assert.Equal("The battery lasts for the shell target duty cycle",
            JsonNode.Parse((await Run(["read", "node", project, "battery-assumption"])).Output)!["text"]!
                .GetValue<string>());
        Assert.Equal("The runtime test verifies the shell target duty cycle",
            JsonNode.Parse((await Run(["read", "node", project, "runtime-test"])).Output)!["text"]!
                .GetValue<string>());
    }

    [Fact]
    public async Task Human_shell_starts_at_root_and_navigates_scope_and_semantic_connections()
    {
        using var temporary = new TemporaryDirectory();
        var project = Path.Combine(temporary.Path, "human shell navigation.vw.db");
        Assert.Equal(0, (await Run(["sample", "create", "technical-project", project])).ExitCode);
        var commands = string.Join(Environment.NewLine,
            "pwd",
            "dir --limit 2",
            "cd scope-power",
            "pwd",
            "cd battery-assumption",
            "dir --limit 20 --upstream 2 --depth 1",
            "cd ..",
            "pwd",
            "cd /",
            "ls --scope-only --limit 20",
            "exit") + Environment.NewLine;

        var result = await Run(["shell", project], commands);

        Assert.Equal(CliRunner.SuccessExitCode, result.ExitCode);
        Assert.Contains("Selected root purpose", result.Output, StringComparison.Ordinal);
        Assert.Contains("/purpose", result.Output, StringComparison.Ordinal);
        Assert.Contains("... 2 more connections omitted; raise --limit.", result.Output, StringComparison.Ordinal);
        Assert.Contains("/purpose/scope-power", result.Output, StringComparison.Ordinal);
        Assert.Contains("[..1] scope-power", result.Output, StringComparison.Ordinal);
        Assert.Contains("[..2] purpose", result.Output, StringComparison.Ordinal);
        Assert.Contains("[out] runtime-test to battery-requires-runtime [requires/source-to-target]", result.Output,
            StringComparison.Ordinal);
        Assert.Contains("[out] power-design-anchor to battery-informs-power-anchor [informs/source-to-target]", result.Output,
            StringComparison.Ordinal);
        Assert.Contains("[scope +1] scope-accessibility", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("error[", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Human_shell_updates_single_metadata_values_moves_a_node_and_can_discard()
    {
        using var temporary = new TemporaryDirectory();
        var project = Path.Combine(temporary.Path, "human shell metadata.vw.db");
        Assert.Equal(0, (await Run(["sample", "create", "technical-project", project])).ExitCode);
        var commands = string.Join(Environment.NewLine,
            "begin --author tester --intent \"Exercise scalar commands\"",
            "node select --id runtime-test",
            "node tag-add --tag shell:test",
            "node attribute-set --name shell-mode --type symbol --value compact",
            "node move --parent scope-privacy",
            "cd scope-privacy",
            "dir --limit 20",
            "cd runtime-test",
            "node add --id shell-note --text \"Temporary shell note\" --parent scope-power",
            "node set --text \"Revised temporary shell note\"",
            "node remove",
            "edge select --id battery-requires-runtime",
            "edge set --rationale \"Checked through the human shell\"",
            "changes",
            "discard",
            "exit") + Environment.NewLine;

        var result = await Run(["shell", project], commands);

        Assert.Equal(CliRunner.SuccessExitCode, result.ExitCode);
        Assert.Contains("replace node runtime-test", result.Output, StringComparison.Ordinal);
        Assert.Contains("replace edge runtime-scope-parent", result.Output, StringComparison.Ordinal);
        Assert.Contains("[scope +1] runtime-test", result.Output, StringComparison.Ordinal);
        Assert.Contains("replace edge battery-requires-runtime", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("add node shell-note", result.Output, StringComparison.Ordinal);
        Assert.Contains("Discarded session", result.Output, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.Error);
        Assert.DoesNotContain("shell:test",
            JsonNode.Parse((await Run(["read", "node", project, "runtime-test"])).Output)!["tags"]!
                .AsArray().Select(item => item!.GetValue<string>()));
    }

    [Fact]
    public async Task Long_lived_ndjson_host_completes_reviewed_change_and_then_exits_cleanly()
    {
        using var temporary = new TemporaryDirectory();
        var project = Path.Combine(temporary.Path, "black box project.vw.db");
        var backup = Path.Combine(temporary.Path, "black box backup.vw.db");
        Assert.Equal(0, (await RunProcess(["sample", "create", "technical-project", project])).ExitCode);

        await using var host = await NdjsonProcess.Start(enableAiReview: true);
        var help = await host.Send("host.help", new { });
        Assert.Equal("ok", help["status"]!.GetValue<string>());
        Assert.Contains("change.write", help["payload"]!["commands"]!.AsArray()
            .Select(value => value!.GetValue<string>()));
        Assert.Contains("change.patch", help["payload"]!["commands"]!.AsArray()
            .Select(value => value!.GetValue<string>()));
        Assert.Contains("read.tag", help["payload"]!["commands"]!.AsArray()
            .Select(value => value!.GetValue<string>()));
        Assert.Contains("read.health", help["payload"]!["commands"]!.AsArray()
            .Select(value => value!.GetValue<string>()));
        Assert.DoesNotContain("ai.review", help["payload"]!["commands"]!.AsArray()
            .Select(value => value!.GetValue<string>()));
        var aiStatus = await host.Send("ai.status", new { });
        Assert.True(aiStatus["payload"]!["enabled"]!.GetValue<bool>());
        Assert.True(aiStatus["payload"]!["configured"]!.GetValue<bool>());

        var tagged = await host.Send("read.tag", new { path = project, tag = "artifact" });
        Assert.Equal(2, tagged["payload"]!["totalCount"]!.GetValue<int>());
        var health = await host.Send("read.health", new { path = project, limit = 1 });
        Assert.Equal(13, health["payload"]!["nodeCount"]!.GetValue<int>());
        Assert.Equal("battery-assumption",
            health["payload"]!["reviewFanOutHotspots"]!["items"]![0]!["nodeId"]!.GetValue<string>());

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
            ["bypassAiReview"] = true,
        });
        Assert.Equal("written", write["payload"]!["status"]!.GetValue<string>());
        Assert.True(write["payload"]!["aiReviewBypassed"]!.GetValue<bool>());
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
    public async Task Ndjson_patch_accumulates_small_edits_and_supports_compact_responses()
    {
        using var temporary = new TemporaryDirectory();
        var project = Path.Combine(temporary.Path, "incremental NDJSON project.vw.db");
        Assert.Equal(0, (await Run(["sample", "create", "technical-project", project])).ExitCode);

        await using var host = await NdjsonProcess.Start();
        var battery = (JsonObject)(await host.Send("read.node", new
        {
            path = project,
            entityId = "battery-assumption",
        }))["payload"]!.DeepClone();
        var runtime = (JsonObject)(await host.Send("read.node", new
        {
            path = project,
            entityId = "runtime-test",
        }))["payload"]!.DeepClone();

        var begin = await host.Send("change.begin", new
        {
            path = project,
            projectId = "technical-project",
            author = "incremental protocol tester",
            intent = "Accumulate two small patches",
            includeOperations = false,
            includeProposedGraph = false,
        });
        Assert.Equal(0, begin["payload"]!["operationCount"]!.GetValue<int>());
        Assert.Null(begin["payload"]!["operations"]);
        Assert.Null(begin["payload"]!["proposedGraph"]);

        var first = await host.SendNode("change.patch", new JsonObject
        {
            ["reference"] = begin["payload"]!["reference"]!.DeepClone(),
            ["operations"] = Batch(ReplaceNode(
                battery,
                "The battery lasts for the incremental NDJSON target duty cycle")),
            ["includeOperations"] = false,
            ["includeProposedGraph"] = false,
        });
        Assert.Equal(1, first["payload"]!["operationCount"]!.GetValue<int>());
        Assert.Null(first["payload"]!["operations"]);
        Assert.Null(first["payload"]!["proposedGraph"]);

        var second = await host.SendNode("change.patch", new JsonObject
        {
            ["reference"] = first["payload"]!["reference"]!.DeepClone(),
            ["operations"] = Batch(ReplaceNode(
                runtime,
                "The runtime test covers the incremental NDJSON target")),
            ["includeOperations"] = false,
            ["includeProposedGraph"] = false,
        });
        Assert.Equal(2, second["payload"]!["operationCount"]!.GetValue<int>());

        var normalized = await host.SendNode("change.patch", new JsonObject
        {
            ["reference"] = second["payload"]!["reference"]!.DeepClone(),
            ["operations"] = Batch(ReplaceNode(battery, battery["text"]!.GetValue<string>())),
            ["includeOperations"] = false,
            ["includeProposedGraph"] = false,
        });
        Assert.Equal(1, normalized["payload"]!["operationCount"]!.GetValue<int>());

        var locator = new JsonObject
        {
            ["projectId"] = "technical-project",
            ["sessionId"] = normalized["payload"]!["reference"]!["sessionId"]!.GetValue<string>(),
        };
        var finalOperations = await host.SendNode("change.show", new JsonObject
        {
            ["session"] = locator,
            ["includeOperations"] = true,
            ["includeProposedGraph"] = false,
        });
        var operations = finalOperations["payload"]!["operations"]!["operations"]!.AsArray();
        Assert.Single(operations);
        Assert.Equal("runtime-test", operations[0]!["entityId"]!.GetValue<string>());
        Assert.Null(finalOperations["payload"]!["proposedGraph"]);

        var stale = await host.SendNode("change.patch", new JsonObject
        {
            ["reference"] = first["payload"]!["reference"]!.DeepClone(),
            ["operations"] = Batch(ReplaceNode(runtime, "A stale patch must be rejected")),
            ["includeOperations"] = false,
            ["includeProposedGraph"] = false,
        });
        Assert.Equal("error", stale["status"]!.GetValue<string>());
        Assert.Equal("change-stale-operation-fingerprint", stale["payload"]!["code"]!.GetValue<string>());

        var discarded = await host.SendNode("change.discard", new JsonObject
        {
            ["reference"] = normalized["payload"]!["reference"]!.DeepClone(),
        });
        Assert.Equal("ok", discarded["status"]!.GetValue<string>());
        _ = await host.Send("host.exit", new { });
        Assert.Equal(0, await host.WaitForExit());
        Assert.Equal(string.Empty, await host.ErrorText());

        static JsonObject Batch(JsonObject operation) => new()
        {
            ["operations"] = new JsonArray(operation),
        };

        static JsonObject ReplaceNode(JsonObject original, string text)
        {
            var node = (JsonObject)original.DeepClone();
            node["text"] = text;
            return new JsonObject
            {
                ["kind"] = "replace",
                ["entityKind"] = "node",
                ["entityId"] = node["id"]!.GetValue<string>(),
                ["node"] = node,
                ["edge"] = null,
            };
        }
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

    [Fact]
    public async Task Disabled_ai_assistant_command_falls_back_to_the_existing_manual_shell()
    {
        using var temporary = new TemporaryDirectory();
        var project = Path.Combine(temporary.Path, "manual-fallback.vw.db");
        Assert.Equal(0, (await RunProcess(["sample", "create", "technical-project", project])).ExitCode);

        var fallback = await RunProcess(
            ["ai-assistant-shell", project],
            "status" + Environment.NewLine + "exit" + Environment.NewLine);
        Assert.Equal(CliRunner.SuccessExitCode, fallback.ExitCode);
        Assert.Contains("AI authoring is disabled", fallback.Output, StringComparison.Ordinal);
        Assert.Contains("technical-project", fallback.Output, StringComparison.Ordinal);

        var missing = await RunProcess(
            ["ai-assistant-shell", Path.Combine(temporary.Path, "missing.vw.db")],
            "exit" + Environment.NewLine);
        Assert.Equal(CliRunner.UsageExitCode, missing.ExitCode);
        Assert.Contains("manual fallback requires an existing database", missing.Error, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<CliResult> Run(string[] arguments, string input = "")
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await CliRunner.RunAsync(arguments, new StringReader(input), output, error);
        return new CliResult(exitCode, output.ToString(), error.ToString());
    }

    private static async Task<CliResult> RunProcess(
        string[] arguments,
        string? input = null,
        bool enableAiReview = false)
    {
        using var process = CreateProcess(arguments);
        if (enableAiReview)
        {
            process.StartInfo.Environment["VW_AIREVIEW__ENABLED"] = "true";
            process.StartInfo.Environment["OPENAI_API_KEY"] = "offline-test-key";
        }
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (input is not null)
        {
            await process.StandardInput.WriteAsync(input);
            await process.StandardInput.DisposeAsync();
        }
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
        start.Environment["VW_AIREVIEW__ENABLED"] = "false";
        start.Environment["VW_AIAUTHORING__ENABLED"] = "false";
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

        public static Task<NdjsonProcess> Start(bool enableAiReview = false)
        {
            var process = CreateProcess(["ndjson"]);
            process.StartInfo.Environment["VW_AIREVIEW__ENABLED"] = enableAiReview ? "true" : "false";
            if (enableAiReview)
                process.StartInfo.Environment["OPENAI_API_KEY"] = "offline-test-key";
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
