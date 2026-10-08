"""One passive companion HTML for exact subagent evidence; no sockets or servers."""
from copy import deepcopy
from hashlib import sha256
import json
import os
from pathlib import Path

from .document_format import safe_path
from .protocol import json_loads_strict

FORMAT = 'validated-world-review-work-1'
PREFIX = '<!doctype html>\n<!-- ValidatedWorld temporary review workspace -->\n'
OPEN = '<script type="application/json" id="vw-review-work">\n'
CLOSE = '\n</script>\n'


def encode(value):
    return json.dumps(value, ensure_ascii=True, sort_keys=True, separators=(',', ':')).replace('<', '\\u003c').encode('utf-8')


def digest(value):
    return sha256(encode(value)).hexdigest()


def companion(document):
    document = safe_path(document)
    return document.with_name('.' + document.stem + '.tmp.html')


def work_path(path):
    path = safe_path(path)
    if path.exists() and path.stat().st_nlink != 1:
        raise ValueError('linked temporary HTML; preserve this file')
    return path


def hidden(path, enabled=True):
    if os.name == 'nt':
        import ctypes
        kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        kernel.GetFileAttributesW.argtypes = [ctypes.c_wchar_p]
        kernel.GetFileAttributesW.restype = ctypes.c_uint32
        kernel.SetFileAttributesW.argtypes = [ctypes.c_wchar_p, ctypes.c_uint32]
        attributes = kernel.GetFileAttributesW(str(path))
        if attributes == 0xffffffff or not kernel.SetFileAttributesW(str(path),
                attributes | 2 if enabled else attributes & ~2):
            raise OSError(ctypes.get_last_error(), 'cannot set temporary HTML attributes')


def _alive(pid):
    if type(pid) is not int or pid <= 0:
        raise ValueError('invalid review workspace owner')
    if os.name == 'nt':
        import ctypes
        kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        kernel.OpenProcess.restype = ctypes.c_void_p
        handle = kernel.OpenProcess(0x1000, False, pid)
        if not handle:
            return ctypes.get_last_error() != 87  # Access denial is not proof of exit.
        try:
            code = ctypes.c_ulong()
            kernel.GetExitCodeProcess.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_ulong)]
            if not kernel.GetExitCodeProcess(handle, ctypes.byref(code)):
                return True
            return code.value == 259
        finally:
            kernel.CloseHandle.argtypes = [ctypes.c_void_p]
            kernel.CloseHandle(handle)
    try:
        os.kill(pid, 0)
        return True
    except ProcessLookupError:
        return False
    except PermissionError:
        return True


def load(path):
    path = work_path(path)
    text = path.read_text(encoding='utf-8')
    if not text.startswith(PREFIX) or text.count(OPEN) != 1 or not text.endswith(CLOSE):
        raise ValueError('unrecognized temporary HTML; preserve this file')
    value = json_loads_strict(text.split(OPEN, 1)[1][:-len(CLOSE)])
    if (not isinstance(value, dict) or value.get('format') != FORMAT or
            not isinstance(value.get('projectPath'), str) or
            companion(value['projectPath']) != path or not isinstance(value.get('assignments'), dict)):
        raise ValueError('invalid temporary HTML identity; preserve this file')
    return value


def reset_stale(document):
    """Only recognized, inactive companion state can be removed on a fresh task."""
    path = work_path(companion(document))
    if not path.exists():
        return
    try:
        value = load(path)
    except ValueError:
        # A process may stop after preparing a complete atomic-save candidate.
        # Recognize only exact generated HTML for this selected project.
        from .document_format import parse, render
        try:
            candidate, saved = parse(path), parse(document)
        except (OSError, ValueError):
            raise ValueError(f'unrecognized temporary HTML; preserve this file: {path}') from None
        if candidate.graph.project_id != saved.graph.project_id or path.read_text(encoding='utf-8') != render(candidate):
            raise ValueError(f'unrecognized temporary HTML; preserve this file: {path}')
        path.unlink()
        return
    if _alive(value['ownerPid']):
        raise ValueError('another live author owns the temporary HTML; finish or discard that session')
    path.unlink()


class ReviewWorkspace:
    def __init__(self):
        self.documents = {}

    def _store(self, session, assignment, pages, mode, limit):
        document = safe_path(session.base.path)
        if document.suffix.lower() != '.html':
            raise ValueError('subagent workspace requires an HTML project')
        path = companion(document)
        owner = session.session_id
        entry = self.documents.get(owner)
        if entry is None:
            if path.exists():
                reset_stale(document)
            value = {'format': FORMAT, 'projectPath': str(document), 'ownerPid': os.getpid(),
                     'sessionId': owner, 'baseFingerprint': session.base.state_fingerprint, 'assignments': {}}
            entry = {'path': path, 'value': value, 'lastBytes': None}
            self.documents[owner] = entry
        if entry['lastBytes'] is not None and (not path.exists() or path.read_bytes() != entry['lastBytes']):
            raise ValueError('temporary HTML was changed externally; preserve it')
        manifest = {'mode': mode, 'binding': pages[-1]['binding'], 'intent': session.intent, 'limit': limit,
                    'pageSha256': [digest(page) for page in pages], 'pageCount': len(pages)}
        entry['value']['assignments'][assignment] = {'manifest': manifest, 'pages': pages}
        raw = (PREFIX + '<meta charset="utf-8"><title>Temporary ValidatedWorld review</title>\n' + OPEN).encode() + encode(entry['value']) + CLOSE.encode()
        work_path(path)
        with path.open('xb' if entry['lastBytes'] is None else 'r+b') as stream:
            stream.seek(0); stream.write(raw); stream.truncate(); stream.flush(); os.fsync(stream.fileno())
        entry['lastBytes'] = raw
        hidden(path)
        return {'workspacePath': str(path), 'assignment': assignment, 'binding': deepcopy(manifest['binding']),
                'pageCount': len(pages), 'manifestSha256': digest(manifest)}

    def export(self, app, reference, plan_fingerprint, packet_id, limit):
        review = app.packet_review(reference, plan_fingerprint)
        previous = deepcopy(review.seen)
        review.seen = {}
        pages = []; cursor = None
        try:
            while True:
                page = review.packet(packet_id, limit, cursor)
                pages.append(page); cursor = page['nextCursor']
                if cursor is None: break
        finally:
            review.seen = previous
        return self._store(app.session(reference), packet_id, pages, 'packet', limit)

    def export_affected(self, app, reference, limit):
        session = app.session(reference)
        pages = []; cursor = None
        while True:
            evidence = session.affected(limit, cursor)
            pages.append({'binding': {'reference': deepcopy(reference)}, 'evidence': evidence})
            cursor = evidence['page']['nextCursor']
            if cursor is None: break
        return self._store(session, 'affected', pages, 'affected', limit)

    def accept(self, app, reference, binding, receipt):
        entry = self.documents.get(reference['sessionId'])
        if entry is None:
            raise ValueError('no exported workspace for this proposal')
        if entry['path'].read_bytes() != entry['lastBytes']:
            raise ValueError('temporary HTML changed externally')
        assignment = entry['value']['assignments'].get(binding['packetId'])
        if assignment is None or assignment['manifest']['binding'] != binding:
            raise ValueError('stale or unknown workspace assignment')
        expected = {'manifestSha256': digest(assignment['manifest']),
                    'pageCount': len(assignment['pages']), 'evidenceSha256': digest(assignment['manifest']['pageSha256'])}
        if receipt != expected:
            raise ValueError('incomplete or different review receipt')
        review = app.packet_review(reference, binding['planFingerprint'])
        # Pages use offsets; regenerate the same bounded sequence from the start.
        previous = deepcopy(review.seen); cursor = None
        review.seen.pop(binding['packetFingerprint'], None)
        try:
            for stored in assignment['pages']:
                current = review.packet(binding['packetId'], assignment['manifest']['limit'], cursor)
                if current != stored: raise ValueError('different exact review evidence')
                cursor = current['nextCursor']
        except Exception:
            review.seen = previous
            raise

    def revoke(self, owner=None):
        removed = 0
        for key in list(self.documents):
            if owner is not None and key != owner: continue
            entry = self.documents[key]; path = entry['path']
            if path.exists():
                if path.read_bytes() != entry['lastBytes']:
                    raise ValueError(f'preserving externally changed temporary HTML: {path}')
                path.unlink()
            del self.documents[key]; removed += 1
        return {'removedWorkspaces': removed}

    def close(self):
        self.revoke()


class ReviewReader:
    """Read a selected assignment once in RAM; only bounded pages enter context."""
    def __init__(self, path, assignment):
        self.path = safe_path(path); self.value = load(self.path); self.assignment = assignment
        item = self.value['assignments'].get(assignment)
        if item is None: raise ValueError('unknown review assignment')
        self.manifest = item['manifest']; self.pages = item['pages']; self.seen = set()
        if len(self.pages) != self.manifest['pageCount'] or [digest(p) for p in self.pages] != self.manifest['pageSha256']:
            raise ValueError('incomplete or altered workspace evidence')
        from .document_format import parse
        if parse(self.value['projectPath']).state_fingerprint != self.value['baseFingerprint']:
            raise ValueError('stale saved project')
        for i, page in enumerate(self.pages):
            if page['binding'] != self.manifest['binding']: raise ValueError('different page binding')
            cursor = page['nextCursor'] if self.manifest['mode'] == 'packet' else page['evidence']['page']['nextCursor']
            if (cursor is None) != (i == len(self.pages)-1): raise ValueError('incomplete pagination')
        if self.manifest['mode'] == 'packet' and not self.pages[-1]['allEvidencePresented']:
            raise ValueError('incomplete packet presentation')

    def page(self, index):
        if type(index) is not int or not 0 <= index < len(self.pages): raise ValueError('invalid page index')
        self.seen.add(index)
        return deepcopy(self.pages[index])

    def receipt(self):
        if self.seen != set(range(len(self.pages))): raise ValueError('read every assigned page before returning a receipt')
        current = load(self.path)['assignments'].get(self.assignment)
        if current is None or current['manifest'] != self.manifest: raise ValueError('stale or revoked review assignment')
        return {'manifestSha256': digest(self.manifest), 'pageCount': len(self.pages),
                'evidenceSha256': digest(self.manifest['pageSha256'])}

    def reply(self, result):
        return {'binding': deepcopy(self.manifest['binding']), 'receipt': self.receipt(), 'result': deepcopy(result)}
