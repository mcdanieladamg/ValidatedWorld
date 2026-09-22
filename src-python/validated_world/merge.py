"""Read-only graph-aware three-way merge planning."""

from __future__ import annotations

from typing import Any

from .canonical import state_fingerprint
from .models import Edge, EntityKind, Graph, Node, Operation, OperationKind, ordinal_key
from .protocol import edge_dto, graph_dto, node_dto, operation_dto
from .storage import ProjectStore
from .validation import validate_graph


def merge_projects(base_path: str, ours_path: str, theirs_path: str) -> dict[str, Any]:
    store = ProjectStore(); base = store.load(base_path); ours = store.load(ours_path); theirs = store.load(theirs_path)
    if base.graph.project_id != ours.graph.project_id or base.graph.project_id != theirs.graph.project_id:
        raise ValueError("base, ours, and theirs must belong to the same project")
    conflicts: list[dict[str, Any]] = []
    for field, base_value, ours_value, theirs_value in (("title", base.graph.title, ours.graph.title, theirs.graph.title), ("purposeNodeId", base.graph.purpose_node_id, ours.graph.purpose_node_id, theirs.graph.purpose_node_id)):
        if not (base_value == ours_value == theirs_value):
            conflicts.append({"kind": "projectMetadataChanged", "entityId": "$project." + field, "entityKind": None, "changedFields": [field], "message": f"Project metadata '{field}' changed and cannot be applied by the graph-entity change contract."})
    base_nodes = {item.id: item for item in base.graph.nodes}; ours_nodes = {item.id: item for item in ours.graph.nodes}; theirs_nodes = {item.id: item for item in theirs.graph.nodes}
    base_edges = {item.id: item for item in base.graph.edges}; ours_edges = {item.id: item for item in ours.graph.edges}; theirs_edges = {item.id: item for item in theirs.graph.edges}
    all_node_ids = set(base_nodes) | set(ours_nodes) | set(theirs_nodes); all_edge_ids = set(base_edges) | set(ours_edges) | set(theirs_edges)
    cross_kind = all_node_ids & all_edge_ids
    for identifier in sorted(cross_kind, key=ordinal_key):
        conflicts.append({"kind": "entityKindChanged", "entityId": identifier, "entityKind": None, "changedFields": ["entityKind"], "message": f"Entity '{identifier}' is a node in one snapshot and an edge in another."})
    merged_nodes: dict[str, Node] = {}; merged_edges: dict[str, Edge] = {}
    _merge_kind("node", base_nodes, ours_nodes, theirs_nodes, merged_nodes, conflicts, cross_kind)
    _merge_kind("edge", base_edges, ours_edges, theirs_edges, merged_edges, conflicts, cross_kind)
    if conflicts:
        return _result(base, ours, theirs, "conflicted", None, (), None, conflicts)
    merged = Graph(base.graph.project_id, base.graph.title, base.graph.purpose_node_id, tuple(merged_nodes.values()), tuple(merged_edges.values()))
    validation = validate_graph(merged)
    if not validation.is_valid:
        return _result(base, ours, theirs, "invalid", merged, _operations_between(ours.graph, merged), validation, [])
    return _result(base, ours, theirs, "clean", merged, _operations_between(ours.graph, merged), validation, [])


def _merge_kind(kind: str, base: dict, ours: dict, theirs: dict, output: dict, conflicts: list[dict], cross_kind: set[str]) -> None:
    for identifier in sorted(set(base) | set(ours) | set(theirs), key=ordinal_key):
        if identifier in cross_kind: continue
        base_value = base.get(identifier); ours_value = ours.get(identifier); theirs_value = theirs.get(identifier)
        if ours_value == theirs_value: selected = ours_value
        elif ours_value == base_value: selected = theirs_value
        elif theirs_value == base_value: selected = ours_value
        else:
            category = "addedDifferently" if base_value is None else ("deletedAndModified" if ours_value is None or theirs_value is None else "modifiedDifferently")
            conflicts.append({"kind": category, "entityId": identifier, "entityKind": kind, "changedFields": _changed_fields(ours_value, theirs_value), "message": f"The {kind} '{identifier}' was changed incompatibly in ours and theirs."})
            continue
        if selected is not None: output[identifier] = selected


def _changed_fields(ours: Any, theirs: Any) -> list[str]:
    if ours is None or theirs is None: return []
    fields = ("text", "kind", "tags", "attributes") if isinstance(ours, Node) else ("source", "target", "relationship", "reviewDirection", "rationale", "tags", "attributes")
    names: list[str] = []
    for field in fields:
        left = getattr(ours, {"reviewDirection": "review_direction"}.get(field, field)); right = getattr(theirs, {"reviewDirection": "review_direction"}.get(field, field))
        if left != right: names.append(field)
    return names


def _operations_between(ours: Graph, merged: Graph) -> tuple[Operation, ...]:
    operations: list[Operation] = []
    _operations_for_kind(ours.nodes, merged.nodes, EntityKind.NODE, operations)
    _operations_for_kind(ours.edges, merged.edges, EntityKind.EDGE, operations)
    return tuple(operations)


def _operations_for_kind(ours: tuple, merged: tuple, kind: EntityKind, output: list[Operation]) -> None:
    ours_map = {item.id: item for item in ours}; merged_map = {item.id: item for item in merged}
    for identifier in sorted(set(ours_map) | set(merged_map), key=ordinal_key):
        old = ours_map.get(identifier); new = merged_map.get(identifier)
        if old is None: output.append(Operation(OperationKind.ADD, kind, identifier, node=new if kind is EntityKind.NODE else None, edge=new if kind is EntityKind.EDGE else None))
        elif new is None: output.append(Operation(OperationKind.REMOVE, kind, identifier))
        elif old != new: output.append(Operation(OperationKind.REPLACE, kind, identifier, node=new if kind is EntityKind.NODE else None, edge=new if kind is EntityKind.EDGE else None))


def _result(base, ours, theirs, status: str, merged: Graph | None, operations: tuple[Operation, ...], validation, conflicts: list[dict]) -> dict[str, Any]:
    return {"basePath": base.path, "oursPath": ours.path, "theirsPath": theirs.path, "projectId": base.graph.project_id, "baseFingerprint": base.state_fingerprint, "oursFingerprint": ours.state_fingerprint, "theirsFingerprint": theirs.state_fingerprint, "status": status, "isReadyToApply": status == "clean", "mergedFingerprint": state_fingerprint(merged) if merged is not None else None, "operationCount": len(operations), "operations": {"operations": [operation_dto(item) for item in operations]}, "mergedGraph": graph_dto(merged) if merged is not None else None, "validation": None if validation is None else {"status": validation.status, "diagnostics": [{"code": item.code, "message": item.message, "entityId": item.entity_id, "relatedEntityId": item.related_entity_id, "path": list(item.path)} for item in validation.diagnostics]}, "conflicts": conflicts}
