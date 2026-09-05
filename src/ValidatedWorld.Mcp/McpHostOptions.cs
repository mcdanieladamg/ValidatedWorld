namespace ValidatedWorld.Mcp;

internal sealed record McpHostOptions(string? DefaultProjectPath, bool ShowHelp)
{
    public static string HelpText => "ValidatedWorld MCP host\n\n" +
        "Usage: ValidatedWorld.Mcp [--project <path>]\n\n" +
        "Starts a local stdio MCP server. The optional project path is selected " +
        "as the default project for this process.";

    public static McpHostOptions Parse(IReadOnlyList<string> args)
    {
        string? path = null;
        var showHelp = false;
        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--help" or "-h":
                    showHelp = true;
                    break;
                case "--project" or "--default-project" when index + 1 < args.Count:
                    path = args[++index];
                    break;
                case "--project" or "--default-project":
                    throw new ArgumentException("A project path is required after --project.");
                default:
                    throw new ArgumentException($"Unknown MCP host argument '{args[index]}'.");
            }
        }

        return new McpHostOptions(path, showHelp);
    }
}
