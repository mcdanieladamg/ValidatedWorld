"""One launch-selected filesystem boundary for persistent authoring sessions."""

import os
from pathlib import Path

from .html_project import safe_path
from .path_safety import is_junction


class ProjectPaths:
    def __init__(self, root):
        selected = safe_path(root)
        if not selected.is_dir():
            raise ValueError('the session project folder must already exist')
        self.root = str(selected)
        self.prefix = self.root.rstrip(os.sep) + os.sep

    def file(self, value):
        if not isinstance(value, str) or not value.strip() or any(ord(c) < 32 for c in value):
            raise ValueError('file paths must be nonempty text without control characters')
        if os.name == 'nt':
            drive, tail = os.path.splitdrive(value)
            if (drive and not tail.startswith(('\\', '/'))) or ':' in tail:
                raise ValueError('drive-relative paths and alternate data streams are unsupported')
            for part in tail.replace('\\', '/').split('/'):
                stem = part.rstrip(' .').split('.')[0].upper()
                if (part.endswith((' ', '.')) and part not in {'.', '..'}) or stem in {
                    'CON', 'PRN', 'AUX', 'NUL', 'CONIN$', 'CONOUT$',
                    *(f'COM{i}' for i in '123456789¹²³'), *(f'LPT{i}' for i in '123456789¹²³'),
                }:
                    raise ValueError('Windows device names and ambiguous path components are unsupported')
        # Normalize before the separator-aware prefix check. Never construct a
        # filesystem Path from the original request value. Absolute paths are
        # permitted only inside the independently selected startup root.
        full = os.path.abspath(os.path.join(self.root, os.path.expanduser(value)))
        if os.name == 'nt':
            # Accept ordinary Windows casing variations without lowercasing
            # filenames or substituting a differently cased startup folder.
            if os.path.normcase(full) == os.path.normcase(self.root):
                return self.root
            if os.path.normcase(full).startswith(os.path.normcase(self.prefix)):
                full = self.root + full[len(self.root):]
        full = os.path.abspath(full)
        if full == self.root:
            return self.root
        if not full.startswith(self.prefix):
            raise ValueError('file path is outside the session project folder')
        resolved = os.path.realpath(full)
        if resolved != self.root and not resolved.startswith(self.prefix):
            raise ValueError('file path resolves outside the session project folder')
        candidate = Path(full)
        for ancestor in (candidate, *candidate.parents):
            if ancestor.is_symlink() or is_junction(ancestor):
                raise ValueError('linked paths are unsupported in a project session')
            if str(ancestor) == self.root:
                break
        return full

    def template(self, value):
        # Return literal built-in names, never an unchecked request path.
        if value == 'code-development': return 'code-development'
        if value == 'research-notebook': return 'research-notebook'
        return self.file(value)
