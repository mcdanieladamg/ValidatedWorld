import os
from pathlib import Path
from types import SimpleNamespace
import tempfile
import unittest
from unittest.mock import patch

from validated_world.html_project import safe_path
from validated_world.path_safety import is_junction


class PathSafetyTests(unittest.TestCase):
    def test_missing_and_non_directory_paths_are_not_junctions(self):
        for error in (FileNotFoundError(), NotADirectoryError()):
            with self.subTest(error=type(error).__name__):
                with patch.object(Path, 'lstat', side_effect=error):
                    self.assertFalse(is_junction(Path('missing')))

    def test_reparse_tags_distinguish_junctions_from_other_files(self):
        for tag, expected in ((0xA0000003, True), (0xA000000C, False), (0, False)):
            with self.subTest(tag=tag):
                with patch.object(Path, 'lstat', return_value=SimpleNamespace(st_reparse_tag=tag)):
                    self.assertEqual(is_junction(Path('example')), expected)
        with patch.object(Path, 'lstat', return_value=SimpleNamespace()):
            self.assertFalse(is_junction(Path('example')))

    def test_access_errors_are_not_treated_as_safe_paths(self):
        with patch.object(Path, 'lstat', side_effect=PermissionError('denied')):
            with self.assertRaises(PermissionError):
                is_junction(Path('example'))

    @unittest.skipUnless(os.name == 'nt', 'Windows junction regression')
    def test_real_junction_and_its_ancestors_remain_rejected(self):
        import subprocess
        with tempfile.TemporaryDirectory(prefix='vw-junction-') as temporary:
            root = Path(temporary).resolve()
            target = root / 'physical'
            target.mkdir()
            alias = root / 'alias'
            # Windows directory junctions do not need symbolic-link privileges.
            subprocess.run(['powershell.exe', '-NoProfile', '-NonInteractive', '-Command',
                            'New-Item -ItemType Junction -Path $env:VW_TEST_ALIAS -Target $env:VW_TEST_TARGET | Out-Null'],
                           env={**os.environ, 'VW_TEST_ALIAS': str(alias), 'VW_TEST_TARGET': str(target)},
                           check=True, capture_output=True)
            try:
                self.assertTrue(is_junction(alias))
                for path in (alias, alias / 'docs-vw.html'):
                    with self.subTest(path=path):
                        with self.assertRaisesRegex(ValueError, 'linked project path'):
                            safe_path(path)
                self.assertFalse(is_junction(target))
                self.assertEqual(safe_path(target / 'docs-vw.html'), target / 'docs-vw.html')
            finally:
                # Remove only the alias itself; never traverse its target.
                alias.rmdir()
