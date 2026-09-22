import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parents[1] / "src-python"))

from validated_world.application import sample_graph
from validated_world.canonical import state_fingerprint
from validated_world.models import Attribute, Edge, Graph, GraphValue, Node
from validated_world.queries import Queries
from validated_world.storage import ProjectStore, StoredProject


class QueryTests(unittest.TestCase):
    def queries(self, graph=None):
        if graph is not None:
            return Queries(StoredProject("memory.vw.db", graph, state_fingerprint(graph), "", ""))
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        path = Path(temporary.name) / "query project.vw.db"
        project = ProjectStore().initialize(path, sample_graph())
        return Queries(project)

    def test_pages_search_tags_and_cursors_are_deterministic_and_bound(self):
        queries = self.queries()
        first = queries.nodes(2)
        second = queries.nodes(2, first["nextCursor"])
        self.assertEqual([item["id"] for item in first["items"]], ["accessibility-acceptance", "battery-assumption"])
        self.assertEqual(first["totalCount"], 13)
        self.assertEqual(first["omission"]["remainingCount"], 11)
        self.assertEqual([item["id"] for item in second["items"]], ["power-design-anchor", "privacy-architecture"])
        with self.assertRaisesRegex(ValueError, "different query"):
            queries.edges(2, first["nextCursor"])

        literal = queries.search("POWER")
        self.assertEqual(
            [item["entityId"] for item in literal["items"]],
            ["battery-informs-power-anchor", "power-anchor-scope-parent", "power-design-anchor", "scope-power", "scope-power-parent"],
        )
        self.assertEqual([item["entityId"] for item in queries.tag("artifact")["items"]], ["power-design-anchor", "privacy-documentation"])
        self.assertEqual(queries.tag("Artifact")["items"], [])

    def test_ranked_search_explains_ids_tags_phrases_tokens_and_metadata(self):
        base = sample_graph()
        note = Node(
            "maintenance-note",
            "Inspect the power enclosure before maintenance",
            "note",
            ("operations",),
            (Attribute("owner", GraphValue.text("Power Team")),),
        )
        graph = Graph(base.project_id, base.title, base.purpose_node_id, (*base.nodes, note), (*base.edges, Edge("maintenance-note-parent", note.id, "scope-power", "scope-parent")))
        queries = self.queries(graph)

        self.assertEqual(queries.ranked_search("maintenance-note")["items"][0]["entityId"], note.id)
        self.assertEqual(queries.ranked_search("operations")["items"][0]["entityId"], note.id)
        phrase = queries.ranked_search('"power enclosure"')["items"][0]
        self.assertEqual(phrase["entityId"], note.id)
        self.assertTrue(any(item["kind"] == "phrase" and item["field"] == "text" for item in phrase["matches"]))
        metadata = queries.ranked_search("Power Team")["items"][0]
        self.assertEqual(metadata["entityId"], note.id)
        self.assertTrue(any(item["kind"] == "metadata" and item["field"] == "attribute:owner" for item in metadata["matches"]))
        with self.assertRaisesRegex(ValueError, "closing quotes"):
            queries.ranked_search('"unclosed')
        with self.assertRaisesRegex(ValueError, "cannot be empty"):
            queries.ranked_search('"  "')

    def test_scope_neighbors_dependencies_path_and_context_exclude_siblings(self):
        queries = self.queries()
        scope = queries.scope("scope-power")
        self.assertEqual([item["id"] for item in scope["upstream"]], ["purpose"])
        self.assertEqual([item["id"] for item in scope["descendants"]["items"]], ["battery-assumption", "power-design-anchor", "runtime-test"])
        self.assertEqual([item["nodeId"] for item in queries.neighbors("battery-assumption")["items"]], ["power-design-anchor", "runtime-test", "scope-power"])
        dependencies = queries.dependencies("battery-assumption")["items"]
        self.assertEqual(len(dependencies), 2)
        self.assertTrue(all(item["isOutgoing"] for item in dependencies))
        path = queries.path("battery-assumption", "runtime-test")
        self.assertEqual(path, {"found": True, "nodes": ["battery-assumption", "runtime-test"], "edges": ["battery-requires-runtime"], "omissions": []})
        context = queries.context(["battery-assumption", "retention-policy"])
        self.assertEqual([item["id"] for item in context["contextNodes"]], ["battery-assumption", "purpose", "retention-policy", "scope-power", "scope-privacy"])

        bounded = queries.scope("purpose", max_depth=1, max_visited=100)
        self.assertTrue(any(item["reason"] == "traversalDepthLimit" for item in bounded["omissions"]))
        self.assertEqual([item["id"] for item in bounded["descendants"]["items"]], ["scope-accessibility", "scope-documentation", "scope-power", "scope-privacy"])
        not_found = queries.path("battery-assumption", "retention-policy", max_depth=0)
        self.assertFalse(not_found["found"])
        self.assertTrue(any(item["reason"] == "traversalDepthLimit" for item in not_found["omissions"]))

    def test_observability_reports_scope_dependency_rationale_isolation_and_tags(self):
        report = self.queries().health(1)
        self.assertEqual((report["nodeCount"], report["edgeCount"], report["semanticReviewArcCount"]), (13, 17, 5))
        self.assertEqual(report["scopeCoverage"]["coveragePercent"], 100.0)
        self.assertEqual(report["unreachableNodeIds"]["totalCount"], 0)
        self.assertEqual(report["reviewFanOutHotspots"]["totalCount"], 3)
        self.assertEqual(report["reviewFanOutHotspots"]["items"][0]["nodeId"], "battery-assumption")
        self.assertEqual(report["suspiciouslyIsolatedClaims"]["items"][0]["nodeId"], "accessibility-acceptance")
        self.assertEqual(report["missingRationales"]["totalCount"], 5)
        self.assertEqual(report["tagUsage"]["items"][0], {"tag": "artifact", "nodeCount": 2, "edgeCount": 0, "totalCount": 2})
        self.assertEqual((report["untaggedNodeCount"], report["untaggedEdgeCount"]), (11, 17))

    def test_orphan_is_reported_without_mutating_the_graph(self):
        base = sample_graph()
        orphan = Node("orphan-claim", "An orphan claim", "claim")
        graph = Graph(base.project_id, base.title, base.purpose_node_id, (*base.nodes, orphan), base.edges)
        queries = self.queries(graph)
        report = queries.health()
        self.assertIn("orphan-claim", report["unreachableNodeIds"]["items"])
        self.assertTrue(any(item["nodeId"] == "orphan-claim" for item in report["suspiciouslyIsolatedClaims"]["items"]))
        self.assertEqual(report["edgeCount"], 17)


if __name__ == "__main__":
    unittest.main()
