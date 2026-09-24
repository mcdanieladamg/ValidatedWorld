import hashlib
import io
import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parents[1] / "src-python"))

from validated_world.application import sample_graph
from validated_world.cli import DOMAIN, SUCCESS, USAGE, direct_command
from validated_world.models import Attribute, Edge, Graph, GraphValue, Node
from validated_world.storage import ProjectStore


class CliProtocolTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.path = self.root / "cli project.vw.db"
        ProjectStore().initialize(self.path, sample_graph())

    def process(self):
        environment = os.environ.copy()
        environment["PYTHONPATH"] = str(Path(__file__).parents[1] / "src-python")
        process = subprocess.Popen(
            [sys.executable, "-m", "validated_world", "ndjson"],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, env=environment,
        )
        def cleanup():
            if process.poll() is None:
                process.kill()
                process.wait(timeout=10)
            for stream in (process.stdin, process.stdout, process.stderr):
                if stream is not None and not stream.closed:
                    stream.close()
        self.addCleanup(cleanup)
        return process

    @staticmethod
    def send(process, command, payload, **extra):
        process.stdin.write(json.dumps({"version": 1, "command": command, "payload": payload, **extra}) + "\n")
        process.stdin.flush()
        return json.loads(process.stdout.readline())

    def test_unknown_and_duplicate_members_are_rejected_but_process_continues(self):
        process = self.process()
        unknown = self.send(process, "project.status", {"path": str(self.path), "extra": True})
        self.assertEqual(unknown["status"], "error")
        root_unknown = self.send(process, "project.status", {"path": str(self.path)}, extra=True)
        self.assertEqual(root_unknown["status"], "error")
        process.stdin.write('{"version":1,"command":"project.status","command":"project.verify","payload":{}}\n')
        process.stdin.flush()
        self.assertEqual(json.loads(process.stdout.readline())["status"], "error")
        valid = self.send(process, "project.status", {"path": str(self.path)})
        self.assertEqual(valid["status"], "ok")
        self.assertEqual(valid["payload"]["projectId"], "technical-project")

    def test_expected_project_id_and_strict_nested_operations_are_enforced(self):
        process = self.process()
        mismatch = self.send(process, "read.node", {"path": str(self.path), "entityId": "purpose", "expectedProjectId": "wrong"})
        self.assertEqual(mismatch["status"], "error")
        begun = self.send(process, "change.begin", {"path": str(self.path), "projectId": "technical-project", "author": "test", "intent": "test"})["payload"]
        operation = {"kind": "replace", "entityKind": "node", "entityId": "purpose", "node": {"id": "purpose", "text": "Changed", "kind": None, "tags": [], "attributes": []}, "edge": None}
        wrong_shape = self.send(process, "change.apply", {"reference": begun["reference"], "operations": [operation]})
        self.assertEqual(wrong_shape["status"], "error")
        unknown_operation = {**operation, "unexpected": 1}
        rejected = self.send(process, "change.apply", {"reference": begun["reference"], "operations": {"operations": [unknown_operation]}})
        self.assertEqual(rejected["status"], "error")
        accepted = self.send(process, "change.apply", {"reference": begun["reference"], "operations": {"operations": [operation]}})
        self.assertEqual(accepted["status"], "ok")

    def test_large_change_snapshots_are_compact_and_affected_evidence_is_paged(self):
        base = sample_graph()
        nodes = list(base.nodes)
        edges = list(base.edges)
        for index in range(1200):
            node_id = f"controlled-claim-{index:04d}"
            nodes.append(Node(node_id, f"Controlled claim {index} with representative project context.", "claim"))
            edges.append(Edge(f"{node_id}-scope", node_id, "purpose", "scope-parent"))
        graph = Graph(base.project_id, base.title, base.purpose_node_id, tuple(nodes), tuple(edges))
        large_path = self.root / "large controlled graph.vw.db"
        ProjectStore().initialize(large_path, graph)
        process = self.process()

        begun = self.send(process, "change.begin", {"path": str(large_path), "projectId": graph.project_id, "author": "bounded-test", "intent": "Update project purpose"})["payload"]
        serialized_begin = json.dumps(begun, separators=(",", ":"))
        self.assertLess(len(serialized_begin), 12000)
        self.assertIsNone(begun["proposedGraph"])
        self.assertIsNone(begun["operations"])
        self.assertNotIn("affectedNodes", begun["affected"])

        purpose = next(item for item in graph.nodes if item.id == graph.purpose_node_id)
        operation = {"kind": "replace", "entityKind": "node", "entityId": purpose.id, "node": {"id": purpose.id, "text": purpose.text + " Updated.", "kind": purpose.kind, "tags": list(purpose.tags), "attributes": []}, "edge": None}
        changed = self.send(process, "change.apply", {"reference": begun["reference"], "operations": {"operations": [operation]}})["payload"]
        serialized_changed = json.dumps(changed, separators=(",", ":"))
        self.assertLess(len(serialized_changed), 12000)
        self.assertEqual(changed["affected"]["affectedNodeCount"], len(graph.nodes))

        locator = {"projectId": changed["reference"]["projectId"], "sessionId": changed["reference"]["sessionId"]}
        page = self.send(process, "change.affected", {"session": locator, "limit": 37})["payload"]
        self.assertEqual(len(page["items"]), 37)
        self.assertFalse(page["page"]["isComplete"])
        first_cursor = page["page"]["nextCursor"]
        seen = list(page["items"])
        while page["page"]["nextCursor"]:
            page = self.send(process, "change.affected", {"session": locator, "limit": 37, "cursor": page["page"]["nextCursor"]})["payload"]
            seen.extend(page["items"])
        self.assertTrue(page["page"]["isComplete"])
        self.assertEqual(page["page"]["totalCount"], len(graph.nodes))
        self.assertEqual(sum(item["kind"] == "affectedNode" for item in seen), len(graph.nodes))
        self.assertEqual({item["value"]["nodeId"] for item in seen if item["kind"] == "affectedNode"}, {item.id for item in graph.nodes})

        first_preview = self.send(process, "change.preview", {"reference": changed["reference"], "limit": 25})["payload"]
        self.assertEqual(len(first_preview["reviewPage"]["items"]), 25)
        self.assertIsNotNone(first_preview["reviewPage"]["nextCursor"])
        invalid_cursor = self.send(process, "change.affected", {"session": locator, "limit": 38, "cursor": first_cursor})
        self.assertEqual(invalid_cursor["status"], "error")

    def test_sessions_are_process_local_and_exit_cleanly(self):
        first = self.process()
        begun = self.send(first, "change.begin", {"path": str(self.path), "projectId": "technical-project", "author": "test", "intent": "test"})["payload"]
        locator = {key: begun["reference"][key] for key in ("projectId", "sessionId")}
        first.stdin.close(); first.wait(timeout=10)
        second = self.process()
        missing = self.send(second, "change.show", {"session": locator})
        self.assertEqual(missing["status"], "error")
        exited = self.send(second, "host.exit", {})
        self.assertEqual(exited["status"], "ok")
        second.stdin.close(); self.assertEqual(second.wait(timeout=10), SUCCESS)

    def test_artifact_check_is_read_only_through_direct_and_ndjson_surfaces(self):
        artifact = self.root / "artifact.bin"; artifact.write_bytes(b"artifact")
        digest = hashlib.sha256(b"artifact").hexdigest()
        purpose = Node("purpose", "Purpose", "purpose")
        anchored = Node("anchor", "Anchor", "external-anchor", ("artifact",), (Attribute("artifact.path", GraphValue.text("artifact.bin")), Attribute("artifact.sha256", GraphValue.text(digest))))
        graph = Graph("artifact-project", "Artifacts", purpose.id, (purpose, anchored), (Edge("anchor-scope", anchored.id, purpose.id, "scope-parent"),))
        artifact_project = self.root / "artifact project.vw.db"
        ProjectStore().initialize(artifact_project, graph)
        before = artifact_project.read_bytes()
        output, error = io.StringIO(), io.StringIO()
        code = direct_command(["artifact", "check", str(artifact_project), "--allow-root", str(self.root), "--max-sample-bytes", "4"], output, error)
        self.assertEqual(code, SUCCESS, error.getvalue())
        direct_report = json.loads(output.getvalue())
        self.assertEqual(direct_report["matchedCount"], 1, direct_report)
        process = self.process()
        result = self.send(process, "artifact.check", {"path": str(artifact_project), "allowedRoots": [str(self.root)], "maxSampleBytes": 4})
        self.assertEqual(result["payload"]["matchedCount"], 1, result)
        self.assertEqual(artifact_project.read_bytes(), before)

    def test_artifact_check_preserves_the_caller_path_namespace(self):
        actual = self.root / "actual"; alias = self.root / "alias"
        actual.mkdir()
        try:
            os.symlink(actual, alias, target_is_directory=True)
        except (OSError, NotImplementedError) as exc:
            self.skipTest(f"directory links are not available to this Windows account: {exc}")
        content = b"artifact-through-authorized-alias"
        (actual / "artifact.bin").write_bytes(content)
        digest = hashlib.sha256(content).hexdigest()
        purpose = Node("purpose", "Purpose", "purpose")
        anchored = Node("anchor", "Anchor", "external-anchor", ("artifact",), (
            Attribute("artifact.path", GraphValue.text("artifact.bin")),
            Attribute("artifact.sha256", GraphValue.text(digest)),
        ))
        candidate = Graph(
            "artifact-alias", "Artifact alias", purpose.id, (purpose, anchored),
            (Edge("anchor-scope", anchored.id, purpose.id, "scope-parent"),),
        )
        ProjectStore().initialize(actual / "project.vw.db", candidate)
        output, error = io.StringIO(), io.StringIO()

        code = direct_command([
            "artifact", "check", str(alias / "project.vw.db"),
            "--allow-root", str(alias),
        ], output, error)

        self.assertEqual(code, SUCCESS, error.getvalue())
        report = json.loads(output.getvalue())
        self.assertEqual(report["matchedCount"], 1, report)

    def test_direct_exit_codes_and_traversal_flags_are_explicit(self):
        output, error = io.StringIO(), io.StringIO()
        self.assertEqual(direct_command(["project", "status", str(self.root / "missing.vw.db")], output, error), DOMAIN)
        output, error = io.StringIO(), io.StringIO()
        code = direct_command(["read", "scope", str(self.path), "purpose", "--max-depth", "1", "--max-visited-nodes", "100"], output, error)
        self.assertEqual(code, SUCCESS, error.getvalue())
        result = json.loads(output.getvalue())
        self.assertTrue(any(item["reason"] == "traversalDepthLimit" for item in result["omissions"]))
        output, error = io.StringIO(), io.StringIO()
        self.assertEqual(direct_command(["unknown"], output, error), USAGE)


if __name__ == "__main__":
    unittest.main()
