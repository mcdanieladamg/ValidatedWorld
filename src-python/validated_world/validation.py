"""Deterministic structural validation, projection, scope and review arcs."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Iterable

from .models import Edge, EntityKind, Graph, Node, Operation, OperationKind, ReviewDirection, operation_batch, ordinal_key


@dataclass(frozen=True)
class Diagnostic:
    code: str
    message: str
    entity_id: str | None = None
    related_entity_id: str | None = None
    path: tuple[str, ...] = ()


@dataclass(frozen=True)
class ValidationResult:
    status: str
    diagnostics: tuple[Diagnostic, ...]

    @property
    def is_valid(self) -> bool:
        return self.status == "valid"


class GraphIndex:
    def __init__(self, graph: Graph):
        self.graph = graph
        self.nodes_by_id: dict[str, Node] = {}
        self.edges_by_id: dict[str, Edge] = {}
        self.edges_by_source: dict[str, list[Edge]] = {}
        self.edges_by_target: dict[str, list[Edge]] = {}
        self.scope_by_child: dict[str, list[Edge]] = {}
        self.children_by_parent: dict[str, list[str]] = {}
        self.review_arcs: list[tuple[str, str, str]] = []
        for node in graph.nodes:
            self.nodes_by_id.setdefault(node.id, node)
        for edge in graph.edges:
            self.edges_by_id.setdefault(edge.id, edge)
            self.edges_by_source.setdefault(edge.source, []).append(edge)
            self.edges_by_target.setdefault(edge.target, []).append(edge)
            if edge.relationship == "scope-parent":
                self.scope_by_child.setdefault(edge.source, []).append(edge)
                self.children_by_parent.setdefault(edge.target, []).append(edge.source)
            elif edge.review_direction in (ReviewDirection.SOURCE_TO_TARGET, ReviewDirection.BOTH):
                self.review_arcs.append((edge.id, edge.source, edge.target))
            if edge.relationship != "scope-parent" and edge.review_direction in (ReviewDirection.TARGET_TO_SOURCE, ReviewDirection.BOTH):
                self.review_arcs.append((edge.id, edge.target, edge.source))
        for values in (self.edges_by_source, self.edges_by_target, self.scope_by_child, self.children_by_parent):
            for key in values:
                values[key].sort(key=lambda x: ordinal_key(x.id) if isinstance(x, Edge) else ordinal_key(x))
        self.review_arcs.sort(key=lambda item: (ordinal_key(item[1]), ordinal_key(item[2]), ordinal_key(item[0])))

    def scope_parents(self, node_id: str) -> list[Edge]:
        return self.scope_by_child.get(node_id, [])

    def scope_children(self, node_id: str) -> list[str]:
        return self.children_by_parent.get(node_id, [])

    def descendants(self, node_id: str) -> list[str]:
        result: list[str] = []
        queue = list(self.scope_children(node_id))
        seen: set[str] = set()
        while queue:
            current = queue.pop(0)
            if current in seen:
                continue
            seen.add(current)
            result.append(current)
            queue.extend(self.scope_children(current))
        return result

    def upstream(self, node_id: str) -> list[str]:
        result: list[str] = []
        seen: set[str] = set()
        current = node_id
        while current not in seen:
            seen.add(current)
            result.append(current)
            parents = self.scope_parents(current)
            if len(parents) != 1:
                break
            current = parents[0].target
        return result


def validate_graph(graph: Graph, max_diagnostics: int = 2**31 - 1, max_traversal_depth: int = 2**31 - 1) -> ValidationResult:
    if not isinstance(max_diagnostics, int) or isinstance(max_diagnostics, bool) or max_diagnostics < 1:
        raise ValueError("max_diagnostics must be a positive integer")
    if not isinstance(max_traversal_depth, int) or isinstance(max_traversal_depth, bool) or max_traversal_depth < 1:
        raise ValueError("max_traversal_depth must be a positive integer")
    index = GraphIndex(graph)
    diagnostics: list[Diagnostic] = []
    inconclusive = False

    def add(code: str, message: str, entity: str | None = None, related: str | None = None, path: Iterable[str] = ()) -> None:
        nonlocal inconclusive
        if len(diagnostics) < max_diagnostics:
            diagnostics.append(Diagnostic(code, message, entity, related, tuple(path)))
        else:
            inconclusive = True

    node_counts: dict[str, int] = {}
    edge_counts: dict[str, int] = {}
    for node in graph.nodes:
        node_counts[node.id] = node_counts.get(node.id, 0) + 1
    for edge in graph.edges:
        edge_counts[edge.id] = edge_counts.get(edge.id, 0) + 1
    for key in sorted(node_counts, key=ordinal_key):
        if node_counts[key] > 1:
            add("duplicate-node-id", f"Node ID '{key}' occurs {node_counts[key]} times.", key)
    for key in sorted(edge_counts, key=ordinal_key):
        if edge_counts[key] > 1:
            add("duplicate-edge-id", f"Edge ID '{key}' occurs {edge_counts[key]} times.", key)
    for key in sorted(set(node_counts) & set(edge_counts), key=ordinal_key):
        add("entity-id-collision", f"Entity ID '{key}' is used by both a node and an edge.", key)

    for edge in graph.edges:
        if edge.source not in node_counts:
            add("missing-edge-source", f"Edge '{edge.id}' references missing source node '{edge.source}'.", edge.id, edge.source)
        if edge.target not in node_counts:
            add("missing-edge-target", f"Edge '{edge.id}' references missing target node '{edge.target}'.", edge.id, edge.target)

    if graph.purpose_node_id not in node_counts:
        add("missing-purpose-node", f"Purpose node '{graph.purpose_node_id}' does not exist.", graph.purpose_node_id)
    for child, edges in sorted(index.scope_by_child.items(), key=lambda item: ordinal_key(item[0])):
        if len(edges) > 1:
            add("multiple-scope-parents", f"Node '{child}' has {len(edges)} scope-parent edges; exactly one is required.", child)
        for edge in edges:
            if edge.review_direction != ReviewDirection.NONE:
                add("scope-parent-review-direction", f"Scope-parent edge '{edge.id}' must use review direction None.", edge.id)
    for edge in index.scope_parents(graph.purpose_node_id):
        add("purpose-has-scope-parent", f"Purpose node '{graph.purpose_node_id}' must not have a scope-parent edge.", edge.id, graph.purpose_node_id)
    for node in graph.nodes:
        if node.id != graph.purpose_node_id and not index.scope_parents(node.id):
            add("missing-scope-parent", f"Node '{node.id}' has no scope-parent edge; exactly one is required.", node.id)

    for node in graph.nodes:
        path = []
        seen: set[str] = set()
        current = node.id
        reached = False
        while True:
            path.append(current)
            if current == graph.purpose_node_id:
                reached = True
                break
            if current in seen:
                add("scope-cycle", f"Scope lineage for '{node.id}' repeats node '{current}'.", node.id, current, path)
                break
            seen.add(current)
            parents = index.scope_parents(current)
            if len(parents) != 1:
                break
            if len(path) >= max_traversal_depth:
                inconclusive = True
                add("traversal-depth-limit", f"Scope lineage validation for '{node.id}' reached the configured depth limit.", node.id, path=path)
                break
            current = parents[0].target
            if current not in node_counts:
                break
        if path and not reached:
            add("scope-does-not-reach-purpose", f"Scope lineage for '{node.id}' does not reach purpose node '{graph.purpose_node_id}'.", node.id, graph.purpose_node_id, path)

    diagnostics.sort(key=lambda item: (item.code, ordinal_key(item.entity_id or ""), ordinal_key(item.related_entity_id or ""), item.message))
    return ValidationResult("inconclusive" if inconclusive else ("valid" if not diagnostics else "invalid"), tuple(diagnostics))


def project_graph(graph: Graph, operations: Iterable[Operation]) -> tuple[Graph, tuple[Operation, ...]]:
    operations = operation_batch(operations)
    nodes = {item.id: item for item in graph.nodes}
    edges = {item.id: item for item in graph.edges}
    if set(nodes) & set(edges):
        raise ValueError("base graph uses an entity ID for both a node and an edge")
    for operation in operations:
        target = nodes if operation.entity_kind is EntityKind.NODE else edges
        other = edges if operation.entity_kind is EntityKind.NODE else nodes
        if operation.entity_id in other:
            raise ValueError(f"operation for '{operation.entity_id}' has the wrong entity kind")
        exists = operation.entity_id in target
        if operation.kind is OperationKind.ADD:
            if exists:
                raise ValueError(f"cannot add existing {operation.entity_kind.name.lower()} '{operation.entity_id}'")
            target[operation.entity_id] = operation.node if operation.node is not None else operation.edge
        elif operation.kind is OperationKind.REPLACE:
            if not exists:
                raise ValueError(f"cannot replace missing {operation.entity_kind.name.lower()} '{operation.entity_id}'")
            target[operation.entity_id] = operation.node if operation.node is not None else operation.edge
        elif operation.kind is OperationKind.REMOVE:
            if not exists:
                raise ValueError(f"cannot remove missing {operation.entity_kind.name.lower()} '{operation.entity_id}'")
            del target[operation.entity_id]
    return Graph(graph.project_id, graph.title, graph.purpose_node_id, tuple(nodes.values()), tuple(edges.values())), operations
