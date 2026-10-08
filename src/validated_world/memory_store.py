"""Verified, fixed-schema SQLite sessions without filesystem workspaces."""
import sqlite3

from .storage import ProjectStore, utc_now
from .validation import validate_graph


class _Connection(sqlite3.Connection):
    def commit(self):
        super().commit()
        self.owner.snapshots[self.key] = self.serialize()


class MemoryStore(ProjectStore):
    def __init__(self, write_fault=None):
        super().__init__(write_fault)
        self.snapshots = {}

    def _open(self, path, read_only=False):
        connection = sqlite3.connect(':memory:', factory=_Connection)
        connection.owner = self
        connection.key = str(path)
        connection.row_factory = sqlite3.Row
        if str(path) in self.snapshots: connection.deserialize(self.snapshots[str(path)])
        connection.execute('pragma foreign_keys = ON')
        connection.execute('pragma trusted_schema = ON')
        connection.execute('pragma temp_store = MEMORY')
        if read_only: connection.execute('pragma query_only = ON')
        return connection

    def initialize(self, path, graph, *, created_utc=None, updated_utc=None):
        key = str(path)
        if key in self.snapshots: raise FileExistsError(key)
        validation = validate_graph(graph)
        if not validation.is_valid: raise ValueError(validation.diagnostics[0].message)
        connection = self._open(key)
        try:
            self._apply_schema(connection)
            now = utc_now()
            self._insert_graph(connection, graph, created_utc or now, updated_utc or now)
            connection.commit()
            return self._load_connection(connection, key)
        finally:
            connection.close()

    def load(self, path):
        key = str(path)
        if key not in self.snapshots: raise FileNotFoundError(key)
        connection = self._open(key, True)
        try: return self._load_connection(connection, key)
        finally: connection.close()
