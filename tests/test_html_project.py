import dataclasses
import io
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).parents[1] / 'src'))
from validated_world.application import Application, sample_graph
from validated_world.cli import direct_command, _diff, ndjson_loop
from validated_world.document_store import ProjectFiles, Publisher, byte_identity
from validated_world.document_format import HtmlParseError, parse, render
from validated_world.models import Attribute, Edge, EntityKind, Graph, GraphValue, Node, Operation, OperationKind, ReviewDirection
from validated_world.storage import ProjectStore
from validated_world.merge import merge_projects
from validated_world.protocol import operation_dto


class HtmlProjectTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve(); self.folder = self.root / 'project.html'
        self.store = ProjectFiles(); self.addCleanup(self.store.close)
        self.project = self.store.initialize(self.folder, sample_graph())

    def files(self, folder=None):
        root = folder or self.folder
        return {'document.html': root.read_bytes()}

    def edit(self, folder=None, old='The battery lasts for the target duty cycle', new='The battery lasts two duty cycles'):
        path = (folder or self.folder)
        content = path.read_text(encoding='utf-8')
        self.assertIn(old, content); path.write_text(content.replace(old, new), encoding='utf-8', newline='\n')

    @staticmethod
    def present(session):
        cursor = None; items = []
        while True:
            page = session.preview(3, cursor); items.extend(page['reviewPage']['items'])
            cursor = page['reviewPage']['nextCursor']
            if cursor is None: return items

    def proposal(self, app=None, ordinary=False):
        app = app or Application(self.store)
        self.addCleanup(app.close)
        session = app.begin(str(self.folder), self.project.graph.project_id, 'tester', 'Change the battery assumption')
        old = next(n for n in self.project.graph.nodes if n.id == 'battery-assumption')
        op = Operation(OperationKind.REPLACE, EntityKind.NODE, old.id, node=dataclasses.replace(old, text='The battery lasts two duty cycles'))
        session = app.apply(session.reference(), (op,), skip_dependencies=not ordinary)
        if ordinary:
            app.review(session.reference(), [{'nodeId': n['nodeId'], 'kind': 'updated' if n['isDirectChange'] else 'reviewedNoChange'} for n in session.affected_nodes], [n['nodeId'] for n in session.scope_context])
        self.present(session)
        return app, session

    def test_lossless_fields_and_every_scalar_and_metadata(self):
        unusual = ' leading\n\t<>&"\r\nNUL:\x00 C1:\x85 literal:\\u0000\ntrailing '
        attrs = (Attribute('text', GraphValue.text(unusual)), Attribute('integer', GraphValue.integer(-(2**63))), Attribute('decimal', GraphValue.decimal('-0.01')), Attribute('boolean', GraphValue.boolean(False)), Attribute('symbol', GraphValue.symbol('symbol & 日本語')), Attribute('instant', GraphValue.instant('2026-10-04T10:00:00.0000000+00:00')))
        nodes = (Node('purpose', unusual, None, ('A', 'a', '<tag>'), attrs), Node('../../CON:?', 'Child', 'claim'))
        edges = (Edge('parent', '../../CON:?', 'purpose', 'scope-parent', ReviewDirection.NONE, None, (), attrs), Edge('empty rationale', 'purpose', '../../CON:?', 'rel', ReviewDirection.BOTH, '', ('edge-tag',)))
        graph = Graph('graph & ID', 'Human-readable 日本語', 'purpose', nodes, edges)
        db = self.root / 'unusual.vw.db'
        expected = ProjectStore().initialize(db, graph, created_utc='2026-01-01T01:02:03.0000000+00:00', updated_utc='2026-02-01T01:02:03.0000000+00:00')
        destination = self.root / 'unusual.html'
        self.store.export_html(db, destination)
        actual = self.store.import_html(destination, self.root / 'import.vw.db')
        self.assertEqual((actual.graph, actual.created_utc, actual.updated_utc), (expected.graph, expected.created_utc, expected.updated_utc))
        self.assertIn(b'\\u0000', self.files(destination)['document.html'])
        self.assertEqual(self.files(destination)['document.html'].count(b'<script type="application/json" id="vw-record">'), 1)
        self.assertTrue(db.exists())

    def test_noop_roundtrip_is_byte_identical(self):
        before = self.files(); db = self.root / 'copy.vw.db'
        self.store.import_html(self.folder, db); self.store.export_html(db, self.folder)
        self.assertEqual(before, self.files())
        self.assertEqual(self.project.created_utc, parse(self.folder).created_utc)

    def test_generated_document_has_root_first_and_navigation_without_instructional_copy(self):
        source = render(self.project)
        self.assertNotIn('Use browser Find', source)
        self.assertNotIn('Visible record fields are the project source', source)
        self.assertIn('<main id="main"></main>', source)
        self.assertNotIn('<section', source)
        self.assertLess(source.index('id="vw-record"'), source.index('id="vw-viewer"'))
        self.assertEqual(source.count('<script type="application/json"'), 1)
        self.assertEqual(parse(self.folder).graph, self.project.graph)

    def test_viewer_template_is_independent_of_graph_and_data_occurs_once(self):
        claim = '"lowercase first paragraph"\ncontinued on the same paragraph\n\n  second paragraph\r\n\r\nthird paragraph'
        graph = dataclasses.replace(self.project.graph, title='lowercase project', nodes=tuple(dataclasses.replace(n, text=claim) if n.id == 'purpose' else n for n in self.project.graph.nodes))
        project = dataclasses.replace(self.project, graph=graph)
        document = render(project)
        marker = '<script type="application/json" id="vw-record">'
        view, record = document.split(marker)
        data, tail = record.split('</script>', 1)
        old_view, old_record = render(self.project).split(marker)
        old_data, old_tail = old_record.split('</script>', 1)
        self.assertEqual((view, tail), (old_view, old_tail))
        self.assertNotIn('lowercase project', view + tail)
        self.assertEqual(next(n['text'] for n in json.loads(data)['nodes'] if n['id'] == 'purpose'), claim)
        self.assertEqual(document.count('lowercase project'), 1)
        destination = self.root / 'capitalized.html'
        Publisher().publish(project, destination, require_absent=True)
        actual = parse(destination)
        self.assertEqual((actual.graph, actual.created_utc, actual.updated_utc), (project.graph, project.created_utc, project.updated_utc))

    def test_compatible_external_edit_and_presentation_changes_are_parsed(self):
        self.edit()
        content = self.folder.read_text(encoding='utf-8')
        self.folder.write_text(content.replace('max-width: 80ch', 'max-width: 70ch'), encoding='utf-8')
        parsed = self.store.import_html(self.folder, self.root / 'edited.vw.db')
        self.assertIn('two duty cycles', next(n.text for n in parsed.graph.nodes if n.id == 'battery-assumption'))

    def test_import_ignores_and_never_executes_viewer_code(self):
        path = self.folder
        original = path.read_text(encoding='utf-8')
        view, record = original.split('<script type="application/json" id="vw-record">')
        changed = 'The battery lasts two duty cycles'
        path.write_text(original.replace("'use strict';", "throw new Error('viewer deliberately broken');"), encoding='utf-8')
        self.assertEqual(parse(self.folder).graph, self.project.graph)
        path.write_text(view + '<script type="application/json" id="vw-record">' + record.replace('The battery lasts for the target duty cycle', changed), encoding='utf-8')
        self.assertEqual(next(n.text for n in parse(self.folder).graph.nodes if n.id == 'battery-assumption'), changed)

    def test_markup_like_data_stays_passive_and_links_are_local(self):
        malicious = '</script><script>alert("x")</script><img src="https://example.com/a">'
        graph = dataclasses.replace(self.project.graph, nodes=tuple(dataclasses.replace(n, text=malicious) if n.id == 'purpose' else n for n in self.project.graph.nodes))
        project = dataclasses.replace(self.project, graph=graph)
        document = render(project)
        self.assertIn('\\u003c/script\\u003e', document)
        self.assertNotIn('<script>alert', document)
        from html.parser import HTMLParser
        class Links(HTMLParser):
            def __init__(inner): super().__init__(); inner.ids = []; inner.links = []; inner.scripts = []; inner.policy = None; inner.active = False; inner.code = []
            def handle_starttag(inner, tag, attrs):
                attrs = dict(attrs)
                if 'id' in attrs: inner.ids.append(attrs['id'])
                if tag == 'script':
                    inner.scripts.append(attrs)
                    inner.active = attrs.get('id') == 'vw-viewer'
                if tag == 'meta' and attrs.get('http-equiv') == 'Content-Security-Policy': inner.policy = attrs['content']
                if tag == 'a': inner.links.append(attrs['href'])
                self.assertNotIn('src', attrs)
                self.assertFalse(any(name.startswith('on') for name in attrs))
            def handle_data(inner, data):
                if inner.active: inner.code.append(data)
            def handle_endtag(inner, tag):
                if tag == 'script': inner.active = False
        links = Links(); links.feed(document)
        self.assertEqual(len(set(links.ids)), len(links.ids))
        self.assertEqual(links.links, ['#purpose', '#project', '#tags'])
        self.assertEqual([script.get('id') for script in links.scripts], ['vw-record', 'vw-viewer'])
        self.assertEqual(links.scripts[0]['type'], 'application/json')
        import base64, hashlib
        digest = base64.b64encode(hashlib.sha256(''.join(links.code).encode('utf-8')).digest()).decode('ascii')
        self.assertIn("script-src 'sha256-" + digest + "'", links.policy)
        self.assertIn("default-src 'none'", links.policy)
        self.assertNotIn("script-src 'unsafe-inline'", links.policy)
        destination = self.root / 'markup.html'
        Publisher().publish(project, destination, require_absent=True)
        self.assertEqual(parse(destination).graph, graph)

    def test_broken_markup_missing_record_duplicate_field_and_invalid_scalar_fail(self):
        path = self.folder; original = path.read_text(encoding='utf-8')
        mutations = [original.replace('</script>', '', 1), original.replace('id="vw-record"', 'id="missing"'), original.replace('"text":', '"id": "duplicate", "text":'), original.replace('"text":', '"text": invalid, "unused":')]
        for index, changed in enumerate(mutations):
            with self.subTest(index=index):
                path.write_text(changed, encoding='utf-8'); target = self.root / f'failed-{index}.vw.db'
                with self.assertRaises(HtmlParseError): self.store.import_html(self.folder, target)
                self.assertFalse(target.exists())
        path.write_text(original, encoding='utf-8'); path.unlink()
        with self.assertRaises(FileNotFoundError): parse(self.folder)

    def test_readonly_commands_do_not_rewrite_and_folder_diff_merge(self):
        before = self.files()
        for arguments in [['project', 'verify', str(self.folder)], ['project', 'status', str(self.folder)], ['read', 'tag', str(self.folder), 'sample', '--limit', '1']]:
            out, err = io.StringIO(), io.StringIO(); self.assertEqual(direct_command(arguments, out, err), 0, err.getvalue())
        self.assertEqual(before, self.files())
        base = self.root / 'base.html'; ours = self.root / 'ours.html'; theirs = self.root / 'theirs.html'
        for p in (base, ours, theirs): self.store.backup(self.folder, p)
        self.edit(ours)
        self.assertEqual(_diff(str(base), str(ours), 1)['summary']['nodesReplaced'], 1)
        merged = merge_projects(str(base), str(ours), str(theirs))
        self.assertIn('operations', merged)
        with self.assertRaises(FileExistsError): self.store.backup(self.folder, base)


    def test_explicit_db_workflow_retains_authority(self):
        db = self.root / 'authoritative.vw.db'; self.store.import_html(self.folder, db)
        app = Application(self.store); self.addCleanup(app.close)
        session = app.begin(str(db), self.project.graph.project_id, 'tester', 'DB-authoritative project')
        session = app.apply(session.reference(), (Operation(OperationKind.REPLACE, EntityKind.NODE, 'battery-assumption', node=Node('battery-assumption', 'Updated DB claim')),), skip_dependencies=True)
        self.present(session); before = self.files()
        self.assertEqual(app.write(session.reference())['status'], 'written'); self.assertTrue(db.exists()); self.assertEqual(self.files(), before)

    def test_folder_agent_review_is_complete_current_and_fail_closed(self):
        before = self.files(); app, session = self.proposal(ordinary=True)
        session.preview_seen.clear(); session.preview(1)
        allow = {'decision': 'allow', 'summary': 'The reviewed fixture change is coherent.', 'concerns': []}
        with self.assertRaisesRegex(ValueError, 'complete exact proposal evidence'):
            app.record_agent_review(session.reference(), allow)
        self.present(session)
        block = {'decision': 'block', 'summary': 'Fixture review needs clarification.', 'concerns': [{'code': 'needs-clarification', 'message': 'Clarify the new battery duration.', 'citations': [{'entityId': 'battery-assumption'}]}]}
        app.record_agent_review(session.reference(), block)
        self.assertEqual(app.agent_write(session.reference())['status'], 'agentReviewBlocked')
        self.assertEqual(self.files(), before)
        old_reference = session.reference()
        old = next(n for n in self.project.graph.nodes if n.id == 'battery-assumption')
        session = app.apply(old_reference, (Operation(OperationKind.REPLACE, EntityKind.NODE, old.id, node=dataclasses.replace(old, text=old.text + '.')),))
        self.assertEqual(app.agent_write(session.reference())['status'], 'agentReviewBlocked')
        with self.assertRaisesRegex(ValueError, 'stale-'):
            app.record_agent_review(old_reference, allow)
        app.review(session.reference(), [{'nodeId': n['nodeId'], 'kind': 'updated' if n['isDirectChange'] else 'reviewedNoChange'} for n in session.affected_nodes], [n['nodeId'] for n in session.scope_context])
        self.present(session); app.record_agent_review(session.reference(), allow)
        self.assertEqual(app.agent_write(session.reference())['status'], 'written')
        self.assertNotEqual(self.files(), before)

    def test_artifact_checks_use_the_logical_folder_namespace(self):
        import hashlib
        content = b'artifact beside the documentation folder'
        (self.root / 'artifact.txt').write_bytes(content)
        graph = Graph('artifact-folder', 'Artifact folder', 'purpose', (Node('purpose', 'Purpose'), Node('anchor', 'Anchor', 'external-anchor', ('artifact',), (Attribute('artifact.path', GraphValue.text('artifact.txt')), Attribute('artifact.sha256', GraphValue.text(hashlib.sha256(content).hexdigest()))))), (Edge('parent', 'anchor', 'purpose', 'scope-parent'),))
        folder = self.root / 'anchored-docs.html'; self.store.initialize(folder, graph)
        out, err = io.StringIO(), io.StringIO()
        self.assertEqual(direct_command(['artifact', 'check', str(folder), '--allow-root', str(self.root)], out, err), 0, err.getvalue())
        self.assertEqual(json.loads(out.getvalue())['matchedCount'], 1)



    @unittest.skipUnless(os.name == 'nt', 'Windows publication avoids hard-link creation')
    def test_windows_html_workflow_with_hard_link_permission_denied(self):
        from unittest.mock import patch
        before = set(self.root.iterdir())
        destination = self.root / 'garden.html'
        backup = self.root / 'backup.html'
        with patch('validated_world.file_publication.os.link',
                   side_effect=PermissionError('[WinError 5] hard links denied')) as link:
            self.store.initialize(destination, self.project.graph)
            self.assertEqual(self.store.load(destination).graph, self.project.graph)
            self.store.backup(destination, backup)
            self.assertTrue(self.store.verify(backup)['isValid'])
            self.assertIn('CREATE TABLE', self.store.export_sql(destination))
            app, session = self.proposal(ordinary=True)
            self.assertEqual(app.write(session.reference())['status'], 'written')
            self.assertTrue(self.store.verify(self.folder)['isValid'])
            link.assert_not_called()
        self.assertEqual(set(self.root.iterdir()), before | {destination, backup})

    def test_new_html_publication_preserves_destination_created_after_preflight(self):
        destination = self.root / 'competing.html'

        def competing_writer(stage):
            if stage == 'prepared':
                destination.write_bytes(b'human file created during publication')

        with self.assertRaises(FileExistsError):
            Publisher(competing_writer).publish(parse(self.folder), destination, require_absent=True)
        self.assertEqual(destination.read_bytes(), b'human file created during publication')
        self.assertFalse(list(self.root.glob('.*.vw-stage-*')))
        self.assertFalse(list(self.root.glob('.*.vw-lock')))














    def test_replace_is_one_file_and_preserves_unrelated_neighbor(self):
        neighbor = self.root / 'notes.html'; neighbor.write_text('Unrelated documentation')
        project = parse(self.folder)
        self.store.publisher.publish(project, self.folder)
        self.assertEqual(neighbor.read_text(), 'Unrelated documentation')
        self.assertEqual(parse(self.folder).graph, project.graph)

    def test_path_boundaries(self):
        db = self.root / 'x.vw.db'; self.store.import_html(self.folder, db)
        with self.assertRaises(ValueError): self.store.export_html(db, Path.cwd())
        with self.assertRaises(ValueError): self.store.export_html(db, self.root)
        with self.assertRaises(ValueError): Publisher.destination(Path(Path.cwd().anchor))
        with self.assertRaises(ValueError): Publisher.destination(Path.cwd() / '.git' / 'docs.html')
        installation = self.root / 'installed-package'
        (installation / 'src' / 'validated_world').mkdir(parents=True)
        with self.assertRaises(ValueError): Publisher.destination(installation)
        skill = self.root / 'installed-skill'
        (skill / 'scripts').mkdir(parents=True)
        (skill / 'SKILL.md').write_text('Known skill')
        (skill / 'scripts' / 'validated_world.py').write_text('Known launcher')
        with self.assertRaises(ValueError): Publisher.destination(skill)

    def test_ndjson_strict_bool_and_folder_command_parity(self):
        requests = [
            {'version': 1, 'command': 'project.status', 'payload': {'path': str(self.folder)}},
            {'version': 1, 'command': 'change.begin', 'payload': {'path': str(self.folder), 'projectId': self.project.graph.project_id, 'author': 'tester', 'intent': 'read', 'keepWorkingDb': 'true'}},
            {'version': 1, 'command': 'project.import-html', 'payload': {'sourcePath': str(self.folder), 'destinationPath': str(self.root / 'ndjson.vw.db')}},
            {'version': 1, 'command': 'project.export-html', 'payload': {'sourcePath': str(self.root / 'ndjson.vw.db'), 'destinationPath': str(self.root / 'ndjson-folder.html')}},
        ]
        out = io.StringIO(); ndjson_loop(io.StringIO('\n'.join(json.dumps(r) for r in requests)), out, io.StringIO())
        results = [json.loads(line) for line in out.getvalue().splitlines()]
        self.assertEqual(results[0]['payload'], self.store.status(self.folder)); self.assertEqual(results[1]['status'], 'error')
        self.assertEqual(results[2]['status'], 'ok'); self.assertEqual(results[3]['status'], 'ok'); self.assertEqual(self.files(), self.files(self.root / 'ndjson-folder.html'))


if __name__ == '__main__': unittest.main()
