"""Pure deterministic ownership of exact review ordinals by scope branch."""

from .models import Graph, ordinal_key
from .validation import GraphIndex

ALGORITHM = "scope-ordinals-v1"


def branch(index: GraphIndex, node_id: str) -> str:
    path = index.upstream(node_id)
    return path[-2] if len(path) > 1 else ""


def partition(current: Graph, proposed: Graph, entities: list[tuple[int, str, str]]) -> dict[str, list[int]]:
    before, after = GraphIndex(current), GraphIndex(proposed)
    result = {"synthesis": []}
    branches = {}
    for ordinal, kind, entity_id in entities:
        node_id = entity_id
        if kind == "edge":
            edge = after.edges_by_id.get(entity_id) or before.edges_by_id.get(entity_id)
            node_id = edge.source if edge else ""
        index = after if node_id in after.nodes_by_id else before
        scope = branch(index, node_id) if kind in {"node", "edge"} and node_id in index.nodes_by_id else ""
        branches.setdefault(scope, []).append(ordinal)
    for number, scope in enumerate(sorted((key for key in branches if key), key=ordinal_key)):
        result[f"branch-{number:06d}"] = sorted(branches[scope])
    result["synthesis"] = sorted(branches.get("", []))
    return result


def refine(ownership: dict[str, list[int]], refinements: list[dict]) -> dict[str, list[int]]:
    """Explicit ordinal subdivisions are lossless; no capacity estimate is needed."""
    result = {key: list(value) for key, value in ownership.items()}
    touched = set()
    for refinement in refinements:
        if not isinstance(refinement, dict) or set(refinement) != {"packetId", "groups"}:
            raise ValueError("refinement requires packetId and groups")
        packet_id, groups = refinement["packetId"], refinement["groups"]
        if not isinstance(packet_id, str) or packet_id not in result or packet_id in touched or packet_id == "synthesis":
            raise ValueError("refinement names an unknown, duplicate or synthesis packet")
        if not isinstance(groups, list) or len(groups) < 2 or any(not isinstance(g, list) or not g for g in groups):
            raise ValueError("refinement requires at least two nonempty ordinal groups")
        flattened = [ordinal for group in groups for ordinal in group]
        if any(not isinstance(v, int) or isinstance(v, bool) for v in flattened):
            raise ValueError("refinement ordinals must be integers")
        if len(set(flattened)) != len(flattened) or sorted(flattened) != result[packet_id]:
            raise ValueError("refinement must own every assigned ordinal exactly once")
        touched.add(packet_id)
        del result[packet_id]
        for number, group in enumerate(sorted((sorted(g) for g in groups), key=lambda g: g[0])):
            child = f"{packet_id}-{number:06d}"
            if child in result:
                raise ValueError("refinement packet collision")
            result[child] = group
    return result
