using System.Collections.ObjectModel;
using ValidatedWorld.Core;
using ValidatedWorld.Validation;

namespace ValidatedWorld.Application;

public enum ProjectStorageErrorCode
{
    InvalidPath,
    ProjectAlreadyExists,
    ProjectNotFound,
    BackupDestinationExists,
    InvalidDatabase,
    UnsupportedVersion,
    MigrationMismatch,
    SchemaMismatch,
    IntegrityFailure,
    MappingFailure,
    FingerprintMismatch,
    InvalidGraph,
    ResourceLimitExceeded,
    StorageFailure,
}

/// <summary>A stable failure reported by a project-storage implementation.</summary>
public sealed class ProjectStorageException : Exception
{
    public ProjectStorageException(ProjectStorageErrorCode code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public ProjectStorageErrorCode Code { get; }
}

public sealed record StoredProject(
    string Path,
    ProjectGraph Graph,
    string StateFingerprint,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

public sealed record ProjectStatus(
    string Path,
    ProjectId ProjectId,
    string Title,
    EntityId PurposeNodeId,
    int NodeCount,
    int EdgeCount,
    string StateFingerprint,
    int SchemaVersion,
    string SqliteVersion);

public sealed record ProjectVerification(
    string Path,
    bool IsValid,
    string StateFingerprint,
    int NodeCount,
    int EdgeCount,
    IReadOnlyList<string> Checks);

/// <summary>The immutable write request produced by one reviewed change session.</summary>
public sealed record ProjectWriteRequest(
    string Path,
    ProjectId ProjectId,
    string BaseFingerprint,
    GraphOperationBatch Operations,
    string ProposedFingerprint);

public enum ProjectWriteOutcome
{
    Written,
    Stale,
    Busy,
    Failed,
}

/// <summary>The storage result for an attempted atomic graph write.</summary>
public sealed record ProjectWriteResult(
    ProjectWriteOutcome Outcome,
    StoredProject? Project,
    ProjectStorageErrorCode? ErrorCode,
    string Message);

/// <summary>Persistence operations expressed in application/domain values.</summary>
public interface IProjectStore
{
    StoredProject Initialize(string path, ProjectGraph graph);

    StoredProject Load(string path);

    ProjectStatus GetStatus(string path);

    ProjectVerification Verify(string path);

    StoredProject Backup(string sourcePath, string destinationPath);

    ProjectWriteResult Write(ProjectWriteRequest request);
}

/// <summary>Public project use cases over the configured store.</summary>
public sealed partial class ProjectApplication
{
    private readonly IProjectStore _store;
    private readonly GraphValidator _validator;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<string> _sessionIdFactory;

    public ProjectApplication(
        IProjectStore store,
        GraphValidator? validator = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<string>? sessionIdFactory = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _validator = validator ?? new GraphValidator();
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _sessionIdFactory = sessionIdFactory ?? (() => Guid.NewGuid().ToString("N"));
    }

    public StoredProject Initialize(string path, ProjectGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        EnsureValidGraph(graph);
        return _store.Initialize(path, graph);
    }

    public StoredProject Load(string path) => _store.Load(path);

    public ProjectStatus Status(string path) => _store.GetStatus(path);

    public ProjectVerification Verify(string path) => _store.Verify(path);

    public StoredProject Backup(string sourcePath, string destinationPath) =>
        _store.Backup(sourcePath, destinationPath);

    public ProjectQueries Queries(string path, ProjectId? expectedProjectId = null)
    {
        var project = _store.Load(path);
        if (expectedProjectId is { } expected && project.Graph.ProjectId != expected)
        {
            throw new ProjectQueryException(
                ProjectQueryErrorCode.ProjectMismatch,
                $"Project '{project.Graph.ProjectId.Value}' does not match expected project '{expected.Value}'.");
        }

        return new ProjectQueries(project);
    }

    public StoredProject CreateSample(string sampleName, string path) =>
        Initialize(path, SampleProjectCatalog.Create(sampleName));

    private void EnsureValidGraph(ProjectGraph graph)
    {
        var validation = _validator.Validate(graph);
        if (!validation.IsValid)
        {
            var first = validation.Diagnostics.FirstOrDefault()?.Message ??
                "Graph validation did not complete successfully.";
            throw new ProjectStorageException(
                ProjectStorageErrorCode.InvalidGraph,
                $"The project graph cannot be initialized: {first}");
        }
    }
}

/// <summary>Small built-in samples available through the same public initialization path.</summary>
public static class SampleProjectCatalog
{
    public const string TechnicalProject = "technical-project";

    private static readonly ReadOnlyCollection<string> SampleNames = new([TechnicalProject]);

    public static IReadOnlyList<string> Names => SampleNames;

    public static ProjectGraph Create(string sampleName)
    {
        if (!StringComparer.Ordinal.Equals(sampleName, TechnicalProject))
        {
            throw new ArgumentException($"Unknown sample '{sampleName}'.", nameof(sampleName));
        }

        var purpose = new GraphNode(new EntityId("purpose"), "An offline privacy-preserving sensor");
        var power = new GraphNode(new EntityId("scope-power"), "Power behavior", "scope");
        var privacy = new GraphNode(new EntityId("scope-privacy"), "Privacy behavior", "scope");
        var battery = new GraphNode(
            new EntityId("battery-assumption"),
            "The battery lasts for the target duty cycle",
            "assumption");
        var retention = new GraphNode(
            new EntityId("retention-policy"),
            "Collected data is retained only for the required interval",
            "requirement");
        var runtimeTest = new GraphNode(
            new EntityId("runtime-test"),
            "Runtime behavior is verified on the target device",
            "verification");
        var designAnchor = new GraphNode(
            new EntityId("design-anchor"),
            "Design record for the sensor",
            "external-anchor",
            ["artifact"]);

        GraphEdge Scope(string id, GraphNode child, GraphNode parent) => new(
            new EntityId(id), child.Id, parent.Id, "scope-parent", ReviewDirection.None);

        return new ProjectGraph(
            new ProjectId(TechnicalProject),
            "Technical Project",
            purpose.Id,
            [purpose, power, privacy, battery, retention, runtimeTest, designAnchor],
            [
                Scope("scope-power-parent", power, purpose),
                Scope("scope-privacy-parent", privacy, purpose),
                Scope("battery-scope-parent", battery, power),
                Scope("retention-scope-parent", retention, privacy),
                Scope("runtime-scope-parent", runtimeTest, power),
                Scope("anchor-scope-parent", designAnchor, power),
                new GraphEdge(
                    new EntityId("battery-requires-test"),
                    battery.Id,
                    runtimeTest.Id,
                    "requires",
                    ReviewDirection.SourceToTarget),
                new GraphEdge(
                    new EntityId("retention-informs-design"),
                    retention.Id,
                    designAnchor.Id,
                    "informs",
                    ReviewDirection.TargetToSource),
            ]);
    }
}
