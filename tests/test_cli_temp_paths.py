"""CLI fixtures must select physical project paths beneath an aliased OS temp."""
import io
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import test_cli_protocol as protocol_tests
import test_cli_surface as surface_tests
import test_core as core_tests
import test_review_packets as packet_tests
import test_skip_dependencies as skip_tests


class CliTempPathTests(unittest.TestCase):
    def test_cli_fixtures_work_when_os_temp_is_a_directory_alias(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            physical = root / 'physical'; physical.mkdir()
            alias = root / 'alias'
            if os.name == 'nt':
                linked = subprocess.run(['cmd', '/c', 'mklink', '/J', str(alias), str(physical)],
                                        capture_output=True, timeout=30)
                self.assertEqual(linked.returncode, 0, linked.stderr)
            else:
                alias.symlink_to(physical, target_is_directory=True)
            try:
                # Exercise actual CLI protocols when OS temp has a linked
                # ancestor, as macOS /var does. Fixtures must canonicalize paths
                # before selecting a root or passing absolute file arguments.
                spellings = [str(alias)]
                if os.name == 'nt':
                    # Windows runner temp may use an 8.3 spelling even without
                    # a directory link. Exercise it when this volume provides it.
                    import ctypes
                    from ctypes import wintypes
                    get_short_path = ctypes.WinDLL('kernel32', use_last_error=True).GetShortPathNameW
                    get_short_path.argtypes = (wintypes.LPCWSTR, wintypes.LPWSTR, wintypes.DWORD)
                    get_short_path.restype = wintypes.DWORD
                    size = get_short_path(str(physical), None, 0)
                    self.assertGreater(size, 0)
                    buffer = ctypes.create_unicode_buffer(size)
                    self.assertGreater(get_short_path(str(physical), buffer, size), 0)
                    if os.path.normcase(buffer.value) != os.path.normcase(str(physical)):
                        spellings.append(buffer.value)
                for spelling in spellings:
                    with self.subTest(temp_path=spelling):
                        cases = (
                            protocol_tests.CliProtocolTests('test_unknown_and_duplicate_members_are_rejected_but_process_continues'),
                            surface_tests.CliSurfaceTests('test_ndjson_dispatches_every_stateless_family'),
                            core_tests.PythonProductTests('test_ndjson_exact_preview_gate_and_persistent_session'),
                            skip_tests.SkipDependenciesTests('test_ndjson_boolean_flag_is_explicit_and_reset_on_patch'),
                            packet_tests.GameAndScaleTests('test_continent_count_rule_dependency_context_and_missing_link'),
                            packet_tests.GameAndScaleTests('test_large_purpose_review_can_refine_without_truncating_analysis'),
                        )
                        with patch('tempfile.tempdir', spelling):
                            result = unittest.TextTestRunner(stream=io.StringIO()).run(unittest.TestSuite(cases))
                        failures = [(str(case), diagnostic) for case, diagnostic in result.errors + result.failures]
                        self.assertTrue(result.wasSuccessful(), failures)
                self.assertEqual(list(physical.iterdir()), [])
            finally:
                # Remove the owned link itself before TemporaryDirectory cleans
                # the physical tree; never recurse through a directory link.
                os.rmdir(alias) if os.name == 'nt' else alias.unlink()
