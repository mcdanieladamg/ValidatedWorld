"""Environment-only optional OpenAI configuration.

No .env files, user-secret stores, keychains, or secret persistence are used.
"""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timezone
import hashlib
import json
import os
from time import monotonic, sleep
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.parse import quote
from urllib.request import Request, urlopen

from .protocol import json_loads_strict


def _value(name: str) -> str | None:
    value = os.environ.get(name)
    return value if value is not None and value.strip() else None


def _text(name: str, fallback: str) -> str:
    return _value(name) or fallback


def _boolean(name: str, fallback: bool) -> bool:
    value = _value(name)
    if value is None: return fallback
    if value.lower() == "true": return True
    if value.lower() == "false": return False
    raise ValueError(f"configuration '{name}' must be true or false")


def _positive(name: str, fallback: int | None, maximum: int | None = None) -> int | None:
    value = _value(name)
    if value is None: return fallback
    try: result = int(value, 10)
    except ValueError as exc: raise ValueError(f"configuration '{name}' must be a positive integer") from exc
    if result <= 0 or (maximum is not None and result > maximum): raise ValueError(f"configuration '{name}' must be a positive integer")
    return result


@dataclass(frozen=True)
class ReviewConfig:
    enabled: bool
    provider: str
    model: str
    timeout_seconds: int
    live_tests: bool
    api_key: str | None
    max_request_bytes: int | None
    max_request_items: int | None
    max_request_tokens: int | None
    poll_interval_seconds: float = 1.0

    @property
    def configured(self) -> bool:
        return bool(self.api_key) and self.provider.lower() == "openai"

    @property
    def effectively_enabled(self) -> bool:
        return self.enabled and self.configured

    def public(self) -> dict[str, Any]:
        return {"enabled": self.enabled, "configured": self.configured, "provider": self.provider, "model": self.model, "timeoutSeconds": self.timeout_seconds, "liveTests": self.live_tests, "message": "Configured independent review is enabled only when an API key is present." if self.effectively_enabled else "Independent review is unavailable; writes use the complete manual workflow.", "maxRequestBytes": self.max_request_bytes, "maxRequestItems": self.max_request_items, "maxRequestTokens": self.max_request_tokens}


def load_review_config() -> ReviewConfig:
    key = _value("VW_AIREVIEW__OPENAI__APIKEY") or _value("OPENAI_API_KEY")
    return ReviewConfig(
        enabled=_boolean("VW_AIREVIEW__ENABLED", True),
        provider=_text("VW_AIREVIEW__PROVIDER", "openai"),
        model=_text("VW_AIREVIEW__MODEL", "gpt-5.6-terra"),
        timeout_seconds=_positive("VW_AIREVIEW__TIMEOUTSECONDS", 1200) or 1200,
        live_tests=_boolean("VW_AIREVIEW__LIVETESTS", False),
        api_key=key,
        max_request_bytes=_positive("VW_AIREVIEW__MAXREQUESTBYTES", None),
        max_request_items=_positive("VW_AIREVIEW__MAXREQUESTITEMS", None),
        max_request_tokens=_positive("VW_AIREVIEW__MAXREQUESTTOKENS", None),
    )


def load_authoring_config() -> dict[str, Any]:
    key = _value("VW_AIAUTHORING__OPENAI__APIKEY") or _value("VW_AIREVIEW__OPENAI__APIKEY") or _value("OPENAI_API_KEY")
    enabled = _boolean("VW_AIAUTHORING__ENABLED", True)
    provider = _text("VW_AIAUTHORING__PROVIDER", "openai")
    return {"enabled": enabled, "configured": bool(key) and provider.lower() == "openai", "provider": provider, "model": _text("VW_AIAUTHORING__MODEL", "gpt-5.6-terra"), "timeoutSeconds": _positive("VW_AIAUTHORING__TIMEOUTSECONDS", 1200) or 1200, "liveTests": _boolean("VW_AIAUTHORING__LIVETESTS", False), "maxToolCallsPerTurn": _positive("VW_AIAUTHORING__MAXTOOLCALLSPERTURN", 32) or 32, "_apiKey": key}


def semantic_review(request_payload: dict[str, Any], config: ReviewConfig | None = None, *, transport=None) -> dict[str, Any]:
    """Perform one explicit Responses API review call.

    This function is called only for an effectively enabled configuration. It
    makes exactly one request and returns a block/inconclusive result for any
    transport, HTTP, JSON, or contract failure.
    """
    config = config or load_review_config()
    if not config.effectively_enabled: return {"status": "disabled", "decision": None, "summary": "Independent review is not configured."}
    serialized = json.dumps(request_payload, ensure_ascii=False, separators=(",", ":"), sort_keys=True)
    request_fingerprint = hashlib.sha256(serialized.encode("utf-8")).hexdigest()
    component_names = ("operations", "affectedNodes", "evidenceEdges", "contextNodes", "scopeTopologyChanges", "reviewDispositions")
    component_counts = {name: len(request_payload.get(name, ())) for name in component_names}
    manifest = request_payload.get("manifest", {})
    component_counts["omissionGroups"] = len(manifest.get("omissionGroups", ())) if isinstance(manifest, dict) else 0
    component_counts["omissionSamples"] = sum(len(item.get("sample", ())) for item in manifest.get("omissionGroups", ()) if isinstance(item, dict)) if isinstance(manifest, dict) else 0
    for name in ("currentValidation", "proposedValidation"):
        value = request_payload.get(name, {})
        component_counts[name + "Diagnostics"] = len(value.get("diagnostics", ())) if isinstance(value, dict) else 0
    item_count = sum(component_counts.values())
    schema = {
        "type": "object", "additionalProperties": False,
        "required": ["decision", "summary", "concerns"],
        "properties": {
            "decision": {"type": "string", "enum": ["allow", "block"]},
            "summary": {"type": "string", "minLength": 1},
            "concerns": {"type": "array", "items": {"type": "object", "additionalProperties": False,
                "required": ["code", "message", "citations"], "properties": {
                    "code": {"type": "string", "minLength": 1}, "message": {"type": "string", "minLength": 1},
                    "citations": {"type": "array", "minItems": 1, "items": {"type": "object", "additionalProperties": False, "required": ["entityId"], "properties": {"entityId": {"type": "string", "minLength": 1}}}},
                }}},
        },
    }
    body = {"model": config.model, "background": True, "store": True, "instructions": request_payload.get("instructions", "Independently review this exact ValidatedWorld proposal and cite only manifest.allowedCitationIds."), "input": serialized, "reasoning": {"effort": "low"}, "max_output_tokens": 2000, "tools": [], "tool_choice": "none", "text": {"format": {"type": "json_schema", "name": "validated_world_semantic_review", "strict": True, "schema": schema}}}
    outbound = json.dumps(body, separators=(",", ":"))
    byte_count = len(outbound.encode("utf-8")); token_estimate = (byte_count + 3) // 4
    exceeded = []
    if config.max_request_bytes is not None and byte_count > config.max_request_bytes: exceeded.append("bytes")
    if config.max_request_items is not None and item_count > config.max_request_items: exceeded.append("items")
    if config.max_request_tokens is not None and token_estimate > config.max_request_tokens: exceeded.append("tokens")
    measurement = {"serializedRequestBytes": byte_count, "requestItems": item_count, "estimatedTokens": token_estimate, "exceededLimits": exceeded, "componentCounts": component_counts}
    if exceeded:
        return {"status": "inconclusive", "decision": None, "summary": "The semantic review request exceeds an explicit caller budget. Split or remodel the proposal before retrying.", "failureCode": "request-budget-exceeded", "measurement": measurement}
    headers = {"Authorization": "Bearer " + config.api_key, "Content-Type": "application/json"}
    request = Request("https://api.openai.com/v1/responses", data=outbound.encode("utf-8"), headers=headers, method="POST")
    started = monotonic()
    try:
        deadline = started + config.timeout_seconds
        parsed = _review_transport(request, config.timeout_seconds, transport)
        response_id = parsed.get("id")
        status = parsed.get("status")
        if not isinstance(response_id, str) or not response_id or not isinstance(status, str) or not status:
            raise ValueError("review response is missing its ID or status")
        if config.poll_interval_seconds < 0:
            raise ValueError("review poll interval must be nonnegative")
        while status in {"queued", "in_progress"}:
            remaining = deadline - monotonic()
            if remaining <= 0: raise TimeoutError("review polling exceeded its deadline")
            if config.poll_interval_seconds: sleep(min(config.poll_interval_seconds, remaining))
            poll = Request("https://api.openai.com/v1/responses/" + quote(response_id, safe=""), headers=headers, method="GET")
            parsed = _review_transport(poll, max(0.001, deadline - monotonic()), transport)
            if parsed.get("id") != response_id: raise ValueError("review poll response does not match the created response")
            status = parsed.get("status")
            if not isinstance(status, str) or not status: raise ValueError("review poll response is missing its status")
        if status != "completed": raise ValueError(f"review response ended with status '{status}'")
        text = _response_text(parsed)
        decision = json_loads_strict(text)
        concerns = _validate_review_output(decision, _allowed_citations(request_payload))
        usage = parsed.get("usage")
        if usage is not None:
            if not isinstance(usage, dict) or any(not isinstance(usage.get(name), int) or isinstance(usage.get(name), bool) or usage[name] < 0 for name in ("input_tokens", "output_tokens", "total_tokens")):
                raise ValueError("review response usage is malformed")
        return {"status": "complete", "decision": decision["decision"], "provider": config.provider, "model": config.model, "requestFingerprint": request_fingerprint, "binding": request_payload.get("binding"), "summary": decision["summary"], "concerns": concerns, "durationSeconds": monotonic() - started, "usage": usage, "responseId": response_id, "completedUtc": datetime.now(timezone.utc).isoformat(), "isCurrent": True, "measurement": measurement}
    except (HTTPError, URLError, TimeoutError, OSError, TypeError, UnicodeDecodeError, json.JSONDecodeError, ValueError) as exc:
        return {"status": "inconclusive", "decision": None, "summary": f"Independent review failed: {type(exc).__name__}.", "failureCode": "provider-failure"}


def _review_transport(request: Request, timeout: float, transport) -> dict[str, Any]:
    if transport is None:
        with urlopen(request, timeout=timeout) as response: raw = response.read()
    else:
        raw = transport(request, timeout)
    value = raw if isinstance(raw, dict) else json_loads_strict(raw.decode("utf-8") if isinstance(raw, bytes) else raw)
    if not isinstance(value, dict): raise ValueError("review response must be an object")
    return value


def _response_text(value: dict[str, Any]) -> str:
    output = value.get("output", [])
    if not isinstance(output, list): raise ValueError("review output must be an array")
    for item in output:
        if not isinstance(item, dict): raise ValueError("review output item must be an object")
        content_items = item.get("content", [])
        if not isinstance(content_items, list): raise ValueError("review content must be an array")
        for content in content_items:
            if not isinstance(content, dict): raise ValueError("review content item must be an object")
            if content.get("type") == "refusal":
                raise ValueError("review response was refused")
            if content.get("type") in {"output_text", "text"} and isinstance(content.get("text"), str): return content["text"]
    raise ValueError("review response did not contain output text")


def _allowed_citations(payload: dict[str, Any]) -> set[str]:
    manifest = payload.get("manifest")
    if isinstance(manifest, dict) and isinstance(manifest.get("allowedCitationIds"), list):
        return {item for item in manifest["allowedCitationIds"] if isinstance(item, str) and item}
    values: set[str] = set()
    for operation in payload.get("operations", ()):
        if isinstance(operation, dict):
            operation_value = operation.get("operation", operation)
            if isinstance(operation_value, dict): values.add(str(operation_value.get("entityId", "")))
    for item in payload.get("affectedNodes", payload.get("affected", ())):
        if isinstance(item, dict): values.add(str(item.get("nodeId", "")))
    for item in payload.get("contextNodes", payload.get("scopeContext", ())):
        if isinstance(item, dict): values.add(str(item.get("nodeId", "")))
    return {item for item in values if item}


def _validate_review_output(value: Any, allowed: set[str]) -> list[dict[str, Any]]:
    if not isinstance(value, dict) or set(value) != {"decision", "summary", "concerns"}:
        raise ValueError("review response has an invalid object shape")
    if value["decision"] not in {"allow", "block"} or not isinstance(value["summary"], str) or not value["summary"].strip() or not isinstance(value["concerns"], list):
        raise ValueError("review response fields are invalid")
    normalized = []
    for concern in value["concerns"]:
        if not isinstance(concern, dict) or set(concern) != {"code", "message", "citations"}:
            raise ValueError("review concern has an invalid object shape")
        if not isinstance(concern["code"], str) or not concern["code"].strip() or not isinstance(concern["message"], str) or not concern["message"].strip() or not isinstance(concern["citations"], list) or not concern["citations"]:
            raise ValueError("review concern fields are invalid")
        if any(not isinstance(item, dict) or set(item) != {"entityId"} or not isinstance(item["entityId"], str) or item["entityId"] not in allowed for item in concern["citations"]):
            raise ValueError("review response cites an ID that was not supplied")
        normalized.append({"code": concern["code"], "message": concern["message"], "citations": sorted({item["entityId"] for item in concern["citations"]})})
    if value["decision"] == "allow" and value["concerns"]:
        raise ValueError("allow decisions cannot contain concerns")
    if value["decision"] == "block" and not value["concerns"]:
        raise ValueError("block decisions require at least one concern")
    return normalized
