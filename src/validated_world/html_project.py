"""Versioned inline graph viewer; document_format owns the passive JSON data."""
from __future__ import annotations

from base64 import b64encode
from hashlib import sha256
from pathlib import Path

from .path_safety import is_junction

VIEWER_VERSION = 4
CSS = """body { max-width: 80ch; margin: 1rem auto; padding: 0 1rem; line-height: 1.5; }
a, h1, h2, h3 { overflow-wrap: anywhere; }
nav { display: flex; flex-wrap: wrap; gap: 1rem; }
pre { white-space: pre-wrap; overflow-wrap: anywhere; font: inherit; margin: 0; }
dt { font-weight: bold; margin-top: .8rem; }
dd { margin-left: 0; }
table { border-collapse: collapse; display: block; overflow-x: auto; }
td, th { text-align: left; vertical-align: top; padding: .25rem .75rem .25rem 0; }
section { margin-block: 2rem; }
"""

# This fixed script reads only embedded data and builds DOM nodes with textContent.
# Keep its bytes independent of the graph so semantic changes only touch JSON.
VIEWER = r"""(() => {
  'use strict';
  const graph = JSON.parse(document.getElementById('vw-record').textContent);
  const main = document.getElementById('main');
  const nodes = new Map(graph.nodes.map(node => [node.id, node]));
  const parents = new Map(), children = new Map(), neighbors = new Map(), tags = new Map();
  const append = (map, key, value) => {
    if (!map.has(key)) map.set(key, []);
    map.get(key).push(value);
  };
  for (const edge of graph.edges) {
    append(neighbors, edge.source, edge);
    if (edge.target !== edge.source) append(neighbors, edge.target, edge);
    if (edge.relationship === 'scope-parent') {
      parents.set(edge.source, edge.target);
      append(children, edge.target, edge.source);
    }
  }
  for (const kind of ['node', 'edge']) {
    for (const record of graph[kind + 's']) {
      for (const tag of record.tags) append(tags, tag, [kind, record.id]);
    }
  }
  const display = value => String(value).split(/(\r?\n[ \t]*\r?\n)/).map((part, index) =>
    index % 2 ? part : part.replace(/\p{L}/u, letter => letter.toUpperCase())).join('');
  const readable = value => String(value).replace(/[\u0000-\u0008\u000b-\u001f\u007f-\u009f]/g,
    char => '\\u' + char.charCodeAt(0).toString(16).padStart(4, '0'));
  const element = (name, value) => {
    const node = document.createElement(name);
    if (value !== undefined) node.textContent = readable(value);
    return node;
  };
  const anchor = (kind, id) => kind === 'node' && id === graph.purpose ? 'purpose' : kind + ':' + id;
  const link = (kind, id, label = id) => {
    const node = element('a', display(label));
    node.setAttribute('href', '#' + encodeURIComponent(anchor(kind, id)));
    return node;
  };
  const list = (parent, heading, items) => {
    if (!items.length) return;
    parent.append(element('h3', display(heading)));
    const ul = element('ul');
    for (const item of items) { const li = element('li'); li.append(item); ul.append(li); }
    parent.append(ul);
  };
  const fields = (parent, values) => {
    const dl = element('dl');
    for (const [label, value, capitalize] of values) {
      dl.append(element('dt', label));
      const dd = element('dd'); dd.append(element('pre', capitalize ? display(value) : value)); dl.append(dd);
    }
    parent.append(dl);
  };
  const section = (id, title, root = false) => {
    const node = element('section'); node.id = id;
    node.append(element(root ? 'h1' : 'h2', display(title))); return node;
  };
  const common = (parent, record) => {
    list(parent, 'Tags', record.tags.map(tag => element('pre', tag)));
    if (!record.attributes.length) return;
    parent.append(element('h3', 'Attributes'));
    const table = element('table'), head = element('thead'), row = element('tr'), body = element('tbody');
    for (const label of ['Name', 'Type', 'Value']) row.append(element('th', label));
    head.append(row); table.append(head);
    for (const attribute of record.attributes) {
      const tr = element('tr');
      for (const value of [attribute.name, attribute.type, attribute.value]) {
        const td = element('td'); td.append(element('pre', value)); tr.append(td);
      }
      body.append(tr);
    }
    table.append(body); parent.append(table);
  };
  const nodeSection = node => {
    const root = node.id === graph.purpose, view = section(anchor('node', node.id), node.id, root);
    const lineage = [], seen = new Set([node.id]); let current = node.id;
    while (parents.has(current)) {
      current = parents.get(current);
      if (seen.has(current)) break;
      seen.add(current); lineage.unshift(current);
    }
    if (lineage.length) {
      const breadcrumb = element('p'); breadcrumb.setAttribute('aria-label', 'Scope lineage');
      lineage.forEach((id, index) => { if (index) breadcrumb.append(' / '); breadcrumb.append(link('node', id)); });
      view.append(breadcrumb);
    }
    fields(view, [['Node ID', node.id], ['Kind', node.kind]]);
    view.append(element('h3', 'Claim'), element('pre', display(node.text))); common(view, node);
    list(view, 'Children', (children.get(node.id) || []).sort().map(id => link('node', id)));
    list(view, 'Connected relationships', (neighbors.get(node.id) || []).map(edge => {
      const outgoing = edge.source === node.id, other = outgoing ? edge.target : edge.source, span = element('span');
      span.append(link('edge', edge.id, edge.relationship), ' — ', link('node', other), outgoing ? ' (Outgoing)' : ' (Incoming)');
      return span;
    }));
    return view;
  };
  document.title = display(graph.purpose);
  main.append(nodeSection(nodes.get(graph.purpose)));
  const metadata = section('project', 'Project records');
  fields(metadata, [['Representation', graph.format], ['Project ID', graph.id], ['Title', graph.title, true],
    ['Purpose node', graph.purpose], ['Created (UTC)', graph.created], ['Updated (UTC)', graph.updated]]);
  list(metadata, 'Nodes', graph.nodes.map(node => link('node', node.id)));
  list(metadata, 'Edges', graph.edges.map(edge => link('edge', edge.id))); main.append(metadata);
  const index = section('tags', 'Tags');
  for (const [tag, records] of [...tags].sort(([a], [b]) => a < b ? -1 : a > b ? 1 : 0)) {
    list(index, tag, records.map(([kind, id]) => link(kind, id, kind + ': ' + id)));
  }
  main.append(index);
  for (const node of graph.nodes) if (node.id !== graph.purpose) main.append(nodeSection(node));
  for (const edge of graph.edges) {
    const view = section(anchor('edge', edge.id), edge.id);
    fields(view, [['Edge ID', edge.id], ['Source', edge.source], ['Target', edge.target],
      ['Relationship', edge.relationship], ['Review direction', edge.reviewDirection], ['Rationale', edge.rationale, true]]);
    const endpoints = element('p'); endpoints.append(link('node', edge.source), ' → ', link('node', edge.target));
    view.append(endpoints); common(view, edge); main.append(view);
  }
  // The initial fragment can precede the dynamically created target.
  try { document.getElementById(decodeURIComponent(location.hash.slice(1)))?.scrollIntoView(); } catch {}
})();"""


def safe_path(path: str | Path) -> Path:
    """Reject links before resolution, including Windows junction ancestors."""
    p = Path(path).expanduser().absolute()
    for ancestor in (p, *p.parents):
        if ancestor.is_symlink() or is_junction(ancestor):
            raise ValueError(f"linked project path is unsupported: {ancestor}")
    return p.resolve()


def render() -> str:
    script = '\n' + VIEWER + '\n'
    digest = b64encode(sha256(script.encode('utf-8')).digest()).decode('ascii')
    policy = f"default-src 'none'; script-src 'sha256-{digest}'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'"
    return '\n'.join([
        '<!doctype html>', '<html lang="en">', '<head>', '<meta charset="utf-8">',
        '<meta name="viewport" content="width=device-width, initial-scale=1">',
        f'<meta http-equiv="Content-Security-Policy" content="{policy}">',
        '<title>Project documentation</title>', f'<meta name="vw-viewer-version" content="{VIEWER_VERSION}">',
        '<style>' + CSS + '</style>', '</head>', '<body>',
        '<nav aria-label="Project navigation"><a href="#purpose">Root</a> <a href="#project">Records &amp; metadata</a> <a href="#tags">Tags</a></nav>',
        '<main id="main"></main>', '<!--vw-data-->',
        '<script id="vw-viewer">' + script + '</script>', '</body>', '</html>', ''])
