import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).parents[1] / "src"))

from validated_world.application import Application, sample_graph
from validated_world.models import Attribute, Edge, EntityKind, Graph, GraphValue, Node, Operation, OperationKind
from validated_world.protocol import operation_dto
from validated_world.storage import ProjectStore
from validated_world.cli import DOMAIN, SUCCESS, USAGE, direct_command


class SkipDependenciesTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.path = Path(self.temporary.name) / "cleanup project.vw.db"
        base = sample_graph()
        rule = Node("unrelated-rule", "Purpose must exist", "validation-rule", ("rule:active",), (
            Attribute("rule:version", GraphValue.integer(1)),
            Attribute("rule:expression", GraphValue.text('{"exists":{"nodes":{"id":"purpose"}}}')),
        ))
        self.graph = Graph(base.project_id, base.title, base.purpose_node_id, (*base.nodes, rule),
                           (*base.edges, Edge("rule-parent", rule.id, "purpose", "scope-parent")))
        ProjectStore().initialize(self.path, self.graph)
        self.app = Application()

    def begin(self):
        return self.app.begin(str(self.path), self.graph.project_id, "tester", "Clean up misplaced facts")

    @staticmethod
    def replace(node_id, text, kind=None):
        return Operation(OperationKind.REPLACE, EntityKind.NODE, node_id, node=Node(node_id, text, kind))

    @staticmethod
    def present(session, limit=2):
        cursor = None
        items = []
        while True:
            page = session.preview(limit, cursor)
            items.extend(page["reviewPage"]["items"])
            cursor = page["reviewPage"]["nextCursor"]
            if cursor is None:
                return items

    def cleanup_operations(self):
        return (
            self.replace("scope-power", "Power behavior without misplaced battery assumptions", "scope"),
            Operation(OperationKind.ADD, EntityKind.NODE, "battery-detail", node=Node("battery-detail", "Battery assumptions", "scope")),
            Operation(OperationKind.ADD, EntityKind.EDGE, "battery-detail-parent", edge=Edge("battery-detail-parent", "battery-detail", "scope-power", "scope-parent")),
            Operation(OperationKind.REPLACE, EntityKind.EDGE, "battery-scope-parent", edge=Edge("battery-scope-parent", "battery-assumption", "battery-detail", "scope-parent")),
            Operation(OperationKind.REMOVE, EntityKind.EDGE, "battery-informs-power-anchor"),
        )

    def test_high_level_edit_has_only_its_before_after_and_never_traverses_review_tree(self):
        session = self.begin()
        with patch("validated_world.validation.GraphIndex.upstream", side_effect=AssertionError("scope review traversed")), \
             patch("validated_world.validation.GraphIndex.descendants", side_effect=AssertionError("descendants traversed")):
            session = self.app.apply(session.reference(), (self.replace("purpose", "Cleaned project purpose"),), skip_dependencies=True)
            items = self.present(session)
        self.assertEqual(session.snapshot()["skipDependencies"], True)
        self.assertEqual([n["nodeId"] for n in session.affected_nodes], ["purpose"])
        self.assertEqual(session.scope_context, [])
        self.assertEqual(session.dispositions, {})
        self.assertEqual(len(items), 1)
        self.assertEqual(items[0]["kind"], "operation")
        self.assertEqual(items[0]["currentNode"]["text"], next(node.text for node in self.graph.nodes if node.id == "purpose"))
        self.assertEqual(items[0]["proposedNode"]["text"], "Cleaned project purpose")
        self.assertEqual(self.app.agent_write(session.reference())["status"], "written")

    def test_batch_cleanup_writes_only_requested_nodes_and_edges(self):
        session = self.app.apply(self.begin().reference(), self.cleanup_operations(), skip_dependencies=True)
        self.assertTrue(session.readiness()["isReady"])
        items = self.present(session, 1)
        self.assertEqual(len(items), 5)
        self.assertTrue(all(item["kind"] == "operation" for item in items))
        evidence = {item["operation"]["entityId"]: item for item in items}
        self.assertIsNone(evidence["battery-detail"]["currentNode"])
        self.assertIsNone(evidence["battery-informs-power-anchor"]["proposedEdge"])
        self.assertEqual(evidence["battery-scope-parent"]["currentEdge"]["target"], "scope-power")
        self.assertEqual(evidence["battery-scope-parent"]["proposedEdge"]["target"], "battery-detail")
        self.assertEqual(self.app.write(session.reference())["status"], "written")
        graph = ProjectStore().load(self.path).graph
        edited = {op.entity_id for op in self.cleanup_operations()}
        before = {n.id: n for n in (*self.graph.nodes, *self.graph.edges)}
        after = {n.id: n for n in (*graph.nodes, *graph.edges)}
        self.assertEqual({eid for eid in before.keys() | after.keys() if before.get(eid) != after.get(eid)}, edited)
        self.assertTrue(ProjectStore().verify(self.path)["isValid"])

    def test_edge_only_update_does_not_pull_endpoints_into_review(self):
        operation = Operation(OperationKind.REMOVE, EntityKind.EDGE, "battery-requires-runtime")
        session = self.app.apply(self.begin().reference(), (operation,), skip_dependencies=True)
        self.assertEqual(session.affected_nodes, [])
        self.assertEqual(session.scope_context, [])
        items = self.present(session)
        self.assertEqual(len(items), 1)
        self.assertEqual(items[0]["currentEdge"]["id"], operation.entity_id)
        self.assertEqual(self.app.write(session.reference())["status"], "written")

    def test_explicit_node_and_incident_edge_removal_is_atomic(self):
        incident = [edge for edge in self.graph.edges if "battery-assumption" in (edge.source, edge.target)]
        operations = (*[Operation(OperationKind.REMOVE, EntityKind.EDGE, edge.id) for edge in incident],
                      Operation(OperationKind.REMOVE, EntityKind.NODE, "battery-assumption"))
        session = self.app.apply(self.begin().reference(), operations, skip_dependencies=True)
        evidence = self.present(session)
        removed = next(item for item in evidence if item["operation"]["entityId"] == "battery-assumption")
        self.assertIsNone(removed["proposedNode"])
        self.assertEqual(self.app.write(session.reference())["status"], "written")

    def test_missing_or_partial_preview_blocks_and_failure_rolls_back_whole_batch(self):
        def fault(stage):
            if stage == "before-commit":
                raise RuntimeError("injected rollback check")
        self.app = Application(ProjectStore(fault))
        before = self.path.read_bytes()
        session = self.app.apply(self.begin().reference(), self.cleanup_operations(), skip_dependencies=True)
        self.assertEqual(self.app.write(session.reference())["status"], "reviewNotReady")
        session.preview(1)
        self.assertEqual(self.app.agent_write(session.reference())["status"], "reviewNotReady")
        self.present(session)
        self.assertEqual(self.app.write(session.reference())["status"], "failed")
        self.assertEqual(self.path.read_bytes(), before)
        self.assertTrue(self.app.session(session.reference()).skip_dependencies)

    def test_structural_and_rule_failures_still_block_writes(self):
        before = self.path.read_bytes()
        for operations in (
            (Operation(OperationKind.REMOVE, EntityKind.EDGE, "battery-scope-parent"),),
            (Operation(OperationKind.REMOVE, EntityKind.NODE, "battery-assumption"),),
            (Operation(OperationKind.REPLACE, EntityKind.NODE, "unrelated-rule", node=Node(
                "unrelated-rule", "Impossible count", "validation-rule", ("rule:active",), (
                    Attribute("rule:version", GraphValue.integer(1)),
                    Attribute("rule:expression", GraphValue.text('{"count":{"set":{"nodes":{"id":"purpose"}},"compare":"eq","value":0}}')),
                ))),),
        ):
            with self.subTest(operations=operations):
                session = self.app.apply(self.begin().reference(), operations, skip_dependencies=True)
                self.present(session)
                self.assertFalse(session.readiness()["isReady"])
                self.assertEqual(self.app.write(session.reference())["status"], "reviewNotReady")
                self.assertEqual(self.path.read_bytes(), before)
                self.app.discard(session.reference())

    def test_stale_database_rejects_skip_write(self):
        session = self.app.apply(self.begin().reference(), (self.replace("purpose", "Skipped edit"),), skip_dependencies=True)
        self.present(session)
        other = Application()
        alternative = other.begin(str(self.path), self.graph.project_id, "other", "Concurrent cleanup")
        alternative = other.apply(alternative.reference(), (self.replace("scope-privacy", "Concurrent edit", "scope"),), skip_dependencies=True)
        self.present(alternative)
        self.assertEqual(other.write(alternative.reference())["status"], "written")
        concurrent = self.path.read_bytes()
        self.assertEqual(self.app.write(session.reference())["status"], "stale")
        self.assertEqual(self.path.read_bytes(), concurrent)

    def test_mode_is_bound_and_defaults_back_to_normal_on_apply_patch_expand_and_begin(self):
        operations = (self.replace("purpose", "Updated purpose"),)
        for action in ("apply", "patch", "expand"):
            with self.subTest(action=action):
                session = self.app.apply(self.begin().reference(), operations, skip_dependencies=True)
                ref = session.reference()
                session.preview(1)
                if action == "apply":
                    session = self.app.apply(ref, operations)
                elif action == "patch":
                    session = self.app.apply(ref, (self.replace("purpose", "Patched purpose"),), patch=True)
                else:
                    session = self.app.expand(ref)
                self.assertFalse(session.skip_dependencies)
                self.assertGreater(len(session.affected_nodes), 1)
                self.assertFalse(session.preview_seen)
                self.assertGreater(session.readiness()["pendingCount"], 0)
                with self.assertRaisesRegex(ValueError, "stale-"):
                    self.app.write(ref)
                self.app.discard(session.reference())
        self.assertFalse(self.begin().skip_dependencies)

    def test_skip_mode_rejects_packet_chain_and_supplemental_context(self):
        session = self.app.apply(self.begin().reference(), (self.replace("purpose", "Updated"),), skip_dependencies=True)
        for call in (
            lambda: self.app.review_plan(session.reference()),
            lambda: self.app.review_context(session.reference(), ["unrelated-rule"]),
            lambda: self.app.review(session.reference(), [], []),
        ):
            with self.assertRaisesRegex(ValueError, "Dependency review is skipped"):
                call()
        self.assertEqual(len(self.present(session)), 1)

    def test_empty_skip_and_failed_apply_preserve_live_reference(self):
        session = self.begin()
        ref = session.reference()
        with self.assertRaisesRegex(ValueError, "nonempty"):
            self.app.apply(ref, (), skip_dependencies=True)
        with self.assertRaisesRegex(ValueError, "cannot replace missing"):
            self.app.apply(ref, (self.replace("missing", "No such node"),), skip_dependencies=True)
        self.assertEqual(session.reference(), ref)
        self.assertFalse(session.skip_dependencies)

    def test_direct_command_requires_flag_and_prints_only_batch_edits_before_write(self):
        batch = Path(self.temporary.name) / "operation batch.json"
        batch.write_text(json.dumps({"operations": [operation_dto(op) for op in self.cleanup_operations()]}), encoding="utf-8")
        before = self.path.read_bytes()
        out, err = io.StringIO(), io.StringIO()
        self.assertEqual(direct_command(["change", "write", str(self.path), str(batch)], out, err), USAGE)
        self.assertEqual(self.path.read_bytes(), before)
        out, err = io.StringIO(), io.StringIO()
        self.assertEqual(direct_command(["change", "write", str(self.path), str(batch), "--skip-dependencies"], out, err), SUCCESS, err.getvalue())
        preview, written = map(json.loads, out.getvalue().splitlines())
        self.assertTrue(preview["skipDependencies"])
        self.assertEqual(len(preview["reviewPage"]["items"]), 5)
        self.assertEqual(written["status"], "written")
        self.assertEqual(preview["scopeContextCount"], 0)

    def test_direct_invalid_batch_returns_failure_and_keeps_database(self):
        batch = Path(self.temporary.name) / "invalid.json"
        batch.write_text(json.dumps({"operations": [operation_dto(Operation(OperationKind.REMOVE, EntityKind.EDGE, "battery-scope-parent"))]}))
        before = self.path.read_bytes()
        out, err = io.StringIO(), io.StringIO()
        self.assertEqual(direct_command(["change", "write", str(self.path), str(batch), "--skip-dependencies"], out, err), DOMAIN)
        self.assertEqual(json.loads(out.getvalue().splitlines()[-1])["status"], "reviewNotReady")
        self.assertEqual(self.path.read_bytes(), before)

    def test_ndjson_boolean_flag_is_explicit_and_reset_on_patch(self):
        environment = os.environ.copy()
        environment["PYTHONPATH"] = str(Path(__file__).parents[1] / "src")
        with subprocess.Popen([sys.executable, "-m", "validated_world", "ndjson"], stdin=subprocess.PIPE,
                              stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, env=environment) as process:
            def send(command, **payload):
                process.stdin.write(json.dumps({"version": 1, "command": command, "payload": payload}) + "\n")
                process.stdin.flush()
                return json.loads(process.stdout.readline())
            session = send("change.begin", path=str(self.path), projectId=self.graph.project_id, author="tester", intent="Cleanup")["payload"]
            batch = {"operations": [operation_dto(self.replace("purpose", "Repaired purpose"))]}
            for flag in (1, "true", None):
                self.assertEqual(send("change.apply", reference=session["reference"], operations=batch, skipDependencies=flag)["status"], "error")
            session = send("change.apply", reference=session["reference"], operations=batch, skipDependencies=True)["payload"]
            preview = send("change.preview", reference=session["reference"])["payload"]
            self.assertEqual(preview["reviewPage"]["totalCount"], 1)
            session = send("change.patch", reference=session["reference"], operations={"operations": [operation_dto(self.replace("purpose", "Changed again"))]})["payload"]
            self.assertFalse(session["skipDependencies"])
            self.assertGreater(session["affected"]["affectedNodeCount"], 1)
            self.assertEqual(send("change.agent-write", reference=session["reference"])["payload"]["status"], "agentReviewBlocked")
            session = send("change.apply", reference=session["reference"], operations=batch, skipDependencies=True)["payload"]
            self.assertEqual(send("change.write", reference=session["reference"])["payload"]["status"], "reviewNotReady")
            send("change.preview", reference=session["reference"])
            self.assertEqual(send("change.agent-write", reference=session["reference"])["payload"]["status"], "written")
            send("host.exit")
            process.stdin.close()
            self.assertEqual(process.wait(timeout=10), SUCCESS)


if __name__ == "__main__":
    unittest.main()
