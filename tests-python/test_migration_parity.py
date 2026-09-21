import json
import tempfile
import unittest
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).parents[1] / "src-python"))

from validated_world.application import Application, sample_graph
from validated_world.bulk import plan_bulk
from validated_world.cli import _diff
from validated_world.merge import merge_projects
from validated_world.models import Attribute, Edge, EntityKind, Graph, GraphValue, Node, Operation, OperationKind, ReviewDirection, operation_batch
from validated_world.protocol import edge_dto, graph_from_dto, json_loads_strict, node_dto, operation_dto, operation_from_dto
from validated_world.rules import evaluate_rules
from validated_world.storage import ProjectStore
from validated_world.templates import instantiate, resolve
from validated_world.validation import GraphIndex, project_graph, validate_graph


class MigrationParityTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(); self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)

    def test_model_scalars_identity_and_metadata_are_canonical(self):
        self.assertEqual(str(GraphValue.boolean(True)), "true")
        for value in (GraphValue.text("café"), GraphValue.integer(-(2**63)), GraphValue.decimal("1.25"), GraphValue.boolean(False), GraphValue.symbol("state:ready"), GraphValue.instant("2026-09-19T12:00:00.0000000+00:00")):
            self.assertIsNotNone(value.kind)
        for invalid in ("01", "1.0", "-0", "1e2", "+1"):
            with self.assertRaises(ValueError): GraphValue.decimal(invalid)
        with self.assertRaises(ValueError): Node("bad\n", "text")
        with self.assertRaises(ValueError): Node("node", "text", tags=("same", "same"))
        with self.assertRaises(ValueError): Node("node", "text", attributes=(Attribute("same", GraphValue.text("a")), Attribute("same", GraphValue.text("b"))))
        node = Node("node", "text", "kind", ("z", "a"), (Attribute("z", GraphValue.integer(1)), Attribute("a", GraphValue.text("v"))))
        self.assertEqual(node.tags, ("a", "z")); self.assertEqual([item.name for item in node.attributes], ["a", "z"])

    def test_validation_projection_index_and_explicit_bounds_match_reference_contracts(self):
        graph = sample_graph(); index = GraphIndex(graph)
        self.assertEqual(index.upstream("retention-policy"), ["retention-policy", "scope-privacy", "purpose"])
        self.assertNotIn("scope-power-parent", {item[0] for item in index.review_arcs})
        self.assertTrue(validate_graph(graph).is_valid)
        self.assertEqual(validate_graph(graph, max_traversal_depth=1).status, "inconclusive")
        duplicate = Graph(graph.project_id, graph.title, graph.purpose_node_id, (*graph.nodes, graph.nodes[0]), graph.edges)
        codes = {item.code for item in validate_graph(duplicate).diagnostics}; self.assertIn("duplicate-node-id", codes)
        with self.assertRaises(ValueError): operation_batch((Operation(OperationKind.REMOVE, EntityKind.NODE, "retention-policy"), Operation(OperationKind.REMOVE, EntityKind.NODE, "retention-policy")))
        projected, _ = project_graph(graph, (Operation(OperationKind.REMOVE, EntityKind.NODE, "battery-assumption"),))
        self.assertIn("missing-edge-source", {item.code for item in validate_graph(projected).diagnostics})
        with self.assertRaisesRegex(ValueError, "cannot add existing"): project_graph(graph, (Operation(OperationKind.ADD, EntityKind.NODE, "purpose", node=Node("purpose", "duplicate")),))
        many_orphans = Graph("invalid", "Invalid", "purpose", (Node("purpose", "Purpose"), Node("a", "A"), Node("b", "B")), ())
        bounded = validate_graph(many_orphans, max_diagnostics=1)
        self.assertEqual((bounded.status, len(bounded.diagnostics)), ("inconclusive", 1))

    def test_diff_is_complete_page_bound_and_rejects_project_mismatch(self):
        base = self.root / "base.vw.db"; target = self.root / "target.vw.db"
        graph = sample_graph(); ProjectStore().initialize(base, graph)
        changed, _ = project_graph(graph, (Operation(OperationKind.REPLACE, EntityKind.NODE, "battery-assumption", node=Node("battery-assumption", "Changed", "decision", ("new",), (Attribute("count", GraphValue.integer(2)),))), Operation(OperationKind.REPLACE, EntityKind.NODE, "retention-policy", node=Node("retention-policy", "Changed retention", "requirement"))))
        ProjectStore().initialize(target, Graph(graph.project_id, "Changed title", graph.purpose_node_id, changed.nodes, changed.edges))
        first = _diff(str(base), str(target), 1); self.assertEqual(first["summary"]["totalChanges"], 3)
        self.assertEqual(first["items"][0]["changedFields"], ["text", "kind", "tags", "attributes"])
        self.assertIsNone(_diff(str(base), str(target), 1, first["nextCursor"])["nextCursor"])
        with self.assertRaises(ValueError): _diff(str(base), str(target), 2, first["nextCursor"])
        other = self.root / "other.vw.db"; ProjectStore().initialize(other, Graph("other", graph.title, graph.purpose_node_id, graph.nodes, graph.edges))
        with self.assertRaisesRegex(ValueError, "does not match"): _diff(str(base), str(other))

    def test_merge_combines_independent_changes_and_reports_conflicts_and_invalid_results(self):
        graph = sample_graph(); base = self.root / "base.vw.db"; ours = self.root / "ours.vw.db"; theirs = self.root / "theirs.vw.db"
        ProjectStore().initialize(base, graph)
        ours_graph, _ = project_graph(graph, (Operation(OperationKind.REPLACE, EntityKind.NODE, "battery-assumption", node=Node("battery-assumption", "Ours", "assumption")),))
        theirs_graph, _ = project_graph(graph, (Operation(OperationKind.REPLACE, EntityKind.NODE, "retention-policy", node=Node("retention-policy", "Theirs", "requirement")),))
        ProjectStore().initialize(ours, ours_graph); ProjectStore().initialize(theirs, theirs_graph)
        clean = merge_projects(str(base), str(ours), str(theirs)); self.assertEqual(clean["status"], "clean"); self.assertIsNotNone(clean["mergedGraph"]); self.assertEqual(clean["operationCount"], 1)
        conflicting = self.root / "conflicting.vw.db"; conflict_graph, _ = project_graph(graph, (Operation(OperationKind.REPLACE, EntityKind.NODE, "battery-assumption", node=Node("battery-assumption", "Theirs conflict", "assumption")),)); ProjectStore().initialize(conflicting, conflict_graph)
        conflict = merge_projects(str(base), str(ours), str(conflicting)); self.assertEqual(conflict["status"], "conflicted"); self.assertEqual(conflict["conflicts"][0]["kind"], "modifiedDifferently")

    def test_merge_identical_additions_deletions_metadata_kind_and_invalid_combinations(self):
        purpose = Node("purpose", "Purpose")
        base_graph = Graph("merge", "Merge", purpose.id, (purpose,), ())
        note = Node("note", "Shared note")
        note_edge = Edge("note-parent", note.id, purpose.id, "scope-parent")
        added = Graph("merge", "Merge", purpose.id, (purpose, note), (note_edge,))
        different = Graph("merge", "Merge", purpose.id, (purpose, Node("note", "Different note")), (note_edge,))
        paths = {name: self.root / f"{name}.vw.db" for name in ("base-add", "ours-add", "same-add", "different-add")}
        for name, graph in (("base-add", base_graph), ("ours-add", added), ("same-add", added), ("different-add", different)):
            ProjectStore().initialize(paths[name], graph)
        self.assertEqual(merge_projects(str(paths["base-add"]), str(paths["ours-add"]), str(paths["same-add"]))["operationCount"], 0)
        divergent = merge_projects(str(paths["base-add"]), str(paths["ours-add"]), str(paths["different-add"]))
        self.assertEqual((divergent["status"], divergent["conflicts"][0]["kind"]), ("conflicted", "addedDifferently"))

        delete_base = self.root / "delete-base.vw.db"; delete_ours = self.root / "delete-ours.vw.db"; delete_theirs = self.root / "delete-theirs.vw.db"
        ProjectStore().initialize(delete_base, added); ProjectStore().initialize(delete_ours, added); ProjectStore().initialize(delete_theirs, base_graph)
        removal = merge_projects(str(delete_base), str(delete_ours), str(delete_theirs))
        self.assertEqual(removal["status"], "clean")
        self.assertEqual([(item["kind"], item["entityId"]) for item in removal["operations"]["operations"]], [("remove", "note"), ("remove", "note-parent")])

        metadata = self.root / "metadata.vw.db"; ProjectStore().initialize(metadata, Graph("merge", "Changed", purpose.id, (purpose,), ()))
        metadata_result = merge_projects(str(paths["base-add"]), str(metadata), str(paths["base-add"]))
        self.assertEqual(metadata_result["conflicts"][0]["kind"], "projectMetadataChanged")

        anchor = Node("anchor", "Anchor"); anchor_edge = Edge("anchor-parent", anchor.id, purpose.id, "scope-parent")
        kind_base_graph = Graph("kind", "Kind", purpose.id, (purpose, anchor), (anchor_edge,))
        node_version = Graph("kind", "Kind", purpose.id, (purpose, anchor, Node("shared", "Node")), (anchor_edge, Edge("shared-parent", "shared", purpose.id, "scope-parent")))
        edge_version = Graph("kind", "Kind", purpose.id, (purpose, anchor), (anchor_edge, Edge("shared", anchor.id, purpose.id, "informs")))
        kind_paths = []
        for index, graph in enumerate((kind_base_graph, node_version, edge_version)):
            path = self.root / f"kind-{index}.vw.db"; ProjectStore().initialize(path, graph); kind_paths.append(path)
        self.assertTrue(any(item["kind"] == "entityKindChanged" for item in merge_projects(*(str(path) for path in kind_paths))["conflicts"]))

        scope_a = Node("scope-a", "A", "scope"); scope_b = Node("scope-b", "B", "scope"); child = Node("child", "Child")
        scoped = Graph("invalid-merge", "Invalid merge", purpose.id, (purpose, scope_a, scope_b, child), (Edge("a-parent", scope_a.id, purpose.id, "scope-parent"), Edge("b-parent", scope_b.id, purpose.id, "scope-parent"), Edge("child-parent", child.id, scope_a.id, "scope-parent")))
        ours_invalid = Graph("invalid-merge", "Invalid merge", purpose.id, (purpose, scope_a, child), (Edge("a-parent", scope_a.id, purpose.id, "scope-parent"), Edge("child-parent", child.id, scope_a.id, "scope-parent")))
        theirs_invalid = Graph("invalid-merge", "Invalid merge", purpose.id, (purpose, scope_a, scope_b, child), (Edge("a-parent", scope_a.id, purpose.id, "scope-parent"), Edge("b-parent", scope_b.id, purpose.id, "scope-parent"), Edge("child-parent", child.id, scope_b.id, "scope-parent")))
        invalid_paths = []
        for index, graph in enumerate((scoped, ours_invalid, theirs_invalid)):
            path = self.root / f"invalid-{index}.vw.db"; ProjectStore().initialize(path, graph); invalid_paths.append(path)
        invalid = merge_projects(*(str(path) for path in invalid_paths))
        self.assertEqual((invalid["status"], invalid["validation"]["status"]), ("invalid", "invalid"))

    def test_bulk_manifest_is_strict_resumable_large_line_safe_and_checkpoint_validated(self):
        path = self.root / "bulk.vw.db"; project = ProjectStore().initialize(path, Graph("bulk", "Bulk", "purpose", (Node("purpose", "Purpose"),), ()))
        manifest = self.root / "bulk.jsonl"
        header = {"version": 1, "format": "validated-world-bulk-manifest", "projectId": "bulk", "baseFingerprint": project.state_fingerprint, "intent": "Import scopes"}
        huge = Node("scope", "x" * (1024 * 1024 + 1), "scope"); parent = Edge("scope-parent", "scope", "purpose", "scope-parent")
        second = Node("scope-two", "Second", "scope"); second_parent = Edge("scope-two-parent", "scope-two", "purpose", "scope-parent")
        records = (header, operation_dto(Operation(OperationKind.ADD, EntityKind.NODE, huge.id, node=huge)), operation_dto(Operation(OperationKind.ADD, EntityKind.EDGE, parent.id, edge=parent)), operation_dto(Operation(OperationKind.ADD, EntityKind.NODE, second.id, node=second)), operation_dto(Operation(OperationKind.ADD, EntityKind.EDGE, second_parent.id, edge=second_parent)))
        manifest.write_text("\n".join(json.dumps(item, separators=(",", ":")) for item in records) + "\n", encoding="utf-8")
        whole = plan_bulk(str(path), str(manifest), 10); self.assertEqual(whole["chunkOperationCount"], 4)
        first = plan_bulk(str(path), str(manifest), 2); self.assertIsNotNone(first["nextCursor"]); self.assertIsNone(plan_bulk(str(path), str(manifest), 2, first["nextCursor"])["nextCursor"])
        with self.assertRaisesRegex(ValueError, "checkpoint"): plan_bulk(str(path), str(manifest), 1)
        with self.assertRaises(ValueError): plan_bulk(str(path), str(manifest), 3, first["nextCursor"])
        manifest.write_text('{"version":1,"version":1,"format":"validated-world-bulk-manifest","projectId":"bulk","baseFingerprint":"x","intent":"x"}\n', encoding="utf-8")
        with self.assertRaises(ValueError): plan_bulk(str(path), str(manifest))

    def test_custom_template_strictness_and_code_roadmap_rules(self):
        code = instantiate(resolve("code-development"), "code", "Code", "Purpose")
        self.assertTrue(evaluate_rules(code).is_valid)
        custom = self.root / "custom.json"
        scope = Node("notes", "Notes", "scope"); edge = Edge("notes-scope", "notes", "purpose", "scope-parent")
        raw = {"version": 1, "id": "custom", "description": "Custom notes.", "purposeNodeId": "purpose", "nodes": [node_dto(scope)], "edges": [edge_dto(edge)]}
        custom.write_text(json.dumps(raw), encoding="utf-8"); graph = instantiate(resolve(str(custom)), "notes", "Notes", "Take notes")
        self.assertTrue(validate_graph(graph).is_valid)
        custom.write_text(json.dumps({**raw, "unknown": True}), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "shape"): resolve(str(custom))

        large = self.root / "large-template.json"
        large_node = Node("large-note", "x" * (1024 * 1024 + 1), "note")
        large_raw = {"version": 1, "id": "large", "description": "Large template.", "purposeNodeId": "purpose", "nodes": [node_dto(large_node)], "edges": [edge_dto(Edge("large-note-scope", "large-note", "purpose", "scope-parent"))]}
        large.write_text(json.dumps(large_raw), encoding="utf-8")
        instantiated = instantiate(resolve(str(large)), "large", "Large", "Purpose")
        self.assertEqual(next(item for item in instantiated.nodes if item.id == "large-note").text, large_node.text)

    def test_application_write_failures_are_structured_and_retry_preserves_session(self):
        path = self.root / "atomic.vw.db"; ProjectStore().initialize(path, sample_graph())
        fail = {"enabled": True}
        def inject(stage):
            if fail["enabled"] and stage == "edges-written": raise RuntimeError("injected")
        store = ProjectStore(write_fault=inject)
        app = Application(store); session = app.begin(str(path), "technical-project", "human", "atomic")
        session = app.apply(session.reference(), (Operation(OperationKind.REPLACE, EntityKind.NODE, "battery-assumption", node=Node("battery-assumption", "Retry", "assumption")),))
        session = app.review(session.reference(), [{"nodeId": item["nodeId"], "kind": "updated" if item["isDirectChange"] else "reviewedNoChange"} for item in session.affected_nodes], [item["nodeId"] for item in session.scope_context]); session.preview(1000)
        before = path.read_bytes(); failed = app.write(session.reference(), bypass_ai_review=True)
        self.assertEqual(failed["status"], "failed"); self.assertEqual(path.read_bytes(), before); self.assertIn("technical-project", app.sessions)
        fail["enabled"] = False; self.assertEqual(app.write(session.reference(), bypass_ai_review=True)["status"], "written")

    def test_independently_reviewed_golden_scenarios_match_affected_and_context_sets(self):
        fixture_root = Path(__file__).parents[1] / "samples" / "TechnicalProject"
        graph = graph_from_dto(json_loads_strict((fixture_root / "baseline.json").read_text(encoding="utf-8")))
        self.assertEqual(graph, sample_graph())
        for scenario_path in sorted(fixture_root.glob("*-change.json")):
            scenario = json_loads_strict(scenario_path.read_text(encoding="utf-8"))
            operations = tuple(operation_from_dto(item) for item in scenario["operations"]["operations"])
            session = self.session_for_graph(graph, operations, scenario_path.stem)
            expected = scenario["expected"]
            self.assertEqual([item["nodeId"] for item in session.affected_nodes], expected["affectedNodeIds"], scenario_path.name)
            self.assertEqual([item["nodeId"] for item in session.scope_context], expected["scopeContextNodeIds"], scenario_path.name)
            self.assertTrue(set(expected["excludedNodeIds"]).isdisjoint(item["nodeId"] for item in session.affected_nodes), scenario_path.name)
            self.assertEqual([item["operation"]["entityId"] for item in session.edge_changes], expected["expectedEdgeChangeIds"], scenario_path.name)

    def session_for_graph(self, graph, operations, name):
        path = self.root / f"{name}.vw.db"; ProjectStore().initialize(path, graph)
        app = Application(); session = app.begin(str(path), graph.project_id, "golden", name)
        return app.apply(session.reference(), operations)


if __name__ == "__main__":
    unittest.main()
