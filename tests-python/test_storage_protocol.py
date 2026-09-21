import sqlite3
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parents[1] / "src-python"))

from validated_world.application import sample_graph
from validated_world.canonical import state_fingerprint
from validated_world.models import Attribute, Edge, EntityKind, Graph, GraphValue, Node, Operation, OperationKind
from validated_world.protocol import graph_dto, graph_from_dto, json_loads_strict, operation_from_dto
from validated_world.storage import ProjectStore
from validated_world.validation import project_graph


class ProtocolTests(unittest.TestCase):
    def test_graph_round_trip_is_strict_and_preserves_typed_values(self):
        graph = sample_graph()
        self.assertEqual(graph_from_dto(graph_dto(graph)), graph)
        value = graph_dto(graph)
        value["extra"] = 1
        with self.assertRaisesRegex(ValueError, "unknown member"):
            graph_from_dto(value)

    def test_public_operation_rejects_unknown_members_numeric_enums_and_noncanonical_slots(self):
        base = {
            "kind": "remove",
            "entityKind": "node",
            "entityId": "purpose",
            "node": None,
            "edge": None,
        }
        self.assertEqual(operation_from_dto(base).entity_id, "purpose")
        with self.assertRaises(ValueError):
            operation_from_dto({**base, "kind": 2})
        with self.assertRaises(ValueError):
            operation_from_dto({**base, "extra": True})

        node = {
            "kind": "replace",
            "entityKind": "node",
            "entityId": "purpose",
            "node": {
                "id": "purpose",
                "text": "Purpose",
                "kind": None,
                "tags": [],
                "attributes": [{"name": "answer", "value": {"kind": "boolean", "text": None, "integer": 1, "boolean": True, "instant": None}}],
            },
            "edge": None,
        }
        with self.assertRaisesRegex(ValueError, "noncanonical"):
            operation_from_dto(node)

    def test_duplicate_json_members_are_rejected(self):
        with self.assertRaisesRegex(ValueError, "duplicate JSON member"):
            json_loads_strict('{"version":1,"version":1}')


class StorageTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)

    def initialize(self, name="project with spaces.vw.db", graph=None, store=None):
        path = self.root / name
        (store or ProjectStore()).initialize(path, graph or sample_graph())
        return path

    def replacement(self, path, text="Changed purpose"):
        project = ProjectStore().load(path)
        current = next(item for item in project.graph.nodes if item.id == project.graph.purpose_node_id)
        changed = Node(current.id, text, current.kind, current.tags, current.attributes)
        operation = Operation(OperationKind.REPLACE, EntityKind.NODE, changed.id, node=changed)
        proposed, operations = project_graph(project.graph, (operation,))
        return project, proposed, operations

    def test_read_only_open_with_spaces_and_unicode_does_not_change_bytes(self):
        purpose = Node("purpose", "Préserver — 研究計画 🌍", "purpose", (), (Attribute("answer", GraphValue.boolean(True)),))
        graph = Graph("unicode", "Plan — 研究", purpose.id, (purpose,), ())
        path = self.initialize(graph=graph)
        before = path.read_bytes()
        loaded = ProjectStore().load(path)
        verified = ProjectStore().verify(path)
        self.assertEqual(loaded.graph, graph)
        self.assertTrue(verified["isValid"])
        self.assertEqual(path.read_bytes(), before)
        self.assertEqual(list(path.parent.iterdir()), [path])

    def test_header_schema_migration_and_strict_rows_are_verified(self):
        mutations = (
            ("version.vw.db", "pragma user_version = 2", "unsupported schema version"),
            ("migration.vw.db", "update schema_migrations set checksum = '0000000000000000000000000000000000000000000000000000000000000000'", "migration checksum"),
            ("application.vw.db", "pragma application_id = 0", "application ID"),
            ("schema.vw.db", "create index unexpected_index on nodes(text)", "schema objects"),
            ("tags.vw.db", "update nodes set tags_json='[\"duplicate\",\"duplicate\"]' where node_id='purpose'", "duplicate tag"),
            ("attributes.vw.db", "update nodes set attributes_json='[{\"name\":\"x\",\"value\":{\"kind\":0,\"text\":\"x\",\"integer\":0,\"boolean\":false,\"instant\":null,\"extra\":1}}]' where node_id='purpose'", "invalid object shape"),
        )
        for name, sql, message in mutations:
            with self.subTest(name=name):
                path = self.initialize(name)
                connection = sqlite3.connect(path)
                try:
                    connection.execute(sql)
                    connection.commit()
                finally:
                    connection.close()
                with self.assertRaisesRegex(ValueError, message):
                    ProjectStore().load(path)

    def test_corrupt_file_and_malformed_stored_rows_are_rejected(self):
        corrupt = self.root / "corrupt.vw.db"
        corrupt.write_bytes(b"\x01\x02\x03\x04")
        with self.assertRaises(sqlite3.DatabaseError):
            ProjectStore().load(corrupt)

        mutations = (
            ("migration-time.vw.db", "update schema_migrations set applied_utc='not-a-time'", "migration timestamp"),
            ("project-time.vw.db", "update projects set updated_utc='not-a-time'", "updated_utc"),
            ("fingerprint.vw.db", "update projects set state_fingerprint='0000000000000000000000000000000000000000000000000000000000000000'", "fingerprint"),
            ("direction.vw.db", "update edges set review_direction=99 where edge_id='battery-requires-runtime'", None),
            ("tags-json.vw.db", "update nodes set tags_json='not-json' where node_id='purpose'", None),
        )
        for name, sql, message in mutations:
            with self.subTest(name=name):
                path = self.initialize(name)
                connection = sqlite3.connect(path)
                try:
                    connection.execute("pragma ignore_check_constraints=on")
                    connection.execute(sql)
                    connection.commit()
                finally:
                    connection.close()
                expected = self.assertRaisesRegex(ValueError, message) if message else self.assertRaises((ValueError, TypeError, sqlite3.DatabaseError))
                with expected:
                    ProjectStore().load(path)

    def test_backup_is_verified_and_never_overwrites(self):
        source = self.initialize("source.vw.db")
        destination = self.root / "backup folder" / "backup.vw.db"
        backup = ProjectStore().backup(source, destination)
        self.assertEqual(backup.graph, ProjectStore().load(source).graph)
        with self.assertRaises(FileExistsError):
            ProjectStore().backup(source, destination)
        self.assertFalse(any("backup.tmp" in item.name for item in destination.parent.iterdir()))

    def test_initialization_rejects_existing_and_invalid_graphs_without_partial_files(self):
        existing = self.initialize("existing.vw.db")
        with self.assertRaises(FileExistsError): ProjectStore().initialize(existing, sample_graph())
        invalid_path = self.root / "invalid.vw.db"
        invalid = Graph("invalid", "Invalid", "purpose", (Node("purpose", "Purpose"), Node("orphan", "Orphan")), ())
        with self.assertRaises(ValueError): ProjectStore().initialize(invalid_path, invalid)
        self.assertFalse(invalid_path.exists())
        self.assertFalse(any("initializing.tmp" in item.name for item in self.root.iterdir()))

    def test_large_values_round_trip_without_product_size_ceilings(self):
        text = "x" * (2 * 1024 * 1024)
        identifier = "p" * 1024
        purpose = Node(identifier, text, "k" * 1024, ("t" * 1024,), (Attribute("a" * 1024, GraphValue.text(text)),))
        graph = Graph("i" * 1024, text, identifier, (purpose,), ())
        path = self.initialize("large.vw.db", graph)
        self.assertEqual(ProjectStore().load(path).graph, graph)

    def test_schema_views_foreign_keys_and_logical_fingerprint_are_stable(self):
        path = self.initialize("schema.vw.db")
        before = ProjectStore().load(path)
        connection = sqlite3.connect(path)
        try:
            connection.execute("pragma foreign_keys=on")
            self.assertEqual(connection.execute("pragma application_id").fetchone()[0], 0x56574C44)
            self.assertEqual(connection.execute("pragma user_version").fetchone()[0], 1)
            self.assertEqual(connection.execute("select count(*) from pragma_table_list where strict=1").fetchone()[0], 4)
            self.assertEqual(connection.execute("select count(*) from vw_nodes").fetchone()[0], 13)
            self.assertEqual(connection.execute("select count(*) from vw_edges").fetchone()[0], 17)
            self.assertEqual(connection.execute("select count(*) from vw_scope").fetchone()[0], 12)
            self.assertEqual(connection.execute("select count(*) from vw_review_arcs").fetchone()[0], 5)
            with self.assertRaises(sqlite3.IntegrityError):
                connection.execute("insert into edges(edge_id,project_id,source_node_id,target_node_id,relationship,review_direction,rationale,tags_json,attributes_json) values('invalid','technical-project','missing','purpose','requires',1,null,'[]','[]')")
            connection.rollback()
            connection.execute("begin")
            connection.execute("pragma defer_foreign_keys=on")
            connection.execute("create temp table node_copy as select * from nodes")
            connection.execute("create temp table edge_copy as select * from edges")
            connection.execute("delete from edges"); connection.execute("delete from nodes")
            connection.execute("insert into nodes select * from node_copy order by node_id collate binary desc")
            connection.execute("insert into edges select * from edge_copy order by edge_id collate binary desc")
            connection.commit()
        finally:
            connection.close()
        after = ProjectStore().load(path)
        self.assertEqual((after.graph, after.state_fingerprint), (before.graph, before.state_fingerprint))

    def test_every_injected_write_boundary_rolls_back_all_rows(self):
        for stage in ("transaction-begun", "edges-removed", "nodes-removed", "nodes-written", "edges-written", "metadata-written", "before-commit"):
            with self.subTest(stage=stage):
                path = self.initialize(stage + ".vw.db")
                before = ProjectStore().load(path)
                project, proposed, operations = self.replacement(path, stage)

                def fault(actual):
                    if actual == stage:
                        raise RuntimeError("injected-write-failure")

                with self.assertRaisesRegex(RuntimeError, "injected-write-failure"):
                    ProjectStore(fault).write(path, project.graph.project_id, project.state_fingerprint, state_fingerprint(proposed), operations)
                after = ProjectStore().load(path)
                self.assertEqual(after.graph, before.graph)
                self.assertEqual(after.state_fingerprint, before.state_fingerprint)

    def test_stale_base_is_rejected_and_same_proposal_can_retry_after_failure(self):
        path = self.initialize()
        project, proposed, operations = self.replacement(path)
        expected = state_fingerprint(proposed)
        with self.assertRaisesRegex(RuntimeError, "injected"):
            ProjectStore(lambda stage: (_ for _ in ()).throw(RuntimeError("injected")) if stage == "before-commit" else None).write(path, project.graph.project_id, project.state_fingerprint, expected, operations)
        written = ProjectStore().write(path, project.graph.project_id, project.state_fingerprint, expected, operations)
        self.assertEqual(written.state_fingerprint, expected)
        with self.assertRaisesRegex(RuntimeError, "stale-base"):
            ProjectStore().write(path, project.graph.project_id, project.state_fingerprint, expected, operations)

    def test_rule_invalid_baseline_opens_and_can_be_repaired(self):
        purpose = Node("purpose", "Purpose", "purpose")
        claim = Node("claim", "Claim", "claim")
        rule = Node(
            "rule",
            "Every claim must be confirmed.",
            "validation-rule",
            ("rule:active",),
            (
                Attribute("rule:version", GraphValue.integer(1)),
                Attribute("rule:expression", GraphValue.text('{"all":{"set":{"nodes":{"kind":"claim"}},"condition":{"hasTag":"confirmed"}}}')),
            ),
        )
        from validated_world.models import Edge
        graph = Graph("repair", "Repair", purpose.id, (purpose, claim, rule), (Edge("claim-scope", claim.id, purpose.id, "scope-parent"), Edge("rule-scope", rule.id, purpose.id, "scope-parent")))
        path = self.initialize("repair.vw.db", graph)
        store = ProjectStore()
        baseline = store.load(path)
        self.assertFalse(store.verify(path)["isValid"])
        fixed = Node(claim.id, claim.text, claim.kind, ("confirmed",))
        operation = Operation(OperationKind.REPLACE, EntityKind.NODE, fixed.id, node=fixed)
        proposed, operations = project_graph(graph, (operation,))
        store.write(path, graph.project_id, baseline.state_fingerprint, state_fingerprint(proposed), operations)
        self.assertTrue(store.verify(path)["isValid"])

    def test_disposable_copies_of_every_tracked_database_can_continue_under_python(self):
        repository = Path(__file__).parents[1]
        tracked = [repository / "ValidatedWorld.Blueprint.vw.db", *sorted((repository / "samples").rglob("*.vw.db"))]
        self.assertGreaterEqual(len(tracked), 2)
        for index, source in enumerate(tracked):
            with self.subTest(source=str(source.relative_to(repository))):
                destination = self.root / f"tracked-{index}.vw.db"
                store = ProjectStore()
                baseline = store.backup(source, destination)
                current = next(item for item in baseline.graph.nodes if item.id == baseline.graph.purpose_node_id)
                changed = Node(current.id, current.text + " [continuation test]", current.kind, current.tags, current.attributes)
                operation = Operation(OperationKind.REPLACE, EntityKind.NODE, changed.id, node=changed)
                proposed, operations = project_graph(baseline.graph, (operation,))
                written = store.write(destination, baseline.graph.project_id, baseline.state_fingerprint, state_fingerprint(proposed), operations)
                self.assertEqual(store.load(destination).state_fingerprint, written.state_fingerprint)


if __name__ == "__main__":
    unittest.main()
