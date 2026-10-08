"""Independent file delivery, guarded publication and compact host output."""
import copy
import importlib.util
import io
import json
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

CHECKOUT = Path(__file__).resolve().parents[1]
SCRIPT = CHECKOUT / 'skills/validated-world/scripts/review_handoff.py'
spec = importlib.util.spec_from_file_location('vw_review_handoff_test', SCRIPT)
handoff = importlib.util.module_from_spec(spec)
spec.loader.exec_module(handoff)
sys.path.insert(0, str(CHECKOUT / 'src'))
from validated_world.application import Application
from validated_world.cli import ndjson_loop


class ReviewHandoffTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()
        self.files = handoff.ReviewFiles(self.root)
        self.addCleanup(self.files.close)

    def export(self):
        binding = {'reference': {'projectId': 'garden', 'sessionId': 'fixture'},
                   'planFingerprint': 'plan', 'packetId': 'branch', 'packetFingerprint': 'packet'}
        return self.files._export([{'binding': binding, 'items': [{'text': 'Jardín 日本語'}]},
                                   {'binding': binding, 'items': [], 'allEvidencePresented': True}],
                                  'packet', binding)

    def test_full_authoring_branch_synthesis_and_cleanup_without_socket_access(self):
        app = Application()
        self.addCleanup(app.close)
        def request(command, payload):
            out = io.StringIO()
            ndjson_loop([json.dumps({'version': 1, 'command': command, 'payload': payload})],
                        out, io.StringIO(), app=app, close_on_eof=False, project_root=self.root)
            result = json.loads(out.getvalue())
            if result['status'] != 'ok':
                raise RuntimeError(result)
            return result
        def worker(descriptor):
            # A separate Python reads every page; no author terminal or URL.
            reader = """import importlib.util,json,sys
s=importlib.util.spec_from_file_location('reader',sys.argv[1]); m=importlib.util.module_from_spec(s);s.loader.exec_module(m)
d=json.loads(sys.argv[2])
for i in range(d['pageCount']): m.read_page(d['manifestPath'],d['manifestSha256'],i)
print(json.dumps(m.receipt(d['manifestPath'],d['manifestSha256'])))
"""
            return json.loads(subprocess.check_output(
                [sys.executable, '-B', '-X', 'utf8', '-c', reader, str(SCRIPT), json.dumps(descriptor)],
                text=True, encoding='utf-8', timeout=30))
        path = self.root / 'garden.html'
        with patch.object(socket, 'socket', side_effect=PermissionError('sockets denied')):
            request('sample.create', {'sampleName': 'technical-project', 'path': str(path)})
            reference = request('change.begin', {'path': str(path), 'projectId': 'technical-project',
                              'author': 'offline-fixture', 'intent': 'Clarify purpose'})['payload']['reference']
            node = request('read.node', {'path': str(path), 'entityId': 'purpose'})['payload']
            node['text'] += ' — Jardín 日本語 documentation.'
            reference = request('change.apply', {'reference': reference, 'operations': {'operations': [
                {'kind': 'replace', 'entityKind': 'node', 'entityId': 'purpose', 'node': node, 'edge': None}
            ]}})['payload']['reference']
            evidence = self.files.affected(request, reference, limit=2)
            self.assertEqual(evidence['binding']['reference'], reference)
            rows = [item for i in range(evidence['pageCount']) for item in
                    handoff.read_page(evidence['manifestPath'], evidence['manifestSha256'], i)['items']]
            reference = request('change.review', {'reference': reference,
                'dispositions': [{'nodeId': item['value']['nodeId'], 'kind':
                                  'updated' if item['value']['isDirectChange'] else 'reviewedNoChange'}
                                 for item in rows if item['kind'] == 'affectedNode'],
                'presentedContextNodeIds': [item['value']['nodeId'] for item in rows if item['kind'] == 'scopeContext']
            })['payload']['reference']
            plan = request('change.review-plan', {'reference': reference, 'limit': 2})['payload']
            entries = list(plan['items']); cursor = plan['nextCursor']
            while cursor:
                page = request('change.review-plan', {'reference': reference, 'limit': 2, 'cursor': cursor})['payload']
                entries.extend(page['items']); cursor = page['nextCursor']
            branches = sorted({item['packetId'] for item in entries} - {'synthesis'})
            self.assertTrue(branches)
            for packet_id in branches + ['synthesis']:
                descriptor = self.files.packet(request, reference, plan['planFingerprint'], packet_id, limit=2)
                reply = {'binding': descriptor['binding'], 'receipt': worker(descriptor), 'result': {
                    'decision': 'allow', 'summary': 'Synthetic offline file delivery fixture; not host acceptance.',
                    'citations': [{'entityId': 'purpose'}], 'concerns': [], 'questions': []}}
                incomplete = copy.deepcopy(reply); incomplete['receipt']['pageCount'] -= 1
                with self.assertRaisesRegex(ValueError, 'receipt'):
                    self.files.submit(request, descriptor, incomplete)
                self.assertEqual(request('change.agent-write', {'reference': reference})['payload']['status'],
                                 'agentReviewBlocked')
                self.files.submit(request, descriptor, reply)
            saved = request('change.agent-write', {'reference': reference})
            self.assertEqual(saved['payload']['status'], 'written')
            self.assertNotIn('packetReview', handoff.compact(saved))
            self.assertTrue(request('project.verify', {'path': str(path)})['payload']['isValid'])
            self.assertEqual(request('read.node', {'path': str(path), 'entityId': 'purpose'})['payload']['text'], node['text'])
        self.assertEqual(self.files.close(), [])
        self.assertEqual(list(self.root.iterdir()), [path])

    def test_hashes_binding_and_owned_receipts_cannot_be_substituted(self):
        descriptor = self.export()
        receipt = handoff.receipt(descriptor['manifestPath'], descriptor['manifestSha256'])
        calls = []
        request = lambda *args: calls.append(args)
        reply = {'binding': descriptor['binding'], 'receipt': receipt, 'result': {'decision': 'allow'}}
        for key in ('binding', 'receipt'):
            changed = copy.deepcopy(reply); changed[key] = {}
            with self.assertRaises(ValueError): self.files.submit(request, descriptor, changed)
        self.assertEqual(calls, [])
        with self.assertRaisesRegex(ValueError, 'manifest hash'):
            handoff.read_page(descriptor['manifestPath'], '0' * 64, 0)
        with self.assertRaises(ValueError): handoff.read_page(descriptor['manifestPath'], descriptor['manifestSha256'], -1)
        page = Path(descriptor['manifestPath']).parent / 'page-000000.json.gz'
        original = page.read_bytes(); page.write_bytes(original + b'changed')
        with self.assertRaisesRegex(ValueError, 'page hash'):
            self.files.submit(request, descriptor, reply)
        self.assertEqual(calls, [])
        self.assertTrue(self.files.close())
        self.assertTrue(page.exists())  # Changed/unknown files are never erased.
        page.write_bytes(original)

    def test_cleanup_preserves_unknown_files_and_preexisting_folder(self):
        base = self.root / '.vw-review'; base.mkdir()
        existing = base / 'human-note.txt'; existing.write_text('keep', encoding='utf-8')
        descriptor = self.export()
        unknown = Path(descriptor['manifestPath']).parent / 'unknown.txt'
        unknown.write_text('keep', encoding='utf-8')
        warnings = self.files.close()
        self.assertTrue(warnings)
        self.assertEqual(unknown.read_text(), 'keep')
        self.assertEqual(existing.read_text(), 'keep')
        unknown.unlink()
        self.assertEqual(self.files.close(), [])
        self.assertTrue(base.exists())

    def test_affected_reference_changes_reject_export_before_files(self):
        reference = {'projectId': 'garden', 'sessionId': 'session', 'reviewFingerprint': 'old'}
        calls = []
        def request(command, payload):
            calls.append(command)
            if command == 'change.show':
                return {'payload': {'reference': reference if len(calls) == 1 else dict(reference, reviewFingerprint='new')}}
            return {'payload': {'items': [], 'page': {'nextCursor': None}}}
        with self.assertRaisesRegex(ValueError, 'changed'): self.files.affected(request, reference)
        self.assertEqual(list(self.root.iterdir()), [])

    def test_linked_handoff_location_is_rejected(self):
        outside = self.root / 'other'; outside.mkdir()
        try: (self.root / '.vw-review').symlink_to(outside, target_is_directory=True)
        except OSError: self.skipTest('symlink creation unavailable')
        with self.assertRaisesRegex(ValueError, 'linked'): self.export()
        self.assertEqual(list(outside.iterdir()), [])

    def test_compact_output_omits_evidence_and_worker_decisions_without_changing_response(self):
        result = {'command': 'change.agent-write', 'status': 'ok', 'payload': {
            'status': 'written', 'reference': {'sessionId': 'exact'}, 'items': [{'text': 'private evidence'}],
            'totalCount': 1, 'nextCursor': None, 'packetReview': {'results': ['private review']},
            'graph': {'text': 'whole project'}, 'readiness': {'isReady': True, 'blockers': ['long trace']}}}
        before = copy.deepcopy(result)
        summary = handoff.compact(result)
        self.assertEqual(summary['reference'], {'sessionId': 'exact'})
        self.assertEqual(summary['itemCount'], 1)
        self.assertNotIn('private', json.dumps(summary))
        self.assertNotIn('whole project', json.dumps(summary))
        self.assertNotIn('long trace', json.dumps(summary))
        self.assertEqual(result, before)
        diagnostic = {'command': 'host.help', 'status': 'error', 'payload': {'message': 'actual error'}}
        self.assertEqual(handoff.compact(diagnostic), diagnostic)

    def test_compact_affected_reports_actual_pagination_without_exposing_evidence(self):
        app = Application()
        self.addCleanup(app.close)
        def request(command, payload):
            out = io.StringIO()
            ndjson_loop([json.dumps({'version': 1, 'command': command, 'payload': payload})],
                        out, io.StringIO(), app=app, close_on_eof=False, project_root=self.root)
            result = json.loads(out.getvalue())
            self.assertEqual(result['status'], 'ok', result)
            return result
        path = self.root / 'garden.html'
        request('sample.create', {'sampleName': 'technical-project', 'path': str(path)})
        reference = request('change.begin', {'path': str(path), 'projectId': 'technical-project',
                            'author': 'pagination-test', 'intent': 'Clarify purpose'})['payload']['reference']
        node = request('read.node', {'path': str(path), 'entityId': 'purpose'})['payload']
        node['text'] += ' Clarify the documented purpose.'
        reference = request('change.apply', {'reference': reference, 'operations': {'operations': [
            {'kind': 'replace', 'entityKind': 'node', 'entityId': 'purpose', 'node': node, 'edge': None}
        ]}})['payload']['reference']
        session = {key: reference[key] for key in ('projectId', 'sessionId')}
        cursor = None; seen = 0; summaries = []
        while True:
            payload = {'session': session, 'limit': 2}
            if cursor is not None: payload['cursor'] = cursor
            result = request('change.affected', payload)
            before = copy.deepcopy(result)
            summary = handoff.compact(result)
            page = result['payload']['page']
            self.assertEqual(summary['totalCount'], page['totalCount'])
            self.assertEqual(summary['itemCount'], len(result['payload']['items']))
            self.assertEqual(summary['hasMore'], page['nextCursor'] is not None)
            self.assertNotIn('items', summary)
            self.assertEqual(result, before)
            seen += summary['itemCount']; summaries.append(summary)
            cursor = page['nextCursor']
            if cursor is None: break
        self.assertGreater(len(summaries), 2)
        self.assertTrue(summaries[0]['hasMore'])
        self.assertFalse(summaries[-1]['hasMore'])
        self.assertTrue(all(summary['totalCount'] == seen for summary in summaries))


if __name__ == '__main__':
    unittest.main()
