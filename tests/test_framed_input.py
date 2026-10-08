import importlib.util
import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest

from validated_world.scratch import read_responses


SKILL = Path(__file__).resolve().parents[1] / 'skills' / 'validated-world'
HELPER = SKILL / 'scripts' / 'ndjson_log.py'
spec = importlib.util.spec_from_file_location('framed_input_helper', HELPER)
helper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(helper)


class FramedInputTests(unittest.TestCase):
    def test_round_trip_preserves_escape_boundaries_unicode_and_multiple_requests(self):
        requests = [{'version': 1, 'command': 'host.help', 'payload': {}},
                    {'version': 1, 'command': 'read.search', 'payload': {
                        'text': 'quotes " newline\n backslash \\ Ω 😀 ' * 300}}]
        for size in (1, 7, 600):
            with self.subTest(size=size):
                frames = [frame for request in requests for frame in helper.request_frames(request, size)]
                self.assertTrue(all(len(frame.encode('utf-8')) < 1000 for frame in frames))
                output = io.StringIO()
                decoded = list(helper.FramedInput(io.StringIO(''.join(frames)), output))
                self.assertEqual([json.loads(line) for line in decoded], requests)
                self.assertEqual(output.getvalue(), '')

    def test_altered_missing_reordered_and_duplicated_fragments_never_dispatch(self):
        frames = list(helper.request_frames({'command': 'change.apply', 'payload': {'text': 'A' * 1800 + 'B' * 1800}}, 600))
        variants = {
            'truncated': frames[:2] + [frames[2][:100] + '\n'] + frames[3:],
            'missing': frames[:2] + frames[3:],
            'reordered': frames[:1] + [frames[2], frames[1]] + frames[3:],
            'duplicated': frames[:2] + [frames[1]] + frames[2:],
            'checksum': frames[:-1] + ['@vw-end ' + '0' * 64 + '\n'],
        }
        good = {'version': 1, 'command': 'host.help', 'payload': {}}
        for kind, damaged in variants.items():
            with self.subTest(kind=kind):
                output = io.StringIO()
                source = ''.join(damaged + list(helper.request_frames(good)))
                decoded = list(helper.FramedInput(io.StringIO(source), output))
                self.assertEqual([json.loads(line) for line in decoded], [good])
                error = json.loads(output.getvalue())
                self.assertEqual(error['command'], 'input.frame')
                self.assertEqual(error['status'], 'error')

    def test_unfinished_malformed_and_interleaved_frames_are_not_requests(self):
        for source in ('@vw-begin\n@vw-part {', '@vw-begin\n@vw-part {}\n',
                       '@vw-part {}\n', '@vw-begin\n@vw-begin\n',
                       '@vw-begin\n{"command":"host.exit"}\n'):
            with self.subTest(source=source):
                output = io.StringIO()
                self.assertEqual(list(helper.FramedInput(io.StringIO(source), output)), [])
                self.assertEqual(json.loads(output.getvalue())['status'], 'error')
        direct = '{"version":1,"command":"host.help","payload":{}}\n'
        output = io.StringIO()
        self.assertEqual(list(helper.FramedInput(io.StringIO('@vw-begin\r\n@vw-part {}\r\n@vw-cancel\r\n' + direct), output)), [direct])
        self.assertEqual(output.getvalue(), '')

    def test_encoder_runs_isolated_without_creating_project_files(self):
        request = {'version': 1, 'command': 'read.search', 'payload': {'text': 'Unicode Ω 😀' * 300}}
        with tempfile.TemporaryDirectory() as directory:
            result = subprocess.run([sys.executable, '-X', 'utf8', '-I', '-S', str(HELPER), '--frame-input', '--chunk-size', '71'],
                                    input=json.dumps(request), cwd=directory, capture_output=True, text=True, timeout=10)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(result.stdout, ''.join(helper.request_frames(request, 71)))
            self.assertEqual(list(Path(directory).iterdir()), [])
            for arguments in (['--frame-input', '--chunk-size', '0'], ['--chunk-size', '600'],
                              ['--frame-input', '--read', 'unknown']):
                bad = subprocess.run([sys.executable, '-I', '-S', str(HELPER), *arguments], input='{}', cwd=directory,
                                     capture_output=True, text=True, timeout=10)
                self.assertNotEqual(bad.returncode, 0)
            self.assertEqual(list(Path(directory).iterdir()), [])
        for size in (0, -1, True):
            with self.assertRaises(ValueError): list(helper.request_frames(request, size))

    def test_long_change_apply_survives_limited_calls_and_damaged_request_does_not_mutate(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            document = root / 'custom.html'
            process = subprocess.Popen([sys.executable, '-X', 'utf8', '-I', '-S', str(HELPER), '--document', str(document)],
                                       cwd=root, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                       text=True, encoding='utf-8')
            offset = 0
            def send(command, payload, damage=False):
                nonlocal offset
                frames = list(helper.request_frames({'version': 1, 'command': command, 'payload': payload}))
                if damage:
                    frames[2] = frames[2][:100] + '\n'
                for frame in frames:
                    # Model the evaluator's per-call ceiling: any oversized call
                    # is silently truncated before the child receives it.
                    self.assertLess(len(frame.encode('utf-8')), 1000)
                    process.stdin.write(frame[:1000])
                    process.stdin.flush()
                deadline = time.monotonic() + 10
                while time.monotonic() < deadline:
                    result = read_responses(log, offset)
                    offset = result['nextOffset']
                    if result['responses']:
                        self.assertEqual(len(result['responses']), 1)
                        return result['responses'][0]
                    self.assertIsNone(process.poll())
                    time.sleep(.01)
                self.fail('No live response after framed request')
            try:
                readiness = process.stderr.readline()
                self.assertTrue(readiness.startswith('NDJSON ready; responses: '), readiness)
                log = Path(readiness.removeprefix('NDJSON ready; responses: ').strip())
                self.assertEqual(send('host.help', {})['status'], 'ok')
                self.assertEqual(send('project.init', {'path': str(document), 'projectId': 'large-input', 'title': 'Input test',
                                                      'purposeNodeId': 'purpose', 'purposeText': 'Test intact input.'})['status'], 'ok')
                original = document.read_bytes()
                reference = send('change.begin', {'path': str(document), 'projectId': 'large-input', 'author': 'test',
                                                   'intent': 'Add one intact large claim.'})['payload']['reference']
                session = {key: reference[key] for key in ('projectId', 'sessionId')}
                text = 'A claim with Ω 😀, "quotes", \\ escapes and\nnewlines. ' * 300
                node = {'id': 'large-claim', 'text': text, 'kind': 'claim', 'tags': [], 'attributes': []}
                edge = {'id': 'large-scope', 'source': 'large-claim', 'target': 'purpose', 'relationship': 'scope-parent',
                        'reviewDirection': 'none', 'rationale': None, 'tags': [], 'attributes': []}
                payload = {'reference': reference, 'operations': {'operations': [
                    {'kind': 'add', 'entityKind': 'node', 'entityId': node['id'], 'node': node, 'edge': None},
                    {'kind': 'add', 'entityKind': 'edge', 'entityId': edge['id'], 'node': None, 'edge': edge}]}}
                self.assertGreater(len(json.dumps(payload)), 10000)
                self.assertEqual(send('change.apply', payload, damage=True)['command'], 'input.frame')
                unchanged = send('change.show', {'session': session, 'includeOperations': True})['payload']
                self.assertEqual(unchanged['reference'], reference)
                self.assertEqual(unchanged['operations']['operations'], [])
                applied = send('change.apply', payload)
                self.assertEqual(applied['status'], 'ok', applied)
                shown = send('change.show', {'session': session, 'includeOperations': True})['payload']
                self.assertEqual(shown['operations']['operations'][0]['node']['text'], text)
                self.assertEqual(document.read_bytes(), original)
                self.assertEqual({p.name for p in root.iterdir()}, {'custom.html', '.custom.tmp.html'})
                self.assertEqual(send('change.discard', {'reference': shown['reference']})['status'], 'ok')
                process.stdin.write('{"version":1,"command":"host.exit","payload":{}}\n')
                process.stdin.flush()
                process.stdin.close()
                self.assertEqual(process.wait(timeout=10), 0)
                self.assertFalse(log.exists())
                self.assertEqual(list(root.iterdir()), [document])
            finally:
                if process.poll() is None:
                    process.kill(); process.wait(timeout=10)
                for stream in (process.stdin, process.stdout, process.stderr): stream.close()
