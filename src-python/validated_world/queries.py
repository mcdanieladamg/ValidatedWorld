"""Bounded deterministic reads over a verified project snapshot."""

from __future__ import annotations

import base64
from collections import deque
import hashlib
import re
from typing import Any, Iterable

from .models import Edge, EntityKind, Graph, Node, ReviewDirection, ordinal_key
from .protocol import edge_dto, node_dto
from .storage import StoredProject
from .validation import GraphIndex


def _signature(kind: str, value: str) -> str:
    def length(text: str) -> int:
        return len(text.encode("utf-16-le", "surrogatepass")) // 2
    raw = f"{length(kind)}:{kind}{length(value)}:{value}".encode("utf-8", "surrogatepass")
    return hashlib.sha256(raw).hexdigest()


def _page(items: list[Any], signature: str, limit: int = 100, cursor: str | None = None) -> dict[str, Any]:
    if limit < 1:
        raise ValueError("a query page size must be positive")
    offset = 0
    if cursor is not None:
        try:
            raw = base64.b64decode(cursor, validate=True).decode("utf-8")
            expected, position = raw.rsplit(":", 1)
            if expected != signature or not position.isdigit():
                raise ValueError
            offset = int(position)
        except Exception as exc:
            raise ValueError("cursor is malformed, out of range, or belongs to a different query") from exc
    if offset > len(items):
        raise ValueError("cursor is malformed, out of range, or belongs to a different query")
    selected = items[offset:offset + limit]
    next_offset = offset + len(selected)
    has_more = next_offset < len(items)
    return {"items": selected, "totalCount": len(items), "nextCursor": base64.b64encode(f"{signature}:{next_offset}".encode()).decode() if has_more else None, "omission": {"reason": "outputLimit", "remainingCount": len(items) - next_offset, "message": "Additional deterministic results are available through the next cursor."} if has_more else None}


def _search_value(value: str | None, term: str) -> bool:
    return value is not None and term.casefold() in value.casefold()


class Queries:
    def __init__(self, project: StoredProject):
        self.project = project
        self.graph = project.graph
        self.index = GraphIndex(self.graph)

    def node(self, node_id: str) -> dict[str, Any]:
        if node_id not in self.index.nodes_by_id:
            raise KeyError(f"Node '{node_id}' does not exist")
        return node_dto(self.index.nodes_by_id[node_id])

    def edge(self, edge_id: str) -> dict[str, Any]:
        if edge_id not in self.index.edges_by_id:
            raise KeyError(f"Edge '{edge_id}' does not exist")
        return edge_dto(self.index.edges_by_id[edge_id])

    def nodes(self, limit=100, cursor=None) -> dict:
        return _page([node_dto(item) for item in self.graph.nodes], _signature("nodes", self.project.state_fingerprint), limit, cursor)

    def edges(self, limit=100, cursor=None) -> dict:
        return _page([edge_dto(item) for item in self.graph.edges], _signature("edges", self.project.state_fingerprint), limit, cursor)

    def search(self, text: str, limit=100, cursor=None) -> dict:
        if not text or not text.strip():
            raise ValueError("search text cannot be empty or whitespace-only")
        hits = []
        for node in self.graph.nodes:
            if any(_search_value(value, text) for value in (node.id, node.text, node.kind, *node.tags)):
                hits.append({"entityKind": "node", "entityId": node.id, "node": node_dto(node), "edge": None})
        for edge in self.graph.edges:
            if any(_search_value(value, text) for value in (edge.id, edge.relationship, edge.rationale, *edge.tags)):
                hits.append({"entityKind": "edge", "entityId": edge.id, "node": None, "edge": edge_dto(edge)})
        hits.sort(key=lambda value: (ordinal_key(value["entityId"]), 0 if value["entityKind"] == "node" else 1))
        return _page(hits, _signature("search", self.project.state_fingerprint + text), limit, cursor)

    def tag(self, tag: str, limit=100, cursor=None) -> dict:
        if not tag or any(ord(ch) < 32 for ch in tag):
            raise ValueError("a tag query must be non-empty and free of control characters")
        hits = [{"entityKind": "node", "entityId": node.id, "node": node_dto(node), "edge": None} for node in self.graph.nodes if tag in node.tags]
        hits += [{"entityKind": "edge", "entityId": edge.id, "node": None, "edge": edge_dto(edge)} for edge in self.graph.edges if tag in edge.tags]
        hits.sort(key=lambda value: (ordinal_key(value["entityId"]), 0 if value["entityKind"] == "node" else 1))
        return _page(hits, _signature("tag", self.project.state_fingerprint + tag), limit, cursor)

    def ranked_search(self, text: str, limit=100, cursor=None) -> dict:
        if not text or not text.strip():
            raise ValueError("search text cannot be empty or whitespace-only")
        if text.count('"') % 2:
            raise ValueError("search phrases must have closing quotes")
        raw_phrases = re.findall(r'"([^"]*)"', text)
        if any(not item.strip() for item in raw_phrases):
            raise ValueError("quoted search phrases cannot be empty")
        phrases = [item.strip() for item in raw_phrases]
        normalized = re.sub(r'"[^"]*"', ' ', text).strip()
        tokens = [item.casefold() for item in re.findall(r"[\w]+", text.replace('"', ' '), flags=re.UNICODE)]
        stop = {"a", "an", "and", "are", "as", "at", "be", "by", "can", "do", "does", "for", "from", "how", "if", "in", "is", "it", "of", "on", "or", "that", "the", "this", "to", "was", "what", "when", "where", "which", "who", "why", "with"}
        tokens = list(dict.fromkeys(item for item in tokens if item not in stop))
        unquoted_normalized = " ".join(text.replace('"', ' ').split())
        if len(tokens) > 1 and unquoted_normalized and all(unquoted_normalized.casefold() != item.casefold() for item in phrases):
            phrases.insert(0, unquoted_normalized)
        exact = {" ".join(text.replace('"', ' ').split()).casefold(), *tokens}
        hits = []

        def rank(kind: str, entity_id: str, fields: list[tuple[str, str, bool]], tags: tuple[str, ...], dto: dict) -> None:
            matches = []
            if entity_id.casefold() in exact:
                matches.append({"kind": "stableId", "field": "id", "term": entity_id, "scoreContribution": 1000})
            for tag in tags:
                if tag in {" ".join(text.replace('"', ' ').split()), *tokens}:
                    matches.append({"kind": "exactTag", "field": "tag", "term": tag, "scoreContribution": 800})
            for phrase in phrases + ([" ".join(tokens)] if len(tokens) > 1 else []):
                for field, value, metadata in fields:
                    if phrase.casefold() in value.casefold():
                        matches.append({"kind": "phrase", "field": field, "term": phrase, "scoreContribution": 150 if metadata else 300})
            for token in tokens:
                for field, value, metadata in fields:
                    if any(part.casefold() == token for part in re.findall(r"[\w]+", value, flags=re.UNICODE)):
                        matches.append({"kind": "metadata" if metadata else "token", "field": field, "term": token, "scoreContribution": 25 if metadata else 100})
            if matches:
                matches.sort(key=lambda item: (-item["scoreContribution"], item["kind"], item["field"], item["term"]))
                hits.append({"entityKind": kind, "entityId": entity_id, "node": dto if kind == "node" else None, "edge": dto if kind == "edge" else None, "score": sum(item["scoreContribution"] for item in matches), "matches": matches})

        for node in self.graph.nodes:
            rank("node", node.id, [("id", node.id, True), ("text", node.text, False), *( [("kind", node.kind, True)] if node.kind else []), *[("tag", tag, True) for tag in node.tags], *[(f"attribute:{a.name}", a.name, True) for a in node.attributes], *[(f"attribute:{a.name}", str(a.value), True) for a in node.attributes]], node.tags, node_dto(node))
        for edge in self.graph.edges:
            fields = [("id", edge.id, True), ("relationship", edge.relationship, True), *( [("rationale", edge.rationale, True)] if edge.rationale else []), *[("tag", tag, True) for tag in edge.tags], *[(f"attribute:{a.name}", a.name, True) for a in edge.attributes], *[(f"attribute:{a.name}", str(a.value), True) for a in edge.attributes]]
            rank("edge", edge.id, fields, edge.tags, edge_dto(edge))
        hits.sort(key=lambda item: (-item["score"], ordinal_key(item["entityId"]), 0 if item["entityKind"] == "node" else 1))
        return _page(hits, _signature("ranked-search", self.project.state_fingerprint + text), limit, cursor)

    def scope(self, node_id: str, limit=100, cursor=None, max_depth=2**31 - 1, max_visited=2**31 - 1) -> dict:
        if max_depth < 0 or max_visited < 1:
            raise ValueError("query traversal limits are invalid")
        node = self.index.nodes_by_id.get(node_id)
        if node is None:
            raise KeyError(node_id)
        omissions = []
        upstream_ids = []
        current = node_id
        seen_upstream: set[str] = set()
        depth = 0
        while current not in seen_upstream:
            seen_upstream.add(current)
            if len(seen_upstream) > max_visited:
                _add_omission(omissions, "visitedNodeLimit", "Scope traversal reached its node limit.")
                break
            upstream_ids.append(current)
            parents = self.index.scope_parents(current)
            if len(parents) != 1:
                break
            depth += 1
            if depth > max_depth:
                _add_omission(omissions, "traversalDepthLimit", "Scope traversal reached its depth limit.")
                break
            current = parents[0].target
        descendants = []
        queue = deque([(child, 1) for child in self.index.scope_children(node_id)])
        seen = {node_id}
        while queue:
            current, depth = queue.popleft()
            if depth > max_depth:
                _add_omission(omissions, "traversalDepthLimit", "Scope traversal reached its depth limit.")
                continue
            if current in seen: continue
            seen.add(current)
            if len(seen) > max_visited:
                _add_omission(omissions, "visitedNodeLimit", "Scope traversal reached its node limit."); break
            descendants.append(node_dto(self.index.nodes_by_id[current]))
            queue.extend((child, depth + 1) for child in self.index.scope_children(current))
        signature_value = f"{self.project.state_fingerprint}\0{node_id}\0{max_depth}\0{max_visited}"
        result = _page(descendants, _signature("scope-descendants", signature_value), limit, cursor)
        return {"node": node_dto(node), "upstream": [node_dto(self.index.nodes_by_id[item]) for item in upstream_ids[1:]], "descendants": result, "omissions": omissions}

    def neighbors(self, node_id: str, limit=100, cursor=None) -> dict:
        if node_id not in self.index.nodes_by_id: raise KeyError(node_id)
        values = [{"nodeId": edge.target, "edge": edge_dto(edge), "isOutgoing": True} for edge in self.index.edges_by_source.get(node_id, [])]
        values += [{"nodeId": edge.source, "edge": edge_dto(edge), "isOutgoing": False} for edge in self.index.edges_by_target.get(node_id, [])]
        values.sort(key=lambda item: (ordinal_key(item["nodeId"]), ordinal_key(item["edge"]["id"]), 0 if item["isOutgoing"] else 1))
        return _page(values, _signature("neighbors", self.project.state_fingerprint + node_id), limit, cursor)

    def dependencies(self, node_id: str, limit=100, cursor=None) -> dict:
        if node_id not in self.index.nodes_by_id: raise KeyError(node_id)
        values = [{"edgeId": edge_id, "from": source, "to": target, "isOutgoing": source == node_id} for edge_id, source, target in self.index.review_arcs if source == node_id or target == node_id]
        values.sort(key=lambda item: (0 if item["isOutgoing"] else 1, ordinal_key(item["from"]), ordinal_key(item["to"]), ordinal_key(item["edgeId"])))
        return _page(values, _signature("dependencies", self.project.state_fingerprint + node_id), limit, cursor)

    def path(self, source: str, target: str, max_depth=2**31 - 1, max_visited=2**31 - 1) -> dict:
        if max_depth < 0 or max_visited < 1:
            raise ValueError("query traversal limits are invalid")
        if source not in self.index.nodes_by_id or target not in self.index.nodes_by_id: raise KeyError(source if source not in self.index.nodes_by_id else target)
        if source == target: return {"found": True, "nodes": [source], "edges": [], "omissions": []}
        queue = deque([source]); previous = {}; depths = {source: 0}; omissions = []
        arcs = {}
        for edge_id, from_id, to_id in self.index.review_arcs: arcs.setdefault(from_id, []).append((edge_id, to_id))
        while queue:
            current = queue.popleft()
            for edge_id, nxt in arcs.get(current, []):
                depth = depths[current] + 1
                if depth > max_depth:
                    _add_omission(omissions, "traversalDepthLimit", "Path traversal reached its depth limit.")
                    continue
                if nxt in depths: continue
                if len(depths) >= max_visited:
                    _add_omission(omissions, "visitedNodeLimit", "Path traversal reached its visited-node limit.")
                    continue
                depths[nxt] = depth; previous[nxt] = (current, edge_id)
                if nxt == target:
                    nodes = [target]; edges = []; cur = target
                    while cur != source:
                        prev, edge = previous[cur]; nodes.append(prev); edges.append(edge); cur = prev
                    return {"found": True, "nodes": list(reversed(nodes)), "edges": list(reversed(edges)), "omissions": omissions}
                queue.append(nxt)
        return {"found": False, "nodes": [], "edges": [], "omissions": omissions}

    def context(self, node_ids: Iterable[str], max_depth=2**31 - 1, max_visited=2**31 - 1) -> dict:
        if max_depth < 0 or max_visited < 1:
            raise ValueError("query traversal limits are invalid")
        requested = sorted(set(node_ids), key=ordinal_key)
        for node_id in requested:
            if node_id not in self.index.nodes_by_id: raise KeyError(node_id)
        context: set[str] = set()
        omissions = []
        for node_id in requested:
            current = node_id
            seen: set[str] = set()
            depth = 0
            while current not in seen:
                seen.add(current)
                context.add(current)
                parents = self.index.scope_parents(current)
                if len(parents) != 1:
                    break
                depth += 1
                if depth > max_depth:
                    _add_omission(omissions, "traversalDepthLimit", "Scope traversal reached its depth limit.")
                    break
                current = parents[0].target
            if len(context) > max_visited:
                _add_omission(omissions, "visitedNodeLimit", "Context collection reached its node limit."); context = set(sorted(context, key=ordinal_key)[:max_visited]); break
        return {"requestedNodeIds": requested, "contextNodes": [node_dto(self.index.nodes_by_id[item]) for item in sorted(context, key=ordinal_key)], "omissions": omissions}

    def health(self, limit=100) -> dict:
        if limit < 1:
            raise ValueError("the report size must be positive")
        nodes = len(self.graph.nodes); edges = len(self.graph.edges)
        semantic = len(self.index.review_arcs)
        reaching = sum(1 for node in self.graph.nodes if self.index.upstream(node.id)[-1:] == [self.graph.purpose_node_id])
        exact = sum(1 for node in self.graph.nodes if node.id != self.graph.purpose_node_id and len(self.index.scope_parents(node.id)) == 1)
        outgoing: dict[str, int] = {}
        incoming: dict[str, int] = {}
        connected: set[str] = set()
        for _, source, target in self.index.review_arcs:
            outgoing[source] = outgoing.get(source, 0) + 1
            incoming[target] = incoming.get(target, 0) + 1
            connected.update((source, target))
        hotspots = [
            {"nodeId": node_id, "outgoingReviewArcCount": count, "incomingReviewArcCount": incoming.get(node_id, 0)}
            for node_id, count in outgoing.items()
        ]
        hotspots.sort(key=lambda item: (-item["outgoingReviewArcCount"], ordinal_key(item["nodeId"])))
        isolated = [
            {"nodeId": node.id, "kind": node.kind}
            for node in self.graph.nodes
            if node.id != self.graph.purpose_node_id and node.kind != "scope" and node.id not in connected
        ]
        isolated.sort(key=lambda item: ordinal_key(item["nodeId"]))
        missing = [
            {"edgeId": edge.id, "source": edge.source, "target": edge.target, "relationship": edge.relationship}
            for edge in self.graph.edges
            if edge.relationship != "scope-parent" and (edge.rationale is None or not edge.rationale.strip())
        ]
        missing.sort(key=lambda item: ordinal_key(item["edgeId"]))
        tags = {}
        for node in self.graph.nodes:
            for tag in node.tags: tags.setdefault(tag, [0, 0])[0] += 1
        for edge in self.graph.edges:
            for tag in edge.tags: tags.setdefault(tag, [0, 0])[1] += 1
        tag_usage = [{"tag": key, "nodeCount": value[0], "edgeCount": value[1], "totalCount": sum(value)} for key, value in tags.items()]
        tag_usage.sort(key=lambda item: (-item["totalCount"], ordinal_key(item["tag"])))
        unreachable = [node.id for node in self.graph.nodes if self.index.upstream(node.id)[-1:] != [self.graph.purpose_node_id]]
        return {"nodeCount": nodes, "edgeCount": edges, "semanticReviewArcCount": semantic, "scopeCoverage": {"totalNodeCount": nodes, "scopeParentEdgeCount": sum(len(v) for v in self.index.scope_by_child.values()), "nodesWithExactlyOneScopeParent": exact, "nodesReachingPurpose": reaching, "coveragePercent": round((100.0 * reaching / nodes) if nodes else 100.0, 2)}, "unreachableNodeIds": _section(unreachable, limit), "reviewFanOutHotspots": _section(hotspots, limit), "suspiciouslyIsolatedClaims": _section(isolated, limit), "missingRationales": _section(missing, limit), "tagUsage": _section(tag_usage, limit), "untaggedNodeCount": sum(not node.tags for node in self.graph.nodes), "untaggedEdgeCount": sum(not edge.tags for edge in self.graph.edges), "wasCancelled": False, "omissions": []}


def _add_omission(omissions: list[dict], reason: str, message: str) -> None:
    if not any(item["reason"] == reason for item in omissions):
        omissions.append({"reason": reason, "remainingCount": None, "message": message})


def _section(values: list[Any], limit: int) -> dict[str, Any]:
    return {"totalCount": len(values), "items": values[:limit], "omittedCount": max(0, len(values) - limit)}
