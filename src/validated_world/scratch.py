"""One project-local transport/staging file and process-local working state."""
from contextlib import contextmanager
import ctypes
from hashlib import sha256
import json
import io
import base64
import os
from pathlib import Path
import stat
import time

from .html_project import safe_path


def scratch_path(document):
    document = safe_path(document)
    return document.with_name('.' + document.stem + '.tmp.html')


def hide(path, hidden=True):
    if os.name == 'nt':
        kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        kernel.GetFileAttributesW.argtypes = [ctypes.c_wchar_p]
        kernel.GetFileAttributesW.restype = ctypes.c_uint32
        kernel.SetFileAttributesW.argtypes = [ctypes.c_wchar_p, ctypes.c_uint32]
        kernel.SetFileAttributesW.restype = ctypes.c_int
        attributes = kernel.GetFileAttributesW(str(path))
        desired = attributes | 2 if hidden else attributes & ~2
        if attributes == 0xffffffff or not kernel.SetFileAttributesW(str(path), desired):
            raise ctypes.WinError(ctypes.get_last_error())


class Lease:
    """Non-reentrant process lease, outside the project; never unlink a flock inode."""
    active = set()

    def __init__(self, document, role='document'):
        self.document = safe_path(document)
        target = scratch_path(self.document) if role.startswith('scratch') else self.document
        self.key = sha256((role + ':' + os.path.normcase(str(target))).encode()).hexdigest()
        self.file = None
        self.handle = None
        # Locks must rendezvous even when clients use different TMPDIR/TEMP/TMP.
        # Resolve the OS-owned /tmp alias on macOS, then reject user-owned links.
        self.path = None if os.name == 'nt' else Path('/tmp').resolve() / f'validated-world-locks-{os.getuid()}' / self.key

    def acquire(self, wait=False):
        if self.key in self.active:
            raise RuntimeError(f'project document is busy: {self.document}')
        if os.name == 'nt':
            kernel = ctypes.WinDLL('kernel32', use_last_error=True)
            kernel.CreateMutexW.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_wchar_p]
            kernel.CreateMutexW.restype = ctypes.c_void_p
            kernel.WaitForSingleObject.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
            kernel.WaitForSingleObject.restype = ctypes.c_uint32
            kernel.CloseHandle.argtypes = [ctypes.c_void_p]
            kernel.CloseHandle.restype = ctypes.c_int
            handle = kernel.CreateMutexW(None, False, 'Local\\ValidatedWorld-' + self.key)
            if not handle:
                raise ctypes.WinError(ctypes.get_last_error())
            status = kernel.WaitForSingleObject(handle, 5000 if wait else 0)
            if status not in (0, 0x80):  # Acquired, including an abandoned owner.
                kernel.CloseHandle(handle)
                if status == 0xffffffff:
                    raise ctypes.WinError(ctypes.get_last_error())
                raise RuntimeError(f'project document is busy: {self.document}')
            self.handle = handle
        else:
            import fcntl
            directory = safe_path(self.path.parent)
            directory.mkdir(mode=0o700, exist_ok=True)
            metadata = directory.stat()
            if metadata.st_uid != os.getuid() or stat.S_IMODE(metadata.st_mode) & 0o077:
                raise PermissionError(f'lease directory is not private to the current user: {directory}')
            safe_path(self.path)
            descriptor = os.open(self.path, os.O_CREAT | os.O_RDWR | getattr(os, 'O_NOFOLLOW', 0), 0o600)
            self.file = os.fdopen(descriptor, 'r+b')
            try:
                deadline = time.monotonic() + (5 if wait else 0)
                while True:
                    try:
                        fcntl.flock(self.file, fcntl.LOCK_EX | fcntl.LOCK_NB)
                        break
                    except BlockingIOError:
                        if time.monotonic() >= deadline: raise
                        time.sleep(.01)
            except OSError as exc:
                self.file.close(); self.file = None
                raise RuntimeError(f'project document is busy: {self.document}') from exc
        self.active.add(self.key)
        return self

    def close(self):
        if self.handle is not None:
            kernel = ctypes.WinDLL('kernel32', use_last_error=True)
            kernel.ReleaseMutex.argtypes = [ctypes.c_void_p]
            kernel.ReleaseMutex.restype = ctypes.c_int
            kernel.CloseHandle.argtypes = [ctypes.c_void_p]
            kernel.CloseHandle.restype = ctypes.c_int
            released = kernel.ReleaseMutex(self.handle)
            kernel.CloseHandle(self.handle); self.handle = None
            self.active.discard(self.key)
            if not released:
                raise ctypes.WinError(ctypes.get_last_error())
        if self.file is not None:
            self.file.close(); self.file = None
            self.active.discard(self.key)

    def __enter__(self): return self.acquire()
    def __exit__(self, *_): self.close()


transports = {}
owners = {}
MARKER = 'validated-world-temporary-1'
PREFIX = '<!--vw-temporary:'


def encode_record(record):
    return PREFIX + base64.b64encode(json.dumps(record, ensure_ascii=True).encode()).decode() + ' -->\n'


def decode_record(line):
    if isinstance(line, bytes): line = line.decode('ascii')
    if not line.startswith(PREFIX) or not line.endswith(' -->\n'):
        raise ValueError('incomplete or unknown temporary record')
    return json.loads(base64.b64decode(line[len(PREFIX):-5], validate=True))


def recovery_path(path, document=None):
    """Recognize only complete owned records. Unknown scratch data is preserved."""
    with safe_path(path).open(encoding='utf-8') as stream:
        line = stream.readline()
        first = decode_record(line)
        if not isinstance(first, dict) or first.get('temporary') != MARKER or set(first) not in ({'temporary', 'document'}, {'temporary', 'document', 'recovery'}):
            raise ValueError(f'unrecognized temporary file; inspect before cleanup: {path}')
        if document is not None and first['document'] != str(safe_path(document)):
            raise ValueError(f'temporary file belongs to another document: {path}')
        recovery = first.get('recovery')
        for line in stream:
            if not line.startswith(PREFIX):
                if recovery is None: raise ValueError(f'interrupted HTML staging requires inspection: {path}')
                break
            record = decode_record(line)
            if not isinstance(record, dict) or not ('command' in record or set(record) == {'recovery'}):
                raise ValueError(f'unknown temporary record; inspect before cleanup: {path}')
            if 'recovery' in record: recovery = record['recovery']
        return recovery


class ResponseTransport:
    """Short-lived file handles let publication reuse exactly the same sibling."""
    def __init__(self, document):
        self.document = safe_path(document)
        self.path = scratch_path(document)
        self.lease = Lease(document, 'scratch').acquire()
        self.identity = None
        self.anchor = None
        self.recovery = None
        self.spool = io.StringIO()
        try:
            if self.path.exists():
                recovery = recovery_path(self.path, self.document)
                if recovery:
                    raise ValueError(f'preserve unpublished recovery; use project retry-export {self.path}')
                self.path.unlink()
            self._create()
            self._record({'temporary': MARKER, 'document': str(self.document)})
            transports[str(self.document)] = self
        except BaseException:
            self._close_anchor()
            self.spool.close(); self.lease.close()
            raise

    def _create(self):
        safe_path(self.path)
        with self.path.open('x', encoding='utf-8', newline='\n') as stream:
            metadata = os.fstat(stream.fileno())
            self.identity = (metadata.st_dev, metadata.st_ino)
            # POSIX permits unlink/replacement with an open descriptor. Pin the
            # original inode so unlink/recreate cannot recycle its identity.
            # Windows handles remain short-lived to permit publication there.
            anchor = os.dup(stream.fileno()) if os.name != 'nt' else None
        self._close_anchor()
        self.anchor = anchor
        hide(self.path)

    def _close_anchor(self):
        if self.anchor is not None:
            os.close(self.anchor)
            self.anchor = None

    def _check(self):
        safe_path(self.path)
        metadata = self.path.stat()
        if (metadata.st_dev, metadata.st_ino) != self.identity:
            raise ValueError(f'temporary file replaced; inspect before cleanup: {self.path}')

    def _record(self, record):
        text = encode_record(record)
        self._check()
        with self.path.open('a', encoding='utf-8', newline='\n') as stream:
            stream.write(text); stream.flush()
        self.spool.write(text); self.spool.flush()
        return len(text)

    def write(self, text):
        # The NDJSON engine emits one complete response per write.
        for line in text.splitlines():
            if line: self._record(json.loads(line))
        return len(text)

    def flush(self): pass

    def record_recovery(self, recovery):
        if recovery is not None: self.recovery = recovery
        self._record({'recovery': recovery})
        with self.path.open('ab') as stream: stream.flush(); os.fsync(stream.fileno())
        self.recovery = recovery

    @contextmanager
    def staging(self):
        lease = Lease(self.document, 'scratch-io').acquire(wait=True)
        try:
            with self._staging(): yield self.path
        finally: lease.close()

    @contextmanager
    def _staging(self):
        self._check()
        size = self.path.stat().st_size
        try:
            yield self.path
        finally:
            if self.path.exists():
                self._check()
                with self.path.open('r+b') as stream: stream.truncate(size); stream.flush(); os.fsync(stream.fileno())
                hide(self.path)
            else:
                self._create()
                self.spool.seek(0)
                with self.path.open('a', encoding='utf-8', newline='\n') as stream:
                    while block := self.spool.read(64 * 1024): stream.write(block)
                    stream.flush(); os.fsync(stream.fileno())
                self.spool.seek(0, 2)

    def close(self, clean=True, keep=False):
        try:
            if clean and not keep and not self.recovery:
                self._check(); self.path.unlink()
        finally:
            transports.pop(str(self.document), None)
            self._close_anchor()
            self.spool.close(); self.lease.close()


@contextmanager
def staging(document):
    transport = transports.get(str(safe_path(document)))
    if transport:
        with transport.staging() as path: yield path
    else:
        lease = None if str(safe_path(document)) in owners else Lease(document, 'scratch').acquire()
        io_lease = None
        try:
            io_lease = Lease(document, 'scratch-io').acquire(wait=True)
            path = scratch_path(document)
            saved = None
            if path.exists():
                recovery = recovery_path(path, document)
                if not recovery: raise FileExistsError(f'temporary file requires inspection: {path}')
                if str(safe_path(document)) not in owners and str(safe_path(document)) not in recovering:
                    raise FileExistsError(f'preserve unpublished recovery; use project retry-export {path}')
                # Retain immutable complete recovery records while appending
                # a new candidate; truncate only an interrupted candidate tail.
                saved = b''
                with path.open('rb') as stream:
                    for line in stream:
                        if not line.startswith(PREFIX.encode()): break
                        decode_record(line)
                        saved += line
                with path.open('r+b') as stream: stream.truncate(len(saved)); stream.flush(); os.fsync(stream.fileno())
            try: yield path
            except BaseException:
                if saved is not None:
                    if path.exists():
                        with path.open('r+b') as stream: stream.truncate(len(saved)); stream.flush(); os.fsync(stream.fileno())
                    else:
                        with path.open('xb') as stream: stream.write(saved); stream.flush(); os.fsync(stream.fileno())
                    hide(path)
                raise
        finally:
            if io_lease: io_lease.close()
            if lease: lease.close()


def record_recovery(document, recovery):
    transport = transports.get(str(safe_path(document)))
    if transport:
        transport.record_recovery(recovery)
    elif recovery is not None:
        path = scratch_path(document)
        with path.open('x', encoding='utf-8', newline='\n') as stream:
            stream.write(encode_record({'temporary': MARKER, 'document': str(safe_path(document)), 'recovery': recovery}))
            stream.flush(); os.fsync(stream.fileno())
        hide(path)


def read_responses(path, offset=0):
    """Read complete protocol responses while excluding publication windows."""
    path = safe_path(path)
    if not path.name.startswith('.') or not path.name.endswith('.tmp.html'):
        raise ValueError('response path must be the announced hidden .tmp.html file')
    if offset < 0: raise ValueError('offset must be nonnegative')
    document = path.with_name(path.name[1:-9] + '.html')
    lease = Lease(document, 'scratch-io')
    try: lease.acquire()
    except RuntimeError: return {'responses': [], 'nextOffset': offset, 'busy': True}
    try:
        with path.open('rb') as stream:
            header = decode_record(stream.readline())
            if header.get('temporary') != MARKER: raise ValueError('temporary file is not a response transport')
            if offset > stream.seek(0, 2): raise ValueError('response offset exceeds current file')
            stream.seek(offset)
            responses = []
            next_offset = offset
            for line in stream:
                if not line.endswith(b'\n'): break
                record = decode_record(line)
                next_offset = stream.tell()
                if 'command' in record: responses.append(record)
        return {'responses': responses, 'nextOffset': next_offset, 'busy': False}
    finally: lease.close()


recovering = set()
