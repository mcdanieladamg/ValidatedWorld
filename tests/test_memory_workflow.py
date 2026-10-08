import copy
import dataclasses
from hashlib import sha256
import json
from pathlib import Path
import sqlite3
import sys
import tempfile
import unittest
from unittest.mock import patch
from urllib.error import HTTPError
from urllib.request import Request, ProxyHandler, build_opener

sys.path.insert(0, str(Path(__file__).parents[1] / 'src'))
from validated_world.application import Application, sample_graph
from validated_world.document_store import ProjectFiles, Publisher
from validated_world.document_format import parse
from validated_world.models import EntityKind, Node, Operation, OperationKind


class MemoryWorkflowTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()
        self.path = self.root / 'design.html'
        self.store = ProjectFiles(); self.addCleanup(self.store.close)
        self.project = self.store.initialize(self.path, sample_graph())
        self.app = Application(self.store); self.addCleanup(self.app.close)
        self.client = build_opener(ProxyHandler({}))

    def proposal(self):
        session = self.app.begin(str(self.path), self.project.graph.project_id, 'test', 'Review the revised battery duration')
        old = next(n for n in self.project.graph.nodes if n.id == 'battery-assumption')
        session = self.app.apply(session.reference(), (Operation(OperationKind.REPLACE, EntityKind.NODE, old.id, node=dataclasses.replace(old, text='The battery lasts two duty cycles')),))
        self.app.review(session.reference(), [{'nodeId': n['nodeId'], 'kind': 'updated' if n['isDirectChange'] else 'reviewedNoChange'} for n in session.affected_nodes], [n['nodeId'] for n in session.scope_context])
        return session

    def plan_export(self, session, packet_id=None):
        plan = self.app.review_plan(session.reference())
        review = session.packet_review
        pid = packet_id or next(p for p in review.ownership if p != 'synthesis')
        export = self.app.review_export(session.reference(), plan['planFingerprint'], pid, 1)
        return review, pid, export

    def get(self, url):
        with self.client.open(url, timeout=30) as response: return response.read()

    def result(self):
        return {'decision': 'allow', 'summary': 'Offline fixture of complete review.', 'citations': [{'entityId': 'battery-assumption'}], 'concerns': [], 'questions': []}

    def test_create_read_sql_backup_review_save_discard_without_temporary_files(self):
        with patch('tempfile.mkdtemp', side_effect=AssertionError('no temp allocation')), patch('tempfile.TemporaryDirectory', side_effect=AssertionError('no temp allocation')), patch('os.replace', side_effect=AssertionError('no replacement')), patch('os.link', side_effect=AssertionError('no links')):
            other = self.root / 'other.html'; backup = self.root / 'backup.html'
            self.store.initialize(other, self.project.graph)
            self.assertTrue(self.store.verify(other)['isValid'])
            self.assertIn('CREATE TABLE', self.store.export_sql(other))
            self.store.backup(other, backup)
            session = self.proposal()
            connection = self.store.workspaces[str(self.path)].connection
            self.assertEqual(connection.execute('pragma database_list').fetchone()['file'], '')
            self.assertEqual(connection.execute('pragma temp_store').fetchone()[0], 2)
            self.assertEqual(connection.execute("select count(*) from sqlite_master where type='table'").fetchone()[0], 4)
            session.preview(100)
            self.assertEqual(self.app.agent_write(session.reference())['status'], 'agentReviewBlocked')
            self.app.record_agent_review(session.reference(), {'decision': 'allow', 'summary': 'Offline fixture.', 'concerns': []})
            self.assertEqual(self.app.agent_write(session.reference())['status'], 'written')
            with self.assertRaises(sqlite3.ProgrammingError): connection.execute('select 1')
            self.project = self.store.load(self.path)
            self.app.discard(self.proposal().reference())
            self.assertEqual(set(self.root.iterdir()), {self.path, other, backup})

    def test_reads_and_session_start_need_no_directory_write(self):
        with patch('pathlib.Path.mkdir', side_effect=PermissionError('read-only parent')):
            self.assertTrue(self.store.verify(self.path)['isValid'])
            self.assertIn('CREATE TABLE', self.store.export_sql(self.path))
            self.app.discard(self.proposal().reference())
        self.assertEqual(list(self.root.iterdir()), [self.path])

    def test_discard_and_abnormal_process_loss_leave_last_saved_html(self):
        before = self.path.read_bytes(); session = self.proposal()
        self.app.close()
        self.assertEqual(self.path.read_bytes(), before)
        self.assertEqual(self.store.workspaces, {})
        self.assertEqual(list(self.root.iterdir()), [self.path])

    def test_ram_transaction_rollback_preserves_proposal_and_file(self):
        def fail(stage):
            if stage == 'nodes-written': raise RuntimeError('fixture transaction failure')
        self.store.engine._write_fault = fail
        session = self.proposal(); session.preview(100); before = self.path.read_bytes()
        self.assertEqual(self.app.write(session.reference())['status'], 'failed')
        self.assertEqual(self.path.read_bytes(), before)
        self.assertEqual(self.store.engine._load_connection(self.store.workspaces[str(self.path)].connection, str(self.path)).state_fingerprint, self.project.state_fingerprint)
        self.store.engine._write_fault = None
        self.assertEqual(self.app.write(session.reference())['status'], 'written')

    def test_save_failure_discards_ram_and_reports_possible_partial_file(self):
        session = self.proposal(); session.preview(100)
        def fail(stage):
            if stage == 'writing':
                self.path.write_bytes(b'partial HTML fixture')
                raise PermissionError('interrupted save')
        self.store.publisher.fault = fail
        result = self.app.write(session.reference())
        self.assertEqual(result['status'], 'failed')
        self.assertEqual(result['storageErrorCode'], 'html-publication-failure')
        self.assertNotIn('workingDbPath', result)
        self.assertIn('restore', result['message'])
        self.assertEqual(self.app.sessions, {}); self.assertEqual(self.store.workspaces, {})
        self.assertEqual(list(self.root.iterdir()), [self.path])

    def test_render_failure_and_stale_bytes_do_not_touch_destination(self):
        before = self.path.read_bytes()
        with patch('validated_world.document_format.render', return_value='invalid HTML'):
            with self.assertRaises(ValueError): Publisher().publish(self.project, self.path)
        self.assertEqual(self.path.read_bytes(), before)
        with self.assertRaisesRegex(RuntimeError, 'stale-document'):
            Publisher().publish(self.project, self.path, expected_bytes='wrong')
        self.assertEqual(self.path.read_bytes(), before)

    def test_concurrent_sessions_reject_changed_base_without_lock_files(self):
        session = self.proposal(); session.preview(100)
        other = Application(); self.addCleanup(other.close)
        second = other.begin(str(self.path), self.project.graph.project_id, 'other', 'Independent proposal')
        self.assertEqual(list(self.root.iterdir()), [self.path])
        self.assertEqual(self.app.write(session.reference())['status'], 'written')
        with self.assertRaisesRegex(ValueError, 'stale-base'): other.session(second.reference())

    def test_packet_endpoints_hashes_partial_coverage_readonly_and_cleanup(self):
        session = self.proposal(); review, pid, export = self.plan_export(session)
        self.assertEqual(review.seen.get(pid, set()), set())
        manifest = json.loads(self.get(export['manifestUrl']))
        self.assertEqual(manifest['binding'], export['binding'])
        self.assertEqual(review.seen.get(pid, set()), set())
        first = manifest['pages'][0]
        self.assertEqual(sha256(self.get(first['url'])).hexdigest(), first['sha256'])
        with self.assertRaisesRegex(ValueError, 'presented'): self.app.review_result(session.reference(), export['binding'], self.result())
        for page in manifest['pages'][1:]:
            raw = self.get(page['url']); self.assertEqual(sha256(raw).hexdigest(), page['sha256'])
            self.assertEqual(json.loads(raw)['binding'], export['binding'])
        self.app.review_result(session.reference(), export['binding'], self.result())
        with self.assertRaises(HTTPError): self.get(export['manifestUrl'] + '/unknown')
        with self.assertRaises(HTTPError): self.client.open(Request(export['manifestUrl'], data=b'{}'), timeout=30)
        self.app.cleanup_review_exports(session.session_id)
        with self.assertRaises(HTTPError) as error: self.get(export['manifestUrl'])
        self.assertEqual(error.exception.code, 404)
        self.assertEqual(list(self.root.iterdir()), [self.path])

    def test_supplemental_context_invalidates_endpoints_and_results(self):
        session = self.proposal(); _, _, export = self.plan_export(session)
        self.app.review_context(session.reference(), ['retention-policy'])
        with self.assertRaises(HTTPError) as error: self.get(export['manifestUrl'])
        self.assertEqual(error.exception.code, 410)
        self.assertEqual(self.app.agent_write(session.reference())['status'], 'agentReviewBlocked')

    def test_export_failure_restores_seen_and_allocates_no_evidence(self):
        session = self.proposal(); self.app.review_plan(session.reference())
        review = session.packet_review; pid = next(p for p in review.ownership if p != 'synthesis')
        seen = copy.deepcopy(review.seen); original = review.packet
        def fail(pid, limit, cursor):
            if cursor is not None: raise ValueError('fixture page failure')
            return original(pid, limit, cursor)
        with patch.object(review, 'packet', side_effect=fail):
            with self.assertRaisesRegex(ValueError, 'fixture page'): self.app.review_export(session.reference(), review.plan_fingerprint, pid, 1)
        self.assertEqual(review.seen, seen)
        self.assertEqual(self.app.packet_transport.exports, {})
        self.assertEqual(list(self.root.iterdir()), [self.path])

    def test_unicode_graph_ids_remain_data_in_generated_capability_urls(self):
        from validated_world.models import Edge
        session = self.proposal(); nid = '../../café/🦊'
        self.app.apply(session.reference(), session.operations + (Operation(OperationKind.ADD, EntityKind.NODE, nid, node=Node(nid, 'Untrusted claim')), Operation(OperationKind.ADD, EntityKind.EDGE, 'unicode-parent', edge=Edge('unicode-parent', nid, 'scope-power', 'scope-parent'))))
        self.app.review(session.reference(), [{'nodeId': n['nodeId'], 'kind': 'updated' if n['isDirectChange'] else 'reviewedNoChange'} for n in session.affected_nodes], [n['nodeId'] for n in session.scope_context])
        self.app.review_plan(session.reference())
        pid = next(pid for pid, ordinals in session.packet_review.ownership.items() if any(session.packet_review.evidence[o].get('affectedNode', {}).get('nodeId') == nid for o in ordinals))
        _, _, export = self.plan_export(session, pid)
        manifest = json.loads(self.get(export['manifestUrl']))
        pages = [json.loads(self.get(p['url'])) for p in manifest['pages']]
        self.assertIn(nid, json.dumps(pages, ensure_ascii=False))
        self.assertNotIn('café', export['manifestUrl'])
        self.app.discard(session.reference())
        with self.assertRaises(HTTPError): self.get(export['manifestUrl'])
