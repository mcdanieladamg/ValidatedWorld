"""Read-only, explicitly authorized external artifact verification."""

from __future__ import annotations

import base64
import hashlib
import os
from pathlib import Path
import stat
import sys
from typing import Any, Protocol

from .models import Graph, GraphValueKind, Node, ordinal_key


CONTRACT_VERSION = 1
FILESYSTEM_ADAPTER = "filesystem"


class ArtifactChecker(Protocol):
    adapter_id: str
    contract_version: int

    def check(self, request: dict[str, Any]) -> dict[str, Any]: ...


def _result(node_id: str, path: str | None, resolved: str | None, adapter: str | None,
            version: int | None, status: str, message: str, expected: str | None,
            actual: str | None = None, sample: str | None = None, truncated: bool = False) -> dict[str, Any]:
    return {
        "nodeId": node_id, "path": path, "resolvedPath": resolved,
        "adapterId": adapter, "adapterVersion": version, "status": status,
        "message": message, "expectedSha256": expected, "actualSha256": actual,
        "contentSampleBase64": sample, "contentSampleTruncated": truncated,
    }


def _anchor(node: Node) -> tuple[dict[str, Any] | None, str | None]:
    path = node.attribute("artifact.path")
    digest = node.attribute("artifact.sha256")
    if path is None or path.kind is not GraphValueKind.TEXT or not path.value.strip():
        return None, "Missing text attribute 'artifact.path'."
    if digest is None or digest.kind is not GraphValueKind.TEXT or len(digest.value) != 64 or any(ch not in "0123456789abcdefABCDEF" for ch in digest.value):
        return None, "Missing or invalid SHA-256 attribute 'artifact.sha256'."
    adapter = node.attribute("artifact.adapter")
    adapter_id = FILESYSTEM_ADAPTER
    if adapter is not None:
        if adapter.kind not in (GraphValueKind.TEXT, GraphValueKind.SYMBOL) or not str(adapter.value).strip():
            return None, "Attribute 'artifact.adapter' must be non-empty text or a symbol."
        adapter_id = str(adapter.value)
    version = node.attribute("artifact.adapter-version")
    adapter_version = CONTRACT_VERSION
    if version is not None:
        if version.kind is not GraphValueKind.INTEGER or version.value <= 0 or version.value > 2**31 - 1:
            return None, "Attribute 'artifact.adapter-version' must be a positive integer."
        adapter_version = version.value
    return {"nodeId": node.id, "path": path.value, "sha256": digest.value.lower(), "adapterId": adapter_id, "adapterVersion": adapter_version}, None


def _contains(root: str, candidate: str) -> bool:
    try:
        normalized_root = os.path.normcase(os.path.abspath(root))
        normalized_candidate = os.path.normcase(os.path.abspath(candidate))
        return os.path.commonpath((normalized_root, normalized_candidate)) == normalized_root
    except (OSError, ValueError):
        return False


def _windows_final_path(handle: int) -> str:
    import ctypes

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    function = kernel32.GetFinalPathNameByHandleW
    function.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p, ctypes.c_uint32, ctypes.c_uint32]
    function.restype = ctypes.c_uint32
    capacity = 512
    while True:
        buffer = ctypes.create_unicode_buffer(capacity)
        length = function(handle, buffer, capacity, 0)
        if length == 0:
            error = ctypes.get_last_error()
            raise OSError(error, "the opened path could not be resolved")
        if length < capacity:
            value = buffer.value
            if value.startswith("\\\\?\\UNC\\"):
                value = "\\\\" + value[8:]
            elif value.startswith("\\\\?\\"):
                value = value[4:]
            return os.path.abspath(value)
        capacity = length + 1


def _opened_path(file_descriptor: int, fallback: str) -> str:
    if os.name == "nt":
        import msvcrt

        return _windows_final_path(msvcrt.get_osfhandle(file_descriptor))
    if sys.platform == "darwin":
        import fcntl
        raw = fcntl.fcntl(file_descriptor, 50, bytes(1024))
        return os.path.abspath(os.fsdecode(raw.split(b"\0", 1)[0]))
    descriptor_link = f"/proc/self/fd/{file_descriptor}"
    if os.path.exists(descriptor_link):
        return os.path.abspath(os.readlink(descriptor_link))
    return str(Path(fallback).resolve(strict=True))


def _opened_directory_path(path: str) -> str:
    if os.name == "nt":
        import ctypes

        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        create_file = kernel32.CreateFileW
        create_file.argtypes = [
            ctypes.c_wchar_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p,
            ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p,
        ]
        create_file.restype = ctypes.c_void_p
        handle = create_file(path, 0, 0x7, None, 3, 0x02000000, None)
        invalid_handle = ctypes.c_void_p(-1).value
        if handle is None or handle == invalid_handle:
            error = ctypes.get_last_error()
            raise OSError(error, f"the artifact allowed root could not be opened: '{path}'")
        try:
            return _windows_final_path(handle)
        finally:
            close_handle = kernel32.CloseHandle
            close_handle.argtypes = [ctypes.c_void_p]
            close_handle.restype = ctypes.c_int
            close_handle(handle)

    descriptor = os.open(path, os.O_RDONLY | getattr(os, "O_DIRECTORY", 0))
    try:
        return _opened_path(descriptor, path)
    finally:
        os.close(descriptor)


class FileSystemArtifactChecker:
    adapter_id = FILESYSTEM_ADAPTER
    contract_version = CONTRACT_VERSION

    def check(self, request: dict[str, Any]) -> dict[str, Any]:
        anchor = request["anchor"]
        project_path = request["projectPath"]
        try:
            raw = anchor["path"]
            if not isinstance(raw, str) or "\0" in raw or any(ord(ch) < 32 for ch in raw):
                raise ValueError("artifact paths must be text free of control characters")
            candidate = os.path.abspath(raw if os.path.isabs(raw) else os.path.join(os.path.dirname(os.path.abspath(project_path)), raw))
        except (OSError, ValueError) as exc:
            return _result(anchor["nodeId"], anchor.get("path"), None, self.adapter_id,
                           self.contract_version, "invalidAnchor", f"The artifact path is invalid: {exc}", anchor["sha256"])

        declared_roots: list[str] = []
        opened_roots: list[str] = []
        for root in request["allowedRoots"]:
            if not isinstance(root, str) or not root.strip() or "\0" in root or any(ord(ch) < 32 for ch in root):
                raise ValueError("artifact allowed roots must be non-empty and free of control characters")
            declared = os.path.abspath(root)
            if not os.path.isdir(declared):
                raise FileNotFoundError(f"artifact allowed root does not exist or is not a directory: '{declared}'")
            declared_roots.append(declared)
            opened_roots.append(_opened_directory_path(declared))
        if not any(_contains(root, candidate) for root in declared_roots):
            return _result(anchor["nodeId"], anchor["path"], candidate, self.adapter_id,
                           self.contract_version, "unauthorized", "The artifact path is outside every host-authorized root.", anchor["sha256"])

        descriptor: int | None = None
        try:
            descriptor = os.open(candidate, os.O_RDONLY | getattr(os, "O_BINARY", 0))
            opened = _opened_path(descriptor, candidate)
            if not any(_contains(root, opened) for root in opened_roots):
                os.close(descriptor); descriptor = None
                return _result(anchor["nodeId"], anchor["path"], opened, self.adapter_id,
                               self.contract_version, "unauthorized", "The opened artifact resolves outside every host-authorized root (for example through a link or path replacement).", anchor["sha256"])
            if not stat.S_ISREG(os.fstat(descriptor).st_mode):
                raise OSError("the artifact path is not a regular file")
            digest = hashlib.sha256()
            sample = bytearray()
            total = 0
            with os.fdopen(descriptor, "rb", closefd=True) as stream:
                descriptor = None
                while True:
                    block = stream.read(64 * 1024)
                    if not block:
                        break
                    digest.update(block)
                    if len(sample) < request["maxSampleBytes"]:
                        sample.extend(block[: request["maxSampleBytes"] - len(sample)])
                    total += len(block)
            actual = digest.hexdigest()
            matched = actual == anchor["sha256"]
            return _result(anchor["nodeId"], anchor["path"], opened, self.adapter_id,
                           self.contract_version, "matched" if matched else "drifted",
                           "The artifact bytes match the anchored SHA-256." if matched else "The artifact bytes differ from the anchored SHA-256.",
                           anchor["sha256"], actual, base64.b64encode(sample).decode(), total > request["maxSampleBytes"])
        except FileNotFoundError:
            return _result(anchor["nodeId"], anchor["path"], candidate, self.adapter_id,
                           self.contract_version, "missing", "The anchored artifact file does not exist.", anchor["sha256"])
        except (OSError, PermissionError, NotImplementedError) as exc:
            return _result(anchor["nodeId"], anchor["path"], candidate, self.adapter_id,
                           self.contract_version, "unreadable", f"The anchored artifact could not be read: {exc}", anchor["sha256"])
        finally:
            if descriptor is not None:
                os.close(descriptor)


def check_artifacts(project_path: str, graph: Graph, node_id: str | None = None, *,
                    allowed_roots: list[str] | tuple[str, ...] = (), max_anchors: int = 2**31 - 1,
                    max_sample_bytes: int = 4096, checkers: list[ArtifactChecker] | None = None) -> dict[str, Any]:
    if max_anchors < 1 or max_sample_bytes < 1:
        raise ValueError("artifact bounds must be positive")
    configured = list([FileSystemArtifactChecker()] if checkers is None else checkers)
    if not configured or any(item.contract_version != CONTRACT_VERSION for item in configured):
        raise ValueError(f"artifact checkers must implement contract version {CONTRACT_VERSION}")
    adapters = {item.adapter_id: item for item in configured}
    if len(adapters) != len(configured):
        raise ValueError("artifact checker adapter identifiers must be unique")
    candidates = [
        node for node in graph.nodes
        if ("artifact" in node.tags or node.kind == "external-anchor") and (node_id is None or node.id == node_id)
    ]
    candidates.sort(key=lambda item: ordinal_key(item.id))
    items: list[dict[str, Any]] = []
    for node in candidates[:max_anchors]:
        anchor, error = _anchor(node)
        if anchor is None:
            items.append(_result(node.id, None, None, None, None, "invalidAnchor", error or "Invalid artifact anchor.", None))
            continue
        checker = adapters.get(anchor["adapterId"])
        if checker is None or checker.contract_version != anchor["adapterVersion"]:
            items.append(_result(node.id, anchor["path"], None, anchor["adapterId"], anchor["adapterVersion"],
                                 "unsupportedAdapter", f"No adapter named '{anchor['adapterId']}' supports contract version {anchor['adapterVersion']}.", anchor["sha256"]))
            continue
        items.append(checker.check({"projectPath": project_path, "anchor": anchor,
                                    "maxSampleBytes": max_sample_bytes, "allowedRoots": list(allowed_roots)}))
    names = ("matched", "drifted", "missing", "invalidAnchor", "unsupportedAdapter", "unauthorized", "unreadable")
    counts = {name: sum(item["status"] == name for item in items) for name in names}
    complete = len(items) == len(candidates)
    return {
        "projectPath": project_path, "totalAnchorCount": len(candidates), "items": items,
        **{name + "Count": counts[name] for name in names}, "isComplete": complete,
        "omissionMessage": None if complete else f"Only the first {max_anchors} of {len(candidates)} artifact anchors were checked.",
    }
