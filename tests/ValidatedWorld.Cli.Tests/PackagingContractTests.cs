using System.Text.Json.Nodes;

namespace ValidatedWorld.Cli.Tests;

public sealed class PackagingContractTests
{
    [Fact]
    public void Local_marketplace_plugin_has_real_relative_components_and_no_developer_path()
    {
        var root = RepositoryRoot();
        var marketplacePath = Path.Combine(root, "packaging", ".agents", "plugins", "marketplace.json");
        var pluginRoot = Path.Combine(root, "packaging", "plugins", "validated-world");
        var manifestPath = Path.Combine(pluginRoot, ".codex-plugin", "plugin.json");
        var mcpPath = Path.Combine(pluginRoot, ".mcp.json");

        var marketplace = JsonNode.Parse(File.ReadAllText(marketplacePath))!;
        Assert.Equal("validated-world-local", marketplace["name"]!.GetValue<string>());
        var entry = Assert.Single(marketplace["plugins"]!.AsArray());
        Assert.Equal("validated-world", entry!["name"]!.GetValue<string>());
        Assert.Equal("local", entry["source"]!["source"]!.GetValue<string>());
        Assert.Equal("./plugins/validated-world", entry["source"]!["path"]!.GetValue<string>());

        var manifestText = File.ReadAllText(manifestPath);
        var manifest = JsonNode.Parse(manifestText)!;
        Assert.Equal("validated-world", manifest["name"]!.GetValue<string>());
        Assert.Matches(@"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$", manifest["version"]!.GetValue<string>());
        Assert.Equal("./skills/", manifest["skills"]!.GetValue<string>());
        Assert.Equal("./.mcp.json", manifest["mcpServers"]!.GetValue<string>());
        Assert.DoesNotContain(@"D:\", manifestText, StringComparison.OrdinalIgnoreCase);

        var mcpText = File.ReadAllText(mcpPath);
        var mcp = JsonNode.Parse(mcpText)!["mcpServers"]!["validated_world"]!;
        Assert.Equal("cmd.exe", mcp["command"]!.GetValue<string>());
        Assert.Equal(".", mcp["cwd"]!.GetValue<string>());
        Assert.Contains(mcp["args"]!.AsArray(), argument =>
            argument!.GetValue<string>() == "./scripts/launch-mcp.cmd");
        Assert.DoesNotContain(@"D:\", mcpText, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(pluginRoot, "scripts", "launch-mcp.cmd")));
    }

    [Fact]
    public void Workflow_skill_and_release_automation_define_complete_distribution()
    {
        var root = RepositoryRoot();
        var skill = File.ReadAllText(Path.Combine(
            root, "packaging", "plugins", "validated-world", "skills", "validated-world", "SKILL.md"));
        Assert.DoesNotContain("TODO", skill, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ranked_search", skill, StringComparison.Ordinal);
        Assert.Contains("proposal_preview", skill, StringComparison.Ordinal);
        Assert.Contains("request_approval", skill, StringComparison.Ordinal);
        Assert.Contains("software example", skill, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("novel or research folder", skill, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Git project", skill, StringComparison.Ordinal);
        Assert.Contains("non-Git folders", skill, StringComparison.Ordinal);
        Assert.Contains("normal review process", skill, StringComparison.OrdinalIgnoreCase);

        var releaseScript = File.ReadAllText(Path.Combine(root, "eng", "Build-Release.ps1"));
        Assert.Contains("--self-contained", releaseScript, StringComparison.Ordinal);
        Assert.Contains("PublishSingleFile=true", releaseScript, StringComparison.Ordinal);
        Assert.Contains("IncludeNativeLibrariesForSelfExtract=true", releaseScript, StringComparison.Ordinal);
        Assert.Contains("SHA256SUMS.txt", releaseScript, StringComparison.Ordinal);
        Assert.Contains("validated-world-cli-", releaseScript, StringComparison.Ordinal);
        Assert.Contains("validated-world-plugin-", releaseScript, StringComparison.Ordinal);

        var installGuide = File.ReadAllText(Path.Combine(root, "packaging", "PLUGIN_INSTALL.md"));
        Assert.Contains("outside", installGuide, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("codex plugin remove", installGuide, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ValidatedWorld.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
