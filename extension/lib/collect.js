// extension/lib/collect.js —— 采集：Firefox 书签树 → 同步 state
//
// 本模块是纯函数式的：**依赖注入**，不直接引用 browser.*。这样可以在
// Node 里用 mock 跑测试（见 collect.test.js），不必每次都开浏览器。
//
// ── 采集要回答的问题 ──────────────────────────────────────────────────
//
// 对每个书签/文件夹，它的 key 在本地缓存里是什么？和上次相比，它：
//
//	新增 (NEW)        —— 缓存里没有这个 key
//	修改 (MODIFIED)   —— 内容 (P,T,N,U) 变了
//	复活 (RESURRECT)  —— 缓存里标记为已删除，现在又出现在树上
//	删除 (DELETED)    —— 缓存里有，但树上已经没了
//	未变化            —— 内容一致，沿用缓存里的 m（不打新时间戳）
//
// 为什么要分得这么细：**只给"真正变了"的项打新的 HLC 时间戳**。
// 如果每次同步都给所有项打新时间戳，两台设备会互相覆盖、永远收敛不了。

import {
  TYPE_BOOKMARK,
  TYPE_FOLDER,
  ROOT_TOOLBAR,
  ROOT_MOBILE,
  deriveKeys,
  isRootEnabled,
  resolveRoots,
} from './keys.js';

/** 变更类型。 */
export const CHANGE_NEW = 'new';
export const CHANGE_MODIFIED = 'modified';
export const CHANGE_RESURRECTED = 'resurrected';
export const CHANGE_DELETED = 'deleted';

export const SCHEMA_VERSION = 1;

/**
 * 把 Firefox 书签树拍平成节点数组（此时还没有 key）。
 *
 * 节点的关键字段：
 *   parentRef  父节点标识。depth === 1 时是根目录 id 字面量；
 *              否则是父节点的 **ffId**，等到上一层算完 key 再替换成父 key。
 *
 * @param {Array} nodes 累积数组（原地 push）
 * @param {object} node 当前 Firefox 节点
 * @param {string} parentRef
 * @param {number} depth 从 1 开始（根目录的直接子项是 1）
 */
function flatten(nodes, node, parentRef, depth) {
  if (!node) return;

  // Firefox 用「有没有 url」区分书签与文件夹。
  const isFolder = typeof node.url !== 'string' || node.url === '';
  const self = {
    ffId: node.id,
    type: isFolder ? TYPE_FOLDER : TYPE_BOOKMARK,
    title: node.title ?? '',
    url: isFolder ? '' : node.url,
    parentRef,
    depth,
    index: typeof node.index === 'number' ? node.index : 0,
    key: null,
    children: isFolder && Array.isArray(node.children) ? node.children : null,
  };
  nodes.push(self);

  if (self.children) {
    for (const child of self.children) {
      flatten(nodes, child, node.id, depth + 1);
    }
  }
}

/**
 * 扫描整棵书签树。
 *
 * @param {object} api browser.bookmarks 的替身（依赖注入）
 * @param {object} [opts]
 * @param {Set<string>|string[]} [opts.enabledRoots] 纳入同步的根目录
 * @param {boolean} [opts.includeMobile] 是否同步移动设备书签
 * @returns {Promise<{nodes: Map<string,object>, ffIdToKey: Map<string,string>,
 *                    ffKeyToId: Map<string,string>, warnings: string[],
 *                    rootsIncluded: string[], total: number}>}
 */
export async function scan(api, opts = {}) {
  const enabledRoots = opts.enabledRoots || new Set([ROOT_TOOLBAR]);
  const includeMobile = Boolean(opts.includeMobile);

  const tree = await api.getTree();
  const root = Array.isArray(tree) ? tree[0] : tree;
  if (!root || !Array.isArray(root.children)) {
    throw new Error('bookmarks.getTree() 返回了意外的结构');
  }

  const { roots, warned, message } = resolveRoots(root.children);
  const warnings = warned ? [message] : [];

  // ── 第一趟：拍平 ──────────────────────────────────────────────
  const flat = [];
  const rootsIncluded = [];
  for (const child of root.children) {
    if (!child || !roots.includes(child.id)) continue;
    if (child.id !== ROOT_MOBILE && !isRootEnabled(child.id, enabledRoots)) continue;
    if (child.id === ROOT_MOBILE && !includeMobile) continue;
    rootsIncluded.push(child.id);

    // 根目录**自身**不作为 item 参与同步 —— 它只作为顶层 item 的 p 值出现。
    // 若把根目录也 flatten 进去，会多出 4 条"标题为空的文件夹"上传到云端，
    // 在另一台设备上还会被建成 4 个真的空文件夹。
    // 所以这里只 flatten 它的 children。
    for (const grandChild of child.children || []) {
      flatten(flat, grandChild, child.id, 1);
    }
  }

  // ── 第二趟：按层批量算 key ────────────────────────────────────
  //
  // 必须严格自顶向下：depth === d 的节点的哈希输入里含父级 key，
  // 而父节点在 depth === d-1，不先算完就没法算这一层。
  // 逐层处理让每层的哈希输入一次性批量提交给 WebCrypto，
  // 比逐个 await 快得多。
  const byDepth = new Map();
  for (const node of flat) {
    if (!byDepth.has(node.depth)) byDepth.set(node.depth, []);
    byDepth.get(node.depth).push(node);
  }

  const ffIdToKey = new Map();
  const nodes = new Map();

  for (let depth = 1; depth <= maxDepth(flat); depth++) {
    const level = byDepth.get(depth);
    if (!level || !level.length) continue;

    // 本层的父级 key：depth 1 用根目录字面量，其余用父节点的 key
    for (const node of level) {
      if (node.depth === 1) {
        node.parentKey = node.parentRef;
      } else {
        const parentKey = ffIdToKey.get(node.parentRef);
        if (parentKey === undefined) {
          // 父节点不在同步范围内（理论上不会发生：范围是按根目录划分的，
          // 父在范围内子必然在）。记下来，由上层决定怎么办。
          warnings.push(`节点 ${node.ffId} 的父节点不在同步范围内，已跳过`);
          node.skipped = true;
        }
        node.parentKey = parentKey;
      }
    }

    const usable = level.filter((n) => !n.skipped);
    if (!usable.length) continue;

    const keys = await deriveKeys(
      usable.map((n) => ({ type: n.type, parentKey: n.parentKey, title: n.title, url: n.url })),
    );

    usable.forEach((node, i) => {
      node.key = keys[i];
      ffIdToKey.set(node.ffId, node.key);
      nodes.set(node.key, {
        type: node.type,
        title: node.title,
        url: node.url,
        parentKey: node.parentKey,
        depth: node.depth,
        ffId: node.ffId,
        index: node.index,
      });
    });
  }

  const ffKeyToId = new Map();
  for (const [ffId, key] of ffIdToKey) ffKeyToId.set(key, ffId);

  return { nodes, ffIdToKey, ffKeyToId, warnings, rootsIncluded, total: nodes.size };
}

function maxDepth(nodes) {
  let max = 0;
  for (const n of nodes) if (n.depth > max) max = n.depth;
  return max;
}

/**
 * 对比扫描结果与本地缓存，产出待上报的 state。
 *
 * @param {object} scanResult scan() 的返回值
 * @param {object} cached     上次从服务端拿到的 state（{v, hlc, items}）
 * @param {{now: () => string}} clock HLC 实例
 * @param {number} nowMs      当前墙钟毫秒，写进墓碑的 x 字段
 * @returns {{state: object, base: Record<string,string>, changes: object[], stats: object}}
 */
export function buildState(scanResult, cached, clock, nowMs) {
  const prevItems = (cached && cached.items) || {};
  const items = {};
  const base = {};
  const changes = [];
  const stats = { created: 0, updated: 0, deleted: 0, unchanged: 0, resurrected: 0 };

  for (const [key, node] of scanResult.nodes) {
    const prev = prevItems[key];

    if (!prev) {
      const m = clock.now();
      items[key] = newItem(node, m, m);
      changes.push({ key, change: CHANGE_NEW, depth: node.depth });
      stats.created += 1;
      continue;
    }

    // 无论内容变没变，都要记下"上次见到的 m" ——
    // 服务端靠它区分「我改了」和「对面改了」。缺了这个，冲突日志全是噪声。
    base[key] = prev.m;

    if (prev.d) {
      // 之前被删过，现在又出现在树上：复活。
      // 关键是给一个**更新的 m**，否则墓碑继续赢，书签永远回不来。
      const m = clock.now();
      items[key] = newItem(node, m, prev.a || m);
      changes.push({ key, change: CHANGE_RESURRECTED, depth: node.depth });
      stats.resurrected += 1;
      continue;
    }

    if (contentChanged(prev, node)) {
      const m = clock.now();
      items[key] = newItem(node, m, prev.a || m);
      changes.push({ key, change: CHANGE_MODIFIED, depth: node.depth });
      stats.updated += 1;
    } else {
      // 没变：原样带过去，**不重新打时间戳**。
      // 这是"两台设备不会互相覆盖"的关键。
      items[key] = prev;
      stats.unchanged += 1;
    }
  }

  // 缓存里有、但树上已经没有的 → 写墓碑
  //
  // 特别注意：**子树的每一项都各自写墓碑**，不能只写被删的那个文件夹。
  // 服务端只知道"这一项没了"，它不会替你推断子项也该没了。
  for (const [key, prev] of Object.entries(prevItems)) {
    if (scanResult.nodes.has(key)) continue;
    base[key] = prev.m;
    if (prev.d) {
      // 已经是墓碑：原样带过去。否则它会被 GC 掉，删除就传播不下去了。
      items[key] = prev;
      continue;
    }
    const m = clock.now();
    items[key] = { ...stripUndefined(prev), a: prev.a || m, m, d: true, x: nowMs };
    changes.push({ key, change: CHANGE_DELETED });
    stats.deleted += 1;
  }

  return { state: { v: SCHEMA_VERSION, items }, base, changes, stats };
}

function newItem(node, m, a) {
  return {
    p: node.parentKey,
    t: node.type,
    n: node.title,
    ...(node.url ? { u: node.url } : {}),
    a,
    m,
  };
}

/**
 * 内容是否变化。
 *
 * 刻意**不比较 m / a / d / x** —— 时间戳不是内容。两端各自把同一个书签
 * 重新保存一次，内容完全一样，不应被当成冲突。
 */
export function contentChanged(prev, node) {
  return (
    (prev.p || '') !== (node.parentKey || '') ||
    (prev.t || '') !== node.type ||
    (prev.n || '') !== (node.title || '') ||
    (prev.u || '') !== (node.url || '')
  );
}

function stripUndefined(obj) {
  const out = {};
  for (const [k, v] of Object.entries(obj)) {
    if (v !== undefined && v !== '') out[k] = v;
  }
  return out;
}
