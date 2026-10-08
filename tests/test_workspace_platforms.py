"""Exercise companion ownership safeguards on every CI host with mocked OS APIs."""
import ctypes
import ntpath
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

from validated_world import review_workspace as workspace
from validated_world import project_paths


class WorkspacePlatformTests(unittest.TestCase):
    def windows_api(self, kernel, error=5):
        """Mock only this module's OS selection, never pathlib's host platform."""
        self.enterContext(patch.object(workspace, 'os', SimpleNamespace(name='nt')))
        dll = self.enterContext(patch.object(ctypes, 'WinDLL', return_value=kernel, create=True))
        self.enterContext(patch.object(ctypes, 'get_last_error', return_value=error, create=True))
        return dll

    def test_hidden_flag_preserves_other_windows_attributes(self):
        kernel = Mock()
        kernel.GetFileAttributesW.side_effect = [0x21, 0x23]
        kernel.SetFileAttributesW.return_value = 1
        dll = self.windows_api(kernel)
        workspace.hidden('document.tmp.html')
        workspace.hidden('document.tmp.html', False)
        self.assertEqual(kernel.SetFileAttributesW.call_args_list,
                         [unittest.mock.call('document.tmp.html', 0x23),
                          unittest.mock.call('document.tmp.html', 0x21)])
        self.assertEqual(dll.call_args_list,
                         [unittest.mock.call('kernel32', use_last_error=True)] * 2)

    def test_unknown_windows_attributes_fail_without_attempting_a_write(self):
        kernel = Mock()
        kernel.GetFileAttributesW.return_value = 0xffffffff
        self.windows_api(kernel)
        with self.assertRaises(OSError) as raised:
            workspace.hidden('document.tmp.html')
        self.assertEqual(raised.exception.errno, 5)
        kernel.SetFileAttributesW.assert_not_called()

    def test_windows_attribute_write_failure_is_reported(self):
        kernel = Mock()
        kernel.GetFileAttributesW.return_value = 0x20
        kernel.SetFileAttributesW.return_value = 0
        self.windows_api(kernel)
        with self.assertRaises(OSError) as raised:
            workspace.hidden('document.tmp.html')
        self.assertEqual(raised.exception.errno, 5)
        kernel.SetFileAttributesW.assert_called_once_with('document.tmp.html', 0x22)

    def test_posix_hidden_name_needs_no_windows_attribute_api(self):
        with patch.object(workspace, 'os', SimpleNamespace(name='posix')), \
                patch.object(ctypes, 'WinDLL', create=True) as dll:
            workspace.hidden('.document.tmp.html')
            workspace.hidden('.document.tmp.html', False)
        dll.assert_not_called()

    def test_windows_missing_process_and_permission_denial_are_distinct(self):
        kernel = Mock()
        kernel.OpenProcess.return_value = None
        self.windows_api(kernel)
        with patch.object(ctypes, 'get_last_error', return_value=87):
            self.assertFalse(workspace._alive(123))
        with patch.object(ctypes, 'get_last_error', return_value=5):
            self.assertTrue(workspace._alive(123))
        self.assertEqual(kernel.OpenProcess.call_args_list,
                         [unittest.mock.call(0x1000, False, 123)] * 2)
        kernel.CloseHandle.assert_not_called()

    def test_windows_liveness_checks_always_release_the_process_handle(self):
        kernel = Mock()
        kernel.OpenProcess.return_value = 456
        self.windows_api(kernel)
        for exit_code, expected in ((259, True), (0, False), (1, False)):
            with self.subTest(exit_code=exit_code):
                def read_exit_code(handle, output):
                    self.assertEqual(handle, 456)
                    ctypes.cast(output, ctypes.POINTER(ctypes.c_ulong))[0] = exit_code
                    return 1
                kernel.GetExitCodeProcess.side_effect = read_exit_code
                self.assertEqual(workspace._alive(123), expected)
                kernel.CloseHandle.assert_called_with(456)
        kernel.GetExitCodeProcess.side_effect = None
        kernel.GetExitCodeProcess.return_value = 0
        self.assertTrue(workspace._alive(123))
        kernel.GetExitCodeProcess.side_effect = OSError('API failure')
        with self.assertRaisesRegex(OSError, 'API failure'):
            workspace._alive(123)
        self.assertEqual(kernel.CloseHandle.call_count, 5)

    def test_posix_process_probe_preserves_uncertain_ownership(self):
        kill = Mock()
        with patch.object(workspace, 'os', SimpleNamespace(name='posix', kill=kill)):
            self.assertTrue(workspace._alive(123))
            kill.side_effect = ProcessLookupError()
            self.assertFalse(workspace._alive(123))
            kill.side_effect = PermissionError()
            self.assertTrue(workspace._alive(123))
            kill.side_effect = OSError('unexpected probe failure')
            with self.assertRaisesRegex(OSError, 'unexpected probe failure'):
                workspace._alive(123)
        self.assertEqual(kill.call_args_list, [unittest.mock.call(123, 0)] * 4)

    def test_invalid_owner_never_calls_an_os_api(self):
        kill = Mock()
        with patch.object(workspace, 'os', SimpleNamespace(name='posix', kill=kill)):
            for pid in (0, -1, True, None, '123'):
                with self.subTest(pid=pid), self.assertRaisesRegex(ValueError, 'owner'):
                    workspace._alive(pid)
        kill.assert_not_called()


class WindowsPathPlatformTests(unittest.TestCase):
    def setUp(self):
        # A selected root with Windows syntax, independent of the CI filesystem.
        self.paths = project_paths.ProjectPaths.__new__(project_paths.ProjectPaths)
        self.paths.root = 'C:\\Project'
        self.paths.prefix = 'C:\\Project\\'

    def test_device_stream_and_ambiguous_names_reject_before_filesystem_access(self):
        path_api = Mock(wraps=ntpath)
        with patch.object(project_paths, 'os', SimpleNamespace(name='nt', path=path_api)):
            for value in ('C:relative.html', 'docs.html:secret', 'CON', 'sub/aux.txt',
                          'LPT1.html', 'COM\u00b9.txt', 'CONIN$', 'CONOUT$',
                          'sub/trailing.', 'sub/trailing '):
                with self.subTest(value=value), self.assertRaises(ValueError):
                    self.paths.file(value)
        path_api.abspath.assert_not_called()
        path_api.realpath.assert_not_called()

    def test_windows_root_casing_returns_the_selected_root(self):
        path_api = Mock(wraps=ntpath)
        with patch.object(project_paths, 'os', SimpleNamespace(name='nt', path=path_api)):
            self.assertEqual(self.paths.file('c:\\PROJECT'), self.paths.root)
            self.assertEqual(self.paths.file('.'), self.paths.root)
        path_api.realpath.assert_not_called()
