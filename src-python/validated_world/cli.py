"""Public console and strict line-oriented automation interface."""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
from pathlib import Path
import sys
from typing import Any

from . import __version__
from .application import Application, sample_graph
from .artifacts import check_artifacts
from .authoring import AuthoringConversation, AuthoringToolHost
from .bulk import plan_bulk
from .config import load_authoring_config, load_review_config
from .merge import merge_projects
from .models import EntityKind, Graph, Operation, OperationKind, Node, Edge, ordinal_key
from .protocol import graph_from_dto, json_loads_strict, operation_from_dto
from .queries import Queries
from .storage import ProjectStore
from .templates import descriptor, dto, instantiate, resolve


SUCCESS = 0
USAGE = 1
DOMAIN = 2
UNEXPECTED = 3


def _json(value: Any) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def _print_help(out) -> None:
    out.write("ValidatedWorld - local semantic graph change control\n")
    out.write(f"Version {__version__}\n")
    out.write("Supported product language: English. Unicode graph text can be stored, but non-English workflows are unsupported and unvalidated.\n\n")
    out.write("Commands:\n  project   Initialize, inspect, compare, verify, back up, or export a project\n  artifact  Check opt-in external artifact anchors\n  read      Run bounded graph queries\n  sample    List or create built-in disposable samples\n  template  List, export, describe, or instantiate graph templates\n  ai        Show nonsecret status or run the optional authoring assistant\n  shell     Run the stateful NDJSON workflow until EOF\n  ndjson    Run the structured automation interface\n")


def _stored(project):
    return {"path": project.path, "projectId": project.graph.project_id, "title": project.graph.title, "purposeNodeId": project.graph.purpose_node_id, "nodeCount": len(project.graph.nodes), "edgeCount": len(project.graph.edges), "stateFingerprint": project.state_fingerprint, "createdUtc": project.created_utc, "updatedUtc": project.updated_utc}


def _metadata_change(base, target):
    values = []
    for field, left, right in (("projectId", base.graph.project_id, target.graph.project_id), ("title", base.graph.title, target.graph.title), ("purposeNodeId", base.graph.purpose_node_id, target.graph.purpose_node_id)):
        if left != right: values.append({"field": field, "oldValue": left, "newValue": right})
    return values


def _diff(base_path: str, target_path: str, limit: int = 100, cursor: str | None = None) -> dict:
    if not isinstance(limit, int) or isinstance(limit, bool) or limit < 1: raise ValueError("diff limit must be a positive integer")
    store = ProjectStore(); base = store.load(base_path); target = store.load(target_path)
    if base.graph.project_id != target.graph.project_id: raise ValueError("target project ID does not match the base project")
    changes = []
    base_nodes = {item.id: item for item in base.graph.nodes}; target_nodes = {item.id: item for item in target.graph.nodes}
    base_edges = {item.id: item for item in base.graph.edges}; target_edges = {item.id: item for item in target.graph.edges}
    from .protocol import node_dto, edge_dto, operation_dto
    for entity_id in sorted(set(base_nodes) | set(target_nodes), key=ordinal_key):
        left, right = base_nodes.get(entity_id), target_nodes.get(entity_id)
        if left is None: changes.append({"kind": "add", "entityKind": "node", "entityId": entity_id, "oldNode": None, "newNode": node_dto(right), "oldEdge": None, "newEdge": None, "changedFields": []})
        elif right is None: changes.append({"kind": "remove", "entityKind": "node", "entityId": entity_id, "oldNode": node_dto(left), "newNode": None, "oldEdge": None, "newEdge": None, "changedFields": []})
        elif left != right: changes.append({"kind": "replace", "entityKind": "node", "entityId": entity_id, "oldNode": node_dto(left), "newNode": node_dto(right), "oldEdge": None, "newEdge": None, "changedFields": [key for key in ("text", "kind", "tags", "attributes") if getattr(left, key) != getattr(right, key)]})
    for entity_id in sorted(set(base_edges) | set(target_edges), key=ordinal_key):
        left, right = base_edges.get(entity_id), target_edges.get(entity_id)
        if left is None: changes.append({"kind": "add", "entityKind": "edge", "entityId": entity_id, "oldNode": None, "newNode": None, "oldEdge": None, "newEdge": edge_dto(right), "changedFields": []})
        elif right is None: changes.append({"kind": "remove", "entityKind": "edge", "entityId": entity_id, "oldNode": None, "newNode": None, "oldEdge": edge_dto(left), "newEdge": None, "changedFields": []})
        elif left != right: changes.append({"kind": "replace", "entityKind": "edge", "entityId": entity_id, "oldNode": None, "newNode": None, "oldEdge": edge_dto(left), "newEdge": edge_dto(right), "changedFields": [key for key in ("source", "target", "relationship", "reviewDirection", "rationale", "tags", "attributes") if getattr(left, {"reviewDirection": "review_direction"}.get(key, key)) != getattr(right, {"reviewDirection": "review_direction"}.get(key, key))]})
    signature = hashlib.sha256((base.state_fingerprint + target.state_fingerprint + ":" + str(limit)).encode()).hexdigest()
    offset = 0
    if cursor:
        try:
            value = base64.b64decode(cursor, validate=True).decode(); expected, offset_text = value.rsplit(":", 1)
            if expected != signature: raise ValueError
            offset = int(offset_text)
            if offset < 0 or offset > len(changes): raise ValueError
        except (ValueError, UnicodeDecodeError) as exc: raise ValueError("invalid diff cursor") from exc
    items = changes[offset:offset + limit]; next_offset = offset + len(items)
    return {"basePath": base.path, "targetPath": target.path, "projectId": target.graph.project_id, "baseFingerprint": base.state_fingerprint, "targetFingerprint": target.state_fingerprint, "metadataChanges": _metadata_change(base, target), "summary": {"metadataChanges": len(_metadata_change(base, target)), "nodesAdded": sum(item["kind"] == "add" and item["entityKind"] == "node" for item in changes), "nodesReplaced": sum(item["kind"] == "replace" and item["entityKind"] == "node" for item in changes), "nodesRemoved": sum(item["kind"] == "remove" and item["entityKind"] == "node" for item in changes), "edgesAdded": sum(item["kind"] == "add" and item["entityKind"] == "edge" for item in changes), "edgesReplaced": sum(item["kind"] == "replace" and item["entityKind"] == "edge" for item in changes), "edgesRemoved": sum(item["kind"] == "remove" and item["entityKind"] == "edge" for item in changes), "entityChanges": len(changes), "totalChanges": len(changes) + len(_metadata_change(base, target))}, "items": items, "totalCount": len(changes), "nextCursor": base64.b64encode(f"{signature}:{next_offset}".encode()).decode() if next_offset < len(changes) else None, "omission": {"reason": "outputLimit", "remainingCount": len(changes) - next_offset, "message": "Additional deterministic results are available through the next cursor."} if next_offset < len(changes) else None}


def _artifact_check(path: str, node_id: str | None = None, roots: list[str] | None = None,
                    max_anchors: int = 2**31 - 1, max_sample_bytes: int = 4096) -> dict:
    project = ProjectStore().load(path)
    return check_artifacts(path, project.graph, node_id, allowed_roots=roots or (),
                           max_anchors=max_anchors, max_sample_bytes=max_sample_bytes)


def _command_options(arguments: list[str], start: int, allowed: set[str], repeated: set[str] | None = None) -> dict[str, Any]:
    repeated = repeated or set()
    values: dict[str, Any] = {}
    index = start
    while index < len(arguments):
        name = arguments[index]
        if name not in allowed or index + 1 >= len(arguments):
            raise ValueError(f"unsupported or incomplete option '{name}'")
        if name in values and name not in repeated:
            raise ValueError(f"duplicate option '{name}'")
        if name in repeated:
            values.setdefault(name, []).append(arguments[index + 1])
        else:
            values[name] = arguments[index + 1]
        index += 2
    return values


def direct_command(arguments: list[str], out, err) -> int:
    if not arguments or arguments[0] in {"help", "--help", "-h"}:
        _print_help(out); return SUCCESS
    if len(arguments) == 1 and arguments[0] in {"version", "--version", "-v"}:
        out.write(f"ValidatedWorld.Cli {__version__}\n"); return SUCCESS
    store = ProjectStore(); group = arguments[0]
    try:
        if group == "project":
            if len(arguments) < 2 or arguments[1] in {"help", "--help", "-h"}:
                out.write("project init <path> <projectId> <title> <purposeNodeId> <purposeText>\nproject status|open|verify <path>\nproject backup <source> <destination>\nproject export-sql <path>\nproject diff <base> <target> [--limit N] [--cursor TOKEN]\nproject merge <base> <ours> <theirs>\nproject bulk-plan <path> <manifest> [--chunk-size N] [--cursor TOKEN]\n"); return SUCCESS
            command = arguments[1]
            if command == "init" and len(arguments) == 7:
                out.write(_json(_stored(Application(store).initialize(arguments[2], arguments[3], arguments[4], arguments[5], arguments[6]))) + "\n"); return SUCCESS
            if command == "status" and len(arguments) == 3: out.write(_json(store.status(arguments[2])) + "\n"); return SUCCESS
            if command == "open" and len(arguments) == 3:
                project = store.load(arguments[2]); from .protocol import graph_dto; out.write(_json({"project": _stored(project), "graph": graph_dto(project.graph)}) + "\n"); return SUCCESS
            if command == "verify" and len(arguments) == 3: out.write(_json(store.verify(arguments[2])) + "\n"); return SUCCESS
            if command == "backup" and len(arguments) == 4: out.write(_json(_stored(store.backup(arguments[2], arguments[3]))) + "\n"); return SUCCESS
            if command == "export-sql" and len(arguments) == 3: out.write(store.export_sql(arguments[2])); return SUCCESS
            if command == "diff" and len(arguments) >= 4:
                options = _command_options(arguments, 4, {"--limit", "--cursor"})
                limit = int(options.get("--limit", 100)); cursor = options.get("--cursor"); out.write(_json(_diff(arguments[2], arguments[3], limit, cursor)) + "\n"); return SUCCESS
            if command == "merge" and len(arguments) == 5: out.write(_json(merge_projects(arguments[2], arguments[3], arguments[4])) + "\n"); return SUCCESS
            if command == "bulk-plan" and len(arguments) >= 4:
                options = _command_options(arguments, 4, {"--chunk-size", "--cursor"})
                chunk_size = int(options.get("--chunk-size", 100)); cursor = options.get("--cursor"); out.write(_json(plan_bulk(arguments[2], arguments[3], chunk_size, cursor)) + "\n"); return SUCCESS
            raise ValueError("incorrect project arguments")
        if group == "read":
            if len(arguments) < 3: raise ValueError("a database path is required")
            project = store.load(arguments[2]); query = Queries(project); command = arguments[1]
            positional = []; opts = {}; index = 3
            while index < len(arguments):
                item = arguments[index]
                if item.startswith("--"):
                    if item not in {"--limit", "--cursor", "--max-depth", "--max-visited-nodes"} or index + 1 >= len(arguments):
                        raise ValueError(f"unsupported or incomplete read option '{item}'")
                    if item in opts:
                        raise ValueError(f"duplicate read option '{item}'")
                    opts[item] = arguments[index + 1]; index += 2
                else:
                    positional.append(item); index += 1
            limit = int(opts.get("--limit", 100)); cursor = opts.get("--cursor")
            max_depth = int(opts.get("--max-depth", 2**31 - 1)); max_visited = int(opts.get("--max-visited-nodes", 2**31 - 1))
            expected = {"node": 1, "edge": 1, "nodes": 0, "edges": 0, "search": 1, "ranked-search": 1, "tag": 1, "scope": 1, "neighbors": 1, "dependencies": 1, "path": 2, "context": 1, "health": 0, "report": 0}
            if command not in expected: raise ValueError(f"unknown read command '{command}'")
            if len(positional) != expected[command]: raise ValueError(f"read {command} expects {expected[command]} argument(s) after the database path")
            if command == "node": result = query.node(positional[0])
            elif command == "edge": result = query.edge(positional[0])
            elif command == "nodes": result = query.nodes(limit, cursor)
            elif command == "edges": result = query.edges(limit, cursor)
            elif command == "search": result = query.search(positional[0], limit, cursor)
            elif command == "ranked-search": result = query.ranked_search(positional[0], limit, cursor)
            elif command == "tag": result = query.tag(positional[0], limit, cursor)
            elif command == "scope": result = query.scope(positional[0], limit, cursor, max_depth, max_visited)
            elif command == "neighbors": result = query.neighbors(positional[0], limit, cursor)
            elif command == "dependencies": result = query.dependencies(positional[0], limit, cursor)
            elif command == "path": result = query.path(positional[0], positional[1], max_depth, max_visited)
            elif command == "context": result = query.context(positional[0].split(","), max_depth, max_visited)
            else: result = query.health(limit)
            out.write(_json(result) + "\n"); return SUCCESS
        if group == "artifact" and len(arguments) >= 3 and arguments[1] == "check":
            options = _command_options(arguments, 3, {"--allow-root", "--node-id", "--max-anchors", "--max-sample-bytes"}, {"--allow-root"})
            roots = options.get("--allow-root", [])
            node_id = options.get("--node-id")
            max_anchors = int(options.get("--max-anchors", 2**31 - 1))
            max_sample = int(options.get("--max-sample-bytes", 4096))
            out.write(_json(_artifact_check(arguments[2], node_id, roots, max_anchors, max_sample)) + "\n"); return SUCCESS
        if group == "sample":
            if len(arguments) == 2 and arguments[1] == "list": out.write(_json(["technical-project"]) + "\n"); return SUCCESS
            if len(arguments) == 4 and arguments[1] == "create":
                if arguments[2] != "technical-project": raise ValueError(f"unknown sample '{arguments[2]}'")
                out.write(_json(_stored(store.initialize(arguments[3], sample_graph()))) + "\n"); return SUCCESS
            raise ValueError("incorrect sample arguments")
        if group == "template":
            if len(arguments) == 2 and arguments[1] == "list": out.write(_json([{"id": name, "description": descriptor(resolve(name))["description"], "version": 1} for name in ("code-development", "research-notebook")]) + "\n"); return SUCCESS
            if len(arguments) == 3 and arguments[1] == "describe": out.write(_json(descriptor(resolve(arguments[2]))) + "\n"); return SUCCESS
            if len(arguments) == 4 and arguments[1] == "export":
                template = resolve(arguments[2]); Path(arguments[3]).write_text(_json(dto(template)) + "\n", encoding="utf-8", newline="\n"); out.write(_json({"path": str(Path(arguments[3]).resolve()), "template": descriptor(template)}) + "\n"); return SUCCESS
            if len(arguments) == 7 and arguments[1] == "instantiate":
                template = resolve(arguments[2]); graph = instantiate(template, arguments[4], arguments[5], arguments[6]); out.write(_json(_stored(store.initialize(arguments[3], graph))) + "\n"); return SUCCESS
            raise ValueError("incorrect template arguments")
        if group == "ai":
            if len(arguments) == 2 and arguments[1] == "status":
                out.write(_json(load_review_config().public() | {"authoring": {key: value for key, value in load_authoring_config().items() if not key.startswith("_")}}) + "\n"); return SUCCESS
            if len(arguments) == 3 and arguments[1] == "assistant":
                configuration = load_authoring_config()
                if not configuration["enabled"] or not configuration["configured"]:
                    raise ValueError("AI authoring is not configured and enabled; set the documented environment variables or use the manual NDJSON workflow")
                conversation = AuthoringConversation(AuthoringToolHost(Application(), arguments[2]), max_tool_calls=configuration["maxToolCallsPerTurn"])
                out.write("ValidatedWorld AI authoring assistant. Type exit to stop.\n"); out.flush()
                for line in sys.stdin:
                    if line.strip().lower() in {"exit", "quit"}: break
                    if not line.strip(): continue
                    result = conversation.turn(line.strip())
                    if result["text"]: out.write(result["text"] + "\n")
                    for warning in result["warnings"]: err.write("warning[" + warning + "]\n")
                    out.flush(); err.flush()
                if conversation.host.session is not None: err.write("warning[session-loss]: an unwritten in-memory change was discarded when the assistant exited\n")
                return SUCCESS
            raise ValueError("expected 'ai status' or 'ai assistant <path>'")
        if group == "shell":
            return ndjson_loop(sys.stdin, out, err)
        if group == "ndjson": return ndjson_loop(sys.stdin, out, err)
        raise ValueError(f"unknown command group '{group}'")
    except (ValueError, KeyError, FileExistsError, FileNotFoundError) as exc:
        err.write(f"error[invalid-argument]: {exc}\n"); return DOMAIN if isinstance(exc, (FileExistsError, FileNotFoundError)) else USAGE
    except Exception as exc:
        err.write(f"error[unexpected]: {exc}\n"); return UNEXPECTED


def _result(command: str, payload: Any) -> dict:
    return {"version": 1, "command": command, "status": "ok", "payload": payload}


def _error(command: str, code: str, message: str) -> dict:
    return {"version": 1, "command": command, "status": "error", "payload": {"code": code, "message": message}}


def _ops(payload: dict) -> tuple[Operation, ...]:
    batch = payload["operations"]
    _exact(batch, {"operations"}, set(), "operation batch")
    if not isinstance(batch["operations"], list):
        raise ValueError("operation batch operations must be an array")
    return tuple(operation_from_dto(item) for item in batch["operations"])


def _exact(value: Any, required: set[str], optional: set[str], label: str) -> dict:
    if not isinstance(value, dict):
        raise ValueError(f"{label} must be an object")
    missing = required - set(value)
    unknown = set(value) - required - optional
    if missing:
        raise ValueError(f"{label} is missing required member '{sorted(missing)[0]}'")
    if unknown:
        raise ValueError(f"{label} contains unknown member '{sorted(unknown)[0]}'")
    return value


_REFERENCE_FIELDS = {"projectId", "sessionId", "baseFingerprint", "operationFingerprint", "proposedFingerprint", "affectedFingerprint", "reviewFingerprint"}


def _validate_payload(command: str, payload: Any) -> dict:
    shapes = {
        "host.help": (set(), set()), "host.exit": (set(), set()), "ai.status": (set(), set()),
        "project.init": ({"path", "projectId", "title", "purposeNodeId", "purposeText"}, set()),
        "project.status": ({"path"}, set()), "project.open": ({"path"}, set()),
        "project.verify": ({"path"}, set()), "project.export-sql": ({"path"}, set()),
        "project.backup": ({"sourcePath", "destinationPath"}, set()),
        "project.diff": ({"basePath", "targetPath"}, {"limit", "cursor"}),
        "project.merge": ({"basePath", "oursPath", "theirsPath"}, set()),
        "project.bulk_plan": ({"path", "manifestPath"}, {"chunkSize", "cursor"}),
        "artifact.check": ({"path"}, {"nodeId", "maxAnchors", "maxSampleBytes", "allowedRoots"}),
        "sample.list": (set(), set()), "sample.create": ({"sampleName", "path"}, set()),
        "template.list": (set(), set()), "template.describe": ({"name"}, set()),
        "template.export": ({"name", "destinationPath"}, set()),
        "template.instantiate": ({"name", "path", "projectId", "title", "purposeText"}, set()),
        "read.node": ({"path", "entityId"}, {"expectedProjectId"}),
        "read.edge": ({"path", "entityId"}, {"expectedProjectId"}),
        "read.nodes": ({"path"}, {"limit", "cursor", "expectedProjectId"}),
        "read.edges": ({"path"}, {"limit", "cursor", "expectedProjectId"}),
        "read.search": ({"path", "text"}, {"limit", "cursor", "expectedProjectId"}),
        "read.ranked_search": ({"path", "text"}, {"limit", "cursor", "expectedProjectId"}),
        "read.tag": ({"path", "tag"}, {"limit", "cursor", "expectedProjectId"}),
        "read.scope": ({"path", "nodeId"}, {"limit", "cursor", "maxDepth", "maxVisitedNodes", "expectedProjectId"}),
        "read.neighbors": ({"path", "entityId"}, {"limit", "cursor", "expectedProjectId"}),
        "read.dependencies": ({"path", "entityId"}, {"limit", "cursor", "expectedProjectId"}),
        "read.path": ({"path", "sourceNodeId", "targetNodeId"}, {"maxDepth", "maxVisitedNodes", "expectedProjectId"}),
        "read.context": ({"path", "nodeIds"}, {"maxDepth", "maxVisitedNodes", "expectedProjectId"}),
        "read.health": ({"path"}, {"limit", "expectedProjectId"}),
        "read.report": ({"path"}, {"limit", "expectedProjectId"}),
        "change.begin": ({"path", "projectId", "author", "intent"}, {"includeOperations", "includeProposedGraph"}),
        "change.show": ({"session"}, {"includeOperations", "includeProposedGraph"}),
        "change.affected": ({"session"}, set()),
        "change.omission-details": ({"reference", "fingerprint"}, {"limit", "cursor"}),
        "change.focus": ({"reference", "operations", "scopeParents"}, set()),
        "change.apply": ({"reference", "operations"}, {"includeOperations", "includeProposedGraph", "maxTraversalDepth", "maxAffectedNodes", "maxOutputItems"}),
        "change.patch": ({"reference", "operations"}, {"includeOperations", "includeProposedGraph", "maxTraversalDepth", "maxAffectedNodes", "maxOutputItems"}),
        "change.expand": ({"reference"}, {"includeOperations", "includeProposedGraph", "maxTraversalDepth", "maxAffectedNodes", "maxOutputItems"}),
        "change.preview": ({"reference"}, {"limit", "cursor"}),
        "change.review": ({"reference", "dispositions", "presentedContextNodeIds"}, {"includeOperations", "includeProposedGraph"}),
        "change.validate": ({"reference"}, {"includeOperations", "includeProposedGraph"}),
        "change.write": ({"reference"}, {"bypassAiReview"}),
        "change.discard": ({"reference"}, set()),
    }
    if command not in shapes:
        raise ValueError(f"unknown NDJSON command '{command}'")
    required, optional = shapes[command]
    payload = _exact(payload, required, optional, f"{command} payload")
    if "reference" in payload:
        _exact(payload["reference"], _REFERENCE_FIELDS, set(), "session reference")
        for name, value in payload["reference"].items():
            if not isinstance(value, str) or not value:
                raise ValueError(f"session reference member '{name}' must be nonempty text")
    if "session" in payload:
        _exact(payload["session"], {"projectId", "sessionId"}, set(), "session locator")
        if any(not isinstance(value, str) or not value for value in payload["session"].values()):
            raise ValueError("session locator members must be nonempty text")
    text_fields = {"path", "sourcePath", "destinationPath", "basePath", "targetPath", "oursPath", "theirsPath", "manifestPath", "projectId", "title", "purposeNodeId", "purposeText", "sampleName", "name", "entityId", "text", "tag", "nodeId", "sourceNodeId", "targetNodeId", "author", "intent", "fingerprint"}
    for name in text_fields & set(payload):
        if not isinstance(payload[name], str) or not payload[name].strip():
            raise ValueError(f"{name} must be nonempty text")
    positive_fields = {"limit", "chunkSize", "maxAnchors", "maxSampleBytes", "maxDepth", "maxVisitedNodes", "maxTraversalDepth", "maxAffectedNodes", "maxOutputItems"}
    for name in positive_fields & set(payload):
        if not isinstance(payload[name], int) or isinstance(payload[name], bool) or payload[name] < 1:
            raise ValueError(f"{name} must be a positive integer")
    for name in {"includeOperations", "includeProposedGraph", "bypassAiReview"} & set(payload):
        if not isinstance(payload[name], bool):
            raise ValueError(f"{name} must be Boolean")
    if "cursor" in payload and (not isinstance(payload["cursor"], str) or not payload["cursor"]):
        raise ValueError("cursor must be nonempty text")
    if "expectedProjectId" in payload and (not isinstance(payload["expectedProjectId"], str) or not payload["expectedProjectId"]):
        raise ValueError("expectedProjectId must be nonempty text")
    if "nodeIds" in payload and not isinstance(payload["nodeIds"], list):
        raise ValueError("nodeIds must be an array")
    if "nodeIds" in payload and any(not isinstance(item, str) or not item for item in payload["nodeIds"]):
        raise ValueError("nodeIds must contain only nonempty text")
    if "allowedRoots" in payload and not isinstance(payload["allowedRoots"], list):
        raise ValueError("allowedRoots must be an array")
    if "allowedRoots" in payload and any(not isinstance(item, str) or not item for item in payload["allowedRoots"]):
        raise ValueError("allowedRoots must contain only nonempty text")
    if "dispositions" in payload:
        if not isinstance(payload["dispositions"], list):
            raise ValueError("dispositions must be an array")
        for item in payload["dispositions"]:
            _exact(item, {"nodeId", "kind"}, {"rationale"}, "review disposition")
            if not isinstance(item["nodeId"], str) or not item["nodeId"]:
                raise ValueError("review disposition nodeId must be nonempty text")
            if item["kind"] not in {"updated", "reviewedNoChange", "notApplicable", "pending"}:
                raise ValueError("review disposition kind is unsupported")
            if "rationale" in item and item["rationale"] is not None and not isinstance(item["rationale"], str):
                raise ValueError("review disposition rationale must be text or null")
    if "presentedContextNodeIds" in payload and not isinstance(payload["presentedContextNodeIds"], list):
        raise ValueError("presentedContextNodeIds must be an array")
    if "presentedContextNodeIds" in payload and any(not isinstance(item, str) or not item for item in payload["presentedContextNodeIds"]):
        raise ValueError("presentedContextNodeIds must contain only nonempty text")
    if "scopeParents" in payload:
        if not isinstance(payload["scopeParents"], list): raise ValueError("scopeParents must be an array")
        for item in payload["scopeParents"]:
            _exact(item, {"childNodeId", "parentNodeId", "edgeId"}, set(), "scope-parent selection")
            if any(not isinstance(value, str) or not value for value in item.values()):
                raise ValueError("scope-parent selection members must be nonempty text")
    return payload


def ndjson_loop(inp, out, err) -> int:
    app = Application()
    for line in inp:
        if not line.strip(): continue
        command = "unknown"
        try:
            request = json_loads_strict(line)
            _exact(request, {"version", "command", "payload"}, set(), "protocol request")
            command = request["command"]
            if not isinstance(request["version"], int) or isinstance(request["version"], bool) or request["version"] != 1 or not isinstance(command, str) or not command:
                raise ValueError("protocol version 1 and a text command are required")
            payload = _validate_payload(command, request["payload"])
            if command == "host.help":
                value = {"protocolVersion": 1, "framing": "One request and one result JSON object per line. Unknown fields are rejected.", "supportedProductLanguage": "English", "graphTextSupport": "Unicode text is stored without language interpretation; non-English workflows are unsupported and unvalidated.", "commands": ["host.help", "host.exit", "project.init", "project.status", "project.open", "project.verify", "project.backup", "project.export-sql", "project.diff", "project.merge", "project.bulk_plan", "artifact.check", "sample.list", "sample.create", "template.list", "template.describe", "template.export", "template.instantiate", "read.node", "read.edge", "read.nodes", "read.edges", "read.search", "read.ranked_search", "read.tag", "read.scope", "read.neighbors", "read.dependencies", "read.path", "read.context", "read.health", "read.report", "change.begin", "change.show", "change.focus", "change.apply", "change.patch", "change.expand", "change.affected", "change.omission-details", "change.preview", "change.review", "change.validate", "change.write", "change.discard", "ai.status"]}
            elif command == "host.exit":
                out.write(_json(_result(command, {"warnings": []})) + "\n"); out.flush(); return SUCCESS
            elif command == "ai.status":
                value = load_review_config().public() | {"authoring": {key: item for key, item in load_authoring_config().items() if not key.startswith("_")}}
            elif command.startswith("project."):
                if command == "project.init": value = _stored(app.initialize(payload["path"], payload["projectId"], payload["title"], payload["purposeNodeId"], payload["purposeText"]))
                elif command == "project.status": value = app.store.status(payload["path"])
                elif command == "project.open": project = app.store.load(payload["path"]); from .protocol import graph_dto; value = {"project": _stored(project), "graph": graph_dto(project.graph)}
                elif command == "project.verify": value = app.store.verify(payload["path"])
                elif command == "project.backup": value = _stored(app.store.backup(payload["sourcePath"], payload["destinationPath"]))
                elif command == "project.export-sql": value = {"sql": app.store.export_sql(payload["path"])}
                elif command == "project.diff": value = _diff(payload["basePath"], payload["targetPath"], payload.get("limit", 100), payload.get("cursor"))
                elif command == "project.merge": value = merge_projects(payload["basePath"], payload["oursPath"], payload["theirsPath"])
                elif command == "project.bulk_plan": value = plan_bulk(payload["path"], payload["manifestPath"], payload.get("chunkSize", 100), payload.get("cursor"))
                else: raise ValueError(f"unknown project command '{command}'")
            elif command == "template.list":
                value = [{"id": name, "description": descriptor(resolve(name))["description"], "version": 1} for name in ("code-development", "research-notebook")]
            elif command == "template.describe":
                value = descriptor(resolve(payload["name"]))
            elif command == "template.export":
                template = resolve(payload["name"]); Path(payload["destinationPath"]).write_text(_json(dto(template)) + "\n", encoding="utf-8", newline="\n"); value = {"path": str(Path(payload["destinationPath"]).resolve()), "template": descriptor(template)}
            elif command == "template.instantiate":
                template = resolve(payload["name"]); value = _stored(app.store.initialize(payload["path"], instantiate(template, payload["projectId"], payload["title"], payload["purposeText"])))
            elif command == "sample.list":
                value = ["technical-project"]
            elif command == "sample.create":
                if payload["sampleName"] != "technical-project": raise ValueError(f"unknown sample '{payload['sampleName']}'")
                value = _stored(app.store.initialize(payload["path"], sample_graph()))
            elif command == "artifact.check":
                value = _artifact_check(payload["path"], payload.get("nodeId"), payload.get("allowedRoots", []), payload.get("maxAnchors", 2**31 - 1), payload.get("maxSampleBytes", 4096))
            elif command.startswith("read."):
                project = app.store.load(payload["path"])
                if payload.get("expectedProjectId") is not None and payload["expectedProjectId"] != project.graph.project_id:
                    raise ValueError("project mismatch")
                query = Queries(project); name = command[5:]; limit = payload.get("limit", 100); cursor = payload.get("cursor")
                value = {"node": lambda: query.node(payload["entityId"]), "edge": lambda: query.edge(payload["entityId"]), "nodes": lambda: query.nodes(limit, cursor), "edges": lambda: query.edges(limit, cursor), "search": lambda: query.search(payload["text"], limit, cursor), "ranked_search": lambda: query.ranked_search(payload["text"], limit, cursor), "tag": lambda: query.tag(payload["tag"], limit, cursor), "scope": lambda: query.scope(payload["nodeId"], limit, cursor, payload.get("maxDepth", 2**31 - 1), payload.get("maxVisitedNodes", 2**31 - 1)), "neighbors": lambda: query.neighbors(payload["entityId"], limit, cursor), "dependencies": lambda: query.dependencies(payload["entityId"], limit, cursor), "path": lambda: query.path(payload["sourceNodeId"], payload["targetNodeId"], payload.get("maxDepth", 2**31 - 1), payload.get("maxVisitedNodes", 2**31 - 1)), "context": lambda: query.context(payload["nodeIds"], payload.get("maxDepth", 2**31 - 1), payload.get("maxVisitedNodes", 2**31 - 1)), "health": lambda: query.health(limit), "report": lambda: query.health(limit)}[name]()
            elif command == "change.begin": value = app.begin(payload["path"], payload["projectId"], payload["author"], payload["intent"]).snapshot(payload.get("includeOperations", True), payload.get("includeProposedGraph", True))
            elif command in {"change.apply", "change.patch"}:
                session = app.apply(payload["reference"], _ops(payload), command.endswith("patch"), max_traversal_depth=payload.get("maxTraversalDepth", 2**31 - 1), max_affected_nodes=payload.get("maxAffectedNodes", 2**31 - 1), max_output_items=payload.get("maxOutputItems", 2**31 - 1)); value = session.snapshot(payload.get("includeOperations", True), payload.get("includeProposedGraph", True))
            elif command == "change.expand":
                session = app.expand(payload["reference"], max_traversal_depth=payload.get("maxTraversalDepth", 2**31 - 1), max_affected_nodes=payload.get("maxAffectedNodes", 2**31 - 1), max_output_items=payload.get("maxOutputItems", 2**31 - 1)); value = session.snapshot(payload.get("includeOperations", True), payload.get("includeProposedGraph", True))
            elif command == "change.focus": value = app.focus(payload["reference"], _ops(payload), payload["scopeParents"])
            elif command == "change.show": value = app.locate(payload["session"]).snapshot(payload.get("includeOperations", True), payload.get("includeProposedGraph", True))
            elif command == "change.preview":
                value = app.session(payload["reference"]).preview(payload.get("limit", 100), payload.get("cursor"))
            elif command == "change.affected": value = app.locate(payload["session"]).affected()
            elif command == "change.omission-details": value = app.session(payload["reference"]).read_omission_details(payload["fingerprint"], payload.get("limit", 100), payload.get("cursor"))
            elif command == "change.review": value = app.review(payload["reference"], payload.get("dispositions", []), payload.get("presentedContextNodeIds", [])).snapshot(payload.get("includeOperations", True), payload.get("includeProposedGraph", True))
            elif command == "change.validate": value = app.session(payload["reference"]).snapshot(payload.get("includeOperations", True), payload.get("includeProposedGraph", True))
            elif command == "change.write":
                value = app.write(payload["reference"], payload.get("bypassAiReview", False))
            elif command == "change.discard": value = app.discard(payload["reference"])
            else: raise ValueError(f"unknown NDJSON command '{command}'")
            out.write(_json(_result(command, value)) + "\n"); out.flush()
        except Exception as exc:
            out.write(_json(_error(command, "invalid-request", str(exc))) + "\n"); out.flush()
    return SUCCESS


def main(argv: list[str] | None = None) -> int:
    return direct_command(list(sys.argv[1:] if argv is None else argv), sys.stdout, sys.stderr)
