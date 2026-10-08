"""In-memory HTML project sessions with one companion file and atomic replacement."""
from __future__ import annotations

from dataclasses import dataclass, replace
from hashlib import sha256
import os

from . import document_format as html
from .storage import ProjectStore

DEFAULT_HTML_AUTHORITY = True


def is_database(path):
    return str(path).lower().endswith('.vw.db') or not DEFAULT_HTML_AUTHORITY


def byte_identity(root):
    root = html.safe_path(root)
    return sha256(root.read_bytes()).hexdigest() if root.exists() else None


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
            if ancestor.name.casefold() in ('.git', 'validated_world', '.codex-plugin'):
                raise ValueError('export cannot overwrite repository or application internals')
        if source is not None and html.safe_path(source) == root:
            raise ValueError('source and export destination overlap')
        return root

    def publish(self, project, destination, *, source=None, expected_bytes=None, require_absent=False):
        root = self.destination(destination, source)
        text = html.render(project)
        check = html.parse_text(text, root)
        if (check.graph, check.created_utc, check.updated_utc) != (project.graph, project.created_utc, project.updated_utc):
            raise ValueError('export round-trip verification failed')
        raw = text.encode('utf-8')
        self._fault('prepared')
        # Existing documents are replaced only after a complete candidate is
        # flushed and verified in the single allowed companion HTML.
        root = self.destination(root, source)
        if expected_bytes is not None and byte_identity(root) != expected_bytes:
            raise RuntimeError('stale-document-bytes')
        if not root.parent.exists(): root.parent.mkdir(parents=True, exist_ok=True)
        if require_absent or not root.exists():
            with root.open('xb') as stream:
                self._fault('writing')
                stream.write(raw); stream.flush(); os.fsync(stream.fileno())
        else:
            from .review_workspace import companion, hidden, work_path
            staging = work_path(companion(root))
            if staging.exists():
                raise ValueError('temporary HTML already exists; preserve this file')
            try:
                with staging.open('xb') as stream:
                    stream.seek(0)
                    self._fault('writing')
                    stream.write(raw); stream.truncate(); stream.flush(); os.fsync(stream.fileno())
                hidden(staging)
                staged = html.parse(staging)
                if (staged.graph, staged.created_utc, staged.updated_utc) != (project.graph, project.created_utc, project.updated_utc):
                    raise ValueError('save candidate verification failed')
                self._fault('staged')
                if expected_bytes is not None and byte_identity(root) != expected_bytes:
                    raise RuntimeError('stale-document-bytes')
                hidden(staging, False)
                os.replace(staging, root)
            except Exception:
                # Remove only our unchanged complete candidate. Unknown/changed
                # content is preserved; the selected document remains untouched.
                if staging.exists() and staging.read_bytes() == raw: staging.unlink()
                raise
        self._fault('published')
        check = html.parse(root)
        if (check.graph, check.created_utc, check.updated_utc) != (project.graph, project.created_utc, project.updated_utc):
            raise ValueError('saved document verification failed')
        return replace(project, path=str(root)), []


@dataclass
class Workspace:
    connection: object
    source: object
    baseline: str


class PublicationError(RuntimeError):
    """The RAM transaction committed but publishing the HTML failed."""


class ProjectFiles(ProjectStore):
    def __init__(self, write_fault=None, publication_fault=None):
        super().__init__(write_fault)
        self.engine = ProjectStore(write_fault); self.publisher = Publisher(publication_fault)
        self.workspaces = {}; self.last_warnings = []

    def _memory(self, project):
        return self.engine.initialize_memory(project.path, project.graph, created_utc=project.created_utc, updated_utc=project.updated_utc)

    def import_html(self, source, destination):
        p = self.load(source)
        return self.engine.initialize(destination, p.graph, created_utc=p.created_utc, updated_utc=p.updated_utc)

    def export_html(self, source, destination):
        result, self.last_warnings = self.publisher.publish(self.engine.load(source), destination, source=source)
        return result

    def load(self, path):
        if is_database(path): return self.engine.load(path)
        connection, project = self._memory(html.parse(path))
        connection.close()
        return project

    def initialize(self, path, graph, **metadata):
        if is_database(path): return self.engine.initialize(path, graph, **metadata)
        root = self.publisher.destination(path)
        connection, project = self.engine.initialize_memory(str(root), graph, **metadata)
        try:
            result, self.last_warnings = self.publisher.publish(project, root, require_absent=True)
            return result
        finally:
            connection.close()

    def status(self, path):
        if is_database(path): return self.engine.status(path)
        p = self.load(path)
        return {'path': p.path, 'projectId': p.graph.project_id, 'title': p.graph.title, 'purposeNodeId': p.graph.purpose_node_id, 'nodeCount': len(p.graph.nodes), 'edgeCount': len(p.graph.edges), 'stateFingerprint': p.state_fingerprint}

    def backup(self, source, destination):
        p = self.load(source)
        if is_database(destination): return self.engine.initialize(destination, p.graph, created_utc=p.created_utc, updated_utc=p.updated_utc)
        result, self.last_warnings = self.publisher.publish(p, destination, source=source, require_absent=True)
        return result

    def export_sql(self, path):
        if is_database(path): return self.engine.export_sql(path)
        connection, _ = self._memory(self.load(path))
        try: return '\n'.join(connection.iterdump()) + '\n'
        finally: connection.close()

    def begin_workspace(self, path):
        if is_database(path): return self.engine.load(path)
        root = html.safe_path(path)
        if str(root) in self.workspaces: raise ValueError('project already has an owned workspace')
        baseline = byte_identity(root)
        connection, project = self._memory(html.parse(root))
        if byte_identity(root) != baseline:
            connection.close(); raise RuntimeError('stale-document-bytes')
        self.workspaces[str(root)] = Workspace(connection, root, baseline)
        return project

    def write(self, path, project_id, base_fingerprint, proposed_fingerprint, operations):
        self.last_warnings = []
        if is_database(path): return self.engine.write(path, project_id, base_fingerprint, proposed_fingerprint, operations)
        workspace = self.workspaces[str(html.safe_path(path))]
        if byte_identity(workspace.source) != workspace.baseline: raise RuntimeError('stale-base-fingerprint')
        project = self.engine.write_connection(workspace.connection, str(workspace.source), project_id, base_fingerprint, proposed_fingerprint, operations)
        try:
            result, self.last_warnings = self.publisher.publish(project, workspace.source, expected_bytes=workspace.baseline)
            return result
        except Exception as exc:
            raise PublicationError(f'HTML save failed: {exc}. Unsaved work was discarded. Verify the selected HTML before starting a fresh session; a staged candidate may remain.') from exc
        finally:
            self.close_workspace(path)

    def close_workspace(self, path):
        workspace = self.workspaces.pop(str(html.safe_path(path)), None)
        if workspace is not None: workspace.connection.close()

    def close(self):
        for path in list(self.workspaces): self.close_workspace(path)
