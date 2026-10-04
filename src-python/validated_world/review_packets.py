"""Process-local packet review services; serialization and files stay outside Core."""

from copy import deepcopy
from pathlib import Path
import hashlib
import json
import shutil

from .planning import ALGORITHM, partition, refine
from .protocol import node_dto, edge_dto
from .queries import _page
from .validation import GraphIndex


def fingerprint(value):
    return hashlib.sha256(json.dumps(value, ensure_ascii=True, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def entity(item):
    kind = item["kind"]
    if kind == "operation":
        value = item["operation"]
        return value["entityKind"], value["entityId"]
    if kind == "edgeChange":
        return "edge", item[kind]["operation"]["entityId"]
    if kind in {"affectedNode", "scopeContext", "disposition"}:
        return "node", item[kind]["nodeId"]
    return "global", ""


class PacketReview:
    def __init__(self, session):
        self.reference = deepcopy(session.reference())
        self.evidence = deepcopy(session.review_items())
        self.supplemental = deepcopy(session.supplemental)
        self.ownership = partition(session.base.graph, session.proposed,
                                   [(i["ordinal"], *entity(i)) for i in self.evidence])
        self.results = {}
        self.seen = {}
        self.cached_items = {}
        self.before, self.after = GraphIndex(session.base.graph), GraphIndex(session.proposed)
        self.intent = session.intent
        self.rebind()

    def rebind(self):
        flat = [v for values in self.ownership.values() for v in values]
        if sorted(flat) != list(range(len(self.evidence))):
            raise ValueError("plan must own every review ordinal exactly once")
        self.plan_fingerprint = fingerprint({"algorithm": ALGORITHM, "reference": self.reference,
                                             "evidence": self.evidence, "supplemental": self.supplemental,
                                             "ownership": self.ownership})
        self.results.clear()
        self.seen.clear()
        self.cached_items.clear()

    def refine(self, refinements):
        self.ownership = refine(self.ownership, refinements)
        self.rebind()

    def check(self, plan_fingerprint):
        if plan_fingerprint != self.plan_fingerprint:
            raise ValueError("stale planFingerprint")

    def manifest(self, limit, cursor):
        items = [{"packetId": packet_id, "role": "synthesis" if packet_id == "synthesis" else "branch",
                  "ownedOrdinal": ordinal, "kind": self.evidence[ordinal]["kind"],
                  "entityKind": entity(self.evidence[ordinal])[0], "entityId": entity(self.evidence[ordinal])[1]}
                 for packet_id in sorted(self.ownership) for ordinal in self.ownership[packet_id]]
        return {"reference": self.reference, "algorithm": ALGORITHM, "planFingerprint": self.plan_fingerprint,
                "packetCount": len(self.ownership), "synthesisPacketId": "synthesis", "coverageCount": len(self.evidence),
                "supplementalFingerprint": fingerprint(self.supplemental),
                **_page(items, fingerprint([self.plan_fingerprint, "manifest", limit]), limit, cursor)}

    def packet_items(self, packet_id):
        if packet_id not in self.ownership:
            raise ValueError("unknown packetId")
        if packet_id in self.cached_items:
            return self.cached_items[packet_id]
        owned = set(self.ownership[packet_id])
        items = [{"kind": "ownedEvidence", "evidence": self.evidence[v]} for v in sorted(owned)]
        nodes = set()
        edges = set()
        for ordinal in owned:
            kind, eid = entity(self.evidence[ordinal])
            if kind == "node": nodes.add(eid)
            if kind == "edge": edges.add(eid)
        if packet_id != "synthesis":
            # Repeat root operations and global diagnostics explicitly; ownership stays unique.
            for ordinal in self.ownership["synthesis"]:
                if self.evidence[ordinal]["kind"] in {"operation", "currentRuleDiagnostic", "proposedRuleDiagnostic", "currentValidationDiagnostic", "proposedValidationDiagnostic"}:
                    items.append({"kind": "sharedEvidence", "evidence": self.evidence[ordinal]})
                    kind, eid = entity(self.evidence[ordinal])
                    if kind == "node": nodes.add(eid)
        for index in (self.before, self.after):
            for node_id in list(nodes):
                if node_id in index.nodes_by_id: nodes.update(index.upstream(node_id))
            for edge in index.graph.edges:
                if edge.source in nodes or edge.target in nodes or edge.id in edges:
                    # Scope context must never fan out through siblings.
                    if edge.relationship != "scope-parent" or edge.source in nodes:
                        edges.add(edge.id)
        if packet_id == "synthesis":
            node_owners = {}
            for pid, ordinals in self.ownership.items():
                for ordinal in ordinals:
                    kind, eid = entity(self.evidence[ordinal])
                    if kind == "node": node_owners.setdefault(eid, set()).add(pid)
            for index in (self.before, self.after):
                for edge in index.graph.edges:
                    left, right = node_owners.get(edge.source, set()), node_owners.get(edge.target, set())
                    if edge.relationship != "scope-parent" and left and right and left != right:
                        edges.add(edge.id)
            items.extend({"kind": "branchResult", "packetId": pid, "result": result}
                         for pid, result in sorted(self.results.items()) if pid != "synthesis")
        for edge_id in sorted(edges):
            old, new = self.before.edges_by_id.get(edge_id), self.after.edges_by_id.get(edge_id)
            items.append({"kind": "dependency", "entityId": edge_id,
                          "currentEdge": edge_dto(old) if old else None, "proposedEdge": edge_dto(new) if new else None})
            for edge in (old, new):
                if edge: nodes.update((edge.source, edge.target))
        # Endpoint lineage is context too, including moved and removed endpoints.
        for index in (self.before, self.after):
            for nid in list(nodes):
                if nid in index.nodes_by_id: nodes.update(index.upstream(nid))
        for nid in sorted(nodes):
            old, new = self.before.nodes_by_id.get(nid), self.after.nodes_by_id.get(nid)
            items.append({"kind": "sharedNode", "entityId": nid,
                          "currentNode": node_dto(old) if old else None, "proposedNode": node_dto(new) if new else None})
        items.extend({"kind": "supplementalEvidence", **value} for value in self.supplemental)
        self.cached_items[packet_id] = items
        return items

    def packet(self, packet_id, limit, cursor):
        if packet_id == "synthesis" and not self.branches_allowed():
            raise ValueError("synthesis requires all branch packets to allow")
        items = self.packet_items(packet_id)
        packet_fingerprint = fingerprint([self.plan_fingerprint, packet_id, items])
        page = _page(items, fingerprint([packet_fingerprint, limit]), limit, cursor)
        # Count presentation by exact packet content, independently of page size.
        start = 0 if cursor is None else int(__import__("base64").b64decode(cursor).decode().rsplit(":", 1)[1])
        self.seen.setdefault(packet_fingerprint, set()).update(range(start, start + len(page["items"])))
        return {"binding": {"reference": self.reference, "planFingerprint": self.plan_fingerprint,
                            "packetId": packet_id, "packetFingerprint": packet_fingerprint},
                "intent": self.intent, "role": "synthesis" if packet_id == "synthesis" else "branch",
                "allEvidencePresented": len(self.seen[packet_fingerprint]) == len(items), **deepcopy(page)}

    def branches_allowed(self):
        return all(self.results.get(pid, {}).get("decision") == "allow" for pid in self.ownership if pid != "synthesis")

    def complete(self):
        return self.branches_allowed() and self.results.get("synthesis", {}).get("decision") == "allow"

    def record(self, binding, result):
        if not isinstance(binding, dict) or set(binding) != {"reference", "planFingerprint", "packetId", "packetFingerprint"}:
            raise ValueError("packet result binding has an invalid shape")
        self.check(binding["planFingerprint"])
        pid = binding["packetId"]
        if binding["reference"] != self.reference:
            raise ValueError("stale packet reference")
        items = self.packet_items(pid)
        packet_fp = fingerprint([self.plan_fingerprint, pid, items])
        if binding["packetFingerprint"] != packet_fp:
            raise ValueError("stale packetFingerprint")
        if pid == "synthesis" and not self.branches_allowed():
            raise ValueError("synthesis requires all branch packets to allow")
        if len(self.seen.get(packet_fp, set())) != len(items):
            raise ValueError("every exact packet page must be presented before a result")
        validate_result(result, items)
        previous = self.results.get(pid)
        if previous and previous["decision"] in {"allow", "block"} and previous != result:
            raise ValueError("terminal packet result is immutable; revise or replan before another review")
        self.results[pid] = deepcopy(result)
        if pid != "synthesis":
            self.results.pop("synthesis", None)
            self.cached_items.pop("synthesis", None)


def validate_result(result, items):
    fields = {"decision", "summary", "citations", "concerns", "questions"}
    if not isinstance(result, dict) or set(result) != fields:
        raise ValueError("packet result requires decision, summary, citations, concerns and questions")
    if result["decision"] not in {"allow", "block", "needs-context"} or not isinstance(result["summary"], str) or not result["summary"].strip():
        raise ValueError("invalid packet decision or summary")
    allowed = set()
    def collect(value):
        if isinstance(value, dict):
            for key, v in value.items():
                if key in {"entityId", "nodeId", "id", "ruleId"} and isinstance(v, str): allowed.add(v)
                else: collect(v)
        elif isinstance(value, list):
            for v in value: collect(v)
    collect(items)
    def citations(value):
        if not isinstance(value, list) or not value or any(not isinstance(v, dict) or set(v) != {"entityId"} or not isinstance(v["entityId"], str) or v["entityId"] not in allowed for v in value):
            raise ValueError("citations must identify exact packet evidence")
    citations(result["citations"])
    if not isinstance(result["concerns"], list) or not isinstance(result["questions"], list):
        raise ValueError("concerns and questions must be arrays")
    for concern in result["concerns"]:
        if not isinstance(concern, dict) or set(concern) != {"code", "message", "citations"} or any(not isinstance(concern[k], str) or not concern[k].strip() for k in ("code", "message")):
            raise ValueError("concern requires code, message and citations")
        citations(concern["citations"])
    if any(not isinstance(q, str) or not q.strip() for q in result["questions"]):
        raise ValueError("questions must contain nonempty text")
    if result["decision"] == "allow" and (result["concerns"] or result["questions"]):
        raise ValueError("allow cannot carry unresolved concerns or questions")
    if result["decision"] == "block" and not result["concerns"]:
        raise ValueError("block requires cited concerns")
    if result["decision"] == "needs-context" and not result["questions"]:
        raise ValueError("needs-context requires explicit questions")


def export_packet(review, packet_id, destination, limit):
    """Create an optional evidence directory exclusively; remove partial output on failure."""
    if ".." in Path(destination).parts:
        raise ValueError("review export path cannot contain parent traversal")
    target = Path(destination).absolute()
    for path in (target, *target.parents):
        if path.is_symlink() or path.is_junction():
            raise ValueError("review export path must not traverse a link or junction")
    if not target.parent.is_dir():
        raise ValueError("review export requires an existing parent directory")
    # Validate before creating output. File names never use graph IDs or supplied packet IDs.
    saved_seen = deepcopy(review.seen)
    created = False
    try:
        first = review.packet(packet_id, limit, None)
        target.mkdir(exist_ok=False)
        created = True
        pages = []; page = first; number = 0
        while True:
            name = f"page-{number:06d}.json"
            raw = (json.dumps(page, ensure_ascii=True, sort_keys=True) + "\n").encode()
            with (target / name).open("xb") as stream: stream.write(raw)
            pages.append({"path": str(target / name), "sha256": hashlib.sha256(raw).hexdigest()})
            if page["nextCursor"] is None: break
            number += 1; page = review.packet(packet_id, limit, page["nextCursor"])
        manifest = {"binding": first["binding"], "pages": pages, "role": first["role"], "intent": review.intent}
        with (target / "manifest.json").open("x", encoding="utf-8") as stream:
            json.dump(manifest, stream, ensure_ascii=True)
        return {"binding": first["binding"], "manifestPath": str(target / "manifest.json"), "pageCount": len(pages)}
    except BaseException:
        review.seen = saved_seen
        if created: shutil.rmtree(target)
        raise
