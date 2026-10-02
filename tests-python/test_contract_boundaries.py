import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parents[1] / "src-python"))

from validated_world.models import (
    Attribute, Edge, EntityKind, Graph, GraphValue, GraphValueKind, Node,
    Operation, OperationKind, ReviewDirection, camel_enum, operation_batch,
    validate_id, validate_metadata, validate_text,
)
from validated_world.protocol import (
    edge_dto, edge_from_dto, graph_dto, graph_from_dto, json_loads_strict,
    node_dto, operation_dto, operation_from_dto, value_dto,
)


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



if __name__ == '__main__':
    unittest.main()
