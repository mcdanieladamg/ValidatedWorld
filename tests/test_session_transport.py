import io
import json
from pathlib import Path
from queue import Queue
import subprocess
import sys
import tempfile
from threading import Thread
import unittest
from urllib.error import HTTPError
from urllib.request import Request, ProxyHandler, build_opener

sys.path.insert(0, str(Path(__file__).parents[1] / 'src'))
from validated_world.memory_transport import request
from eng.verify_skill_workflow import verify


class SessionTransportTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()
        self.launcher = Path(__file__).parents[1] / 'skills/validated-world/scripts/validated_world.py'
        self.process = subprocess.Popen([sys.executable, '-B', '-X', 'utf8', '-I', '-S', str(self.launcher), 'serve'], cwd=self.root, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding='utf-8')
        self.addCleanup(self.close)
        line = Queue(); Thread(target=lambda: line.put(self.process.stdout.readline()), daemon=True).start()
        self.url = json.loads(line.get(timeout=30))['controllerUrl']
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

    def test_request_cli_uses_finished_commands_without_losing_server_session(self):
        result = subprocess.run([sys.executable, '-B', '-I', '-S', str(self.launcher), 'request', self.url], input=json.dumps({'version': 1, 'command': 'host.help', 'payload': {}}), capture_output=True, text=True, encoding='utf-8', timeout=30)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(result.stdout)['status'], 'ok')
        self.assertIsNone(self.process.poll())
        self.assertEqual(list(self.root.iterdir()), [])

    def test_isolated_bundled_workflow_denies_all_implicit_files(self):
        verify(self.launcher.parent.parent, session_http=True)
        verify(self.launcher.parent.parent)
