"""Exercise native lease contracts on every host without requiring a second OS."""
from contextlib import contextmanager, ExitStack
import os
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

from validated_world import scratch


class ScratchLeaseTests(unittest.TestCase):
    @contextmanager
    def windows_api(self):
        kernel = SimpleNamespace(**{
            name: Mock(return_value=result) for name, result in {
                'CreateMutexW': 42, 'WaitForSingleObject': 0,
                'ReleaseMutex': 1, 'CloseHandle': 1,
                'GetFileAttributesW': 32, 'SetFileAttributesW': 1,
            }.items()
        })
        windows_os = SimpleNamespace(**{**vars(os), 'name': 'nt'})
        with tempfile.TemporaryDirectory() as directory, ExitStack() as stack:
            stack.enter_context(patch.object(scratch, 'os', windows_os))
            stack.enter_context(patch.object(scratch.ctypes, 'WinDLL', return_value=kernel, create=True))
            stack.enter_context(patch.object(scratch.ctypes, 'get_last_error', return_value=5, create=True))
            stack.enter_context(patch.object(scratch.ctypes, 'WinError', side_effect=lambda code: OSError(code, 'native API denied'), create=True))
            yield Path(directory).resolve() / 'design.html', kernel

    def test_failed_mutex_creation_does_not_register_or_close_an_invalid_handle(self):
        with self.windows_api() as (document, kernel):
            kernel.CreateMutexW.return_value = 0
            lease = scratch.Lease(document)
            with self.assertRaisesRegex(OSError, 'native API denied'):
                lease.acquire()
            self.assertIsNone(lease.handle)
            self.assertNotIn(lease.key, scratch.Lease.active)
            kernel.WaitForSingleObject.assert_not_called()
            kernel.CloseHandle.assert_not_called()

    def test_failed_mutex_wait_closes_the_handle_and_permits_a_retry(self):
        for status, error in ((0x102, RuntimeError), (0xffffffff, OSError)):
            with self.subTest(status=status), self.windows_api() as (document, kernel):
                lease = scratch.Lease(document)
                kernel.WaitForSingleObject.return_value = status
                with self.assertRaises(error):
                    lease.acquire(wait=True)
                kernel.WaitForSingleObject.assert_called_once_with(42, 5000)
                kernel.CloseHandle.assert_called_once_with(42)
                self.assertIsNone(lease.handle)
                self.assertNotIn(lease.key, scratch.Lease.active)
                kernel.WaitForSingleObject.return_value = 0
                try:
                    self.assertIs(lease.acquire(), lease)
                finally:
                    lease.close()
                self.assertEqual(kernel.CloseHandle.call_count, 2)

    def test_abandoned_mutex_is_acquired_but_still_rejects_reentrant_writers(self):
        with self.windows_api() as (document, kernel):
            kernel.WaitForSingleObject.return_value = 0x80
            lease = scratch.Lease(document, 'scratch-io')
            competitor = scratch.Lease(document, 'scratch-io')
            try:
                lease.acquire()
                kernel.WaitForSingleObject.assert_called_once_with(42, 0)
                with self.assertRaisesRegex(RuntimeError, 'busy'):
                    competitor.acquire()
                self.assertEqual(kernel.CreateMutexW.call_count, 1)
            finally:
                lease.close()
            with competitor:
                self.assertIn(competitor.key, scratch.Lease.active)
            self.assertNotIn(competitor.key, scratch.Lease.active)

    def test_failed_mutex_release_closes_handle_and_clears_process_ownership(self):
        with self.windows_api() as (document, kernel):
            lease = scratch.Lease(document).acquire()
            kernel.ReleaseMutex.return_value = 0
            with self.assertRaisesRegex(OSError, 'native API denied'):
                lease.close()
            kernel.CloseHandle.assert_called_once_with(42)
            self.assertIsNone(lease.handle)
            self.assertNotIn(lease.key, scratch.Lease.active)
            lease.close()
            self.assertEqual(kernel.CloseHandle.call_count, 1)
            kernel.ReleaseMutex.return_value = 1
            with scratch.Lease(document):
                self.assertIn(lease.key, scratch.Lease.active)

    def test_hiding_and_unhiding_preserve_other_windows_attributes(self):
        with self.windows_api() as (document, kernel):
            scratch.hide(document)
            kernel.SetFileAttributesW.assert_called_once_with(str(document), 34)
            kernel.GetFileAttributesW.return_value = 34
            scratch.hide(document, hidden=False)
            kernel.SetFileAttributesW.assert_called_with(str(document), 32)

    def test_unknown_windows_attributes_are_not_overwritten(self):
        with self.windows_api() as (document, kernel):
            kernel.GetFileAttributesW.return_value = 0xffffffff
            with self.assertRaisesRegex(OSError, 'native API denied'):
                scratch.hide(document)
            kernel.SetFileAttributesW.assert_not_called()

    def test_failed_windows_attribute_write_is_reported(self):
        with self.windows_api() as (document, kernel):
            kernel.SetFileAttributesW.return_value = 0
            with self.assertRaisesRegex(OSError, 'native API denied'):
                scratch.hide(document)

    @contextmanager
    def posix_api(self, *, uid=1234, mode=0o40700):
        # Only the scratch module sees the simulated OS. Path and filesystem I/O
        # continue to use the real host, including on Windows.
        posix_os = SimpleNamespace(**{**vars(os), 'name': 'posix', 'getuid': lambda: 1234})
        fcntl = SimpleNamespace(LOCK_EX=2, LOCK_NB=4, flock=Mock())
        streams = []
        def fdopen(descriptor, mode):
            stream = os.fdopen(descriptor, mode)
            streams.append(stream)
            return stream
        posix_os.fdopen = fdopen
        with tempfile.TemporaryDirectory() as directory, ExitStack() as stack:
            stack.enter_context(patch.object(scratch, 'os', posix_os))
            stack.enter_context(patch.dict(sys.modules, {'fcntl': fcntl}))
            lease = scratch.Lease(Path(directory).resolve() / 'design.html')
            lease.path = Path(directory).resolve() / lease.key
            private_directory = Mock()
            private_directory.stat.return_value = SimpleNamespace(st_uid=uid, st_mode=mode)
            safe_path = scratch.safe_path
            stack.enter_context(patch.object(scratch, 'safe_path', side_effect=lambda path:
                private_directory if path == lease.path.parent else safe_path(path)))
            try:
                yield lease, fcntl, streams
            finally:
                lease.close()

    def test_posix_rejects_foreign_or_public_lock_directories_before_opening(self):
        for uid, mode in ((9999, 0o40700), (1234, 0o40755)):
            with self.subTest(uid=uid, mode=mode), self.posix_api(uid=uid, mode=mode) as (lease, fcntl, streams):
                with self.assertRaisesRegex(PermissionError, 'not private'):
                    lease.acquire()
                self.assertFalse(lease.path.exists())
                self.assertEqual(streams, [])
                fcntl.flock.assert_not_called()
                self.assertNotIn(lease.key, scratch.Lease.active)

    def test_posix_lock_timeout_closes_descriptor_and_permits_a_retry(self):
        for wait in (False, True):
            with self.subTest(wait=wait), self.posix_api() as (lease, fcntl, streams):
                fcntl.flock.side_effect = BlockingIOError('owned by another process')
                with patch.object(scratch.time, 'monotonic', side_effect=[10, 15]), patch.object(scratch.time, 'sleep') as sleep:
                    with self.assertRaisesRegex(RuntimeError, 'busy'):
                        lease.acquire(wait=wait)
                    sleep.assert_not_called()
                self.assertTrue(streams[0].closed)
                self.assertIsNone(lease.file)
                self.assertNotIn(lease.key, scratch.Lease.active)
                fcntl.flock.side_effect = None
                lease.acquire()
                self.assertIn(lease.key, scratch.Lease.active)

    def test_posix_wait_retries_contention_then_releases_without_unlinking(self):
        with self.posix_api() as (lease, fcntl, streams):
            fcntl.flock.side_effect = [BlockingIOError('busy'), None]
            with patch.object(scratch.time, 'monotonic', side_effect=[10, 10.1]), patch.object(scratch.time, 'sleep') as sleep:
                lease.acquire(wait=True)
                sleep.assert_called_once_with(.01)
            self.assertEqual(fcntl.flock.call_count, 2)
            fcntl.flock.assert_called_with(streams[0], fcntl.LOCK_EX | fcntl.LOCK_NB)
            identity = lease.path.stat().st_ino
            lease.close()
            self.assertTrue(streams[0].closed)
            self.assertNotIn(lease.key, scratch.Lease.active)
            self.assertEqual(lease.path.stat().st_ino, identity)
            self.assertTrue(lease.path.exists())

    def test_posix_lock_error_closes_descriptor_without_leaving_process_ownership(self):
        with self.posix_api() as (lease, fcntl, streams):
            fcntl.flock.side_effect = OSError('lock denied')
            with self.assertRaisesRegex(RuntimeError, 'busy') as raised:
                lease.acquire()
            self.assertIsInstance(raised.exception.__cause__, OSError)
            self.assertTrue(streams[0].closed)
            self.assertIsNone(lease.file)
            self.assertNotIn(lease.key, scratch.Lease.active)
