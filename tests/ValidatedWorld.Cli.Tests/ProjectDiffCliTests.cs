using System.Text.Json;
using System.Text.Json.Nodes;
using ValidatedWorld.Cli;

namespace ValidatedWorld.Cli.Tests;

public sealed class ProjectDiffCliTests
{
    [Fact]
    public async Task Cli_and_ndjson_diff_real_verified_databases_without_modifying_them()
    {
        using var workspace = new TemporaryDirectory();
        var basePath = Path.Combine(workspace.Path, "base project.vw.db");
        var targetPath = Path.Combine(workspace.Path, "target project.vw.db");
        var otherPath = Path.Combine(workspace.Path, "other project.vw.db");
        var invalidPath = Path.Combine(workspace.Path, "invalid project.vw.db");

        Assert.Equal(0, (await Run(["sample", "create", "technical-project", basePath])).ExitCode);
        Assert.Equal(0, (await Run(["sample", "create", "technical-project", targetPath])).ExitCode);
        Assert.Equal(0, (await Run([
            "project", "init", otherPath, "other-project", "Other project", "purpose", "Other purpose",
        ])).ExitCode);
        File.WriteAllText(invalidPath, "This is not a SQLite database.");

        var shellInput = string.Join(Environment.NewLine,
            "begin --author tester --intent \"Create a meaningful diff\"",
            "node select --id battery-assumption",
            "node set --text \"The battery lasts for the changed target duty cycle\"",
            "node select --id runtime-test",
            "node set --text \"Runtime verification covers the changed target duty cycle\"",
            "review --id battery-assumption --as updated",
            "review --id runtime-test --as updated",
            "review --id power-design-anchor --as reviewed-no-change",
            "context mark --id purpose",
            "context mark --id scope-power",
            "validate",
            "commit --bypass-ai-review",
            "exit") + Environment.NewLine;
        var changed = await Run(["shell", targetPath], shellInput);
        Assert.Equal(0, changed.ExitCode);
        Assert.Contains("Commit written", changed.Output, StringComparison.Ordinal);

        var baseBytes = File.ReadAllBytes(basePath);
        var targetBytes = File.ReadAllBytes(targetPath);
        var help = await Run(["project", "--help"]);
        Assert.Contains("project diff <base-database> <target-database>", help.Output, StringComparison.Ordinal);

        var first = await Run(["project", "diff", basePath, targetPath, "--limit", "1"]);
        Assert.Equal(CliRunner.SuccessExitCode, first.ExitCode);
        Assert.Equal(string.Empty, first.Error);
        var firstJson = JsonNode.Parse(first.Output)!;
        Assert.Equal("technical-project", firstJson["projectId"]!.GetValue<string>());
        Assert.NotEqual(
            firstJson["baseFingerprint"]!.GetValue<string>(),
            firstJson["targetFingerprint"]!.GetValue<string>());
        Assert.Empty(firstJson["metadataChanges"]!.AsArray());
        Assert.Equal(2, firstJson["summary"]!["nodesReplaced"]!.GetValue<int>());
        Assert.Equal(2, firstJson["summary"]!["totalChanges"]!.GetValue<int>());
        Assert.Equal(2, firstJson["totalCount"]!.GetValue<int>());
        Assert.Equal("battery-assumption", firstJson["items"]![0]!["entityId"]!.GetValue<string>());
        Assert.Equal("replace", firstJson["items"]![0]!["kind"]!.GetValue<string>());
        Assert.Equal(["text"], firstJson["items"]![0]!["changedFields"]!.AsArray()
            .Select(value => value!.GetValue<string>()));
        var cursor = firstJson["nextCursor"]!.GetValue<string>();

        var second = await Run(["project", "diff", basePath, targetPath, "--limit", "1", "--cursor", cursor]);
        var secondJson = JsonNode.Parse(second.Output)!;
        Assert.Equal("runtime-test", secondJson["items"]![0]!["entityId"]!.GetValue<string>());
        Assert.Null(secondJson["nextCursor"]);
        Assert.Null(secondJson["omission"]);
        Assert.Equal(firstJson["summary"]!.ToJsonString(), secondJson["summary"]!.ToJsonString());

        var reversed = JsonNode.Parse((await Run([
            "project", "diff", targetPath, basePath, "--limit", "10",
        ])).Output)!;
        var reversedBattery = reversed["items"]!.AsArray().Single(item =>
            item!["entityId"]!.GetValue<string>() == "battery-assumption")!;
        Assert.Equal(
            "The battery lasts for the changed target duty cycle",
            reversedBattery["oldNode"]!["text"]!.GetValue<string>());
        Assert.Equal(
            "The battery lasts for the target duty cycle",
            reversedBattery["newNode"]!["text"]!.GetValue<string>());

        var identical = JsonNode.Parse((await Run([
            "project", "diff", basePath, basePath,
        ])).Output)!;
        Assert.Equal(0, identical["summary"]!["totalChanges"]!.GetValue<int>());
        Assert.Empty(identical["items"]!.AsArray());

        var ndjsonLine = JsonSerializer.Serialize(new
        {
            version = 1,
            command = "project.diff",
            payload = new { basePath, targetPath, limit = 10 },
        }) + Environment.NewLine;
        var ndjson = await Run(["ndjson"], ndjsonLine);
        Assert.Equal(CliRunner.SuccessExitCode, ndjson.ExitCode);
        var envelope = JsonNode.Parse(ndjson.Output)!;
        Assert.Equal("project.diff", envelope["command"]!.GetValue<string>());
        Assert.Equal("ok", envelope["status"]!.GetValue<string>());
        Assert.Equal(2, envelope["payload"]!["totalCount"]!.GetValue<int>());

        var staleCursor = await Run([
            "project", "diff", basePath, targetPath, "--limit", "2", "--cursor", cursor,
        ]);
        Assert.Equal(CliRunner.DomainErrorExitCode, staleCursor.ExitCode);
        Assert.Contains("query-invalid-cursor", staleCursor.Error, StringComparison.Ordinal);

        var mismatch = await Run(["project", "diff", basePath, otherPath]);
        Assert.Equal(CliRunner.DomainErrorExitCode, mismatch.ExitCode);
        Assert.Contains("query-project-mismatch", mismatch.Error, StringComparison.Ordinal);

        var missing = await Run([
            "project", "diff", basePath, Path.Combine(workspace.Path, "missing.vw.db"),
        ]);
        Assert.Equal(CliRunner.DomainErrorExitCode, missing.ExitCode);
        Assert.Contains("storage-project-not-found", missing.Error, StringComparison.Ordinal);

        var invalid = await Run(["project", "diff", basePath, invalidPath]);
        Assert.Equal(CliRunner.DomainErrorExitCode, invalid.ExitCode);
        Assert.Contains("storage-invalid-database", invalid.Error, StringComparison.Ordinal);

        Assert.Equal(baseBytes, File.ReadAllBytes(basePath));
        Assert.Equal(targetBytes, File.ReadAllBytes(targetPath));
    }

    [Fact]
    public async Task Ndjson_help_documents_the_diff_payload_and_rejects_unknown_fields()
    {
        var helpInput = JsonSerializer.Serialize(new
        {
            version = 1,
            command = "host.help",
            payload = new { },
        }) + Environment.NewLine;
        var help = JsonNode.Parse((await Run(["ndjson"], helpInput)).Output)!;
        Assert.Contains("project.diff", help["payload"]!["commands"]!.AsArray()
            .Select(item => item!.GetValue<string>()));
        Assert.Contains("diff {basePath,targetPath,limit?,cursor?}",
            help["payload"]!["payloads"]!["project"]!.GetValue<string>(), StringComparison.Ordinal);

        var invalidInput = "{\"version\":1,\"command\":\"project.diff\",\"payload\":" +
            "{\"basePath\":\"a\",\"targetPath\":\"b\",\"extra\":true}}" + Environment.NewLine;
        var invalid = JsonNode.Parse((await Run(["ndjson"], invalidInput)).Output)!;
        Assert.Equal("error", invalid["status"]!.GetValue<string>());
        Assert.Equal("malformed-json", invalid["payload"]!["code"]!.GetValue<string>());
    }

    private static async Task<CliResult> Run(string[] arguments, string input = "")
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await CliRunner.RunAsync(
            arguments,
            new StringReader(input),
            output,
            error);
        return new CliResult(exitCode, output.ToString(), error.ToString());
    }

    private sealed record CliResult(int ExitCode, string Output, string Error);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"ValidatedWorld.Diff.Cli.Tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
