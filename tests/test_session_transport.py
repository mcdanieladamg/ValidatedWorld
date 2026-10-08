import io
import json
from hashlib import sha256
from pathlib import Path
from queue import Empty, Queue
import subprocess
import sys
import tempfile
from threading import Thread
import unittest
from unittest.mock import patch
from urllib.error import HTTPError
from urllib.request import Request, ProxyHandler, build_opener

sys.path.insert(0, str(Path(__file__).parents[1] / 'src'))
from validated_world.memory_transport import PacketTransport, request
from validated_world.cli import direct_command
from eng.verify_skill_workflow import verify


class InProcessControllerTests(unittest.TestCase):
    def test_authenticated_controller_rejects_bad_requests_and_discards_unsaved_work(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            ready = Queue(); finished = Queue()
            diagnostics = io.StringIO()

            class Announcement(io.StringIO):
                def flush(self): ready.put(self.getvalue())

            def run():
                try: finished.put(direct_command(['serve', str(root)], Announcement(), diagnostics))
                except Exception as exc: finished.put(exc)

            thread = Thread(target=run, daemon=True); thread.start()
            url = json.loads(ready.get(timeout=30))['controllerUrl']
            client = build_opener(ProxyHandler({}))

            def send(command, payload=None):
                out = io.StringIO()
                request(url, io.StringIO(json.dumps({'version': 1, 'command': command,
                                                    'payload': payload or {}})), out)
                return json.loads(out.getvalue())

            exited = False
            try:
                with self.assertRaises(HTTPError) as denied:
                    request(url + '-wrong', io.StringIO('{}'), io.StringIO())
                self.assertEqual(denied.exception.code, 404)
                denied.exception.close()
                for raw, headers in ((b'{broken', {}), (b'\xff', {}),
                                     (b'{}', {'Content-Length': '-1'}),
                                     (b'{}', {'Content-Length': 'invalid'})):
                    with self.subTest(raw=raw, headers=headers):
                        with self.assertRaises(HTTPError) as invalid:
                            client.open(Request(url, data=raw, headers=headers), timeout=30)
                        self.assertEqual(invalid.exception.code, 400)
                        self.assertIn('error', json.loads(invalid.exception.read()))
                        invalid.exception.close()
                help_result = send('host.help')
                self.assertEqual(Path(help_result['payload']['projectRoot']), root)
                outside = send('project.open', {'path': '../outside.html'})
                self.assertEqual(outside['status'], 'error')
                self.assertIn('outside the session project folder', outside['payload']['message'])
                document = root / '日本語.html'
                initialized = send('project.init', {'path': str(document), 'projectId': 'p',
                                                   'title': 'Café', 'purposeNodeId': 'purpose',
                                                   'purposeText': '日本語 baseline'})
                self.assertEqual(initialized['status'], 'ok', initialized)
                before = document.read_bytes()
                begun = send('change.begin', {'path': str(document), 'projectId': 'p',
                                              'author': 'test', 'intent': 'Unsaved edit'})
                self.assertEqual(begun['status'], 'ok', begun)
                applied = send('change.apply', {'reference': begun['payload']['reference'],
                    'operations': {'operations': [{'kind': 'replace', 'entityKind': 'node',
                        'entityId': 'purpose', 'node': {'id': 'purpose', 'text': 'Unsaved change',
                        'kind': None, 'tags': [], 'attributes': []}, 'edge': None}]}})
                self.assertEqual(applied['status'], 'ok', applied)
                read_back = send('read.node', {'path': str(document), 'entityId': 'purpose'})
                self.assertEqual(read_back['payload']['text'], '日本語 baseline')
                reference = applied['payload']['reference']
                affected = send('change.affected', {'session': {key: reference[key]
                                                              for key in ('projectId', 'sessionId')}})
                self.assertEqual(affected['payload']['affectedNodeCount'], 1)
                reviewed = send('change.review', {'reference': reference,
                    'dispositions': [{'nodeId': 'purpose', 'kind': 'updated'}],
                    'presentedContextNodeIds': []})
                self.assertEqual(reviewed['status'], 'ok', reviewed)
                reference = reviewed['payload']['reference']
                context = send('change.review-context', {'reference': reference, 'entityIds': ['purpose']})
                self.assertEqual(context['status'], 'ok', context)
                reference = context['payload']['reference']
                preview = send('change.preview', {'reference': reference, 'limit': 100})
                self.assertTrue(preview['payload']['reviewPage']['allEvidencePresented'])
                blocked = send('change.agent-write', {'reference': reference})
                self.assertEqual(blocked['payload']['status'], 'agentReviewBlocked')
                plan = send('change.review-plan', {'reference': reference, 'limit': 100})
                self.assertEqual(plan['status'], 'ok', plan)
                packet_args = {'reference': reference, 'planFingerprint': plan['payload']['planFingerprint'],
                               'packetId': plan['payload']['synthesisPacketId'], 'limit': 100}
                packet = send('change.review-packet', packet_args)
                self.assertEqual(packet['status'], 'ok', packet)
                export = send('change.review-export', packet_args)
                self.assertEqual(export['status'], 'ok', export)
                manifest_url = export['payload']['manifestUrl']
                with client.open(manifest_url, timeout=30) as response:
                    manifest = json.loads(response.read())
                for page in manifest['pages']:
                    with client.open(page['url'], timeout=30) as response: raw = response.read()
                    self.assertEqual(sha256(raw).hexdigest(), page['sha256'])
                    self.assertEqual(json.loads(raw)['binding'], export['payload']['binding'])
                # Synthetic protocol fixture only, not a fresh human/agent
                # semantic review. Exercise exact result bindings without saving.
                result = send('change.review-result', {'reference': reference,
                    'binding': packet['payload']['binding'], 'result': {'decision': 'allow',
                    'summary': 'Offline transport fixture only.', 'citations': [{'entityId': 'purpose'}],
                    'concerns': [], 'questions': []}})
                self.assertEqual(result['status'], 'ok', result)
                cleaned = send('change.review-cleanup', {'reference': reference})
                self.assertGreater(cleaned['payload']['revokedEndpoints'], 0)
                with self.assertRaises(HTTPError) as revoked: client.open(manifest_url, timeout=30)
                self.assertEqual(revoked.exception.code, 404)
                revoked.exception.close()
                self.assertEqual(send('host.exit')['status'], 'ok')
                exited = True
            finally:
                if not exited and thread.is_alive(): send('host.exit')
                thread.join(timeout=30)
            self.assertFalse(thread.is_alive())
            self.assertEqual(finished.get(timeout=30), 0)
            self.assertEqual(diagnostics.getvalue(), '')
            self.assertEqual(document.read_bytes(), before)
            self.assertEqual(list(root.iterdir()), [document])


class SessionTransportTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()
        self.launcher = Path(__file__).resolve().parents[1] / 'skills/validated-world/scripts/validated_world.py'
        self.process = subprocess.Popen([sys.executable, '-B', '-u', '-X', 'utf8', '-I', '-S', str(self.launcher), 'serve'], cwd=self.root, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding='utf-8')
        self.addCleanup(self.close)
        line = Queue(); Thread(target=lambda: line.put(self.process.stdout.readline()), daemon=True).start()
        try:
            announcement = line.get(timeout=30)
        except Empty as exc:
            self.process.terminate(); self.process.wait(timeout=30)
            raise AssertionError(f'Controller startup timed out after 30s; exit={self.process.returncode}; stderr={self.process.stderr.read()}') from exc
        if not announcement:
            self.process.wait(timeout=30)
            self.fail(f'Controller exited before announcing its URL; exit={self.process.returncode}; stderr={self.process.stderr.read()}')
        self.url = json.loads(announcement)['controllerUrl']
        self.client = build_opener(ProxyHandler({}))

    def close(self):
        if self.process.poll() is None:
            self.process.terminate(); self.process.wait(timeout=30)
        for stream in (self.process.stdin, self.process.stdout, self.process.stderr): stream.close()

    def send(self, command, payload=None):
        out = io.StringIO()
        request(self.url, io.StringIO(json.dumps({'version': 1, 'command': command, 'payload': payload or {}})), out)
        return json.loads(out.getvalue())

    def test_live_utf8_responses_errors_capability_and_exit_without_files(self):
        self.assertEqual(self.send('host.help')['status'], 'ok')
        with self.assertRaises(HTTPError): request(self.url + '-wrong', io.StringIO('{}'), io.StringIO())
        with self.assertRaises(HTTPError): self.client.open(self.url, timeout=30)
        with self.assertRaises(HTTPError): self.client.open(Request(self.url, data=b'{broken'), timeout=30)
        self.assertEqual(self.send('host.help', {'unexpected': True})['status'], 'error')
        path = self.root / '日本語.html'
        self.assertEqual(self.send('project.init', {'path': str(path), 'projectId': 'unicode', 'title': 'Café 🦊', 'purposeNodeId': 'purpose', 'purposeText': '日本語 data'})['status'], 'ok')
        self.assertEqual(self.send('read.node', {'path': str(path), 'entityId': 'purpose'})['payload']['text'], '日本語 data')
        self.assertEqual(self.send('project.verify', {'path': str(path)})['status'], 'ok')
        self.assertEqual(self.send('host.exit')['status'], 'ok')
        self.assertEqual(self.process.wait(timeout=30), 0)
        self.assertEqual(self.process.stderr.read(), '')
        self.assertEqual(list(self.root.iterdir()), [path])

    def test_controller_accepts_only_loopback_announced_urls(self):
        for url in ('https://127.0.0.1:10/token', 'http://example.com:10/token', 'http://user@127.0.0.1:10/token', 'http://127.0.0.1:10/token?x=1', 'http://127.0.0.1:10/token#x', 'http://127.0.0.1/token'):
            with self.assertRaisesRegex(ValueError, 'loopback'): request(url, io.StringIO('{}'), io.StringIO())

    def test_controller_rejects_external_reads_writes_and_root_expansion(self):
        with tempfile.TemporaryDirectory() as outside_directory:
            outside = Path(outside_directory).resolve() / 'untouched.html'
            outside.write_text('untouched', encoding='utf-8')
            requests = [
                ('project.init', {'path': str(outside), 'projectId': 'p', 'title': 'P', 'purposeNodeId': 'purpose', 'purposeText': 'P'}),
                ('project.open', {'path': str(outside)}),
                ('template.describe', {'name': str(outside)}),
                ('template.export', {'name': 'research-notebook', 'destinationPath': str(outside)}),
                ('project.init', {'path': '../escape.html', 'projectId': 'p', 'title': 'P', 'purposeNodeId': 'purpose', 'purposeText': 'P'}),
            ]
            for command, payload in requests:
                result = self.send(command, payload)
                self.assertEqual(result['status'], 'error', result)
                self.assertIn('outside the session project folder', result['payload']['message'])
            result = self.send('host.help', {'projectRoot': str(outside.parent)})
            self.assertEqual(result['status'], 'error')
            self.assertEqual(Path(self.send('host.help')['payload']['projectRoot']), self.root)
            self.assertEqual(outside.read_text(encoding='utf-8'), 'untouched')
            self.assertEqual(list(outside.parent.iterdir()), [outside])
            self.assertEqual(list(self.root.iterdir()), [])

    def test_controller_supports_descendants_and_custom_templates_inside_root(self):
        result = self.send('template.export', {'name': 'research-notebook', 'destinationPath': 'notebook.json'})
        self.assertEqual(result['status'], 'ok', result)
        result = self.send('template.instantiate', {'name': 'notebook.json', 'path': 'sub/docs.html', 'projectId': 'p', 'title': 'P', 'purposeText': 'P'})
        self.assertEqual(result['status'], 'ok', result)
        result = self.send('project.backup', {'sourcePath': 'sub/docs.html', 'destinationPath': 'backups/baseline.html'})
        self.assertEqual(result['status'], 'ok', result)
        self.assertEqual(self.send('project.verify', {'path': 'backups/baseline.html'})['status'], 'ok')

    def test_packet_startup_does_not_require_reverse_dns(self):
        with patch('socket.getfqdn', side_effect=AssertionError('reverse DNS is unavailable')):
            transport = PacketTransport()
            try:
                self.assertEqual(transport.server.server_name, '127.0.0.1')
                self.assertGreater(transport.server.server_port, 0)
            finally:
                transport.close()

    def test_controller_runs_without_reverse_dns(self):
        bootstrap = '''import runpy, socket, sys
def deny_dns(*args):
    raise AssertionError("reverse DNS is unavailable")
socket.getfqdn = deny_dns
sys.argv = sys.argv[1:]
runpy.run_path(sys.argv[0], run_name="__main__")
'''
        with subprocess.Popen([sys.executable, '-B', '-u', '-I', '-S', '-c', bootstrap, str(self.launcher), 'serve', str(self.root)], cwd=self.launcher.parent, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding='utf-8') as process:
            try:
                lines = Queue()
                Thread(target=lambda: lines.put(process.stdout.readline()), daemon=True).start()
                announcement = lines.get(timeout=30)
                if not announcement:
                    process.wait(timeout=30)
                    self.fail(f'Controller failed with DNS denied: {process.stderr.read()}')
                url = json.loads(announcement)['controllerUrl']
                self.assertEqual(Path(json.loads(announcement)['projectRoot']), self.root)
                output = io.StringIO()
                request(url, io.StringIO(json.dumps({'version': 1, 'command': 'host.help', 'payload': {}})), output)
                self.assertEqual(json.loads(output.getvalue())['status'], 'ok')
                request(url, io.StringIO(json.dumps({'version': 1, 'command': 'host.exit', 'payload': {}})), io.StringIO())
                self.assertEqual(process.wait(timeout=30), 0)
                self.assertEqual(process.stderr.read(), '')
                self.assertEqual(list(self.root.iterdir()), [])
            finally:
                if process.poll() is None: process.terminate(); process.wait(timeout=30)

    def test_request_cli_uses_finished_commands_without_losing_server_session(self):
        result = subprocess.run([sys.executable, '-B', '-I', '-S', str(self.launcher), 'request', self.url], input=json.dumps({'version': 1, 'command': 'host.help', 'payload': {}}), capture_output=True, text=True, encoding='utf-8', timeout=30)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(result.stdout)['status'], 'ok')
        self.assertIsNone(self.process.poll())
        self.assertEqual(list(self.root.iterdir()), [])

    def test_isolated_bundled_workflow_denies_all_implicit_files(self):
        verify(self.launcher.parent.parent, session_http=True)
        verify(self.launcher.parent.parent)
