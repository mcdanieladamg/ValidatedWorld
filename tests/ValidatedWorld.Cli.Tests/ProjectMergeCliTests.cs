using System.Text.Json;
using System.Text.Json.Nodes;
using ValidatedWorld.Cli;

namespace ValidatedWorld.Cli.Tests;

public sealed class ProjectMergeCliTests
{
    [Fact]
    public async Task Project_merge_is_read_only_and_is_available_through_ndjson()
    {
        using var workspace = new TemporaryDirectory();
        var basePath = Path.Combine(workspace.Path, "base.vw.db");
        var oursPath = Path.Combine(workspace.Path, "ours.vw.db");
        var theirsPath = Path.Combine(workspace.Path, "theirs.vw.db");

        Assert.Equal(0, (await Run(["sample", "create", "technical-project", basePath])).ExitCode);
        Assert.Equal(0, (await Run(["sample", "create", "technical-project", oursPath])).ExitCode);
        Assert.Equal(0, (await Run(["sample", "create", "technical-project", theirsPath])).ExitCode);

        Assert.Equal(0, (await Run(["shell", oursPath], Change("accessibility-acceptance",
            "Ours accessibility acceptance", "scope-accessibility"))).ExitCode);
        Assert.Equal(0, (await Run(["shell", theirsPath], Change("privacy-documentation",
            "Theirs privacy documentation", "scope-documentation"))).ExitCode);
        var baseBytes = File.ReadAllBytes(basePath);
        var oursBytes = File.ReadAllBytes(oursPath);
        var theirsBytes = File.ReadAllBytes(theirsPath);

        var help = await Run(["project", "--help"]);
        Assert.Contains("project merge <base-database> <ours-database> <theirs-database>", help.Output,
            StringComparison.Ordinal);

        var merged = await Run(["project", "merge", basePath, oursPath, theirsPath]);
        Assert.Equal(CliRunner.SuccessExitCode, merged.ExitCode);
        var json = JsonNode.Parse(merged.Output)!;
        Assert.Equal("clean", json["status"]!.GetValue<string>());
        Assert.True(json["isReadyToApply"]!.GetValue<bool>());
        Assert.Equal(1, json["operationCount"]!.GetValue<int>());
        Assert.NotNull(json["mergedFingerprint"]);
        Assert.Null(json["mergedGraph"]);
        Assert.Single(json["operations"]!["operations"]!.AsArray());
        Assert.Contains(json["operations"]!["operations"]!.AsArray(), operation =>
            operation!["entityId"]!.GetValue<string>() == "privacy-documentation");
        Assert.Empty(json["conflicts"]!.AsArray());

        var ndjsonInput = JsonSerializer.Serialize(new
        {
            version = 1,
            command = "project.merge",
            payload = new { basePath, oursPath, theirsPath },
        }) + Environment.NewLine;
        var ndjson = await Run(["ndjson"], ndjsonInput);
        var ndjsonJson = JsonNode.Parse(ndjson.Output)!;
        Assert.Equal("ok", ndjsonJson["status"]!.GetValue<string>());
        Assert.Equal("clean", ndjsonJson["payload"]!["status"]!.GetValue<string>());

        Assert.Equal(baseBytes, File.ReadAllBytes(basePath));
        Assert.Equal(oursBytes, File.ReadAllBytes(oursPath));
        Assert.Equal(theirsBytes, File.ReadAllBytes(theirsPath));
    }

    private static string Change(string nodeId, string text, string scopeId) => string.Join(
        Environment.NewLine,
        "begin --author tester --intent \"Prepare a branch merge\"",
        $"node select --id {nodeId}",
        $"node set --text \"{text}\"",
        $"review --id {nodeId} --as updated",
        $"context mark --id {scopeId}",
        "context mark --id purpose",
        "commit --bypass-ai-review",
        "exit") + Environment.NewLine;

    private static async Task<CliResult> Run(string[] arguments, string input = "")
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await CliRunner.RunAsync(arguments, new StringReader(input), output, error);
        return new CliResult(exitCode, output.ToString(), error.ToString());
    }

    private sealed record CliResult(int ExitCode, string Output, string Error);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"ValidatedWorld.Merge.Cli.Tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
