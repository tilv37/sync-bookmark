// extension/lib/mock-bookmarks.js — in-memory stand-in for browser.bookmarks
//
// Why a mock: once `collect` / `apply` call `browser.*` directly, tests can
// only run inside Firefox and the feedback loop becomes unbearably long.
// Both modules are dependency-injected, so this stand-in lets Node run them.
//
// It deliberately mimics several real Firefox behaviors, each a pitfall the
// design works around:
//
//   · bookmarks vs folders distinguished by "has a url" (not a type field)
//   · getTree() returns a virtual root with id="root________"
//   · four system roots use fixed ids
//   · remove() on a folder is **recursive**
//   · ids are local integers, unusable across devices
//
// Usage:
//   const bm = new MockBookmarks();
//   bm.addFolder(ROOT_TOOLBAR, 'Work');
//   const { nodes } = await scan(bm);

import { ROOT_TOOLBAR, ROOT_MENU, ROOT_UNFILED, ROOT_MOBILE } from './keys.js';

let nextId = 1;

export class MockBookmarks {
  /**
   * @param {object} [opts]
   * @param {string[]} [opts.roots] Roots that actually exist (simulates Firefox version differences)
   */
  constructor(opts = {}) {
    /** @type {Map<string, object>} id -> node */
    this.nodes = new Map();
    /** Call log, for asserting API call order and counts */
    this.calls = [];
    this.failOn = new Set();

    const roots = opts.roots || [ROOT_TOOLBAR, ROOT_MENU, ROOT_UNFILED, ROOT_MOBILE];
    // Virtual root
    this.nodes.set('root________', { id: 'root________', title: '', children: [] });
    for (const id of roots) {
      this._create(id, '', 'root________');
    }
    this._reindex();
  }

  // ── Tree construction ─────────────────────────────────────────────

  /** Create a node under parentId. */
  _create(id, title, parentId, url) {
    this.nodes.set(id, {
      id,
      title,
      ...(url === undefined || url === null ? {} : { url }),
      parentId: parentId ?? null,
      children: [],
    });
    if (parentId) {
      const parent = this.nodes.get(parentId);
      if (!parent) throw new Error(`Parent node missing: ${parentId}`);
      parent.children.push(id);
    }
    return id;
  }

  addBookmark(parentId, title, url) {
    const id = String(nextId++);
    this._create(id, title, parentId, url);
    this._reindex();
    return id;
  }

  addFolder(parentId, title) {
    const id = String(nextId++);
    this._create(id, title, parentId);
    this._reindex();
    return id;
  }

  /** Refresh each node's index (matches children array order). */
  _reindex() {
    for (const node of this.nodes.values()) {
      (node.children || []).forEach((childId, i) => {
        this.nodes.get(childId).index = i;
      });
    }
  }

  /** Return the Firefox bookmarks.getTree() shape. */
  _tree() {
    const build = (id) => {
      const node = this.nodes.get(id);
      const out = { id: node.id, title: node.title, index: node.index ?? 0 };
      if (node.url !== undefined) out.url = node.url;
      if (node.children) out.children = node.children.map(build);
      return out;
    };
    const root = this.nodes.get('root________');
    return [{ id: root.id, title: root.title, children: root.children.map(build) }];
  }

  // ── browser.bookmarks stand-in ────────────────────────────────────

  async getTree() {
    this.calls.push({ op: 'getTree' });
    return this._tree();
  }

  async get(id) {
    this.calls.push({ op: 'get', id });
    if (!this.nodes.has(id)) throw new Error(`no bookmark: ${id}`);
    const node = this.nodes.get(id);
    const out = { id: node.id, title: node.title, index: node.index ?? 0 };
    if (node.url !== undefined) out.url = node.url;
    if (node.children) out.children = node.children.map((child) => this.nodes.get(child).id);
    return [out];
  }

  async getChildren(id) {
    this.calls.push({ op: 'getChildren', id });
    if (!this.nodes.has(id)) throw new Error(`no bookmark: ${id}`);
    return (this.nodes.get(id).children || []).map((child) => {
      const node = this.nodes.get(child);
      const out = { id: node.id, title: node.title, index: node.index ?? 0 };
      if (node.url !== undefined) out.url = node.url;
      return out;
    });
  }

  async create(details) {
    this.calls.push({ op: 'create', details });
    this._maybeFail('create');
    const id = String(nextId++);
    this._create(id, details.title ?? '', details.parentId,
      details.type === 'folder' ? undefined : details.url);
    this._reindex();
    return { id, title: details.title ?? '', ...(details.url ? { url: details.url } : {}) };
  }

  async update(id, changes) {
    this.calls.push({ op: 'update', id, changes });
    this._maybeFail('update');
    const node = this.nodes.get(id);
    if (!node) throw new Error(`no bookmark: ${id}`);
    if (changes.title !== undefined) node.title = changes.title;
    if (changes.url !== undefined) node.url = changes.url;
    return { id, title: node.title, ...(node.url !== undefined ? { url: node.url } : {}) };
  }

  async move(id, dest) {
    this.calls.push({ op: 'move', id, dest });
    this._maybeFail('move');
    const node = this.nodes.get(id);
    if (!node) throw new Error(`no bookmark: ${id}`);
    // Detach from the old position
    const oldParent = this.nodes.get(node.parentId);
    if (oldParent) oldParent.children = oldParent.children.filter((child) => child !== id);
    // Attach at the new position
    const target = this.nodes.get(dest.parentId);
    if (!target) throw new Error(`no bookmark: ${dest.parentId}`);
    const at = dest.index === undefined ? target.children.length : dest.index;
    target.children.splice(at, 0, id);
    node.parentId = dest.parentId;
    this._reindex();
    return { id, title: node.title, parentId: dest.parentId };
  }

  /** Remove. **Recursive for folders** — matches real Firefox behavior. */
  async remove(id) {
    this.calls.push({ op: 'remove', id });
    this._maybeFail('remove');
    if (!this.nodes.has(id)) throw new Error(`no bookmark: ${id}`);
    const node = this.nodes.get(id);
    const parent = this.nodes.get(node.parentId);
    if (parent) parent.children = parent.children.filter((child) => child !== id);
    this._dropRecursive(id);
  }

  _dropRecursive(id) {
    const node = this.nodes.get(id);
    if (!node) return;
    for (const child of [...(node.children || [])]) this._dropRecursive(child);
    this.nodes.delete(id);
  }

  // ── Test helpers ──────────────────────────────────────────────────

  /** Make an op throw, for exercising failure paths. */
  _maybeFail(op) {
    if (this.failOn.has(op)) {
      throw new Error(`Simulated ${op} failure`);
    }
  }

  clearCalls() {
    this.calls = [];
  }

  ops() {
    return this.calls.map((call) => call.op);
  }

  countOp(op) {
    return this.calls.filter((call) => call.op === op).length;
  }

  /** Render the whole tree as comparable text, for assertions. */
  dump(id = 'root________', depth = 0) {
    const lines = [];
    for (const child of this.nodes.get(id).children || []) {
      const node = this.nodes.get(child);
      const label = node.url !== undefined ? `[${node.title}](${node.url})` : `${node.title}/`;
      lines.push(`${'  '.repeat(depth)}${label}`);
      if (node.children) lines.push(...this.dump(child, depth + 1).split('\n').filter(Boolean));
    }
    return lines.join('\n');
  }

  exists(id) {
    return this.nodes.has(id);
  }

  countNodes() {
    return this.nodes.size;
  }
}
