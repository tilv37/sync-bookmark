// extension/lib/mock-bookmarks.js —— browser.bookmarks 的内存替身
//
// 为什么要 mock：`collect` / `apply` 一旦直接调用 `browser.*`，
// 就只能在 Firefox 里跑，测试反馈循环会长到无法忍受。
// 两个模块已经写成依赖注入，用这个替身可以在 Node 里跑。
//
// 刻意模拟了 Firefox 的几个真实特性，因为它们都是设计里要绕开的坑：
//
//   · 用「有没有 url」区分书签与文件夹（不是用 type 字段）
//   · getTree() 返回带 id="root________" 的虚拟根
//   · 四个系统根目录用固定 id
//   · remove() 对文件夹是**递归**的
//   · id 是本地整数，不可跨设备使用
//
// 用法：
//   const bm = new MockBookmarks();
//   bm.addFolder(ROOT_TOOLBAR, '工作');
//   const { nodes } = await scan(bm);

import { ROOT_TOOLBAR, ROOT_MENU, ROOT_UNFILED, ROOT_MOBILE } from './keys.js';

let nextId = 1;

export class MockBookmarks {
  /**
   * @param {object} [opts]
   * @param {string[]} [opts.roots] 实际存在的根目录（模拟 Firefox 版本差异）
   */
  constructor(opts = {}) {
    /** @type {Map<string, object>} id → 节点 */
    this.nodes = new Map();
    /** 调用日志，用于断言 API 调用的顺序与次数 */
    this.calls = [];
    this.failOn = new Set();

    const roots = opts.roots || [ROOT_TOOLBAR, ROOT_MENU, ROOT_UNFILED, ROOT_MOBILE];
    // 虚拟根
    this.nodes.set('root________', { id: 'root________', title: '', children: [] });
    for (const id of roots) {
      this._create(id, '', 'root________');
    }
    this._reindex();
  }

  // ── 构造树 ────────────────────────────────────────────────────────

  /** 创建一个节点并挂到 parentId 下。 */
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
      if (!parent) throw new Error(`父节点不存在: ${parentId}`);
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

  /** 刷新每个节点的 index（与 children 数组顺序一致）。 */
  _reindex() {
    for (const node of this.nodes.values()) {
      (node.children || []).forEach((childId, i) => {
        this.nodes.get(childId).index = i;
      });
    }
  }

  /** 返回 Firefox bookmarks.getTree() 的结构。 */
  _tree() {
    const build = (id) => {
      const n = this.nodes.get(id);
      const out = { id: n.id, title: n.title, index: n.index ?? 0 };
      if (n.url !== undefined) out.url = n.url;
      if (n.children) out.children = n.children.map(build);
      return out;
    };
    const root = this.nodes.get('root________');
    return [{ id: root.id, title: root.title, children: root.children.map(build) }];
  }

  // ── browser.bookmarks 的替身 ──────────────────────────────────────

  async getTree() {
    this.calls.push({ op: 'getTree' });
    return this._tree();
  }

  async get(id) {
    this.calls.push({ op: 'get', id });
    if (!this.nodes.has(id)) throw new Error(`no bookmark: ${id}`);
    const n = this.nodes.get(id);
    const out = { id: n.id, title: n.title, index: n.index ?? 0 };
    if (n.url !== undefined) out.url = n.url;
    if (n.children) out.children = n.children.map((c) => this.nodes.get(c).id);
    return [out];
  }

  async getChildren(id) {
    this.calls.push({ op: 'getChildren', id });
    if (!this.nodes.has(id)) throw new Error(`no bookmark: ${id}`);
    return (this.nodes.get(id).children || []).map((c) => {
      const n = this.nodes.get(c);
      const out = { id: n.id, title: n.title, index: n.index ?? 0 };
      if (n.url !== undefined) out.url = n.url;
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
    const n = this.nodes.get(id);
    if (!n) throw new Error(`no bookmark: ${id}`);
    if (changes.title !== undefined) n.title = changes.title;
    if (changes.url !== undefined) n.url = changes.url;
    return { id, title: n.title, ...(n.url !== undefined ? { url: n.url } : {}) };
  }

  async move(id, dest) {
    this.calls.push({ op: 'move', id, dest });
    this._maybeFail('move');
    const n = this.nodes.get(id);
    if (!n) throw new Error(`no bookmark: ${id}`);
    // 摘出旧位置
    const old = this.nodes.get(n.parentId);
    if (old) old.children = old.children.filter((c) => c !== id);
    // 放入新位置
    const target = this.nodes.get(dest.parentId);
    if (!target) throw new Error(`no bookmark: ${dest.parentId}`);
    const at = dest.index === undefined ? target.children.length : dest.index;
    target.children.splice(at, 0, id);
    n.parentId = dest.parentId;
    this._reindex();
    return { id, title: n.title, parentId: dest.parentId };
  }

  /** 删除。**对文件夹递归** —— 这与 Firefox 的真实行为一致。 */
  async remove(id) {
    this.calls.push({ op: 'remove', id });
    this._maybeFail('remove');
    if (!this.nodes.has(id)) throw new Error(`no bookmark: ${id}`);
    const n = this.nodes.get(id);
    const parent = this.nodes.get(n.parentId);
    if (parent) parent.children = parent.children.filter((c) => c !== id);
    this._dropRecursive(id);
  }

  _dropRecursive(id) {
    const n = this.nodes.get(id);
    if (!n) return;
    for (const c of [...(n.children || [])]) this._dropRecursive(c);
    this.nodes.delete(id);
  }

  // ── 测试辅助 ──────────────────────────────────────────────────────

  /** 让某个 op 抛错，用于验证容错路径。 */
  _maybeFail(op) {
    if (this.failOn.has(op)) {
      throw new Error(`模拟 ${op} 失败`);
    }
  }

  clearCalls() {
    this.calls = [];
  }

  ops() {
    return this.calls.map((c) => c.op);
  }

  countOp(op) {
    return this.calls.filter((c) => c.op === op).length;
  }

  /** 供断言用：把整棵树渲染成可比较的文本。 */
  dump(id = 'root________', depth = 0) {
    const lines = [];
    for (const c of this.nodes.get(id).children || []) {
      const n = this.nodes.get(c);
      const label = n.url !== undefined ? `[${n.title}](${n.url})` : `${n.title}/`;
      lines.push(`${'  '.repeat(depth)}${label}`);
      if (n.children) lines.push(...this.dump(c, depth + 1).split('\n').filter(Boolean));
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
