using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using ValidatedWorld.Application;
using ValidatedWorld.Core;
using ValidatedWorld.Serialization;

namespace ValidatedWorld.Mcp;

internal sealed record McpProjectSelection(
    string Path,
    string ProjectId,
    string Title,
    string PurposeNodeId,
    int NodeCount,
    int EdgeCount,
    string StateFingerprint,
    int SchemaVersion,
    string SqliteVersion);

internal sealed record McpProjectSelectionResult(
    bool Selected,
    McpProjectSelection? Project,
    string Message);

internal sealed record McpProjectInitializationResult(
    McpProjectSelection Project,
    string Message);

internal sealed record McpReadResult<T>(
    T? Item,
    bool Complete,
    McpOmission? Omission);

internal sealed record McpBoundedResult<T>(
    T? Data,
    bool Complete,
    McpOmission? Omission);

internal sealed record McpPage<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    string? NextCursor,
    McpOmission? Omission);

internal sealed record McpSearchHit(
    string EntityKind,
    string EntityId,
    NodeDto? Node,
    EdgeDto? Edge);

internal sealed record McpOmission(
    string Reason,
    int? RemainingCount,
    string Message);

internal sealed record McpScopeResult(
    NodeDto Node,
    IReadOnlyList<NodeDto> Upstream,
    McpPage<NodeDto> Descendants,
    IReadOnlyList<McpOmission> Omissions);

internal sealed record McpPathResult(
    bool Found,
    IReadOnlyList<string> Nodes,
    IReadOnlyList<string> Edges,
    IReadOnlyList<McpOmission> Omissions);

internal sealed record McpContextResult(
    IReadOnlyList<string> RequestedNodeIds,
    IReadOnlyList<NodeDto> ContextNodes,
    IReadOnlyList<McpOmission> Omissions);

internal sealed record McpReportSection<T>(
    int TotalCount,
    IReadOnlyList<T> Items,
    int OmittedCount);

internal sealed record McpReviewFanOutHotspot(
    string NodeId,
    int OutgoingReviewArcCount,
    int IncomingReviewArcCount);

internal sealed record McpIsolatedClaim(string NodeId, string? Kind);

internal sealed record McpMissingRationale(
    string EdgeId,
    string Source,
    string Target,
    string Relationship);

internal sealed record McpTagUsage(string Tag, int NodeCount, int EdgeCount, int TotalCount);

internal sealed record McpHealthResult(
    int NodeCount,
    int EdgeCount,
    int SemanticReviewArcCount,
    object ScopeCoverage,
    McpReportSection<string> UnreachableNodeIds,
    McpReportSection<McpReviewFanOutHotspot> ReviewFanOutHotspots,
    McpReportSection<McpIsolatedClaim> SuspiciouslyIsolatedClaims,
    McpReportSection<McpMissingRationale> MissingRationales,
    McpReportSection<McpTagUsage> TagUsage,
    int UntaggedNodeCount,
    int UntaggedEdgeCount,
    bool WasCancelled,
    IReadOnlyList<McpOmission> Omissions);

internal sealed class McpProjectService(ProjectApplication application, McpHostOptions hostOptions)
{
    private const int MaximumPathLength = 4_096;
    private const int MaximumOutputBytes = 512 * 1_024;
    private readonly object _gate = new();
    private McpProjectSelection? _selection;
    private bool _defaultWasAttempted;

    public McpProjectSelectionResult Select(string path)
    {
        var normalized = ProjectPathPolicy.Existing(path);
        var selected = ToSelection(application.Status(normalized));
        lock (_gate) _selection = selected;
        return new McpProjectSelectionResult(true, selected, "The project is selected for this MCP session.");
    }

    public McpProjectInitializationResult Initialize(
        string path,
        string projectId,
        string title,
        string purposeNodeId,
        string purposeText)
    {
        var normalized = ProjectPathPolicy.New(path);
        var created = application.Initialize(
            normalized,
            new ProjectId(projectId),
            title,
            new EntityId(purposeNodeId),
            purposeText);
        var selected = ToSelection(application.Status(created.Path));
        lock (_gate) _selection = selected;
        return new McpProjectInitializationResult(
            selected,
            "The purpose-only project was initialized and selected. Add graph content through a reviewed change session in the CLI or NDJSON interface.");
    }

    public McpProjectSelection Status()
    {
        EnsureDefaultSelected();
        return Selection();
    }

    public ProjectQueries Queries()
    {
        EnsureDefaultSelected();
        var selected = Selection();
        return application.Queries(selected.Path, new ProjectId(selected.ProjectId));
    }

    private McpProjectSelection Selection()
    {
        lock (_gate)
        {
            return _selection ?? throw new InvalidOperationException(
                "No project is selected. Call select_project with an existing .vw.db path, or initialize_project first.");
        }
    }

    private void EnsureDefaultSelected()
    {
        string? path;
        lock (_gate)
        {
            if (_selection is not null || _defaultWasAttempted) return;
            _defaultWasAttempted = true;
            path = hostOptions.DefaultProjectPath;
        }

        if (path is not null) Select(path);
    }

    private static McpProjectSelection ToSelection(ProjectStatus status) => new(
        status.Path,
        status.ProjectId.Value,
        status.Title,
        status.PurposeNodeId.Value,
        status.NodeCount,
        status.EdgeCount,
        status.StateFingerprint,
        status.SchemaVersion,
        status.SqliteVersion);

    public static NodeDto Node(GraphNode node) => GraphProtocol.ToDto(node);

    public static EdgeDto Edge(GraphEdge edge) => GraphProtocol.ToDto(edge);

    public static McpPage<TOut> ProjectPage<TIn, TOut>(QueryPage<TIn> page, Func<TIn, TOut> projection)
    {
        var items = page.Items.Select(projection).ToArray();
        var result = new McpPage<TOut>(items, page.TotalCount, page.NextCursor, Omission(page.Omission));
        return Fits(result)
            ? result
            : new McpPage<TOut>([], page.TotalCount, null, new McpOmission(
                "output-byte-limit", page.TotalCount,
                $"The result exceeded the {MaximumOutputBytes} byte MCP output bound; request a narrower query."));
    }

    public static McpReadResult<T> Read<T>(T item)
    {
        var result = new McpReadResult<T>(item, true, null);
        return Fits(result)
            ? result
            : new McpReadResult<T>(default, false, new McpOmission(
                "output-byte-limit", null,
                $"The result exceeded the {MaximumOutputBytes} byte MCP output bound; request a narrower query."));
    }

    public static object Bound<T>(T item)
    {
        if (Fits(item)) return item!;
        return new McpBoundedResult<T>(default, false, new McpOmission(
            "output-byte-limit", null,
            $"The result exceeded the {MaximumOutputBytes} byte MCP output bound; request a narrower query."));
    }

    public static McpOmission? Omission(QueryOmission? omission) => omission is null
        ? null
        : new McpOmission(omission.Reason.ToString(), omission.RemainingCount, omission.Message);

    public static IReadOnlyList<McpOmission> Omissions(IEnumerable<QueryOmission> omissions) =>
        omissions.Select(omission => new McpOmission(
            omission.Reason.ToString(), omission.RemainingCount, omission.Message)).ToArray();

    private static bool Fits<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, Protocol.CreateJsonOptions()).Length <= MaximumOutputBytes;
}

internal static class ProjectPathPolicy
{
    public static string Existing(string path)
    {
        var normalized = Normalize(path);
        if (!File.Exists(normalized))
            throw new FileNotFoundException($"The selected project file does not exist: '{normalized}'.", normalized);

        var info = new FileInfo(normalized);
        if ((info.Attributes & FileAttributes.Directory) != 0)
            throw new ArgumentException("The selected project path must be a file.", nameof(path));

        var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
        return resolved is null ? normalized : Normalize(resolved.FullName);
    }

    public static string New(string path)
    {
        var normalized = Normalize(path);
        if (File.Exists(normalized) || Directory.Exists(normalized))
            throw new IOException($"The project destination already exists: '{normalized}'.");
        return normalized;
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4_096 || path.Any(char.IsControl))
            throw new ArgumentException("A project path must be non-empty, bounded, and free of control characters.", nameof(path));

        var fullPath = Path.GetFullPath(path);
        if (Path.GetPathRoot(fullPath) is null ||
            !fullPath.EndsWith(".vw.db", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A project path must be rooted after normalization and end with '.vw.db'.", nameof(path));

        var installPath = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (fullPath.StartsWith(installPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            StringComparer.OrdinalIgnoreCase.Equals(fullPath, installPath))
            throw new ArgumentException("Project data must be stored outside the MCP host installation directory.", nameof(path));

        return fullPath;
    }
}
