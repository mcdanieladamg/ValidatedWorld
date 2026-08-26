using Microsoft.Data.Sqlite;
using ValidatedWorld.Application;
using ValidatedWorld.Core;

namespace ValidatedWorld.Persistence.Sqlite.Tests;

public sealed class SqliteProjectStoreTests
{
    private static readonly DateTimeOffset FixedUtc =
        new(2026, 8, 26, 12, 34, 56, TimeSpan.Zero);

    [Fact]
    public void Fresh_project_reopens_read_only_through_public_application_path_with_spaces()
    {
        using var workspace = new TestWorkspace();
        var databasePath = workspace.PathFor("folder with spaces", "Technical Project.vw.db");
        var application = CreateApplication();

        var created = application.CreateSample(SampleProjectCatalog.TechnicalProject, databasePath);
        var bytesBeforeReads = File.ReadAllBytes(databasePath);
        var loaded = application.Load(databasePath);
        var status = application.Status(databasePath);
        var verification = application.Verify(databasePath);
        var bytesAfterReads = File.ReadAllBytes(databasePath);

        Assert.Equal(created.Graph, loaded.Graph);
        Assert.Equal(created.StateFingerprint, loaded.StateFingerprint);
        Assert.Equal(FixedUtc, loaded.CreatedUtc);
        Assert.Equal(13, status.NodeCount);
        Assert.Equal(17, status.EdgeCount);
        Assert.Equal(1, status.SchemaVersion);
        Assert.NotEmpty(status.SqliteVersion);
        Assert.True(verification.IsValid);
        Assert.Equal(9, verification.Checks.Count);
        Assert.Equal(bytesBeforeReads, bytesAfterReads);
        Assert.Equal(new[] { databasePath }, Directory.GetFiles(Path.GetDirectoryName(databasePath)!));
    }

    [Fact]
    public void Header_version_migration_checksum_and_schema_are_checked()
    {
        using var workspace = new TestWorkspace();
        var application = CreateApplication();

        var versionPath = CreateSample(application, workspace, "unknown-version.vw.db");
        Execute(versionPath, "PRAGMA user_version = 2");
        AssertStorageError(ProjectStorageErrorCode.UnsupportedVersion, () => application.Verify(versionPath));

        var migrationPath = CreateSample(application, workspace, "migration-mismatch.vw.db");
        Execute(
            migrationPath,
            "UPDATE schema_migrations SET checksum = $value",
            ("$value", new string('0', 64)));
        AssertStorageError(ProjectStorageErrorCode.MigrationMismatch, () => application.Verify(migrationPath));

        var applicationIdPath = CreateSample(application, workspace, "wrong-application.vw.db");
        Execute(applicationIdPath, "PRAGMA application_id = 0");
        AssertStorageError(ProjectStorageErrorCode.InvalidDatabase, () => application.Verify(applicationIdPath));

        var schemaPath = CreateSample(application, workspace, "unexpected-schema.vw.db");
        Execute(schemaPath, "CREATE INDEX unexpected_index ON nodes(text)");
        AssertStorageError(ProjectStorageErrorCode.SchemaMismatch, () => application.Verify(schemaPath));
    }

    [Fact]
    public void Corrupt_malformed_and_oversized_rows_are_rejected()
    {
        using var workspace = new TestWorkspace();
        var application = CreateApplication();

        var corruptPath = workspace.PathFor("corrupt.vw.db");
        File.WriteAllBytes(corruptPath, [0x56, 0x57, 0x00, 0x01]);
        AssertStorageError(ProjectStorageErrorCode.InvalidDatabase, () => application.Verify(corruptPath));

        var malformedPath = CreateSample(application, workspace, "malformed-row.vw.db");
        Execute(
            malformedPath,
            "UPDATE nodes SET tags_json = '[\"duplicate\",\"duplicate\"]' WHERE node_id = 'purpose'");
        AssertStorageError(ProjectStorageErrorCode.MappingFailure, () => application.Verify(malformedPath));

        var oversizedPath = CreateSample(application, workspace, "oversized-row.vw.db");
        Execute(
            oversizedPath,
            "PRAGMA ignore_check_constraints = ON; UPDATE nodes SET text = $text WHERE node_id = 'purpose'",
            ("$text", new string('x', GraphLimits.TextMaxLength + 1)));
        AssertStorageError(ProjectStorageErrorCode.ResourceLimitExceeded, () => application.Verify(oversizedPath));
    }

    [Fact]
    public void Schema_is_strict_enforces_foreign_keys_and_exposes_required_views_and_indexes()
    {
        using var workspace = new TestWorkspace();
        var application = CreateApplication();
        var path = CreateSample(application, workspace, "schema.vw.db");

        using var connection = OpenReadWrite(path);
        Assert.Equal(0x56574C44, Scalar<long>(connection, "PRAGMA application_id"));
        Assert.Equal(1, Scalar<long>(connection, "PRAGMA user_version"));
        Assert.Equal("delete", Scalar<string>(connection, "PRAGMA journal_mode"));
        Assert.Equal(4, Scalar<long>(connection, "SELECT count(*) FROM pragma_table_list WHERE strict = 1"));
        Assert.Equal(1, Scalar<long>(connection, "SELECT count(*) FROM vw_project"));
        Assert.Equal(13, Scalar<long>(connection, "SELECT count(*) FROM vw_nodes"));
        Assert.Equal(17, Scalar<long>(connection, "SELECT count(*) FROM vw_edges"));
        Assert.Equal(12, Scalar<long>(connection, "SELECT count(*) FROM vw_scope"));
        Assert.Equal(5, Scalar<long>(connection, "SELECT count(*) FROM vw_review_arcs"));
        Assert.Equal(4, Scalar<long>(connection, """
            SELECT count(*) FROM sqlite_schema
            WHERE type = 'index' AND name IN (
                'ix_nodes_kind', 'ix_edges_source', 'ix_edges_target', 'ux_edges_scope_parent')
            """));

        using var invalidEdge = connection.CreateCommand();
        invalidEdge.CommandText = """
            INSERT INTO edges (
                edge_id, project_id, source_node_id, target_node_id, relationship,
                review_direction, rationale, tags_json, attributes_json)
            VALUES ('invalid-edge', 'technical-project', 'missing', 'purpose', 'requires', 1, NULL, '[]', '[]')
            """;
        var exception = Assert.Throws<SqliteException>(() => invalidEdge.ExecuteNonQuery());
        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public void Logical_fingerprint_is_independent_of_row_reinsertion_order()
    {
        using var workspace = new TestWorkspace();
        var application = CreateApplication();
        var path = CreateSample(application, workspace, "reordered.vw.db");
        var before = application.Load(path);
        var physicalBytesBefore = File.ReadAllBytes(path);

        Execute(path, """
            BEGIN;
            PRAGMA defer_foreign_keys = ON;
            CREATE TEMP TABLE node_copy AS SELECT * FROM nodes;
            CREATE TEMP TABLE edge_copy AS SELECT * FROM edges;
            DELETE FROM edges;
            DELETE FROM nodes;
            INSERT INTO nodes SELECT * FROM node_copy ORDER BY node_id COLLATE BINARY DESC;
            INSERT INTO edges SELECT * FROM edge_copy ORDER BY edge_id COLLATE BINARY DESC;
            COMMIT;
            """);

        var after = application.Load(path);
        var physicalBytesAfter = File.ReadAllBytes(path);
        Assert.False(physicalBytesBefore.SequenceEqual(physicalBytesAfter));
        Assert.Equal(before.Graph, after.Graph);
        Assert.Equal(before.StateFingerprint, after.StateFingerprint);
    }

    [Fact]
    public void Online_backup_is_equivalent_verified_and_never_overwrites()
    {
        using var workspace = new TestWorkspace();
        var application = CreateApplication();
        var sourcePath = CreateSample(application, workspace, "source project.vw.db");
        var destinationPath = workspace.PathFor("backup folder", "backup project.vw.db");

        var backup = application.Backup(sourcePath, destinationPath);
        var source = application.Load(sourcePath);

        Assert.Equal(source.Graph, backup.Graph);
        Assert.Equal(source.StateFingerprint, backup.StateFingerprint);
        Assert.Equal(source.CreatedUtc, backup.CreatedUtc);
        Assert.True(application.Verify(destinationPath).IsValid);
        AssertStorageError(
            ProjectStorageErrorCode.BackupDestinationExists,
            () => application.Backup(sourcePath, destinationPath));
    }

    [Fact]
    public void Initialization_rejects_existing_destinations_and_invalid_graphs_without_partial_files()
    {
        using var workspace = new TestWorkspace();
        var application = CreateApplication();
        var path = CreateSample(application, workspace, "existing.vw.db");

        AssertStorageError(
            ProjectStorageErrorCode.ProjectAlreadyExists,
            () => application.CreateSample(SampleProjectCatalog.TechnicalProject, path));

        var purpose = new GraphNode(new EntityId("purpose"), "Purpose");
        var orphan = new GraphNode(new EntityId("orphan"), "Orphan");
        var invalidGraph = new ProjectGraph(
            new ProjectId("invalid"),
            "Invalid",
            purpose.Id,
            [purpose, orphan],
            []);
        var invalidPath = workspace.PathFor("invalid.vw.db");
        AssertStorageError(
            ProjectStorageErrorCode.InvalidGraph,
            () => application.Initialize(invalidPath, invalidGraph));
        Assert.False(File.Exists(invalidPath));
    }

    private static ProjectApplication CreateApplication() =>
        new(new SqliteProjectStore(() => FixedUtc));

    private static string CreateSample(ProjectApplication application, TestWorkspace workspace, string name)
    {
        var path = workspace.PathFor(name);
        application.CreateSample(SampleProjectCatalog.TechnicalProject, path);
        return path;
    }

    private static SqliteConnection OpenReadWrite(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = true,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(string path, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = OpenReadWrite(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        command.ExecuteNonQuery();
    }

    private static T Scalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(
            command.ExecuteScalar()!,
            typeof(T),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void AssertStorageError(ProjectStorageErrorCode code, Action action)
    {
        var exception = Assert.Throws<ProjectStorageException>(action);
        Assert.Equal(code, exception.Code);
    }

    private sealed class TestWorkspace : IDisposable
    {
        public TestWorkspace()
        {
            Root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"ValidatedWorld-SqliteTests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string PathFor(params string[] segments)
        {
            var path = segments.Aggregate(Root, System.IO.Path.Combine);
            var directory = System.IO.Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
