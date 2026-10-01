// extension/lib/collect.js — collect: Firefox bookmark tree -> sync state
//
// This module is purely functional: **dependency-injected**, never touches
// browser.* directly. That lets Node run it with a mock (see
// collect.test.js) without opening a browser.
//
// ── What collection answers ───────────────────────────────────────────
// For each bookmark/folder, what is its key in the local cache, and compared
// to last time, is it:
//
//   NEW         — key not in the cache
//   MODIFIED    — content (P,T,N,U) changed
//   RESURRECTED — marked deleted in the cache, but back on the tree
//   DELETED     — in the cache, but gone from the tree
//   unchanged   — content identical, reuse the cached m (no fresh timestamp)
//
// Why split this finely: **only stamp a fresh HLC on what really changed**.
// Stamping everything every round makes two devices overwrite each other and
// never converge.

import {
  TYPE_BOOKMARK,
  TYPE_FOLDER,
  ROOT_TOOLBAR,
  ROOT_MOBILE,
  deriveKeys,
  isRootEnabled,
  resolveRoots,
} from './keys.js';

/** Change types. */
export const CHANGE_NEW = 'new';
export const CHANGE_MODIFIED = 'modified';
export const CHANGE_RESURRECTED = 'resurrected';
export const CHANGE_DELETED = 'deleted';

export const SCHEMA_VERSION = 1;

/**
 * Flatten the Firefox bookmark tree into a node array (keys not yet computed).
 *
 * Key node fields:
 *   parentRef  Parent identifier. A root-dir literal when depth === 1;
 *              otherwise the parent node's **firefoxLocalId**, replaced with
 *              the parent key once the upper level has been hashed.
 *
 * @param {Array} nodes Accumulator array (pushed in place)
 * @param {object} node Current Firefox node
 * @param {string} parentRef
 * @param {number} depth Starts at 1 (direct children of a root are 1)
 */
function flatten(nodes, node, parentRef, depth) {
  if (!node) return;

  // Firefox distinguishes bookmarks from folders by "has a url".
  const isFolder = typeof node.url !== 'string' || node.url === '';
  const self = {
    firefoxLocalId: node.id,
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
 * Scan the whole bookmark tree.
 *
 * @param {object} api A browser.bookmarks stand-in (dependency injection)
 * @param {object} [opts]
 * @param {Set<string>|string[]} [opts.enabledRoots] Roots included in sync
 * @param {boolean} [opts.includeMobile] Whether to sync mobile bookmarks
 * @returns {Promise<{nodes: Map<string,object>, firefoxLocalIdToKey: Map<string,string>,
 *                    firefoxIdBySyncKey: Map<string,string>, warnings: string[],
 *                    rootsIncluded: string[], total: number}>}
 */
export async function scan(api, opts = {}) {
  const enabledRoots = opts.enabledRoots || new Set([ROOT_TOOLBAR]);
  const includeMobile = Boolean(opts.includeMobile);

  const tree = await api.getTree();
  const root = Array.isArray(tree) ? tree[0] : tree;
  if (!root || !Array.isArray(root.children)) {
    throw new Error('bookmarks.getTree() returned an unexpected shape');
  }

  const { roots, warned, message } = resolveRoots(root.children);
  const warnings = warned ? [message] : [];

  // ── Pass 1: flatten ───────────────────────────────────────────
  const flat = [];
  const rootsIncluded = [];
  for (const child of root.children) {
    if (!child || !roots.includes(child.id)) continue;
    if (child.id !== ROOT_MOBILE && !isRootEnabled(child.id, enabledRoots)) continue;
    if (child.id === ROOT_MOBILE && !includeMobile) continue;
    rootsIncluded.push(child.id);

    // The root dir **itself** is not an item — it only appears as the p value
    // of top-level items. Flattening it would upload 4 extra "empty-title
    // folders" to the cloud, which the other device would then create as 4
    // real empty folders. So only flatten its children.
    for (const grandChild of child.children || []) {
      flatten(flat, grandChild, child.id, 1);
    }
  }

  // ── Pass 2: hash keys level by level ──────────────────────────
  //
  // Strictly top-down: a node at depth d hashes its parent key, and the
  // parent lives at depth d-1, so the upper level must finish first.
  // Per-level batching submits each level to WebCrypto at once, far faster
  // than awaiting one by one.
  const byDepth = new Map();
  for (const node of flat) {
    if (!byDepth.has(node.depth)) byDepth.set(node.depth, []);
    byDepth.get(node.depth).push(node);
  }

  const firefoxLocalIdToKey = new Map();
  const nodes = new Map();

  for (let depth = 1; depth <= maxDepth(flat); depth++) {
    const level = byDepth.get(depth);
    if (!level || !level.length) continue;

    // Parent key for this level: root literal at depth 1, else the parent key
    for (const node of level) {
      if (node.depth === 1) {
        node.parentKey = node.parentRef;
      } else {
        const parentKey = firefoxLocalIdToKey.get(node.parentRef);
        if (parentKey === undefined) {
          // Parent outside the sync scope (theoretically impossible: scope is
          // partitioned by root, so an in-scope child always has an in-scope
          // parent). Record it and let the caller decide.
          warnings.push(`Node ${node.firefoxLocalId} has its parent outside the sync scope, skipped`);
          node.skipped = true;
        }
        node.parentKey = parentKey;
      }
    }

    const usable = level.filter((node) => !node.skipped);
    if (!usable.length) continue;

    const keys = await deriveKeys(
      usable.map((node) => ({ type: node.type, parentKey: node.parentKey, title: node.title, url: node.url })),
    );

    usable.forEach((node, i) => {
      node.key = keys[i];
      firefoxLocalIdToKey.set(node.firefoxLocalId, node.key);
      nodes.set(node.key, {
        type: node.type,
        title: node.title,
        url: node.url,
        parentKey: node.parentKey,
        depth: node.depth,
        firefoxLocalId: node.firefoxLocalId,
        index: node.index,
      });
    });
  }

  const firefoxIdBySyncKey = new Map();
  for (const [firefoxLocalId, key] of firefoxLocalIdToKey) firefoxIdBySyncKey.set(key, firefoxLocalId);

  return { nodes, firefoxLocalIdToKey, firefoxIdBySyncKey, warnings, rootsIncluded, total: nodes.size };
}

function maxDepth(nodes) {
  let deepest = 0;
  for (const node of nodes) if (node.depth > deepest) deepest = node.depth;
  return deepest;
}

/**
 * Diff a scan against the local cache, producing the state to upload.
 *
 * @param {object} scanResult scan() return value
 * @param {object} cached     Last state fetched from the server ({v, hlc, items})
 * @param {{now: () => string}} clock HLC instance
 * @param {number} nowMs      Current wall-clock ms, written into tombstone x
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
      const modifiedHlc = clock.now();
      items[key] = newItem(node, modifiedHlc, modifiedHlc);
      changes.push({ key, change: CHANGE_NEW, depth: node.depth });
      stats.created += 1;
      continue;
    }

    // Always record "the m we last saw", whether content changed or not —
    // the server uses it to tell "I changed" from "the other side changed".
    // Without it, the conflict log is pure noise.
    base[key] = prev.m;

    if (prev.d) {
      // Previously deleted, now back on the tree: resurrect.
      // The point is a **newer m**, otherwise the tombstone keeps winning
      // and the bookmark never comes back.
      const modifiedHlc = clock.now();
      items[key] = newItem(node, modifiedHlc, prev.a || modifiedHlc);
      changes.push({ key, change: CHANGE_RESURRECTED, depth: node.depth });
      stats.resurrected += 1;
      continue;
    }

    if (contentChanged(prev, node)) {
      const modifiedHlc = clock.now();
      items[key] = newItem(node, modifiedHlc, prev.a || modifiedHlc);
      changes.push({ key, change: CHANGE_MODIFIED, depth: node.depth });
      stats.updated += 1;
    } else {
      // Unchanged: carry over untouched, **no fresh timestamp**.
      // This is what keeps two devices from overwriting each other.
      items[key] = prev;
      stats.unchanged += 1;
    }
  }

  // In the cache but gone from the tree → write tombstones
  //
  // Note: **every item of a deleted subtree gets its own tombstone**; writing
  // only the removed folder is not enough. The server only knows "this item
  // is gone" and will not infer the children.
  for (const [key, prev] of Object.entries(prevItems)) {
    if (scanResult.nodes.has(key)) continue;
    base[key] = prev.m;
    if (prev.d) {
      // Already a tombstone: carry over untouched, or GC would drop it and
      // the deletion would stop propagating.
      items[key] = prev;
      continue;
    }
    const modifiedHlc = clock.now();
    items[key] = { ...stripUndefined(prev), a: prev.a || modifiedHlc, m: modifiedHlc, d: true, x: nowMs };
    changes.push({ key, change: CHANGE_DELETED });
    stats.deleted += 1;
  }

  return { state: { v: SCHEMA_VERSION, items }, base, changes, stats };
}

function newItem(node, modifiedHlc, createdHlc) {
  return {
    p: node.parentKey,
    t: node.type,
    n: node.title,
    ...(node.url ? { u: node.url } : {}),
    a: createdHlc,
    m: modifiedHlc,
  };
}

/**
 * Whether content changed.
 *
 * Deliberately **ignores m / a / d / x** — timestamps are not content. Both
 * sides re-saving the same bookmark with identical content must not count
 * as a conflict.
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
  for (const [key, value] of Object.entries(obj)) {
    if (value !== undefined && value !== '') out[key] = value;
  }
  return out;
}
