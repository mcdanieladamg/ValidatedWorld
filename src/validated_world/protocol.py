"""JSON protocol adapters.  Storage JSON and host JSON intentionally differ only
in enum representation: public protocol uses camel-case enum names."""

from __future__ import annotations

import json
from typing import Any

from .models import Attribute, Edge, EntityKind, Graph, GraphValue, Node, Operation, camel_enum


def value_dto(value: GraphValue) -> dict[str, Any]:
    return {
        "kind": camel_enum(value.kind),
        "text": value.value if value.kind.name in {"TEXT", "DECIMAL", "SYMBOL"} else None,
        "integer": value.value if value.kind.name == "INTEGER" else 0,
        "boolean": value.value if value.kind.name == "BOOLEAN" else False,
        "instant": value.value if value.kind.name == "INSTANT" else None,
    }


def attribute_dto(value: Attribute) -> dict[str, Any]:
    return {"name": value.name, "value": value_dto(value.value)}


def node_dto(value: Node) -> dict[str, Any]:
    return {"id": value.id, "text": value.text, "kind": value.kind, "tags": list(value.tags), "attributes": [attribute_dto(item) for item in value.attributes]}


def edge_dto(value: Edge) -> dict[str, Any]:
    return {"id": value.id, "source": value.source, "target": value.target, "relationship": value.relationship, "reviewDirection": camel_enum(value.review_direction), "rationale": value.rationale, "tags": list(value.tags), "attributes": [attribute_dto(item) for item in value.attributes]}


def graph_dto(value: Graph) -> dict[str, Any]:
    return {"projectId": value.project_id, "title": value.title, "purposeNodeId": value.purpose_node_id, "nodes": [node_dto(item) for item in value.nodes], "edges": [edge_dto(item) for item in value.edges]}


def operation_dto(value: Operation) -> dict[str, Any]:
    return {"kind": camel_enum(value.kind), "entityKind": camel_enum(value.entity_kind), "entityId": value.entity_id, "node": node_dto(value.node) if value.node else None, "edge": edge_dto(value.edge) if value.edge else None}


def json_loads_strict(text: str) -> Any:
    """Parse JSON while rejecting duplicate object members."""
    def pairs(items):
        result = {}
        for key, value in items:
            if key in result:
                raise ValueError(f"duplicate JSON member '{key}'")
            result[key] = value
        return result
    return json.loads(text, object_pairs_hook=pairs)


def _object(value: Any, required: set[str], name: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise TypeError(f"{name} must be an object")
    missing = required - set(value)
    unknown = set(value) - required
    if missing:
        raise ValueError(f"{name} is missing required member '{sorted(missing)[0]}'")
    if unknown:
        raise ValueError(f"{name} contains unknown member '{sorted(unknown)[0]}'")
    return value


def _array(value: Any, name: str) -> list[Any]:
    if not isinstance(value, list):
        raise TypeError(f"{name} must be an array")
    return value


def graph_from_dto(value: dict[str, Any]) -> Graph:
    value = _object(value, {"projectId", "title", "purposeNodeId", "nodes", "edges"}, "graph")
    return Graph(value["projectId"], value["title"], value["purposeNodeId"], tuple(node_from_dto(item) for item in _array(value["nodes"], "graph nodes")), tuple(edge_from_dto(item) for item in _array(value["edges"], "graph edges")))


def _value_from_dto(value: dict[str, Any]) -> GraphValue:
    value = _object(value, {"kind", "text", "integer", "boolean", "instant"}, "graph value")
    names = {"text": 0, "integer": 1, "decimal": 2, "boolean": 3, "symbol": 4, "instant": 5}
    if value["kind"] not in names:
        raise ValueError("graph value kind is unsupported")
    kind = names[value["kind"]]
    if isinstance(value["integer"], bool) or not isinstance(value["integer"], int):
        raise TypeError("graph value integer slot must be an integer")
    if not isinstance(value["boolean"], bool):
        raise TypeError("graph value boolean slot must be Boolean")
    if kind in (0, 2, 4):
        if not isinstance(value["text"], str) or value["integer"] != 0 or value["boolean"] or value["instant"] is not None:
            raise ValueError("graph value has noncanonical scalar slots")
        return GraphValue(kind, value["text"])
    if kind == 1:
        if value["text"] is not None or value["boolean"] or value["instant"] is not None:
            raise ValueError("graph value has noncanonical scalar slots")
        return GraphValue.integer(value["integer"])
    if kind == 3:
        if value["text"] is not None or value["integer"] != 0 or value["instant"] is not None:
            raise ValueError("graph value has noncanonical scalar slots")
        return GraphValue.boolean(value["boolean"])
    if value["text"] is not None or value["integer"] != 0 or value["boolean"] or not isinstance(value["instant"], str):
        raise ValueError("graph value has noncanonical scalar slots")
    return GraphValue.instant(value["instant"])


def node_from_dto(value: dict[str, Any]) -> Node:
    value = _object(value, {"id", "text", "kind", "tags", "attributes"}, "node")
    tags = _array(value["tags"], "node tags")
    attributes = _array(value["attributes"], "node attributes")
    return Node(value["id"], value["text"], value["kind"], tuple(tags), tuple(_attribute_from_dto(item) for item in attributes))


def edge_from_dto(value: dict[str, Any]) -> Edge:
    value = _object(value, {"id", "source", "target", "relationship", "reviewDirection", "rationale", "tags", "attributes"}, "edge")
    directions = {"none": 0, "sourceToTarget": 1, "targetToSource": 2, "both": 3}
    if value["reviewDirection"] not in directions:
        raise ValueError("edge reviewDirection is unsupported")
    return Edge(value["id"], value["source"], value["target"], value["relationship"], directions[value["reviewDirection"]], value["rationale"], tuple(_array(value["tags"], "edge tags")), tuple(_attribute_from_dto(item) for item in _array(value["attributes"], "edge attributes")))


def operation_from_dto(value: dict[str, Any]) -> Operation:
    value = _object(value, {"kind", "entityKind", "entityId", "node", "edge"}, "operation")
    kinds = {"add": 0, "replace": 1, "remove": 2}
    entities = {"node": 0, "edge": 1}
    if value["kind"] not in kinds or value["entityKind"] not in entities:
        raise ValueError("operation kind or entityKind is unsupported")
    kind = kinds[value["kind"]]
    entity = entities[value["entityKind"]]
    return Operation(kind, entity, value["entityId"], node_from_dto(value["node"]) if value.get("node") else None, edge_from_dto(value["edge"]) if value.get("edge") else None)


def _attribute_from_dto(value: Any) -> Attribute:
    value = _object(value, {"name", "value"}, "attribute")
    return Attribute(value["name"], _value_from_dto(value["value"]))
