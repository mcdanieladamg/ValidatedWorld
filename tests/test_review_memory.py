"""In-memory message delivery, guarded publication and compact host output."""
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
SCRIPT = CHECKOUT / 'skills/validated-world/scripts/review_memory.py'
spec = importlib.util.spec_from_file_location('vw_review_memory_test', SCRIPT)
handoff = importlib.util.module_from_spec(spec)
spec.loader.exec_module(handoff)
sys.path.insert(0, str(CHECKOUT / 'src'))
from validated_world.application import Application
from validated_world.cli import ndjson_loop


class ReviewMemoryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()
        self.memory = handoff.ReviewMemory()
        self.addCleanup(self.memory.close)

    def request_for(self, app):
        def request(command, payload):
            out = io.StringIO()
            ndjson_loop([json.dumps({'version': 1, 'command': command, 'payload': payload})],
                        out, io.StringIO(), app=app, close_on_eof=False, project_root=self.root)
            result = json.loads(out.getvalue())
            return handoff.response(result, command)
        return request

    def proposal(self, request):
        path = self.root / 'garden.html'
        request('sample.create', {'sampleName': 'technical-project', 'path': str(path)})
        reference = request('change.begin', {'path': str(path), 'projectId': 'technical-project',
                          'author': 'offline-fixture', 'intent': 'Clarify purpose'})['payload']['reference']
        node = request('read.node', {'path': str(path), 'entityId': 'purpose'})['payload']
        node['text'] += ' — Jardín 日本語 documentation.'
        reference = request('change.apply', {'reference': reference, 'operations': {'operations': [
            {'kind': 'replace', 'entityKind': 'node', 'entityId': 'purpose', 'node': node, 'edge': None}
        ]}})['payload']['reference']
        return path, reference, node

    def test_full_authoring_branch_synthesis_messages_without_files_or_sockets(self):
        app = Application(); self.addCleanup(app.close)
        request = self.request_for(app)
        def worker(message):
            # Independent process receives the explicit complete message on stdin.
            # This is an offline transport fixture, not semantic host acceptance.
            reader = "import json,sys;sys.path.insert(0,sys.argv[1]);from review_memory import receipt;print(json.dumps(receipt(json.load(sys.stdin))))"
            return json.loads(subprocess.check_output(
                [sys.executable, '-B', '-X', 'utf8', '-c', reader, str(SCRIPT.parent)],
                input=json.dumps(message), text=True, encoding='utf-8', timeout=30))
        with patch.object(socket, 'socket', side_effect=PermissionError('sockets denied')):
            path, reference, node = self.proposal(request)
            evidence = self.memory.affected(request, reference, limit=2)
            rows = [item for result in self.memory.message(evidence)['responses'] for item in result['payload']['items']]
            self.assertGreater(evidence['pageCount'], 2)
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
                descriptor = self.memory.packet(request, reference, plan['planFingerprint'], packet_id, limit=2)
                message = self.memory.message(descriptor)
                reply = {'binding': descriptor['binding'], 'receipt': worker(message), 'result': {
                    'decision': 'allow', 'summary': 'Synthetic offline memory delivery fixture; not host acceptance.',
                    'citations': [{'entityId': 'purpose'}], 'concerns': [], 'questions': []}}
                incomplete = copy.deepcopy(reply); incomplete['receipt']['pageCount'] -= 1
                with self.assertRaisesRegex(ValueError, 'receipt'):
                    self.memory.submit(request, descriptor, incomplete)
                self.assertEqual(request('change.agent-write', {'reference': reference})['payload']['status'],
                                 'agentReviewBlocked')
                self.memory.submit(request, descriptor, reply)
            saved = request('change.agent-write', {'reference': reference})
            self.assertEqual(saved['payload']['status'], 'written')
            self.assertNotIn('packetReview', handoff.compact(saved))
            self.assertTrue(request('project.verify', {'path': str(path)})['payload']['isValid'])
            self.assertEqual(request('read.node', {'path': str(path), 'entityId': 'purpose'})['payload']['text'], node['text'])
            # An old proposal's exact pages/receipts cannot authorize the consumed session.
            with self.assertRaises(RuntimeError): self.memory.submit(request, descriptor, reply)
        self.memory.close()
        self.assertFalse(self.memory.messages)
        self.assertEqual(list(self.root.iterdir()), [path])

    def test_complete_response_contract_and_external_payload_only_hypothesis(self):
        reference = {'projectId': 'garden', 'sessionId': 'session', 'reviewFingerprint': 'old'}
        with self.assertRaisesRegex(ValueError, 'complete NDJSON response'):
            self.memory.affected(lambda *args: {'reference': reference}, reference)
        for result in (None, {}, {'version': 1, 'command': 'other', 'status': 'ok', 'payload': {}},
                       {'version': True, 'command': 'change.show', 'status': 'ok', 'payload': {}},
                       {'version': 1, 'command': 'change.show', 'status': 'ok', 'payload': []}):
            with self.assertRaises(ValueError): handoff.response(result, 'change.show')
        error = {'version': 1, 'command': 'change.show', 'status': 'error', 'payload': {'code': 'wrong-session'}}
        with self.assertRaises(RuntimeError) as raised: handoff.response(error, 'change.show')
        self.assertEqual(raised.exception.args[0], error)

    def test_reference_change_during_paging_leaves_no_retained_evidence(self):
        reference = {'projectId': 'garden', 'sessionId': 'session', 'reviewFingerprint': 'old'}
        calls = []
        def request(command, payload):
            calls.append(command)
            value = {'reference': reference if len(calls) == 1 else dict(reference, sessionId='restarted')} if command == 'change.show' else {'items': [], 'page': {'nextCursor': None}}
            return {'version': 1, 'command': command, 'status': 'ok', 'payload': value}
        with self.assertRaisesRegex(ValueError, 'changed'): self.memory.affected(request, reference)
        self.assertEqual(self.memory.messages, {})
        self.assertEqual(list(self.root.iterdir()), [])

    def test_changed_missing_reordered_pages_and_bindings_reject(self):
        app = Application(); self.addCleanup(app.close)
        request = self.request_for(app)
        _, reference, _ = self.proposal(request)
        descriptor = self.memory.affected(request, reference, limit=2)
        message = self.memory.message(descriptor)
        for mutation in ('changed', 'missing', 'reordered', 'binding'):
            bad = copy.deepcopy(message)
            if mutation == 'changed': bad['responses'][0]['payload']['items'] = []
            if mutation == 'missing': bad['responses'].pop()
            if mutation == 'reordered': bad['responses'].reverse()
            if mutation == 'binding': bad['manifest']['binding'] = {}
            if mutation == 'binding':
                self.assertNotEqual(handoff.receipt(bad), handoff.receipt(message))
            else:
                with self.assertRaises(ValueError): handoff.receipt(bad)
        changed = copy.deepcopy(descriptor); changed['binding'] = {}
        with self.assertRaises(ValueError): self.memory.message(changed)
        reply = {'binding': descriptor['binding'], 'receipt': handoff.receipt(message), 'result': {}}
        with self.assertRaisesRegex(ValueError, 'not terminal'): self.memory.submit(request, descriptor, reply)
        # Returned messages cannot mutate the retained controller copy.
        message['responses'].clear()
        self.assertGreater(len(self.memory.message(descriptor)['responses']), 2)

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


if __name__ == "__main__":
    unittest.main()
