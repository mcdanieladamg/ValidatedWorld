"""Read-only native IPC packets, held in RAM. No HTTP, URLs or disk sockets."""
from copy import deepcopy
from hashlib import sha256
import json
from multiprocessing.connection import Client, Listener
import os
import secrets
import sys
import threading

from .protocol import json_loads_strict


def encode(value):
    return (json.dumps(value, ensure_ascii=True, sort_keys=True) + '\n').encode('utf-8')


def _address(channel):
    """Accept only product-generated local addresses, never graph-selected hosts."""
    family, address = channel['family'], channel['address']
    if family == 'AF_PIPE' and isinstance(address, str) and address.startswith('\\\\.\\pipe\\validated-world-'):
        suffix = address[len('\\\\.\\pipe\\validated-world-'):]
    elif family == 'AF_UNIX' and isinstance(address, str) and address.startswith('\0validated-world-'):
        suffix = address[len('\0validated-world-'):]
    elif (family == 'AF_INET' and isinstance(address, (list, tuple)) and len(address) == 2 and
          address[0] == '127.0.0.1' and type(address[1]) is int and 0 < address[1] < 65536):
        return family, tuple(address)
    else:
        raise ValueError('review channel must be a local native address')
    if len(suffix) != 64 or any(c not in '0123456789abcdef' for c in suffix):
        raise ValueError('invalid native channel name')
    return family, address


def fetch(channel, key):
    """Read one bounded page using byte framing, never pickle/object decoding."""
    family, address = _address(channel)
    if not isinstance(key, str) or not isinstance(channel['token'], str):
        raise ValueError('channel token and evidence key must be text')
    with Client(address, family=family) as connection:
        connection.send_bytes(encode({'token': channel['token'], 'key': key}))
        if not connection.poll(30):
            raise TimeoutError('native reviewer channel did not reply within 30 seconds')
        raw = connection.recv_bytes()
    value = json_loads_strict(raw.decode('utf-8'))
    if isinstance(value, dict) and 'transportError' in value:
        raise ValueError(value['transportError'])
    return raw


class PacketTransport:
    """Only complete precomputed evidence can be read; no authoring commands."""
    def __init__(self):
        name = 'validated-world-' + secrets.token_hex(32)
        if os.name == 'nt':
            family, address = 'AF_PIPE', '\\\\.\\pipe\\' + name
        elif sys.platform.startswith('linux'):
            family, address = 'AF_UNIX', '\0' + name
        else:
            # macOS has no abstract Unix socket namespace. A numeric loopback
            # byte channel avoids disk sockets; availability must be tested by
            # the actual reviewer. No HTTP client/server or URL is involved.
            family, address = 'AF_INET', ('127.0.0.1', 0)
        self.listener = Listener(address, family=family)
        actual = self.listener.address
        if isinstance(actual, bytes):
            actual = actual.decode('ascii')
        self.channel = {'family': family, 'address': actual, 'token': secrets.token_hex(32)}
        self.exports = {}
        self.lock = threading.RLock()
        self.stopping = False
        self.connections = set()
        self.thread = threading.Thread(target=self._accept, daemon=True)
        self.thread.start()

    def _accept(self):
        while not self.stopping:
            try:
                connection = self.listener.accept()
            except (OSError, EOFError):
                return
            with self.lock:
                if self.stopping:
                    connection.close(); return
                self.connections.add(connection)
            threading.Thread(target=self._read, args=(connection,), daemon=True).start()

    def _read(self, connection):
        try:
            if not connection.poll(30):
                return
            # Control frames contain only a fixed-size capability and page key;
            # this is not a limit on graph size or evidence response bytes.
            request = json_loads_strict(connection.recv_bytes(512).decode('utf-8'))
            if (not isinstance(request, dict) or set(request) != {'token', 'key'} or
                    not isinstance(request['token'], str) or not isinstance(request['key'], str) or
                    not secrets.compare_digest(request['token'], self.channel['token'])):
                connection.send_bytes(encode({'transportError': 'invalid reviewer capability'})); return
            with self.lock:
                entry = self.exports.get(request['key'])
                if entry is None:
                    connection.send_bytes(encode({'transportError': 'revoked or unknown evidence'})); return
                _, raw, validate, deliver = entry
                try:
                    validate()
                    if deliver is not None:
                        deliver()
                except (ValueError, KeyError, RuntimeError, OSError):
                    connection.send_bytes(encode({'transportError': 'stale evidence'})); return
                connection.send_bytes(raw)
        except (ValueError, UnicodeError, OSError, EOFError):
            pass
        finally:
            with self.lock:
                self.connections.discard(connection)
            connection.close()

    def export(self, app, reference, plan_fingerprint, packet_id, limit):
        review = app.packet_review(reference, plan_fingerprint)
        saved_seen = deepcopy(review.seen)
        pages = []
        try:
            cursor = None
            while True:
                page = review.packet(packet_id, limit, cursor)
                pages.append((encode(page), cursor))
                cursor = page['nextCursor']
                if cursor is None:
                    break
        finally:
            # Allocating a channel is not evidence presentation.
            review.seen = saved_seen
        binding = page['binding']
        prefix = secrets.token_hex(32)
        manifest = {'binding': binding, 'role': page['role'], 'intent': review.intent,
                    'pages': [{'key': f'{prefix}/page-{i:06d}', 'sha256': sha256(raw).hexdigest()}
                              for i, (raw, _) in enumerate(pages)]}

        def validate():
            current = app.packet_review(reference, plan_fingerprint)
            if current is not review:
                raise ValueError('stale packet export')
            previous = deepcopy(review.seen)
            try:
                if review.packet(packet_id, limit, None)['binding'] != binding:
                    raise ValueError('stale packet evidence')
            finally:
                review.seen = previous

        owner = reference['sessionId']
        with self.lock:
            for i, (raw, cursor) in enumerate(pages):
                self.exports[f'{prefix}/page-{i:06d}'] = (owner, raw, validate, lambda c=cursor: review.packet(packet_id, limit, c))
            self.exports[prefix + '/manifest'] = (owner, encode(manifest), validate, None)
        return {'binding': binding, 'channel': deepcopy(self.channel),
                'manifestKey': prefix + '/manifest', 'pageCount': len(pages)}

    def revoke(self, owner=None):
        with self.lock:
            names = [name for name, entry in self.exports.items() if owner is None or entry[0] == owner]
            for name in names:
                del self.exports[name]
        return {'revokedEndpoints': len(names)}

    def export_affected(self, app, reference, limit):
        session = app.session(reference)
        pages = []; cursor = None
        binding = {'reference': deepcopy(reference)}
        while True:
            value = session.affected(limit, cursor)
            pages.append(encode({'binding': binding, 'evidence': value}))
            cursor = value['page']['nextCursor']
            if cursor is None:
                break
        app.session(reference)
        prefix = secrets.token_hex(32)
        manifest = {'mode': 'affected', 'binding': binding, 'intent': session.intent,
                    'pages': [{'key': f'{prefix}/page-{i:06d}', 'sha256': sha256(raw).hexdigest()}
                              for i, raw in enumerate(pages)]}
        def validate():
            if app.session(reference) is not session:
                raise ValueError('stale affected evidence')
        owner = reference['sessionId']
        with self.lock:
            for i, raw in enumerate(pages):
                self.exports[f'{prefix}/page-{i:06d}'] = (owner, raw, validate, None)
            self.exports[prefix + '/manifest'] = (owner, encode(manifest), validate, None)
        return {'mode': 'affected', 'binding': binding, 'channel': deepcopy(self.channel),
                'manifestKey': prefix + '/manifest', 'pageCount': len(pages)}

    def close(self):
        self.revoke()
        self.stopping = True
        # Wake the accept thread with native IPC, without a disk sentinel.
        with Client(self.listener.address, family=self.channel['family']):
            pass
        self.thread.join(timeout=30)
        self.listener.close()
        with self.lock:
            for connection in tuple(self.connections):
                connection.close()
            self.connections.clear()
