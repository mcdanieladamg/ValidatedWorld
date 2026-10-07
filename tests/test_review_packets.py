import copy
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).parents[1] / "src"))
from validated_world.application import Application, sample_graph
from validated_world.cli import _validate_payload, ndjson_loop
from validated_world.models import EntityKind, Node, Operation, OperationKind
from validated_world.protocol import operation_from_dto
from validated_world.storage import ProjectStore


class PacketReviewTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup)
        # macOS temporary paths can include /var -> /private/var. Exports
        # deliberately reject links, so use the fixture's physical directory.
        self.root = Path(self.temp.name).resolve(strict=True)
        self.path = self.root / "project.vw.db"
        ProjectStore().initialize(self.path, sample_graph())
        self.app = Application(); self.addCleanup(self.app.close)
        self.session = self.app.begin(str(self.path), "technical-project", "tester", "Review complete changed purpose")
        self.session = self.app.apply(self.session.reference(), (Operation(OperationKind.REPLACE, EntityKind.NODE, "purpose", node=Node("purpose", "An offline privacy-preserving sensor with improved documentation")),))
        self.app.review(self.session.reference(), [{"nodeId": i["nodeId"], "kind": "updated" if i["isDirectChange"] else "reviewedNoChange"} for i in self.session.affected_nodes], [i["nodeId"] for i in self.session.scope_context])
        self.plan = self.app.review_plan(self.session.reference(), 2)
        self.fp = self.plan["planFingerprint"]

    def packet(self, pid, limit=3):
        page = self.app.review_packet(self.session.reference(), self.fp, pid, limit)
        items = list(page["items"])
        while page["nextCursor"]:
            page = self.app.review_packet(self.session.reference(), self.fp, pid, limit, page["nextCursor"])
            items.extend(page["items"])
        self.assertTrue(page["allEvidencePresented"])
        return page["binding"], items

    def result(self, binding, decision="allow"):
        return {"decision": decision, "summary": "Reviewed exact root, consequences and context.",
                "citations": [{"entityId": "purpose"}], "concerns": [] if decision != "block" else [{"code": "conflict", "message": "The root conflicts with this branch.", "citations": [{"entityId": "purpose"}]}],
                "questions": ["What evidence supports the root?"] if decision == "needs-context" else []}

    def allow_branches(self):
        for pid in self.session.packet_review.ownership:
            if pid != "synthesis":
                binding, _ = self.packet(pid)
                self.app.review_result(self.session.reference(), binding, self.result(binding))

    def test_unique_complete_coverage_paging_and_determinism(self):
        rows = list(self.plan["items"]); page = self.plan
        while page["nextCursor"]:
            page = self.app.review_plan(self.session.reference(), 2, page["nextCursor"])
            rows.extend(page["items"])
        self.assertEqual(sorted(r["ownedOrdinal"] for r in rows), list(range(len(self.session.review_items()))))
        self.assertEqual(self.app.review_plan(self.session.reference(), 2)["planFingerprint"], self.fp)
        with self.assertRaises(ValueError): self.app.review_plan(self.session.reference(), 1, self.plan["nextCursor"])
        pid = next(p for p in self.session.packet_review.ownership if p != "synthesis")
        page = self.app.review_packet(self.session.reference(), self.fp, pid, 1)
        with self.assertRaises(ValueError): self.app.review_packet(self.session.reference(), self.fp, pid, 2, page["nextCursor"])
        self.assertEqual(self.app.agent_write(self.session.reference())["status"], "agentReviewBlocked")

    def test_partial_packet_block_and_needs_context_cannot_write(self):
        pid = next(p for p in self.session.packet_review.ownership if p != "synthesis")
        first = self.app.review_packet(self.session.reference(), self.fp, pid, 1)
        with self.assertRaisesRegex(ValueError, "every exact packet page"):
            self.app.review_result(self.session.reference(), first["binding"], self.result(first["binding"]))
        binding, _ = self.packet(pid)
        self.app.review_result(self.session.reference(), binding, self.result(binding, "needs-context"))
        self.assertEqual(self.app.agent_write(self.session.reference())["status"], "agentReviewBlocked")
        self.app.review_result(self.session.reference(), binding, self.result(binding, "block"))
        with self.assertRaisesRegex(ValueError, "terminal"):
            self.app.review_result(self.session.reference(), binding, self.result(binding))
        with self.assertRaisesRegex(ValueError, "all branch"):
            self.packet("synthesis")

    def test_cross_branch_evidence_synthesis_and_atomic_write(self):
        before = self.path.read_bytes()
        self.allow_branches()
        self.assertEqual(self.path.read_bytes(), before)
        self.assertEqual(self.app.agent_write(self.session.reference())["status"], "agentReviewBlocked")
        binding, items = self.packet("synthesis")
        deps = {i["entityId"] for i in items if i["kind"] == "dependency"}
        self.assertIn("architecture-informs-privacy-documentation", deps)
        self.assertTrue(any(i["kind"] == "branchResult" for i in items))
        self.app.review_result(self.session.reference(), binding, self.result(binding))
        result = self.app.agent_write(self.session.reference())
        self.assertEqual(result["status"], "written")
        self.assertNotEqual(self.path.read_bytes(), before)
        self.assertTrue(ProjectStore().verify(self.path)["isValid"])

    def test_lossless_refinement_and_foreign_duplicate_missing_ownership(self):
        review = self.session.packet_review
        pid = next(p for p in review.ownership if p != "synthesis")
        ordinals = review.ownership[pid]
        binding, _ = self.packet(pid)
        for groups in ([ordinals[:-1], [ordinals[0]]], [ordinals, [999999]], [ordinals, [ordinals[0]]]):
            with self.assertRaises(ValueError): self.app.review_plan(self.session.reference(), refinements=[{"packetId": pid, "groups": groups}])
            self.assertEqual(review.plan_fingerprint, self.fp)
        new_plan = self.app.review_plan(self.session.reference(), refinements=[{"packetId": pid, "groups": [ordinals[:1], ordinals[1:]]}])
        self.assertNotEqual(new_plan["planFingerprint"], self.fp)
        self.assertEqual(sorted(o for v in review.ownership.values() for o in v), list(range(len(review.evidence))))
        with self.assertRaisesRegex(ValueError, "stale plan"):
            self.app.review_result(self.session.reference(), binding, self.result(binding))

    def test_registered_supplemental_disposition_and_proposal_changes_invalidate(self):
        pid = next(p for p in self.session.packet_review.ownership if p != "synthesis")
        binding, _ = self.packet(pid)
        before = self.session.reference()
        context = self.app.review_context(before, ["privacy-architecture"])
        self.assertNotEqual(before["reviewFingerprint"], context["reference"]["reviewFingerprint"])
        self.assertIsNone(self.session.packet_review)
        with self.assertRaises(ValueError): self.app.review_result(before, binding, self.result(binding))
        self.app.review_plan(self.session.reference())
        self.app.review(self.session.reference(), [{"nodeId": "runtime-test", "kind": "pending"}], [])
        self.assertIsNone(self.session.packet_review)
        self.assertFalse(self.session.readiness()["isReady"])
        self.app.apply(self.session.reference(), self.session.operations)
        self.assertEqual(self.session.supplemental, [])
        self.assertIsNone(self.session.packet_review)

    def test_strict_result_binding_citations_and_unresolved_questions(self):
        pid = next(p for p in self.session.packet_review.ownership if p != "synthesis")
        binding, _ = self.packet(pid)
        cases = [dict(self.result(binding), extra=True), dict(self.result(binding), citations=[{"entityId": "foreign"}]),
                 dict(self.result(binding), questions=["Unresolved"]), dict(self.result(binding), decision="needs-context"),
                 dict(self.result(binding), citations=[])]
        for result in cases:
            with self.assertRaises(ValueError): self.app.review_result(self.session.reference(), binding, result)
        for field in ("packetId", "packetFingerprint", "planFingerprint"):
            bad = dict(binding, **{field: "foreign"})
            with self.assertRaises(ValueError): self.app.review_result(self.session.reference(), bad, self.result(binding))





    def test_removed_allocation_inputs_are_rejected_and_new_commands_strict(self):
        for field in ("maxTraversalDepth", "maxAffectedNodes", "maxOutputItems"):
            with self.assertRaises(ValueError): _validate_payload("change.expand", {"reference": self.session.reference(), field: 1})
        with self.assertRaises(ValueError): _validate_payload("read.scope", {"path": str(self.path), "nodeId": "purpose", "maxVisitedNodes": 1})
        out = io.StringIO()
        ndjson_loop([json.dumps({"version": 1, "command": "host.help", "payload": {}})], out, io.StringIO())
        self.assertIn("change.review-export", json.loads(out.getvalue())["payload"]["commands"])




class GameAndScaleTests(unittest.TestCase):
    def fixture(self, extra=0):
        import importlib.util
        source = Path(__file__).parents[1] / "samples/ReviewPackets/create_fixture.py"
        spec = importlib.util.spec_from_file_location("game_fixture", source)
        fixture = importlib.util.module_from_spec(spec); spec.loader.exec_module(fixture)
        temp = tempfile.TemporaryDirectory(); self.addCleanup(temp.cleanup)
        path = Path(temp.name) / "game.vw.db"
        fixture.create(path, extra)
        return fixture, path

    def test_continent_count_rule_dependency_context_and_missing_link(self):
        fixture, path = self.fixture()
        app = Application(); s = app.begin(str(path), "game-world", "tester", "Add sixth continent")
        new = fixture.node("continent:F", "Continent F.", "continent")
        parent = fixture.edge("continent:F:parent", "continent:F", "world")
        count = fixture.node("continent-count", "Tamriel has six continents.")
        ops = [Operation(OperationKind.REPLACE, EntityKind.NODE, count["id"], node=__import__("validated_world.protocol", fromlist=["node_from_dto"]).node_from_dto(count)),
               operation_from_dto({"kind":"add", "entityKind":"node", "entityId":new["id"], "node":new, "edge":None}),
               operation_from_dto({"kind":"add", "entityKind":"edge", "entityId":parent["id"], "node":None, "edge":parent})]
        s = app.apply(s.reference(), tuple(ops))
        self.assertFalse(s.proposed_rules.is_valid)
        self.assertEqual(app.write(s.reference())["status"], "reviewNotReady")
        with self.assertRaises(ValueError): app.review_plan(s.reference())
        rules = fixture.rule(6)
        ops.append(operation_from_dto({"kind":"replace", "entityKind":"node", "entityId":rules["id"], "node":rules, "edge":None}))
        s = app.apply(s.reference(), tuple(ops))
        self.assertTrue(s.proposed_rules.is_valid)
        ids = {i["nodeId"] for i in s.affected_nodes}
        self.assertTrue({"dialogue:John", "dialogue:Darrell"}.issubset(ids))
        self.assertNotIn("dialogue:Tod", ids)
        self.assertNotIn("dialogue:unlinked", ids)
        context = {i["nodeId"] for i in s.scope_context}
        self.assertTrue({"npc:Darrell", "continent:Morrowind", "purpose"}.issubset(context))
        self.assertIn("continent-rule", {i["ruleEvidence"]["entityId"] for i in s.review_items() if i["kind"] == "ruleEvidence"})

    def test_large_purpose_review_can_refine_without_truncating_analysis(self):
        _, path = self.fixture(1000)
        app = Application(); s = app.begin(str(path), "game-world", "tester", "Broad game purpose change")
        s = app.apply(s.reference(), (Operation(OperationKind.REPLACE, EntityKind.NODE, "purpose", node=Node("purpose", "Make a coherent fantasy video game with accessible controls.")),))
        self.assertEqual(len(s.affected_nodes), len(s.proposed.nodes))
        self.assertGreater(len(s.affected_nodes), 1000)
        app.review(s.reference(), [{"nodeId":i["nodeId"], "kind":"updated" if i["isDirectChange"] else "reviewedNoChange"} for i in s.affected_nodes], [])
        plan = app.review_plan(s.reference(), 3)
        owned = s.packet_review.ownership["branch-000000"]
        groups = [owned[i:i+100] for i in range(0, len(owned), 100)]
        refined = app.review_plan(s.reference(), refinements=[{"packetId":"branch-000000", "groups":groups}])
        self.assertNotEqual(plan["planFingerprint"], refined["planFingerprint"])
        self.assertEqual(sum(len(v) for v in s.packet_review.ownership.values()), len(s.review_items()))
        page = app.review_packet(s.reference(), refined["planFingerprint"], next(p for p in s.packet_review.ownership if p != "synthesis"), 1)
        self.assertFalse(page["allEvidencePresented"])
        self.assertEqual(app.agent_write(s.reference())["status"], "agentReviewBlocked")


if __name__ == "__main__": unittest.main()
