"""Application services and process-local reviewed change sessions."""

from __future__ import annotations

from dataclasses import dataclass, field
import hashlib
import json
from pathlib import Path
import sqlite3
import uuid
from typing import Any, Iterable

from .canonical import operations_fingerprint, state_fingerprint
from .models import Edge, EntityKind, Graph, Node, Operation, OperationKind, ordinal_key
from .protocol import edge_dto, graph_dto, node_dto, operation_dto
from .queries import Queries
from .rules import RuleResult, evaluate_rules
from .storage import ProjectStore, StoredProject
from .validation import Diagnostic, GraphIndex, ValidationResult, project_graph, validate_graph


def _hash_json(value: Any) -> str:
    return hashlib.sha256(json.dumps(value, ensure_ascii=True, separators=(",", ":"), sort_keys=True).encode()).hexdigest()


def _validation(value: ValidationResult) -> dict:
    return {"status": value.status, "diagnostics": [{"code": item.code, "message": item.message, "entityId": item.entity_id, "relatedEntityId": item.related_entity_id, "path": list(item.path)} for item in value.diagnostics]}


def _rule_dto(value) -> dict:
    return {"ruleId": value.rule_id, "status": value.status, "message": value.message, "offenderIds": list(value.offender_ids), "omittedCount": value.omitted_count}


def _stored(value: StoredProject | None) -> dict | None:
    if value is None: return None
    return {"path": value.path, "projectId": value.graph.project_id, "title": value.graph.title, "purposeNodeId": value.graph.purpose_node_id, "nodeCount": len(value.graph.nodes), "edgeCount": len(value.graph.edges), "stateFingerprint": value.state_fingerprint, "createdUtc": value.created_utc, "updatedUtc": value.updated_utc}


@dataclass
class Session:
    base: StoredProject
    author: str
    intent: str
    session_id: str = field(default_factory=lambda: uuid.uuid4().hex)
    operations: tuple[Operation, ...] = ()
    proposed: Graph | None = None
    current_validation: ValidationResult | None = None
    proposed_validation: ValidationResult | None = None
    current_rules: RuleResult | None = None
    proposed_rules: RuleResult | None = None
    affected_nodes: list[dict] = field(default_factory=list)
    edge_changes: list[dict] = field(default_factory=list)
    scope_context: list[dict] = field(default_factory=list)
    dispositions: dict[str, dict] = field(default_factory=dict)
    presented_context: set[str] = field(default_factory=set)
    preview_signature: str | None = None
    preview_limit: int | None = None
    preview_seen: set[int] = field(default_factory=set)
    agent_decision: dict | None = None
    refresh: dict | None = None
    max_traversal_depth: int = 2**31 - 1
    max_affected_nodes: int = 2**31 - 1
    max_output_items: int = 2**31 - 1
    omissions: list[dict] = field(default_factory=list)
    omission_details: dict[str, list[dict]] = field(default_factory=dict)

    def rebuild(self) -> None:
        previous_affected = {item["nodeId"]: _hash_json({"current": item.get("currentNode"), "proposed": item.get("proposedNode")}) for item in self.affected_nodes}
        previous_context = {item["nodeId"]: _hash_json({"current": item.get("currentNode"), "proposed": item.get("proposedNode"), "lineages": item.get("lineages")}) for item in self.scope_context}
        old_dispositions = dict(self.dispositions)
        old_presented = set(self.presented_context)
        self.proposed, self.operations = project_graph(self.base.graph, self.operations)
        self.current_validation = validate_graph(self.base.graph)
        self.proposed_validation = validate_graph(self.proposed)
        self.current_rules = evaluate_rules(self.base.graph)
        self.proposed_rules = evaluate_rules(self.proposed)
        self._analyze()
        current_affected = {item["nodeId"]: _hash_json({"current": item.get("currentNode"), "proposed": item.get("proposedNode")}) for item in self.affected_nodes}
        current_context = {item["nodeId"]: _hash_json({"current": item.get("currentNode"), "proposed": item.get("proposedNode"), "lineages": item.get("lineages")}) for item in self.scope_context}
        invalidated_dispositions = sorted((node_id for node_id in old_dispositions if node_id in current_affected and previous_affected.get(node_id) != current_affected[node_id]), key=ordinal_key)
        invalidated_context = sorted((node_id for node_id in old_presented if node_id in current_context and previous_context.get(node_id) != current_context[node_id]), key=ordinal_key)
        self.dispositions = {
            item["nodeId"]: old_dispositions.get(item["nodeId"], {"nodeId": item["nodeId"], "kind": "pending", "rationale": None})
            if item["nodeId"] not in invalidated_dispositions else {"nodeId": item["nodeId"], "kind": "pending", "rationale": None}
            for item in self.affected_nodes
        }
        self.presented_context = (old_presented & set(current_context)) - set(invalidated_context)
        self.refresh = {"invalidatedDispositionNodeIds": invalidated_dispositions, "invalidatedContextNodeIds": invalidated_context}

    def _analyze(self) -> None:
        current = GraphIndex(self.base.graph)
        proposed = GraphIndex(self.proposed)
        direct = {item.entity_id for item in self.operations if item.entity_kind is EntityKind.NODE}
        current_arcs = {(edge_id, source, target) for edge_id, source, target in current.review_arcs}
        proposed_arcs = {(edge_id, source, target) for edge_id, source, target in proposed.review_arcs}

        seed_candidates: list[tuple[str, list[str], list[str], bool]] = [
            (node_id, [node_id], [], True) for node_id in sorted(direct, key=ordinal_key)
        ]
        scope_topology_children: set[str] = set()
        for operation in self.operations:
            if operation.entity_kind is not EntityKind.EDGE:
                continue
            for edge_id, source, _ in sorted(
                current_arcs | proposed_arcs,
                key=lambda value: (ordinal_key(value[1]), ordinal_key(value[2]), ordinal_key(value[0])),
            ):
                if edge_id == operation.entity_id:
                    seed_candidates.append((source, [source], [], False))
            old_edge = current.edges_by_id.get(operation.entity_id)
            new_edge = proposed.edges_by_id.get(operation.entity_id)
            for edge in (old_edge, new_edge):
                if edge is None or edge.relationship != "scope-parent":
                    continue
                scope_topology_children.add(edge.source)
                seed_candidates.append((edge.source, [edge.source], [], False))
                seed_candidates.append((edge.target, [edge.source, edge.target], [edge.id], False))

        arcs: dict[str, list[tuple[str, str]]] = {}
        for edge_id, source, target in sorted(current_arcs | proposed_arcs, key=lambda value: (ordinal_key(value[1]), ordinal_key(value[2]), ordinal_key(value[0]))):
            arcs.setdefault(source, []).append((edge_id, target))

        # A changed scope changes the meaning of its complete subtree. A changed
        # scope-parent edge changes the child subtree plus both the old and new
        # parents. Scope traversal is deliberately parent-to-child here and never
        # fans out through an unchanged sibling.
        expansion_roots = direct | scope_topology_children
        expansion_nodes = set(expansion_roots)
        for node_id in expansion_roots:
            expansion_nodes.update(current.descendants(node_id))
            expansion_nodes.update(proposed.descendants(node_id))
        scope_arcs = {
            (edge.id, edge.target, edge.source)
            for edge in (*self.base.graph.edges, *self.proposed.edges)
            if edge.relationship == "scope-parent" and edge.target in expansion_nodes
        }
        for edge_id, source, target in sorted(
            scope_arcs,
            key=lambda value: (ordinal_key(value[1]), ordinal_key(value[2]), ordinal_key(value[0])),
        ):
            value = (edge_id, target)
            if value not in arcs.setdefault(source, []):
                arcs[source].append(value)
                arcs[source].sort(key=lambda item: (ordinal_key(item[1]), ordinal_key(item[0])))

        seed_paths: dict[str, tuple[list[str], list[str], bool]] = {}
        for node_id, nodes, edges, is_direct in sorted(
            seed_candidates,
            key=lambda item: (not item[3], ordinal_key(item[0]), tuple(map(ordinal_key, item[1]))),
        ):
            existing = seed_paths.get(node_id)
            if existing is None:
                seed_paths[node_id] = (nodes, edges, is_direct)
            elif is_direct and not existing[2]:
                seed_paths[node_id] = (existing[0], existing[1], True)

        queue = list(sorted(seed_paths, key=ordinal_key)); seen: set[str] = set(); records = []; omitted: list[dict] = []
        while queue:
            node_id = queue.pop(0)
            if node_id in seen: continue
            seen.add(node_id)
            nodes, edges, direct_flag = seed_paths[node_id]
            current_node = next((node for node in self.base.graph.nodes if node.id == node_id), None)
            proposed_node = next((node for node in self.proposed.nodes if node.id == node_id), None)
            records.append({"nodeId": node_id, "isDirectChange": direct_flag, "distance": max(0, len(edges)), "explanation": {"nodes": nodes, "edges": edges}, "currentNode": node_dto(current_node) if current_node else None, "proposedNode": node_dto(proposed_node) if proposed_node else None})
            for edge_id, target in arcs.get(node_id, []):
                if len(edges) >= self.max_traversal_depth:
                    omitted.append({"reason": "traversalDepthLimit", "nodeId": node_id, "edgeId": edge_id, "message": "Affected traversal reached its configured depth limit."})
                    continue
                if target not in seen and target not in seed_paths:
                    seed_paths[target] = (nodes + [target], edges + [edge_id], False)
                    queue.append(target)
        records = sorted(records, key=lambda value: ordinal_key(value["nodeId"]))
        if len(records) > self.max_affected_nodes:
            omitted.extend({"reason": "affectedNodeLimit", "nodeId": item["nodeId"], "edgeId": None, "message": "Affected-node collection reached its configured limit."} for item in records[self.max_affected_nodes:])
            records = records[:self.max_affected_nodes]
        self.affected_nodes = records
        self.edge_changes = []
        for operation in self.operations:
            if operation.entity_kind is not EntityKind.EDGE: continue
            current_edge = next((edge for edge in self.base.graph.edges if edge.id == operation.entity_id), None)
            proposed_edge = next((edge for edge in self.proposed.edges if edge.id == operation.entity_id), None)
            self.edge_changes.append({"operation": operation_dto(operation), "currentEdge": edge_dto(current_edge) if current_edge else None, "proposedEdge": edge_dto(proposed_edge) if proposed_edge else None})
        affected_ids = {item["nodeId"] for item in self.affected_nodes}
        context_lineages: dict[str, list[dict]] = {}
        for item in self.affected_nodes:
            current_path = current.upstream(item["nodeId"]) if item["nodeId"] in current.nodes_by_id else []
            proposed_path = proposed.upstream(item["nodeId"]) if item["nodeId"] in proposed.nodes_by_id else []
            lineage = {
                "affectedNodeId": item["nodeId"],
                "currentPath": current_path,
                "proposedPath": proposed_path,
            }
            for node_id in dict.fromkeys((*current_path, *proposed_path)):
                if node_id not in affected_ids:
                    context_lineages.setdefault(node_id, []).append(lineage)
        self.scope_context = []
        for node_id in sorted(context_lineages, key=ordinal_key):
            current_node = current.nodes_by_id.get(node_id)
            proposed_node = proposed.nodes_by_id.get(node_id)
            self.scope_context.append({
                "nodeId": node_id,
                "lineages": sorted(context_lineages[node_id], key=lambda value: ordinal_key(value["affectedNodeId"])),
                "currentNode": node_dto(current_node) if current_node else None,
                "proposedNode": node_dto(proposed_node) if proposed_node else None,
            })
        total_output = len(self.affected_nodes) + len(self.edge_changes) + len(self.scope_context)
        if total_output > self.max_output_items:
            remaining = self.max_output_items
            affected = self.affected_nodes[:remaining]; remaining -= len(affected)
            edges = self.edge_changes[:max(0, remaining)]; remaining -= len(edges)
            context = self.scope_context[:max(0, remaining)]
            included = {item["nodeId"] for item in affected} | {item["nodeId"] for item in context}
            for item in (*self.affected_nodes, *self.scope_context):
                if item["nodeId"] not in included:
                    omitted.append({"reason": "outputLimit", "nodeId": item["nodeId"], "edgeId": None, "message": "Affected output reached its configured item limit."})
            self.affected_nodes, self.edge_changes, self.scope_context = affected, edges, context
        grouped = []
        self.omission_details = {}
        for reason in sorted({item["reason"] for item in omitted}):
            details = [item for item in omitted if item["reason"] == reason]
            fingerprint = _hash_json(details)
            self.omission_details[fingerprint] = details
            grouped.append({"reason": reason, "count": len(details), "sample": details[:5], "detailsFingerprint": fingerprint, "message": details[0]["message"]})
        self.omissions = grouped

    @property
    def base_fingerprint(self) -> str: return self.base.state_fingerprint

    @property
    def proposed_fingerprint(self) -> str: return state_fingerprint(self.proposed)

    @property
    def operation_fingerprint(self) -> str: return operations_fingerprint(self.base.state_fingerprint, self.operations)

    @property
    def affected_fingerprint(self) -> str: return _hash_json({"current": state_fingerprint(self.base.graph), "proposed": self.proposed_fingerprint, "operations": [operation_dto(item) for item in self.operations], "affected": self.affected_nodes, "context": self.scope_context})

    @property
    def review_fingerprint(self) -> str: return _hash_json({"dispositions": [self.dispositions[key] for key in sorted(self.dispositions, key=ordinal_key)], "context": sorted(self.presented_context, key=ordinal_key)})

    def reference(self) -> dict:
        return {"projectId": self.base.graph.project_id, "sessionId": self.session_id, "baseFingerprint": self.base_fingerprint, "operationFingerprint": self.operation_fingerprint, "proposedFingerprint": self.proposed_fingerprint, "affectedFingerprint": self.affected_fingerprint, "reviewFingerprint": self.review_fingerprint}

    def readiness(self) -> dict:
        pending = [item["nodeId"] for item in self.affected_nodes if self.dispositions.get(item["nodeId"], {}).get("kind") == "pending"]
        missing = [item["nodeId"] for item in self.scope_context if item["nodeId"] not in self.presented_context]
        blockers = []
        if not self.proposed_validation.is_valid: blockers.append("The proposed graph is not structurally valid.")
        if not self.proposed_rules.is_valid: blockers.append("The proposed graph does not satisfy all active graph rules.")
        if pending: blockers.append("Affected nodes still have pending review dispositions.")
        if missing: blockers.append("Required scope context has not been presented.")
        if self.omissions: blockers.append("Affected analysis is inconclusive because configured bounds omitted evidence.")
        return {"isReady": not blockers, "analysisStatus": "inconclusive" if self.omissions else "complete", "proposedValidationStatus": self.proposed_validation.status, "pendingCount": len(pending), "missingContextCount": len(missing), "blockers": sorted(blockers)}

    def _affected_items(self) -> list[dict]:
        items = []
        items.extend({"kind": "affectedNode", "value": item} for item in self.affected_nodes)
        items.extend({"kind": "edgeChange", "value": item} for item in self.edge_changes)
        items.extend({"kind": "scopeContext", "value": item} for item in self.scope_context)
        return items

    def affected_summary(self) -> dict:
        direct_node_ids = {item.entity_id for item in self.operations if item.entity_kind is EntityKind.NODE}
        return {
            "status": "inconclusive" if self.omissions else "complete",
            "currentValidationStatus": self.current_validation.status,
            "currentValidationDiagnosticCount": len(self.current_validation.diagnostics),
            "proposedValidationStatus": self.proposed_validation.status,
            "proposedValidationDiagnosticCount": len(self.proposed_validation.diagnostics),
            "currentRuleStatus": self.current_rules.status,
            "proposedRuleStatus": self.proposed_rules.status,
            "proposedRuleDiagnosticCount": len(self.proposed_rules.diagnostics),
            "directNodeCount": len(direct_node_ids),
            "affectedNodeCount": len(self.affected_nodes),
            "edgeChangeCount": len(self.edge_changes),
            "scopeContextCount": len(self.scope_context),
            "omissions": self.omissions,
        }

    def affected(self, limit: int = 100, cursor: str | None = None) -> dict:
        if not isinstance(limit, int) or isinstance(limit, bool) or limit < 1:
            raise ValueError("the affected-analysis page size must be positive")
        items = self._affected_items()
        signature = _hash_json({"reference": self.reference(), "query": "change.affected", "limit": limit})
        offset = 0
        if cursor is not None:
            try:
                raw = __import__("base64").b64decode(cursor, validate=True).decode()
                expected, cursor_limit, cursor_offset = raw.split(":", 2)
                if expected != signature or int(cursor_limit) != limit:
                    raise ValueError
                offset = int(cursor_offset)
                if offset < 0 or offset > len(items):
                    raise ValueError
            except Exception as error:
                raise ValueError("the affected-analysis cursor is invalid for this exact revision and page size") from error
        page_items = items[offset:offset + limit]
        next_offset = offset + len(page_items)
        next_cursor = __import__("base64").b64encode(f"{signature}:{limit}:{next_offset}".encode()).decode() if next_offset < len(items) else None
        return self.affected_summary() | {
            "items": page_items,
            "page": {"offset": offset, "limit": limit, "totalCount": len(items), "nextCursor": next_cursor, "isComplete": next_cursor is None},
        }

    def snapshot(self, include_operations=False, include_proposed_graph=False) -> dict:
        disposition_counts: dict[str, int] = {}
        for item in self.dispositions.values():
            disposition_counts[item["kind"]] = disposition_counts.get(item["kind"], 0) + 1
        return {"path": self.base.path, "author": self.author, "intent": self.intent, "createdUtc": self.base.created_utc, "updatedUtc": self.base.updated_utc, "reference": self.reference(), "operationCount": len(self.operations), "proposedNodeCount": len(self.proposed.nodes), "proposedEdgeCount": len(self.proposed.edges), "operations": {"operations": [operation_dto(item) for item in self.operations]} if include_operations else None, "proposedGraph": graph_dto(self.proposed) if include_proposed_graph else None, "affected": self.affected_summary(), "dispositionCounts": disposition_counts, "presentedContextCount": len(self.presented_context), "readiness": self.readiness(), "refresh": self.refresh, "agentReview": self.agent_decision}

    def review_items(self) -> list[dict]:
        items: list[dict] = []
        for operation in self.operations:
            items.append({"ordinal": len(items), "kind": "operation", "operation": operation_dto(operation)})
        for affected in self.affected_nodes:
            items.append({"ordinal": len(items), "kind": "affectedNode", "affectedNode": affected})
        for edge_change in self.edge_changes:
            items.append({"ordinal": len(items), "kind": "edgeChange", "edgeChange": edge_change})
        for context in self.scope_context:
            items.append({"ordinal": len(items), "kind": "scopeContext", "scopeContext": context})
        for disposition in [self.dispositions[key] for key in sorted(self.dispositions, key=ordinal_key)]:
            items.append({"ordinal": len(items), "kind": "disposition", "disposition": disposition})
        for diagnostic in self.current_validation.diagnostics:
            items.append({"ordinal": len(items), "kind": "currentValidationDiagnostic", "diagnostic": {"code": diagnostic.code, "message": diagnostic.message, "entityId": diagnostic.entity_id, "path": list(diagnostic.path)}})
        for diagnostic in self.proposed_validation.diagnostics:
            items.append({"ordinal": len(items), "kind": "proposedValidationDiagnostic", "diagnostic": {"code": diagnostic.code, "message": diagnostic.message, "entityId": diagnostic.entity_id, "path": list(diagnostic.path)}})
        return items

    def preview(self, limit: int = 100, cursor: str | None = None) -> dict:
        if limit < 1:
            raise ValueError("the proposal preview page size must be positive")
        items = self.review_items()
        signature = _hash_json({"reference": self.reference(), "limit": limit})
        offset = 0
        if cursor is not None:
            try:
                encoded = json.loads(json.dumps(cursor))
                raw = __import__("base64").b64decode(encoded, validate=True).decode()
                expected, cursor_limit, cursor_offset = raw.split(":", 2)
                if expected != signature or int(cursor_limit) != limit:
                    raise ValueError
                offset = int(cursor_offset)
            except Exception as error:
                raise ValueError("the proposal preview cursor is invalid for this exact revision and page size") from error
        self.preview_signature = signature
        self.preview_limit = limit
        page_items = items[offset:offset + limit]
        self.preview_seen.update(item["ordinal"] for item in page_items)
        next_offset = offset + len(page_items)
        next_cursor = __import__("base64").b64encode(f"{signature}:{limit}:{next_offset}".encode()).decode() if next_offset < len(items) else None
        return {"revision": self.reference()["operationFingerprint"], "projectId": self.base.graph.project_id, "title": self.base.graph.title, "intent": self.intent, "operationCount": len(self.operations), "proposedNodeCount": len(self.proposed.nodes), "proposedEdgeCount": len(self.proposed.edges), "affectedNodeCount": len(self.affected_nodes), "edgeChangeCount": len(self.edge_changes), "scopeContextCount": len(self.scope_context), "omissionCount": sum(item["count"] for item in self.omissions), "dispositionCount": len(self.dispositions), "reviewPage": {"offset": offset, "limit": limit, "totalCount": len(items), "items": page_items, "nextCursor": next_cursor, "isComplete": next_cursor is None, "allEvidencePresented": len(self.preview_seen) == len(items)}, "readiness": self.readiness(), "currentValidation": {"status": self.current_validation.status, "diagnosticCount": len(self.current_validation.diagnostics)}, "proposedValidation": {"status": self.proposed_validation.status, "diagnosticCount": len(self.proposed_validation.diagnostics)}, "proposedRules": {"status": self.proposed_rules.status, "diagnosticCount": len(self.proposed_rules.diagnostics)}}

    def read_omission_details(self, fingerprint: str, limit: int = 100, cursor: str | None = None) -> dict:
        if fingerprint not in self.omission_details:
            raise ValueError("the omission-details fingerprint is not part of this exact analysis")
        if not isinstance(limit, int) or isinstance(limit, bool) or limit < 1:
            raise ValueError("the omission-details page size must be positive")
        details = self.omission_details[fingerprint]
        offset = 0
        if cursor is not None:
            try:
                raw = __import__("base64").b64decode(cursor, validate=True).decode()
                expected, cursor_limit, cursor_offset = raw.split(":", 2)
                if expected != fingerprint or int(cursor_limit) != limit: raise ValueError
                offset = int(cursor_offset)
            except Exception as error:
                raise ValueError("the omission-details cursor is invalid for this exact analysis and page size") from error
        items = details[offset:offset + limit]; next_offset = offset + len(items)
        next_cursor = __import__("base64").b64encode(f"{fingerprint}:{limit}:{next_offset}".encode()).decode() if next_offset < len(details) else None
        return {"fingerprint": fingerprint, "offset": offset, "limit": limit, "totalCount": len(details), "items": items, "nextCursor": next_cursor}


class Application:
    def __init__(self, store: ProjectStore | None = None):
        self.store = store or ProjectStore()
        self.sessions: dict[str, Session] = {}

    def initialize(self, path: str, project_id: str, title: str, purpose_node_id: str, purpose_text: str) -> StoredProject:
        return self.store.initialize(path, Graph(project_id, title, purpose_node_id, (Node(purpose_node_id, purpose_text),), ()))

    def begin(self, path: str, project_id: str, author: str, intent: str) -> Session:
        project = self.store.load(path)
        if project.graph.project_id != project_id: raise ValueError("project mismatch")
        if project_id in self.sessions: raise ValueError("project already has an active session")
        session = Session(project, author, intent)
        session.rebuild(); self.sessions[project_id] = session
        return session

    def session(self, reference: dict) -> Session:
        session = self.locate(reference)
        current_project = self.store.load(session.base.path)
        if current_project.state_fingerprint != session.base.state_fingerprint:
            raise ValueError("stale-baseFingerprint")
        current = session.reference()
        for field in ("baseFingerprint", "operationFingerprint", "proposedFingerprint", "affectedFingerprint", "reviewFingerprint"):
            if reference.get(field) != current[field]: raise ValueError(f"stale-{field}")
        return session

    def locate(self, locator: dict) -> Session:
        session = self.sessions.get(locator["projectId"])
        if session is None or session.session_id != locator["sessionId"]:
            raise ValueError("session not found")
        return session

    def apply(self, reference: dict, operations: tuple[Operation, ...], patch: bool = False, *,
              max_traversal_depth: int = 2**31 - 1, max_affected_nodes: int = 2**31 - 1,
              max_output_items: int = 2**31 - 1) -> Session:
        session = self.session(reference)
        for name, value in (("maxTraversalDepth", max_traversal_depth), ("maxAffectedNodes", max_affected_nodes), ("maxOutputItems", max_output_items)):
            if not isinstance(value, int) or isinstance(value, bool) or value < 1:
                raise ValueError(f"{name} must be a positive integer")
        if patch:
            patched, _ = project_graph(session.proposed, operations)
            operations = _operations_between(session.base.graph, patched)
        session.operations = operations
        session.max_traversal_depth = max_traversal_depth
        session.max_affected_nodes = max_affected_nodes
        session.max_output_items = max_output_items
        session.preview_signature = None; session.preview_limit = None; session.preview_seen.clear(); session.agent_decision = None; session.rebuild(); return session

    def expand(self, reference: dict, *, max_traversal_depth: int = 2**31 - 1,
               max_affected_nodes: int = 2**31 - 1, max_output_items: int = 2**31 - 1) -> Session:
        session = self.session(reference)
        return self.apply(reference, session.operations, False, max_traversal_depth=max_traversal_depth,
                          max_affected_nodes=max_affected_nodes, max_output_items=max_output_items)

    def focus(self, reference: dict, operations: tuple[Operation, ...], selections: list[dict]) -> dict:
        self.session(reference)
        selected_children: set[str] = set()
        expanded = list(operations)
        supplied_parents = {item.edge.source for item in operations if item.entity_kind is EntityKind.EDGE and item.edge is not None and item.edge.relationship == "scope-parent"}
        for selection in selections:
            child = selection["childNodeId"]
            if child in selected_children or child in supplied_parents:
                raise ValueError(f"node '{child}' has more than one proposed scope parent")
            selected_children.add(child)
            expanded.append(Operation(OperationKind.ADD, EntityKind.EDGE, selection["edgeId"], edge=Edge(selection["edgeId"], child, selection["parentNodeId"], "scope-parent")))
        # Projection validates all operation preconditions, while focus remains read-only.
        project_graph(self.locate({"projectId": reference["projectId"], "sessionId": reference["sessionId"]}).base.graph, expanded)
        return {"operations": {"operations": [operation_dto(item) for item in sorted(expanded, key=lambda item: ordinal_key(item.entity_id))]}}

    def review(self, reference: dict, dispositions: list[dict], context: list[str]) -> Session:
        session = self.session(reference)
        previous_review_fingerprint = session.review_fingerprint
        affected = {item["nodeId"]: item for item in session.affected_nodes}
        for item in dispositions:
            node_id = item["nodeId"]; kind = item["kind"]
            if node_id not in affected: raise ValueError(f"node '{node_id}' is not affected")
            if kind == "updated" and not affected[node_id]["isDirectChange"]: raise ValueError("only direct changes may use Updated")
            if kind == "notApplicable" and not item.get("rationale", "").strip(): raise ValueError("not-applicable requires a rationale")
            if kind != "notApplicable": item = {**item, "rationale": None}
            session.dispositions[node_id] = {"nodeId": node_id, "kind": kind, "rationale": item.get("rationale")}
        allowed = {item["nodeId"] for item in session.scope_context}
        if not set(context).issubset(allowed): raise ValueError("presented context contains a non-context node")
        session.presented_context.update(context)
        if session.review_fingerprint != previous_review_fingerprint:
            session.preview_signature = None
            session.preview_limit = None
            session.preview_seen.clear()
            session.agent_decision = None
        return session

    def record_agent_review(self, reference: dict, decision: dict) -> Session:
        session = self.session(reference)
        if not session.readiness()["isReady"] or not session.review_items() or len(session.preview_seen) != len(session.review_items()):
            raise ValueError("complete exact proposal evidence must be reviewed before submitting an agent decision")
        if not isinstance(decision, dict) or set(decision) != {"decision", "summary", "concerns"}:
            raise ValueError("agent review decision has an invalid shape")
        if decision["decision"] not in {"allow", "block"} or not isinstance(decision["summary"], str) or not decision["summary"].strip() or not isinstance(decision["concerns"], list):
            raise ValueError("agent review decision fields are invalid")
        allowed = ({item.entity_id for item in session.operations}
                   | {item["nodeId"] for item in session.affected_nodes}
                   | {item["nodeId"] for item in session.scope_context}
                   | {item["operation"]["entityId"] for item in session.edge_changes}
                   | {edge_id for item in session.affected_nodes for edge_id in item["explanation"]["edges"]})
        normalized = []
        for concern in decision["concerns"]:
            if not isinstance(concern, dict) or set(concern) != {"code", "message", "citations"}:
                raise ValueError("agent review concern has an invalid shape")
            if not isinstance(concern["code"], str) or not concern["code"].strip() or not isinstance(concern["message"], str) or not concern["message"].strip() or not isinstance(concern["citations"], list) or not concern["citations"]:
                raise ValueError("agent review concern fields are invalid")
            if any(not isinstance(citation, dict) or set(citation) != {"entityId"} or not isinstance(citation["entityId"], str) or citation["entityId"] not in allowed for citation in concern["citations"]):
                raise ValueError("agent review concern cites an entity absent from the exact proposal")
            normalized.append({"code": concern["code"], "message": concern["message"], "citations": sorted({citation["entityId"] for citation in concern["citations"]}, key=ordinal_key)})
        if decision["decision"] == "allow" and normalized or decision["decision"] == "block" and not normalized:
            raise ValueError("allow needs no concerns and block needs cited concerns")
        candidate = {"binding": session.reference(), "decision": decision["decision"], "summary": decision["summary"], "concerns": normalized}
        if session.agent_decision is not None and session.agent_decision != candidate:
            raise ValueError("an agent decision for this exact proposal is already recorded; revise the proposal before another review")
        session.agent_decision = candidate
        return session

    def agent_write(self, reference: dict) -> dict:
        session = self.session(reference)
        result = session.agent_decision
        if result is None or result["binding"] != session.reference():
            return {"status": "agentReviewBlocked", "projectId": session.base.graph.project_id, "sessionId": session.session_id, "project": None, "storageErrorCode": None, "message": "A current host-subagent review decision is required before agent write.", "agentReview": None}
        if result["decision"] != "allow":
            return {"status": "agentReviewBlocked", "projectId": session.base.graph.project_id, "sessionId": session.session_id, "project": None, "storageErrorCode": None, "message": result["summary"], "agentReview": result}
        written = self.write(reference)
        written["agentReview"] = result
        return written

    def write(self, reference: dict) -> dict:
        try:
            session = self.session(reference)
        except sqlite3.OperationalError as exc:
            session = self.locate(reference)
            status = "busy" if "locked" in str(exc).lower() or "busy" in str(exc).lower() else "failed"
            return {"status": status, "projectId": session.base.graph.project_id, "sessionId": session.session_id, "project": None, "storageErrorCode": "database-busy" if status == "busy" else "storage-failure", "message": str(exc)}
        except ValueError as exc:
            if str(exc) != "stale-baseFingerprint": raise
            session = self.locate(reference)
            return {"status": "stale", "projectId": session.base.graph.project_id, "sessionId": session.session_id, "project": None, "storageErrorCode": "stale-base-fingerprint", "message": "The project changed after this session began; review a fresh proposal."}
        readiness = session.readiness()
        if not readiness["isReady"]: return {"status": "reviewNotReady", "projectId": session.base.graph.project_id, "sessionId": session.session_id, "project": None, "storageErrorCode": None, "message": " ".join(readiness["blockers"])}
        if not session.review_items() or len(session.preview_seen) != len(session.review_items()):
            return {"status": "reviewNotReady", "projectId": session.base.graph.project_id, "sessionId": session.session_id, "project": None, "storageErrorCode": None, "message": "The exact proposal review evidence has not been fully presented. Call change.preview and follow every reviewPage.nextCursor before change.write."}
        try:
            project = self.store.write(session.base.path, session.base.graph.project_id, session.base.state_fingerprint, session.proposed_fingerprint, session.operations)
        except RuntimeError as exc:
            if str(exc) == "stale-base-fingerprint":
                return {"status": "stale", "projectId": session.base.graph.project_id, "sessionId": session.session_id, "project": None, "storageErrorCode": "stale-base-fingerprint", "message": "The project changed before the atomic write; review a fresh proposal."}
            return {"status": "failed", "projectId": session.base.graph.project_id, "sessionId": session.session_id, "project": None, "storageErrorCode": "storage-failure", "message": str(exc)}
        except sqlite3.OperationalError as exc:
            status = "busy" if "locked" in str(exc).lower() or "busy" in str(exc).lower() else "failed"
            return {"status": status, "projectId": session.base.graph.project_id, "sessionId": session.session_id, "project": None, "storageErrorCode": "database-busy" if status == "busy" else "storage-failure", "message": str(exc)}
        except Exception as exc:
            return {"status": "failed", "projectId": session.base.graph.project_id, "sessionId": session.session_id, "project": None, "storageErrorCode": "storage-failure", "message": str(exc)}
        self.sessions.pop(session.base.graph.project_id, None)
        return {"status": "written", "projectId": project.graph.project_id, "sessionId": session.session_id, "project": _stored(project), "storageErrorCode": None, "message": "The reviewed proposal was written atomically."}

    def discard(self, reference: dict) -> dict:
        session = self.session(reference); self.sessions.pop(session.base.graph.project_id, None)
        return {"projectId": session.base.graph.project_id, "sessionId": session.session_id, "discardedUtc": session.base.updated_utc}


def _operations_between(base: Graph, target: Graph) -> tuple[Operation, ...]:
    result: list[Operation] = []
    for kind, before, after in ((EntityKind.NODE, {item.id: item for item in base.nodes}, {item.id: item for item in target.nodes}),
                                (EntityKind.EDGE, {item.id: item for item in base.edges}, {item.id: item for item in target.edges})):
        for entity_id in sorted(set(before) | set(after), key=ordinal_key):
            old, new = before.get(entity_id), after.get(entity_id)
            if old == new:
                continue
            operation_kind = OperationKind.ADD if old is None else OperationKind.REMOVE if new is None else OperationKind.REPLACE
            result.append(Operation(operation_kind, kind, entity_id,
                                    node=new if kind is EntityKind.NODE else None,
                                    edge=new if kind is EntityKind.EDGE else None))
    return tuple(result)


def sample_graph() -> Graph:
    purpose = Node("purpose", "An offline privacy-preserving sensor")
    power = Node("scope-power", "Power behavior", "scope")
    privacy = Node("scope-privacy", "Privacy behavior", "scope")
    documentation = Node("scope-documentation", "Documentation behavior", "scope")
    accessibility = Node("scope-accessibility", "Accessibility behavior", "scope")
    battery = Node("battery-assumption", "The battery lasts for the target duty cycle", "assumption")
    retention = Node("retention-policy", "Collected data is retained only for the required interval", "requirement")
    runtime = Node("runtime-test", "Runtime behavior is verified on the target device", "verification")
    power_anchor = Node("power-design-anchor", "Power design record for the sensor", "external-anchor", ("artifact",))
    architecture = Node("privacy-architecture", "The architecture enforces the required data-retention interval", "decision")
    retention_test = Node("retention-test", "Retention behavior is verified against the documented interval", "verification")
    privacy_doc = Node("privacy-documentation", "The privacy documentation states the retention interval", "external-anchor", ("artifact",))
    accessibility_acceptance = Node("accessibility-acceptance", "Accessibility acceptance covers the sensor configuration workflow", "verification")
    nodes = (purpose, power, privacy, documentation, accessibility, battery, retention, runtime, power_anchor, architecture, retention_test, privacy_doc, accessibility_acceptance)
    def scope(edge_id, child, parent): return Edge(edge_id, child.id, parent.id, "scope-parent")
    edges = (scope("scope-power-parent", power, purpose), scope("scope-privacy-parent", privacy, purpose), scope("scope-documentation-parent", documentation, purpose), scope("scope-accessibility-parent", accessibility, purpose), scope("battery-scope-parent", battery, power), scope("retention-scope-parent", retention, privacy), scope("runtime-scope-parent", runtime, power), scope("power-anchor-scope-parent", power_anchor, power), scope("privacy-architecture-scope-parent", architecture, privacy), scope("retention-test-scope-parent", retention_test, privacy), scope("privacy-documentation-scope-parent", privacy_doc, documentation), scope("accessibility-acceptance-scope-parent", accessibility_acceptance, accessibility), Edge("battery-requires-runtime", battery.id, runtime.id, "requires", 1), Edge("battery-informs-power-anchor", battery.id, power_anchor.id, "informs", 1), Edge("retention-requires-architecture", retention.id, architecture.id, "requires", 1), Edge("architecture-requires-retention-test", architecture.id, retention_test.id, "requires", 1), Edge("architecture-informs-privacy-documentation", architecture.id, privacy_doc.id, "informs", 1))
    return Graph("technical-project", "Technical Project", purpose.id, nodes, edges)
