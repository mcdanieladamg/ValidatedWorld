"""Canonical graph domain types used by every Python adapter."""

from __future__ import annotations

from dataclasses import dataclass, field
from enum import IntEnum
import re
import unicodedata
from typing import Any, Iterable


def ordinal_key(value: str) -> bytes:
    """Sort text by UTF-16 code units for the canonical graph ordering."""
    return value.encode("utf-16-le", "surrogatepass")


def _has_control(value: str) -> bool:
    return any(unicodedata.category(ch) == "Cc" for ch in value)


def validate_id(value: str, name: str = "id") -> str:
    if not isinstance(value, str):
        raise TypeError(f"{name} must be text")
    if not value.strip():
        raise ValueError(f"{name} cannot be empty or whitespace-only")
    if _has_control(value):
        raise ValueError(f"{name} cannot contain control characters")
    return value


def validate_text(value: str, name: str = "text", allow_empty: bool = True) -> str:
    if not isinstance(value, str):
        raise TypeError(f"{name} must be text")
    if not allow_empty and not value.strip():
        raise ValueError(f"{name} must be non-empty")
    return value


def validate_metadata(value: str, name: str = "metadata") -> str:
    if not isinstance(value, str) or not value.strip() or _has_control(value):
        raise ValueError(f"{name} must be non-empty and free of control characters")
    return value


class GraphValueKind(IntEnum):
    TEXT = 0
    INTEGER = 1
    DECIMAL = 2
    BOOLEAN = 3
    SYMBOL = 4
    INSTANT = 5


_DECIMAL = re.compile(r"^-?(?:0|[1-9][0-9]*)(?:\.[0-9]*[1-9])?$")
_INSTANT = re.compile(r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}\+00:00$")


@dataclass(frozen=True)
class GraphValue:
    kind: GraphValueKind
    value: Any

    def __post_init__(self) -> None:
        kind = GraphValueKind(self.kind)
        object.__setattr__(self, "kind", kind)
        if kind is GraphValueKind.TEXT:
            validate_text(self.value, "text")
        elif kind is GraphValueKind.INTEGER:
            if isinstance(self.value, bool) or not isinstance(self.value, int):
                raise TypeError("integer graph values require an integer")
            if not -(2**63) <= self.value < 2**63:
                raise ValueError("integer graph values must fit signed 64-bit range")
        elif kind is GraphValueKind.DECIMAL:
            if not isinstance(self.value, str) or not _DECIMAL.fullmatch(self.value):
                raise ValueError("decimal is not in canonical base-10 form")
            if self.value.startswith("-0") and (self.value == "-0" or set(self.value[2:]) <= {"0", "."}):
                raise ValueError("negative zero is not canonical")
        elif kind is GraphValueKind.BOOLEAN:
            if not isinstance(self.value, bool):
                raise TypeError("boolean graph values require a boolean")
        elif kind is GraphValueKind.SYMBOL:
            validate_metadata(self.value, "symbol")
        elif kind is GraphValueKind.INSTANT:
            if not isinstance(self.value, str) or not _INSTANT.fullmatch(self.value):
                raise ValueError("instant must be canonical UTC round-trip text")

    @classmethod
    def text(cls, value: str) -> "GraphValue":
        return cls(GraphValueKind.TEXT, value)

    @classmethod
    def integer(cls, value: int) -> "GraphValue":
        return cls(GraphValueKind.INTEGER, value)

    @classmethod
    def decimal(cls, value: str) -> "GraphValue":
        return cls(GraphValueKind.DECIMAL, value)

    @classmethod
    def boolean(cls, value: bool) -> "GraphValue":
        return cls(GraphValueKind.BOOLEAN, value)

    @classmethod
    def symbol(cls, value: str) -> "GraphValue":
        return cls(GraphValueKind.SYMBOL, value)

    @classmethod
    def instant(cls, value: str) -> "GraphValue":
        return cls(GraphValueKind.INSTANT, value)

    def text_value(self) -> str:
        if self.kind is not GraphValueKind.TEXT:
            raise TypeError("graph value is not text")
        return self.value

    def __str__(self) -> str:
        if self.kind is GraphValueKind.BOOLEAN:
            return "true" if self.value else "false"
        return str(self.value)


@dataclass(frozen=True)
class Attribute:
    name: str
    value: GraphValue

    def __post_init__(self) -> None:
        validate_metadata(self.name, "attribute name")
        if not isinstance(self.value, GraphValue):
            raise TypeError("attribute value must be a GraphValue")


def canonical_tags(tags: Iterable[str] | None) -> tuple[str, ...]:
    values = sorted((validate_metadata(v, "tag") for v in (tags or ())), key=ordinal_key)
    if len(values) != len(set(values)):
        raise ValueError("duplicate tag")
    return tuple(values)


def canonical_attributes(attributes: Iterable[Attribute | tuple[str, GraphValue]] | None) -> tuple[Attribute, ...]:
    values = []
    for value in attributes or ():
        attr = value if isinstance(value, Attribute) else Attribute(value[0], value[1])
        values.append(attr)
    values.sort(key=lambda item: ordinal_key(item.name))
    if len({item.name for item in values}) != len(values):
        raise ValueError("duplicate attribute")
    return tuple(values)


@dataclass(frozen=True)
class Node:
    id: str
    text: str
    kind: str | None = None
    tags: tuple[str, ...] = field(default_factory=tuple)
    attributes: tuple[Attribute, ...] = field(default_factory=tuple)

    def __post_init__(self) -> None:
        validate_id(self.id)
        validate_text(self.text, "text", allow_empty=False)
        if self.kind is not None:
            validate_metadata(self.kind, "kind")
        object.__setattr__(self, "tags", canonical_tags(self.tags))
        object.__setattr__(self, "attributes", canonical_attributes(self.attributes))

    def attribute(self, name: str) -> GraphValue | None:
        return next((item.value for item in self.attributes if item.name == name), None)


class ReviewDirection(IntEnum):
    NONE = 0
    SOURCE_TO_TARGET = 1
    TARGET_TO_SOURCE = 2
    BOTH = 3


@dataclass(frozen=True)
class Edge:
    id: str
    source: str
    target: str
    relationship: str
    review_direction: ReviewDirection = ReviewDirection.NONE
    rationale: str | None = None
    tags: tuple[str, ...] = field(default_factory=tuple)
    attributes: tuple[Attribute, ...] = field(default_factory=tuple)

    def __post_init__(self) -> None:
        validate_id(self.id)
        validate_id(self.source, "source")
        validate_id(self.target, "target")
        validate_metadata(self.relationship, "relationship")
        object.__setattr__(self, "review_direction", ReviewDirection(self.review_direction))
        if self.rationale is not None:
            validate_text(self.rationale, "rationale")
        object.__setattr__(self, "tags", canonical_tags(self.tags))
        object.__setattr__(self, "attributes", canonical_attributes(self.attributes))


@dataclass(frozen=True)
class Graph:
    project_id: str
    title: str
    purpose_node_id: str
    nodes: tuple[Node, ...]
    edges: tuple[Edge, ...]

    def __post_init__(self) -> None:
        validate_id(self.project_id, "project_id")
        validate_text(self.title, "title", allow_empty=False)
        validate_id(self.purpose_node_id, "purpose_node_id")
        object.__setattr__(self, "nodes", tuple(sorted(self.nodes, key=lambda n: ordinal_key(n.id))))
        object.__setattr__(self, "edges", tuple(sorted(self.edges, key=lambda e: ordinal_key(e.id))))


class EntityKind(IntEnum):
    NODE = 0
    EDGE = 1


class OperationKind(IntEnum):
    ADD = 0
    REPLACE = 1
    REMOVE = 2


@dataclass(frozen=True)
class Operation:
    kind: OperationKind
    entity_kind: EntityKind
    entity_id: str
    node: Node | None = None
    edge: Edge | None = None

    def __post_init__(self) -> None:
        object.__setattr__(self, "kind", OperationKind(self.kind))
        object.__setattr__(self, "entity_kind", EntityKind(self.entity_kind))
        validate_id(self.entity_id, "entity_id")
        if self.kind is OperationKind.REMOVE:
            if self.node is not None or self.edge is not None:
                raise ValueError("remove operations cannot contain an entity")
        elif self.entity_kind is EntityKind.NODE:
            if self.node is None or self.node.id != self.entity_id or self.edge is not None:
                raise ValueError("node operation requires a matching node")
        elif self.edge is None or self.edge.id != self.entity_id or self.node is not None:
            raise ValueError("edge operation requires a matching edge")


def operation_batch(operations: Iterable[Operation]) -> tuple[Operation, ...]:
    values = list(operations)
    ids = [item.entity_id for item in values]
    if len(ids) != len(set(ids)):
        raise ValueError("an operation batch cannot contain duplicate entity IDs")
    return tuple(sorted(values, key=lambda item: (ordinal_key(item.entity_id), int(item.entity_kind))))


def camel_enum(value: IntEnum) -> str:
    names = {
        GraphValueKind: ["text", "integer", "decimal", "boolean", "symbol", "instant"],
        ReviewDirection: ["none", "sourceToTarget", "targetToSource", "both"],
        EntityKind: ["node", "edge"],
        OperationKind: ["add", "replace", "remove"],
    }
    return names[type(value)][int(value)]
