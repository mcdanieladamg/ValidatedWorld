import copy
import dataclasses
from hashlib import sha256
import json
from pathlib import Path
import sqlite3
import sys
import tempfile
import unittest
from unittest.mock import patch
from validated_world.review_workspace import ReviewReader, companion, digest, reset_stale

sys.path.insert(0, str(Path(__file__).parents[1] / 'src'))
from validated_world.application import Application, sample_graph
from validated_world.document_store import ProjectFiles, Publisher
from validated_world.document_format import parse
from validated_world.models import EntityKind, Node, Operation, OperationKind


class MemoryWorkflowTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()
        self.path = self.root / 'design.html'
        self.store = ProjectFiles(); self.addCleanup(self.store.close)
        self.project = self.store.initialize(self.path, sample_graph())
        self.app = Application(self.store); self.addCleanup(self.app.close)

    def proposal(self):
        session = self.app.begin(str(self.path), self.project.graph.project_id, 'test', 'Review the revised battery duration')
        old = next(n for n in self.project.graph.nodes if n.id == 'battery-assumption')
        session = self.app.apply(session.reference(), (Operation(OperationKind.REPLACE, EntityKind.NODE, old.id, node=dataclasses.replace(old, text='The battery lasts two duty cycles')),))
        self.app.review(session.reference(), [{'nodeId': n['nodeId'], 'kind': 'updated' if n['isDirectChange'] else 'reviewedNoChange'} for n in session.affected_nodes], [n['nodeId'] for n in session.scope_context])
        return session

    def plan_export(self, session, packet_id=None):
        plan = self.app.review_plan(session.reference())
        review = session.packet_review
        pid = packet_id or next(p for p in review.ownership if p != 'synthesis')
        export = self.app.review_export(session.reference(), plan['planFingerprint'], pid, 1)
        return review, pid, export

    def result(self):
        return {'decision': 'allow', 'summary': 'Offline fixture of complete review.', 'citations': [{'entityId': 'battery-assumption'}], 'concerns': [], 'questions': []}

    def test_create_read_sql_backup_review_save_discard_without_temporary_files(self):
        with patch('tempfile.mkdtemp', side_effect=AssertionError('no temp allocation')), patch('tempfile.TemporaryDirectory', side_effect=AssertionError('no temp allocation')), patch('os.link', side_effect=AssertionError('no links')):
            other = self.root / 'other.html'; backup = self.root / 'backup.html'
            self.store.initialize(other, self.project.graph)
            self.assertTrue(self.store.verify(other)['isValid'])
            self.assertIn('CREATE TABLE', self.store.export_sql(other))
            self.store.backup(other, backup)
            session = self.proposal()
            connection = self.store.workspaces[str(self.path)].connection
            self.assertEqual(connection.execute('pragma database_list').fetchone()['file'], '')
            self.assertEqual(connection.execute('pragma temp_store').fetchone()[0], 2)
            self.assertEqual(connection.execute("select count(*) from sqlite_master where type='table'").fetchone()[0], 4)
            session.preview(100)
            self.assertEqual(self.app.agent_write(session.reference())['status'], 'agentReviewBlocked')
            self.app.record_agent_review(session.reference(), {'decision': 'allow', 'summary': 'Offline fixture.', 'concerns': []})
            self.assertEqual(self.app.agent_write(session.reference())['status'], 'written')
            with self.assertRaises(sqlite3.ProgrammingError): connection.execute('select 1')
            self.project = self.store.load(self.path)
            self.app.discard(self.proposal().reference())
            self.assertEqual(set(self.root.iterdir()), {self.path, other, backup})

    def test_reads_and_session_start_need_no_directory_write(self):
        with patch('pathlib.Path.mkdir', side_effect=PermissionError('read-only parent')):
            self.assertTrue(self.store.verify(self.path)['isValid'])
            self.assertIn('CREATE TABLE', self.store.export_sql(self.path))
            self.app.discard(self.proposal().reference())
        self.assertEqual(list(self.root.iterdir()), [self.path])

    def test_discard_and_abnormal_process_loss_leave_last_saved_html(self):
        before = self.path.read_bytes(); session = self.proposal()
        self.app.close()
        self.assertEqual(self.path.read_bytes(), before)
        self.assertEqual(self.store.workspaces, {})
        self.assertEqual(list(self.root.iterdir()), [self.path])

    def test_ram_transaction_rollback_preserves_proposal_and_file(self):
        def fail(stage):
            if stage == 'nodes-written': raise RuntimeError('fixture transaction failure')
        self.store.engine._write_fault = fail
        session = self.proposal(); session.preview(100); before = self.path.read_bytes()
        self.assertEqual(self.app.write(session.reference())['status'], 'failed')
        self.assertEqual(self.path.read_bytes(), before)
        self.assertEqual(self.store.engine._load_connection(self.store.workspaces[str(self.path)].connection, str(self.path)).state_fingerprint, self.project.state_fingerprint)
        self.store.engine._write_fault = None
        self.assertEqual(self.app.write(session.reference())['status'], 'written')

    def test_failed_atomic_replacement_preserves_saved_html(self):
        session=self.proposal(); session.preview(100); before=self.path.read_bytes()
        with patch('validated_world.document_store.os.replace',side_effect=PermissionError('denied replacement')):
            result=self.app.write(session.reference())
        self.assertEqual(result['status'],'failed')
        self.assertEqual(self.path.read_bytes(),before)
        self.assertEqual(self.app.sessions,{})
        self.assertEqual(list(self.root.iterdir()),[self.path])

    def test_render_failure_and_stale_bytes_do_not_touch_destination(self):
        before = self.path.read_bytes()
        with patch('validated_world.document_format.render', return_value='invalid HTML'):
            with self.assertRaises(ValueError): Publisher().publish(self.project, self.path)
        self.assertEqual(self.path.read_bytes(), before)
        with self.assertRaisesRegex(RuntimeError, 'stale-document'):
            Publisher().publish(self.project, self.path, expected_bytes='wrong')
        self.assertEqual(self.path.read_bytes(), before)

    def test_concurrent_sessions_reject_changed_base_without_lock_files(self):
        session = self.proposal(); session.preview(100)
        other = Application(); self.addCleanup(other.close)
        second = other.begin(str(self.path), self.project.graph.project_id, 'other', 'Independent proposal')
        self.assertEqual(list(self.root.iterdir()), [self.path])
        self.assertEqual(self.app.write(session.reference())['status'], 'written')
        with self.assertRaisesRegex(ValueError, 'stale-base'): other.session(second.reference())

    def test_workspace_full_receipts_guarded_save_and_cleanup(self):
        session=self.proposal();plan=self.app.review_plan(session.reference());review=session.packet_review
        for pid in sorted(set(review.ownership)-{'synthesis'})+['synthesis']:
            export=self.app.review_export(session.reference(),plan['planFingerprint'],pid,1)
            self.assertEqual(set(self.root.iterdir()),{self.path,companion(self.path)})
            reader=ReviewReader(export['workspacePath'],export['assignment'])
            with self.assertRaisesRegex(ValueError,'every'):reader.receipt()
            reader.page(0)
            with self.assertRaises(ValueError):self.app.review_result(session.reference(),export['binding'],self.result())
            for i in range(1,len(reader.pages)):reader.page(i)
            reply=reader.reply(self.result())
            bad=dict(reply['receipt'],pageCount=1)
            with self.assertRaisesRegex(ValueError,'receipt'):self.app.review_result(session.reference(),reply['binding'],reply['result'],bad)
            self.assertEqual(self.app.agent_write(session.reference())['status'],'agentReviewBlocked')
            self.app.review_result(session.reference(),**reply)
        self.assertEqual(self.app.agent_write(session.reference())['status'],'written')
        self.assertEqual(list(self.root.iterdir()),[self.path])
        with self.assertRaises(FileNotFoundError):reader.receipt()

    def test_affected_workspace_and_mutation_revocation(self):
        session=self.proposal();ex=self.app.affected_export(session.reference(),1)
        reader=ReviewReader(ex['workspacePath'],'affected');items=[]
        for i in range(len(reader.pages)):items.extend(reader.page(i)['evidence']['items'])
        self.assertEqual(len(items),session.affected(100)['page']['totalCount'])
        self.assertGreater(len(reader.pages),2)
        self.app.review_context(session.reference(),['retention-policy'])
        self.assertFalse(companion(self.path).exists())
        with self.assertRaises(FileNotFoundError):reader.receipt()
        self.assertEqual(self.app.agent_write(session.reference())['status'],'agentReviewBlocked')

    def test_changed_workspace_preserved_unknown_live_and_stale_cleanup(self):
        session=self.proposal();_,_,ex=self.plan_export(session)
        path=companion(self.path)
        with self.assertRaisesRegex(ValueError,'live'):reset_stale(self.path)
        original=path.read_bytes()
        with path.open('r+b') as stream:stream.write(b'human data');stream.truncate()
        with self.assertRaisesRegex(ValueError,'preserv'):self.app.cleanup_review_exports(session.session_id)
        self.assertEqual(path.read_bytes(),b'human data')
        with path.open('r+b') as stream:stream.write(original);stream.truncate()
        with patch('validated_world.review_workspace._alive',return_value=False):reset_stale(self.path)
        self.assertFalse(path.exists())
        self.app.cleanup_review_exports(session.session_id)
        path.write_text('human file')
        with self.assertRaisesRegex(ValueError,'unrecognized'):reset_stale(self.path)
        self.assertEqual(path.read_text(),'human file');path.unlink()

    def test_export_failure_restores_seen_without_extra_files(self):
        session=self.proposal();self.app.review_plan(session.reference());review=session.packet_review
        pid=next(p for p in review.ownership if p!='synthesis');seen=copy.deepcopy(review.seen);original=review.packet
        def fail(pid,limit,cursor):
            if cursor is not None:raise ValueError('fixture page failure')
            return original(pid,limit,cursor)
        with patch.object(review,'packet',side_effect=fail):
            with self.assertRaisesRegex(ValueError,'fixture page'):self.app.review_export(session.reference(),review.plan_fingerprint,pid,1)
        self.assertEqual(review.seen,seen);self.assertEqual(self.app.review_workspace.documents,{})
        self.assertEqual(list(self.root.iterdir()),[self.path])

    def test_unicode_ids_are_data_and_external_saved_change_rejects_reader(self):
        from validated_world.models import Edge
        session=self.proposal();nid='../../cafÃƒÂ©/Ã°Å¸Â¦Å '
        self.app.apply(session.reference(),session.operations+(Operation(OperationKind.ADD,EntityKind.NODE,nid,node=Node(nid,'Untrusted claim')),Operation(OperationKind.ADD,EntityKind.EDGE,'unicode-parent',edge=Edge('unicode-parent',nid,'scope-power','scope-parent'))))
        self.app.review(session.reference(),[{'nodeId':n['nodeId'],'kind':'updated' if n['isDirectChange'] else 'reviewedNoChange'} for n in session.affected_nodes],[n['nodeId'] for n in session.scope_context])
        self.app.review_plan(session.reference());pid=next(pid for pid,ords in session.packet_review.ownership.items() if any(session.packet_review.evidence[o].get('affectedNode',{}).get('nodeId')==nid for o in ords))
        _,_,ex=self.plan_export(session,pid);reader=ReviewReader(ex['workspacePath'],pid)
        self.assertIn(nid,json.dumps(reader.pages,ensure_ascii=False));self.assertEqual(companion(self.path).name,'.design.tmp.html')
        text=self.path.read_text(encoding='utf-8');self.path.write_text(text.replace('The battery lasts for the target duty cycle','An external edit'),encoding='utf-8')
        with self.assertRaisesRegex(ValueError,'stale'):ReviewReader(ex['workspacePath'],pid)
        self.path.write_text(text,encoding='utf-8');self.app.discard(session.reference())

    def test_abandoned_complete_save_candidate_is_recognized(self):
        from validated_world.document_format import render
        companion(self.path).write_text(render(self.project),encoding='utf-8')
        reset_stale(self.path);self.assertFalse(companion(self.path).exists())

    def test_foreign_companion_blocks_save_without_overwriting(self):
        session=self.proposal();session.preview(100)
        path=companion(self.path);path.write_text('user data')
        before=self.path.read_bytes()
        result=self.app.write(session.reference())
        self.assertEqual(result['status'],'failed')
        self.assertEqual(path.read_text(),'user data')
        self.assertEqual(self.path.read_bytes(),before)
        path.unlink()

    def test_corrupt_pages_and_invalid_assignment_are_rejected(self):
        from validated_world.review_workspace import load, encode, PREFIX, OPEN, CLOSE
        session=self.proposal();_,pid,ex=self.plan_export(session)
        path=companion(self.path);original=path.read_bytes();value=load(path)
        with self.assertRaisesRegex(ValueError,'unknown'):ReviewReader(path,'missing')
        reader=ReviewReader(path,pid)
        with self.assertRaisesRegex(ValueError,'index'):reader.page(-1)
        value['assignments'][pid]['pages'][0]['binding']['packetId']='altered'
        with path.open('r+b') as stream:
            stream.write((PREFIX+OPEN).encode()+encode(value)+CLOSE.encode());stream.truncate()
        with self.assertRaisesRegex(ValueError,'altered'):ReviewReader(path,pid)
        with path.open('r+b') as stream:stream.write(original);stream.truncate()
        self.app.discard(session.reference())

    def test_linked_companion_is_preserved(self):
        import os
        target=self.root/'human.html';target.write_text('private user data')
        path=companion(self.path)
        try:os.link(target,path)
        except OSError as exc:self.skipTest(str(exc))
        try:
            with self.assertRaisesRegex(ValueError,'linked'):reset_stale(self.path)
            self.assertEqual(target.read_text(),'private user data')
        finally:path.unlink()

    def test_receipt_rejects_changed_binding_file_and_live_evidence(self):
        session=self.proposal();_,pid,ex=self.plan_export(session)
        reader=ReviewReader(ex['workspacePath'],pid)
        for i in range(len(reader.pages)):reader.page(i)
        reply=reader.reply(self.result());workspace=self.app.review_workspace
        entry=workspace.documents[session.session_id]
        binding=dict(reply['binding'],packetId='unknown')
        with self.assertRaisesRegex(ValueError,'unknown'):workspace.accept(self.app,session.reference(),binding,reply['receipt'])
        original=entry['path'].read_bytes()
        with entry['path'].open('r+b') as stream:stream.write(b'changed');stream.truncate()
        with self.assertRaisesRegex(ValueError,'externally'):workspace.accept(self.app,session.reference(),reply['binding'],reply['receipt'])
        with entry['path'].open('r+b') as stream:stream.write(original);stream.truncate()
        review=session.packet_review;previous=copy.deepcopy(review.seen);original_packet=review.packet
        def changed(*args):
            page=original_packet(*args);page['intent']='different';return page
        with patch.object(review,'packet',side_effect=changed):
            with self.assertRaisesRegex(ValueError,'exact'):workspace.accept(self.app,session.reference(),reply['binding'],reply['receipt'])
        self.assertEqual(review.seen,previous)
        self.app.discard(session.reference())
        with self.assertRaisesRegex(ValueError,'no exported'):workspace.accept(self.app,session.reference(),reply['binding'],reply['receipt'])

    def test_assignment_revocation_and_workspace_identity_validation(self):
        from validated_world.review_workspace import load, encode, PREFIX, OPEN, CLOSE, _alive
        session=self.proposal();_,pid,ex=self.plan_export(session)
        path=companion(self.path);value=load(path);original=path.read_bytes()
        reader=ReviewReader(path,pid)
        for i in range(len(reader.pages)):reader.page(i)
        value['assignments'].pop(pid)
        with path.open('r+b') as stream:stream.write((PREFIX+OPEN).encode()+encode(value)+CLOSE.encode());stream.truncate()
        with self.assertRaisesRegex(ValueError,'revoked'):reader.receipt()
        value['projectPath']=str(self.root/'wrong.html')
        with path.open('r+b') as stream:stream.write((PREFIX+OPEN).encode()+encode(value)+CLOSE.encode());stream.truncate()
        with self.assertRaisesRegex(ValueError,'identity'):load(path)
        with path.open('r+b') as stream:stream.write(original);stream.truncate()
        with self.assertRaisesRegex(ValueError,'owner'):_alive(0)
        self.app.discard(session.reference())

    def test_parallel_readers_share_frozen_companion(self):
        from concurrent.futures import ThreadPoolExecutor
        session=self.proposal()
        purpose=next(n for n in self.project.graph.nodes if n.id==self.project.graph.purpose_node_id)
        self.app.apply(session.reference(),session.operations+(Operation(OperationKind.REPLACE,EntityKind.NODE,purpose.id,node=dataclasses.replace(purpose,text=purpose.text+' Review every component together.')),))
        self.app.review(session.reference(),[{'nodeId':n['nodeId'],'kind':'updated' if n['isDirectChange'] else 'reviewedNoChange'} for n in session.affected_nodes],[n['nodeId'] for n in session.scope_context])
        plan=self.app.review_plan(session.reference())
        branches=sorted(set(session.packet_review.ownership)-{'synthesis'})
        exports=[self.app.review_export(session.reference(),plan['planFingerprint'],pid,1) for pid in branches]
        self.assertGreater(len(exports),1)
        self.assertEqual(len({ex['workspacePath'] for ex in exports}),1)
        frozen=companion(self.path).read_bytes()
        def read(ex):
            reader=ReviewReader(ex['workspacePath'],ex['assignment'])
            for i in range(len(reader.pages)):reader.page(i)
            result=self.result();result['citations']=[{'entityId':'purpose'}]
            return reader.reply(result)
        with ThreadPoolExecutor(max_workers=len(exports)) as pool:replies=list(pool.map(read,exports))
        self.assertEqual(companion(self.path).read_bytes(),frozen)
        for reply in replies:self.app.review_result(session.reference(),**reply)
        ex=self.app.review_export(session.reference(),plan['planFingerprint'],'synthesis',1)
        self.app.review_result(session.reference(),**read(ex))
        self.assertEqual(self.app.agent_write(session.reference())['status'],'written')
        self.assertEqual(list(self.root.iterdir()),[self.path])

    @unittest.skipUnless(sys.platform=='win32','Windows file attributes')
    def test_companion_is_hidden_and_saved_document_is_visible(self):
        session=self.proposal();self.plan_export(session)
        self.assertTrue(companion(self.path).stat().st_file_attributes & 2)
        self.app.discard(session.reference())
        session=self.proposal();session.preview(100)
        self.assertEqual(self.app.write(session.reference())['status'],'written')
        self.assertFalse(self.path.stat().st_file_attributes & 2)
