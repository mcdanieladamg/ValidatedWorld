using ModelContextProtocol.Client;
using ValidatedWorld.Application;
using ValidatedWorld.Mcp;
using ValidatedWorld.Persistence.Sqlite;

namespace ValidatedWorld.Cli.Tests;

public sealed class McpClientSmokeTests
{
    [Fact]
    public async Task Official_stdio_client_can_discover_and_read_an_existing_project()
    {
        using var temporary = new TemporaryDirectory();
        var project = Path.Combine(temporary.Path, "agent-host project.vw.db");
        new ProjectApplication(new SqliteProjectStore()).CreateSample("technical-project", project);

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "ValidatedWorld MCP smoke test",
            Command = "dotnet",
            Arguments = [typeof(McpAssembly).Assembly.Location, "--project", project],
        });
        await using var client = await McpClient.CreateAsync(transport);

        var tools = await client.ListToolsAsync();
        Assert.Contains(tools, tool => tool.Name == "project_status");
        var readTool = Assert.Single(tools, tool => tool.Name == "read_node");
        var result = await readTool.CallAsync(new Dictionary<string, object?>
        {
            ["nodeId"] = "purpose",
        });

        Assert.False(result.IsError ?? false);
        Assert.NotNull(result.StructuredContent);
        Assert.Contains("purpose", result.StructuredContent!.ToString(), StringComparison.Ordinal);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ValidatedWorld.Mcp.Client.Tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
