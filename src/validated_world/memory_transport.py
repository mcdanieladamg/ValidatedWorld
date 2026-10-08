"""Optional loopback transports. State and packet bytes live only in this process."""
from copy import deepcopy
from hashlib import sha256
from http.server import BaseHTTPRequestHandler, HTTPServer
from io import StringIO
import json
import secrets
from socketserver import TCPServer, ThreadingMixIn
import threading
from urllib.parse import urlsplit
from urllib.request import Request, ProxyHandler, build_opener


class LoopbackHTTPServer(HTTPServer):
    def server_bind(self):
        # HTTPServer resolves its name through reverse DNS before startup.
        # Our numeric loopback endpoint needs no resolver or external network.
        TCPServer.server_bind(self)
        self.server_name, self.server_port = self.server_address[:2]


class ThreadedLoopbackHTTPServer(ThreadingMixIn, LoopbackHTTPServer):
    daemon_threads = True


class QuietHandler(BaseHTTPRequestHandler):
    def log_message(self, *_): pass

    def respond(self, status, raw=b''):
        self.send_response(status)
        self.send_header('Content-Type', 'application/json; charset=utf-8')
        self.send_header('Content-Length', str(len(raw)))
        self.send_header('Cache-Control', 'no-store')
        self.end_headers()
        self.wfile.write(raw)


def encode(value):
    return (json.dumps(value, ensure_ascii=True, sort_keys=True) + '\n').encode('utf-8')


class PacketTransport:
    """Read-only capability URLs; no controller commands or graph-selected paths."""
    def __init__(self):
        self.exports = {}
        self.lock = threading.RLock()
        transport = self

        class Handler(QuietHandler):
            def do_GET(self):
                with transport.lock:
                    entry = transport.exports.get(self.path)
                    if entry is None:
                        self.respond(404); return
                    owner, raw, validate, deliver = entry
                    try:
                        validate()
                        if deliver is not None: deliver()
                    except (ValueError, KeyError, RuntimeError, OSError):
                        self.respond(410); return
                    self.respond(200, raw)

        self.server = ThreadedLoopbackHTTPServer(('127.0.0.1', 0), Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

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
                if cursor is None: break
        finally:
            # Allocating an endpoint is not evidence presentation. Delivery of
            # each exact page below records the existing presentation gate.
            review.seen = saved_seen
        binding = page['binding']
        prefix = '/' + secrets.token_urlsafe(32)
        base = f'http://127.0.0.1:{self.server.server_port}'
        manifest = {'binding': binding, 'role': page['role'], 'intent': review.intent,
                    'pages': [{'url': f'{base}{prefix}/page-{i:06d}', 'sha256': sha256(raw).hexdigest()} for i, (raw, _) in enumerate(pages)]}

        def validate():
            current = app.packet_review(reference, plan_fingerprint)
            if current is not review: raise ValueError('stale packet export')
            previous = deepcopy(review.seen)
            try:
                if review.packet(packet_id, limit, None)['binding'] != binding:
                    raise ValueError('stale packet evidence')
            finally: review.seen = previous

        owner = reference['sessionId']
        with self.lock:
            for i, (raw, cursor) in enumerate(pages):
                self.exports[f'{prefix}/page-{i:06d}'] = (owner, raw, validate, lambda c=cursor: review.packet(packet_id, limit, c))
            self.exports[prefix + '/manifest'] = (owner, encode(manifest), validate, None)
        return {'binding': binding, 'manifestUrl': base + prefix + '/manifest', 'pageCount': len(pages)}

    def revoke(self, owner=None):
        with self.lock:
            names = [name for name, entry in self.exports.items() if owner is None or entry[0] == owner]
            for name in names: del self.exports[name]
        return {'revokedEndpoints': len(names)}

    def close(self):
        self.revoke()
        self.server.shutdown(); self.server.server_close(); self.thread.join()


def serve(out, err, *, project_root=None):
    """Serial controller requests keep SQLite ownership on the serving thread."""
    from .application import Application
    from .cli import ndjson_loop
    from .protocol import json_loads_strict
    from pathlib import Path
    from .project_paths import ProjectPaths
    paths = ProjectPaths(Path.cwd() if project_root is None else project_root)
    app = Application()
    token = '/' + secrets.token_urlsafe(32)
    exiting = False

    class Handler(QuietHandler):
        def do_POST(self):
            nonlocal exiting
            if self.path != token:
                self.respond(404); return
            self.connection.settimeout(30)
            try:
                size = int(self.headers.get('Content-Length', '-1'))
                if size < 0: raise ValueError('Content-Length is required')
                raw = self.rfile.read(size).decode('utf-8')
                request = json_loads_strict(raw)
                response = StringIO()
                ndjson_loop(StringIO(json.dumps(request) + '\n'), response, err, app=app, close_on_eof=False, project_root=paths.root)
                self.respond(200, response.getvalue().encode('utf-8'))
                if isinstance(request, dict) and request.get('command') == 'host.exit' and json_loads_strict(response.getvalue())['status'] == 'ok':
                    exiting = True
            except (ValueError, UnicodeError, OSError, AttributeError) as exc:
                self.respond(400, encode({'error': str(exc)}))

    server = LoopbackHTTPServer(('127.0.0.1', 0), Handler)
    try:
        out.write(json.dumps({'controllerUrl': f'http://127.0.0.1:{server.server_port}{token}', 'projectRoot': paths.root}) + '\n'); out.flush()
        while not exiting: server.handle_request()
    finally:
        app.close(); server.server_close()
    return 0


def request(url, inp, out):
    parsed = urlsplit(url)
    if parsed.scheme != 'http' or parsed.hostname != '127.0.0.1' or parsed.username or parsed.password or parsed.query or parsed.fragment or not parsed.port:
        raise ValueError('use the exact loopback controller URL announced by serve')
    raw = inp.read().encode('utf-8')
    # Loopback must not pass through environment-configured HTTP proxies.
    with build_opener(ProxyHandler({})).open(Request(url, data=raw, headers={'Content-Type': 'application/json'}), timeout=30) as response:
        out.write(response.read().decode('utf-8')); out.flush()
    return 0
