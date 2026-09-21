import sqlite3
import sys
import tempfile
import time
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parents[1] / "src-python"))

from validated_world.application import Application, sample_graph
from validated_world.config import ReviewConfig
from validated_world.models import EntityKind, Node, Operation, OperationKind
from validated_world.storage import ProjectStore


class ApplicationParityTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.path = Path(self.temporary.name) / "application parity.vw.db"
        ProjectStore().initialize(self.path, sample_graph())

    @staticmethod
    def _review_and_preview(application, session):
        dispositions = [
            {"nodeId": item["nodeId"], "kind": "updated" if item["isDirectChange"] else "reviewedNoChange"}
            for item in session.affected_nodes
        ]
        session = application.review(
            session.reference(), dispositions, [item["nodeId"] for item in session.scope_context]
        )
        cursor = None
        while True:
            page = session.preview(2, cursor)
            cursor = page["reviewPage"]["nextCursor"]
            if cursor is None:
                return session

    def _replace_battery(self, application, text="Replacement battery assumption"):
        session = application.begin(str(self.path), "technical-project", "tester", "Replace battery")
        return application.apply(
            session.reference(),
            (Operation(OperationKind.REPLACE, EntityKind.NODE, "battery-assumption", node=Node("battery-assumption", text, "assumption")),),
        )

    def test_explicit_edges_are_removed_before_their_node_in_one_atomic_write(self):
        application = Application()
        incident_edges = tuple(edge.id for edge in sample_graph().edges if edge.source == "battery-assumption" or edge.target == "battery-assumption")
        session = application.begin(str(self.path), "technical-project", "tester", "Remove battery concept")
        operations = tuple(Operation(OperationKind.REMOVE, EntityKind.EDGE, edge_id) for edge_id in incident_edges) + (
            Operation(OperationKind.REMOVE, EntityKind.NODE, "battery-assumption"),
        )
        session = application.apply(session.reference(), operations)
        session = self._review_and_preview(application, session)

        result = application.write(session.reference(), bypass_ai_review=True)

        self.assertEqual(result["status"], "written")
        stored = ProjectStore().load(self.path)
        self.assertNotIn("battery-assumption", {node.id for node in stored.graph.nodes})
        self.assertTrue(set(incident_edges).isdisjoint(edge.id for edge in stored.graph.edges))
        self.assertEqual((len(stored.graph.nodes), len(stored.graph.edges)), (12, 14))

    def test_unready_invalid_and_bounded_inconclusive_proposals_do_not_write(self):
        application = Application()
        before = self.path.read_bytes()
        pending = self._replace_battery(application)
        self.assertEqual(application.write(pending.reference(), bypass_ai_review=True)["status"], "reviewNotReady")
        self.assertEqual(self.path.read_bytes(), before)
        application.discard(pending.reference())

        invalid = application.begin(str(self.path), "technical-project", "tester", "Break scope")
        invalid = application.apply(
            invalid.reference(),
            (Operation(OperationKind.REMOVE, EntityKind.EDGE, "battery-scope-parent"),),
        )
        self.assertEqual(invalid.proposed_validation.status, "invalid")
        self.assertEqual(application.write(invalid.reference(), bypass_ai_review=True)["status"], "reviewNotReady")
        self.assertEqual(self.path.read_bytes(), before)
        application.discard(invalid.reference())

        bounded = application.begin(str(self.path), "technical-project", "tester", "Bound analysis")
        bounded = application.apply(
            bounded.reference(),
            (Operation(OperationKind.REPLACE, EntityKind.NODE, "battery-assumption", node=Node("battery-assumption", "Bounded", "assumption")),),
            max_affected_nodes=1,
        )
        self.assertTrue(bounded.omissions)
        self.assertEqual(application.write(bounded.reference(), bypass_ai_review=True)["status"], "reviewNotReady")
        self.assertEqual(self.path.read_bytes(), before)

    def test_written_and_discarded_references_cannot_authorize_later_sessions(self):
        application = Application()
        discarded = self._replace_battery(application, "Discarded")
        discarded_reference = discarded.reference()
        application.discard(discarded_reference)
        with self.assertRaisesRegex(ValueError, "session not found"):
            application.locate(discarded_reference)

        written = self._replace_battery(application, "Written")
        written = self._review_and_preview(application, written)
        written_reference = written.reference()
        self.assertEqual(application.write(written_reference, bypass_ai_review=True)["status"], "written")
        with self.assertRaisesRegex(ValueError, "session not found"):
            application.locate(written_reference)

        later = application.begin(str(self.path), "technical-project", "tester", "Later")
        with self.assertRaisesRegex(ValueError, "session not found"):
            application.write(discarded_reference, bypass_ai_review=True)
        self.assertEqual(application.locate(later.reference()).session_id, later.session_id)

    def test_real_sqlite_lock_returns_bounded_busy_result_and_preserves_session(self):
        application = Application()
        session = self._review_and_preview(application, self._replace_battery(application, "Busy write"))
        before = self.path.read_bytes()
        lock = sqlite3.connect(self.path, timeout=1)
        try:
            lock.execute("BEGIN EXCLUSIVE")
            started = time.monotonic()
            result = application.write(session.reference(), bypass_ai_review=True)
            elapsed = time.monotonic() - started
        finally:
            lock.rollback()
            lock.close()

        self.assertEqual((result["status"], result["storageErrorCode"]), ("busy", "database-busy"))
        self.assertLess(elapsed, 15)
        self.assertEqual(self.path.read_bytes(), before)
        self.assertEqual(application.locate(session.reference()).session_id, session.session_id)

    def test_semantic_block_is_invalidated_by_repair_before_a_second_review(self):
        decisions = iter(("block", "allow"))
        requests = []

        def reviewer(request, _config):
            requests.append(request)
            decision = next(decisions)
            return {"status": "complete", "decision": decision, "summary": decision, "concerns": []}

        config = ReviewConfig(True, "openai", "offline", 1, False, "offline-key", None, None, None)
        application = Application(review_config_loader=lambda: config, semantic_reviewer=reviewer)
        session = self._review_and_preview(application, self._replace_battery(application, "Needs clarification"))
        blocked = application.write(session.reference())
        self.assertEqual(blocked["status"], "semanticReviewBlocked")

        repaired = application.apply(
            session.reference(),
            (Operation(OperationKind.REPLACE, EntityKind.NODE, "battery-assumption", node=Node("battery-assumption", "Target duty-cycle battery life", "assumption")),),
        )
        repaired = self._review_and_preview(application, repaired)
        written = application.write(repaired.reference())

        self.assertEqual(written["status"], "written")
        self.assertEqual(len(requests), 2)
        self.assertNotEqual(requests[0]["binding"]["operationFingerprint"], requests[1]["binding"]["operationFingerprint"])


if __name__ == "__main__":
    unittest.main()
