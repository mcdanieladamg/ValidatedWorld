using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ValidatedWorld.Application;
using ValidatedWorld.Core;
using ValidatedWorld.Serialization;
using ValidatedWorld.Validation;

namespace ValidatedWorld.Persistence.Sqlite;

/// <summary>Deterministic fault-injection points before an atomic write commits.</summary>
public enum SqliteWriteBoundary
{
    AfterBeginImmediate,
    AfterBaseVerification,
    AfterEdgeRemovals,
    AfterNodeRemovals,
    AfterNodeUpserts,
    AfterEdgeUpserts,
    AfterProjectUpdate,
    AfterFinalVerification,
}

/// <summary>SQLite v1 storage for one immutable current project graph.</summary>
public sealed class SqliteProjectStore : IProjectStore
{
    public const int MaximumNodeCount = 100_000;
    public const int MaximumEdgeCount = 1_000_000;
    public const int MaximumMetadataJsonLength = 1_048_576;

    private const long MaximumDatabaseFileLength = 16L * 1024 * 1024 * 1024;
    private const int CommandTimeoutSeconds = 10;
    private static readonly object NativeInitializationLock = new();
    private static bool _nativeInitialized;

    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<SqliteWriteBoundary, Exception?>? _writeFault;
    private readonly GraphValidator _validator = new();

    public SqliteProjectStore(
        Func<DateTimeOffset>? utcNow = null,
        Func<SqliteWriteBoundary, Exception?>? writeFault = null)
    {
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _writeFault = writeFault;
    }

    public StoredProject Initialize(string path, ProjectGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var fullPath = NormalizeNewPath(path, ProjectStorageErrorCode.ProjectAlreadyExists);
        EnsureGraphCanBeStored(graph);

        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = CreateTemporaryPath(fullPath, "initializing");
        try
        {
            StoredProject temporaryProject;
            using (var connection = OpenConnection(temporaryPath, SqliteOpenMode.ReadWriteCreate))
            {
                var now = _utcNow().ToUniversalTime();
                SqliteSchema.ApplyMigration(connection, now);
                InsertInitialGraph(connection, graph, now);
                temporaryProject = LoadAndVerify(connection, temporaryPath);
            }

            File.Move(temporaryPath, fullPath, overwrite: false);
            return temporaryProject with { Path = fullPath };
        }
        catch (Exception exception)
        {
            DeleteOwnedTemporaryFile(temporaryPath);
            throw Translate(exception, $"Could not initialize SQLite project '{fullPath}'.");
        }
    }

    public StoredProject Load(string path)
    {
        var fullPath = NormalizeExistingPath(path);
        try
        {
            using var connection = OpenConnection(fullPath, SqliteOpenMode.ReadOnly);
            return LoadAndVerify(connection, fullPath);
        }
        catch (Exception exception)
        {
            throw Translate(exception, $"Could not load SQLite project '{fullPath}'.");
        }
    }

    public ProjectStatus GetStatus(string path)
    {
        var fullPath = NormalizeExistingPath(path);
        try
        {
            using var connection = OpenConnection(fullPath, SqliteOpenMode.ReadOnly);
            var project = LoadAndVerify(connection, fullPath);
            using var versionCommand = CreateCommand(connection, "SELECT sqlite_version()");
            var sqliteVersion = Convert.ToString(
                versionCommand.ExecuteScalar(),
                CultureInfo.InvariantCulture) ?? "unknown";
            return new ProjectStatus(
                fullPath,
                project.Graph.ProjectId,
                project.Graph.Title,
                project.Graph.PurposeNodeId,
                project.Graph.Nodes.Count,
                project.Graph.Edges.Count,
                project.StateFingerprint,
                SqliteSchema.CurrentVersion,
                sqliteVersion);
        }
        catch (Exception exception)
        {
            throw Translate(exception, $"Could not read SQLite project status for '{fullPath}'.");
        }
    }

    public ProjectVerification Verify(string path)
    {
        var project = Load(path);
        return new ProjectVerification(
            project.Path,
            true,
            project.StateFingerprint,
            project.Graph.Nodes.Count,
            project.Graph.Edges.Count,
            [
                "application-id",
                "schema-version",
                "migration-checksum",
                "schema-objects",
                "sqlite-integrity",
                "foreign-keys",
                "strict-row-mapping",
                "graph-validation",
                "state-fingerprint",
            ]);
    }

    public StoredProject Backup(string sourcePath, string destinationPath)
    {
        var sourceFullPath = NormalizeExistingPath(sourcePath);
        var destinationFullPath = NormalizeNewPath(
            destinationPath,
            ProjectStorageErrorCode.BackupDestinationExists);
        if (PathsEqual(sourceFullPath, destinationFullPath))
        {
            throw new ProjectStorageException(
                ProjectStorageErrorCode.BackupDestinationExists,
                "The backup destination must differ from the source project path.");
        }

        var destinationDirectory = Path.GetDirectoryName(destinationFullPath)!;
        Directory.CreateDirectory(destinationDirectory);
        var temporaryPath = CreateTemporaryPath(destinationFullPath, "backup");
        try
        {
            StoredProject temporaryProject;
            using (var source = OpenConnection(sourceFullPath, SqliteOpenMode.ReadOnly))
            {
                _ = LoadAndVerify(source, sourceFullPath);
                using var destination = OpenConnection(temporaryPath, SqliteOpenMode.ReadWriteCreate);
                source.BackupDatabase(destination);
            }

            using (var check = OpenConnection(temporaryPath, SqliteOpenMode.ReadOnly))
            {
                temporaryProject = LoadAndVerify(check, temporaryPath);
            }

            File.Move(temporaryPath, destinationFullPath, overwrite: false);
            return temporaryProject with { Path = destinationFullPath };
        }
        catch (Exception exception)
        {
            DeleteOwnedTemporaryFile(temporaryPath);
            throw Translate(exception, $"Could not back up SQLite project to '{destinationFullPath}'.");
        }
    }

    public ProjectWriteResult Write(ProjectWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Operations);
        var fullPath = NormalizeExistingPath(request.Path);
        var transactionStarted = false;
        SqliteConnection? connection = null;
        try
        {
            connection = OpenConnection(fullPath, SqliteOpenMode.ReadWrite);
            ExecuteTransactionCommand(connection, "BEGIN IMMEDIATE");
            transactionStarted = true;
            InjectFault(SqliteWriteBoundary.AfterBeginImmediate);

            var current = LoadAndVerify(connection, fullPath);
            if (current.Graph.ProjectId != request.ProjectId ||
                !StringComparer.Ordinal.Equals(current.StateFingerprint, request.BaseFingerprint))
            {
                Rollback(connection);
                transactionStarted = false;
                return new ProjectWriteResult(
                    ProjectWriteOutcome.Stale,
                    null,
                    null,
                    "The canonical project changed after the reviewed session began.");
            }

            InjectFault(SqliteWriteBoundary.AfterBaseVerification);
            var projection = new GraphProjector().Project(current.Graph, request.Operations);
            if (!projection.IsValid)
            {
                Rollback(connection);
                transactionStarted = false;
                return new ProjectWriteResult(
                    ProjectWriteOutcome.Failed,
                    null,
                    ProjectStorageErrorCode.InvalidGraph,
                    "The proposal is not structurally valid at write time.");
            }

            var expectedFingerprint = GraphFingerprints.Proposed(projection.Graph);
            if (!StringComparer.Ordinal.Equals(expectedFingerprint, request.ProposedFingerprint))
            {
                Rollback(connection);
                transactionStarted = false;
                return new ProjectWriteResult(
                    ProjectWriteOutcome.Failed,
                    null,
                    ProjectStorageErrorCode.MappingFailure,
                    "The proposal fingerprint no longer matches the final operation batch.");
            }

            DeleteEdges(connection, request.Operations);
            InjectFault(SqliteWriteBoundary.AfterEdgeRemovals);
            DeleteNodes(connection, request.Operations);
            InjectFault(SqliteWriteBoundary.AfterNodeRemovals);
            UpsertNodes(connection, projection.Graph.ProjectId, request.Operations);
            InjectFault(SqliteWriteBoundary.AfterNodeUpserts);
            InsertEdges(connection, projection.Graph.ProjectId, request.Operations);
            InjectFault(SqliteWriteBoundary.AfterEdgeUpserts);

            var now = _utcNow().ToUniversalTime();
            UpdateProjectFingerprint(connection, projection.Graph.ProjectId, expectedFingerprint, now);
            InjectFault(SqliteWriteBoundary.AfterProjectUpdate);
            VerifyForeignKeys(connection, transaction: null);
            var written = LoadAndVerify(connection, fullPath);
            if (!StringComparer.Ordinal.Equals(written.StateFingerprint, expectedFingerprint))
            {
                throw new ProjectStorageException(
                    ProjectStorageErrorCode.FingerprintMismatch,
                    "The committed graph does not match the final proposal fingerprint.");
            }

            InjectFault(SqliteWriteBoundary.AfterFinalVerification);
            ExecuteTransactionCommand(connection, "COMMIT");
            transactionStarted = false;
            return new ProjectWriteResult(
                ProjectWriteOutcome.Written,
                written,
                null,
                "The reviewed proposal was written atomically.");
        }
        catch (Exception exception)
        {
            if (transactionStarted && connection is not null)
            {
                Rollback(connection);
            }

            if (exception is SqliteException sqlite && IsBusy(sqlite))
            {
                return new ProjectWriteResult(
                    ProjectWriteOutcome.Busy,
                    null,
                    null,
                    "SQLite is busy; the proposal remains available for retry.");
            }

            var translated = Translate(exception, $"Could not write SQLite project '{fullPath}'.")
                as ProjectStorageException ?? throw new InvalidOperationException();
            return new ProjectWriteResult(
                ProjectWriteOutcome.Failed,
                null,
                translated.Code,
                translated.Message);
        }
        finally
        {
            connection?.Dispose();
        }
    }

    private StoredProject LoadAndVerify(SqliteConnection connection, string fullPath)
    {
        SqliteSchema.Verify(connection);
        VerifyIntegrity(connection);

        var projectRow = ReadProjectRow(connection);
        var graph = ReadGraph(connection, projectRow);
        var validation = _validator.Validate(graph);
        if (!validation.IsValid)
        {
            var finding = validation.Diagnostics.FirstOrDefault()?.Message ??
                "Graph validation was inconclusive.";
            throw new ProjectStorageException(
                ProjectStorageErrorCode.InvalidGraph,
                $"The stored graph is not structurally valid: {finding}");
        }

        var computedFingerprint = GraphFingerprints.State(graph);
        if (!StringComparer.Ordinal.Equals(projectRow.StateFingerprint, computedFingerprint))
        {
            throw new ProjectStorageException(
                ProjectStorageErrorCode.FingerprintMismatch,
                "The stored state fingerprint does not match the logical project graph.");
        }

        return new StoredProject(
            fullPath,
            graph,
            computedFingerprint,
            projectRow.CreatedUtc,
            projectRow.UpdatedUtc);
    }

    private void InsertInitialGraph(SqliteConnection connection, ProjectGraph graph, DateTimeOffset now)
    {
        using var transaction = connection.BeginTransaction();
        using (var defer = CreateCommand(connection, "PRAGMA defer_foreign_keys = ON", transaction))
        {
            defer.ExecuteNonQuery();
        }

        var fingerprint = GraphFingerprints.State(graph);
        using (var project = CreateCommand(connection, """
            INSERT INTO projects (
                project_id, title, purpose_node_id, created_utc, updated_utc, state_fingerprint)
            VALUES ($projectId, $title, $purposeNodeId, $createdUtc, $updatedUtc, $fingerprint)
            """, transaction))
        {
            project.Parameters.AddWithValue("$projectId", graph.ProjectId.Value);
            project.Parameters.AddWithValue("$title", graph.Title);
            project.Parameters.AddWithValue("$purposeNodeId", graph.PurposeNodeId.Value);
            project.Parameters.AddWithValue("$createdUtc", SqliteSchema.FormatUtc(now));
            project.Parameters.AddWithValue("$updatedUtc", SqliteSchema.FormatUtc(now));
            project.Parameters.AddWithValue("$fingerprint", fingerprint);
            project.ExecuteNonQuery();
        }

        foreach (var node in graph.Nodes)
        {
            using var command = CreateCommand(connection, """
                INSERT INTO nodes (node_id, project_id, text, kind, tags_json, attributes_json)
                VALUES ($id, $projectId, $text, $kind, $tags, $attributes)
                """, transaction);
            command.Parameters.AddWithValue("$id", node.Id.Value);
            command.Parameters.AddWithValue("$projectId", graph.ProjectId.Value);
            command.Parameters.AddWithValue("$text", node.Text);
            command.Parameters.AddWithValue("$kind", (object?)node.Kind ?? DBNull.Value);
            command.Parameters.AddWithValue("$tags", EncodeTags(node.Tags));
            command.Parameters.AddWithValue("$attributes", EncodeAttributes(node.Attributes));
            command.ExecuteNonQuery();
        }

        foreach (var edge in graph.Edges)
        {
            using var command = CreateCommand(connection, """
                INSERT INTO edges (
                    edge_id, project_id, source_node_id, target_node_id, relationship,
                    review_direction, rationale, tags_json, attributes_json)
                VALUES (
                    $id, $projectId, $source, $target, $relationship,
                    $reviewDirection, $rationale, $tags, $attributes)
                """, transaction);
            command.Parameters.AddWithValue("$id", edge.Id.Value);
            command.Parameters.AddWithValue("$projectId", graph.ProjectId.Value);
            command.Parameters.AddWithValue("$source", edge.Source.Value);
            command.Parameters.AddWithValue("$target", edge.Target.Value);
            command.Parameters.AddWithValue("$relationship", edge.Relationship);
            command.Parameters.AddWithValue("$reviewDirection", (int)edge.ReviewDirection);
            command.Parameters.AddWithValue("$rationale", (object?)edge.Rationale ?? DBNull.Value);
            command.Parameters.AddWithValue("$tags", EncodeTags(edge.Tags));
            command.Parameters.AddWithValue("$attributes", EncodeAttributes(edge.Attributes));
            command.ExecuteNonQuery();
        }

        VerifyForeignKeys(connection, transaction);
        transaction.Commit();
    }

    private static void DeleteEdges(SqliteConnection connection, GraphOperationBatch operations)
    {
        foreach (var operation in operations.Operations.Where(operation =>
                     operation.EntityKind == GraphEntityKind.Edge &&
                     (operation.Kind == GraphOperationKind.Remove || operation.Kind == GraphOperationKind.Replace)))
        {
            using var command = CreateCommand(connection, "DELETE FROM edges WHERE edge_id = $id");
            command.Parameters.AddWithValue("$id", operation.EntityId.Value);
            EnsureOneRow(command, $"edge '{operation.EntityId.Value}' removal");
        }
    }

    private static void DeleteNodes(SqliteConnection connection, GraphOperationBatch operations)
    {
        foreach (var operation in operations.Operations.Where(operation =>
                     operation.EntityKind == GraphEntityKind.Node && operation.Kind == GraphOperationKind.Remove))
        {
            using var command = CreateCommand(connection, "DELETE FROM nodes WHERE node_id = $id");
            command.Parameters.AddWithValue("$id", operation.EntityId.Value);
            EnsureOneRow(command, $"node '{operation.EntityId.Value}' removal");
        }
    }

    private static void UpsertNodes(
        SqliteConnection connection,
        ProjectId projectId,
        GraphOperationBatch operations)
    {
        foreach (var operation in operations.Operations.Where(operation =>
                     operation.EntityKind == GraphEntityKind.Node && operation.Kind != GraphOperationKind.Remove))
        {
            var node = operation.Node!;
            using var command = CreateCommand(connection, operation.Kind == GraphOperationKind.Add
                ? """
                    INSERT INTO nodes (node_id, project_id, text, kind, tags_json, attributes_json)
                    VALUES ($id, $projectId, $text, $kind, $tags, $attributes)
                    """
                : """
                    UPDATE nodes
                    SET text = $text, kind = $kind, tags_json = $tags, attributes_json = $attributes
                    WHERE node_id = $id AND project_id = $projectId
                    """);
            command.Parameters.AddWithValue("$id", node.Id.Value);
            command.Parameters.AddWithValue("$projectId", projectId.Value);
            command.Parameters.AddWithValue("$text", node.Text);
            command.Parameters.AddWithValue("$kind", (object?)node.Kind ?? DBNull.Value);
            command.Parameters.AddWithValue("$tags", EncodeTags(node.Tags));
            command.Parameters.AddWithValue("$attributes", EncodeAttributes(node.Attributes));
            EnsureOneRow(command, $"node '{node.Id.Value}' {operation.Kind.ToString().ToLowerInvariant()}");
        }
    }

    private static void InsertEdges(
        SqliteConnection connection,
        ProjectId projectId,
        GraphOperationBatch operations)
    {
        foreach (var operation in operations.Operations.Where(operation =>
                     operation.EntityKind == GraphEntityKind.Edge && operation.Kind != GraphOperationKind.Remove))
        {
            var edge = operation.Edge!;
            using var command = CreateCommand(connection, """
                INSERT INTO edges (
                    edge_id, project_id, source_node_id, target_node_id, relationship,
                    review_direction, rationale, tags_json, attributes_json)
                VALUES (
                    $id, $projectId, $source, $target, $relationship,
                    $reviewDirection, $rationale, $tags, $attributes)
                """);
            command.Parameters.AddWithValue("$id", edge.Id.Value);
            command.Parameters.AddWithValue("$projectId", projectId.Value);
            command.Parameters.AddWithValue("$source", edge.Source.Value);
            command.Parameters.AddWithValue("$target", edge.Target.Value);
            command.Parameters.AddWithValue("$relationship", edge.Relationship);
            command.Parameters.AddWithValue("$reviewDirection", (int)edge.ReviewDirection);
            command.Parameters.AddWithValue("$rationale", (object?)edge.Rationale ?? DBNull.Value);
            command.Parameters.AddWithValue("$tags", EncodeTags(edge.Tags));
            command.Parameters.AddWithValue("$attributes", EncodeAttributes(edge.Attributes));
            EnsureOneRow(command, $"edge '{edge.Id.Value}' {operation.Kind.ToString().ToLowerInvariant()}");
        }
    }

    private static void UpdateProjectFingerprint(
        SqliteConnection connection,
        ProjectId projectId,
        string fingerprint,
        DateTimeOffset updatedUtc)
    {
        using var command = CreateCommand(connection, """
            UPDATE projects
            SET updated_utc = $updatedUtc, state_fingerprint = $fingerprint
            WHERE project_id = $projectId
            """);
        command.Parameters.AddWithValue("$updatedUtc", SqliteSchema.FormatUtc(updatedUtc));
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        command.Parameters.AddWithValue("$projectId", projectId.Value);
        EnsureOneRow(command, "project fingerprint update");
    }

    private void InjectFault(SqliteWriteBoundary boundary)
    {
        var exception = _writeFault?.Invoke(boundary);
        if (exception is not null)
        {
            throw exception;
        }
    }

    private static void EnsureOneRow(SqliteCommand command, string operation)
    {
        if (command.ExecuteNonQuery() != 1)
        {
            throw MappingFailure($"SQLite did not apply exactly one row for {operation}.");
        }
    }

    private static void ExecuteTransactionCommand(SqliteConnection connection, string commandText)
    {
        using var command = CreateCommand(connection, commandText);
        command.ExecuteNonQuery();
    }

    private static void Rollback(SqliteConnection connection)
    {
        try
        {
            ExecuteTransactionCommand(connection, "ROLLBACK");
        }
        catch (SqliteException)
        {
            // The original write failure is more useful than a failed cleanup attempt.
        }
    }

    private static bool IsBusy(SqliteException exception) =>
        exception.SqliteErrorCode is 5 or 6;

    private static ProjectRow ReadProjectRow(SqliteConnection connection)
    {
        using var command = CreateCommand(connection, """
            SELECT project_id, title, purpose_node_id, created_utc, updated_utc, state_fingerprint
            FROM projects
            ORDER BY project_id COLLATE BINARY
            """);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw MappingFailure("The SQLite project contains no project row.");
        }

        var row = new ProjectRow(
            ReadBoundedString(reader, 0, GraphLimits.IdentifierMaxLength, "project_id"),
            ReadBoundedString(reader, 1, GraphLimits.TextMaxLength, "title"),
            ReadBoundedString(reader, 2, GraphLimits.IdentifierMaxLength, "purpose_node_id"),
            ParseUtc(ReadBoundedString(reader, 3, 64, "created_utc"), "created_utc"),
            ParseUtc(ReadBoundedString(reader, 4, 64, "updated_utc"), "updated_utc"),
            ReadFingerprint(reader, 5));
        if (reader.Read())
        {
            throw MappingFailure("The SQLite project contains more than one project row.");
        }

        return row;
    }

    private static ProjectGraph ReadGraph(SqliteConnection connection, ProjectRow project)
    {
        var nodeCount = ReadCount(connection, "nodes", MaximumNodeCount);
        var edgeCount = ReadCount(connection, "edges", MaximumEdgeCount);
        var nodes = new List<GraphNode>(nodeCount);
        var edges = new List<GraphEdge>(edgeCount);

        using (var command = CreateCommand(connection, """
            SELECT node_id, project_id, text, kind, tags_json, attributes_json
            FROM nodes
            ORDER BY node_id COLLATE BINARY
            """))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var projectId = ReadBoundedString(reader, 1, GraphLimits.IdentifierMaxLength, "nodes.project_id");
                EnsureProjectId(project.ProjectId, projectId, "node");
                var tagsJson = ReadBoundedString(reader, 4, MaximumMetadataJsonLength, "nodes.tags_json");
                var attributesJson = ReadBoundedString(
                    reader, 5, MaximumMetadataJsonLength, "nodes.attributes_json");
                var node = new GraphNode(
                    new EntityId(ReadBoundedString(reader, 0, GraphLimits.IdentifierMaxLength, "node_id")),
                    ReadBoundedString(reader, 2, GraphLimits.TextMaxLength, "nodes.text"),
                    ReadNullableBoundedString(reader, 3, GraphLimits.MetadataNameMaxLength, "nodes.kind"),
                    DecodeTags(tagsJson),
                    DecodeAttributes(attributesJson));
                EnsureCanonicalMetadata(tagsJson, attributesJson, node.Tags, node.Attributes, node.Id.Value);
                nodes.Add(node);
            }
        }

        using (var command = CreateCommand(connection, """
            SELECT edge_id, project_id, source_node_id, target_node_id, relationship,
                   review_direction, rationale, tags_json, attributes_json
            FROM edges
            ORDER BY edge_id COLLATE BINARY
            """))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var projectId = ReadBoundedString(reader, 1, GraphLimits.IdentifierMaxLength, "edges.project_id");
                EnsureProjectId(project.ProjectId, projectId, "edge");
                var reviewDirectionValue = reader.GetInt32(5);
                if (!Enum.IsDefined((ReviewDirection)reviewDirectionValue))
                {
                    throw MappingFailure("An edge has an unknown review direction.");
                }

                var tagsJson = ReadBoundedString(reader, 7, MaximumMetadataJsonLength, "edges.tags_json");
                var attributesJson = ReadBoundedString(
                    reader, 8, MaximumMetadataJsonLength, "edges.attributes_json");
                var edge = new GraphEdge(
                    new EntityId(ReadBoundedString(reader, 0, GraphLimits.IdentifierMaxLength, "edge_id")),
                    new EntityId(ReadBoundedString(
                        reader, 2, GraphLimits.IdentifierMaxLength, "source_node_id")),
                    new EntityId(ReadBoundedString(
                        reader, 3, GraphLimits.IdentifierMaxLength, "target_node_id")),
                    ReadBoundedString(
                        reader, 4, GraphLimits.RelationshipLabelMaxLength, "edges.relationship"),
                    (ReviewDirection)reviewDirectionValue,
                    ReadNullableBoundedString(reader, 6, GraphLimits.TextMaxLength, "edges.rationale"),
                    DecodeTags(tagsJson),
                    DecodeAttributes(attributesJson));
                EnsureCanonicalMetadata(tagsJson, attributesJson, edge.Tags, edge.Attributes, edge.Id.Value);
                edges.Add(edge);
            }
        }

        if (nodes.Count != nodeCount || edges.Count != edgeCount)
        {
            throw MappingFailure("The project row count changed while it was being read.");
        }

        try
        {
            return new ProjectGraph(
                new ProjectId(project.ProjectId),
                project.Title,
                new EntityId(project.PurposeNodeId),
                nodes,
                edges);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw MappingFailure("Stored project values do not map to the graph domain.", exception);
        }
    }

    private void EnsureGraphCanBeStored(ProjectGraph graph)
    {
        if (graph.Nodes.Count > MaximumNodeCount || graph.Edges.Count > MaximumEdgeCount)
        {
            throw new ProjectStorageException(
                ProjectStorageErrorCode.ResourceLimitExceeded,
                $"A SQLite project may contain at most {MaximumNodeCount} nodes and {MaximumEdgeCount} edges.");
        }

        var validation = _validator.Validate(graph);
        if (!validation.IsValid)
        {
            throw new ProjectStorageException(
                ProjectStorageErrorCode.InvalidGraph,
                "Only a structurally valid complete graph can initialize a SQLite project.");
        }

        foreach (var json in graph.Nodes.SelectMany(node => new[]
                 {
                     EncodeTags(node.Tags), EncodeAttributes(node.Attributes),
                 }).Concat(graph.Edges.SelectMany(edge => new[]
                 {
                     EncodeTags(edge.Tags), EncodeAttributes(edge.Attributes),
                 })))
        {
            if (json.Length > MaximumMetadataJsonLength)
            {
                throw new ProjectStorageException(
                    ProjectStorageErrorCode.ResourceLimitExceeded,
                    "A graph entity's canonical metadata exceeds the SQLite row limit.");
            }
        }
    }

    private static void VerifyIntegrity(SqliteConnection connection)
    {
        using (var command = CreateCommand(connection, "PRAGMA integrity_check"))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read() || !StringComparer.Ordinal.Equals(reader.GetString(0), "ok") || reader.Read())
            {
                throw new ProjectStorageException(
                    ProjectStorageErrorCode.IntegrityFailure,
                    "SQLite integrity_check did not report a single successful result.");
            }
        }

        VerifyForeignKeys(connection, transaction: null);
    }

    private static void VerifyForeignKeys(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = CreateCommand(connection, "PRAGMA foreign_key_check", transaction);
        using var reader = command.ExecuteReader();
        if (reader.Read())
        {
            throw new ProjectStorageException(
                ProjectStorageErrorCode.IntegrityFailure,
                "SQLite foreign_key_check found a constraint violation.");
        }
    }

    private static SqliteConnection OpenConnection(string fullPath, SqliteOpenMode mode)
    {
        EnsureNativeProvider();
        if (mode == SqliteOpenMode.ReadOnly)
        {
            var fileLength = new FileInfo(fullPath).Length;
            if (fileLength > MaximumDatabaseFileLength)
            {
                throw new ProjectStorageException(
                    ProjectStorageErrorCode.ResourceLimitExceeded,
                    "The SQLite project file exceeds the configured size limit.");
            }
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true,
            Pooling = false,
            DefaultTimeout = CommandTimeoutSeconds,
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();

            using (var policy = CreateCommand(connection, mode == SqliteOpenMode.ReadOnly
                ? """
                    PRAGMA foreign_keys = ON;
                    PRAGMA query_only = ON;
                    PRAGMA trusted_schema = OFF;
                    PRAGMA recursive_triggers = OFF;
                    PRAGMA busy_timeout = 10000;
                    """
                : """
                    PRAGMA foreign_keys = ON;
                    PRAGMA journal_mode = DELETE;
                    PRAGMA synchronous = FULL;
                    PRAGMA trusted_schema = OFF;
                    PRAGMA recursive_triggers = OFF;
                    PRAGMA busy_timeout = 10000;
                    """))
            {
                policy.ExecuteNonQuery();
            }

            using var check = CreateCommand(connection, "PRAGMA foreign_keys");
            if (Convert.ToInt32(check.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
            {
                throw new ProjectStorageException(
                    ProjectStorageErrorCode.StorageFailure,
                    "SQLite foreign-key enforcement could not be enabled.");
            }

            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void EnsureNativeProvider()
    {
        if (_nativeInitialized)
        {
            return;
        }

        lock (NativeInitializationLock)
        {
            if (_nativeInitialized)
            {
                return;
            }

            SQLitePCL.Batteries_V2.Init();
            _nativeInitialized = true;
        }
    }

    private static string EncodeTags(IEnumerable<string> tags) => Protocol.Serialize(tags.ToArray());

    private static string EncodeAttributes(IEnumerable<GraphAttribute> attributes) => Protocol.Serialize(
        attributes.Select(attribute => new AttributeDto(attribute.Name, EncodeValue(attribute.Value))).ToArray());

    private static ValueDto EncodeValue(GraphValue value) => value.Kind switch
    {
        GraphValueKind.Text => new(value.Kind, value.TextValue, 0, false, null),
        GraphValueKind.Integer => new(value.Kind, null, value.IntegerValue, false, null),
        GraphValueKind.Decimal => new(value.Kind, value.DecimalValue, 0, false, null),
        GraphValueKind.Boolean => new(value.Kind, null, 0, value.BooleanValue, null),
        GraphValueKind.Symbol => new(value.Kind, value.SymbolValue, 0, false, null),
        GraphValueKind.Instant => new(value.Kind, null, 0, false, value.InstantValue.ToString("O")),
        _ => throw new InvalidOperationException("The graph value is uninitialized."),
    };

    private static IReadOnlyList<string> DecodeTags(string json)
    {
        try
        {
            return Protocol.Deserialize<string[]>(json);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw MappingFailure("Stored tags are not valid canonical JSON.", exception);
        }
    }

    private static IReadOnlyList<KeyValuePair<string, GraphValue>> DecodeAttributes(string json)
    {
        try
        {
            var values = Protocol.Deserialize<AttributeDto[]>(json);
            return values.Select(attribute => new KeyValuePair<string, GraphValue>(
                attribute.Name,
                DecodeValue(attribute.Value))).ToArray();
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or
                                           ArgumentException or FormatException)
        {
            throw MappingFailure("Stored attributes are not valid canonical JSON.", exception);
        }
    }

    private static GraphValue DecodeValue(ValueDto value)
    {
        if (value is null)
        {
            throw new JsonException("An attribute value cannot be null.");
        }

        return value.Kind switch
        {
            GraphValueKind.Text => GraphValue.FromText(value.Text ?? throw new JsonException("Text is required.")),
            GraphValueKind.Integer => GraphValue.FromInteger(value.Integer),
            GraphValueKind.Decimal => GraphValue.FromDecimal(
                value.Text ?? throw new JsonException("Decimal is required.")),
            GraphValueKind.Boolean => GraphValue.FromBoolean(value.Boolean),
            GraphValueKind.Symbol => GraphValue.FromSymbol(
                value.Text ?? throw new JsonException("Symbol is required.")),
            GraphValueKind.Instant => GraphValue.FromInstant(ParseUtc(
                value.Instant ?? throw new JsonException("Instant is required."), "attribute instant")),
            _ => throw new JsonException("Unknown graph value kind."),
        };
    }

    private static void EnsureCanonicalMetadata(
        string tagsJson,
        string attributesJson,
        IReadOnlyList<string> tags,
        IReadOnlyList<GraphAttribute> attributes,
        string entityId)
    {
        if (!StringComparer.Ordinal.Equals(tagsJson, EncodeTags(tags)) ||
            !StringComparer.Ordinal.Equals(attributesJson, EncodeAttributes(attributes)))
        {
            throw MappingFailure($"Entity '{entityId}' has noncanonical metadata JSON.");
        }
    }

    private static int ReadCount(SqliteConnection connection, string tableName, int maximum)
    {
        using var command = CreateCommand(connection, $"SELECT count(*) FROM {tableName}");
        var count = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (count > maximum)
        {
            throw new ProjectStorageException(
                ProjectStorageErrorCode.ResourceLimitExceeded,
                $"Table '{tableName}' exceeds its configured row limit of {maximum}.");
        }

        return checked((int)count);
    }

    private static string ReadBoundedString(SqliteDataReader reader, int ordinal, int maximum, string columnName)
    {
        if (reader.IsDBNull(ordinal))
        {
            throw MappingFailure($"Required column '{columnName}' is null.");
        }

        var value = reader.GetString(ordinal);
        if (value.Length > maximum)
        {
            throw new ProjectStorageException(
                ProjectStorageErrorCode.ResourceLimitExceeded,
                $"Column '{columnName}' exceeds its configured length limit of {maximum}.");
        }

        return value;
    }

    private static string? ReadNullableBoundedString(
        SqliteDataReader reader,
        int ordinal,
        int maximum,
        string columnName) => reader.IsDBNull(ordinal)
            ? null
            : ReadBoundedString(reader, ordinal, maximum, columnName);

    private static string ReadFingerprint(SqliteDataReader reader, int ordinal)
    {
        var value = ReadBoundedString(reader, ordinal, 64, "state_fingerprint");
        if (value.Length != 64 || value.Any(character =>
                !char.IsAsciiHexDigit(character) || char.IsAsciiLetterUpper(character)))
        {
            throw MappingFailure("The stored state fingerprint is not canonical lowercase SHA-256 text.");
        }

        return value;
    }

    private static DateTimeOffset ParseUtc(string value, string columnName)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed) || parsed.Offset != TimeSpan.Zero ||
            !StringComparer.Ordinal.Equals(value, parsed.ToString("O", CultureInfo.InvariantCulture)))
        {
            throw MappingFailure($"Column '{columnName}' is not a canonical UTC instant.");
        }

        return parsed;
    }

    private static void EnsureProjectId(string expected, string actual, string entityKind)
    {
        if (!StringComparer.Ordinal.Equals(expected, actual))
        {
            throw MappingFailure($"A stored {entityKind} belongs to a different project ID.");
        }
    }

    private static string NormalizeExistingPath(string path)
    {
        var fullPath = NormalizePath(path);
        if (!File.Exists(fullPath))
        {
            throw new ProjectStorageException(
                ProjectStorageErrorCode.ProjectNotFound,
                $"SQLite project '{fullPath}' does not exist.");
        }

        return fullPath;
    }

    private static string NormalizeNewPath(string path, ProjectStorageErrorCode existsCode)
    {
        var fullPath = NormalizePath(path);
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
        {
            throw new ProjectStorageException(existsCode, $"Destination '{fullPath}' already exists.");
        }

        return fullPath;
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl))
        {
            throw new ProjectStorageException(
                ProjectStorageErrorCode.InvalidPath,
                "A project path cannot be empty, whitespace-only, or contain control characters.");
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is
            ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ProjectStorageException(
                ProjectStorageErrorCode.InvalidPath,
                "The project path is invalid.",
                exception);
        }
    }

    private static string CreateTemporaryPath(string finalPath, string purpose) =>
        $"{finalPath}.{purpose}-{Guid.NewGuid():N}.tmp";

    private static void DeleteOwnedTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Preserve the primary storage failure. The unique temporary path is reported in its context.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the primary storage failure. The unique temporary path is reported in its context.
        }
    }

    private static bool PathsEqual(string left, string right) => string.Equals(
        left,
        right,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static SqliteCommand CreateCommand(
        SqliteConnection connection,
        string commandText,
        SqliteTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.CommandTimeout = CommandTimeoutSeconds;
        command.Transaction = transaction;
        return command;
    }

    private static ProjectStorageException MappingFailure(string message, Exception? exception = null) =>
        new(ProjectStorageErrorCode.MappingFailure, message, exception);

    private static Exception Translate(Exception exception, string context)
    {
        if (exception is ProjectStorageException)
        {
            return exception;
        }

        return exception switch
        {
            SqliteException sqlite => new ProjectStorageException(
                ProjectStorageErrorCode.InvalidDatabase,
                $"{context} SQLite rejected the file or operation (error {sqlite.SqliteErrorCode}).",
                sqlite),
            IOException or UnauthorizedAccessException => new ProjectStorageException(
                ProjectStorageErrorCode.StorageFailure,
                context,
                exception),
            _ => new ProjectStorageException(ProjectStorageErrorCode.MappingFailure, context, exception),
        };
    }

    private sealed record ProjectRow(
        string ProjectId,
        string Title,
        string PurposeNodeId,
        DateTimeOffset CreatedUtc,
        DateTimeOffset UpdatedUtc,
        string StateFingerprint);
}
