using System.Globalization;
using System.Text.Json;
using ValidatedWorld.Application;
using ValidatedWorld.Core;
using ValidatedWorld.Persistence.Sqlite;
using ValidatedWorld.Serialization;
using ValidatedWorld.Validation;

namespace ValidatedWorld.Cli;

public static class CliRunner
{
    public const int SuccessExitCode = 0;
    public const int UsageExitCode = 1;
    public const int DomainErrorExitCode = 2;
    public const int UnexpectedErrorExitCode = 3;
    public const int BrokenPipeExitCode = 4;
    public const int CancelledExitCode = 130;

    public static async Task<int> RunAsync(
        string[] arguments,
        TextReader input,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        var application = new ProjectApplication(new SqliteProjectStore());
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (arguments.Length == 0 || IsHelp(arguments[0]))
            {
                await PrintHelp(output);
                return SuccessExitCode;
            }

            return arguments[0] switch
            {
                "project" => await RunProject(application, arguments, output),
                "read" => await RunRead(application, arguments, output, cancellationToken),
                "sample" => await RunSample(application, arguments, output),
                "ndjson" when arguments.Length == 2 && IsHelp(arguments[1]) => await PrintNdjsonHelp(output),
                "ndjson" when arguments.Length == 1 => await new NdjsonHost(
                    application, input, output, error, cancellationToken).RunAsync(),
                "ndjson" => await UsageError(error, "The 'ndjson' command accepts no arguments."),
                _ => await UsageError(error, $"Unknown command group '{arguments[0]}'."),
            };
        }
        catch (OperationCanceledException)
        {
            await TryWriteError(error, "error[cancelled]: The operation was cancelled.");
            return CancelledExitCode;
        }
        catch (IOException)
        {
            await TryWriteError(error, "error[broken-pipe]: The input or output stream was closed.");
            return BrokenPipeExitCode;
        }
        catch (Exception exception)
        {
            var (code, message, exitCode) = Error(exception);
            await TryWriteError(error, $"error[{code}]: {message}");
            return exitCode;
        }
    }

    private static async Task<int> RunProject(
        ProjectApplication application,
        string[] arguments,
        TextWriter output)
    {
        if (arguments.Length < 2 || IsHelp(arguments[1]))
        {
            await PrintProjectHelp(output);
            return SuccessExitCode;
        }

        object result;
        switch (arguments[1])
        {
            case "init" when arguments.Length == 7:
            {
                var purpose = new GraphNode(new EntityId(arguments[5]), arguments[6]);
                var graph = new ProjectGraph(
                    new ProjectId(arguments[3]), arguments[4], purpose.Id, [purpose], []);
                result = CliDto.Stored(application.Initialize(arguments[2], graph));
                break;
            }
            case "status" when arguments.Length == 3:
                result = CliDto.Status(application.Status(arguments[2]));
                break;
            case "open" when arguments.Length == 3:
            {
                var loaded = application.Load(arguments[2]);
                result = new LoadedProjectDto(CliDto.Stored(loaded), GraphProtocol.ToDto(loaded.Graph));
                break;
            }
            case "verify" when arguments.Length == 3:
                result = CliDto.Verification(application.Verify(arguments[2]));
                break;
            case "backup" when arguments.Length == 4:
                result = CliDto.Stored(application.Backup(arguments[2], arguments[3]));
                break;
            case "export-sql" when arguments.Length == 3:
                await output.WriteAsync(application.ExportSql(arguments[2]).Sql);
                return SuccessExitCode;
            case "init" or "open" or "status" or "verify" or "backup" or "export-sql":
                throw new CliUsageException($"Incorrect arguments for 'project {arguments[1]}'.");
            default:
                throw new CliUsageException($"Unknown project command '{arguments[1]}'.");
        }

        await WriteJson(output, result);
        return SuccessExitCode;
    }

    private static async Task<int> RunSample(
        ProjectApplication application,
        string[] arguments,
        TextWriter output)
    {
        if (arguments.Length < 2 || IsHelp(arguments[1]))
        {
            await PrintSampleHelp(output);
            return SuccessExitCode;
        }

        object result = arguments[1] switch
        {
            "list" when arguments.Length == 2 => SampleProjectCatalog.Names,
            "create" when arguments.Length == 4 =>
                CliDto.Stored(application.CreateSample(arguments[2], arguments[3])),
            "list" or "create" => throw new CliUsageException(
                $"Incorrect arguments for 'sample {arguments[1]}'."),
            _ => throw new CliUsageException($"Unknown sample command '{arguments[1]}'."),
        };
        await WriteJson(output, result);
        return SuccessExitCode;
    }

    private static async Task<int> RunRead(
        ProjectApplication application,
        string[] arguments,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (arguments.Length < 2 || IsHelp(arguments[1]))
        {
            await PrintReadHelp(output);
            return SuccessExitCode;
        }

        if (arguments.Length < 3)
        {
            throw new CliUsageException($"A database path is required for 'read {arguments[1]}'.");
        }

        var options = CliReadOptions.Parse(arguments, RequiredPositionals(arguments[1]));
        var queries = application.Queries(arguments[2]);
        var page = new QueryPageRequest(options.Limit, options.Cursor);
        var traversal = new QueryTraversalOptions
        {
            MaxDepth = options.MaxDepth,
            MaxVisitedNodes = options.MaxVisitedNodes,
            CancellationToken = cancellationToken,
        };
        object result = arguments[1] switch
        {
            "node" => GraphProtocol.ToDto(queries.GetNode(new EntityId(arguments[3]))),
            "edge" => GraphProtocol.ToDto(queries.GetEdge(new EntityId(arguments[3]))),
            "nodes" => CliDto.Nodes(queries.ListNodes(page)),
            "edges" => CliDto.Edges(queries.ListEdges(page)),
            "search" => CliDto.Search(queries.Search(arguments[3], page)),
            "tag" => CliDto.Search(queries.SearchByTag(arguments[3], page)),
            "scope" => CliDto.Scope(queries.GetScope(new EntityId(arguments[3]), page, traversal)),
            "neighbors" => CliDto.Neighbors(queries.GetNeighbors(new EntityId(arguments[3]), page)),
            "dependencies" => CliDto.Dependencies(queries.GetDependencies(new EntityId(arguments[3]), page)),
            "path" => CliDto.Path(queries.FindDependencyPath(
                new EntityId(arguments[3]), new EntityId(arguments[4]), traversal)),
            "context" => CliDto.Context(queries.GetContext(
                arguments[3].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(id => new EntityId(id)),
                traversal)),
            _ => throw new CliUsageException($"Unknown read command '{arguments[1]}'."),
        };
        await WriteJson(output, result);
        return SuccessExitCode;
    }

    private static int RequiredPositionals(string command) => command switch
    {
        "nodes" or "edges" => 3,
        "node" or "edge" or "search" or "tag" or "scope" or "neighbors" or "dependencies" or "context" => 4,
        "path" => 5,
        _ => throw new CliUsageException($"Unknown read command '{command}'."),
    };

    internal static (string Code, string Message, int ExitCode) Error(Exception exception) => exception switch
    {
        CliUsageException value => ("usage", value.Message, UsageExitCode),
        JsonException value => ("malformed-json", value.Message, UsageExitCode),
        ProjectStorageException value =>
            ($"storage-{Kebab(value.Code.ToString())}", value.Message, DomainErrorExitCode),
        ProjectQueryException value =>
            ($"query-{Kebab(value.Code.ToString())}", value.Message, DomainErrorExitCode),
        ChangeSessionException value =>
            ($"change-{Kebab(value.Code.ToString())}", value.Message, DomainErrorExitCode),
        GraphOperationException value =>
            ($"operation-{Kebab(value.Code)}", value.Message, DomainErrorExitCode),
        ArgumentException or FormatException or OverflowException =>
            ("invalid-argument", exception.Message, UsageExitCode),
        _ => ("unexpected", "An unexpected internal failure occurred.", UnexpectedErrorExitCode),
    };

    internal static string Kebab(string value)
    {
        var result = new System.Text.StringBuilder(value.Length + 4);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsUpper(character) && index > 0 && result[^1] != '-') result.Append('-');
            result.Append(char.ToLowerInvariant(character));
        }

        return result.ToString().Replace('_', '-');
    }

    private static async Task WriteJson(TextWriter output, object result) =>
        await output.WriteLineAsync(CliJson.Serialize(result));

    private static async Task<int> UsageError(TextWriter error, string message)
    {
        await error.WriteLineAsync($"error[usage]: {message}");
        await error.WriteLineAsync("Run 'ValidatedWorld.Cli --help' for usage.");
        return UsageExitCode;
    }

    private static async Task TryWriteError(TextWriter error, string message)
    {
        try
        {
            await error.WriteLineAsync(message);
        }
        catch (IOException)
        {
            // The original stream failure determines the exit code.
        }
    }

    private static bool IsHelp(string value) => value is "help" or "--help" or "-h";

    private static async Task PrintHelp(TextWriter output)
    {
        await output.WriteLineAsync("ValidatedWorld - local semantic graph change control");
        await output.WriteLineAsync();
        await output.WriteLineAsync("Commands:");
        await output.WriteLineAsync("  project   Initialize, inspect, verify, back up, or export a project");
        await output.WriteLineAsync("  read      Run bounded graph queries");
        await output.WriteLineAsync("  sample    List or create built-in disposable samples");
        await output.WriteLineAsync("  ndjson    Run the long-lived structured host for reads and change sessions");
        await output.WriteLineAsync();
        await output.WriteLineAsync("One-shot command results use JSON on stdout; errors and warnings use stderr.");
        await output.WriteLineAsync(
            "Run '<group> --help' for exact commands and 'ndjson' for the manual change workflow.");
    }

    private static async Task PrintProjectHelp(TextWriter output)
    {
        await output.WriteLineAsync("Project commands:");
        await output.WriteLineAsync("  project init <database> <project-id> <title> <purpose-id> <purpose-text>");
        await output.WriteLineAsync("  project open <database>");
        await output.WriteLineAsync("  project status <database>");
        await output.WriteLineAsync("  project verify <database>");
        await output.WriteLineAsync("  project backup <source-database> <new-destination-database>");
        await output.WriteLineAsync("  project export-sql <database>");
        await output.WriteLineAsync();
        await output.WriteLineAsync(
            "Quote arguments containing spaces. Existing database destinations are not overwritten.");
        await output.WriteLineAsync(
            "SQL export is deterministic UTF-8 text on stdout; redirect it to a new file if desired.");
    }

    private static async Task PrintSampleHelp(TextWriter output)
    {
        await output.WriteLineAsync("Sample commands:");
        await output.WriteLineAsync("  sample list");
        await output.WriteLineAsync("  sample create <sample-name> <new-database>");
    }

    private static async Task PrintReadHelp(TextWriter output)
    {
        await output.WriteLineAsync("Read commands:");
        await output.WriteLineAsync("  read node <database> <node-id>");
        await output.WriteLineAsync("  read edge <database> <edge-id>");
        await output.WriteLineAsync("  read nodes|edges <database> [--limit N] [--cursor TOKEN]");
        await output.WriteLineAsync("  read search <database> <text> [--limit N] [--cursor TOKEN]");
        await output.WriteLineAsync("  read tag <database> <exact-tag> [--limit N] [--cursor TOKEN]");
        await output.WriteLineAsync(
            "  read scope|neighbors|dependencies <database> <node-id> [page/traversal options]");
        await output.WriteLineAsync("  read path <database> <source-node-id> <target-node-id> [traversal options]");
        await output.WriteLineAsync("  read context <database> <node-id[,node-id...]> [traversal options]");
        await output.WriteLineAsync();
        await output.WriteLineAsync("Page options: --limit N --cursor TOKEN");
        await output.WriteLineAsync("Traversal options: --max-depth N --max-nodes N");
    }

    private static async Task<int> PrintNdjsonHelp(TextWriter output)
    {
        await output.WriteLineAsync("NDJSON host:");
        await output.WriteLineAsync("  ndjson");
        await output.WriteLineAsync();
        await output.WriteLineAsync("Send one versioned JSON object per line and read one result per line:");
        await output.WriteLineAsync("  {\"version\":1,\"command\":\"host.help\",\"payload\":{}}");
        await output.WriteLineAsync(
            "  {\"version\":1,\"command\":\"project.status\",\"payload\":{\"path\":\"project.vw.db\"}}");
        await output.WriteLineAsync();
        await output.WriteLineAsync("Use host.help for the complete command and payload-field catalog.");
        await output.WriteLineAsync(
            "Change responses return an exact reference containing all stale-state fingerprints.");
        await output.WriteLineAsync("Pass that complete reference to the next mutating change command.");
        await output.WriteLineAsync(
            "EOF, cancellation, or host.exit ends the process; unresolved sessions are lost with a stderr warning.");
        return SuccessExitCode;
    }

    private sealed class CliUsageException(string message) : Exception(message);

    private sealed record CliReadOptions(int Limit, string? Cursor, int MaxDepth, int MaxVisitedNodes)
    {
        public static CliReadOptions Parse(string[] arguments, int requiredPositionals)
        {
            if (arguments.Length < requiredPositionals)
                throw new CliUsageException($"Incorrect arguments for 'read {arguments[1]}'.");

            var limit = QueryPageRequest.DefaultLimit;
            string? cursor = null;
            var maxDepth = 10_000;
            var maxNodes = 100_000;
            var index = requiredPositionals;
            while (index < arguments.Length)
            {
                if (index + 1 >= arguments.Length)
                    throw new CliUsageException($"Option '{arguments[index]}' requires a value.");
                var value = arguments[index + 1];
                switch (arguments[index])
                {
                    case "--limit": limit = PositiveInt(value, "limit"); break;
                    case "--cursor": cursor = value; break;
                    case "--max-depth": maxDepth = PositiveInt(value, "max-depth"); break;
                    case "--max-nodes": maxNodes = PositiveInt(value, "max-nodes"); break;
                    default: throw new CliUsageException($"Unknown option '{arguments[index]}'.");
                }

                index += 2;
            }

            return new CliReadOptions(limit, cursor, maxDepth, maxNodes);
        }

        private static int PositiveInt(string value, string option)
        {
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
                throw new CliUsageException($"Option '--{option}' requires a positive integer.");
            return parsed;
        }
    }
}
