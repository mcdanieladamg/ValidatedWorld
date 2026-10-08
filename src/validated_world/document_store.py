"""Atomic HTML document publication and owned SQLite workspaces outside Core."""
from __future__ import annotations

from dataclasses import dataclass, replace
from hashlib import sha256
import base64
import os
from pathlib import Path

from . import document_format as html
from .storage import ProjectStore, StoredProject
from .file_publication import publish_new_file
from .scratch import Lease, staging, record_recovery, recovery_path, scratch_path, owners, transports, hide, recovering, PREFIX
from .memory_store import MemoryStore

# Central policy defaults. Explicit .vw.db paths select the instructed DB workflow.
DEFAULT_HTML_AUTHORITY = True
DELETE_WORKING_DB_AFTER_PUBLICATION = True


def is_database(path):
    return str(path).lower().endswith('.vw.db') or not DEFAULT_HTML_AUTHORITY


def byte_identity(root):
    root = html.safe_path(root)
    if not root.exists(): return None
    return sha256(root.read_bytes()).hexdigest()


class DocumentLock(Lease):
    """Writer exclusion without a project-local lock sidecar."""


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
        destination = self.destination(destination, source)
        result = None
        try:
            with staging(destination) as stage:
                result = self._publish(project, destination, stage, source=source, lock=lock,
                                       expected_bytes=expected_bytes, require_absent=require_absent)
        except Exception as exc:
            if result is None: raise
            result[1].append(f'Document published; temporary cleanup requires attention: {scratch_path(destination)}: {exc}')
        return result

    def _publish(self, project, destination, stage, *, source=None, lock=None, expected_bytes=None, require_absent=False):
        root = self.destination(destination, source)
        root.parent.mkdir(parents=True, exist_ok=True)
        owned_lock = lock is None
        lock = lock or DocumentLock(root).acquire()
        installed = False
        stage_owned = False
        append = stage.exists()
        warnings = []
        try:
            if require_absent and root.exists(): raise FileExistsError(root)
            if expected_bytes is not None and byte_identity(root) != expected_bytes: raise RuntimeError('stale-document-bytes')
            html.safe_path(stage)
            html.write_stage(project, stage, append=append); stage_owned = not append
            hide(stage); self._fault('staged')
            if expected_bytes is not None and byte_identity(root) != expected_bytes: raise RuntimeError('stale-document-bytes')
            self._fault('prepared')
            check = html.parse(stage)
            if (check.graph, check.created_utc, check.updated_utc) != (project.graph, project.created_utc, project.updated_utc):
                raise ValueError('staged document changed before publication')
            hide(stage, False)
            # Staging and destination share a filesystem. The old complete file
            # remains in place until this single atomic replacement succeeds.
            if require_absent:
                publish_new_file(stage, root); installed = True
                stage.unlink(missing_ok=True)
            else:
                os.replace(stage, root); installed = True
            self._fault('published')
            # The first complete publication carries its recovery snapshot. Do
            # not truncate the only durable candidate to strip that metadata:
            # reuse the same sibling for a second verified atomic replacement.
            # A crash/failure here leaves a complete, importable published graph.
            if root.read_bytes().startswith(PREFIX.encode()):
                published_identity = byte_identity(root)
                stage_owned = False
                html.write_stage(project, stage); stage_owned = True
                if byte_identity(root) != published_identity: raise RuntimeError('stale-document-bytes')
                os.replace(stage, root)
        except Exception as exc:
            if installed:
                if stage_owned:
                    try: stage.unlink(missing_ok=True)
                    except OSError as cleanup: warnings.append(f'Document published; staging cleanup requires attention: {stage}: {cleanup}')
                warnings.append(f'Document published; cleanup requires attention: {exc}; {stage}')
            else:
                try:
                    if stage_owned: stage.unlink(missing_ok=True)
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
    source: Path
    baseline: str
    lock: DocumentLock
    keep: bool
    scratch_lock: Lease | None = None
    committed: bool = False

    @property
    def db(self): return scratch_path(self.source)


class PublicationError(RuntimeError):
    def __init__(self, message, workspace):
        super().__init__(message); self.workspace = workspace


class ProjectFiles(ProjectStore):
    """One public project-path boundary; SQLite still implements transactions."""
    def __init__(self, write_fault=None, publication_fault=None):
        super().__init__(write_fault)
        self.engine = ProjectStore(write_fault); self.publisher = Publisher(publication_fault)
        self.memory = MemoryStore(write_fault)
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
        memory = MemoryStore()
        return memory.initialize(p.path, p.graph, created_utc=p.created_utc, updated_utc=p.updated_utc)

    def initialize(self, path, graph, **metadata):
        if is_database(path): return self.engine.initialize(path, graph, **metadata)
        root = self.publisher.destination(path)
        project = MemoryStore().initialize(str(root), graph, **metadata)
        result, self.last_warnings = self.publisher.publish(project, root, require_absent=True)
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
        memory = MemoryStore()
        memory.initialize(p.path, p.graph, created_utc=p.created_utc, updated_utc=p.updated_utc)
        return memory.export_sql(p.path)

    def begin_workspace(self, path, keep=False):
        if is_database(path): return self.engine.load(path)
        root = html.safe_path(path)
        if str(root) in self.workspaces: raise ValueError('project already has an owned workspace')
        lock = DocumentLock(root).acquire(); scratch_lock = None
        try:
            if str(root) not in transports:
                scratch_lock = Lease(root, 'scratch').acquire()
                if scratch_path(root).exists(): raise FileExistsError(f'temporary file requires inspection: {scratch_path(root)}')
                owners[str(root)] = scratch_lock
            baseline = byte_identity(root)
            parsed = html.parse(root)
            project = self.memory.initialize(str(root), parsed.graph, created_utc=parsed.created_utc, updated_utc=parsed.updated_utc)
            if byte_identity(root) != baseline: raise RuntimeError('stale-document-bytes')
            self.workspaces[str(root)] = Workspace(root, baseline, lock, keep, scratch_lock)
            return replace(project, path=str(root))
        except Exception:
            lock.close()
            self.memory.snapshots.pop(str(root), None)
            if scratch_lock: scratch_lock.close(); owners.pop(str(root), None)
            raise

    def write(self, path, project_id, base_fingerprint, proposed_fingerprint, operations):
        self.last_warnings = []; self.retained_db = None
        if is_database(path): return self.engine.write(path, project_id, base_fingerprint, proposed_fingerprint, operations)
        workspace = self.workspaces[str(html.safe_path(path))]
        if byte_identity(workspace.source) != workspace.baseline: raise RuntimeError('stale-base-fingerprint')
        key = str(workspace.source)
        before = self.memory.snapshots[key]
        project = self.memory.write(key, project_id, base_fingerprint, proposed_fingerprint, operations)
        data = {'source': key, 'baseline': workspace.baseline, 'keep': workspace.keep,
                'fingerprint': project.state_fingerprint, 'database': base64.b64encode(self.memory.snapshots[key]).decode('ascii')}
        try:
            record_recovery(workspace.source, data)
        except BaseException:
            self.memory.snapshots[key] = before
            raise
        workspace.committed = True
        try:
            result, self.last_warnings = self.publisher.publish(project, workspace.source, lock=workspace.lock, expected_bytes=workspace.baseline)
        except Exception as exc:
            try: self.close_workspace(key)
            except OSError as cleanup: exc = RuntimeError(f'{exc}; {cleanup}')
            raise PublicationError(f'Reviewed snapshot retained but HTML publication failed: {exc}. Retry with project retry-export {workspace.db}', workspace) from exc
        try: self.close_workspace(key)
        except OSError as exc: self.last_warnings.append(f'Document published; writer release requires attention: {workspace.source}: {exc}')
        try: record_recovery(workspace.source, None)
        except (OSError, ValueError) as exc:
            self.last_warnings.append(f'Document published; temporary recovery cleanup requires attention: {workspace.db}: {exc}')
        if workspace.keep or not DELETE_WORKING_DB_AFTER_PUBLICATION:
            self._retain(project, workspace.source)
        return result

    def retry_export(self, path):
        self.last_warnings = []; self.retained_db = None
        db = html.safe_path(path)
        data = recovery_path(db)
        if not isinstance(data, dict) or set(data) != {'source', 'baseline', 'keep', 'fingerprint', 'database'} or not isinstance(data['keep'], bool): raise ValueError('invalid working publication recovery record')
        memory = MemoryStore()
        memory.snapshots[str(db)] = base64.b64decode(data['database'], validate=True)
        project = memory.load(str(db))
        if project.state_fingerprint != data['fingerprint']: raise ValueError('committed recovery database changed')
        root = self.publisher.destination(data['source'], db)
        if scratch_path(root) != db: raise ValueError('recovery path does not match selected document')
        lock = DocumentLock(root).acquire()
        recovering.add(str(root))
        try:
            current = html.parse(root) if root.exists() else None
            if current is not None and current.graph == project.graph and (current.created_utc, current.updated_utc) == (project.created_utc, project.updated_utc):
                result = replace(project, path=str(root))
                with Lease(root, 'scratch'): db.unlink()
            else: result, self.last_warnings = self.publisher.publish(project, root, lock=lock, expected_bytes=data['baseline'])
        finally:
            recovering.discard(str(root))
            self._close_published_lock(lock)
        record_recovery(root, None)
        if data['keep'] or not DELETE_WORKING_DB_AFTER_PUBLICATION: self._retain(result, root)
        return result

    def _retain(self, project, source):
        destination = source.with_name(source.stem + '.working.vw.db')
        try:
            self.engine.initialize(str(destination), project.graph, created_utc=project.created_utc, updated_utc=project.updated_utc)
            self.retained_db = str(destination)
        except (OSError, ValueError) as exc:
            self.last_warnings.append(f'Document published; requested working DB retention failed: {destination}: {exc}')

    def close_workspace(self, path):
        workspace = self.workspaces.pop(str(html.safe_path(path)), None)
        if workspace is not None:
            try: workspace.lock.close()
            finally:
                self.memory.snapshots.pop(str(workspace.source), None)
                if workspace.scratch_lock:
                    workspace.scratch_lock.close(); owners.pop(str(workspace.source), None)

    def _close_published_lock(self, lock):
        try: lock.close()
        except OSError as exc: self.last_warnings.append(f'Document publication lock released; {exc}')

    def close(self):
        for path in list(self.workspaces): self.close_workspace(path)
