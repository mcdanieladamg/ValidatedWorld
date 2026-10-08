"""Exact review messages and compact output; all controller state stays in RAM.

This helper does not deliver objects to another agent. The host must provide a
complete message route within actual context capacity, or a reachable optional
packet endpoint. Hash receipts establish bytes, never identity or understanding.
"""
from copy import deepcopy
import hashlib
import json


def _hash(value):
    raw = json.dumps(value, ensure_ascii=True, sort_keys=True, separators=(',', ':')).encode('utf-8')
    return hashlib.sha256(raw).hexdigest()


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


def receipt(message):
    """Verify every complete page; call only after reading all assigned evidence."""
    manifest = message['manifest']
    if (manifest['version'] != 1 or manifest['mode'] not in {'packet', 'affected'} or
            not message['responses'] or len(message['responses']) != len(manifest['pageSha256'])):
        raise ValueError('incomplete evidence message')
    command = 'change.review-packet' if manifest['mode'] == 'packet' else 'change.affected'
    for index, result in enumerate(message['responses']):
        value = response(result, command)['payload']
        if _hash(result) != manifest['pageSha256'][index]:
            raise ValueError('evidence page hash mismatch')
        if manifest['mode'] == 'packet' and value['binding'] != manifest['binding']:
            raise ValueError('evidence page binding mismatch')
        cursor = value['nextCursor'] if manifest['mode'] == 'packet' else value['page']['nextCursor']
        if (cursor is None) != (index == len(message['responses']) - 1):
            raise ValueError('incomplete or reordered evidence pages')
    if manifest['mode'] == 'packet' and not value['allEvidencePresented']:
        raise ValueError('incomplete packet presentation')
    return {'manifestSha256': _hash(manifest), 'pageCount': len(message['responses']),
            'evidenceSha256': _hash(manifest['pageSha256'])}


class ReviewMemory:
    """Retain full responses; export only when an actual message route is chosen."""
    def __init__(self):
        self.messages = {}

    def _retain(self, responses, mode, binding):
        message = {'manifest': {'version': 1, 'mode': mode, 'binding': deepcopy(binding),
                               'pageSha256': [_hash(result) for result in responses]},
                   'responses': deepcopy(responses)}
        proof = receipt(message)
        self.messages[proof['manifestSha256']] = message
        return {'mode': mode, 'binding': deepcopy(binding), **proof}

    def packet(self, request, reference, plan_fingerprint, packet_id, limit=5):
        responses = []; cursor = None; binding = None
        while True:
            payload = {'reference': reference, 'planFingerprint': plan_fingerprint,
                       'packetId': packet_id, 'limit': limit}
            if cursor is not None:
                payload['cursor'] = cursor
            result = response(request('change.review-packet', payload), 'change.review-packet')
            value = result['payload']
            if binding is None:
                binding = value['binding']
            elif binding != value['binding']:
                raise ValueError('packet binding changed')
            if (binding['reference'] != reference or binding['planFingerprint'] != plan_fingerprint or
                    binding['packetId'] != packet_id):
                raise ValueError('different packet binding')
            responses.append(result)
            cursor = value['nextCursor']
            if cursor is None:
                return self._retain(responses, 'packet', binding)

    def affected(self, request, reference, limit=5):
        responses = []; cursor = None
        session = {k: reference[k] for k in ('projectId', 'sessionId')}
        def check_reference():
            current = response(request('change.show', {'session': session}), 'change.show')['payload']['reference']
            if current != reference:
                raise ValueError('affected evidence changed; retain the live session and current reference')
        check_reference()
        while True:
            payload = {'session': session, 'limit': limit}
            if cursor is not None:
                payload['cursor'] = cursor
            result = response(request('change.affected', payload), 'change.affected')
            responses.append(result)
            cursor = result['payload']['page']['nextCursor']
            if cursor is None:
                check_reference()
                return self._retain(responses, 'affected', {'reference': reference})

    def message(self, descriptor):
        """A complete copy for explicit host delivery; never routine display."""
        message = self.messages[descriptor['manifestSha256']]
        if descriptor != {'mode': message['manifest']['mode'], 'binding': message['manifest']['binding'], **receipt(message)}:
            raise ValueError('different evidence descriptor')
        return deepcopy(message)

    def submit(self, request, descriptor, worker):
        message = self.message(descriptor)
        if (not isinstance(worker, dict) or set(worker) != {'binding', 'receipt', 'result'} or
                worker['binding'] != descriptor['binding'] or worker['receipt'] != receipt(message)):
            raise ValueError('worker returned an incomplete or different evidence receipt')
        if descriptor['mode'] != 'packet':
            raise ValueError('authoring evidence is not terminal approval')
        return response(request('change.review-result', {
            'reference': descriptor['binding']['reference'], 'binding': worker['binding'],
            'result': worker['result']}), 'change.review-result')

    def close(self):
        self.messages.clear()


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
