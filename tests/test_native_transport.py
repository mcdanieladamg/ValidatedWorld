"""Native reviewer channel isolation, byte framing and read-only capabilities."""
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
from multiprocessing.connection import Client

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'src'))
from validated_world.review_transport import PacketTransport, fetch, _address
from validated_world.cli import direct_command


class NativeTransportTests(unittest.TestCase):
    def test_local_channel_no_files_no_dns_and_independent_reader(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            with patch('socket.getfqdn', side_effect=AssertionError('no DNS')), patch('tempfile.mkstemp', side_effect=AssertionError('no temp')), patch('tempfile.mkdtemp', side_effect=AssertionError('no temp')):
                transport = PacketTransport()
                try:
                    transport.exports['fixture'] = ('owner', b'{"text":"Jard\\u00edn"}\n', lambda: None, None)
                    launcher = Path(__file__).resolve().parents[1] / 'skills/validated-world/scripts/validated_world.py'
                    raw = subprocess.check_output([sys.executable, '-B', '-X', 'utf8', '-I', '-S', str(launcher),
                        'review-read', json.dumps(transport.channel), 'fixture'], cwd=root, timeout=30)
                    self.assertEqual(json.loads(raw), {'text': 'Jardín'})
                    self.assertEqual(transport.channel['family'], 'AF_PIPE' if os.name == 'nt' else 'AF_UNIX' if sys.platform.startswith('linux') else 'AF_INET')
                    self.assertEqual(list(root.iterdir()), [])
                    wrong = dict(transport.channel, token='wrong')
                    with self.assertRaisesRegex(ValueError, 'capability'): fetch(wrong, 'fixture')
                    with self.assertRaisesRegex(ValueError, 'unknown'): fetch(transport.channel, 'missing')
                    self.assertEqual(transport.revoke('other')['revokedEndpoints'], 0)
                    self.assertEqual(transport.revoke('owner')['revokedEndpoints'], 1)
                    with self.assertRaisesRegex(ValueError, 'revoked'): fetch(transport.channel, 'fixture')
                finally:
                    transport.close()
                self.assertFalse(transport.thread.is_alive())
                self.assertFalse(transport.connections)
                self.assertEqual(list(root.iterdir()), [])

    def test_rejects_external_addresses_disk_sockets_and_arbitrary_pipe_names(self):
        bad = [('AF_INET', ['example.com', 10]), ('AF_INET', ['127.0.0.1', True]),
               ('AF_INET', ['127.0.0.1', 0]), ('AF_UNIX', '/tmp/socket'),
               ('AF_PIPE', r'\\remote\pipe\validated-world-' + 'a' * 64),
               ('AF_PIPE', r'\\.\pipe\unrelated'), ('AF_UNIX', '\0validated-world-wrong')]
        for family, address in bad:
            with self.subTest(family=family, address=address):
                with self.assertRaises(ValueError): _address({'family': family, 'address': address})
        for family, address in [('AF_PIPE', '\\\\.\\pipe\\validated-world-' + 'a' * 64),
                                ('AF_UNIX', '\0validated-world-' + 'a' * 64), ('AF_INET', ['127.0.0.1', 10])]:
            self.assertEqual(_address({'family': family, 'address': address})[0], family)

    def test_reviewer_read_command_cannot_execute_authoring(self):
        transport = PacketTransport()
        try:
            transport.exports['fixture'] = ('owner', b'{}\n', lambda: None, None)
            out = io.StringIO(); err = io.StringIO()
            self.assertEqual(direct_command(['review-read', json.dumps(transport.channel), 'fixture'], out, err), 0)
            self.assertEqual(json.loads(out.getvalue()), {})
            self.assertEqual(direct_command(['review-read', json.dumps(transport.channel), 'change.write'], io.StringIO(), err), 1)
        finally:
            transport.close()

    def test_malformed_and_oversized_control_frames_do_not_poison_channel(self):
        transport = PacketTransport()
        try:
            transport.exports['fixture'] = ('owner', b'{}\n', lambda: None, None)
            family, address = _address(transport.channel)
            for raw in (b'not JSON', b'x' * 513):
                with Client(address, family=family) as connection:
                    connection.send_bytes(raw)
                    self.assertTrue(connection.poll(30))
                    with self.assertRaises((EOFError, OSError)):
                        connection.recv_bytes()
                self.assertEqual(fetch(transport.channel, 'fixture'), b'{}\n')
        finally:
            transport.close()

    def test_native_packet_workflow_with_only_selected_html_writable(self):
        import importlib.util
        path = Path(__file__).resolve().parents[1] / 'eng/verify_skill_workflow.py'
        spec = importlib.util.spec_from_file_location('native_workflow_fixture', path)
        module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
        module.verify(path.parent.parent / 'skills/validated-world', packet_channel=True)
