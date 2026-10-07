import io
import json
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
from eng.verify_skill_workflow import verify


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
        with subprocess.Popen([sys.executable, '-B', '-u', '-I', '-S', '-c', bootstrap, str(self.launcher), 'serve'], cwd=self.root, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding='utf-8') as process:
            try:
                lines = Queue()
                Thread(target=lambda: lines.put(process.stdout.readline()), daemon=True).start()
                announcement = lines.get(timeout=30)
                if not announcement:
                    process.wait(timeout=30)
                    self.fail(f'Controller failed with DNS denied: {process.stderr.read()}')
                url = json.loads(announcement)['controllerUrl']
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
