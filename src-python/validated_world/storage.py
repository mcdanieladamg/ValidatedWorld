"""SQLite provider preserving the ValidatedWorld four-table format."""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import re
import sqlite3
from typing import Callable, Iterable
from urllib.parse import quote
import uuid

from .canonical import attributes_json, state_fingerprint, tags_json
from .models import Attribute, Edge, Graph, GraphValue, GraphValueKind, Node, Operation, OperationKind, ReviewDirection
from .validation import project_graph, validate_graph
from .rules import evaluate_rules

APPLICATION_ID = 0x56574C44
SCHEMA_VERSION = 1
MIGRATION_ID = "sqlite-current-state"
MIGRATION_CHECKSUM = "269918e56207fef7f570537fcd74c8a0e0bfcac346f8045ee35fadc77e2b24f7"

SCHEMA_OBJECTS = (
    ("table", "schema_migrations", """CREATE TABLE schema_migrations (
                migration_id TEXT PRIMARY KEY,
                checksum TEXT NOT NULL CHECK (length(checksum) = 64),
                applied_utc TEXT NOT NULL
            ) STRICT, WITHOUT ROWID"""),
    ("table", "projects", """CREATE TABLE projects (
                project_id TEXT PRIMARY KEY COLLATE BINARY,
                title TEXT NOT NULL CHECK (length(title) >= 1),
                purpose_node_id TEXT NOT NULL COLLATE BINARY,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                state_fingerprint TEXT NOT NULL CHECK (length(state_fingerprint) = 64),
                FOREIGN KEY (purpose_node_id) REFERENCES nodes(node_id)
                    ON UPDATE RESTRICT ON DELETE RESTRICT DEFERRABLE INITIALLY DEFERRED
            ) STRICT, WITHOUT ROWID"""),
    ("table", "nodes", """CREATE TABLE nodes (
                node_id TEXT PRIMARY KEY COLLATE BINARY,
                project_id TEXT NOT NULL COLLATE BINARY,
                text TEXT NOT NULL CHECK (length(text) >= 1),
                kind TEXT NULL CHECK (kind IS NULL OR length(kind) >= 1),
                tags_json TEXT NOT NULL CHECK (
                    json_valid(tags_json) AND json_type(tags_json) = 'array'),
                attributes_json TEXT NOT NULL CHECK (
                    json_valid(attributes_json) AND json_type(attributes_json) = 'array'),
                FOREIGN KEY (project_id) REFERENCES projects(project_id)
                    ON UPDATE RESTRICT ON DELETE RESTRICT
            ) STRICT, WITHOUT ROWID"""),
    ("table", "edges", """CREATE TABLE edges (
                edge_id TEXT PRIMARY KEY COLLATE BINARY,
                project_id TEXT NOT NULL COLLATE BINARY,
                source_node_id TEXT NOT NULL COLLATE BINARY,
                target_node_id TEXT NOT NULL COLLATE BINARY,
                relationship TEXT NOT NULL CHECK (length(relationship) >= 1),
                review_direction INTEGER NOT NULL CHECK (review_direction BETWEEN 0 AND 3),
                rationale TEXT NULL,
                tags_json TEXT NOT NULL CHECK (
                    json_valid(tags_json) AND json_type(tags_json) = 'array'),
                attributes_json TEXT NOT NULL CHECK (
                    json_valid(attributes_json) AND json_type(attributes_json) = 'array'),
                FOREIGN KEY (project_id) REFERENCES projects(project_id)
                    ON UPDATE RESTRICT ON DELETE RESTRICT,
                FOREIGN KEY (source_node_id) REFERENCES nodes(node_id)
                    ON UPDATE RESTRICT ON DELETE RESTRICT,
                FOREIGN KEY (target_node_id) REFERENCES nodes(node_id)
                    ON UPDATE RESTRICT ON DELETE RESTRICT
            ) STRICT, WITHOUT ROWID"""),
    ("index", "ix_nodes_kind", "CREATE INDEX ix_nodes_kind ON nodes(kind COLLATE BINARY)"),
    ("index", "ix_edges_source", "CREATE INDEX ix_edges_source ON edges(source_node_id COLLATE BINARY)"),
    ("index", "ix_edges_target", "CREATE INDEX ix_edges_target ON edges(target_node_id COLLATE BINARY)"),
    ("index", "ux_edges_scope_parent", """CREATE UNIQUE INDEX ux_edges_scope_parent ON edges(source_node_id COLLATE BINARY)
            WHERE relationship = 'scope-parent'"""),
    ("view", "vw_project", """CREATE VIEW vw_project AS
            SELECT project_id, title, purpose_node_id, created_utc, updated_utc, state_fingerprint
            FROM projects"""),
    ("view", "vw_nodes", """CREATE VIEW vw_nodes AS
            SELECT node_id, project_id, text, kind, tags_json, attributes_json
            FROM nodes"""),
    ("view", "vw_edges", """CREATE VIEW vw_edges AS
            SELECT edge_id, project_id, source_node_id, target_node_id, relationship,
                   review_direction, rationale, tags_json, attributes_json
            FROM edges"""),
    ("view", "vw_scope", """CREATE VIEW vw_scope AS
            SELECT edge_id, source_node_id AS child_node_id, target_node_id AS parent_node_id
            FROM edges
            WHERE relationship = 'scope-parent'"""),
    ("view", "vw_review_arcs", """CREATE VIEW vw_review_arcs AS
            SELECT edge_id, source_node_id AS arc_source_node_id, target_node_id AS arc_target_node_id
            FROM edges
            WHERE relationship <> 'scope-parent' AND review_direction IN (1, 3)
            UNION ALL
            SELECT edge_id, target_node_id AS arc_source_node_id, source_node_id AS arc_target_node_id
            FROM edges
            WHERE relationship <> 'scope-parent' AND review_direction IN (2, 3)"""),
)


def utc_now() -> str:
    now = datetime.now(timezone.utc)
    return now.strftime("%Y-%m-%dT%H:%M:%S.") + f"{now.microsecond:06d}0+00:00"


def _connect(path: str | Path, read_only: bool = False) -> sqlite3.Connection:
    full = str(Path(path).expanduser().resolve())
    if read_only:
        connection = sqlite3.connect(f"file:{quote(full, safe='/:')}?mode=ro", uri=True, timeout=10)
    else:
        connection = sqlite3.connect(full, timeout=10)
    connection.row_factory = sqlite3.Row
    connection.execute("PRAGMA foreign_keys = ON")
    # The stock Python 3.11/SQLite 3.40 Windows build rejects the fixed
    # schema's json_valid/json_type CHECK constraints when trusted_schema is
    # OFF (the same schema is accepted by Microsoft.Data.Sqlite).  Keep
    # extension loading disabled and verify the exact schema objects before
    # reading data; trusted_schema ON is the portable standard-library
    # compatibility setting for this provider.
    connection.execute("PRAGMA trusted_schema = ON")
    connection.execute("PRAGMA recursive_triggers = OFF")
    connection.execute("PRAGMA busy_timeout = 10000")
    if read_only:
        connection.execute("PRAGMA query_only = ON")
    else:
        connection.execute("PRAGMA journal_mode = DELETE")
        connection.execute("PRAGMA synchronous = FULL")
    return connection


def _strict_json(text: str, label: str):
    def pairs(items):
        result = {}
        for key, value in items:
            if key in result:
                raise ValueError(f"{label} contains duplicate JSON member '{key}'")
            result[key] = value
        return result
    try:
        return json.loads(text, object_pairs_hook=pairs)
    except (json.JSONDecodeError, UnicodeError) as exc:
        raise ValueError(f"{label} is malformed JSON") from exc


def _exact_object(value, keys: set[str], label: str) -> dict:
    if not isinstance(value, dict) or set(value) != keys:
        raise ValueError(f"{label} has an invalid object shape")
    return value


def _decode_value(value: dict) -> GraphValue:
    value = _exact_object(value, {"kind", "text", "integer", "boolean", "instant"}, "stored graph value")
    if isinstance(value["kind"], bool) or not isinstance(value["kind"], int):
        raise ValueError("stored graph value kind must be an integer")
    if isinstance(value["integer"], bool) or not isinstance(value["integer"], int) or not isinstance(value["boolean"], bool):
        raise ValueError("stored graph value has invalid scalar slot types")
    kind = GraphValueKind(value["kind"])
    if kind in (GraphValueKind.TEXT, GraphValueKind.DECIMAL, GraphValueKind.SYMBOL):
        if not isinstance(value["text"], str) or value["integer"] != 0 or value["boolean"] or value["instant"] is not None:
            raise ValueError("stored graph value has noncanonical scalar slots")
        return GraphValue(kind, value["text"])
    if kind is GraphValueKind.INTEGER:
        if value["text"] is not None or value["boolean"] or value["instant"] is not None:
            raise ValueError("stored graph value has noncanonical scalar slots")
        return GraphValue.integer(value["integer"])
    if kind is GraphValueKind.BOOLEAN:
        if value["text"] is not None or value["integer"] != 0 or value["instant"] is not None:
            raise ValueError("stored graph value has noncanonical scalar slots")
        return GraphValue.boolean(value["boolean"])
    if value["text"] is not None or value["integer"] != 0 or value["boolean"] or not isinstance(value["instant"], str):
        raise ValueError("stored graph value has noncanonical scalar slots")
    return GraphValue.instant(value["instant"])


def _decode_attributes(text: str) -> tuple[Attribute, ...]:
    values = _strict_json(text, "stored attributes")
    if not isinstance(values, list):
        raise ValueError("stored attributes must be an array")
    return tuple(Attribute(_exact_object(item, {"name", "value"}, "stored attribute")["name"], _decode_value(item["value"])) for item in values)


def _decode_tags(text: str) -> tuple[str, ...]:
    values = _strict_json(text, "stored tags")
    if not isinstance(values, list) or any(not isinstance(item, str) for item in values):
        raise ValueError("stored tags must be an array of strings")
    return tuple(values)


def _node(row: sqlite3.Row) -> Node:
    return Node(row["node_id"], row["text"], row["kind"], _decode_tags(row["tags_json"]), _decode_attributes(row["attributes_json"]))


def _edge(row: sqlite3.Row) -> Edge:
    return Edge(row["edge_id"], row["source_node_id"], row["target_node_id"], row["relationship"], ReviewDirection(row["review_direction"]), row["rationale"], _decode_tags(row["tags_json"]), _decode_attributes(row["attributes_json"]))


@dataclass(frozen=True)
class StoredProject:
    path: str
    graph: Graph
    state_fingerprint: str
    created_utc: str
    updated_utc: str


class ProjectStore:
    def __init__(self, write_fault: Callable[[str], None] | None = None):
        self._write_fault = write_fault

    def _fault(self, stage: str) -> None:
        if self._write_fault is not None:
            self._write_fault(stage)

    @staticmethod
    def _publish_no_overwrite(temporary: str, destination: str) -> None:
        """Atomically publish a same-directory file without replacing a winner."""
        os.link(temporary, destination)
        Path(temporary).unlink()

    def initialize(self, path: str, graph: Graph) -> StoredProject:
        full = str(Path(path).expanduser().resolve())
        if Path(full).exists():
            raise FileExistsError(f"destination already exists: {full}")
        validation = validate_graph(graph)
        if not validation.is_valid:
            raise ValueError(validation.diagnostics[0].message)
        Path(full).parent.mkdir(parents=True, exist_ok=True)
        temporary = full + f".{uuid.uuid4().hex}.initializing.tmp"
        try:
            connection = _connect(temporary)
            try:
                self._apply_schema(connection)
                now = utc_now()
                self._insert_graph(connection, graph, now)
                connection.commit()
                result = self._load_connection(connection, temporary)
            finally:
                connection.close()
            self._publish_no_overwrite(temporary, full)
            return StoredProject(full, result.graph, result.state_fingerprint, result.created_utc, result.updated_utc)
        finally:
            if Path(temporary).exists():
                Path(temporary).unlink()

    def load(self, path: str) -> StoredProject:
        full = str(Path(path).expanduser().resolve())
        if not Path(full).is_file():
            raise FileNotFoundError(full)
        connection = _connect(full, True)
        try:
            return self._load_connection(connection, full)
        finally:
            connection.close()

    def verify(self, path: str) -> dict:
        project = self.load(path)
        rules = evaluate_rules(project.graph)
        return {
            "path": project.path, "isValid": rules.is_valid, "stateFingerprint": project.state_fingerprint,
            "nodeCount": len(project.graph.nodes), "edgeCount": len(project.graph.edges),
            "checks": ["application-id", "schema-version", "migration-checksum", "schema-objects", "sqlite-integrity", "foreign-keys", "strict-row-mapping", "graph-validation", "full-graph-rules", "state-fingerprint"],
            "ruleStatus": rules.status, "ruleDiagnostics": [{"ruleId": item.rule_id, "status": item.status, "message": item.message, "offenderIds": list(item.offender_ids), "omittedCount": item.omitted_count} for item in rules.diagnostics],
        }

    def status(self, path: str) -> dict:
        project = self.load(path)
        connection = _connect(project.path, True)
        try:
            sqlite_version = connection.execute("select sqlite_version()").fetchone()[0]
        finally:
            connection.close()
        return {"path": project.path, "projectId": project.graph.project_id, "title": project.graph.title, "purposeNodeId": project.graph.purpose_node_id, "nodeCount": len(project.graph.nodes), "edgeCount": len(project.graph.edges), "stateFingerprint": project.state_fingerprint, "sqliteVersion": sqlite_version}

    def backup(self, source: str, destination: str) -> StoredProject:
        source_full = str(Path(source).expanduser().resolve())
        destination_full = str(Path(destination).expanduser().resolve())
        project = self.load(source_full)
        if Path(destination_full).exists():
            raise FileExistsError(destination_full)
        Path(destination_full).parent.mkdir(parents=True, exist_ok=True)
        temp = destination_full + f".{uuid.uuid4().hex}.backup.tmp"
        try:
            src = _connect(source_full, True)
            dst = _connect(temp)
            try:
                src.backup(dst)
                dst.commit()
            finally:
                src.close(); dst.close()
            copied = self.load(temp)
            if copied.state_fingerprint != project.state_fingerprint or copied.graph != project.graph:
                raise ValueError("backup verification did not match the source project")
            self._publish_no_overwrite(temp, destination_full)
            return self.load(destination_full)
        finally:
            if Path(temp).exists():
                Path(temp).unlink()

    def export_sql(self, path: str) -> str:
        """Return SQLite's schema/data dump without changing the database."""
        project = self.load(path)
        connection = _connect(project.path, True)
        try:
            return "\n".join(connection.iterdump()) + "\n"
        finally:
            connection.close()

    def write(self, path: str, project_id: str, base_fingerprint: str, proposed_fingerprint: str, operations: Iterable[Operation]) -> StoredProject:
        full = str(Path(path).expanduser().resolve())
        operations = tuple(operations)
        preflight = self.load(full)
        if preflight.graph.project_id != project_id or preflight.state_fingerprint != base_fingerprint:
            raise RuntimeError("stale-base-fingerprint")
        proposed, _ = project_graph(preflight.graph, operations)
        validation = validate_graph(proposed)
        if not validation.is_valid:
            raise ValueError(validation.diagnostics[0].message)
        rules = evaluate_rules(proposed)
        if not rules.is_valid:
            raise ValueError(rules.diagnostics[0].message)
        expected = state_fingerprint(proposed)
        if expected != proposed_fingerprint:
            raise RuntimeError("proposed-fingerprint-mismatch")
        connection = _connect(full)
        try:
            connection.execute("BEGIN IMMEDIATE")
            self._fault("transaction-begun")
            current = self._load_connection(connection, full)
            if current.graph.project_id != project_id or current.state_fingerprint != base_fingerprint:
                connection.rollback(); raise RuntimeError("stale-base-fingerprint")
            for operation in operations:
                if operation.entity_kind.name == "EDGE" and operation.kind in (OperationKind.REMOVE, OperationKind.REPLACE):
                    connection.execute("delete from edges where edge_id = ?", (operation.entity_id,))
            self._fault("edges-removed")
            for operation in operations:
                if operation.entity_kind.name == "NODE" and operation.kind is OperationKind.REMOVE:
                    connection.execute("delete from nodes where node_id = ?", (operation.entity_id,))
            self._fault("nodes-removed")
            for operation in operations:
                if operation.entity_kind.name == "NODE" and operation.kind is not OperationKind.REMOVE:
                    node = operation.node
                    if operation.kind is OperationKind.ADD:
                        connection.execute("insert into nodes(node_id,project_id,text,kind,tags_json,attributes_json) values(?,?,?,?,?,?)", (node.id, project_id, node.text, node.kind, tags_json(node.tags), attributes_json(node.attributes)))
                    else:
                        connection.execute("update nodes set text=?,kind=?,tags_json=?,attributes_json=? where node_id=? and project_id=?", (node.text, node.kind, tags_json(node.tags), attributes_json(node.attributes), node.id, project_id))
            self._fault("nodes-written")
            for operation in operations:
                if operation.entity_kind.name == "EDGE" and operation.kind is not OperationKind.REMOVE:
                    edge = operation.edge
                    connection.execute("insert into edges(edge_id,project_id,source_node_id,target_node_id,relationship,review_direction,rationale,tags_json,attributes_json) values(?,?,?,?,?,?,?,?,?)", (edge.id, project_id, edge.source, edge.target, edge.relationship, int(edge.review_direction), edge.rationale, tags_json(edge.tags), attributes_json(edge.attributes)))
            self._fault("edges-written")
            connection.execute("update projects set updated_utc=?,state_fingerprint=? where project_id=?", (utc_now(), expected, project_id))
            self._fault("metadata-written")
            if connection.execute("pragma foreign_key_check").fetchone() is not None:
                raise ValueError("SQLite foreign-key check failed before commit")
            self._fault("before-commit")
            connection.commit()
            return self.load(full)
        except Exception:
            connection.rollback()
            raise
        finally:
            connection.close()

    @staticmethod
    def _apply_schema(connection: sqlite3.Connection) -> None:
        # Python's SQLite build marks json_valid/json_type as non-innocuous
        # schema functions.  The fixed ValidatedWorld schema legitimately uses
        # them in CHECK constraints, so make the compatibility policy explicit
        # during this short migration step. Existing databases are opened with
        # the same documented standard-library compatibility policy.
        connection.execute("pragma trusted_schema = ON")
        connection.execute("pragma application_id = 1448561732")
        connection.execute("pragma user_version = 1")
        for _, _, sql in SCHEMA_OBJECTS:
            connection.execute(sql)
        connection.execute("insert into schema_migrations(migration_id,checksum,applied_utc) values(?,?,?)", (MIGRATION_ID, MIGRATION_CHECKSUM, utc_now()))
        connection.execute("pragma trusted_schema = ON")

    @staticmethod
    def _insert_graph(connection: sqlite3.Connection, graph: Graph, now: str) -> None:
        connection.execute("pragma defer_foreign_keys = on")
        fingerprint = state_fingerprint(graph)
        connection.execute("insert into projects(project_id,title,purpose_node_id,created_utc,updated_utc,state_fingerprint) values(?,?,?,?,?,?)", (graph.project_id, graph.title, graph.purpose_node_id, now, now, fingerprint))
        for node in graph.nodes:
            connection.execute("insert into nodes(node_id,project_id,text,kind,tags_json,attributes_json) values(?,?,?,?,?,?)", (node.id, graph.project_id, node.text, node.kind, tags_json(node.tags), attributes_json(node.attributes)))
        for edge in graph.edges:
            connection.execute("insert into edges(edge_id,project_id,source_node_id,target_node_id,relationship,review_direction,rationale,tags_json,attributes_json) values(?,?,?,?,?,?,?,?,?)", (edge.id, graph.project_id, edge.source, edge.target, edge.relationship, int(edge.review_direction), edge.rationale, tags_json(edge.tags), attributes_json(edge.attributes)))

    @staticmethod
    def _load_connection(connection: sqlite3.Connection, path: str) -> StoredProject:
        if connection.execute("pragma application_id").fetchone()[0] != APPLICATION_ID:
            raise ValueError("application ID mismatch")
        if connection.execute("pragma user_version").fetchone()[0] != SCHEMA_VERSION:
            raise ValueError("unsupported schema version")
        migration_rows = connection.execute("select migration_id,checksum,applied_utc from schema_migrations order by migration_id").fetchall()
        if len(migration_rows) != 1 or migration_rows[0][0] != MIGRATION_ID or migration_rows[0][1] != MIGRATION_CHECKSUM:
            raise ValueError("schema migration checksum mismatch")
        if not isinstance(migration_rows[0][2], str) or not re.fullmatch(r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}\+00:00", migration_rows[0][2]):
            raise ValueError("schema migration timestamp is malformed")
        actual_objects = {(row[0], row[1]): " ".join(row[2].split()).rstrip(";") for row in connection.execute("select type,name,sql from sqlite_schema where name not like 'sqlite_%' order by type collate binary,name collate binary")}
        expected_objects = {(kind, name): " ".join(sql.split()).rstrip(";") for kind, name, sql in SCHEMA_OBJECTS}
        if actual_objects != expected_objects:
            raise ValueError("SQLite schema objects do not match the current fixed schema")
        if connection.execute("pragma integrity_check").fetchone()[0] != "ok":
            raise ValueError("SQLite integrity check failed")
        if connection.execute("pragma foreign_key_check").fetchone() is not None:
            raise ValueError("SQLite foreign-key check failed")
        projects = connection.execute("select project_id,title,purpose_node_id,created_utc,updated_utc,state_fingerprint from projects").fetchall()
        if len(projects) != 1:
            raise ValueError("database must contain exactly one project row")
        project = projects[0]
        for name in ("created_utc", "updated_utc"):
            if not isinstance(project[name], str) or not re.fullmatch(r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}\+00:00", project[name]):
                raise ValueError(f"project {name} is malformed")
        nodes = tuple(_node(row) for row in connection.execute("select * from nodes order by node_id collate binary"))
        edges = tuple(_edge(row) for row in connection.execute("select * from edges order by edge_id collate binary"))
        graph = Graph(project["project_id"], project["title"], project["purpose_node_id"], nodes, edges)
        validation = validate_graph(graph)
        if not validation.is_valid:
            raise ValueError("stored graph is invalid: " + validation.diagnostics[0].message)
        calculated = state_fingerprint(graph)
        if calculated != project["state_fingerprint"]:
            raise ValueError("state fingerprint mismatch")
        return StoredProject(path, graph, calculated, project["created_utc"], project["updated_utc"])
