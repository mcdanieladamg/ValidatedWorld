"""Execute the agent's documented Python recipe against the public launcher."""
import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch


class PersistentRecipeTests(unittest.TestCase):
    def test_controller_and_memory_helper_deny_all_writes_except_selected_html(self):
        checkout = Path(__file__).resolve().parents[1]
        skill = checkout / 'skills/validated-world'
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            code = r'''
import json, os, pathlib, re, sys
from queue import Empty, Queue
import subprocess
from threading import Thread
python_executable = sys.executable
launcher_path = str(pathlib.Path(sys.argv[1]) / 'scripts/validated_world.py')
project_folder = sys.argv[2]
document = str(pathlib.Path(project_folder) / 'only.html')
recipe = re.search(r'```python\n(.*?)\n```', (pathlib.Path(sys.argv[1]) / 'references/persistent-io.md').read_text(encoding='utf-8'), re.S).group(1)
def audit(event, arguments):
    if event == 'open':
        path, mode, flags = arguments
        writes = (isinstance(mode, str) and any(c in mode for c in 'wax+')) or flags & (os.O_WRONLY | os.O_RDWR | os.O_CREAT | os.O_TRUNC | os.O_APPEND)
        if writes and not isinstance(path, int) and os.path.abspath(path) != document:
            raise PermissionError('unexpected controller write: ' + str(path))
    if event in {'os.mkdir', 'os.remove', 'os.rmdir', 'os.rename', 'os.link', 'os.symlink', 'tempfile.mkstemp', 'tempfile.mkdtemp'}:
        raise PermissionError('unexpected controller filesystem operation: ' + event)
sys.addaudithook(audit)
exec(compile(recipe, '<retained-controller>', 'exec'))
try:
    request('host.help', {})
    request('project.init', {'path': document, 'projectId': 'garden', 'title': 'Garden', 'purposeNodeId': 'purpose', 'purposeText': 'Grow plants.'})
    ref = request('change.begin', {'path': document, 'projectId': 'garden', 'author': 'audit', 'intent': 'Clarify plants'})['payload']['reference']
    node = request('read.node', {'path': document, 'entityId': 'purpose'})['payload']
    node['text'] = 'Grow native plants.'
    ref = request('change.apply', {'reference': ref, 'operations': {'operations': [{'kind': 'replace', 'entityKind': 'node', 'entityId': 'purpose', 'node': node, 'edge': None}]}})['payload']['reference']
    descriptor = review_memory.affected(request, ref, limit=1)
    assert review_memory.message(descriptor)['responses']
    assert len(protocol_responses) > 5
    request('change.discard', {'reference': ref})
finally:
    close_session()
assert not review_memory.messages and not protocol_responses
assert list(pathlib.Path(project_folder).iterdir()) == [pathlib.Path(document)]
print('controller memory audit passed')
'''
            result = subprocess.run([sys.executable, '-B', '-X', 'utf8', '-c', code, str(skill), str(root)],
                                    capture_output=True, text=True, encoding='utf-8', timeout=30)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn('controller memory audit passed', result.stdout)

    def test_documented_recipe_retains_review_saves_unicode_and_closes_cleanly(self):
        checkout = Path(__file__).resolve().parents[1]
        skill = checkout / 'skills/validated-world'
        reference = skill / 'references/persistent-io.md'
        recipe = re.search(r'```python\n(.*?)\n```', reference.read_text(encoding='utf-8'), re.S).group(1)
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            document = root / 'garden.html'
            namespace = {
                'python_executable': sys.executable,
                'launcher_path': str(skill / 'scripts/validated_world.py'),
                'project_folder': str(root),
            }
            resolve_path = Path.resolve

            def restricted_resolve(path, strict=False):
                # Reproduce hosts that permit cwd but deny strict path preflight.
                if strict:
                    raise PermissionError(5, 'Access is denied', str(path))
                return resolve_path(path, strict=strict)

            with patch.object(Path, 'resolve', autospec=True, side_effect=restricted_resolve):
                exec(compile(recipe, str(reference), 'exec'), namespace)
            process = namespace['process']
            request = namespace['request']

            def send(command, payload):
                return request(command, payload)['payload']

            try:
                self.assertEqual(Path(send('host.help', {})['projectRoot']), root)
                self.assertEqual(send('sample.list', {}), ['technical-project'])
                with self.assertRaisesRegex(RuntimeError, 'unknown member'):
                    request('host.help', {'unexpected': True})
                send('project.init', {'path': str(document), 'projectId': 'garden', 'title': 'Jardín 日本語',
                                     'purposeNodeId': 'purpose', 'purposeText': 'Plan three garden beds.'})
                reference = send('change.begin', {'path': str(document), 'projectId': 'garden',
                                                 'author': 'recipe-test', 'intent': 'Plan four garden beds.'})['reference']
                node = send('read.node', {'path': str(document), 'entityId': 'purpose'})
                node['text'] = 'Plan four garden beds — Jardín 日本語.'
                reference = send('change.apply', {'reference': reference, 'operations': {'operations': [
                    {'kind': 'replace', 'entityKind': 'node', 'entityId': 'purpose', 'node': node, 'edge': None}
                ]}})['reference']
                evidence = []; cursor = None
                while True:
                    page = send('change.affected', {'session': {k: reference[k] for k in ('projectId', 'sessionId')},
                                                   'limit': 2, **({'cursor': cursor} if cursor else {})})
                    evidence.extend(page['items']); cursor = page['page']['nextCursor']
                    if cursor is None: break
                reference = send('change.review', {
                    'reference': reference,
                    'dispositions': [{'nodeId': item['value']['nodeId'],
                                      'kind': 'updated' if item['value']['isDirectChange'] else 'reviewedNoChange'}
                                     for item in evidence if item['kind'] == 'affectedNode'],
                    'presentedContextNodeIds': [item['value']['nodeId'] for item in evidence if item['kind'] == 'scopeContext'],
                })['reference']
                cursor = None
                while True:
                    page = send('change.preview', {'reference': reference, 'limit': 2,
                                                  **({'cursor': cursor} if cursor else {})})
                    cursor = page['reviewPage']['nextCursor']
                    if cursor is None: break
                self.assertEqual(send('change.agent-write', {'reference': reference})['status'], 'agentReviewBlocked')
                # Offline transport fixture only; actual fresh-host review is checked separately.
                reference = send('change.agent-review', {'reference': reference, 'decision': {
                    'decision': 'allow', 'summary': 'Synthetic offline recipe acceptance fixture.', 'concerns': [],
                }})['reference']
                self.assertEqual(send('change.agent-write', {'reference': reference})['status'], 'written')
                self.assertTrue(send('project.verify', {'path': str(document)})['isValid'])
                self.assertEqual(send('read.node', {'path': str(document), 'entityId': 'purpose'})['text'], node['text'])
            finally:
                namespace['close_session']()
            self.assertEqual(process.returncode, 0)
            self.assertTrue(process.stdin.closed)
            self.assertTrue(process.stdout.closed)
            self.assertEqual(list(root.iterdir()), [document])
            self.assertIn('Jardín 日本語', document.read_text(encoding='utf-8'))


if __name__ == '__main__':
    unittest.main()
