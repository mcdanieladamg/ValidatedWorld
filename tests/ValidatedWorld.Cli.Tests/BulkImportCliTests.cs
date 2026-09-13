using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ValidatedWorld.Application;
using ValidatedWorld.Cli;
using ValidatedWorld.Core;
using ValidatedWorld.Serialization;

namespace ValidatedWorld.Cli.Tests;

public sealed class BulkImportCliTests
{
    [Fact]
    public async Task Bulk_plan_is_bounded_resumable_and_available_through_ndjson()
    {
        using var workspace = new TemporaryDirectory();
        var projectPath = Path.Combine(workspace.Path, "project.vw.db");
        var manifestPath = Path.Combine(workspace.Path, "import.jsonl");
        var created = await Run(["sample", "create", "technical-project", projectPath]);
        Assert.Equal(CliRunner.SuccessExitCode, created.ExitCode);
        var createdJson = JsonNode.Parse(created.Output)!;
        WriteManifest(manifestPath, createdJson["stateFingerprint"]!.GetValue<string>(), [
            GraphOperation.AddNode(new GraphNode(new EntityId("bulk-scope"), "Bulk scope", "scope")),
            GraphOperation.AddEdge(new GraphEdge(
                new EntityId("bulk-scope-parent"), new EntityId("bulk-scope"),
                new EntityId("purpose"), "scope-parent", ReviewDirection.None)),
            GraphOperation.AddNode(new GraphNode(new EntityId("bulk-note"), "Bulk note", "note")),
            GraphOperation.AddEdge(new GraphEdge(
                new EntityId("bulk-note-parent"), new EntityId("bulk-note"),
                new EntityId("bulk-scope"), "scope-parent", ReviewDirection.None)),
        ]);

        var help = await Run(["project", "--help"]);
        Assert.Contains("project bulk-plan <database> <jsonl-manifest>", help.Output, StringComparison.Ordinal);

        var first = await Run(["project", "bulk-plan", projectPath, manifestPath, "--chunk-size", "2"]);
        Assert.Equal(CliRunner.SuccessExitCode, first.ExitCode);
        var firstJson = JsonNode.Parse(first.Output)!;
        Assert.Equal(4, firstJson["operationCount"]!.GetValue<int>());
        Assert.Equal(2, firstJson["chunkOperationCount"]!.GetValue<int>());
        Assert.NotNull(firstJson["nextCursor"]);
        Assert.Equal(firstJson["baseFingerprint"]!.GetValue<string>(),
            createdJson["stateFingerprint"]!.GetValue<string>());

        var ndjsonInput = JsonSerializer.Serialize(new
        {
            version = 1,
            command = "project.bulk_plan",
            payload = new { path = projectPath, manifestPath, chunkSize = 2, cursor = firstJson["nextCursor"]!.GetValue<string>() },
        }) + Environment.NewLine;
        var ndjson = await Run(["ndjson"], ndjsonInput);
        Assert.Equal(CliRunner.SuccessExitCode, ndjson.ExitCode);
        var ndjsonJson = JsonNode.Parse(ndjson.Output)!;
        Assert.Equal("ok", ndjsonJson["status"]!.GetValue<string>());
        Assert.Equal(1, ndjsonJson["payload"]!["chunkIndex"]!.GetValue<int>());
        Assert.Null(ndjsonJson["payload"]!["nextCursor"]);
    }

    private static void WriteManifest(string path, string baseFingerprint, IEnumerable<GraphOperation> operations)
    {
        var options = Protocol.CreateJsonOptions();
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        using var writer = new StreamWriter(path, append: false);
        writer.WriteLine(JsonSerializer.Serialize(new BulkImportManifestHeader(
            BulkImportContract.Version,
            BulkImportContract.Format,
            SampleProjectCatalog.TechnicalProject,
            baseFingerprint,
            "Import a bounded CLI graph."), options));
        foreach (var operation in operations)
            writer.WriteLine(JsonSerializer.Serialize(GraphProtocol.ToDto(operation), options));
    }

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
                System.IO.Path.GetTempPath(), $"ValidatedWorld.Bulk.Cli.Tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
