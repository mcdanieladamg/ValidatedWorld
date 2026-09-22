"""Resumable, read-only planning for strict JSONL graph imports."""

from __future__ import annotations

import base64
import hashlib
import json
from pathlib import Path
from typing import Any

from .canonical import state_fingerprint
from .protocol import json_loads_strict, operation_dto, operation_from_dto
from .storage import ProjectStore
from .validation import project_graph, validate_graph


FORMAT = "validated-world-bulk-manifest"
VERSION = 1


def plan_bulk(path: str, manifest_path: str, chunk_size: int = 100, cursor: str | None = None) -> dict[str, Any]:
    if chunk_size < 1: raise ValueError("bulk chunk size must be positive")
    project = ProjectStore().load(path); manifest = Path(manifest_path).expanduser().resolve()
    if not manifest.is_file(): raise ValueError(f"bulk manifest '{manifest}' does not exist")
    stream = manifest.open("r", encoding="utf-8", newline="")
    try: header_line = stream.readline()
    except UnicodeDecodeError as exc: stream.close(); raise ValueError(f"bulk manifest header is invalid: {exc}") from exc
    if not header_line or not header_line.strip(): stream.close(); raise ValueError("a bulk manifest must start with one header record")
    try: header = json_loads_strict(header_line)
    except (json.JSONDecodeError, ValueError) as exc: stream.close(); raise ValueError(f"bulk manifest header is invalid: {exc}") from exc
    expected_header = {"version", "format", "projectId", "baseFingerprint", "intent"}
    if not isinstance(header, dict) or set(header) != expected_header: stream.close(); raise ValueError("bulk manifest header has an invalid object shape")
    if header.get("version") != VERSION or header.get("format") != FORMAT: raise ValueError(f"unsupported bulk manifest format; expected {FORMAT} version {VERSION}")
    if header.get("projectId") != project.graph.project_id: raise ValueError("bulk manifest project ID does not match the selected project")
    if header.get("baseFingerprint") != project.state_fingerprint: raise ValueError("bulk manifest base fingerprint does not match the selected project")
    if not isinstance(header.get("intent"), str) or not header["intent"].strip() or any(ord(ch) < 32 for ch in header["intent"]): raise ValueError("bulk manifest intent must be nonempty and contain no control characters")
    operations = []
    seen: set[str] = set()
    normalized = [json.dumps(header, ensure_ascii=False, separators=(",", ":"), sort_keys=True).encode("utf-8")]
    try:
        for number, line in enumerate(stream, 2):
            if not line.strip(): raise ValueError(f"bulk manifest operation line {number} is empty")
            try: dto = json_loads_strict(line); operation = operation_from_dto(dto)
            except (json.JSONDecodeError, KeyError, TypeError, ValueError) as exc: raise ValueError(f"bulk manifest operation line {number} is invalid: {exc}") from exc
            if operation.entity_id in seen: raise ValueError(f"bulk manifest contains more than one operation for entity '{operation.entity_id}'")
            seen.add(operation.entity_id); operations.append(operation)
            normalized.append(json.dumps(operation_dto(operation), ensure_ascii=False, separators=(",", ":"), sort_keys=True).encode("utf-8"))
    finally:
        stream.close()
    if not operations: raise ValueError("bulk manifest must contain at least one operation")
    digest = hashlib.sha256()
    for item in normalized: digest.update(len(item).to_bytes(4, "little", signed=True)); digest.update(item)
    manifest_fingerprint = digest.hexdigest()
    start = 0
    if cursor:
        try:
            raw = base64.urlsafe_b64decode(cursor + "=" * (-len(cursor) % 4)); token = json_loads_strict(raw.decode("utf-8"))
            if not isinstance(token, dict) or set(token) != {"version", "manifestFingerprint", "projectId", "baseFingerprint", "chunkSize", "nextOperationIndex"}: raise ValueError
            if token["version"] != VERSION or token["manifestFingerprint"] != manifest_fingerprint or token["projectId"] != project.graph.project_id or token["baseFingerprint"] != project.state_fingerprint or token["chunkSize"] != chunk_size: raise ValueError
            start = int(token["nextOperationIndex"])
        except (ValueError, KeyError, TypeError, json.JSONDecodeError, UnicodeDecodeError) as exc: raise ValueError("bulk continuation cursor is malformed or stale; start a new plan") from exc
    if start < 0 or start % chunk_size != 0 or start >= len(operations): raise ValueError("bulk continuation cursor has no remaining operations")
    current = project.graph
    for offset in range(0, len(operations), chunk_size):
        current, _ = project_graph(current, operations[offset:offset + chunk_size])
        validation = validate_graph(current)
        if not validation.is_valid: raise ValueError(f"bulk manifest chunk {offset // chunk_size} does not form a valid graph checkpoint: {validation.diagnostics[0].message}")
    selected = operations[start:start + chunk_size]; next_start = start + len(selected)
    next_cursor = None
    if next_start < len(operations):
        token = {"version": VERSION, "manifestFingerprint": manifest_fingerprint, "projectId": project.graph.project_id, "baseFingerprint": project.state_fingerprint, "chunkSize": chunk_size, "nextOperationIndex": next_start}
        next_cursor = base64.urlsafe_b64encode(json.dumps(token, separators=(",", ":"), sort_keys=True).encode()).decode().rstrip("=")
    return {"manifestPath": str(manifest), "projectId": project.graph.project_id, "baseFingerprint": project.state_fingerprint, "manifestFingerprint": manifest_fingerprint, "intent": header["intent"], "operationCount": len(operations), "chunkSize": chunk_size, "chunkIndex": start // chunk_size, "operationStart": start, "chunkOperationCount": len(selected), "chunkCount": (len(operations) + chunk_size - 1) // chunk_size, "operations": {"operations": [operation_dto(item) for item in selected]}, "nextCursor": next_cursor}
