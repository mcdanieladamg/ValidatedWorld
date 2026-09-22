"""Strict, deterministic evaluation of the versioned graph rule language."""

from __future__ import annotations

import json
from dataclasses import dataclass
from typing import Any, Iterable

from .models import Edge, Graph, GraphValue, GraphValueKind, Node, ordinal_key


@dataclass(frozen=True)
class RuleDiagnostic:
    rule_id: str | None
    status: str
    message: str
    offender_ids: tuple[str, ...] = ()
    omitted_count: int = 0


@dataclass(frozen=True)
class RuleResult:
    status: str
    diagnostics: tuple[RuleDiagnostic, ...]

    @property
    def is_valid(self) -> bool:
        return self.status == "valid"


class RuleEvaluationError(ValueError):
    pass


class _DuplicateJsonKey(ValueError):
    pass


class _Budget:
    def __init__(self, maximum: int | None):
        if maximum is not None and maximum < 1:
            raise ValueError("max_work must be positive")
        self.maximum = maximum
        self.used = 0

    def take(self) -> None:
        self.used += 1
        if self.maximum is not None and self.used > self.maximum:
            raise RuleEvaluationError(f"rule evaluation exceeded the work limit of {self.maximum}")


def evaluate_rules(
    graph: Graph,
    *,
    max_diagnostics: int = 2**31 - 1,
    max_sample: int = 20,
    max_work: int | None = None,
) -> RuleResult:
    """Parse and evaluate every active rule over the complete candidate graph."""
    if max_diagnostics < 1 or max_sample < 1:
        raise ValueError("rule diagnostic limits must be positive")
    budget = _Budget(max_work)
    try:
        views = _read_views(graph)
        rules = _read_rules(graph)
        _validate_view_references(views)
    except (RuleEvaluationError, TypeError, ValueError, json.JSONDecodeError) as exc:
        return RuleResult("inconclusive", (RuleDiagnostic(None, "inconclusive", str(exc)),))

    diagnostics: list[RuleDiagnostic] = []
    for rule_id, message, expression in rules:
        try:
            passed, offenders = _evaluate_bool(expression, graph, views, set(), budget)
            if not passed:
                diagnostics.append(_diagnostic(rule_id, "invalid", message, offenders, max_sample))
        except (RuleEvaluationError, TypeError, ValueError) as exc:
            diagnostics.append(
                RuleDiagnostic(rule_id, "inconclusive", f"Rule '{rule_id}' could not be evaluated: {exc}")
            )
        if len(diagnostics) >= max_diagnostics:
            break
    status = "inconclusive" if any(item.status == "inconclusive" for item in diagnostics) else (
        "invalid" if diagnostics else "valid"
    )
    return RuleResult(status, tuple(diagnostics))


def _read_views(graph: Graph) -> dict[str, Any]:
    views: dict[str, Any] = {}
    for node in sorted((item for item in graph.nodes if item.kind == "validation-view"), key=lambda item: ordinal_key(item.id)):
        version = node.attribute("view:version")
        name = node.attribute("view:name")
        expression = node.attribute("view:expression")
        if version is None or version.kind is not GraphValueKind.INTEGER or version.value != 1:
            raise RuleEvaluationError(f"view '{node.id}' must declare view:version=1")
        if name is None or name.kind is not GraphValueKind.TEXT or not name.value.strip():
            raise RuleEvaluationError(f"view '{node.id}' requires a non-empty text view:name")
        if expression is None or expression.kind is not GraphValueKind.TEXT:
            raise RuleEvaluationError(f"view '{node.id}' requires text view:expression")
        if name.value in views:
            raise RuleEvaluationError(f"view name '{name.value}' is duplicated")
        parsed = _load_json(expression.value, node.id)
        _validate_set(parsed)
        views[name.value] = parsed
    return views


def _read_rules(graph: Graph) -> list[tuple[str, str, Any]]:
    result: list[tuple[str, str, Any]] = []
    for node in sorted(
        (item for item in graph.nodes if item.kind == "validation-rule" and "rule:active" in item.tags),
        key=lambda item: ordinal_key(item.id),
    ):
        version = node.attribute("rule:version")
        expression = node.attribute("rule:expression")
        if version is None or version.kind is not GraphValueKind.INTEGER or version.value != 1:
            raise RuleEvaluationError(f"rule '{node.id}' must declare rule:version=1")
        if expression is None or expression.kind is not GraphValueKind.TEXT:
            raise RuleEvaluationError(f"rule '{node.id}' requires text rule:expression")
        parsed = _load_json(expression.value, node.id)
        _validate_bool(parsed)
        result.append((node.id, node.text, parsed))
    return result


def _load_json(value: str, owner: str) -> Any:
    def pairs(items: list[tuple[str, Any]]) -> dict[str, Any]:
        result: dict[str, Any] = {}
        for key, item in items:
            if key in result:
                raise _DuplicateJsonKey(f"rule expression on '{owner}' repeats JSON property '{key}'")
            result[key] = item
        return result

    try:
        return json.loads(value, object_pairs_hook=pairs)
    except _DuplicateJsonKey:
        raise
    except json.JSONDecodeError as exc:
        raise RuleEvaluationError(f"rule expression on '{owner}' is malformed JSON: {exc.msg}") from exc


def _single(expression: Any, context: str) -> tuple[str, Any]:
    if not isinstance(expression, dict) or len(expression) != 1:
        raise RuleEvaluationError(f"each {context} expression must be an object with exactly one operator")
    return next(iter(expression.items()))


def _exact(spec: Any, required: set[str], optional: set[str] = frozenset()) -> dict[str, Any]:
    if not isinstance(spec, dict):
        raise RuleEvaluationError("expression value must be an object")
    missing = required - set(spec)
    unknown = set(spec) - required - optional
    if missing:
        raise RuleEvaluationError(f"expression property '{sorted(missing)[0]}' is required")
    if unknown:
        raise RuleEvaluationError(f"unknown expression property '{sorted(unknown)[0]}'")
    return spec


def _nonempty_string(value: Any, name: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise RuleEvaluationError(f"'{name}' must be a non-empty string")
    return value


def _array(value: Any, name: str, *, nonempty: bool = True) -> list[Any]:
    if not isinstance(value, list) or (nonempty and not value):
        raise RuleEvaluationError(f"'{name}' must be a{' non-empty' if nonempty else ''} array")
    return value


def _validate_set(expression: Any) -> None:
    operator, value = _single(expression, "set")
    if operator in {"nodes", "edges"}:
        _validate_filter(value)
    elif operator == "view":
        _nonempty_string(value, "view")
    elif operator in {"union", "intersect", "except"}:
        for item in _array(value, operator):
            _validate_set(item)
    elif operator == "reachable":
        spec = _exact(value, {"from", "edges", "direction"}, {"includeStart"})
        _validate_set(spec["from"])
        _validate_set(spec["edges"])
        if spec["direction"] not in {"outgoing", "incoming"}:
            raise RuleEvaluationError("reachable direction must be outgoing or incoming")
        if "includeStart" in spec and not isinstance(spec["includeStart"], bool):
            raise RuleEvaluationError("includeStart must be Boolean")
    else:
        raise RuleEvaluationError(f"unsupported set operator '{operator}'")


def _validate_filter(value: Any) -> None:
    spec = _exact(value, set(), {"id", "kind", "relationship", "tagPrefix", "tagsAll", "attributes", "sourceIn", "targetIn"})
    for name in ("id", "kind", "relationship", "tagPrefix"):
        if name in spec:
            _nonempty_string(spec[name], name)
    if "tagsAll" in spec:
        for tag in _array(spec["tagsAll"], "tagsAll", nonempty=False):
            _nonempty_string(tag, "tagsAll")
    if "attributes" in spec:
        names: set[str] = set()
        for attribute in _array(spec["attributes"], "attributes", nonempty=False):
            match = _exact(attribute, {"name", "kind", "value"})
            name = _nonempty_string(match["name"], "name")
            if name in names:
                raise RuleEvaluationError(f"selector repeats attribute '{name}'")
            names.add(name)
            _graph_value(match["kind"], match["value"])
    for name in ("sourceIn", "targetIn"):
        if name in spec:
            _validate_set(spec[name])


def _validate_bool(expression: Any) -> None:
    operator, value = _single(expression, "Boolean")
    if operator in {"and", "or"}:
        for item in _array(value, operator):
            _validate_bool(item)
    elif operator == "not":
        _validate_bool(value)
    elif operator == "exists":
        _validate_set(value)
    elif operator == "count":
        spec = _exact(value, {"set", "compare", "value"})
        _validate_set(spec["set"])
        _validate_comparison(spec["compare"], spec["value"])
    elif operator in {"subset", "equalSets"}:
        values = _array(value, operator)
        if len(values) != 2:
            raise RuleEvaluationError("set comparisons require exactly two operands")
        for item in values:
            _validate_set(item)
    elif operator == "all":
        spec = _exact(value, {"set", "condition"})
        _validate_set(spec["set"])
        condition, detail = _single(spec["condition"], "condition")
        if condition == "hasTag":
            _nonempty_string(detail, "hasTag")
        elif condition == "tagCount":
            count = _exact(detail, {"prefix", "compare", "value"})
            _nonempty_string(count["prefix"], "prefix")
            _validate_comparison(count["compare"], count["value"])
        else:
            raise RuleEvaluationError(f"unsupported condition operator '{condition}'")
    elif operator in {"acyclic", "singleChain"}:
        spec = _exact(value, {"nodes", "edges"})
        _validate_set(spec["nodes"])
        _validate_set(spec["edges"])
    elif operator == "tagSuffixMatch":
        spec = _exact(value, {"left", "leftPrefix", "right", "rightPrefix"})
        _validate_set(spec["left"])
        _validate_set(spec["right"])
        _nonempty_string(spec["leftPrefix"], "leftPrefix")
        _nonempty_string(spec["rightPrefix"], "rightPrefix")
    else:
        raise RuleEvaluationError(f"unsupported Boolean operator '{operator}'")


def _validate_comparison(operator: Any, value: Any) -> None:
    if operator not in {"eq", "ne", "lt", "lte", "gt", "gte"}:
        raise RuleEvaluationError(f"unsupported comparison '{operator}'")
    if isinstance(value, bool) or not isinstance(value, int) or value < 0 or value > 2**31 - 1:
        raise RuleEvaluationError("comparison value must be a non-negative 32-bit integer")


def _validate_view_references(views: dict[str, Any]) -> None:
    state: dict[str, int] = {}

    def references(expression: Any) -> Iterable[str]:
        operator, value = _single(expression, "set")
        if operator == "view":
            yield value
        elif operator in {"union", "intersect", "except"}:
            for item in value:
                yield from references(item)
        elif operator == "reachable":
            yield from references(value["from"])
            yield from references(value["edges"])
        elif operator in {"nodes", "edges"}:
            for name in ("sourceIn", "targetIn"):
                if name in value:
                    yield from references(value[name])

    def visit(name: str, path: list[str]) -> None:
        if name not in views:
            raise RuleEvaluationError(f"referenced view '{name}' does not exist")
        if state.get(name) == 2:
            return
        if state.get(name) == 1:
            raise RuleEvaluationError(f"view references are cyclic: {' -> '.join((*path, name))}")
        state[name] = 1
        for child in references(views[name]):
            visit(child, [*path, name])
        state[name] = 2

    for name in sorted(views, key=ordinal_key):
        visit(name, [])


def _diagnostic(rule_id: str, status: str, message: str, offenders: Iterable[str], max_sample: int) -> RuleDiagnostic:
    values = sorted(set(offenders), key=ordinal_key)
    return RuleDiagnostic(rule_id, status, message, tuple(values[:max_sample]), max(0, len(values) - max_sample))


def _evaluate_bool(expression: Any, graph: Graph, views: dict[str, Any], stack: set[str], budget: _Budget) -> tuple[bool, set[str]]:
    budget.take()
    operator, value = _single(expression, "Boolean")
    if operator in {"and", "or"}:
        results = [_evaluate_bool(item, graph, views, stack, budget) for item in value]
        passed = all(item[0] for item in results) if operator == "and" else any(item[0] for item in results)
        relevant = [item for item in results if not item[0]] if operator == "and" else ([] if passed else results)
        return passed, set().union(*(item[1] for item in relevant)) if relevant else set()
    if operator == "not":
        passed, offenders = _evaluate_bool(value, graph, views, stack, budget)
        return not passed, set() if not passed else offenders
    if operator == "exists":
        selected = _select(value, graph, views, stack, budget)
        return bool(selected), set()
    if operator == "count":
        selected = _select(value["set"], graph, views, stack, budget)
        passed = _compare(len(selected), value["compare"], value["value"])
        return passed, set() if passed else _ids(selected)
    if operator in {"subset", "equalSets"}:
        left = _select(value[0], graph, views, stack, budget)
        right = _select(value[1], graph, views, stack, budget)
        missing = left - right if operator == "subset" else left ^ right
        return not missing, _ids(missing)
    if operator == "all":
        selected = _select(value["set"], graph, views, stack, budget)
        offenders = {item for item in selected if not _condition(value["condition"], item, budget)}
        return not offenders, _ids(offenders)
    if operator in {"acyclic", "singleChain"}:
        nodes = _select(value["nodes"], graph, views, stack, budget)
        edges = _select(value["edges"], graph, views, stack, budget)
        passed = not _has_cycle(nodes, edges, budget) if operator == "acyclic" else _single_chain(nodes, edges, budget)
        return passed, set() if passed else _ids(nodes)
    left = _select(value["left"], graph, views, stack, budget)
    right = _select(value["right"], graph, views, stack, budget)
    left_tags = [tag[len(value["leftPrefix"]):] for item in left for tag in item.tags if tag.startswith(value["leftPrefix"])]
    right_tags = [tag[len(value["rightPrefix"]):] for item in right for tag in item.tags if tag.startswith(value["rightPrefix"])]
    passed = len(left) == len(right) == 1 and len(left_tags) == len(right_tags) == 1 and left_tags[0] == right_tags[0]
    return passed, set() if passed else _ids(left | right)


def _condition(condition: Any, item: Node | Edge, budget: _Budget) -> bool:
    budget.take()
    operator, value = _single(condition, "condition")
    if operator == "hasTag":
        return value in item.tags
    count = sum(tag.startswith(value["prefix"]) for tag in item.tags)
    return _compare(count, value["compare"], value["value"])


def _select(expression: Any, graph: Graph, views: dict[str, Any], stack: set[str], budget: _Budget) -> set[Node | Edge]:
    budget.take()
    operator, value = _single(expression, "set")
    if operator == "view":
        if value in stack:
            raise RuleEvaluationError(f"view cycle at '{value}'")
        return _select(views[value], graph, views, stack | {value}, budget)
    if operator in {"union", "intersect", "except"}:
        operands = [_select(item, graph, views, stack, budget) for item in value]
        result = set(operands[0])
        for operand in operands[1:]:
            if operator == "union": result.update(operand)
            elif operator == "intersect": result.intersection_update(operand)
            else: result.difference_update(operand)
        return result
    if operator == "nodes":
        return {item for item in graph.nodes if _matches(item, value, graph, views, stack, budget)}
    if operator == "edges":
        return {item for item in graph.edges if _matches(item, value, graph, views, stack, budget)}

    starts = _select(value["from"], graph, views, stack, budget)
    edges = _select(value["edges"], graph, views, stack, budget)
    adjacency: dict[str, set[str]] = {}
    for edge in edges:
        if isinstance(edge, Edge):
            source, target = (edge.target, edge.source) if value["direction"] == "incoming" else (edge.source, edge.target)
            adjacency.setdefault(source, set()).add(target)
    nodes = {item.id: item for item in graph.nodes}
    result: set[Node | Edge] = set(starts) if value.get("includeStart", False) else set()
    seen = {item.id for item in starts if isinstance(item, Node)}
    queue = sorted(seen, key=ordinal_key)
    while queue:
        current = queue.pop(0)
        for target in sorted(adjacency.get(current, ()), key=ordinal_key):
            budget.take()
            node = nodes.get(target)
            if node is not None:
                result.add(node)
                if target not in seen:
                    seen.add(target); queue.append(target)
    return result


def _matches(item: Node | Edge, spec: dict[str, Any], graph: Graph, views: dict[str, Any], stack: set[str], budget: _Budget) -> bool:
    budget.take()
    if "id" in spec and item.id != spec["id"]: return False
    if "kind" in spec and (not isinstance(item, Node) or item.kind != spec["kind"]): return False
    if "relationship" in spec and (not isinstance(item, Edge) or item.relationship != spec["relationship"]): return False
    if "tagPrefix" in spec and not any(tag.startswith(spec["tagPrefix"]) for tag in item.tags): return False
    if "tagsAll" in spec and not set(spec["tagsAll"]).issubset(item.tags): return False
    for match in spec.get("attributes", ()):
        expected = _graph_value(match["kind"], match["value"])
        if not any(attribute.name == match["name"] and attribute.value == expected for attribute in item.attributes): return False
    if isinstance(item, Edge):
        if "sourceIn" in spec and item.source not in _ids(_select(spec["sourceIn"], graph, views, stack, budget)): return False
        if "targetIn" in spec and item.target not in _ids(_select(spec["targetIn"], graph, views, stack, budget)): return False
    elif "sourceIn" in spec or "targetIn" in spec:
        return False
    return True


def _graph_value(kind: Any, value: Any) -> GraphValue:
    if kind == "text": return GraphValue.text(_nonempty_string(value, "value"))
    if kind == "integer":
        if isinstance(value, bool) or not isinstance(value, int): raise RuleEvaluationError("integer attribute match requires an integer value")
        return GraphValue.integer(value)
    if kind == "decimal": return GraphValue.decimal(_nonempty_string(value, "value"))
    if kind == "boolean":
        if not isinstance(value, bool): raise RuleEvaluationError("boolean attribute match requires a Boolean value")
        return GraphValue.boolean(value)
    if kind == "symbol": return GraphValue.symbol(_nonempty_string(value, "value"))
    if kind == "instant": return GraphValue.instant(_nonempty_string(value, "value"))
    raise RuleEvaluationError(f"unsupported or mismatched attribute kind '{kind}'")


def _has_cycle(nodes: set[Node | Edge], edges: set[Node | Edge], budget: _Budget) -> bool:
    node_ids = {item.id for item in nodes if isinstance(item, Node)}
    adjacency: dict[str, set[str]] = {}
    for edge in edges:
        if isinstance(edge, Edge) and edge.source in node_ids and edge.target in node_ids:
            adjacency.setdefault(edge.source, set()).add(edge.target)
    visiting: set[str] = set(); done: set[str] = set()
    def visit(node_id: str) -> bool:
        budget.take()
        if node_id in visiting: return True
        if node_id in done: return False
        visiting.add(node_id)
        if any(visit(target) for target in adjacency.get(node_id, ())): return True
        visiting.remove(node_id); done.add(node_id); return False
    return any(visit(node_id) for node_id in sorted(node_ids, key=ordinal_key))


def _single_chain(nodes: set[Node | Edge], edges: set[Node | Edge], budget: _Budget) -> bool:
    node_ids = {item.id for item in nodes if isinstance(item, Node)}
    selected = [item for item in edges if isinstance(item, Edge) and item.source in node_ids and item.target in node_ids]
    if _has_cycle(nodes, edges, budget): return False
    incoming = {node_id: 0 for node_id in node_ids}; outgoing = {node_id: 0 for node_id in node_ids}
    for edge in selected:
        budget.take(); incoming[edge.target] += 1; outgoing[edge.source] += 1
    return not node_ids or (len(selected) == len(node_ids) - 1 and sum(value == 0 for value in incoming.values()) == 1 and sum(value == 0 for value in outgoing.values()) == 1 and all(value <= 1 for value in incoming.values()) and all(value <= 1 for value in outgoing.values()))


def _compare(actual: int, operator: str, expected: int) -> bool:
    return {"eq": actual == expected, "ne": actual != expected, "lt": actual < expected, "lte": actual <= expected, "gt": actual > expected, "gte": actual >= expected}[operator]


def _ids(values: Iterable[Node | Edge]) -> set[str]:
    return {item.id for item in values}
