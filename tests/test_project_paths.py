import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).parents[1] / 'src'))
from validated_world.cli import ndjson_loop
from validated_world.project_paths import ProjectPaths


class ProjectPathTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()
        self.project = self.root / 'project'; self.project.mkdir()
        self.sibling = self.root / 'project-extra'; self.sibling.mkdir()
        self.paths = ProjectPaths(self.project)

    def test_normalized_relative_absolute_unicode_and_descendant_paths(self):
        for name in ('docs-vw.html', 'sub/../docs-vw.html', 'sub/Café 日本語.html'):
            self.assertEqual(Path(self.paths.file(name)), self.project / os.path.normpath(name))
        self.assertEqual(Path(self.paths.file(str(self.project / 'docs.html'))), self.project / 'docs.html')
        self.assertEqual(Path(self.paths.file('.')), self.project)
        self.assertEqual(Path(self.paths.file('MixedCaseNew.html')).name, 'MixedCaseNew.html')

    def test_parent_absolute_sibling_prefix_and_control_characters_are_rejected(self):
        for value in ('../outside.html', 'sub/../../outside.html', str(self.sibling / 'docs.html'),
                      str(self.root / 'outside.html'), '', '   ', 'bad\x00.html', 'bad\n.html', 1):
            with self.subTest(value=value), self.assertRaises(ValueError): self.paths.file(value)

    def test_links_cannot_expand_authority_or_hide_an_inside_alias(self):
        link = self.project / 'link'
        if os.name == 'nt':
            result = subprocess.run(['cmd', '/c', 'mklink', '/J', str(link), str(self.sibling)], capture_output=True)
            self.assertEqual(result.returncode, 0, result.stderr)
        else:
            link.symlink_to(self.sibling, target_is_directory=True)
        try:
            with self.assertRaisesRegex(ValueError, 'outside'): self.paths.file('link/outside.html')
            with self.assertRaises(ValueError): ProjectPaths(link)
        finally: os.rmdir(link) if os.name == 'nt' else link.unlink()
        if os.name != 'nt':
            link.symlink_to(self.project, target_is_directory=True)
            try:
                with self.assertRaisesRegex(ValueError, 'linked'): self.paths.file('link/docs.html')
            finally: link.unlink()

    @unittest.skipUnless(os.name == 'nt', 'Windows path semantics')
    def test_windows_drive_relative_stream_and_device_aliases_are_rejected(self):
        for value in ('C:docs.html', 'docs.html:secret', 'NUL', 'nul.txt', 'dir/COM1.html',
                      'LPT¹.txt', 'trailing./docs.html', 'space /docs.html', '\\\\?\\C:\\outside.html',
                      '\\\\server\\share\\outside.html'):
            with self.subTest(value=value), self.assertRaises(ValueError): self.paths.file(value)

    def test_startup_requires_an_existing_directory(self):
        file = self.project / 'file'; file.write_text('data')
        for root in (file, self.project / 'missing'):
            with self.assertRaises(ValueError): ProjectPaths(root)

    def test_native_session_rejects_every_external_file_argument_and_keeps_running(self):
        outside = self.sibling / 'untouched.html'; outside.write_text('untouched')
        inside = str(self.project / 'docs.html')
        requests = [
            ('project.init', {'path': str(outside), 'projectId': 'p', 'title': 'P', 'purposeNodeId': 'purpose', 'purposeText': 'P'}),
            ('project.open', {'path': str(outside)}),
            ('read.node', {'path': str(outside), 'entityId': 'purpose'}),
            ('change.begin', {'path': str(outside), 'projectId': 'p', 'author': 'test', 'intent': 'test'}),
            ('template.describe', {'name': str(outside)}),
            ('template.export', {'name': 'research-notebook', 'destinationPath': str(outside)}),
            ('template.instantiate', {'name': str(outside), 'path': inside, 'projectId': 'p', 'title': 'P', 'purposeText': 'P'}),
            ('project.diff', {'basePath': str(outside), 'targetPath': inside}),
            ('project.merge', {'basePath': str(outside), 'oursPath': inside, 'theirsPath': inside}),
            ('host.help', {}),
            ('project.init', {'path': inside, 'projectId': 'p', 'title': 'P', 'purposeNodeId': 'purpose', 'purposeText': 'P'}),
            ('project.backup', {'sourcePath': inside, 'destinationPath': str(outside)}),
            ('project.bulk_plan', {'path': inside, 'manifestPath': str(outside)}),
            ('artifact.check', {'path': inside, 'allowedRoots': [str(self.sibling)]}),
            ('project.import-html', {'sourcePath': inside, 'destinationPath': str(self.sibling / 'db.vw.db')}),
            ('project.verify', {'path': inside}),
            ('host.exit', {}),
        ]
        out = io.StringIO()
        ndjson_loop([json.dumps({'version': 1, 'command': c, 'payload': p}) for c, p in requests], out, io.StringIO(), project_root=self.project)
        results = [json.loads(line) for line in out.getvalue().splitlines()]
        for index, result in enumerate(results):
            self.assertEqual(result['status'], 'ok' if index in {9, 10, 15, 16} else 'error', result)
            if result['status'] == 'error': self.assertIn('outside the session project folder', result['payload']['message'])
        self.assertEqual(outside.read_text(), 'untouched')
        self.assertEqual(list(self.sibling.iterdir()), [outside])
        self.assertEqual(list(self.project.iterdir()), [Path(inside)])
