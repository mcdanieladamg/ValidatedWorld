import json
import os
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parents[1] / "src-python"))

from validated_world.application import Application, sample_graph
from validated_world.config import ReviewConfig, load_authoring_config, load_review_config, semantic_review
from validated_world.models import Edge, EntityKind, Graph, Node, Operation, OperationKind
from validated_world.storage import ProjectStore


def config(**changes):
    values = dict(enabled=True, provider="openai", model="test-model", timeout_seconds=2,
                  live_tests=False, api_key="test-secret", max_request_bytes=None,
                  max_request_items=None, max_request_tokens=None, poll_interval_seconds=0)
    values.update(changes)
    return ReviewConfig(**values)


def request_payload():
    return {
        "version": 1,
        "project": {"projectId": "project"},
        "intent": "Change a claim",
        "binding": {"baseFingerprint": "a", "operationFingerprint": "o", "proposedFingerprint": "b", "affectedFingerprint": "c", "reviewFingerprint": "d"},
        "operations": [{"operation": {"entityId": "claim"}}],
        "affectedNodes": [{"nodeId": "claim"}, {"nodeId": "consumer"}],
        "evidenceEdges": [{"edgeId": "claim-consumer"}],
        "contextNodes": [{"nodeId": "purpose"}],
        "scopeTopologyChanges": [],
        "reviewDispositions": [],
        "currentValidation": {"diagnostics": []},
        "proposedValidation": {"diagnostics": []},
        "manifest": {"allowedCitationIds": ["claim", "consumer", "claim-consumer", "purpose"], "omissionGroups": []},
    }


def response(output, *, refusal=False):
    content = {"type": "refusal", "refusal": "Declined"} if refusal else {"type": "output_text", "text": json.dumps(output, separators=(",", ":"))}
    return {"id": "resp_test", "status": "completed", "output": [{"content": [content]}], "usage": {"input_tokens": 11, "output_tokens": 7, "total_tokens": 18}}


class OpenAiBoundaryTests(unittest.TestCase):
    def test_environment_precedence_boolean_validation_and_public_status_hide_keys(self):
        names = [name for name in os.environ if name.startswith("VW_AIREVIEW__") or name.startswith("VW_AIAUTHORING__") or name == "OPENAI_API_KEY"]
        previous = {name: os.environ[name] for name in names}
        try:
            for name in names: os.environ.pop(name, None)
            os.environ["OPENAI_API_KEY"] = "shared"
            os.environ["VW_AIREVIEW__OPENAI__APIKEY"] = "review"
            os.environ["VW_AIAUTHORING__OPENAI__APIKEY"] = "author"
            review = load_review_config(); author = load_authoring_config()
            self.assertEqual(review.api_key, "review")
            self.assertEqual(author["_apiKey"], "author")
            self.assertNotIn("apiKey", review.public())
            os.environ["VW_AIREVIEW__ENABLED"] = "yes"
            with self.assertRaisesRegex(ValueError, "true or false"):
                load_review_config()
        finally:
            for name in list(os.environ):
                if name.startswith("VW_AIREVIEW__") or name.startswith("VW_AIAUTHORING__") or name == "OPENAI_API_KEY":
                    os.environ.pop(name, None)
            os.environ.update(previous)

    def test_request_uses_strict_json_schema_and_makes_exactly_one_call(self):
        calls = []
        def transport(request, timeout):
            calls.append((request, timeout))
            return response({"decision": "allow", "summary": "No blocking concerns.", "concerns": []})
        result = semantic_review(request_payload(), config(), transport=transport)
        self.assertEqual((result["status"], result["decision"], len(calls)), ("complete", "allow", 1))
        self.assertEqual((result["provider"], result["model"], result["responseId"], result["binding"]["operationFingerprint"]), ("openai", "test-model", "resp_test", "o"))
        self.assertEqual(len(result["requestFingerprint"]), 64)
        body = json.loads(calls[0][0].data)
        self.assertEqual(body["text"]["format"]["type"], "json_schema")
        self.assertTrue(body["text"]["format"]["strict"])
        self.assertFalse(body["text"]["format"]["schema"]["additionalProperties"])
        self.assertEqual(body["tool_choice"], "none")
        self.assertNotIn("test-secret", calls[0][0].data.decode())

    def test_strict_output_requires_known_citations_and_consistent_decisions(self):
        cases = (
            {"decision": "allow", "summary": "Allowed", "concerns": [{"code": "x", "message": "x", "citations": [{"entityId": "claim"}]}]},
            {"decision": "block", "summary": "Blocked", "concerns": []},
            {"decision": "block", "summary": "Blocked", "concerns": [{"code": "x", "message": "x", "citations": [{"entityId": "invented"}]}]},
            {"decision": "allow", "summary": "Allowed", "concerns": [], "extra": True},
        )
        for output in cases:
            with self.subTest(output=output):
                result = semantic_review(request_payload(), config(), transport=lambda *_: response(output))
                self.assertEqual(result["status"], "inconclusive")
                self.assertEqual(result["failureCode"], "provider-failure")
        valid = {"decision": "block", "summary": "Conflict", "concerns": [{"code": "conflict", "message": "Claim conflicts.", "citations": [{"entityId": "claim"}, {"entityId": "consumer"}]}]}
        blocked = semantic_review(request_payload(), config(), transport=lambda *_: response(valid))
        self.assertEqual(blocked["decision"], "block")
        self.assertEqual(blocked["concerns"][0]["citations"], ["claim", "consumer"])

    def test_background_review_polls_the_created_response_without_recreating_it(self):
        calls = []
        values = iter([
            {"id": "review/1", "status": "queued", "output": []},
            {"id": "review/1", "status": "in_progress", "output": []},
            response({"decision": "allow", "summary": "No concerns.", "concerns": []}) | {"id": "review/1"},
        ])
        def transport(request, timeout):
            calls.append((request.method, request.full_url, request.data))
            return next(values)
        result = semantic_review(request_payload(), config(), transport=transport)
        self.assertEqual(result["decision"], "allow")
        self.assertEqual([item[0] for item in calls], ["POST", "GET", "GET"])
        self.assertTrue(calls[1][1].endswith("review%2F1"))
        self.assertIsNone(calls[1][2])

    def test_all_explicit_budgets_are_measured_before_dispatch(self):
        for field in ("max_request_bytes", "max_request_items", "max_request_tokens"):
            calls = []
            result = semantic_review(request_payload(), config(**{field: 1}), transport=lambda *args: calls.append(args))
            self.assertEqual(result["failureCode"], "request-budget-exceeded")
            self.assertEqual(calls, [])
            self.assertIn(field.removeprefix("max_request_").replace("request_", ""), result["measurement"]["exceededLimits"])

    def test_refusal_malformed_transport_and_timeout_have_no_automatic_retry(self):
        scenarios = (
            lambda *_: response({}, refusal=True),
            lambda *_: {"output": [{"content": [{"type": "output_text", "text": "not-json"}]}]},
            lambda *_: (_ for _ in ()).throw(TimeoutError("timeout")),
        )
        for transport in scenarios:
            calls = []
            def counted(*args):
                calls.append(args)
                return transport(*args)
            result = semantic_review(request_payload(), config(), transport=counted)
            self.assertEqual(result["status"], "inconclusive")
            self.assertEqual(len(calls), 1)

    def reviewed_session(self, reviewer):
        temporary = tempfile.TemporaryDirectory(); self.addCleanup(temporary.cleanup)
        path = Path(temporary.name) / "review.vw.db"
        store = ProjectStore(); store.initialize(path, sample_graph())
        application = Application(store, review_config_loader=lambda: config(), semantic_reviewer=reviewer)
        session = application.begin(str(path), "technical-project", "test", "change battery")
        changed = Node("battery-assumption", "Changed battery", "assumption")
        session = application.apply(session.reference(), (Operation(OperationKind.REPLACE, EntityKind.NODE, changed.id, node=changed),))
        session = application.review(session.reference(), [{"nodeId": item["nodeId"], "kind": "updated" if item["isDirectChange"] else "reviewedNoChange"} for item in session.affected_nodes], [item["nodeId"] for item in session.scope_context])
        session.preview(10_000)
        return application, session

    def test_complete_block_is_cached_failure_is_retryable_and_bypass_is_explicit(self):
        calls = []
        block = {"status": "complete", "decision": "block", "summary": "Blocked", "concerns": []}
        application, session = self.reviewed_session(lambda *_: calls.append(1) or block)
        first = application.write(session.reference()); second = application.write(session.reference())
        self.assertEqual((first["status"], second["status"], len(calls)), ("semanticReviewBlocked", "semanticReviewBlocked", 1))
        bypass = application.write(session.reference(), bypass_ai_review=True)
        self.assertEqual((bypass["status"], bypass["aiReviewBypassed"]), ("written", True))

        failures = []
        failed_app, failed_session = self.reviewed_session(lambda *_: failures.append(1) or {"status": "inconclusive", "decision": None, "summary": "Failed"})
        self.assertEqual(failed_app.write(failed_session.reference())["status"], "semanticReviewBlocked")
        self.assertEqual(failed_app.write(failed_session.reference())["status"], "semanticReviewBlocked")
        self.assertEqual(len(failures), 2)

    def test_write_gate_supplies_complete_bound_review_evidence(self):
        captured = []
        application, session = self.reviewed_session(lambda request, _config: captured.append(request) or {"status": "complete", "decision": "allow", "summary": "Allowed", "concerns": []})
        result = application.write(session.reference())
        self.assertEqual(result["status"], "written")
        request = captured[0]
        self.assertEqual(request["binding"]["operationFingerprint"], session.operation_fingerprint)
        self.assertEqual(request["project"]["purposeNodeId"], "purpose")
        self.assertEqual(request["manifest"]["operationCount"], len(request["operations"]))
        self.assertEqual(request["manifest"]["affectedNodeCount"], len(request["affectedNodes"]))
        self.assertEqual(request["manifest"]["contextNodeCount"], len(request["contextNodes"]))
        self.assertEqual(request["manifest"]["evidenceEdgeCount"], len(request["evidenceEdges"]))
        self.assertEqual({item["nodeId"] for item in request["reviewDispositions"]}, set(request["manifest"]["affectedNodeIds"]))
        self.assertTrue(set(request["manifest"]["evidenceEdgeIds"]).issubset(request["manifest"]["allowedCitationIds"]))
        self.assertIn("currentValidation", request)
        self.assertIn("proposedValidation", request)

    def test_review_planner_is_scope_aware_and_includes_old_new_edges_and_lineages(self):
        temporary = tempfile.TemporaryDirectory(); self.addCleanup(temporary.cleanup)
        path = Path(temporary.name) / "topology.vw.db"
        base = sample_graph(); detail = Node("battery-detail", "Battery detail", "fact")
        graph = Graph(base.project_id, base.title, base.purpose_node_id, (*base.nodes, detail), (*base.edges, Edge("battery-detail-parent", detail.id, "battery-assumption", "scope-parent")))
        ProjectStore().initialize(path, graph)
        captured = []
        app = Application(review_config_loader=lambda: config(), semantic_reviewer=lambda request, _config: captured.append(request) or {"status": "complete", "decision": "allow", "summary": "Allowed", "concerns": []})
        session = app.begin(str(path), graph.project_id, "test", "Move battery scope")
        replacement = Edge("battery-scope-parent", "battery-assumption", "scope-privacy", "scope-parent")
        session = app.apply(session.reference(), (Operation(OperationKind.REPLACE, EntityKind.EDGE, replacement.id, edge=replacement),))
        session = app.review(session.reference(), [{"nodeId": item["nodeId"], "kind": "reviewedNoChange"} for item in session.affected_nodes], [item["nodeId"] for item in session.scope_context])
        session.preview(max(1, len(session.review_items())))
        self.assertEqual(app.write(session.reference())["status"], "written")
        request = captured[0]; topology = request["scopeTopologyChanges"][0]
        self.assertEqual((topology["currentParentId"], topology["proposedParentId"]), ("scope-power", "scope-privacy"))
        self.assertEqual(set(topology["currentChildSubtreeIds"]), {"battery-assumption", "battery-detail"})
        self.assertEqual(set(topology["proposedChildSubtreeIds"]), {"battery-assumption", "battery-detail"})
        operation = request["operations"][0]
        self.assertEqual(operation["currentEdge"]["target"], "scope-power")
        self.assertEqual(operation["proposedEdge"]["target"], "scope-privacy")
        evidence = next(item for item in request["evidenceEdges"] if item["edgeId"] == "battery-scope-parent")
        self.assertIn("scope-topology", evidence["evidenceRole"])
        self.assertIn("battery-scope-parent", request["manifest"]["allowedCitationIds"])


if __name__ == "__main__":
    unittest.main()
