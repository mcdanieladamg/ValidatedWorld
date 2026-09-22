import json
import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).parents[1] / "src-python"))

from validated_world.application import Application, sample_graph
from validated_world.authoring import AuthoringConversation, AuthoringToolHost, openai_authoring_response
from validated_world.config import ReviewConfig, load_authoring_config, load_review_config, semantic_review
from validated_world.models import (
    Attribute, Edge, EntityKind, Graph, GraphValue, GraphValueKind, Node,
    Operation, OperationKind, ReviewDirection, camel_enum, operation_batch,
    validate_id, validate_metadata, validate_text,
)
from validated_world.protocol import (
    edge_dto, edge_from_dto, graph_dto, graph_from_dto, json_loads_strict,
    node_dto, operation_dto, operation_from_dto, value_dto,
)
from validated_world.storage import ProjectStore


def review_config(**changes):
    values = dict(enabled=True, provider="openai", model="offline", timeout_seconds=1,
                  live_tests=False, api_key="not-a-real-key", max_request_bytes=None,
                  max_request_items=None, max_request_tokens=None, poll_interval_seconds=0)
    values.update(changes)
    return ReviewConfig(**values)


def review_payload():
    return {
        "operations": [{"entityId": "changed"}],
        "affected": [{"nodeId": "affected"}],
        "scopeContext": [{"nodeId": "context"}],
        "currentValidation": {"diagnostics": []},
        "proposedValidation": {"diagnostics": []},
    }


def review_response(output, **changes):
    value = {
        "id": "response",
        "status": "completed",
        "output": [{"content": [{"type": "output_text", "text": json.dumps(output)}]}],
    }
    value.update(changes)
    return value


class ModelAndProtocolBoundaryTests(unittest.TestCase):
    def test_scalar_kinds_round_trip_through_public_protocol(self):
        values = (
            GraphValue.text("text"), GraphValue.integer(-(2**63)), GraphValue.integer(2**63 - 1),
            GraphValue.decimal("-12.34"), GraphValue.boolean(True), GraphValue.boolean(False),
            GraphValue.symbol("symbol"), GraphValue.instant("2026-09-20T12:34:56.1234567+00:00"),
        )
        for value in values:
            with self.subTest(value=value):
                node = Node("n", "Node", attributes=(Attribute("value", value),))
                self.assertEqual(graph_from_dto(graph_dto(Graph("p", "P", "n", (node,), ())))
                                 .nodes[0].attributes[0].value, value)
                dto = value_dto(value)
                self.assertEqual(set(dto), {"kind", "text", "integer", "boolean", "instant"})
        self.assertEqual(str(GraphValue.boolean(True)), "true")
        self.assertEqual(str(GraphValue.boolean(False)), "false")
        self.assertEqual(GraphValue.text("x").text_value(), "x")
        with self.assertRaises(TypeError):
            GraphValue.integer(1).text_value()

    def test_scalar_and_metadata_validation_rejects_noncanonical_values(self):
        invalid_calls = (
            lambda: validate_id(1), lambda: validate_id(" "), lambda: validate_id("a\x00b"),
            lambda: validate_text(1), lambda: validate_text(" ", allow_empty=False),
            lambda: validate_metadata(""), lambda: validate_metadata("bad\nvalue"),
            lambda: GraphValue.integer(True), lambda: GraphValue.integer(2**63),
            lambda: GraphValue.decimal("01"), lambda: GraphValue.decimal("-0"),
            lambda: GraphValue.boolean(1), lambda: GraphValue.symbol(" "),
            lambda: GraphValue.instant("2026-09-20"), lambda: Attribute("a", "not-a-value"),
            lambda: Node("n", "N", tags=("same", "same")),
            lambda: Node("n", "N", attributes=(("a", GraphValue.text("1")), ("a", GraphValue.text("2")))),
        )
        for call in invalid_calls:
            with self.subTest(call=call), self.assertRaises((TypeError, ValueError)):
                call()

    def test_operations_enforce_entity_shape_uniqueness_and_enum_names(self):
        node = Node("n", "Node")
        edge = Edge("e", "n", "n", "related", ReviewDirection.BOTH)
        operations = (
            Operation(OperationKind.ADD, EntityKind.NODE, "n", node=node),
            Operation(OperationKind.REPLACE, EntityKind.EDGE, "e", edge=edge),
            Operation(OperationKind.REMOVE, EntityKind.NODE, "old"),
        )
        self.assertEqual([item.entity_id for item in operation_batch(reversed(operations))], ["e", "n", "old"])
        self.assertEqual([camel_enum(item) for item in (GraphValueKind.INSTANT, ReviewDirection.SOURCE_TO_TARGET, EntityKind.EDGE, OperationKind.REMOVE)],
                         ["instant", "sourceToTarget", "edge", "remove"])
        invalid_calls = (
            lambda: Operation(OperationKind.REMOVE, EntityKind.NODE, "n", node=node),
            lambda: Operation(OperationKind.ADD, EntityKind.NODE, "n"),
            lambda: Operation(OperationKind.ADD, EntityKind.EDGE, "e", node=node),
            lambda: operation_batch((operations[0], operations[0])),
        )
        for call in invalid_calls:
            with self.subTest(call=call), self.assertRaises(ValueError):
                call()

    def test_graph_node_edge_and_operation_dtos_round_trip(self):
        node = Node("n", "Node", "fact", ("b", "a"), (Attribute("z", GraphValue.integer(2)), Attribute("a", GraphValue.text("x"))))
        edge = Edge("e", "n", "n", "rel", ReviewDirection.TARGET_TO_SOURCE, "why", ("tag",), ())
        graph = Graph("p", "Project", "n", (node,), (edge,))
        self.assertEqual(graph_from_dto(graph_dto(graph)), graph)
        self.assertEqual(edge_from_dto(edge_dto(edge)), edge)
        for operation in (
            Operation(OperationKind.ADD, EntityKind.NODE, "n", node=node),
            Operation(OperationKind.REPLACE, EntityKind.EDGE, "e", edge=edge),
            Operation(OperationKind.REMOVE, EntityKind.NODE, "old"),
        ):
            self.assertEqual(operation_from_dto(operation_dto(operation)), operation)
        self.assertEqual(json_loads_strict('{"a":1}'), {"a": 1})
        with self.assertRaisesRegex(ValueError, "duplicate"):
            json_loads_strict('{"a":1,"a":2}')

    def test_protocol_rejects_shape_enum_slot_and_collection_errors(self):
        node = node_dto(Node("n", "Node"))
        edge = edge_dto(Edge("e", "n", "n", "rel"))
        graph = graph_dto(Graph("p", "P", "n", (Node("n", "N"),), ()))
        cases = (
            lambda: graph_from_dto([]),
            lambda: graph_from_dto({**graph, "extra": 1}),
            lambda: graph_from_dto({key: value for key, value in graph.items() if key != "title"}),
            lambda: graph_from_dto({**graph, "nodes": {}}),
            lambda: edge_from_dto({**edge, "reviewDirection": "sideways"}),
            lambda: edge_from_dto({**edge, "tags": {}}),
            lambda: operation_from_dto({"kind": "move", "entityKind": "node", "entityId": "n", "node": node, "edge": None}),
            lambda: operation_from_dto({"kind": "add", "entityKind": "thing", "entityId": "n", "node": node, "edge": None}),
        )
        scalar = node["attributes"] = [{"name": "a", "value": {"kind": "integer", "text": None, "integer": 1, "boolean": False, "instant": None}}]
        del scalar
        cases += (
            lambda: graph_from_dto({**graph, "nodes": [{**node, "attributes": [{"name": "a", "value": {"kind": "mystery", "text": None, "integer": 0, "boolean": False, "instant": None}}]}]}),
            lambda: graph_from_dto({**graph, "nodes": [{**node, "attributes": [{"name": "a", "value": {"kind": "integer", "text": None, "integer": True, "boolean": False, "instant": None}}]}]}),
            lambda: graph_from_dto({**graph, "nodes": [{**node, "attributes": [{"name": "a", "value": {"kind": "boolean", "text": None, "integer": 0, "boolean": 1, "instant": None}}]}]}),
            lambda: graph_from_dto({**graph, "nodes": [{**node, "attributes": [{"name": "a", "value": {"kind": "text", "text": "x", "integer": 1, "boolean": False, "instant": None}}]}]}),
            lambda: graph_from_dto({**graph, "nodes": [{**node, "attributes": [{"name": "a", "value": {"kind": "instant", "text": None, "integer": 0, "boolean": False, "instant": None}}]}]}),
        )
        for call in cases:
            with self.subTest(call=call), self.assertRaises((TypeError, ValueError)):
                call()


class ConfigurationAndProviderBoundaryTests(unittest.TestCase):
    def setUp(self):
        names = [name for name in os.environ if name.startswith("VW_AI") or name == "OPENAI_API_KEY"]
        self.previous = {name: os.environ[name] for name in names}
        for name in names:
            os.environ.pop(name, None)
        self.addCleanup(self._restore)

    def _restore(self):
        for name in list(os.environ):
            if name.startswith("VW_AI") or name == "OPENAI_API_KEY":
                os.environ.pop(name, None)
        os.environ.update(self.previous)

    def test_environment_loaders_accept_overrides_and_reject_bad_positive_values(self):
        os.environ.update({
            "OPENAI_API_KEY": "shared", "VW_AIREVIEW__ENABLED": "false",
            "VW_AIREVIEW__PROVIDER": "custom", "VW_AIREVIEW__MODEL": "review-model",
            "VW_AIREVIEW__TIMEOUTSECONDS": "9", "VW_AIREVIEW__LIVETESTS": "true",
            "VW_AIREVIEW__MAXREQUESTBYTES": "100", "VW_AIREVIEW__MAXREQUESTITEMS": "11",
            "VW_AIREVIEW__MAXREQUESTTOKENS": "22", "VW_AIAUTHORING__MODEL": "author-model",
            "VW_AIAUTHORING__TIMEOUTSECONDS": "8", "VW_AIAUTHORING__MAXTOOLCALLSPERTURN": "7",
        })
        review = load_review_config(); author = load_authoring_config()
        self.assertEqual((review.enabled, review.provider, review.model, review.timeout_seconds, review.live_tests), (False, "custom", "review-model", 9, True))
        self.assertEqual((review.max_request_bytes, review.max_request_items, review.max_request_tokens), (100, 11, 22))
        self.assertEqual((author["model"], author["timeoutSeconds"], author["maxToolCallsPerTurn"]), ("author-model", 8, 7))
        self.assertTrue(author["configured"])
        for bad in ("zero", "0", "-1"):
            os.environ["VW_AIAUTHORING__TIMEOUTSECONDS"] = bad
            with self.subTest(bad=bad), self.assertRaisesRegex(ValueError, "positive integer"):
                load_authoring_config()

    def test_review_disabled_budget_components_and_fallback_citations(self):
        disabled = semantic_review({}, review_config(enabled=False), transport=lambda *_: self.fail("must not dispatch"))
        self.assertEqual(disabled["status"], "disabled")
        payload = review_payload()
        output = {"decision": "block", "summary": "Conflict", "concerns": [{"code": "c", "message": "m", "citations": [{"entityId": "changed"}, {"entityId": "affected"}, {"entityId": "context"}]}]}
        result = semantic_review(payload, review_config(), transport=lambda *_: review_response(output))
        self.assertEqual(result["decision"], "block")
        self.assertEqual(result["concerns"][0]["citations"], ["affected", "changed", "context"])

    def test_review_provider_rejects_every_malformed_boundary_without_retry(self):
        allow = {"decision": "allow", "summary": "Fine", "concerns": []}
        scenarios = (
            [],
            {"id": "", "status": "completed", "output": []},
            {"id": "r", "status": "failed", "output": []},
            {"id": "r", "status": "completed", "output": {}},
            {"id": "r", "status": "completed", "output": [1]},
            {"id": "r", "status": "completed", "output": [{"content": {}}]},
            {"id": "r", "status": "completed", "output": [{"content": [1]}]},
            review_response(allow, usage={"input_tokens": True, "output_tokens": 1, "total_tokens": 2}),
            review_response({"decision": "maybe", "summary": "x", "concerns": []}),
            review_response({"decision": "block", "summary": "x", "concerns": [{"code": "", "message": "m", "citations": [{"entityId": "changed"}]}]}),
        )
        for value in scenarios:
            calls = []
            result = semantic_review(review_payload(), review_config(), transport=lambda *_args, value=value: calls.append(1) or value)
            with self.subTest(value=value):
                self.assertEqual(result["status"], "inconclusive")
                self.assertEqual(calls, [1])

    def test_review_poll_mismatch_and_invalid_interval_are_inconclusive(self):
        for cfg, responses in (
            (review_config(poll_interval_seconds=-1), [{"id": "r", "status": "queued", "output": []}]),
            (review_config(), [{"id": "r", "status": "queued", "output": []}, {"id": "other", "status": "completed", "output": []}]),
            (review_config(), [{"id": "r", "status": "queued", "output": []}, {"id": "r", "status": None, "output": []}]),
        ):
            values = iter(responses)
            with self.subTest(cfg=cfg, responses=responses):
                self.assertEqual(semantic_review(review_payload(), cfg, transport=lambda *_: next(values))["status"], "inconclusive")


class AuthoringHostBoundaryTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(); self.addCleanup(self.temporary.cleanup)
        self.path = Path(self.temporary.name) / "authoring.vw.db"
        ProjectStore().initialize(self.path, sample_graph())
        self.host = AuthoringToolHost(Application(), str(self.path))

    def test_read_tools_tag_search_attributes_replace_remove_and_discard(self):
        self.assertTrue(self.host.execute("search_graph", {"text": None, "tag": "scope:power", "limit": 10})["ok"])
        for name, arguments in (
            ("ranked_search_graph", {"text": "battery", "limit": 5}),
            ("read_node", {"node_id": "purpose"}),
            ("read_edge", {"edge_id": "battery-scope-parent"}),
            ("read_scope", {"node_id": "scope-power", "limit": 10}),
            ("graph_health", {"limit": 10}),
        ):
            with self.subTest(name=name): self.assertTrue(self.host.execute(name, arguments)["ok"])
        self.assertTrue(self.host.execute("begin_change", {"intent": "Exercise tools"})["ok"])
        self.assertFalse(self.host.execute("begin_change", {"intent": "Again"})["ok"])
        attributes = [
            {"name": "text", "kind": "text", "value": "x"},
            {"name": "integer", "kind": "integer", "value": "2"},
            {"name": "decimal", "kind": "decimal", "value": "1.5"},
            {"name": "boolean", "kind": "boolean", "value": "true"},
            {"name": "symbol", "kind": "symbol", "value": "x"},
            {"name": "instant", "kind": "instant", "value": "2026-09-20T12:34:56.1234567+00:00"},
        ]
        replaced = self.host.execute("put_node", {"mode": "replace", "id": "battery-assumption", "text": "Changed", "kind": "assumption", "tags": [], "attributes": attributes})
        self.assertTrue(replaced["ok"], replaced)
        removed = self.host.execute("remove_entity", {"entity_kind": "edge", "id": "battery-requires-runtime"})
        self.assertTrue(removed["ok"], removed)
        self.assertTrue(self.host.execute("discard_change", {})["ok"])
        self.assertFalse(self.host.execute("discard_change", {})["ok"])

    def test_host_state_and_argument_errors_are_recoverable(self):
        absent = AuthoringToolHost(Application(), str(Path(self.temporary.name) / "absent.vw.db"))
        self.assertFalse(absent.execute("project_status", {})["value"]["exists"])
        self.assertFalse(absent.execute("read_node", {"node_id": "x"})["ok"])
        for name, arguments in (
            ("search_graph", {"text": None, "tag": None, "limit": 1}),
            ("search_graph", {"text": "x", "tag": "x", "limit": 1}),
            ("put_node", {"mode": "replace", "id": "purpose", "text": "x", "kind": None, "tags": [], "attributes": []}),
            ("proposal_preview", {}), ("write_change", {}), ("discard_change", {}),
        ):
            with self.subTest(name=name): self.assertFalse(self.host.execute(name, arguments)["ok"])
        self.host.execute("begin_change", {"intent": "Empty"})
        self.assertFalse(self.host.execute("write_change", {})["ok"])
        bad_attributes = {"mode": "replace", "id": "purpose", "text": "x", "kind": None, "tags": [], "attributes": [{"name": "b", "kind": "boolean", "value": "maybe"}]}
        self.assertFalse(self.host.execute("put_node", bad_attributes)["ok"])

    def test_authoring_provider_transport_and_output_contract_failures(self):
        cfg = {"enabled": True, "configured": True, "model": "offline", "timeoutSeconds": 1, "pollIntervalSeconds": 0, "_apiKey": "secret"}
        malformed = (
            [], {"id": "", "status": "completed", "output": []}, {"id": "r", "output": []},
            {"id": "r", "status": "completed", "output": {}},
            {"id": "r", "status": "completed", "output": [1]},
            {"id": "r", "status": "completed", "output": [{"type": "message", "content": {}}]},
            {"id": "r", "status": "completed", "output": [{"type": "message", "content": [1]}]},
            {"id": "r", "status": "completed", "output": [{"type": "function_call", "call_id": 1, "name": "x", "arguments": "{}"}]},
            {"id": "r", "status": "completed", "output": [
                {"type": "function_call", "call_id": "1", "name": "x", "arguments": "{}"},
                {"type": "function_call", "call_id": "2", "name": "x", "arguments": "{}"}]},
        )
        for value in malformed:
            with self.subTest(value=value), self.assertRaises(RuntimeError):
                openai_authoring_response([], config=cfg, transport=lambda *_args, value=value: value)
        with self.assertRaises(ValueError):
            openai_authoring_response([], config={**cfg, "enabled": False}, transport=lambda *_: {})
        byte_result = openai_authoring_response([], config=cfg, transport=lambda *_: json.dumps({"id": "r", "status": "completed", "output": [{"type": "message", "content": [{"type": "output_text", "text": "Done"}]}]}).encode())
        self.assertEqual(byte_result["text"], "Done")

    def test_conversation_validates_input_limit_and_reports_tool_limit(self):
        with self.assertRaises(ValueError): AuthoringConversation(self.host, max_tool_calls=True)
        conversation = AuthoringConversation(self.host, lambda *_: {}, max_tool_calls=1)
        with self.assertRaises(ValueError): conversation.turn(" ")
        responses = iter((
            {"responseId": "one", "text": None, "toolCall": {"callId": "1", "name": "project_status", "arguments": {}}},
            {"responseId": "two", "text": "Stopped", "toolCall": {"callId": "2", "name": "project_status", "arguments": {}}},
        ))
        limited = AuthoringConversation(self.host, lambda *_: next(responses), max_tool_calls=1).turn("Inspect")
        self.assertEqual(limited["toolCallCount"], 1)
        self.assertIn("tool-limit", limited["warnings"][0])


if __name__ == "__main__":
    unittest.main()
