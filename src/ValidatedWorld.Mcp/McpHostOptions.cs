namespace ValidatedWorld.Mcp;

internal sealed record McpHostOptions(
    string? DefaultProjectPath,
    IReadOnlyList<string> ArtifactAllowedRoots,
    bool ShowHelp,
    bool ShowVersion)
{
    public static string HelpText => "ValidatedWorld MCP host\n\n" +
        "Usage: ValidatedWorld.Mcp [--project <path>] [--artifact-root <directory> ...]\n" +
        "       ValidatedWorld.Mcp --version\n\n" +
        "Starts a local stdio MCP server. The optional project path is selected " +
        "as the default project for this process. Artifact reads are denied unless " +
        "one or more host-authorized roots are supplied.";

    public static McpHostOptions Parse(IReadOnlyList<string> args)
    {
        string? path = null;
        var artifactRoots = new List<string>();
        var showHelp = false;
        var showVersion = false;
        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--help" or "-h":
                    showHelp = true;
                    break;
                case "version" or "--version" or "-v":
                    showVersion = true;
                    break;
                case "--project" or "--default-project" when index + 1 < args.Count:
                    path = args[++index];
                    break;
                case "--project" or "--default-project":
                    throw new ArgumentException("A project path is required after --project.");
                case "--artifact-root" when index + 1 < args.Count:
                    artifactRoots.Add(args[++index]);
                    break;
                case "--artifact-root":
                    throw new ArgumentException("A directory path is required after --artifact-root.");
                default:
                    throw new ArgumentException($"Unknown MCP host argument '{args[index]}'.");
            }
        }

        if (showVersion && (showHelp || path is not null || artifactRoots.Count != 0 || args.Count != 1))
            throw new ArgumentException("The --version option cannot be combined with other arguments.");

        return new McpHostOptions(path, artifactRoots, showHelp, showVersion);
    }
}
