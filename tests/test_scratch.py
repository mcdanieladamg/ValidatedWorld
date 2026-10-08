import dataclasses
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

from validated_world.application import Application, sample_graph
from validated_world.document_store import ProjectFiles, DocumentLock
from validated_world.models import EntityKind, Operation, OperationKind
from validated_world.scratch import ResponseTransport, scratch_path, read_responses, recovery_path, Lease, encode_record, record_recovery, staging, recovering


class ScratchTests(unittest.TestCase):
    def test_reader_waits_for_publication_and_holds_partial_response(self):
        with tempfile.TemporaryDirectory() as directory:
            document = Path(directory).resolve() / 'docs-vw.html'
            transport = ResponseTransport(document)
            try:
                offset = read_responses(transport.path)['nextOffset']
                with Lease(document, 'scratch-io'):
                    self.assertEqual(read_responses(transport.path, offset), {'responses': [], 'nextOffset': offset, 'busy': True})
                response = encode_record({'command': 'host.help', 'payload': {}}).encode()
                with transport.path.open('ab') as stream: stream.write(response[:-1])
                self.assertEqual(read_responses(transport.path, offset)['nextOffset'], offset)
                with transport.path.open('ab') as stream: stream.write(response[-1:])
                self.assertEqual(read_responses(transport.path, offset)['responses'][0]['command'], 'host.help')
                with self.assertRaises(ValueError): read_responses(transport.path, -1)
                with self.assertRaises(ValueError): read_responses(transport.path, transport.path.stat().st_size + 1)
                with self.assertRaises(ValueError): read_responses(document)
            finally: transport.close()

    def test_replaced_transport_and_unknown_records_are_never_removed(self):
        with tempfile.TemporaryDirectory() as directory:
            document = Path(directory).resolve() / 'docs-vw.html'
            transport = ResponseTransport(document)
            moved = transport.path.with_name('original')
            transport.path.rename(moved)
            transport.path.write_bytes(b'human data')
            with self.assertRaisesRegex(ValueError, 'replaced'): transport.close()
            self.assertEqual(transport.path.read_bytes(), b'human data')
            transport.path.unlink()
            moved.rename(transport.path)
            with transport.path.open('ab') as stream: stream.write(encode_record({'unknown': True}).encode())
            before = transport.path.read_bytes()
            with self.assertRaisesRegex(ValueError, 'unknown'): ResponseTransport(document)
            self.assertEqual(transport.path.read_bytes(), before)

    def test_recovery_rejects_wrong_fingerprint_and_preserves_old_document(self):
        with tempfile.TemporaryDirectory() as directory:
            document = Path(directory).resolve() / 'docs-vw.html'
            store = ProjectFiles()
            project = store.initialize(document, sample_graph())
            before = document.read_bytes()
            with self.assertRaisesRegex(ValueError, 'invalid working'):
                record_recovery(document, {'invalid': True})
                store.retry_export(scratch_path(document))
            scratch_path(document).unlink()
            store.begin_workspace(document)
            import base64
            from validated_world.document_store import byte_identity
            data = {'source': str(document), 'baseline': byte_identity(document), 'keep': False,
                    'fingerprint': 'wrong', 'database': base64.b64encode(store.memory.snapshots[str(document)]).decode()}
            record_recovery(document, data)
            store.close()
            with self.assertRaisesRegex(ValueError, 'database changed'): ProjectFiles().retry_export(scratch_path(document))
            with self.assertRaisesRegex(FileExistsError, 'preserve unpublished'):
                store.publisher.publish(project, document)
            self.assertEqual(document.read_bytes(), before)
            self.assertTrue(scratch_path(document).exists())

    def test_failure_after_first_publication_still_leaves_complete_graph(self):
        with tempfile.TemporaryDirectory() as directory:
            document = Path(directory).resolve() / 'docs-vw.html'
            transport = ResponseTransport(document)
            store = ProjectFiles()
            try:
                original = __import__('validated_world.document_format', fromlist=['write_stage']).write_stage
                def fail_clean(project, path, **kwargs):
                    if not kwargs.get('append'): raise OSError('clean publication denied')
                    return original(project, path, **kwargs)
                with patch('validated_world.document_store.html.write_stage', side_effect=fail_clean):
                    result = store.initialize(document, sample_graph())
                self.assertEqual(store.load(document).graph, result.graph)
                self.assertTrue(store.last_warnings)
                self.assertEqual(read_responses(transport.path)['responses'], [])
            finally: transport.close()

    def test_recovery_sync_failure_rolls_back_memory_and_preserves_source(self):
        with tempfile.TemporaryDirectory() as directory:
            document = Path(directory).resolve() / 'docs-vw.html'
            store = ProjectFiles()
            base = store.initialize(document, sample_graph())
            store.begin_workspace(document)
            from validated_world.canonical import state_fingerprint
            from validated_world.validation import project_graph
            node = next(n for n in base.graph.nodes if n.id == 'battery-assumption')
            operations = (Operation(OperationKind.REPLACE, EntityKind.NODE, node.id, node=dataclasses.replace(node, text='New duration')),)
            proposed, _ = project_graph(base.graph, operations)
            try:
                with patch('validated_world.document_store.record_recovery', side_effect=OSError('snapshot sync denied')):
                    with self.assertRaisesRegex(OSError, 'snapshot sync denied'):
                        store.write(str(document), base.graph.project_id, base.state_fingerprint, state_fingerprint(proposed), operations)
                self.assertEqual(store.memory.load(str(document)).graph, base.graph)
                self.assertEqual(store.load(document).graph, base.graph)
                self.assertFalse(scratch_path(document).exists())
            finally: store.close()

    def test_one_hidden_file_for_live_review_publication_and_concurrent_readers(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            document = root / 'custom.html'
            transport = ResponseTransport(document)
            store = ProjectFiles()
            observed = []
            store.publisher.fault = lambda stage: observed.append(set(p.name for p in root.iterdir()))
            try:
                store.initialize(document, sample_graph())
                self.assertEqual(scratch_path(document), root / '.custom.tmp.html')
                self.assertEqual(read_responses(transport.path)['responses'], [])
                transport.write(json.dumps({'command': 'read.node', 'payload': {'text': 'Ω'}}) + '\n')
                read = read_responses(transport.path)
                self.assertEqual(read['responses'][0]['payload']['text'], 'Ω')
                self.assertEqual(read_responses(transport.path, read['nextOffset'])['responses'], [])
                app = Application(store)
                session = app.begin(str(document), 'technical-project', 'test', 'Inspect then discard')
                self.assertTrue(ProjectFiles().verify(document)['isValid'])
                self.assertEqual(set(p.name for p in root.iterdir()), {'custom.html', '.custom.tmp.html'})
                app.discard(session.reference())
                self.assertTrue(all(files <= {'custom.html', '.custom.tmp.html'} for files in observed))
                if os.name == 'nt':
                    self.assertTrue(transport.path.stat().st_file_attributes & 2)
                    self.assertFalse(document.stat().st_file_attributes & 2)
            finally: store.close(); transport.close()
            self.assertEqual(set(p.name for p in root.iterdir()), {'custom.html'})

    def test_competing_process_cannot_remove_active_transport_and_readers_remain_available(self):
        with tempfile.TemporaryDirectory() as directory:
            document = Path(directory).resolve() / 'docs-vw.html'
            with_transport = ResponseTransport(document)
            try:
                code = 'from validated_world.scratch import ResponseTransport; import sys; ResponseTransport(sys.argv[1])'
                child = subprocess.run([sys.executable, '-c', code, str(document)], capture_output=True, text=True)
                self.assertNotEqual(child.returncode, 0)
                self.assertIn('busy', child.stderr)
                self.assertTrue(with_transport.path.exists())
                self.assertEqual(read_responses(with_transport.path)['responses'], [])
            finally: with_transport.close()

    def test_complete_stale_transport_is_reclaimed_but_unknown_or_recovery_data_is_preserved(self):
        with tempfile.TemporaryDirectory() as directory:
            document = Path(directory).resolve() / 'docs-vw.html'
            first = ResponseTransport(document)
            first.close(clean=False)
            second = ResponseTransport(document)
            second.record_recovery({'protected': 'snapshot'})
            second.close()
            before = second.path.read_bytes()
            with self.assertRaisesRegex(ValueError, 'retry-export'): ResponseTransport(document)
            self.assertEqual(second.path.read_bytes(), before)
            second.path.unlink()
            second.path.write_bytes(b'human content')
            with self.assertRaises(ValueError): ResponseTransport(document)
            self.assertEqual(second.path.read_bytes(), b'human content')

    def test_publication_failure_retains_same_scratch_snapshot_and_fresh_retry_publishes_it(self):
        with tempfile.TemporaryDirectory() as directory:
            document = Path(directory).resolve() / 'docs-vw.html'
            transport = ResponseTransport(document)
            store = ProjectFiles()
            project = store.initialize(document, sample_graph())
            original = document.read_bytes()
            app = Application(store)
            session = app.begin(str(document), project.graph.project_id, 'test', 'Change battery duration')
            node = next(n for n in project.graph.nodes if n.id == 'battery-assumption')
            session = app.apply(session.reference(), (Operation(OperationKind.REPLACE, EntityKind.NODE, node.id,
                node=dataclasses.replace(node, text='The battery lasts two duty cycles')),), skip_dependencies=True)
            cursor = None
            while True:
                page = session.preview(5, cursor)
                cursor = page['reviewPage']['nextCursor']
                if cursor is None: break
            def fail(stage):
                if stage == 'prepared': raise PermissionError('publication denied')
            store.publisher.fault = fail
            result = app.write(session.reference())
            self.assertEqual(result['status'], 'unpublished', result)
            self.assertEqual(Path(result['workingDbPath']), transport.path)
            self.assertEqual(document.read_bytes(), original)
            self.assertTrue(recovery_path(transport.path))
            transport.close()
            recovered = ProjectFiles().retry_export(result['workingDbPath'])
            self.assertEqual(next(n.text for n in recovered.graph.nodes if n.id == node.id), 'The battery lasts two duty cycles')
            self.assertFalse(transport.path.exists())

    def test_memory_transaction_rollback_preserves_committed_base(self):
        with tempfile.TemporaryDirectory() as directory:
            document = Path(directory).resolve() / 'docs-vw.html'
            store = ProjectFiles(write_fault=lambda stage: (_ for _ in ()).throw(RuntimeError('rollback')) if stage == 'before-commit' else None)
            base = store.initialize(document, sample_graph())
            store.begin_workspace(document)
            from validated_world.canonical import state_fingerprint
            from validated_world.validation import project_graph
            node = next(n for n in base.graph.nodes if n.id == 'battery-assumption')
            operations = (Operation(OperationKind.REPLACE, EntityKind.NODE, node.id, node=dataclasses.replace(node, text='New duration')),)
            proposed, _ = project_graph(base.graph, operations)
            try:
                with self.assertRaisesRegex(RuntimeError, 'rollback'):
                    store.write(str(document), base.graph.project_id, base.state_fingerprint, state_fingerprint(proposed), operations)
                self.assertEqual(store.memory.load(str(document)).graph, base.graph)
                self.assertFalse(scratch_path(document).exists())
            finally: store.close()

    def test_hard_crash_during_staging_keeps_durable_snapshot_for_fresh_retry(self):
        with tempfile.TemporaryDirectory() as directory:
            document = Path(directory).resolve() / 'docs-vw.html'
            store = ProjectFiles()
            store.initialize(document, sample_graph())
            original = document.read_bytes()
            code = '''import dataclasses, os, sys
from validated_world.application import Application
from validated_world.document_store import ProjectFiles
from validated_world.scratch import ResponseTransport
from validated_world.models import Operation, OperationKind, EntityKind
document = sys.argv[1]
transport = ResponseTransport(document)
store = ProjectFiles(publication_fault=lambda stage: os._exit(72) if stage == 'staged' else None)
base = store.load(document)
app = Application(store)
session = app.begin(document, base.graph.project_id, 'test', 'Crash recovery test')
node = next(n for n in base.graph.nodes if n.id == 'battery-assumption')
session = app.apply(session.reference(), (Operation(OperationKind.REPLACE, EntityKind.NODE, node.id,
    node=dataclasses.replace(node, text='Reviewed crash recovery duration')),), skip_dependencies=True)
cursor = None
while True:
    page = session.preview(10, cursor)
    cursor = page['reviewPage']['nextCursor']
    if cursor is None: break
app.write(session.reference())
'''
            child = subprocess.run([sys.executable, '-c', code, str(document)], capture_output=True, text=True)
            self.assertEqual(child.returncode, 72, child.stderr)
            self.assertEqual(document.read_bytes(), original)
            temporary = scratch_path(document)
            self.assertTrue(recovery_path(temporary))
            with self.assertRaisesRegex(ValueError, 'retry-export'): ResponseTransport(document)
            result = ProjectFiles().retry_export(temporary)
            self.assertEqual(next(n.text for n in result.graph.nodes if n.id == 'battery-assumption'), 'Reviewed crash recovery duration')
            self.assertFalse(temporary.exists())
