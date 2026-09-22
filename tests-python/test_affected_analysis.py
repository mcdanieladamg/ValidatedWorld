import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parents[1] / "src-python"))

from validated_world.application import Application, sample_graph
from validated_world.models import Edge, EntityKind, Graph, Node, Operation, OperationKind
from validated_world.storage import ProjectStore


class AffectedAnalysisTests(unittest.TestCase):
    def session_for(self, graph, operations):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        path = Path(temporary.name) / "affected.vw.db"
        ProjectStore().initialize(path, graph)
        application = Application()
        session = application.begin(str(path), graph.project_id, "test", "exercise affected analysis")
        return application.apply(session.reference(), tuple(operations))

    def test_node_change_follows_review_arcs_and_collects_only_scope_ancestors(self):
        graph = sample_graph()
        changed = Node("battery-assumption", "Changed battery", "assumption")
        session = self.session_for(
            graph,
            [Operation(OperationKind.REPLACE, EntityKind.NODE, changed.id, node=changed)],
        )

        affected = {item["nodeId"] for item in session.affected_nodes}
        self.assertEqual(
            affected,
            {"battery-assumption", "runtime-test", "power-design-anchor"},
        )
        self.assertEqual(
            {item["nodeId"] for item in session.scope_context},
            {"scope-power", "purpose"},
        )
        runtime = next(item for item in session.affected_nodes if item["nodeId"] == "runtime-test")
        self.assertEqual(runtime["explanation"]["nodes"], ["battery-assumption", "runtime-test"])
        self.assertEqual(runtime["explanation"]["edges"], ["battery-requires-runtime"])

    def test_direct_scope_change_selects_its_subtree_without_sibling_fanout(self):
        graph = sample_graph()
        changed = Node("scope-power", "Changed power scope", "scope")
        session = self.session_for(
            graph,
            [Operation(OperationKind.REPLACE, EntityKind.NODE, changed.id, node=changed)],
        )

        affected = {item["nodeId"] for item in session.affected_nodes}
        self.assertEqual(
            affected,
            {"scope-power", "battery-assumption", "runtime-test", "power-design-anchor"},
        )
        self.assertNotIn("scope-privacy", affected)
        self.assertEqual({item["nodeId"] for item in session.scope_context}, {"purpose"})

    def test_direct_purpose_change_selects_the_entire_project(self):
        graph = sample_graph()
        changed = Node("purpose", "Changed purpose")
        session = self.session_for(
            graph,
            [Operation(OperationKind.REPLACE, EntityKind.NODE, changed.id, node=changed)],
        )

        self.assertEqual(
            {item["nodeId"] for item in session.affected_nodes},
            {item.id for item in graph.nodes},
        )
        self.assertEqual(session.scope_context, [])

    def test_scope_parent_redirect_selects_child_subtree_and_both_parents(self):
        base = sample_graph()
        detail = Node("battery-detail", "Battery chemistry", "fact")
        graph = Graph(
            base.project_id,
            base.title,
            base.purpose_node_id,
            (*base.nodes, detail),
            (*base.edges, Edge("battery-detail-parent", detail.id, "battery-assumption", "scope-parent")),
        )
        replacement = Edge(
            "battery-scope-parent",
            "battery-assumption",
            "scope-privacy",
            "scope-parent",
        )
        session = self.session_for(
            graph,
            [Operation(OperationKind.REPLACE, EntityKind.EDGE, replacement.id, edge=replacement)],
        )

        affected = {item["nodeId"] for item in session.affected_nodes}
        self.assertEqual(
            affected,
            {
                "battery-assumption",
                "battery-detail",
                "runtime-test",
                "power-design-anchor",
                "scope-power",
                "scope-privacy",
            },
        )
        self.assertNotIn("retention-policy", affected)
        self.assertEqual({item["nodeId"] for item in session.scope_context}, {"purpose"})
        self.assertTrue(all(not item["isDirectChange"] for item in session.affected_nodes))

        old_parent = next(item for item in session.affected_nodes if item["nodeId"] == "scope-power")
        new_parent = next(item for item in session.affected_nodes if item["nodeId"] == "scope-privacy")
        self.assertEqual(old_parent["explanation"]["nodes"], ["battery-assumption", "scope-power"])
        self.assertEqual(new_parent["explanation"]["nodes"], ["battery-assumption", "scope-privacy"])

    def test_scope_context_preserves_each_affected_old_and_new_lineage(self):
        graph = sample_graph()
        replacement = Edge(
            "battery-scope-parent",
            "battery-assumption",
            "scope-privacy",
            "scope-parent",
        )
        session = self.session_for(
            graph,
            [Operation(OperationKind.REPLACE, EntityKind.EDGE, replacement.id, edge=replacement)],
        )

        purpose = next(item for item in session.scope_context if item["nodeId"] == "purpose")
        battery = next(
            item for item in purpose["lineages"] if item["affectedNodeId"] == "battery-assumption"
        )
        self.assertEqual(battery["currentPath"], ["battery-assumption", "scope-power", "purpose"])
        self.assertEqual(battery["proposedPath"], ["battery-assumption", "scope-privacy", "purpose"])

    def test_patch_revert_normalizes_to_base_and_refresh_invalidates_changed_evidence(self):
        graph = sample_graph(); temporary = tempfile.TemporaryDirectory(); self.addCleanup(temporary.cleanup)
        path = Path(temporary.name) / "refresh.vw.db"; ProjectStore().initialize(path, graph)
        app = Application(); session = app.begin(str(path), graph.project_id, "human", "revise")
        battery = next(item for item in graph.nodes if item.id == "battery-assumption")
        first = app.apply(session.reference(), (Operation(OperationKind.REPLACE, EntityKind.NODE, battery.id, node=Node(battery.id, "First revision", battery.kind)),), patch=True)
        reviewed = app.review(first.reference(), [{"nodeId": item["nodeId"], "kind": "updated" if item["isDirectChange"] else "reviewedNoChange"} for item in first.affected_nodes], [item["nodeId"] for item in first.scope_context])
        changed = app.apply(reviewed.reference(), (Operation(OperationKind.REPLACE, EntityKind.NODE, battery.id, node=Node(battery.id, "Second revision", battery.kind)),), patch=True)
        self.assertIn("battery-assumption", changed.refresh["invalidatedDispositionNodeIds"])
        self.assertEqual(changed.dispositions["battery-assumption"]["kind"], "pending")
        reverted = app.apply(changed.reference(), (Operation(OperationKind.REPLACE, EntityKind.NODE, battery.id, node=battery),), patch=True)
        self.assertNotIn("battery-assumption", [item.entity_id for item in reverted.operations])

    def test_bounds_are_inconclusive_and_omission_details_are_revision_bound(self):
        graph = sample_graph(); temporary = tempfile.TemporaryDirectory(); self.addCleanup(temporary.cleanup)
        path = Path(temporary.name) / "bounded.vw.db"; ProjectStore().initialize(path, graph)
        app = Application(); session = app.begin(str(path), graph.project_id, "human", "purpose")
        purpose = next(item for item in graph.nodes if item.id == "purpose")
        bounded = app.apply(session.reference(), (Operation(OperationKind.REPLACE, EntityKind.NODE, purpose.id, node=Node(purpose.id, "Revised purpose", purpose.kind)),), max_output_items=1)
        self.assertEqual(bounded.affected()["status"], "inconclusive")
        self.assertFalse(bounded.readiness()["isReady"])
        omission = bounded.omissions[0]
        self.assertEqual(len(bounded.read_omission_details(omission["detailsFingerprint"], 1)["items"]), 1)
        with self.assertRaises(ValueError): bounded.read_omission_details("0" * 64, 1)

    def test_external_database_change_makes_session_explicitly_stale(self):
        graph = sample_graph(); temporary = tempfile.TemporaryDirectory(); self.addCleanup(temporary.cleanup)
        path = Path(temporary.name) / "stale.vw.db"; ProjectStore().initialize(path, graph)
        app = Application(); session = app.begin(str(path), graph.project_id, "human", "stale")
        external = Application(); other = external.begin(str(path), graph.project_id, "other", "external")
        purpose = next(item for item in graph.nodes if item.id == "purpose")
        other = external.apply(other.reference(), (Operation(OperationKind.REPLACE, EntityKind.NODE, purpose.id, node=Node(purpose.id, "Externally changed", purpose.kind)),))
        external.review(other.reference(), [{"nodeId": item["nodeId"], "kind": "updated" if item["isDirectChange"] else "reviewedNoChange"} for item in other.affected_nodes], [item["nodeId"] for item in other.scope_context])
        other.preview(max(1, len(other.review_items()))); external.write(other.reference(), bypass_ai_review=True)
        with self.assertRaisesRegex(ValueError, "stale-baseFingerprint"): app.session(session.reference())


if __name__ == "__main__":
    unittest.main()
