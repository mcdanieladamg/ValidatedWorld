using ValidatedWorld.Application;
using ValidatedWorld.Core;
using ValidatedWorld.Persistence.Sqlite;

return Run(args);

static int Run(string[] arguments)
{
    if (arguments.Length == 0 || IsHelp(arguments[0]))
    {
        PrintHelp();
        return 0;
    }

    var application = new ProjectApplication(new SqliteProjectStore());
    try
    {
        return arguments[0] switch
        {
            "project" => RunProject(application, arguments),
            "sample" => RunSample(application, arguments),
            _ => UsageError($"Unknown command group '{arguments[0]}'."),
        };
    }
    catch (ProjectStorageException exception)
    {
        Console.Error.WriteLine($"error[{exception.Code}]: {exception.Message}");
        return 2;
    }
    catch (ArgumentException exception)
    {
        Console.Error.WriteLine($"error[invalid-argument]: {exception.Message}");
        return 1;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"error[unexpected]: {exception.Message}");
        return 3;
    }
}

static int RunProject(ProjectApplication application, string[] arguments)
{
    if (arguments.Length < 2 || IsHelp(arguments[1]))
    {
        PrintProjectHelp();
        return 0;
    }

    return arguments[1] switch
    {
        "init" when arguments.Length == 7 => Initialize(application, arguments),
        "status" when arguments.Length == 3 => Status(application, arguments[2]),
        "verify" when arguments.Length == 3 => Verify(application, arguments[2]),
        "backup" when arguments.Length == 4 => Backup(application, arguments[2], arguments[3]),
        "init" or "status" or "verify" or "backup" =>
            UsageError($"Incorrect arguments for 'project {arguments[1]}'."),
        _ => UsageError($"Unknown project command '{arguments[1]}'."),
    };
}

static int RunSample(ProjectApplication application, string[] arguments)
{
    if (arguments.Length < 2 || IsHelp(arguments[1]))
    {
        PrintSampleHelp();
        return 0;
    }

    return arguments[1] switch
    {
        "list" when arguments.Length == 2 => ListSamples(),
        "create" when arguments.Length == 4 => CreateSample(application, arguments[2], arguments[3]),
        "list" or "create" => UsageError($"Incorrect arguments for 'sample {arguments[1]}'."),
        _ => UsageError($"Unknown sample command '{arguments[1]}'."),
    };
}

static int Initialize(ProjectApplication application, string[] arguments)
{
    var purpose = new GraphNode(new EntityId(arguments[5]), arguments[6]);
    var graph = new ProjectGraph(
        new ProjectId(arguments[3]),
        arguments[4],
        purpose.Id,
        [purpose],
        []);
    var stored = application.Initialize(arguments[2], graph);
    PrintStored("initialized", stored);
    return 0;
}

static int Status(ProjectApplication application, string path)
{
    var status = application.Status(path);
    Console.WriteLine("status=valid");
    Console.WriteLine($"path={status.Path}");
    Console.WriteLine($"projectId={status.ProjectId.Value}");
    Console.WriteLine($"title={status.Title}");
    Console.WriteLine($"purposeNodeId={status.PurposeNodeId.Value}");
    Console.WriteLine($"nodes={status.NodeCount}");
    Console.WriteLine($"edges={status.EdgeCount}");
    Console.WriteLine($"stateFingerprint={status.StateFingerprint}");
    Console.WriteLine($"schemaVersion={status.SchemaVersion}");
    Console.WriteLine($"sqliteVersion={status.SqliteVersion}");
    return 0;
}

static int Verify(ProjectApplication application, string path)
{
    var verification = application.Verify(path);
    Console.WriteLine($"verified={verification.IsValid.ToString().ToLowerInvariant()}");
    Console.WriteLine($"path={verification.Path}");
    Console.WriteLine($"nodes={verification.NodeCount}");
    Console.WriteLine($"edges={verification.EdgeCount}");
    Console.WriteLine($"stateFingerprint={verification.StateFingerprint}");
    Console.WriteLine($"checks={string.Join(',', verification.Checks)}");
    return 0;
}

static int Backup(ProjectApplication application, string sourcePath, string destinationPath)
{
    var stored = application.Backup(sourcePath, destinationPath);
    PrintStored("backed-up", stored);
    return 0;
}

static int ListSamples()
{
    foreach (var name in SampleProjectCatalog.Names)
    {
        Console.WriteLine(name);
    }

    return 0;
}

static int CreateSample(ProjectApplication application, string sampleName, string path)
{
    var stored = application.CreateSample(sampleName, path);
    PrintStored("sample-created", stored);
    return 0;
}

static void PrintStored(string outcome, StoredProject stored)
{
    Console.WriteLine($"outcome={outcome}");
    Console.WriteLine($"path={stored.Path}");
    Console.WriteLine($"projectId={stored.Graph.ProjectId.Value}");
    Console.WriteLine($"nodes={stored.Graph.Nodes.Count}");
    Console.WriteLine($"edges={stored.Graph.Edges.Count}");
    Console.WriteLine($"stateFingerprint={stored.StateFingerprint}");
}

static int UsageError(string message)
{
    Console.Error.WriteLine($"error[usage]: {message}");
    Console.Error.WriteLine("Run 'ValidatedWorld.Cli --help' for usage.");
    return 1;
}

static bool IsHelp(string value) => value is "help" or "--help" or "-h";

static void PrintHelp()
{
    Console.WriteLine("ValidatedWorld - local semantic graph change control");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  project init     Create a one-purpose-node .vw.db project");
    Console.WriteLine("  project status   Load and summarize a .vw.db project");
    Console.WriteLine("  project verify   Run schema, integrity, graph, and fingerprint checks");
    Console.WriteLine("  project backup   Create a verified SQLite online backup");
    Console.WriteLine("  sample list      List built-in disposable samples");
    Console.WriteLine("  sample create    Create a built-in sample as a new .vw.db project");
    Console.WriteLine();
    Console.WriteLine("Run 'ValidatedWorld.Cli project --help' or 'ValidatedWorld.Cli sample --help'.");
}

static void PrintProjectHelp()
{
    Console.WriteLine("Project commands:");
    Console.WriteLine("  project init <database> <project-id> <title> <purpose-id> <purpose-text>");
    Console.WriteLine("  project status <database>");
    Console.WriteLine("  project verify <database>");
    Console.WriteLine("  project backup <source-database> <new-destination-database>");
    Console.WriteLine();
    Console.WriteLine("Quote arguments that contain spaces. Existing destinations are never overwritten.");
}

static void PrintSampleHelp()
{
    Console.WriteLine("Sample commands:");
    Console.WriteLine("  sample list");
    Console.WriteLine("  sample create <sample-name> <new-database>");
}
