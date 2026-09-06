using System.Reflection;

namespace ValidatedWorld.Mcp;

/// <summary>Public assembly marker for local host discovery and integration tests.</summary>
public static class McpAssembly
{
    public static string ProductVersion =>
        typeof(McpAssembly).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(McpAssembly).Assembly.GetName().Version?.ToString()
        ?? "unknown";
}
