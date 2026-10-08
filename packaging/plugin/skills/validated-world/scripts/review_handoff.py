"""Exact, passive review files and compact controller output; stdlib only."""
from copy import deepcopy
import gzip
import hashlib
import json
import os
from pathlib import Path
import secrets
import sys


def _json(value):
    return json.dumps(value, ensure_ascii=True, sort_keys=True, separators=(',', ':')).encode('utf-8')


def _hash(raw):
    return hashlib.sha256(raw).hexdigest()


def _load(raw):
    def pairs(items):
        result = {}
        for key, value in items:
            if key in result:
                raise ValueError('duplicate JSON member')
            result[key] = value
        return result
    return json.loads(raw, object_pairs_hook=pairs)


def _safe(path):
    path = Path(os.path.abspath(path))
    for ancestor in (path, *path.parents):
        junction = getattr(ancestor, 'is_junction', lambda: False)()
        if os.name == 'nt' and ancestor.exists():
            junction = junction or bool(ancestor.lstat().st_file_attributes & 0x400)
        if ancestor.is_symlink() or junction:
            raise ValueError('linked review paths are unsupported')
    return path


class ReviewFiles:
    """Own only files created by this controller under its selected project."""
    def __init__(self, project_root):
        # Launch already confirms the project root. Check filesystem links when
        # exporting, not as an extra startup preflight on restricted hosts.
        self.root = Path(os.path.abspath(project_root))
        self.base = self.root / '.vw-review'
        self.owned = {}
        self.base_created = False

    def _export(self, pages, mode, binding):
        _safe(self.base)
        if not self.base.exists():
            self.base.mkdir()
            self.base_created = True
        if not self.base.is_dir():
            raise ValueError('review handoff location is not a directory')
        directory = self.base / ('review-' + secrets.token_hex(16))
        directory.mkdir()
        files = {}
        self.owned[str(directory)] = files
        entries = []
        for index, page in enumerate(pages):
            filename = f'page-{index:06d}.json.gz'
            raw = gzip.compress(_json(page), mtime=0)
            path = directory / filename
            with path.open('xb') as stream:
                stream.write(raw)
            files[filename] = _hash(raw)
            entries.append({'index': index, 'sha256': files[filename]})
        manifest = {'version': 1, 'mode': mode, 'binding': binding, 'pages': entries}
        raw = _json(manifest)
        path = directory / 'manifest.json'
        with path.open('xb') as stream:
            stream.write(raw)
        files[path.name] = _hash(raw)
        return {'manifestPath': str(path), 'manifestSha256': files[path.name],
                'binding': deepcopy(binding), 'mode': mode, 'pageCount': len(pages)}

    def packet(self, request, reference, plan_fingerprint, packet_id, limit=5):
        pages = []; cursor = None; binding = None
        while True:
            payload = {'reference': reference, 'planFingerprint': plan_fingerprint,
                       'packetId': packet_id, 'limit': limit}
            if cursor is not None:
                payload['cursor'] = cursor
            page = request('change.review-packet', payload)['payload']
            if binding is None:
                binding = page['binding']
            elif binding != page['binding']:
                raise ValueError('packet binding changed')
            pages.append(page)
            cursor = page['nextCursor']
            if cursor is None:
                if not page['allEvidencePresented']:
                    raise ValueError('incomplete packet presentation')
                return self._export(pages, 'packet', binding)

    def affected(self, request, reference, limit=5):
        pages = []; cursor = None
        session = {k: reference[k] for k in ('projectId', 'sessionId')}
        def check_reference():
            current = request('change.show', {'session': session})['payload']['reference']
            if current != reference:
                raise ValueError('affected evidence changed')
        check_reference()
        while True:
            payload = {'session': session, 'limit': limit}
            if cursor is not None:
                payload['cursor'] = cursor
            page = request('change.affected', payload)['payload']
            pages.append(page)
            cursor = page['page']['nextCursor']
            if cursor is None:
                check_reference()
                return self._export(pages, 'affected', {'reference': reference})

    def submit(self, request, handoff, worker):
        # The host supplies the actual unchanged worker reply. This is no proof
        # of worker identity or semantic understanding, just exact file binding.
        directory = str(Path(handoff['manifestPath']).parent)
        if directory not in self.owned or self.owned[directory].get('manifest.json') != handoff['manifestSha256']:
            raise ValueError('handoff is not owned by this controller')
        expected = receipt(handoff['manifestPath'], handoff['manifestSha256'])
        if (not isinstance(worker, dict) or set(worker) != {'binding', 'receipt', 'result'} or
                worker['binding'] != handoff['binding'] or worker['receipt'] != expected):
            raise ValueError('worker returned an incomplete or different evidence receipt')
        if handoff['mode'] != 'packet':
            raise ValueError('authoring evidence is not terminal packet approval')
        return request('change.review-result', {'reference': handoff['binding']['reference'],
                        'binding': worker['binding'], 'result': worker['result']})

    def close(self):
        warnings = []
        for name, files in list(self.owned.items()):
            directory = Path(name)
            try:
                _safe(directory)
                if directory.parent != self.base:
                    raise ValueError('unexpected cleanup location')
                for filename, digest in files.items():
                    path = _safe(directory / filename)
                    if path.exists():
                        if _hash(path.read_bytes()) != digest:
                            raise ValueError(f'changed file preserved: {path}')
                        path.unlink()
                directory.rmdir()  # Unknown entries prevent removal; never recurse.
                del self.owned[name]
            except (OSError, ValueError) as exc:
                warnings.append(f'{directory}: {exc}')
        if self.base_created and not self.owned:
            try:
                _safe(self.base).rmdir()
                self.base_created = False
            except (OSError, ValueError) as exc:
                warnings.append(f'{self.base}: {exc}')
        return warnings


def _manifest(path, expected_hash):
    path = _safe(path)
    if path.name != 'manifest.json' or path.parent.parent.name != '.vw-review':
        raise ValueError('not a review handoff manifest')
    raw = path.read_bytes()
    if _hash(raw) != expected_hash:
        raise ValueError('manifest hash mismatch')
    manifest = _load(raw)
    if (not isinstance(manifest, dict) or set(manifest) != {'version', 'mode', 'binding', 'pages'} or
            type(manifest['version']) is not int or manifest['version'] != 1 or manifest['mode'] not in {'packet', 'affected'} or
            not isinstance(manifest['binding'], dict) or not isinstance(manifest['pages'], list) or
            not manifest['pages']):
        raise ValueError('invalid review manifest')
    for index, entry in enumerate(manifest['pages']):
        if (not isinstance(entry, dict) or set(entry) != {'index', 'sha256'} or
                type(entry['index']) is not int or entry['index'] != index or
                not isinstance(entry['sha256'], str) or len(entry['sha256']) != 64 or
                any(char not in '0123456789abcdef' for char in entry['sha256'])):
            raise ValueError('invalid review page entry')
    return path, manifest


def read_page(path, expected_hash, index):
    path, manifest = _manifest(path, expected_hash)
    if type(index) is not int or index < 0 or index >= len(manifest['pages']):
        raise ValueError('invalid review page index')
    page_path = _safe(path.parent / f'page-{index:06d}.json.gz')
    raw = page_path.read_bytes()
    if _hash(raw) != manifest['pages'][index]['sha256']:
        raise ValueError('review page hash mismatch')
    page = _load(gzip.decompress(raw))
    if manifest['mode'] == 'packet' and page.get('binding') != manifest['binding']:
        raise ValueError('review page binding mismatch')
    return page


def receipt(path, expected_hash):
    _, manifest = _manifest(path, expected_hash)
    for index in range(len(manifest['pages'])):
        read_page(path, expected_hash, index)
    # Bind every page without returning an unbounded list into the author's
    # conversation. The manifest retains the individual hashes for readers.
    hashes = [entry['sha256'] for entry in manifest['pages']]
    return {'manifestSha256': expected_hash, 'pageCount': len(hashes),
            'evidenceSha256': _hash(_json({'manifestSha256': expected_hash, 'pageSha256': hashes}))}


def compact(result):
    """Display status/counts/references only; preserve the original separately."""
    value = result['payload']
    if result['status'] != 'ok':
        return result
    if not isinstance(value, dict):
        return {'command': result['command'], 'status': result['status'], 'count': len(value)}
    keys = ('status', 'reference', 'readiness', 'affected', 'planFingerprint',
            'packetCount', 'coverageCount', 'decision', 'allAllowed', 'isValid', 'path')
    summary = {key: value[key] for key in keys if key in value}
    if isinstance(value.get('affected'), dict):
        summary['affected'] = {key: val for key, val in value['affected'].items() if key != 'omissions'}
        summary['omissionCount'] = len(value['affected'].get('omissions', []))
    if 'readiness' in summary:
        summary['readiness'] = {key: val for key, val in value['readiness'].items() if key != 'blockers'}
    if 'items' in value:
        page = value['page'] if isinstance(value.get('page'), dict) else value
        summary.update(itemCount=len(value['items']), totalCount=page.get('totalCount'), hasMore=bool(page.get('nextCursor')))
    if 'project' in value and isinstance(value['project'], dict):
        summary['project'] = {key: value['project'][key] for key in
                              ('path', 'projectId', 'nodeCount', 'edgeCount', 'stateFingerprint') if key in value['project']}
    return {'command': result['command'], 'status': result['status'], **summary}


def main():
    try:
        command, path, expected_hash, *rest = sys.argv[1:]
        if command == 'inspect' and not rest:
            _, manifest = _manifest(path, expected_hash)
            result = {'mode': manifest['mode'], 'binding': manifest['binding'], 'pageCount': len(manifest['pages'])}
        elif command == 'page' and len(rest) == 1:
            result = read_page(path, expected_hash, int(rest[0]))
        elif command == 'receipt' and not rest:
            result = receipt(path, expected_hash)
        else:
            raise ValueError('use inspect|page|receipt MANIFEST SHA256 [PAGE_INDEX]')
        print(_json(result).decode('utf-8'))
        return 0
    except (ValueError, OSError, EOFError) as exc:
        print(f'review_handoff: {exc}', file=sys.stderr)
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
