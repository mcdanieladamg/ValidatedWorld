using System.Text.Json.Nodes;

namespace ValidatedWorld.Cli.Tests;

[Collection("Live OpenAI")]
public sealed class LiveSemanticReviewTests
{
    [Fact]
    [Trait("Category", "LiveOpenAI")]
    public async Task Configured_live_reviewer_allows_control_and_blocks_project_purpose_contradiction()
    {
        var configuration = AiReviewConfiguration.Load();
        if (!configuration.LiveTests || !configuration.IsEffectivelyEnabled)
            return;

        using var workspace = new TemporaryWorkspace();
        var allowProject = Path.Combine(workspace.Path, "allow-control.vw.db");
        var blockProject = Path.Combine(workspace.Path, "block-known-case.vw.db");
        await CreateSample(allowProject);
        await CreateSample(blockProject);

        var allow = await RunShell(allowProject,
            "The battery lasts for the target duty cycle.",
            "Confirm an equivalent punctuation-only battery statement");
        Assert.Equal(CliRunner.SuccessExitCode, allow.ExitCode);
        AssertContainsResult(allow, "AI review: complete/allow");
        Assert.Contains("Commit written", allow.Output, StringComparison.Ordinal);

        var requestLogPath = Path.Combine(
            Directory.GetCurrentDirectory(), "artifacts", "ai-review-live-request.json");
        Assert.True(File.Exists(requestLogPath), "The credential-free live request log was not written.");
        var requestLog = await File.ReadAllTextAsync(requestLogPath);
        var outbound = JsonNode.Parse(requestLog)!;
        Assert.True(outbound["background"]!.GetValue<bool>());
        Assert.Equal("none", outbound["tool_choice"]!.GetValue<string>());
        Assert.Contains("battery-assumption", outbound["input"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("runtime-test", outbound["input"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Null(outbound["authorization"]);
        Assert.Null(outbound["apiKey"]);

        var block = await RunShell(blockProject,
            "The sensor requires a continuous public-cloud connection and uploads every reading off-device.",
            "Introduce a direct contradiction of the offline privacy-preserving project purpose",
            discardAfterCommit: true);
        Assert.Equal(CliRunner.SuccessExitCode, block.ExitCode);
        AssertContainsResult(block, "AI review: complete/block");
        Assert.Contains("Commit semanticreviewblocked", block.Output, StringComparison.Ordinal);
        Assert.Contains("Discarded session", block.Output, StringComparison.Ordinal);
    }

    private static async Task CreateSample(string path)
    {
        var result = await Run(["sample", "create", "technical-project", path]);
        Assert.Equal(CliRunner.SuccessExitCode, result.ExitCode);
    }

    private static Task<CliResult> RunShell(
        string path,
        string batteryText,
        string intent,
        bool discardAfterCommit = false)
    {
        var commands = new List<string>
        {
            $"begin --author \"T12 live test\" --intent \"{intent}\"",
            "cd battery-assumption",
            $"node set --text \"{batteryText}\"",
            "review --id battery-assumption --as updated",
            "review --id runtime-test --as reviewed-no-change",
            "review --id power-design-anchor --as reviewed-no-change",
            "context mark --id purpose",
            "context mark --id scope-power",
            "validate",
            "commit",
        };
        if (discardAfterCommit) commands.Add("discard");
        commands.Add("exit");
        return Run(["shell", path], string.Join(Environment.NewLine, commands) + Environment.NewLine);
    }

    private static async Task<CliResult> Run(string[] arguments, string input = "")
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var exitCode = await CliRunner.RunAsync(
            arguments,
            new StringReader(input),
            output,
            error,
            timeout.Token);
        return new CliResult(exitCode, output.ToString(), error.ToString());
    }

    private static void AssertContainsResult(CliResult result, string expected)
    {
        Assert.True(
            result.Output.Contains(expected, StringComparison.Ordinal),
            $"Expected '{expected}'.{Environment.NewLine}stdout:{Environment.NewLine}{result.Output}" +
            $"{Environment.NewLine}stderr:{Environment.NewLine}{result.Error}");
    }

    private sealed record CliResult(int ExitCode, string Output, string Error);

    private sealed class TemporaryWorkspace : IDisposable
    {
        public TemporaryWorkspace()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"ValidatedWorld.T12.Live-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
