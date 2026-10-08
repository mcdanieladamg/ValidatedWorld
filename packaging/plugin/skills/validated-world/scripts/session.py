"""Bundled retained session: full replies in RAM, references managed by code."""
import json
from pathlib import Path
from queue import Queue, Empty
import subprocess
import sys
from threading import Thread

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).parent))
def response(result, command):
    """Reject payload-only wrappers before accessing protocol payload fields."""
    if not isinstance(result, dict) or not {'version', 'command', 'status', 'payload'} <= result.keys():
        raise ValueError(f'{command}: request must return the complete NDJSON response; '
                         'not a payload-only or compact wrapper')
    if type(result['version']) is not int or result['version'] != 1 or result['command'] != command:
        raise ValueError(f'{command}: different protocol response')
    if result['status'] != 'ok':
        raise RuntimeError(result)
    if command in {'change.show', 'change.affected', 'change.review-packet', 'change.review-result'} and not isinstance(result['payload'], dict):
        raise ValueError(f'{command}: requires an object payload')
    return result



def compact(result):
    """Display status/counts/references only; preserve the original separately."""
    value = result['payload']
    if result['status'] != 'ok':
        return result
    if not isinstance(value, dict):
        return {'command': result['command'], 'status': result['status'], 'count': len(value)}
    keys = ('status', 'reference', 'readiness', 'affected', 'planFingerprint',
            'packetCount', 'coverageCount', 'allAllowed', 'isValid', 'path')
    summary = {key: value[key] for key in keys if key in value}
    if isinstance(value.get('affected'), dict):
        summary['affected'] = {key: val for key, val in value['affected'].items() if key != 'omissions'}
        summary['omissionCount'] = len(value['affected'].get('omissions', []))
    if 'readiness' in summary:
        summary['readiness'] = {key: val for key, val in value['readiness'].items() if key != 'blockers'}
    if 'items' in value:
        page = value['page'] if isinstance(value.get('page'), dict) else value
        summary.update(itemCount=len(value['items']), totalCount=page.get('totalCount'), hasMore=page.get('nextCursor') is not None)
    if 'project' in value and isinstance(value['project'], dict):
        summary['project'] = {key: value['project'][key] for key in
                              ('path', 'projectId', 'nodeCount', 'edgeCount', 'stateFingerprint') if key in value['project']}
    return {'command': result['command'], 'status': result['status'], **summary}



class Session:
    def __init__(self, python_executable, project_folder, launcher=None):
        launcher = launcher or str(Path(__file__).with_name('validated_world.py'))
        self.folder = str(Path(project_folder).resolve())
        self.responses = []; self.reference = None; self.plan_fingerprint = None
        self.process = subprocess.Popen([python_executable, '-B', '-X', 'utf8', '-u', launcher, 'ndjson'],
            cwd=self.folder, stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, encoding='utf-8')
        self.queue = Queue()
        def read():
            for line in self.process.stdout: self.queue.put(line)
            self.queue.put(None)
        Thread(target=read, daemon=True).start()
        try:
            if self.request('host.help')['payload']['projectRoot'] != self.folder:
                raise ValueError('selected project folder mismatch')
        except Exception:
            self.close(force=True)
            raise

    def request(self, command, payload=None):
        payload = dict(payload or {})
        if command.startswith('change.') and command != 'change.begin':
            if self.reference is None: raise ValueError('begin a proposal first')
            if command in {'change.show', 'change.affected'}:
                payload.setdefault('session', {k:self.reference[k] for k in ('projectId','sessionId')})
            else:
                payload.setdefault('reference', self.reference)
            if command in {'change.review-export', 'change.review-packet'}:
                payload.setdefault('planFingerprint', self.plan_fingerprint)
        self.process.stdin.write(json.dumps({'version':1,'command':command,'payload':payload})+'\n')
        self.process.stdin.flush()
        try: line = self.queue.get(timeout=30)
        except Empty:
            self.close(force=True); raise TimeoutError('ValidatedWorld did not reply within 30 seconds')
        if line is None: raise RuntimeError('ValidatedWorld exited; unfinished proposal is lost')
        result = json.loads(line); self.responses.append(result)
        response(result, command)
        value = result['payload']
        if isinstance(value, dict):
            if 'reference' in value: self.reference = value['reference']
            if 'planFingerprint' in value: self.plan_fingerprint = value['planFingerprint']
            if command in {'change.apply','change.patch','change.review','change.review-context'}:
                self.plan_fingerprint = None
        return result

    def call(self, command, **payload):
        return compact(self.request(command,payload))

    def begin(self, path, intent, author='project-agent'):
        status = self.request('project.status',{'path':path})['payload']
        return self.call('change.begin',path=path,projectId=status['projectId'],author=author,intent=intent)

    def assignments(self, limit=5, refinements=None):
        payload={'limit':limit}
        if refinements is not None: payload['refinements']=refinements
        plan=self.request('change.review-plan',payload)['payload']
        items=plan['items'][:];cursor=plan['nextCursor']
        while cursor:
            page=self.request('change.review-plan',{'limit':limit,'cursor':cursor})['payload']
            items.extend(page['items']);cursor=page['nextCursor']
        return sorted({item['packetId'] for item in items}-{'synthesis'})

    def export(self, assignment, limit=5):
        if assignment == 'affected': return self.request('change.affected-export',{'limit':limit})['payload']
        return self.request('change.review-export',{'packetId':assignment,'limit':limit})['payload']

    def export_batch(self, assignments=None, limit=5):
        """Finish all serialized writes before starting parallel read-only workers."""
        assignments = self.assignments(limit) if assignments is None else assignments
        return [self.export(assignment, limit) for assignment in assignments]

    def submit(self, worker_reply):
        if set(worker_reply) != {'binding','receipt','result'}:
            raise ValueError('reviewer returns exactly binding, receipt and result')
        return self.call('change.review-result',**worker_reply)

    def close(self, force=False):
        try:
            if self.process.poll() is None:
                if force: self.process.terminate()
                else: self.request('host.exit')
        finally:
            try: self.process.wait(timeout=30)
            except subprocess.TimeoutExpired:
                self.process.kill();self.process.wait(timeout=30)
            self.process.stdin.close();self.process.stdout.close();self.responses.clear()
