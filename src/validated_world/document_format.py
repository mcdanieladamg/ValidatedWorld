"""Self-contained documentation: one authoritative graph JSON block in one HTML file."""
from __future__ import annotations

from html.parser import HTMLParser
import json
from pathlib import Path
import re

from .canonical import state_fingerprint
from . import html_project as viewer
from .models import Attribute, Edge, Graph, GraphValue, GraphValueKind, Node, ReviewDirection, camel_enum
from .protocol import json_loads_strict
from .storage import StoredProject
from .validation import validate_graph

FORMAT = 'validated-world-document-2'
safe_path = viewer.safe_path


class HtmlParseError(ValueError):
    pass


def _attributes(attributes):
    return [{'name': a.name, 'type': a.value.kind.name.lower(), 'value': str(a.value) if a.value.kind == GraphValueKind.INTEGER else a.value.value} for a in attributes]


def node_data(node):
    return {'id': node.id, 'text': node.text, 'kind': node.kind, 'tags': list(node.tags), 'attributes': _attributes(node.attributes)}


def edge_data(edge):
    return {'id': edge.id, 'source': edge.source, 'target': edge.target, 'relationship': edge.relationship, 'reviewDirection': camel_enum(edge.review_direction), 'rationale': edge.rationale, 'tags': list(edge.tags), 'attributes': _attributes(edge.attributes)}


def render(project):
    graph = project.graph
    document = viewer.render()
    record = {'format': FORMAT, 'id': graph.project_id, 'title': graph.title, 'purpose': graph.purpose_node_id,
              'created': project.created_utc, 'updated': project.updated_utc,
              'nodes': [node_data(n) for n in graph.nodes], 'edges': [edge_data(e) for e in graph.edges]}
    # JSON is passive data. Escaping '<' prevents text from closing its block.
    data = json.dumps(record, ensure_ascii=False, indent=2, allow_nan=False).replace('<', '\\u003c').replace('>', '\\u003e').replace('&', '\\u0026')
    block = '<script type="application/json" id="vw-record">\n' + data + '\n</script>\n'
    return document.replace('<!--vw-data-->\n', block)


class _JsonBlock(HTMLParser):
    def __init__(self, filename):
        super().__init__(convert_charrefs=False)
        self.filename = filename; self.active = False; self.seen = False; self.closed = False; self.parts = []

    def fail(self, message):
        line, column = self.getpos()
        raise HtmlParseError(f'{self.filename}:{line}:{column}: {message}')

    def handle_starttag(self, tag, attrs):
        if dict(attrs).get('id') != 'vw-record': return
        if self.seen: self.fail('duplicate JSON record')
        if len({k for k, _ in attrs}) != len(attrs): self.fail('duplicate record markup attribute')
        if tag != 'script' or dict(attrs).get('type') != 'application/json': self.fail('record must be a non-executing application/json block')
        self.seen = self.active = True

    def handle_endtag(self, tag):
        if tag == 'script' and self.active: self.active = False; self.closed = True

    def handle_data(self, data):
        if self.active: self.parts.append(data)


def _read(path, required):
    parser = _JsonBlock(str(path))
    try:
        parser.feed(path.read_text(encoding='utf-8')); parser.close()
        if not parser.closed or parser.active: parser.fail('missing or unclosed JSON record')
        data = json_loads_strict(''.join(parser.parts))
        if not isinstance(data, dict) or set(data) != set(required): parser.fail('missing or unsupported JSON record fields')
        return data
    except (UnicodeError, ValueError) as exc:
        if isinstance(exc, HtmlParseError): raise
        raise HtmlParseError(f'{path}: JSON parse: {exc}') from exc


def _read_attributes(values):
    if not isinstance(values, list): raise ValueError('attributes must be an array')
    result = []
    for value in values:
        if not isinstance(value, dict) or set(value) != {'name', 'type', 'value'}: raise ValueError('invalid attribute record')
        kind = GraphValueKind[value['type'].upper()]; scalar = value['value']
        if kind == GraphValueKind.INTEGER:
            if not isinstance(scalar, str) or not re.fullmatch(r'-?(0|[1-9][0-9]*)', scalar) or scalar == '-0': raise ValueError('integer requires canonical signed decimal text')
            scalar = int(scalar)
        result.append(Attribute(value['name'], GraphValue(kind, scalar)))
    return tuple(result)


def parse(path):
    path = safe_path(path)
    p = _read(path, ('format', 'id', 'title', 'purpose', 'created', 'updated', 'nodes', 'edges'))
    if p['format'] != FORMAT: raise HtmlParseError(f'{path}: unsupported documentation representation')
    nodes = []; edges = []
    for kind, output in [('node', nodes), ('edge', edges)]:
        inventory = p[kind + 's']
        if not isinstance(inventory, list): raise HtmlParseError(f'{path}: {kind} records must be an array')
        required = ('id', 'text', 'kind', 'tags', 'attributes') if kind == 'node' else ('id', 'source', 'target', 'relationship', 'reviewDirection', 'rationale', 'tags', 'attributes')
        for index, data in enumerate(inventory):
            try:
                if not isinstance(data, dict) or set(data) != set(required): raise ValueError('missing or unsupported record fields')
                if not isinstance(data['tags'], list): raise ValueError('tags must be an array')
                attributes = _read_attributes(data['attributes'])
                if kind == 'node': output.append(Node(data['id'], data['text'], data['kind'], tuple(data['tags']), attributes))
                else:
                    direction = {'none': 0, 'sourceToTarget': 1, 'targetToSource': 2, 'both': 3}[data['reviewDirection']]
                    output.append(Edge(data['id'], data['source'], data['target'], data['relationship'], ReviewDirection(direction), data['rationale'], tuple(data['tags']), attributes))
            except (KeyError, TypeError, AttributeError, ValueError) as exc: raise HtmlParseError(f'{path}: {kind}[{index}]: JSON record: {exc}') from exc
    graph = Graph(p['id'], p['title'], p['purpose'], tuple(nodes), tuple(edges))
    validation = validate_graph(graph)
    if not validation.is_valid: raise ValueError('Documentation graph validation: ' + validation.diagnostics[0].message)
    for name in ('created', 'updated'):
        if not isinstance(p[name], str) or not re.fullmatch(r'\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}\+00:00', p[name]): raise HtmlParseError(f'{path}: invalid UTC timestamp: {name}')
    return StoredProject(str(path), graph, state_fingerprint(graph), p['created'], p['updated'])


def write_stage(project, path: Path, *, append=False):
    import os
    size = path.stat().st_size if append else None
    with path.open('a' if append else 'x', encoding='utf-8', newline='\n') as stream:
        try:
            stream.write(render(project)); stream.flush(); os.fsync(stream.fileno())
        except BaseException:
            stream.close()
            if append:
                with path.open('r+b') as rollback: rollback.truncate(size)
            else: path.unlink()
            raise
    try:
        check = parse(path)
        if (check.graph, check.created_utc, check.updated_utc) != (project.graph, project.created_utc, project.updated_utc): raise ValueError('export round-trip verification failed')
    except BaseException:
        if append:
            with path.open('r+b') as rollback: rollback.truncate(size)
        else: path.unlink()
        raise
