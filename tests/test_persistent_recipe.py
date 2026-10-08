"""Execute the short bundled session recipe and complete file-based reviews."""
import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import unittest


class PersistentRecipeTests(unittest.TestCase):
    def test_documented_helper_manages_references_complete_responses_and_save(self):
        root_repo=Path(__file__).resolve().parents[1];skill=root_repo/'skills/validated-world'
        sys.path.insert(0,str(skill/'scripts'))
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary).resolve();path=root/'docs-vw.html'
            namespace={'skill_directory':str(skill),'python_executable':sys.executable,'project_folder':str(root)}
            text=(skill/'references/persistent-io.md').read_text(encoding='utf-8')
            recipe=re.search(r'```python\n(.*?)\n```',text,re.S).group(1)
            exec(recipe,namespace);session=namespace['session']
            try:
                with self.assertRaisesRegex(RuntimeError,'unknown member'):session.request('host.help',{'unexpected':True})
                self.assertEqual(session.responses[-1]['status'],'error')
                session.call('project.init',path=str(path),projectId='garden',title='JardÃ­n æ—¥æœ¬èªž',purposeNodeId='purpose',purposeText='Plan three beds.')
                session.begin(str(path),'Plan four beds.')
                node=session.request('read.node',{'path':str(path),'entityId':'purpose'})['payload'];node['text']='Plan four beds â€” JardÃ­n æ—¥æœ¬èªž.'
                session.call('change.apply',operations={'operations':[{'kind':'replace','entityKind':'node','entityId':'purpose','node':node,'edge':None}]})
                old=session.reference.copy()
                evidence=session.request('change.affected',{'limit':1})['payload']
                from session import compact,response
                summary=compact(session.responses[-1]);self.assertEqual(summary['totalCount'],1);self.assertFalse(summary['hasMore'])
                with self.assertRaisesRegex(ValueError,'complete NDJSON'):response(evidence,'change.affected')
                session.call('change.review',dispositions=[{'nodeId':'purpose','kind':'updated'}],presentedContextNodeIds=[])
                self.assertNotEqual(session.reference,old)
                with self.assertRaisesRegex(RuntimeError,'stale'):session.request('change.patch',{'reference':old,'operations':{'operations':[]}})
                self.assertEqual(session.call('change.agent-write')['status'],'agentReviewBlocked')
                exports=session.export_batch(limit=1)
                # A purpose-only proposal has global synthesis evidence and no branches.
                self.assertEqual(exports,[])
                for descriptor in exports+[None]:
                    if descriptor is None:descriptor=session.export('synthesis',limit=1)
                    assignment=descriptor['assignment']
                    code="import sys,json;sys.path.insert(0,sys.argv[1]);from reviewer import ReviewReader;r=ReviewReader(sys.argv[2],sys.argv[3]);[r.page(i) for i in range(len(r.pages))];print(json.dumps(r.reply({'decision':'allow','summary':'Offline helper fixture only.','citations':[{'entityId':'purpose'}],'concerns':[],'questions':[]})))"
                    reply=json.loads(subprocess.check_output([sys.executable,'-B','-X','utf8','-I','-S','-c',code,str(skill/'scripts'),descriptor['workspacePath'],assignment],timeout=30))
                    session.submit(reply)
                self.assertEqual(session.call('change.agent-write')['status'],'written')
                self.assertTrue(session.request('project.verify',{'path':str(path)})['payload']['isValid'])
                self.assertEqual(session.request('read.node',{'path':str(path),'entityId':'purpose'})['payload'],node)
            finally:session.close()
            self.assertEqual(session.process.returncode,0);self.assertEqual(session.responses,[])
            self.assertEqual(list(root.iterdir()),[path])

    def test_audited_public_workflow_denies_sockets_and_all_other_files(self):
        import importlib.util
        path=Path(__file__).resolve().parents[1]/'eng/verify_skill_workflow.py'
        spec=importlib.util.spec_from_file_location('workspace_fixture',path);module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
        module.verify(path.parent.parent/'skills/validated-world',packet_workspace=True)
