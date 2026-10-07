"""Canonical JSON and SHA-256 encodings for persistent project data."""

from __future__ import annotations

import hashlib
import json
import struct
from typing import Any

from .models import Attribute, Edge, Graph, GraphValue, Node, Operation, ordinal_key


def json_compact(value: Any) -> str:
    return json.dumps(value, ensure_ascii=True, separators=(",", ":"))


def value_storage(value: GraphValue) -> dict[str, Any]:
    return {
        "kind": int(value.kind),
        "text": value.value if value.kind.name in {"TEXT", "DECIMAL", "SYMBOL"} else None,
        "integer": value.value if value.kind.name == "INTEGER" else 0,
        "boolean": value.value if value.kind.name == "BOOLEAN" else False,
        "instant": value.value if value.kind.name == "INSTANT" else None,
    }


def tags_json(tags: tuple[str, ...]) -> str:
    return json_compact(list(tags))


def attributes_json(attributes: tuple[Attribute, ...]) -> str:
    return json_compact([{"name": item.name, "value": value_storage(item.value)} for item in attributes])


def _string(value: str) -> bytes:
    raw = value.encode("utf-8", "surrogatepass")
    return struct.pack(">i", len(raw)) + raw


def _nullable(value: str | None) -> bytes:
    return struct.pack(">i", -1) if value is None else _string(value)


def _integer(value: int) -> bytes:
    return struct.pack(">i", value)


def _bytes(value: bytes) -> bytes:
    return struct.pack(">i", len(value)) + value


def _strings(values: tuple[str, ...]) -> bytes:
    return _integer(len(values)) + b"".join(_string(item) for item in values)


def _attributes(values: tuple[Attribute, ...]) -> bytes:
    out = [_integer(len(values))]
    for item in values:
        out += [_string(item.name), _integer(int(item.value.kind)), _string(str(item.value))]
    return b"".join(out)


def _node(node: Node) -> bytes:
    return b"".join([_string(node.id), _string(node.text), _nullable(node.kind), _strings(node.tags), _attributes(node.attributes)])


def _edge(edge: Edge) -> bytes:
    return b"".join([
        _string(edge.id), _string(edge.source), _string(edge.target), _string(edge.relationship),
        _integer(int(edge.review_direction)), _nullable(edge.rationale), _strings(edge.tags), _attributes(edge.attributes),
    ])


def graph_bytes(graph: Graph) -> bytes:
    return b"".join([
        _string("graph"), _string(graph.project_id), _string(graph.title), _string(graph.purpose_node_id),
        _integer(len(graph.nodes)), b"".join(_node(item) for item in graph.nodes),
        _integer(len(graph.edges)), b"".join(_edge(item) for item in graph.edges),
    ])


def operation_bytes(operations: tuple[Operation, ...]) -> bytes:
    out = [_string("operation-batch"), _integer(len(operations))]
    for operation in operations:
        out += [_integer(int(operation.kind)), _integer(int(operation.entity_kind)), _string(operation.entity_id)]
        if operation.node is not None:
            out.append(_node(operation.node))
        if operation.edge is not None:
            out.append(_edge(operation.edge))
    return b"".join(out)


def state_fingerprint(graph: Graph) -> str:
    return hashlib.sha256(graph_bytes(graph)).hexdigest()


def operations_fingerprint(base_fingerprint: str, operations: tuple[Operation, ...]) -> str:
    return hashlib.sha256(_string("operations") + _string(base_fingerprint) + _bytes(operation_bytes(operations))).hexdigest()


def join_fingerprint(*values: str | bytes) -> str:
    data = b"".join(_bytes(v) if isinstance(v, bytes) else _string(v) for v in values)
    return hashlib.sha256(data).hexdigest()
