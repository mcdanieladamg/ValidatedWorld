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
