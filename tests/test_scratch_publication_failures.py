"""Publication failures must preserve recovery bytes and release scratch leases."""
import dataclasses
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from validated_world.application import sample_graph
from validated_world import document_format
from validated_world.memory_store import MemoryStore
from validated_world.scratch import (
    encode_record, Lease, MARKER, ResponseTransport, scratch_path,
)


class ScratchPublicationFailureTests(unittest.TestCase):
    def assert_stage_rollback(self, failure, append):
        with tempfile.TemporaryDirectory() as directory:
            document = Path(directory).resolve() / 'design.html'
            project = MemoryStore().initialize(str(document), sample_graph())
            document.write_text(document_format.render(project), encoding='utf-8')
            original = document.read_bytes()
            stage = scratch_path(document)
            recovery = encode_record({'temporary': MARKER, 'document': str(document),
                                      'recovery': {'protected': 'snapshot'}}).encode()
            if append:
                stage.write_bytes(recovery)
            with failure(project), self.assertRaisesRegex((OSError, ValueError), 'denied|verification failed'):
                document_format.write_stage(project, stage, append=append)
            self.assertEqual(document.read_bytes(), original)
            if append:
                self.assertEqual(stage.read_bytes(), recovery)
            else:
                self.assertFalse(stage.exists())

    def test_render_and_sync_failures_preserve_recovery_or_remove_owned_new_stage(self):
        failures = {
            'render': lambda project: patch.object(document_format, 'render', side_effect=ValueError('render denied')),
            'sync': lambda project: patch('os.fsync', side_effect=OSError('sync denied')),
        }
        for name, failure in failures.items():
            for append in (False, True):
                with self.subTest(failure=name, append=append):
                    self.assert_stage_rollback(failure, append)

    def test_parse_failure_and_changed_round_trip_preserve_recovery_or_remove_new_stage(self):
        failures = {
            'parse': lambda project: patch.object(document_format, 'parse', side_effect=ValueError('parse denied')),
            'changed': lambda project: patch.object(document_format, 'parse', return_value=dataclasses.replace(
                project, updated_utc='2001-01-01T00:00:00.0000000+00:00')),
        }
        for name, failure in failures.items():
            for append in (False, True):
                with self.subTest(failure=name, append=append):
                    self.assert_stage_rollback(failure, append)

    def test_unknown_header_wrong_document_and_interrupted_stage_are_preserved(self):
        with tempfile.TemporaryDirectory() as directory:
            document = Path(directory).resolve() / 'design.html'
            path = scratch_path(document)
            cases = {
                'unrecognized': encode_record({'temporary': 'unknown', 'document': str(document)}),
                'another document': encode_record({'temporary': MARKER, 'document': str(document.with_name('other.html'))}),
                'interrupted HTML staging': encode_record({'temporary': MARKER, 'document': str(document)}) + '<html>unfinished',
            }
            for message, contents in cases.items():
                with self.subTest(message=message):
                    path.write_text(contents, encoding='utf-8', newline='\n')
                    original = path.read_bytes()
                    with self.assertRaisesRegex(ValueError, message):
                        ResponseTransport(document)
                    self.assertEqual(path.read_bytes(), original)
                    # A failed constructor must not prevent inspection/recovery
                    # by the next owner in this process.
                    with Lease(document, 'scratch'):
                        self.assertEqual(path.read_bytes(), original)
