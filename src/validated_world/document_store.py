"""Atomic HTML document publication and owned SQLite workspaces outside Core."""
from __future__ import annotations

from dataclasses import dataclass, replace
from contextlib import contextmanager
from hashlib import sha256
import json
import os
from pathlib import Path
import shutil
import tempfile
import uuid

from . import document_format as html
from .storage import ProjectStore, StoredProject
from .file_publication import publish_new_file

# Central policy defaults. Explicit .vw.db paths select the instructed DB workflow.
DEFAULT_HTML_AUTHORITY = True
DELETE_WORKING_DB_AFTER_PUBLICATION = True


def is_database(path):
    return str(path).lower().endswith('.vw.db') or not DEFAULT_HTML_AUTHORITY


def byte_identity(root):
    root = html.safe_path(root)
    if not root.exists(): return None
    return sha256(root.read_bytes()).hexdigest()


def _remove_owned(root):
    root = html.safe_path(root)
    # Validate all children before a recursive deletion, including Windows junctions.
    for child in root.rglob('*'):
        resolved = html.safe_path(child)
        if not resolved.is_relative_to(root): raise ValueError('cleanup path escaped its owned directory')
    shutil.rmtree(root)


def _workspace_parent(path):
    # Use only the caller's selected authority, never graph text or OS Temp.
    from .path_safety import project_temp_root
    root = html.safe_path(path)
    try:
        root.parent.mkdir(parents=True, exist_ok=True)
        return project_temp_root(root.parent)
    except OSError as exc:
        raise OSError(f'cannot prepare SQLite workspace for {root}: {exc}') from exc


@contextmanager
def _temporary_database(path, prefix):
    parent = _workspace_parent(path)
    try:
        temporary = tempfile.TemporaryDirectory(prefix=prefix, dir=parent)
    except OSError as exc:
        raise OSError(f'cannot allocate {prefix}SQLite workspace in {parent}: {exc}') from exc
    with temporary as directory:
        # Canonicalize owned allocations before rejecting explicit linked paths.
        yield Path(directory).resolve() / 'working.vw.db'


class DocumentLock:
    """Hold an OS lock for an operation and remove its file on normal release."""
    def __init__(self, destination):
        self.destination = html.safe_path(destination); self.file = None
        self.path = self.destination.parent / ('.' + self.destination.name + '.vw-lock')

    def acquire(self):
        html.safe_path(self.path); self.destination.parent.mkdir(parents=True, exist_ok=True)
        self.file = open(self.path, 'a+b')
        try:
            if os.name == 'nt':
                import msvcrt
                if self.file.seek(0, 2) == 0: self.file.write(b'0'); self.file.flush()
                self.file.seek(0); msvcrt.locking(self.file.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(self.file, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError as exc:
            self.file.close(); self.file = None
            raise RuntimeError(f'project document is busy: {self.destination}') from exc
        return self

    def close(self):
        if self.file is not None:
            self.file.close(); self.file = None
            try:
                html.safe_path(self.path)
                self.path.unlink(missing_ok=True)
            except OSError as exc:
                raise OSError(f'lock-file cleanup requires attention: {self.path}: {exc}') from exc

    def __enter__(self): return self.acquire()
    def __exit__(self, *_): self.close()


class Publisher:
    def __init__(self, fault=None): self.fault = fault

    def _fault(self, stage):
        if self.fault: self.fault(stage)

    @staticmethod
    def destination(path, source=None):
        root = html.safe_path(path)
        if root.suffix.lower() != '.html' or (root.exists() and not root.is_file()):
            raise ValueError('HTML destination must be a .html file')
        for ancestor in (root, *root.parents):
            if ancestor.name.casefold() in ('.git', 'validated_world', '.codex-plugin'): raise ValueError('export cannot overwrite repository or application internals')
        if source is not None:
            source = html.safe_path(source)
            if source == root: raise ValueError('source and export destination overlap')
        return root

    def publish(self, project, destination, *, source=None, lock=None, expected_bytes=None, require_absent=False):
        root = self.destination(destination, source)
        owned_lock = lock is None
        lock = lock or DocumentLock(root).acquire()
        stage = root.parent / f'.{root.name}.vw-stage-{uuid.uuid4().hex}'
        installed = False
        warnings = []
        try:
            if require_absent and root.exists(): raise FileExistsError(root)
            if expected_bytes is not None and byte_identity(root) != expected_bytes: raise RuntimeError('stale-document-bytes')
            html.safe_path(stage)
            html.write_stage(project, stage); self._fault('staged')
            if expected_bytes is not None and byte_identity(root) != expected_bytes: raise RuntimeError('stale-document-bytes')
            self._fault('prepared')
            # Staging and destination share a filesystem. The old complete file
            # remains in place until this single atomic replacement succeeds.
            if require_absent:
                publish_new_file(stage, root); installed = True
                stage.unlink(missing_ok=True)
            else:
                os.replace(stage, root); installed = True
            self._fault('published')
        except Exception as exc:
            if installed:
                warnings.append(f'Document published; cleanup requires attention: {exc}; {stage}')
            else:
                try:
                    stage.unlink(missing_ok=True)
                except Exception as cleanup:
                    raise RuntimeError(f'publication failed: {exc}; staging cleanup requires {stage}: {cleanup}') from exc
                raise
        finally:
            if owned_lock:
                try: lock.close()
                except OSError as exc:
                    if not installed: raise
                    warnings.append(f'Document published; {exc}')
        return replace(project, path=str(root)), warnings


@dataclass
class Workspace:
    directory: Path
    db: Path
    source: Path
    baseline: str
    lock: DocumentLock
    keep: bool
    committed: bool = False


class PublicationError(RuntimeError):
    def __init__(self, message, workspace):
        super().__init__(message); self.workspace = workspace


class ProjectFiles(ProjectStore):
    """One public project-path boundary; SQLite still implements transactions."""
    def __init__(self, write_fault=None, publication_fault=None):
        super().__init__(write_fault)
        self.engine = ProjectStore(write_fault); self.publisher = Publisher(publication_fault)
        self.workspaces = {}; self.last_warnings = []; self.retained_db = None

    def import_html(self, source, destination):
        self.last_warnings = []; self.retained_db = None
        p = html.parse(source)
        return self.engine.initialize(destination, p.graph, created_utc=p.created_utc, updated_utc=p.updated_utc)

    def export_html(self, source, destination):
        project = self.engine.load(source)
        result, self.last_warnings = self.publisher.publish(project, destination, source=source)
        return result

    def load(self, path):
        if is_database(path): return self.engine.load(path)
        p = html.parse(path)
        if p.path in self.workspaces: return p
        # Exercise the verified SQLite mapping even for reads; leave the source alone.
        with _temporary_database(p.path, 'vw-read-') as db:
            imported = self.engine.initialize(str(db), p.graph, created_utc=p.created_utc, updated_utc=p.updated_utc)
            return replace(imported, path=p.path)

    def initialize(self, path, graph, **metadata):
        if is_database(path): return self.engine.initialize(path, graph, **metadata)
        root = self.publisher.destination(path)
        with _temporary_database(root, 'vw-init-') as db:
            project = self.engine.initialize(str(db), graph, **metadata)
            result, self.last_warnings = self.publisher.publish(project, root, source=db, require_absent=True)
            return result

    def status(self, path):
        if is_database(path): return self.engine.status(path)
        project = self.load(path)
        return {'path': project.path, 'projectId': project.graph.project_id, 'title': project.graph.title, 'purposeNodeId': project.graph.purpose_node_id, 'nodeCount': len(project.graph.nodes), 'edgeCount': len(project.graph.edges), 'stateFingerprint': project.state_fingerprint}

    def backup(self, source, destination):
        project = self.load(source)
        if is_database(destination): return self.engine.initialize(destination, project.graph, created_utc=project.created_utc, updated_utc=project.updated_utc)
        result, self.last_warnings = self.publisher.publish(project, destination, source=source, require_absent=True)
        return result

    def export_sql(self, path):
        if is_database(path): return self.engine.export_sql(path)
        p = self.load(path)
        with _temporary_database(p.path, 'vw-sql-') as db:
            self.engine.initialize(str(db), p.graph, created_utc=p.created_utc, updated_utc=p.updated_utc)
            return self.engine.export_sql(str(db))

    def begin_workspace(self, path, keep=False):
        if is_database(path): return self.engine.load(path)
        root = html.safe_path(path)
        if str(root) in self.workspaces: raise ValueError('project already has an owned workspace')
        lock = DocumentLock(root).acquire(); directory = None
        try:
            baseline = byte_identity(root)
            parent = _workspace_parent(root)
            try:
                directory = Path(tempfile.mkdtemp(prefix='vw-change-', dir=parent)).resolve()
            except OSError as exc:
                raise OSError(f'cannot allocate change SQLite workspace in {parent}: {exc}') from exc
            db = directory / 'working.vw.db'
            project = self.import_html(root, db)
            if byte_identity(root) != baseline: raise RuntimeError('stale-document-bytes')
            self.workspaces[str(root)] = Workspace(directory, db, root, baseline, lock, keep)
            return replace(project, path=str(root))
        except Exception:
            lock.close()
            if directory is not None: _remove_owned(directory)
            raise

    def write(self, path, project_id, base_fingerprint, proposed_fingerprint, operations):
        self.last_warnings = []; self.retained_db = None
        if is_database(path): return self.engine.write(path, project_id, base_fingerprint, proposed_fingerprint, operations)
        workspace = self.workspaces[str(html.safe_path(path))]
        if byte_identity(workspace.source) != workspace.baseline: raise RuntimeError('stale-base-fingerprint')
        # Make recovery discoverable before committing: a crash after SQLite's
        # commit must not strand the only durable copy without a retry record.
        recovery = workspace.directory / 'publication.json'
        pending = recovery.with_suffix('.tmp')
        try:
            with pending.open('w', encoding='utf-8', newline='\n') as stream:
                json.dump({'source': str(workspace.source), 'baseline': workspace.baseline, 'keep': workspace.keep, 'fingerprint': proposed_fingerprint}, stream)
                stream.write('\n'); stream.flush(); os.fsync(stream.fileno())
            os.replace(pending, recovery)
        finally:
            pending.unlink(missing_ok=True)
        project = self.engine.write(str(workspace.db), project_id, base_fingerprint, proposed_fingerprint, operations)
        workspace.committed = True
        try:
            result, self.last_warnings = self.publisher.publish(project, workspace.source, source=workspace.db, lock=workspace.lock, expected_bytes=workspace.baseline)
        except Exception as exc:
            self.workspaces.pop(str(workspace.source))
            try: workspace.lock.close()
            except OSError as cleanup: exc = RuntimeError(f'{exc}; {cleanup}')
            raise PublicationError(f'SQLite committed but HTML publication failed: {exc}. Retry with project retry-export {workspace.db}', workspace) from exc
        self.workspaces.pop(str(workspace.source)); self._close_published_lock(workspace.lock)
        if workspace.keep or not DELETE_WORKING_DB_AFTER_PUBLICATION:
            self.retained_db = str(workspace.db)
            try: recovery.unlink()
            except OSError as exc: self.last_warnings.append(f'Document published; recovery record cleanup requires attention: {recovery}: {exc}')
        else:
            try: _remove_owned(workspace.directory)
            except OSError as exc: self.last_warnings.append(f'Document published; working DB cleanup requires attention: {workspace.directory}: {exc}'); self.retained_db = str(workspace.db)
        return result

    def retry_export(self, path):
        self.last_warnings = []; self.retained_db = None
        db = html.safe_path(path); record = html.safe_path(db.parent / 'publication.json')
        data = json.loads(record.read_text(encoding='utf-8'))
        if set(data) != {'source', 'baseline', 'keep', 'fingerprint'} or not isinstance(data['keep'], bool): raise ValueError('invalid working publication recovery record')
        project = self.engine.load(str(db))
        if project.state_fingerprint != data['fingerprint']: raise ValueError('committed recovery database changed')
        root = self.publisher.destination(data['source'], db)
        lock = DocumentLock(root).acquire()
        try:
            current = html.parse(root) if root.exists() else None
            if current is not None and current.graph == project.graph and (current.created_utc, current.updated_utc) == (project.created_utc, project.updated_utc): result = replace(project, path=str(root))
            else: result, self.last_warnings = self.publisher.publish(project, root, source=db, lock=lock, expected_bytes=data['baseline'])
        finally:
            self._close_published_lock(lock)
        try:
            if data['keep'] or not DELETE_WORKING_DB_AFTER_PUBLICATION: record.unlink(); self.retained_db = str(db)
            else:
                # Only this command's fixed engine-created workspace may be removed.
                if db.name != 'working.vw.db' or not db.parent.name.startswith('vw-change-') or set(p.name for p in db.parent.iterdir()) != {'working.vw.db', 'publication.json'}:
                    raise ValueError('recovery workspace contains unknown files, cleanup needs inspection')
                _remove_owned(db.parent)
        except (OSError, ValueError) as exc:
            self.last_warnings.append(f'Document published; working DB cleanup requires attention: {db.parent}: {exc}')
            self.retained_db = str(db)
        return result

    def close_workspace(self, path):
        workspace = self.workspaces.pop(str(html.safe_path(path)), None)
        if workspace is not None:
            try: workspace.lock.close()
            finally:
                if not workspace.committed: _remove_owned(workspace.directory)

    def _close_published_lock(self, lock):
        try: lock.close()
        except OSError as exc: self.last_warnings.append(f'Document publication lock released; {exc}')

    def close(self):
        for path in list(self.workspaces): self.close_workspace(path)
