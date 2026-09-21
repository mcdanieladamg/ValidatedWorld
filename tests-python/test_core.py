import base64
import json
import os
import subprocess
import tempfile
import unittest
from pathlib import Path

import sys
sys.path.insert(0, str(Path(__file__).parents[1] / "src-python"))

from validated_world.application import Application, sample_graph
from validated_world.bulk import plan_bulk
from validated_world.canonical import state_fingerprint
from validated_world.config import load_review_config
from validated_world.merge import merge_projects
from validated_world.models import Attribute, Edge, Graph, GraphValue, Node, Operation, ReviewDirection
from validated_world.queries import Queries
from validated_world.storage import ProjectStore
from validated_world.templates import descriptor, instantiate, resolve


class PythonProductTests(unittest.TestCase):
    def test_tracked_fixture_fingerprint_is_stable(self):
        project = ProjectStore().load(Path(__file__).parents[1] / "samples" / "TechnicalProject" / "semantic-review-foundation.vw.db")
        self.assertEqual(project.state_fingerprint, "c16391dda09b1f3e3a8272e732b92051f213db0f6cd47a4a8c6182f140724ef1")

    def test_initialize_round_trips_current_schema(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "unicode project.vw.db"
            graph = Graph("p", "Project", "purpose", (Node("purpose", "Keep it coherent"), Node("note", "Unicode café", "note", ("tag",), (Attribute("count", GraphValue.integer(2)),))), (Edge("note-parent", "note", "purpose", "scope-parent"),))
            project = ProjectStore().initialize(str(path), graph)
            self.assertEqual(project.state_fingerprint, state_fingerprint(graph))
            self.assertEqual(ProjectStore().load(str(path)).graph, graph)

    def test_queries_and_atomic_reviewed_write(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "project.vw.db"
            ProjectStore().initialize(str(path), sample_graph())
            app = Application()
            session = app.begin(str(path), "technical-project", "test", "change battery claim")
            operation = Operation(1, 0, "battery-assumption", Node("battery-assumption", "The battery lasts two duty cycles", "assumption"))
            session = app.apply(session.reference(), (operation,))
            self.assertIn("runtime-test", {item["nodeId"] for item in session.affected_nodes})
            app.review(session.reference(), [{"nodeId": item["nodeId"], "kind": "updated" if item["isDirectChange"] else "reviewedNoChange"} for item in session.affected_nodes], [item["nodeId"] for item in session.scope_context])
            preview = session.preview(1000)
            self.assertTrue(preview["reviewPage"]["allEvidencePresented"])
            result = app.write(session.reference())
            self.assertEqual(result["status"], "written")
            self.assertEqual(ProjectStore().load(str(path)).graph.nodes[[item.id for item in ProjectStore().load(str(path)).graph.nodes].index("battery-assumption")].text, "The battery lasts two duty cycles")

    def test_query_cursor_is_snapshot_bound(self):
        project = ProjectStore().load(Path(__file__).parents[1] / "ValidatedWorld.Blueprint.vw.db")
        page = Queries(project).nodes(2)
        self.assertIsNotNone(page["nextCursor"])
        with self.assertRaises(ValueError):
            Queries(project).edges(2, page["nextCursor"])

    def test_rules_templates_merge_and_bulk_plan_are_deterministic(self):
        template = resolve("code-development")
        self.assertEqual(descriptor(template)["id"], "code-development")
        graph = instantiate(template, "demo", "Demo", "Purpose")
        with tempfile.TemporaryDirectory() as temp:
            base = Path(temp) / "base.vw.db"
            ours = Path(temp) / "ours.vw.db"
            theirs = Path(temp) / "theirs.vw.db"
            ProjectStore().initialize(str(base), graph)
            ProjectStore().backup(str(base), str(ours))
            ProjectStore().backup(str(base), str(theirs))
            result = merge_projects(str(base), str(ours), str(theirs))
            self.assertEqual(result["status"], "clean")
            project = ProjectStore().load(str(base))
            manifest = Path(temp) / "import.jsonl"
            header = {"version": 1, "format": "validated-world-bulk-manifest", "projectId": "demo", "baseFingerprint": project.state_fingerprint, "intent": "Add a scope"}
            node = {"kind": "add", "entityKind": "node", "entityId": "extra-scope", "node": {"id": "extra-scope", "text": "Extra scope", "kind": "scope", "tags": [], "attributes": []}, "edge": None}
            edge = {"kind": "add", "entityKind": "edge", "entityId": "extra-scope-parent", "node": None, "edge": {"id": "extra-scope-parent", "source": "extra-scope", "target": "purpose", "relationship": "scope-parent", "reviewDirection": "none", "rationale": None, "tags": [], "attributes": []}}
            manifest.write_text("\n".join(json.dumps(item, separators=(",", ":")) for item in (header, node, edge)) + "\n", encoding="utf-8")
            page = plan_bulk(str(base), str(manifest), 2)
            self.assertEqual(page["chunkOperationCount"], 2)
            self.assertIsNone(page["nextCursor"])
            self.assertEqual(ProjectStore().verify(str(base))["ruleStatus"], "valid")

    def test_environment_only_ai_configuration_has_no_key_fragment_in_status(self):
        previous = os.environ.pop("VW_AIREVIEW__OPENAI__APIKEY", None)
        shared_previous = os.environ.pop("OPENAI_API_KEY", None)
        try:
            config = load_review_config()
            self.assertFalse(config.configured)
            self.assertNotIn("apiKey", config.public())
        finally:
            if previous is not None:
                os.environ["VW_AIREVIEW__OPENAI__APIKEY"] = previous
            if shared_previous is not None:
                os.environ["OPENAI_API_KEY"] = shared_previous

    def test_ndjson_exact_preview_gate_and_persistent_session(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "ndjson project.vw.db"
            ProjectStore().initialize(str(path), sample_graph())
            environment = os.environ.copy()
            environment["PYTHONPATH"] = str(Path(__file__).parents[1] / "src-python")
            process = subprocess.Popen([sys.executable, "-m", "validated_world", "ndjson"], stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, env=environment)

            def send(command, payload):
                process.stdin.write(json.dumps({"version": 1, "command": command, "payload": payload}) + "\n")
                process.stdin.flush()
                return json.loads(process.stdout.readline())

            begun = send("change.begin", {"path": str(path), "projectId": "technical-project", "author": "test", "intent": "change battery"})["payload"]
            operation = {"kind": "replace", "entityKind": "node", "entityId": "battery-assumption", "node": {"id": "battery-assumption", "text": "The battery lasts two duty cycles", "kind": "assumption", "tags": [], "attributes": []}, "edge": None}
            changed = send("change.apply", {"reference": begun["reference"], "operations": {"operations": [operation]}})["payload"]
            blocked = send("change.write", {"reference": changed["reference"]})["payload"]
            self.assertEqual(blocked["status"], "reviewNotReady")
            review = send("change.review", {"reference": changed["reference"], "dispositions": [{"nodeId": item["nodeId"], "kind": "updated" if item["isDirectChange"] else "reviewedNoChange"} for item in changed["affected"]["affectedNodes"]], "presentedContextNodeIds": [item["nodeId"] for item in changed["affected"]["scopeContext"]]})["payload"]
            preview = send("change.preview", {"reference": review["reference"], "limit": 2})["payload"]
            while preview["reviewPage"]["nextCursor"]:
                preview = send("change.preview", {"reference": review["reference"], "limit": 2, "cursor": preview["reviewPage"]["nextCursor"]})["payload"]
            self.assertTrue(preview["reviewPage"]["allEvidencePresented"])
            written = send("change.write", {"reference": review["reference"]})["payload"]
            self.assertEqual(written["status"], "written")
            process.stdin.close()
            process.stdout.close()
            process.wait(timeout=10)

    def test_review_change_invalidates_presented_preview(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "preview invalidation.vw.db"
            ProjectStore().initialize(str(path), sample_graph())
            app = Application()
            session = app.begin(str(path), "technical-project", "test", "change battery claim")
            session = app.apply(session.reference(), (Operation(1, 0, "battery-assumption", Node("battery-assumption", "Changed", "assumption")),))
            dispositions = [{"nodeId": item["nodeId"], "kind": "updated" if item["isDirectChange"] else "reviewedNoChange"} for item in session.affected_nodes]
            context = [item["nodeId"] for item in session.scope_context]
            session = app.review(session.reference(), dispositions, context)
            self.assertTrue(session.preview(1000)["reviewPage"]["allEvidencePresented"])

            session = app.review(session.reference(), [{"nodeId": "battery-assumption", "kind": "notApplicable", "rationale": "The revised disposition still needs presentation."}], [])
            blocked = app.write(session.reference())

            self.assertEqual(blocked["status"], "reviewNotReady")
            self.assertIn("review evidence", blocked["message"])


if __name__ == "__main__":
    unittest.main()
