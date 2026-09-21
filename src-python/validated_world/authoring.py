"""Optional built-in authoring tools and OpenAI Responses presentation."""

from __future__ import annotations

import json
from pathlib import Path
from time import monotonic, sleep
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.parse import quote
from urllib.request import Request, urlopen

from .application import Application
from .config import load_authoring_config
from .models import Attribute, Edge, EntityKind, GraphValue, Node, Operation, OperationKind, ReviewDirection
from .protocol import edge_dto, json_loads_strict, node_dto
from .queries import Queries


INSTRUCTIONS = """Maintain the selected ValidatedWorld project through the bounded tools below.
Inspect project status and search before creating or changing graph concepts. Ask a focused
question when ambiguity would materially change graph meaning. Stable IDs share one namespace.
Use scope-parent only for the single purpose-rooted scope tree; direct semantic edges toward
consumers that may become stale. Build one process-local change, inspect proposal_preview after
the final mutation, and use write_change only when the evidence is coherent. Never invent review
dispositions, bypass independent review, claim an unreported write, or expose credentials."""


def _schema(properties: dict[str, Any], required: list[str]) -> dict[str, Any]:
    return {"type": "object", "properties": properties, "required": required, "additionalProperties": False}


_TEXT = {"type": "string", "minLength": 1}
_LIMIT = {"type": "integer", "minimum": 1}
_ATTRIBUTES = {"type": "array", "items": _schema({"name": _TEXT, "kind": {"type": "string", "enum": ["text", "integer", "decimal", "boolean", "symbol", "instant"]}, "value": {"type": "string"}}, ["name", "kind", "value"])}
_TAGS = {"type": "array", "items": {"type": "string", "minLength": 1}}

TOOL_DEFINITIONS = (
    {"name": "project_status", "description": "Inspect the fixed project path without a provider call.", "parameters": _schema({}, [])},
    {"name": "initialize_project", "description": "Create a purpose-only project when the fixed path does not exist.", "parameters": _schema({"project_id": _TEXT, "title": _TEXT, "purpose_id": _TEXT, "purpose_text": _TEXT}, ["project_id", "title", "purpose_id", "purpose_text"])},
    {"name": "search_graph", "description": "Bounded text or exact-tag search; required before adding entities.", "parameters": _schema({"text": {"type": ["string", "null"]}, "tag": {"type": ["string", "null"]}, "limit": _LIMIT}, ["text", "tag", "limit"])},
    {"name": "ranked_search_graph", "description": "Bounded explainable lexical search.", "parameters": _schema({"text": _TEXT, "limit": _LIMIT}, ["text", "limit"])},
    {"name": "read_node", "description": "Read one node by stable ID.", "parameters": _schema({"node_id": _TEXT}, ["node_id"])},
    {"name": "read_edge", "description": "Read one edge by stable ID.", "parameters": _schema({"edge_id": _TEXT}, ["edge_id"])},
    {"name": "read_scope", "description": "Read scope ancestors and bounded descendants.", "parameters": _schema({"node_id": _TEXT, "limit": _LIMIT}, ["node_id", "limit"])},
    {"name": "graph_health", "description": "Read bounded graph-quality diagnostics.", "parameters": _schema({"limit": _LIMIT}, ["limit"])},
    {"name": "begin_change", "description": "Begin the one process-local incremental change.", "parameters": _schema({"intent": _TEXT}, ["intent"])},
    {"name": "put_node", "description": "Add or replace one complete node; add requires prior search.", "parameters": _schema({"mode": {"type": "string", "enum": ["add", "replace"]}, "id": _TEXT, "text": _TEXT, "kind": {"type": ["string", "null"]}, "tags": _TAGS, "attributes": _ATTRIBUTES}, ["mode", "id", "text", "kind", "tags", "attributes"])},
    {"name": "put_edge", "description": "Add or replace one complete edge; add requires prior search.", "parameters": _schema({"mode": {"type": "string", "enum": ["add", "replace"]}, "id": _TEXT, "source": _TEXT, "target": _TEXT, "relationship": _TEXT, "review_direction": {"type": "string", "enum": ["none", "sourceToTarget", "targetToSource", "both"]}, "rationale": {"type": ["string", "null"]}, "tags": _TAGS, "attributes": _ATTRIBUTES}, ["mode", "id", "source", "target", "relationship", "review_direction", "rationale", "tags", "attributes"])},
    {"name": "remove_entity", "description": "Remove one existing node or edge by stable ID.", "parameters": _schema({"entity_kind": {"type": "string", "enum": ["node", "edge"]}, "id": _TEXT}, ["entity_kind", "id"])},
    {"name": "proposal_preview", "description": "Inspect the complete exact current proposal evidence.", "parameters": _schema({}, [])},
    {"name": "write_change", "description": "Account for affected/context evidence and attempt the exact guarded write without bypass.", "parameters": _schema({}, [])},
    {"name": "discard_change", "description": "Discard the active in-memory change.", "parameters": _schema({}, [])},
)


def _strict(arguments: Any, definition: dict[str, Any]) -> dict[str, Any]:
    if not isinstance(arguments, dict):
        raise ValueError("tool arguments must be an object")
    schema = definition["parameters"]
    required = set(schema["required"]); allowed = set(schema["properties"])
    if required - set(arguments): raise ValueError(f"missing tool argument '{sorted(required - set(arguments))[0]}'")
    if set(arguments) - allowed: raise ValueError(f"unknown tool argument '{sorted(set(arguments) - allowed)[0]}'")
    _validate_schema(arguments, schema, "tool arguments")
    return arguments


def _validate_schema(value: Any, schema: dict[str, Any], label: str) -> None:
    expected = schema.get("type")
    expected_types = expected if isinstance(expected, list) else [expected]
    matches = {
        "object": isinstance(value, dict),
        "array": isinstance(value, list),
        "string": isinstance(value, str),
        "integer": isinstance(value, int) and not isinstance(value, bool),
        "null": value is None,
    }
    if expected is not None and not any(matches.get(item, False) for item in expected_types):
        raise ValueError(f"{label} has the wrong type")
    if value is None:
        return
    if "enum" in schema and value not in schema["enum"]:
        raise ValueError(f"{label} has an unsupported value")
    if isinstance(value, str) and len(value) < schema.get("minLength", 0):
        raise ValueError(f"{label} must be nonempty text")
    if isinstance(value, int) and not isinstance(value, bool) and value < schema.get("minimum", value):
        raise ValueError(f"{label} must be at least {schema['minimum']}")
    if isinstance(value, list) and "items" in schema:
        for index, item in enumerate(value):
            _validate_schema(item, schema["items"], f"{label}[{index}]")
    if isinstance(value, dict):
        properties = schema.get("properties", {})
        required = set(schema.get("required", ()))
        missing = required - set(value)
        if missing:
            raise ValueError(f"{label} is missing '{sorted(missing)[0]}'")
        if schema.get("additionalProperties") is False:
            unknown = set(value) - set(properties)
            if unknown:
                raise ValueError(f"{label} contains unknown member '{sorted(unknown)[0]}'")
        for name, item in value.items():
            if name in properties:
                _validate_schema(item, properties[name], f"{label}.{name}")


def _attributes(values: list[dict[str, Any]]) -> tuple[Attribute, ...]:
    result = []
    for item in values:
        kind, raw = item["kind"], item["value"]
        if kind == "text": value = GraphValue.text(raw)
        elif kind == "integer": value = GraphValue.integer(int(raw, 10))
        elif kind == "decimal": value = GraphValue.decimal(raw)
        elif kind == "boolean":
            if raw.lower() not in {"true", "false"}: raise ValueError("boolean attribute values must be true or false")
            value = GraphValue.boolean(raw.lower() == "true")
        elif kind == "symbol": value = GraphValue.symbol(raw)
        elif kind == "instant": value = GraphValue.instant(raw)
        else: raise ValueError(f"unknown attribute kind '{kind}'")
        result.append(Attribute(item["name"], value))
    return tuple(result)


class AuthoringToolHost:
    def __init__(self, application: Application, path: str):
        if not path or not path.strip(): raise ValueError("a database path is required")
        self.application = application
        self.path = str(Path(path).resolve())
        self.session = None
        self.project_id: str | None = None
        self.searched = False

    def execute(self, name: str, arguments: dict[str, Any]) -> dict[str, Any]:
        definition = next((item for item in TOOL_DEFINITIONS if item["name"] == name), None)
        if definition is None: return {"ok": False, "error": f"Unknown authoring tool '{name}'."}
        try:
            arguments = _strict(arguments, definition)
            value = getattr(self, "_" + name)(arguments)
            return {"ok": True, "value": value}
        except Exception as exc:
            return {"ok": False, "error": str(exc), "errorType": type(exc).__name__}

    def _queries(self) -> Queries:
        if not Path(self.path).is_file(): raise ValueError("initialize the project first")
        project = self.application.store.load(self.path)
        self.project_id = project.graph.project_id
        return Queries(project)

    def _project_status(self, arguments):
        if not Path(self.path).is_file(): return {"exists": False, "path": self.path, "message": "No project exists at this path."}
        value = self.application.store.status(self.path); self.project_id = value["projectId"]
        return {"exists": True, "project": value, "semanticReview": self.application.review_config_loader().public()}

    def _initialize_project(self, arguments):
        if Path(self.path).exists(): raise ValueError("a project already exists at this path")
        project = self.application.initialize(self.path, arguments["project_id"], arguments["title"], arguments["purpose_id"], arguments["purpose_text"])
        self.project_id = project.graph.project_id
        return {"initialized": True, "projectId": self.project_id, "path": project.path}

    def _search_graph(self, arguments):
        if (arguments["text"] is None) == (arguments["tag"] is None): raise ValueError("supply exactly one of text or tag")
        self.searched = True
        return self._queries().search(arguments["text"], arguments["limit"]) if arguments["text"] is not None else self._queries().tag(arguments["tag"], arguments["limit"])

    def _ranked_search_graph(self, arguments): self.searched = True; return self._queries().ranked_search(arguments["text"], arguments["limit"])
    def _read_node(self, arguments): return self._queries().node(arguments["node_id"])
    def _read_edge(self, arguments): return self._queries().edge(arguments["edge_id"])
    def _read_scope(self, arguments): return self._queries().scope(arguments["node_id"], arguments["limit"])
    def _graph_health(self, arguments): return self._queries().health(arguments["limit"])

    def _begin_change(self, arguments):
        if self.session is not None: raise ValueError("this conversation already has an active change")
        queries = self._queries()
        self.session = self.application.begin(self.path, queries.graph.project_id, "ai-authoring-agent", arguments["intent"])
        return self._summary()

    def _put_node(self, arguments):
        if arguments["mode"] == "add" and not self.searched: raise ValueError("search the existing graph before adding a node")
        node = Node(arguments["id"], arguments["text"], arguments["kind"], tuple(arguments["tags"]), _attributes(arguments["attributes"]))
        return self._patch(Operation(OperationKind.ADD if arguments["mode"] == "add" else OperationKind.REPLACE, EntityKind.NODE, node.id, node=node))

    def _put_edge(self, arguments):
        if arguments["mode"] == "add" and not self.searched: raise ValueError("search the existing graph before adding an edge")
        directions = {"none": ReviewDirection.NONE, "sourceToTarget": ReviewDirection.SOURCE_TO_TARGET, "targetToSource": ReviewDirection.TARGET_TO_SOURCE, "both": ReviewDirection.BOTH}
        edge = Edge(arguments["id"], arguments["source"], arguments["target"], arguments["relationship"], directions[arguments["review_direction"]], arguments["rationale"], tuple(arguments["tags"]), _attributes(arguments["attributes"]))
        return self._patch(Operation(OperationKind.ADD if arguments["mode"] == "add" else OperationKind.REPLACE, EntityKind.EDGE, edge.id, edge=edge))

    def _remove_entity(self, arguments):
        kind = EntityKind.NODE if arguments["entity_kind"] == "node" else EntityKind.EDGE
        return self._patch(Operation(OperationKind.REMOVE, kind, arguments["id"]))

    def _patch(self, operation):
        if self.session is None: raise ValueError("begin a change before using this tool")
        self.session = self.application.apply(self.session.reference(), (operation,), patch=True)
        return self._summary()

    def _proposal_preview(self, arguments):
        if self.session is None: raise ValueError("begin a change before using this tool")
        return self.session.preview(max(1, len(self.session.review_items())))

    def _write_change(self, arguments):
        if self.session is None: raise ValueError("begin a change before using this tool")
        if not self.session.operations: raise ValueError("there is no proposal to write")
        dispositions = [{"nodeId": item["nodeId"], "kind": "updated" if item["isDirectChange"] else "reviewedNoChange"} for item in self.session.affected_nodes]
        self.session = self.application.review(self.session.reference(), dispositions, [item["nodeId"] for item in self.session.scope_context])
        # The authoring host has already shown the complete affected/context set;
        # include the resulting disposition evidence in the exact bound preview.
        self.session.preview(max(1, len(self.session.review_items())))
        result = self.application.write(self.session.reference(), bypass_ai_review=False)
        if result["status"] == "written": self.session = None
        return {"result": result}

    def _discard_change(self, arguments):
        if self.session is None: raise ValueError("begin a change before using this tool")
        result = self.application.discard(self.session.reference()); self.session = None
        return result

    def _summary(self):
        return {"reference": self.session.reference(), "operationCount": len(self.session.operations), "affectedNodeCount": len(self.session.affected_nodes), "contextNodeCount": len(self.session.scope_context), "pendingReviewCount": sum(item["kind"] == "pending" for item in self.session.dispositions.values()), "validation": self.session.proposed_validation.status, "analysis": "complete", "note": "Use proposal_preview for complete evidence."}


def serialize_authoring_request(input_items: list[dict[str, Any]], previous_response_id: str | None = None,
                                config: dict[str, Any] | None = None) -> str:
    config = config or load_authoring_config()
    outbound: dict[str, Any] = {
        "model": config["model"], "background": True, "store": True, "instructions": INSTRUCTIONS,
        "input": input_items, "reasoning": {"effort": "low"}, "max_output_tokens": 4000,
        "parallel_tool_calls": False,
        "tools": [{"type": "function", "name": item["name"], "description": item["description"], "parameters": item["parameters"], "strict": True} for item in TOOL_DEFINITIONS],
        "tool_choice": "auto", "text": {"format": {"type": "text"}, "verbosity": "low"},
    }
    if previous_response_id is not None: outbound["previous_response_id"] = previous_response_id
    return json.dumps(outbound, ensure_ascii=False, separators=(",", ":"))


def openai_authoring_response(input_items: list[dict[str, Any]], previous_response_id: str | None = None,
                              config: dict[str, Any] | None = None, *, transport=None) -> dict[str, Any]:
    """Create one Responses API request, poll that response, and normalize its output."""
    config = config or load_authoring_config()
    if not config.get("enabled") or not config.get("configured"):
        raise ValueError("AI authoring is not configured and enabled")
    body = serialize_authoring_request(input_items, previous_response_id, config)
    headers = {"Authorization": "Bearer " + config["_apiKey"], "Content-Type": "application/json"}
    request = Request("https://api.openai.com/v1/responses", data=body.encode("utf-8"), headers=headers, method="POST")
    try:
        deadline = monotonic() + config["timeoutSeconds"]
        value = _authoring_transport(request, config["timeoutSeconds"], transport)
        if not isinstance(value, dict) or not isinstance(value.get("id"), str) or not value["id"]:
            raise ValueError("authoring response is missing its response ID")
        response_id = value["id"]
        status = value.get("status")
        if not isinstance(status, str) or not status:
            raise ValueError("authoring response is missing its status")
        poll_interval = config.get("pollIntervalSeconds", 1)
        if not isinstance(poll_interval, (int, float)) or isinstance(poll_interval, bool) or poll_interval < 0:
            raise ValueError("authoring poll interval must be nonnegative")
        while status in {"queued", "in_progress"}:
            remaining = deadline - monotonic()
            if remaining <= 0:
                raise TimeoutError("authoring response polling exceeded its deadline")
            if poll_interval:
                sleep(min(poll_interval, remaining))
            poll = Request(
                "https://api.openai.com/v1/responses/" + quote(response_id, safe=""),
                headers=headers,
                method="GET",
            )
            value = _authoring_transport(poll, max(0.001, deadline - monotonic()), transport)
            if not isinstance(value, dict) or value.get("id") != response_id:
                raise ValueError("authoring poll response does not match the created response")
            status = value.get("status")
            if not isinstance(status, str) or not status:
                raise ValueError("authoring poll response is missing its status")
        if status != "completed":
            raise ValueError(f"authoring response ended with status '{status}'")
        texts: list[str] = []; calls: list[dict[str, Any]] = []
        output = value.get("output")
        if not isinstance(output, list):
            raise ValueError("authoring response output must be an array")
        for item in output:
            if not isinstance(item, dict): raise ValueError("authoring response output item must be an object")
            if item.get("type") == "function_call":
                arguments = json_loads_strict(item.get("arguments", ""))
                if not isinstance(item.get("call_id"), str) or not isinstance(item.get("name"), str) or not isinstance(arguments, dict):
                    raise ValueError("authoring function call is malformed")
                calls.append({"callId": item["call_id"], "name": item["name"], "arguments": arguments})
            elif item.get("type") == "message":
                content_items = item.get("content")
                if not isinstance(content_items, list):
                    raise ValueError("authoring message content must be an array")
                for content in content_items:
                    if not isinstance(content, dict):
                        raise ValueError("authoring message content item must be an object")
                    if content.get("type") == "refusal": raise ValueError("authoring response was refused")
                    if content.get("type") in {"output_text", "text"} and isinstance(content.get("text"), str): texts.append(content["text"])
        if len(calls) > 1: raise ValueError("parallel or multiple authoring tool calls are not supported")
        text = "\n".join(item for item in texts if item.strip()) or None
        if text is None and not calls: raise ValueError("authoring response contained neither text nor a tool call")
        usage = value.get("usage")
        if usage is not None:
            if not isinstance(usage, dict) or any(not isinstance(usage.get(name), int) or isinstance(usage.get(name), bool) or usage[name] < 0 for name in ("input_tokens", "output_tokens", "total_tokens")):
                raise ValueError("authoring response usage is malformed")
        return {"responseId": response_id, "text": text, "toolCall": calls[0] if calls else None, "usage": usage}
    except (HTTPError, URLError, TimeoutError, OSError, TypeError, UnicodeDecodeError, json.JSONDecodeError, ValueError) as exc:
        raise RuntimeError(f"AI authoring provider failed: {type(exc).__name__}") from exc


def _authoring_transport(request: Request, timeout: float, transport) -> dict[str, Any]:
    if transport is None:
        with urlopen(request, timeout=timeout) as response:
            raw = response.read()
    else:
        raw = transport(request, timeout)
    if isinstance(raw, dict):
        return raw
    value = json_loads_strict(raw.decode("utf-8") if isinstance(raw, bytes) else raw)
    if not isinstance(value, dict):
        raise ValueError("authoring response must be an object")
    return value


class AuthoringConversation:
    """One bounded, recoverable conversation over a fixed host-owned project path."""
    def __init__(self, host: AuthoringToolHost, provider=openai_authoring_response, *, max_tool_calls: int = 32):
        if not isinstance(max_tool_calls, int) or isinstance(max_tool_calls, bool) or max_tool_calls < 1:
            raise ValueError("max_tool_calls must be a positive integer")
        self.host = host; self.provider = provider; self.max_tool_calls = max_tool_calls
        self.previous_response_id: str | None = None

    def turn(self, text: str) -> dict[str, Any]:
        if not isinstance(text, str) or not text.strip(): raise ValueError("authoring input must be nonempty text")
        input_items = [{"role": "user", "content": [{"type": "input_text", "text": text}]}]
        calls = 0; warnings: list[str] = []
        try:
            while True:
                response = self.provider(input_items, self.previous_response_id)
                self.previous_response_id = response["responseId"]
                call = response.get("toolCall")
                if call is None:
                    return {"text": response.get("text"), "toolCallCount": calls, "warnings": warnings}
                if calls >= self.max_tool_calls:
                    warnings.append("tool-limit: the provider requested another tool after the per-turn limit")
                    return {"text": response.get("text"), "toolCallCount": calls, "warnings": warnings}
                execution = self.host.execute(call["name"], call["arguments"]); calls += 1
                input_items = [{"type": "function_call_output", "call_id": call["callId"], "output": json.dumps(execution, ensure_ascii=False, separators=(",", ":"))}]
        except Exception as exc:
            self.previous_response_id = None
            return {"text": None, "toolCallCount": calls, "warnings": [f"conversation-reset: {exc}"]}
