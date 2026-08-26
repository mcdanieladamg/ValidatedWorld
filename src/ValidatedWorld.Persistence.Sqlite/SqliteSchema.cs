using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using ValidatedWorld.Application;

namespace ValidatedWorld.Persistence.Sqlite;

internal static class SqliteSchema
{
    public const int ApplicationId = 0x56574C44; // VWLD
    public const int CurrentVersion = 1;
    public const string MigrationId = "sqlite-v1-current-state";

    private static readonly SchemaObject[] Objects =
    [
        new("table", "schema_migrations", """
            CREATE TABLE schema_migrations (
                migration_id TEXT PRIMARY KEY,
                checksum TEXT NOT NULL CHECK (length(checksum) = 64),
                applied_utc TEXT NOT NULL
            ) STRICT, WITHOUT ROWID
            """),
        new("table", "projects", """
            CREATE TABLE projects (
                project_id TEXT PRIMARY KEY COLLATE BINARY,
                title TEXT NOT NULL CHECK (length(title) BETWEEN 1 AND 16384),
                purpose_node_id TEXT NOT NULL COLLATE BINARY,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                state_fingerprint TEXT NOT NULL CHECK (length(state_fingerprint) = 64),
                FOREIGN KEY (purpose_node_id) REFERENCES nodes(node_id)
                    ON UPDATE RESTRICT ON DELETE RESTRICT DEFERRABLE INITIALLY DEFERRED
            ) STRICT, WITHOUT ROWID
            """),
        new("table", "nodes", """
            CREATE TABLE nodes (
                node_id TEXT PRIMARY KEY COLLATE BINARY,
                project_id TEXT NOT NULL COLLATE BINARY,
                text TEXT NOT NULL CHECK (length(text) BETWEEN 1 AND 16384),
                kind TEXT NULL CHECK (kind IS NULL OR length(kind) BETWEEN 1 AND 256),
                tags_json TEXT NOT NULL CHECK (
                    length(tags_json) <= 1048576 AND json_valid(tags_json) AND json_type(tags_json) = 'array'),
                attributes_json TEXT NOT NULL CHECK (
                    length(attributes_json) <= 1048576 AND
                    json_valid(attributes_json) AND json_type(attributes_json) = 'array'),
                FOREIGN KEY (project_id) REFERENCES projects(project_id)
                    ON UPDATE RESTRICT ON DELETE RESTRICT
            ) STRICT, WITHOUT ROWID
            """),
        new("table", "edges", """
            CREATE TABLE edges (
                edge_id TEXT PRIMARY KEY COLLATE BINARY,
                project_id TEXT NOT NULL COLLATE BINARY,
                source_node_id TEXT NOT NULL COLLATE BINARY,
                target_node_id TEXT NOT NULL COLLATE BINARY,
                relationship TEXT NOT NULL CHECK (length(relationship) BETWEEN 1 AND 1024),
                review_direction INTEGER NOT NULL CHECK (review_direction BETWEEN 0 AND 3),
                rationale TEXT NULL CHECK (rationale IS NULL OR length(rationale) <= 16384),
                tags_json TEXT NOT NULL CHECK (
                    length(tags_json) <= 1048576 AND json_valid(tags_json) AND json_type(tags_json) = 'array'),
                attributes_json TEXT NOT NULL CHECK (
                    length(attributes_json) <= 1048576 AND
                    json_valid(attributes_json) AND json_type(attributes_json) = 'array'),
                FOREIGN KEY (project_id) REFERENCES projects(project_id)
                    ON UPDATE RESTRICT ON DELETE RESTRICT,
                FOREIGN KEY (source_node_id) REFERENCES nodes(node_id)
                    ON UPDATE RESTRICT ON DELETE RESTRICT,
                FOREIGN KEY (target_node_id) REFERENCES nodes(node_id)
                    ON UPDATE RESTRICT ON DELETE RESTRICT
            ) STRICT, WITHOUT ROWID
            """),
        new("index", "ix_nodes_kind", "CREATE INDEX ix_nodes_kind ON nodes(kind COLLATE BINARY)"),
        new("index", "ix_edges_source", "CREATE INDEX ix_edges_source ON edges(source_node_id COLLATE BINARY)"),
        new("index", "ix_edges_target", "CREATE INDEX ix_edges_target ON edges(target_node_id COLLATE BINARY)"),
        new("index", "ux_edges_scope_parent", """
            CREATE UNIQUE INDEX ux_edges_scope_parent ON edges(source_node_id COLLATE BINARY)
            WHERE relationship = 'scope-parent'
            """),
        new("view", "vw_project", """
            CREATE VIEW vw_project AS
            SELECT project_id, title, purpose_node_id, created_utc, updated_utc, state_fingerprint
            FROM projects
            """),
        new("view", "vw_nodes", """
            CREATE VIEW vw_nodes AS
            SELECT node_id, project_id, text, kind, tags_json, attributes_json
            FROM nodes
            """),
        new("view", "vw_edges", """
            CREATE VIEW vw_edges AS
            SELECT edge_id, project_id, source_node_id, target_node_id, relationship,
                   review_direction, rationale, tags_json, attributes_json
            FROM edges
            """),
        new("view", "vw_scope", """
            CREATE VIEW vw_scope AS
            SELECT edge_id, source_node_id AS child_node_id, target_node_id AS parent_node_id
            FROM edges
            WHERE relationship = 'scope-parent'
            """),
        new("view", "vw_review_arcs", """
            CREATE VIEW vw_review_arcs AS
            SELECT edge_id, source_node_id AS arc_source_node_id, target_node_id AS arc_target_node_id
            FROM edges
            WHERE relationship <> 'scope-parent' AND review_direction IN (1, 3)
            UNION ALL
            SELECT edge_id, target_node_id AS arc_source_node_id, source_node_id AS arc_target_node_id
            FROM edges
            WHERE relationship <> 'scope-parent' AND review_direction IN (2, 3)
            """),
    ];

    public static string MigrationChecksum { get; } = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(";\n", Objects.Select(value => value.Sql)))))
        .ToLowerInvariant();

    public static void ApplyMigration(SqliteConnection connection, DateTimeOffset appliedUtc)
    {
        using var transaction = connection.BeginTransaction();
        foreach (var schemaObject in Objects)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = schemaObject.Sql;
            command.ExecuteNonQuery();
        }

        using (var migration = connection.CreateCommand())
        {
            migration.Transaction = transaction;
            migration.CommandText = """
                INSERT INTO schema_migrations (migration_id, checksum, applied_utc)
                VALUES ($id, $checksum, $appliedUtc)
                """;
            migration.Parameters.AddWithValue("$id", MigrationId);
            migration.Parameters.AddWithValue("$checksum", MigrationChecksum);
            migration.Parameters.AddWithValue("$appliedUtc", FormatUtc(appliedUtc));
            migration.ExecuteNonQuery();
        }

        ExecutePragma(connection, transaction, $"PRAGMA application_id = {ApplicationId}");
        ExecutePragma(connection, transaction, $"PRAGMA user_version = {CurrentVersion}");
        transaction.Commit();
    }

    public static void Verify(SqliteConnection connection)
    {
        var applicationId = ReadPragmaInt64(connection, "application_id");
        if (applicationId != ApplicationId)
        {
            throw new ProjectStorageException(
                ProjectStorageErrorCode.InvalidDatabase,
                "The file is not a ValidatedWorld SQLite project (application ID mismatch)." );
        }

        var version = ReadPragmaInt64(connection, "user_version");
        if (version != CurrentVersion)
        {
            throw new ProjectStorageException(
                ProjectStorageErrorCode.UnsupportedVersion,
                $"Unsupported SQLite project schema version {version}; expected {CurrentVersion}.");
        }

        VerifyMigration(connection);
        VerifyObjects(connection);
    }

    private static void VerifyMigration(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT migration_id, checksum FROM schema_migrations ORDER BY migration_id";
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new ProjectStorageException(
                ProjectStorageErrorCode.MigrationMismatch,
                "The required SQLite schema migration record is missing.");
        }

        var id = reader.GetString(0);
        var checksum = reader.GetString(1);
        if (reader.Read() || !StringComparer.Ordinal.Equals(id, MigrationId) ||
            !StringComparer.Ordinal.Equals(checksum, MigrationChecksum))
        {
            throw new ProjectStorageException(
                ProjectStorageErrorCode.MigrationMismatch,
                "The SQLite schema migration ID or checksum does not match this application build.");
        }
    }

    private static void VerifyObjects(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT type, name, sql
            FROM sqlite_schema
            WHERE name NOT LIKE 'sqlite_%'
            ORDER BY type COLLATE BINARY, name COLLATE BINARY
            """;
        using var reader = command.ExecuteReader();
        var actual = new Dictionary<(string Type, string Name), string>();
        while (reader.Read())
        {
            actual.Add((reader.GetString(0), reader.GetString(1)), reader.GetString(2));
        }

        if (actual.Count != Objects.Length)
        {
            throw SchemaMismatch("The SQLite schema has missing or unexpected application objects.");
        }

        foreach (var expected in Objects)
        {
            if (!actual.TryGetValue((expected.Type, expected.Name), out var sql) ||
                !StringComparer.Ordinal.Equals(NormalizeSql(sql), NormalizeSql(expected.Sql)))
            {
                throw SchemaMismatch($"SQLite schema object '{expected.Name}' does not match schema v1.");
            }
        }
    }

    private static string NormalizeSql(string sql) =>
        string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TrimEnd(';');

    private static ProjectStorageException SchemaMismatch(string message) =>
        new(ProjectStorageErrorCode.SchemaMismatch, message);

    private static long ReadPragmaInt64(SqliteConnection connection, string pragma)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {pragma}";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void ExecutePragma(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string commandText)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    internal static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private sealed record SchemaObject(string Type, string Name, string Sql);
}
