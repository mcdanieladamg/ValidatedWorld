"""Built-in versioned graph templates."""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

from .models import Attribute, Edge, Graph, GraphValue, Node
from .protocol import edge_dto, edge_from_dto, json_loads_strict, node_dto, node_from_dto


def _text(name: str, value: str) -> Attribute: return Attribute(name, GraphValue.text(value))
def _int(name: str, value: int) -> Attribute: return Attribute(name, GraphValue.integer(value))
def _node(identifier: str, text: str, kind: str | None = None, tags=(), attributes=()) -> Node: return Node(identifier, text, kind, tuple(tags), tuple(attributes))
def _scope(identifier: str, text: str) -> tuple[Node, Edge]: return _node(identifier, text, "scope"), Edge(identifier + "-scope", identifier, "purpose", "scope-parent")


def _code_template() -> dict[str, Any]:
    nodes: list[Node] = []
    edges: list[Edge] = []
    for identifier, text in (("scope-architecture", "Implemented architecture and accepted technical decisions."), ("scope-contracts", "Public behavior, constraints, and compatibility contracts."), ("scope-evidence", "Source, tests, documentation, and other implementation evidence."), ("scope-uncertainties", "Open questions, uncertainty, and claims that still require evidence."), ("scope-roadmap", "Project lifecycle, status, and ordered implementation phases.")):
        node, edge = _scope(identifier, text); nodes.append(node); edges.append(edge)
    nodes.append(_node("project-status", "The project is being documented. Evidence, uncertainty, and a governed roadmap are not yet complete.", "project-status", ("project:status", "status:planning")))
    edges.append(Edge("project-status-scope", "project-status", "scope-roadmap", "scope-parent"))
    views = {
        "phases": '{"nodes":{"kind":"development-phase","tagsAll":["roadmap:phase"]}}',
        "current-phases": '{"nodes":{"kind":"development-phase","tagsAll":["roadmap:phase","status:current"]}}',
        "estimate-nodes": '{"nodes":{"tagPrefix":"estimate:"}}',
        "valid-estimate-nodes": '{"union":[{"nodes":{"tagsAll":["estimate:small"]}},{"nodes":{"tagsAll":["estimate:medium"]}},{"nodes":{"tagsAll":["estimate:large"]}},{"nodes":{"tagsAll":["estimate:gigantic"]}}]}',
        "pending-phases": '{"nodes":{"kind":"development-phase","tagsAll":["roadmap:phase","status:pending"]}}',
        "complete-phases": '{"nodes":{"kind":"development-phase","tagsAll":["roadmap:phase","status:complete"]}}',
        "precedes-edges": '{"edges":{"relationship":"precedes","sourceIn":{"view":"phases"},"targetIn":{"view":"phases"}}}',
    }
    for name, expression in views.items():
        nodes.append(_node("view-" + name, f"Reusable selector for {name}.", "validation-view", (), (_text("view:name", name), _int("view:version", 1), _text("view:expression", expression))))
        edges.append(Edge("view-" + name + "-scope", "view-" + name, "scope-roadmap", "scope-parent"))
    rules = {
        "roadmap-phase-state": ("Every roadmap phase must have exactly one pending, current, or complete state tag.", '{"all":{"set":{"view":"phases"},"condition":{"tagCount":{"prefix":"status:","compare":"eq","value":1}}}}'),
        "roadmap-phase-id": ("Every roadmap phase must have exactly one phase identifier tag.", '{"all":{"set":{"view":"phases"},"condition":{"tagCount":{"prefix":"phase:","compare":"eq","value":1}}}}'),
        "roadmap-status-unique": ("There must be one project-status node with exactly one lifecycle state tag.", '{"and":[{"count":{"set":{"nodes":{"id":"project-status","tagsAll":["project:status"]}},"compare":"eq","value":1}},{"all":{"set":{"nodes":{"id":"project-status"}},"condition":{"tagCount":{"prefix":"status:","compare":"eq","value":1}}}}]}'),
        "roadmap-current-lifecycle": ("Planning has no current phase, active has exactly one, and finished has no unfinished phase.", '{"or":[{"and":[{"exists":{"nodes":{"id":"project-status","tagsAll":["status:planning"]}}},{"count":{"set":{"view":"current-phases"},"compare":"eq","value":0}}]},{"and":[{"exists":{"nodes":{"id":"project-status","tagsAll":["status:active"]}}},{"count":{"set":{"view":"current-phases"},"compare":"eq","value":1}}]},{"and":[{"exists":{"nodes":{"id":"project-status","tagsAll":["status:finished"]}}},{"count":{"set":{"union":[{"view":"current-phases"},{"view":"pending-phases"}]},"compare":"eq","value":0}}]}]}'),
        "roadmap-estimate-placement": ("Only the current phase may carry an estimate tag.", '{"subset":[{"view":"estimate-nodes"},{"view":"current-phases"}]}'),
        "roadmap-current-estimate": ("Each current phase must carry exactly one estimate tag.", '{"all":{"set":{"view":"current-phases"},"condition":{"tagCount":{"prefix":"estimate:","compare":"eq","value":1}}}}'),
        "roadmap-estimate-values": ("Estimates use only small, medium, large, or gigantic.", '{"subset":[{"view":"estimate-nodes"},{"view":"valid-estimate-nodes"}]}'),
        "roadmap-chain": ("Roadmap phases and precedes edges must form one acyclic chain.", '{"singleChain":{"nodes":{"view":"phases"},"edges":{"view":"precedes-edges"}}}'),
        "roadmap-current-pointer": ("The current-phase edge is absent without a current phase, otherwise exactly one connects project-status to it.", '{"or":[{"and":[{"count":{"set":{"view":"current-phases"},"compare":"eq","value":0}},{"count":{"set":{"edges":{"relationship":"current-phase"}},"compare":"eq","value":0}}]},{"and":[{"count":{"set":{"edges":{"relationship":"current-phase"}},"compare":"eq","value":1}},{"count":{"set":{"edges":{"relationship":"current-phase","sourceIn":{"nodes":{"id":"project-status"}},"targetIn":{"view":"current-phases"}}},"compare":"eq","value":1}}]}]}'),
        "roadmap-order": ("Complete phases precede the current phase and pending phases follow it.", '{"or":[{"count":{"set":{"view":"current-phases"},"compare":"eq","value":0}},{"and":[{"subset":[{"view":"complete-phases"},{"reachable":{"from":{"view":"current-phases"},"edges":{"view":"precedes-edges"},"direction":"incoming"}}]},{"subset":[{"view":"pending-phases"},{"reachable":{"from":{"view":"current-phases"},"edges":{"view":"precedes-edges"},"direction":"outgoing"}}]}]}]}'),
        "roadmap-pointer-tag": ("The project-status current-phase tag must match the current phase's phase tag.", '{"or":[{"count":{"set":{"view":"current-phases"},"compare":"eq","value":0}},{"tagSuffixMatch":{"left":{"nodes":{"id":"project-status"}},"leftPrefix":"current-phase:","right":{"view":"current-phases"},"rightPrefix":"phase:"}}]}'),
    }
    for suffix, (message, expression) in rules.items():
        identifier = "rule-" + suffix
        nodes.append(_node(identifier, message, "validation-rule", ("rule:active",), (_int("rule:version", 1), _text("rule:expression", expression))))
        edges.append(Edge(identifier + "-scope", identifier, "scope-roadmap", "scope-parent"))
    return {"version": 1, "id": "code-development", "description": "Governed software-project knowledge with evidence, uncertainty, public contracts, architecture, and an ordered roadmap.", "purposeNodeId": "purpose", "nodes": nodes, "edges": edges}


def _research_template() -> dict[str, Any]:
    nodes = [_node("scope-evidence", "Sources, observations, and measurements.", "scope"), _node("scope-claims", "Claims and conclusions supported by explicit evidence links.", "scope"), _node("scope-uncertainties", "Uncertainty, alternatives, and unresolved questions.", "scope")]
    edges = [Edge(node.id + "-scope", node.id, "purpose", "scope-parent") for node in nodes]
    return {"version": 1, "id": "research-notebook", "description": "A domain-neutral research notebook separating evidence, claims, and uncertainty.", "purposeNodeId": "purpose", "nodes": nodes, "edges": edges}


def resolve(name_or_path: str) -> dict[str, Any]:
    if name_or_path == "code-development": return _code_template()
    if name_or_path == "research-notebook": return _research_template()
    path = Path(name_or_path).expanduser().resolve()
    if not path.is_file(): raise ValueError(f"template '{name_or_path}' is not a built-in name or existing JSON file")
    raw = json_loads_strict(path.read_text(encoding="utf-8"))
    required = {"version", "id", "description", "purposeNodeId", "nodes", "edges"}
    if not isinstance(raw, dict) or set(raw) != required: raise ValueError("template has an invalid object shape")
    if raw.get("version") != 1: raise ValueError("unsupported template version")
    if not isinstance(raw["id"], str) or not raw["id"].strip() or not isinstance(raw["description"], str) or not raw["description"].strip() or not isinstance(raw["purposeNodeId"], str) or not isinstance(raw["nodes"], list) or not isinstance(raw["edges"], list):
        raise ValueError("template fields have invalid types")
    return {"version": raw["version"], "id": raw["id"], "description": raw["description"], "purposeNodeId": raw["purposeNodeId"], "nodes": [node_from_dto(item) for item in raw["nodes"]], "edges": [edge_from_dto(item) for item in raw["edges"]]}


def descriptor(template: dict[str, Any]) -> dict[str, Any]:
    return {"id": template["id"], "description": template["description"], "version": template["version"], "nodeCount": len(template["nodes"]), "edgeCount": len(template["edges"])}


def dto(template: dict[str, Any]) -> dict[str, Any]:
    return {"version": template["version"], "id": template["id"], "description": template["description"], "purposeNodeId": template["purposeNodeId"], "nodes": [node_dto(item) for item in template["nodes"]], "edges": [edge_dto(item) for item in template["edges"]]}


def instantiate(template: dict[str, Any], project_id: str, title: str, purpose_text: str) -> Graph:
    purpose = _node(template["purposeNodeId"], purpose_text, "purpose")
    return Graph(project_id, title, purpose.id, (purpose, *template["nodes"]), template["edges"])
