// extension/lib/apply.js — apply: sync state -> Firefox bookmark tree
//
// Mirror image of collect. Also dependency-injected, never touches browser.*.
//
// ── Three ordering constraints (breaking any one of them fails) ─────────
//  1. **Delete before create**. Creating first makes a "locally present but
//     remotely deleted" item look "already there" and skipped, so the
//     deletion never propagates.
//  2. **Delete only the top level**. bookmarks.remove on a folder is
//     recursive; children disappear with it, and a second remove on a child
//     fails because the node is already gone.
//  3. **Create in ascending depth**. A parent folder must land first, or
//     create's parentId is invalid. Same for move — it may target a
//     not-yet-created directory.
//
// ── No rollback ───────────────────────────────────────────────────────
// On mid-flight failure, **do not roll back**. Rationale in docs/architecture.md §4:
// the Firefox bookmark tree has no transactions, and reverse-rolling every
// step costs far more than it saves; LWW merge directionality guarantees
// "retry only gets closer to the correct state, never destroys". Instead,
// record the applied part and prompt for retry in the UI.

import { TYPE_FOLDER, isRootFolder } from './keys.js';

/**
 * Compute an apply plan.
 *
 * Split into "plan" and "execute" so the ordering constraints can be
 * asserted directly by unit tests without touching a real (or mock) tree.
 *
 * @param {Map<string,object>} liveNodes   scan() result: current local state
 * @param {object} incomingState          Authoritative state from the server
 * @param {Map<string,string>} firefoxIdBySyncKey   key -> Firefox local id
 * @returns {{removes: object[], upserts: object[], skipped: object[], counts: object}}
 */
export function planApply(liveNodes, incomingState, firefoxIdBySyncKey) {
  const incoming = (incomingState && incomingState.items) || {};

  // desired: every **live** item in the server state
  const desired = new Map();
  for (const [key, item] of Object.entries(incoming)) {
    if (item && item.d) continue; // tombstones never land
    desired.set(key, item);
  }

  // ── Delete phase ──────────────────────────────────────────────
  // Find keys "locally present, remotely absent". Keep only those whose
  // parent is NOT also in the delete set — they are the tops of deleted
  // subtrees, and recursive remove clears the descendants with them.
  const toDelete = new Set();
  for (const key of liveNodes.keys()) {
    if (!desired.has(key)) toDelete.add(key);
  }

  const removes = [];
  for (const key of toDelete) {
    const parentKey = liveNodes.get(key).parentKey;
    if (parentKey && toDelete.has(parentKey)) continue; // parent covers it
    const firefoxLocalId = firefoxIdBySyncKey.get(key);
    if (firefoxLocalId === undefined) continue; // node already gone
    removes.push({ key, firefoxLocalId, type: liveNodes.get(key).type });
  }

  // ── Create / update phase ─────────────────────────────────────
  // Depth of each item to land, for sorting. Depth walks up the parentKey
  // chain; items with a broken parent chain cannot be placed and go to skipped.
  const depthOf = (key, seen = new Set()) => {
    if (seen.has(key)) return -1; // cycle
    seen.add(key);
    const item = desired.get(key);
    if (!item) return -1;
    if (isRootFolder(item.p)) return 1;
    if (!desired.has(item.p)) return -1; // parent missing from cloud state
    const parentDepth = depthOf(item.p, seen);
    return parentDepth < 0 ? -1 : parentDepth + 1;
  };

  const upserts = [];
  const skipped = [];
  for (const [key, want] of desired) {
    const depth = depthOf(key);
    if (depth < 0) {
      // Missing parent or cycle. **Never park it at the root** — silently
      // moving a user's bookmark under "Other Bookmarks" is worse than skipping.
      skipped.push({ key, reason: 'parent-missing', want });
      continue;
    }

    const live = liveNodes.get(key);
    if (live) {
      const upsertOp = { key, depth, firefoxLocalId: live.firefoxLocalId, want, live };
      upsertOp.titleChanged = live.title !== (want.n || '');
      upsertOp.urlChanged = live.url !== (want.u || '');
      upsertOp.parentChanged = (live.parentKey || '') !== (want.p || '');
      upsertOp.typeChanged = live.type !== want.t;
      if (upsertOp.titleChanged || upsertOp.urlChanged || upsertOp.parentChanged || upsertOp.typeChanged) {
        upserts.push(upsertOp);
      }
      continue;
    }
    upserts.push({ key, depth, firefoxLocalId: null, want, live: null, isCreate: true });
  }

  // Ascending depth; same depth sorted by key for determinism
  upserts.sort((left, right) => left.depth - right.depth || (left.key < right.key ? -1 : left.key > right.key ? 1 : 0));

  return {
    removes,
    upserts,
    skipped,
    // firefoxIdBySyncKey is the full key -> Firefox id table of **already
    // existing local** nodes.
    //
    // Why it must be passed separately: upserts only holds "items needing a
    // change", but moving a bookmark into a "locally present yet unchanged"
    // directory references a directory that is not in upserts at all. With
    // nowhere to look it up, execution reports "parent missing" and the move
    // silently fails — the bookmark stays put while sync reports success.
    //
    // This bug was caught by apply.test.js's "parent changed -> move" case.
    firefoxIdBySyncKey: new Map([...liveNodes].map(([key, node]) => [key, node.firefoxLocalId]).filter(([, id]) => id)),
    counts: {
      remove: removes.length,
      create: upserts.filter((upsertOp) => upsertOp.isCreate).length,
      update: upserts.filter((upsertOp) => !upsertOp.isCreate).length,
      moved: upserts.filter((upsertOp) => !upsertOp.isCreate && upsertOp.parentChanged).length,
      skipped: skipped.length,
    },
  };
}

/**
 * Execute an apply plan.
 *
 * @param {object} api browser.bookmarks stand-in
 * @param {object} plan planApply() return value
 * @param {object} [opts]
 * @param {(msg: string) => void} [opts.onProgress] Progress callback for long tasks
 * @returns {Promise<{created:number, updated:number, moved:number, deleted:number, failed:object[]}>}
 */
export async function executePlan(api, plan, opts = {}) {
  const { onProgress } = opts;
  const result = { created: 0, updated: 0, moved: 0, deleted: 0, failed: [] };

  // ── Phase 1: deletes ──────────────────────────────────────────
  for (const removeOp of plan.removes) {
    try {
      await api.remove(removeOp.firefoxLocalId);
      result.deleted += 1;
    } catch (err) {
      // The node may already have been deleted by the user. Not a failure.
      if (!isNotFound(err)) {
        result.failed.push({ op: 'remove', key: removeOp.key, error: String(err) });
      }
    }
  }
  onProgress?.(`Deleted ${result.deleted} items`);

  // ── Phase 2: creates / updates ────────────────────────────────
  //
  // Ascending depth already guarantees parents exist before children. One more
  // case: the target parent may not exist locally (on the server, but its
  // local folder is excluded). Then create fails — recorded in failed, never
  // silently parked at the root, which is harder to debug than an error.
  // Full id table of existing nodes (from planApply), plus whatever this run
  // creates. Order matters: freshly created parents must be visible before
  // their children, so the table grows as execution proceeds.
  const liveIds = new Map(plan.firefoxIdBySyncKey || []);
  // Back-compat: older plans used ffIdByKey.
  if (plan.ffIdByKey) {
    for (const [key, id] of plan.ffIdByKey) {
      if (!liveIds.has(key)) liveIds.set(key, id);
    }
  }
  for (const upsertOp of plan.upserts) {
    if (upsertOp.firefoxLocalId && !liveIds.has(upsertOp.key)) liveIds.set(upsertOp.key, upsertOp.firefoxLocalId);
  }

  for (const upsertOp of plan.upserts) {
    try {
      if (upsertOp.isCreate) {
        const parentId = resolveParentId(api, upsertOp, liveIds);
        if (parentId === null) {
          result.failed.push({ op: 'create', key: upsertOp.key, error: 'parent folder missing' });
          continue;
        }
        const node = await api.create({
          parentId,
          title: upsertOp.want.n || '',
          ...(upsertOp.want.t === TYPE_FOLDER ? {} : { url: upsertOp.want.u || '' }),
        });
        liveIds.set(upsertOp.key, node.id);
        result.created += 1;
        continue;
      }

      // Update: move first, then change attributes. Reversed, the update lands on the old position.
      if (upsertOp.parentChanged) {
        const parentId = resolveParentId(api, upsertOp, liveIds);
        if (parentId === null) {
          result.failed.push({ op: 'move', key: upsertOp.key, error: 'parent folder missing' });
          continue;
        }
        await api.move(upsertOp.firefoxLocalId, { parentId });
        result.moved += 1;
      }
      // Type changed (bookmark <-> folder) can only be delete + recreate:
      // bookmarks.update cannot change the type.
      if (upsertOp.typeChanged) {
        await api.remove(upsertOp.firefoxLocalId);
        const parentId = resolveParentId(api, upsertOp, liveIds);
        if (parentId === null) {
          result.failed.push({ op: 'recreate', key: upsertOp.key, error: 'parent folder missing' });
          continue;
        }
        const node = await api.create({
          parentId,
          title: upsertOp.want.n || '',
          ...(upsertOp.want.t === TYPE_FOLDER ? {} : { url: upsertOp.want.u || '' }),
        });
        liveIds.set(upsertOp.key, node.id);
        result.created += 1;
        continue;
      }
      if (upsertOp.titleChanged || upsertOp.urlChanged) {
        await api.update(upsertOp.firefoxLocalId, {
          ...(upsertOp.titleChanged ? { title: upsertOp.want.n || '' } : {}),
          ...(upsertOp.urlChanged && upsertOp.want.u ? { url: upsertOp.want.u } : {}),
        });
        result.updated += 1;
      }
    } catch (err) {
      if (!isNotFound(err)) {
        result.failed.push({ op: 'update', key: upsertOp.key, error: String(err) });
      }
    }
  }
  onProgress?.(`Created ${result.created}, updated ${result.updated}, moved ${result.moved}`);

  return result;
}

/**
 * Resolve the Firefox id of a parent folder.
 *
 * Top-level items parent to one of the four roots — not items themselves,
 * but their Firefox ids are **fixed literals** usable directly.
 */
function resolveParentId(api, upsertOp, liveIds) {
  const parentKey = upsertOp.want.p;
  // Top-level parents are the four roots — not items, but their Firefox ids
  // are **fixed literals**, usable directly.
  if (isRootFolder(parentKey)) return parentKey;
  const id = liveIds.get(parentKey);
  return id === undefined ? null : id;
}

function isNotFound(err) {
  const msg = String(err && err.message ? err.message : err).toLowerCase();
  // Match English and Chinese Firefox locales ('不存在' = 'not found' in zh-CN errors).
  return msg.includes('no bookmark') || msg.includes('not found') || msg.includes('不存在');
}
