import io
import json
import os
import runpy
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).parents[1] / "src-python"))

from validated_world.application import sample_graph
from validated_world.cli import DOMAIN, SUCCESS, USAGE, direct_command, ndjson_loop
from validated_world.models import Edge, EntityKind, Node, Operation, OperationKind
from validated_world.protocol import operation_dto
from validated_world.storage import ProjectStore


class CliSurfaceTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(); self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.path = self.root / "sample.vw.db"
        ProjectStore().initialize(self.path, sample_graph())

    def run_direct(self, *arguments):
        output = io.StringIO(); error = io.StringIO()
        code = direct_command(list(arguments), output, error)
        return code, output.getvalue(), error.getvalue()

    def parsed_direct(self, *arguments):
        code, output, error = self.run_direct(*arguments)
        self.assertEqual((code, error), (SUCCESS, ""), arguments)
        return json.loads(output)

    def test_help_version_errors_and_project_sample_template_commands(self):
        self.assertIn("local semantic graph", self.run_direct("--help")[1])
        self.assertIn("ValidatedWorld.Cli", self.run_direct("--version")[1])
        self.assertEqual(self.run_direct("unknown")[0], USAGE)
        self.assertEqual(self.run_direct("project", "status", str(self.root / "missing.vw.db"))[0], DOMAIN)
        self.assertEqual(self.run_direct("sample", "create", "unknown", str(self.root / "bad.vw.db"))[0], USAGE)

        initialized = self.root / "initialized.vw.db"
        self.assertEqual(self.parsed_direct("project", "init", str(initialized), "cli-project", "CLI Project", "purpose", "Purpose")["nodeCount"], 1)
        self.assertEqual(self.parsed_direct("project", "status", str(initialized))["projectId"], "cli-project")
        self.assertEqual(self.parsed_direct("project", "open", str(initialized))["graph"]["purposeNodeId"], "purpose")
        self.assertTrue(self.parsed_direct("project", "verify", str(initialized))["isValid"])
        backup = self.root / "backup.vw.db"
        self.assertEqual(self.parsed_direct("project", "backup", str(initialized), str(backup))["projectId"], "cli-project")
        self.assertIn("CREATE TABLE", self.run_direct("project", "export-sql", str(initialized))[1])
        self.assertEqual(self.parsed_direct("project", "diff", str(initialized), str(backup), "--limit", "1")["totalCount"], 0)
        self.assertEqual(self.parsed_direct("project", "merge", str(initialized), str(backup), str(backup))["status"], "clean")

        self.assertEqual(self.parsed_direct("sample", "list"), ["technical-project"])
        created = self.root / "created-sample.vw.db"
        self.assertEqual(self.parsed_direct("sample", "create", "technical-project", str(created))["nodeCount"], 13)
        templates = self.parsed_direct("template", "list")
        self.assertEqual({item["id"] for item in templates}, {"code-development", "research-notebook"})
        self.assertEqual(self.parsed_direct("template", "describe", "code-development")["id"], "code-development")
        exported = self.root / "template.json"
        self.parsed_direct("template", "export", "research-notebook", str(exported))
        self.assertTrue(exported.is_file())
        instantiated = self.root / "code.vw.db"
        self.assertGreater(self.parsed_direct("template", "instantiate", "code-development", str(instantiated), "code", "Code", "Build code")["nodeCount"], 1)
        self.assertFalse(self.parsed_direct("ai", "status")["configured"])

    def test_module_entry_point_and_one_shot_options_fail_loudly(self):
        output = io.StringIO()
        with patch.object(sys, "argv", ["validated-world", "--version"]), \
             patch.object(sys, "stdout", output), self.assertRaises(SystemExit) as exited:
            runpy.run_module("validated_world", run_name="__main__")
        self.assertEqual(exited.exception.code, SUCCESS)
        self.assertIn("ValidatedWorld.Cli", output.getvalue())
        path = str(self.path)
        self.assertEqual(self.run_direct("project", "diff", path, path, "--bogus", "1")[0], USAGE)
        self.assertEqual(self.run_direct("project", "diff", path, path, "--limit", "1", "--limit", "2")[0], USAGE)
        self.assertEqual(self.run_direct("read", "nodes", path, "--limit", "1", "--limit", "2")[0], USAGE)
        self.assertEqual(self.run_direct("artifact", "check", path, "--bogus", "1")[0], USAGE)

    def test_module_output_is_safe_for_a_narrow_windows_console_encoding(self):
        environment = os.environ.copy()
        environment["PYTHONPATH"] = str(Path(__file__).parents[1] / "src-python")
        environment["PYTHONIOENCODING"] = "cp1252:strict"
        path = self.root / "unicode Ω project.vw.db"
        command = [sys.executable, "-m", "validated_world", "sample", "create", "technical-project", str(path)]

        created = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=environment, check=False)
        self.assertEqual(created.returncode, SUCCESS, created.stderr.decode("cp1252"))
        self.assertEqual(json.loads(created.stdout.decode("cp1252"))["path"], str(path.resolve()))

        duplicate = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=environment, check=False)
        self.assertEqual(duplicate.returncode, DOMAIN)
        self.assertIn("already exists", duplicate.stderr.decode("cp1252"))

    def test_every_direct_read_family_and_artifact_boundary(self):
        path = str(self.path)
        cases = (
            (("read", "node", path, "purpose"), lambda value: value["id"] == "purpose"),
            (("read", "edge", path, "scope-power-parent"), lambda value: value["id"] == "scope-power-parent"),
            (("read", "nodes", path, "--limit", "2"), lambda value: len(value["items"]) == 2),
            (("read", "edges", path, "--limit", "2"), lambda value: len(value["items"]) == 2),
            (("read", "search", path, "battery"), lambda value: value["totalCount"] > 0),
            (("read", "ranked-search", path, '"battery lasts"'), lambda value: value["totalCount"] > 0),
            (("read", "tag", path, "artifact"), lambda value: value["totalCount"] > 0),
            (("read", "scope", path, "scope-power", "--max-depth", "4", "--max-visited-nodes", "20"), lambda value: value["node"]["id"] == "scope-power"),
            (("read", "neighbors", path, "battery-assumption"), lambda value: value["totalCount"] > 0),
            (("read", "dependencies", path, "battery-assumption"), lambda value: value["totalCount"] > 0),
            (("read", "path", path, "battery-assumption", "runtime-test"), lambda value: value["found"]),
            (("read", "context", path, "battery-assumption,retention-policy"), lambda value: len(value["requestedNodeIds"]) == 2),
            (("read", "health", path), lambda value: value["nodeCount"] == 13),
            (("read", "report", path), lambda value: value["edgeCount"] == 17),
        )
        for arguments, check in cases:
            with self.subTest(arguments=arguments): self.assertTrue(check(self.parsed_direct(*arguments)))
        artifact = self.parsed_direct("artifact", "check", path, "--node-id", "power-design-anchor", "--max-anchors", "1", "--max-sample-bytes", "32", "--allow-root", str(self.root))
        self.assertEqual(artifact["totalAnchorCount"], 1)
        self.assertEqual(artifact["invalidAnchorCount"], 1)
        self.assertEqual(self.run_direct("read", "path", path, "only-one")[0], USAGE)
        self.assertEqual(self.run_direct("read", "nodes", path, "--unknown", "1")[0], USAGE)

    def test_direct_bulk_plan_and_paging(self):
        project = ProjectStore().load(self.path)
        manifest = self.root / "bulk.jsonl"
        node = Node("cli-scope", "CLI scope", "scope")
        edge = Edge("cli-scope-parent", node.id, "purpose", "scope-parent")
        records = [
            {"version": 1, "format": "validated-world-bulk-manifest", "projectId": project.graph.project_id, "baseFingerprint": project.state_fingerprint, "intent": "Add a CLI scope"},
            operation_dto(Operation(OperationKind.ADD, EntityKind.NODE, node.id, node=node)),
            operation_dto(Operation(OperationKind.ADD, EntityKind.EDGE, edge.id, edge=edge)),
        ]
        manifest.write_text("\n".join(json.dumps(item, separators=(",", ":")) for item in records) + "\n", encoding="utf-8")
        planned = self.parsed_direct("project", "bulk-plan", str(self.path), str(manifest), "--chunk-size", "2")
        self.assertEqual(planned["chunkOperationCount"], 2)

    def test_strict_ndjson_catalog_stateless_commands_and_types(self):
        requests = [
            ("host.help", {}), ("project.status", {"path": str(self.path)}),
            ("project.open", {"path": str(self.path)}), ("project.verify", {"path": str(self.path)}),
            ("project.export-sql", {"path": str(self.path)}), ("sample.list", {}),
            ("template.list", {}), ("template.describe", {"name": "research-notebook"}),
            ("read.report", {"path": str(self.path), "limit": 5}), ("ai.status", {}),
        ]
        lines = [json.dumps({"version": 1, "command": command, "payload": payload}) for command, payload in requests]
        lines += [
            json.dumps({"version": True, "command": "host.help", "payload": {}}),
            json.dumps({"version": 1, "command": "read.nodes", "payload": {"path": str(self.path), "limit": True}}),
            json.dumps({"version": 1, "command": "read.context", "payload": {"path": str(self.path), "nodeIds": [1]}}),
            json.dumps({"version": 1, "command": "host.exit", "payload": {}}),
        ]
        output = io.StringIO(); self.assertEqual(ndjson_loop(lines, output, io.StringIO()), SUCCESS)
        results = [json.loads(line) for line in output.getvalue().splitlines()]
        self.assertTrue(all(item["status"] == "ok" for item in results[:len(requests)]))
        commands = results[0]["payload"]["commands"]
        for required in ("change.focus", "change.expand", "change.affected", "change.omission-details", "read.report", "sample.create"):
            self.assertIn(required, commands)
        self.assertEqual([item["status"] for item in results[-4:]], ["error", "error", "error", "ok"])

    def test_stateful_ndjson_focus_bounds_expand_review_preview_write_patch_and_discard(self):
        output = io.StringIO()
        path = str(self.path)

        def send(command, payload):
            return json.dumps({"version": 1, "command": command, "payload": payload}, separators=(",", ":"))

        def latest(): return json.loads(output.getvalue().splitlines()[-1])

        def script():
            yield send("change.begin", {"path": path, "projectId": "technical-project", "author": "cli-test", "intent": "Add scope", "includeOperations": False, "includeProposedGraph": False})
            reference = latest()["payload"]["reference"]
            node = Node("new-scope", "A new scope", "scope")
            node_operation = operation_dto(Operation(OperationKind.ADD, EntityKind.NODE, node.id, node=node))
            yield send("change.focus", {"reference": reference, "operations": {"operations": [node_operation]}, "scopeParents": [{"childNodeId": node.id, "parentNodeId": "purpose", "edgeId": "new-scope-parent"}]})
            focused = latest()["payload"]
            yield send("change.apply", {"reference": reference, "operations": focused["operations"], "maxAffectedNodes": 1, "includeOperations": False, "includeProposedGraph": False})
            bounded = latest()["payload"]; bounded_reference = bounded["reference"]
            self.assertEqual(bounded["affected"]["status"], "inconclusive")
            omission = bounded["affected"]["omissions"][0]
            yield send("change.omission-details", {"reference": bounded_reference, "fingerprint": omission["detailsFingerprint"], "limit": 1})
            self.assertGreaterEqual(latest()["payload"]["totalCount"], 1)
            yield send("change.expand", {"reference": bounded_reference, "maxTraversalDepth": 100, "maxAffectedNodes": 100, "maxOutputItems": 100, "includeOperations": True, "includeProposedGraph": False})
            expanded = latest()["payload"]; reference = expanded["reference"]
            yield send("change.show", {"session": {"projectId": reference["projectId"], "sessionId": reference["sessionId"]}, "includeOperations": False, "includeProposedGraph": False})
            self.assertEqual(latest()["payload"]["operationCount"], 2)
            yield send("change.affected", {"session": {"projectId": reference["projectId"], "sessionId": reference["sessionId"]}, "limit": 2})
            affected = latest()["payload"]
            affected_items = list(affected["items"])
            while affected["page"]["nextCursor"]:
                yield send("change.affected", {"session": {"projectId": reference["projectId"], "sessionId": reference["sessionId"]}, "limit": 2, "cursor": affected["page"]["nextCursor"]})
                affected = latest()["payload"]
                affected_items.extend(affected["items"])
            nodes = [item["value"] for item in affected_items if item["kind"] == "affectedNode"]
            contexts = [item["value"] for item in affected_items if item["kind"] == "scopeContext"]
            yield send("change.review", {"reference": reference, "dispositions": [{"nodeId": item["nodeId"], "kind": "updated" if item["isDirectChange"] else "reviewedNoChange"} for item in nodes], "presentedContextNodeIds": [item["nodeId"] for item in contexts], "includeOperations": False, "includeProposedGraph": False})
            reviewed = latest()["payload"]; reference = reviewed["reference"]
            yield send("change.validate", {"reference": reference, "includeOperations": False, "includeProposedGraph": False})
            self.assertTrue(latest()["payload"]["readiness"]["isReady"])
            cursor = None
            while True:
                payload = {"reference": reference, "limit": 2}
                if cursor is not None: payload["cursor"] = cursor
                yield send("change.preview", payload)
                cursor = latest()["payload"]["reviewPage"]["nextCursor"]
                if cursor is None: break
            yield send("change.write", {"reference": reference, "bypassAiReview": False})
            self.assertEqual(latest()["payload"]["status"], "written")

            yield send("change.begin", {"path": path, "projectId": "technical-project", "author": "cli-test", "intent": "Patch purpose"})
            second = latest()["payload"]; reference = second["reference"]
            purpose = Node("purpose", "Updated purpose")
            operation = operation_dto(Operation(OperationKind.REPLACE, EntityKind.NODE, purpose.id, node=purpose))
            yield send("change.patch", {"reference": reference, "operations": {"operations": [operation]}, "includeOperations": False, "includeProposedGraph": False})
            reference = latest()["payload"]["reference"]
            yield send("change.discard", {"reference": reference})
            self.assertEqual(latest()["payload"]["projectId"], "technical-project")
            yield send("host.exit", {})

        self.assertEqual(ndjson_loop(script(), output, io.StringIO()), SUCCESS)
        graph = ProjectStore().load(self.path).graph
        self.assertIn("new-scope", {item.id for item in graph.nodes})
        self.assertEqual(next(item.text for item in graph.nodes if item.id == "purpose"), "An offline privacy-preserving sensor")

    def test_ndjson_dispatches_every_stateless_family(self):
        project = ProjectStore().load(self.path)
        manifest = self.root / "all-commands.jsonl"
        header = {"version": 1, "format": "validated-world-bulk-manifest", "projectId": project.graph.project_id, "baseFingerprint": project.state_fingerprint, "intent": "No changes"}
        bulk_node = Node("bulk-command-node", "Bulk command node", "note")
        bulk_edge = Edge("bulk-command-node-parent", bulk_node.id, "purpose", "scope-parent")
        manifest.write_text("\n".join((
            json.dumps(header),
            json.dumps(operation_dto(Operation(OperationKind.ADD, EntityKind.NODE, bulk_node.id, node=bulk_node))),
            json.dumps(operation_dto(Operation(OperationKind.ADD, EntityKind.EDGE, bulk_edge.id, edge=bulk_edge))),
        )) + "\n", encoding="utf-8")
        initialized = self.root / "ndjson-init.vw.db"
        backup = self.root / "ndjson-backup.vw.db"
        sample = self.root / "ndjson-sample.vw.db"
        exported = self.root / "ndjson-template.json"
        instantiated = self.root / "ndjson-template.vw.db"
        path = str(self.path)
        requests = (
            ("project.init", {"path": str(initialized), "projectId": "new", "title": "New", "purposeNodeId": "purpose", "purposeText": "Purpose"}),
            ("project.backup", {"sourcePath": str(initialized), "destinationPath": str(backup)}),
            ("project.diff", {"basePath": str(initialized), "targetPath": str(backup), "limit": 1}),
            ("project.merge", {"basePath": str(initialized), "oursPath": str(backup), "theirsPath": str(backup)}),
            ("project.bulk_plan", {"path": path, "manifestPath": str(manifest), "chunkSize": 2}),
            ("template.export", {"name": "research-notebook", "destinationPath": str(exported)}),
            ("template.instantiate", {"name": "code-development", "path": str(instantiated), "projectId": "code", "title": "Code", "purposeText": "Build"}),
            ("sample.create", {"sampleName": "technical-project", "path": str(sample)}),
            ("artifact.check", {"path": path, "nodeId": "power-design-anchor", "allowedRoots": [str(self.root)], "maxAnchors": 1, "maxSampleBytes": 1}),
            ("read.node", {"path": path, "entityId": "purpose", "expectedProjectId": "technical-project"}),
            ("read.edge", {"path": path, "entityId": "scope-power-parent"}),
            ("read.nodes", {"path": path, "limit": 1}),
            ("read.edges", {"path": path, "limit": 1}),
            ("read.search", {"path": path, "text": "battery", "limit": 1}),
            ("read.ranked_search", {"path": path, "text": "battery", "limit": 1}),
            ("read.tag", {"path": path, "tag": "artifact", "limit": 1}),
            ("read.scope", {"path": path, "nodeId": "scope-power", "limit": 1, "maxDepth": 2, "maxVisitedNodes": 10}),
            ("read.neighbors", {"path": path, "entityId": "battery-assumption", "limit": 1}),
            ("read.dependencies", {"path": path, "entityId": "battery-assumption", "limit": 1}),
            ("read.path", {"path": path, "sourceNodeId": "battery-assumption", "targetNodeId": "runtime-test", "maxDepth": 3, "maxVisitedNodes": 20}),
            ("read.context", {"path": path, "nodeIds": ["battery-assumption"], "maxDepth": 3, "maxVisitedNodes": 20}),
            ("read.health", {"path": path, "limit": 2}),
        )
        lines = [json.dumps({"version": 1, "command": command, "payload": payload}) for command, payload in requests]
        output = io.StringIO(); self.assertEqual(ndjson_loop(lines, output, io.StringIO()), SUCCESS)
        results = [json.loads(line) for line in output.getvalue().splitlines()]
        self.assertEqual(len(results), len(requests))
        self.assertTrue(all(item["status"] == "ok" for item in results), results)

    def test_ndjson_nested_shapes_and_scalar_types_are_rejected(self):
        reference = {name: "x" for name in ("projectId", "sessionId", "baseFingerprint", "operationFingerprint", "proposedFingerprint", "affectedFingerprint", "reviewFingerprint")}
        invalid = (
            ("unknown.command", {}),
            ("project.status", []),
            ("project.status", {}),
            ("project.status", {"path": str(self.path), "extra": 1}),
            ("change.show", {"session": {"projectId": "", "sessionId": "x"}}),
            ("change.write", {"reference": reference | {"projectId": ""}}),
            ("read.nodes", {"path": str(self.path), "cursor": 1}),
            ("read.nodes", {"path": str(self.path), "expectedProjectId": 1}),
            ("read.context", {"path": str(self.path), "nodeIds": "x"}),
            ("artifact.check", {"path": str(self.path), "allowedRoots": "x"}),
            ("change.review", {"reference": reference, "dispositions": {}, "presentedContextNodeIds": []}),
            ("change.review", {"reference": reference, "dispositions": [{"nodeId": "x", "kind": "wrong"}], "presentedContextNodeIds": []}),
            ("change.review", {"reference": reference, "dispositions": [{"nodeId": "x", "kind": "pending", "rationale": 1}], "presentedContextNodeIds": []}),
            ("change.review", {"reference": reference, "dispositions": [], "presentedContextNodeIds": "x"}),
            ("change.focus", {"reference": reference, "operations": {"operations": []}, "scopeParents": {}}),
            ("change.focus", {"reference": reference, "operations": {"operations": []}, "scopeParents": [{"childNodeId": "", "parentNodeId": "x", "edgeId": "x"}]}),
        )
        lines = [json.dumps({"version": 1, "command": command, "payload": payload}) for command, payload in invalid]
        output = io.StringIO(); ndjson_loop(lines, output, io.StringIO())
        results = [json.loads(line) for line in output.getvalue().splitlines()]
        self.assertEqual([item["status"] for item in results], ["error"] * len(invalid))


if __name__ == "__main__":
    unittest.main()
