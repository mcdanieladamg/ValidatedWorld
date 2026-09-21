import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parents[1] / "src-python"))

from validated_world.application import Application, sample_graph
from validated_world.authoring import AuthoringConversation, AuthoringToolHost, INSTRUCTIONS, TOOL_DEFINITIONS, openai_authoring_response, serialize_authoring_request
from validated_world.models import EntityKind, Node, Operation, OperationKind
from validated_world.storage import ProjectStore


class AuthoringTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(); self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)

    def host(self):
        path = self.root / "authoring.vw.db"
        ProjectStore().initialize(path, sample_graph())
        return AuthoringToolHost(Application(), str(path)), path

    def test_defaults_and_tools_are_strict_without_bypass_or_forged_review(self):
        names = [item["name"] for item in TOOL_DEFINITIONS]
        self.assertIn("write_change", names)
        self.assertIn("ranked_search_graph", names)
        self.assertFalse(any("sql" in item.lower() or "bypass" in item.lower() for item in names))
        self.assertNotIn("review_change", names)
        self.assertFalse(any("disposition" in item.lower() for item in names))
        self.assertTrue(all(item["parameters"]["additionalProperties"] is False for item in TOOL_DEFINITIONS))

    def test_search_is_required_and_write_accounts_for_exact_review_set(self):
        host, path = self.host()
        self.assertTrue(host.execute("project_status", {})["value"]["exists"])
        self.assertTrue(host.execute("begin_change", {"intent": "Add a power note"})["ok"])
        node_args = {"mode": "add", "id": "power-note", "text": "Initial note", "kind": "note", "tags": [], "attributes": []}
        rejected = host.execute("put_node", node_args)
        self.assertFalse(rejected["ok"]); self.assertIn("search", rejected["error"].lower())
        host.execute("search_graph", {"text": "power note", "tag": None, "limit": 10})
        self.assertTrue(host.execute("put_node", node_args)["ok"])
        edge_args = {"mode": "add", "id": "power-note-parent", "source": "power-note", "target": "scope-power", "relationship": "scope-parent", "review_direction": "none", "rationale": None, "tags": [], "attributes": []}
        self.assertTrue(host.execute("put_edge", edge_args)["ok"])
        preview = host.execute("proposal_preview", {})["value"]
        self.assertEqual(preview["operationCount"], 2)
        written = host.execute("write_change", {})
        self.assertEqual(written["value"]["result"]["status"], "written")
        self.assertFalse(written["value"]["result"]["aiReviewBypassed"])
        self.assertIsNone(host.session)
        self.assertEqual(ProjectStore().load(path).graph.nodes[-1].id, "scope-privacy")
        self.assertIn("power-note", {item.id for item in ProjectStore().load(path).graph.nodes})

    def test_new_project_is_purpose_only_and_never_overwrites(self):
        path = self.root / "new project.vw.db"
        host = AuthoringToolHost(Application(), str(path))
        arguments = {"project_id": "new", "title": "New", "purpose_id": "purpose", "purpose_text": "Purpose"}
        created = host.execute("initialize_project", arguments)
        self.assertTrue(created["value"]["initialized"])
        self.assertEqual(len(ProjectStore().load(path).graph.nodes), 1)
        self.assertFalse(host.execute("initialize_project", arguments)["ok"])

    def test_preview_exposes_old_and_new_scope_lineages(self):
        host, _ = self.host()
        host.execute("project_status", {})
        host.execute("begin_change", {"intent": "Move runtime"})
        host.execute("put_edge", {"mode": "replace", "id": "runtime-scope-parent", "source": "runtime-test", "target": "scope-privacy", "relationship": "scope-parent", "review_direction": "none", "rationale": None, "tags": [], "attributes": []})
        preview = json.dumps(host.execute("proposal_preview", {})["value"], separators=(",", ":"))
        self.assertIn('"currentPath":["runtime-test","scope-power","purpose"]', preview)
        self.assertIn('"proposedPath":["runtime-test","scope-privacy","purpose"]', preview)

    def test_outbound_request_has_standalone_instructions_and_bounded_strict_tools(self):
        configuration = {"model": "test-model", "_apiKey": "offline-key"}
        serialized = serialize_authoring_request([{"role": "user", "content": "Help"}], config=configuration)
        parsed = json.loads(serialized)
        self.assertIn("search", parsed["instructions"].lower())
        self.assertIn("write_change", parsed["instructions"])
        self.assertFalse(parsed["parallel_tool_calls"])
        self.assertTrue(all(item["strict"] for item in parsed["tools"]))
        self.assertNotIn("offline-key", serialized)
        self.assertNotIn("bypassAiReview", serialized)
        self.assertEqual(parsed["instructions"], INSTRUCTIONS)

    def test_conversation_allows_final_text_after_tool_limit_and_resets_context_on_failure(self):
        _, path = self.host()
        responses = iter([
            {"responseId": "one", "text": None, "toolCall": {"callId": "call-1", "name": "project_status", "arguments": {}}},
            {"responseId": "two", "text": "Inspection finished.", "toolCall": None},
        ])
        conversation = AuthoringConversation(AuthoringToolHost(Application(), str(path)), lambda inputs, previous: next(responses), max_tool_calls=1)
        result = conversation.turn("Inspect")
        self.assertEqual(result["text"], "Inspection finished.")
        self.assertEqual(result["toolCallCount"], 1)
        self.assertEqual(result["warnings"], [])

        previous_values = []
        def recovering(inputs, previous):
            previous_values.append(previous)
            if len(previous_values) == 1: return {"responseId": "before", "text": None, "toolCall": {"callId": "call", "name": "project_status", "arguments": {}}}
            if len(previous_values) == 2: raise RuntimeError("transport")
            return {"responseId": "after", "text": "Recovered.", "toolCall": None}
        recovering_conversation = AuthoringConversation(AuthoringToolHost(Application(), str(path)), recovering)
        failed = recovering_conversation.turn("Inspect")
        self.assertIn("conversation-reset", failed["warnings"][0])
        recovered = recovering_conversation.turn("Continue")
        self.assertEqual(recovered["text"], "Recovered.")
        self.assertIsNone(previous_values[-1])

    def test_provider_parses_one_strict_tool_call_and_never_serializes_key(self):
        configuration = {"enabled": True, "configured": True, "provider": "openai", "model": "offline", "timeoutSeconds": 1, "pollIntervalSeconds": 0, "maxToolCallsPerTurn": 2, "_apiKey": "secret-key"}
        captured = {}
        def transport(request, timeout):
            captured["body"] = request.data.decode("utf-8"); captured["authorization"] = request.headers["Authorization"]
            return {"id": "response", "status": "completed", "output": [{"type": "function_call", "call_id": "call", "name": "project_status", "arguments": "{}"}], "usage": {"input_tokens": 2, "output_tokens": 1, "total_tokens": 3}}
        result = openai_authoring_response([{"role": "user", "content": "Inspect"}], config=configuration, transport=transport)
        self.assertEqual(result["toolCall"]["name"], "project_status")
        self.assertNotIn("secret-key", captured["body"])
        self.assertIn("Bearer", captured["authorization"])

    def test_provider_polls_one_background_response_and_rejects_bad_terminal_output(self):
        configuration = {"enabled": True, "configured": True, "provider": "openai", "model": "offline", "timeoutSeconds": 1, "pollIntervalSeconds": 0, "_apiKey": "secret-key"}
        calls = []
        responses = iter([
            {"id": "response/one", "status": "queued", "output": []},
            {"id": "response/one", "status": "in_progress", "output": []},
            {"id": "response/one", "status": "completed", "output": [{"type": "message", "content": [{"type": "output_text", "text": "Done."}]}]},
        ])
        def transport(request, timeout):
            calls.append((request.method, request.full_url, request.data))
            return next(responses)
        result = openai_authoring_response([{"role": "user", "content": "Inspect"}], config=configuration, transport=transport)
        self.assertEqual(result["text"], "Done.")
        self.assertEqual([item[0] for item in calls], ["POST", "GET", "GET"])
        self.assertIsNone(calls[1][2])
        self.assertTrue(calls[1][1].endswith("response%2Fone"))

        for value in (
            {"id": "x", "status": "failed", "output": []},
            {"id": "x", "status": "completed", "output": [{"type": "function_call", "call_id": "c", "name": "project_status", "arguments": '{"a":1,"a":2}'}]},
            {"id": "x", "status": "completed", "output": [{"type": "message", "content": [{"type": "refusal", "refusal": "No"}]}]},
            {"id": "x", "status": "completed", "output": [], "usage": {"input_tokens": True, "output_tokens": 1, "total_tokens": 2}},
        ):
            with self.subTest(value=value):
                with self.assertRaisesRegex(RuntimeError, "AI authoring provider failed"):
                    openai_authoring_response([], config=configuration, transport=lambda *_args, value=value: value)

    def test_material_question_does_not_mutate_project(self):
        _, path = self.host(); before = path.read_bytes()
        conversation = AuthoringConversation(AuthoringToolHost(Application(), str(path)), lambda inputs, previous: {"responseId": "question", "text": "Should this replace the measurement or only the assumption?", "toolCall": None})
        result = conversation.turn("Change the battery value")
        self.assertIn("measurement", result["text"])
        self.assertEqual(path.read_bytes(), before)

    def test_unknown_tool_and_unknown_arguments_are_safe_errors(self):
        host, _ = self.host()
        self.assertFalse(host.execute("unknown", {})["ok"])
        self.assertFalse(host.execute("project_status", {"extra": True})["ok"])
        self.assertFalse(host.execute("graph_health", {"limit": True})["ok"])
        self.assertFalse(host.execute("search_graph", {"text": "x", "tag": None, "limit": 0})["ok"])
        self.assertFalse(host.execute("put_node", {"mode": "add", "id": "x", "text": "x", "kind": None, "tags": [1], "attributes": []})["ok"])
        self.assertFalse(host.execute("put_node", {"mode": "add", "id": "x", "text": "x", "kind": None, "tags": [], "attributes": [{"name": "a", "kind": "text", "value": "x", "extra": 1}]})["ok"])

    def test_caller_sized_search_duplicate_protection_and_external_status_refresh(self):
        host, path = self.host()
        large_page = host.execute("search_graph", {"text": "power", "tag": None, "limit": 51})
        self.assertTrue(large_page["ok"])
        self.assertTrue(large_page["value"]["items"])
        host.execute("begin_change", {"intent": "Try an accidental duplicate"})
        duplicate = host.execute("put_node", {"mode": "add", "id": "battery-assumption", "text": "Duplicate", "kind": None, "tags": [], "attributes": []})
        self.assertFalse(duplicate["ok"])
        host.execute("discard_change", {})

        before = host.execute("project_status", {})["value"]["project"]["stateFingerprint"]
        external = Application()
        session = external.begin(str(path), "technical-project", "external", "External update")
        session = external.apply(session.reference(), (Operation(OperationKind.REPLACE, EntityKind.NODE, "battery-assumption", node=Node("battery-assumption", "Externally changed", "assumption")),))
        dispositions = [{"nodeId": item["nodeId"], "kind": "updated" if item["isDirectChange"] else "reviewedNoChange"} for item in session.affected_nodes]
        session = external.review(session.reference(), dispositions, [item["nodeId"] for item in session.scope_context])
        session.preview(100)
        self.assertEqual(external.write(session.reference(), bypass_ai_review=True)["status"], "written")
        after = host.execute("project_status", {})["value"]["project"]["stateFingerprint"]
        self.assertNotEqual(before, after)


if __name__ == "__main__":
    unittest.main()
