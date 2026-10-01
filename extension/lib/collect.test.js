// extension/lib/collect.test.js — collect: bookmark tree -> sync state
//
// The question collection answers is: "compared to last time, what changed".
// Getting the answer wrong has two consequences:
//
//   · under-report → local edits never reach the cloud
//   · over-report  → fresh timestamps on unchanged items → two devices
//                    overwrite each other and never converge
//
// So the point here is not "can it list bookmarks" but **whether unchanged
// items pass through untouched**.

import test from 'node:test';
import assert from 'node:assert/strict';

import { HLC, HLC_ZERO, compare } from './hlc.js';
import { deriveKey, isRootFolder, ROOT_TOOLBAR, ROOT_MENU, ROOT_UNFILED } from './keys.js';
import { scan, buildState, contentChanged, SCHEMA_VERSION } from './collect.js';
import { MockBookmarks } from './mock-bookmarks.js';

const ALL_ROOTS = new Set([ROOT_TOOLBAR, ROOT_MENU, ROOT_UNFILED]);
const NOW = 1_700_000_000_000;

// One HLC **persisted across calls**, plus a moving physical clock.
//
// Why persistent: the real flow creates one HLC (in sync.js) calibrated with
// the largest cached m. A fresh HLC(() => constant) per test would stamp
// **identical** timestamps on two collections, so assertions like "m must be
// larger after edit" always fail — looking like an implementation bug when it
// is really the test not modelling real time.
//
// now() also moves: in reality the wall clock advances, and HLC uses it to
// tell events apart.
let clockMs = NOW;
const clock = new HLC(() => clockMs);

function tickClock(ms = 1) {
  clockMs += ms;
}

/** Run one full collection: scan + buildState. */
async function collect(bm, { cached = null, clk = clock } = {}) {
  const scanResult = await scan(bm, { enabledRoots: ALL_ROOTS });
  const out = buildState(scanResult, cached || { v: SCHEMA_VERSION, items: {} }, clk, clockMs);
  return { ...out, scanResult, clock: clk };
}

/** Convenience: fetch one key's item */
const itemOf = (state, key) => state.items[key];

// ── Basic scanning ────────────────────────────────────────────────────
//
// The clock is shared across tests (see the header), so no reset is needed
// here. Tests needing clean state use nested t.beforeEach ones.

test('scans bookmarks and folders, deriving keys', async () => {
  const bm = new MockBookmarks();
  const folderId = bm.addFolder(ROOT_TOOLBAR, 'Work');
  const bookmarkId = bm.addBookmark(folderId, 'Some doc', 'https://example.com/doc');

  const { state, scanResult } = await collect(bm);

  assert.equal(scanResult.total, 2, 'should find 1 folder + 1 bookmark');
  assert.equal(Object.keys(state.items).length, 2);

  const folderKey = scanResult.firefoxLocalIdToKey.get(folderId);
  const bookmarkKey = scanResult.firefoxLocalIdToKey.get(bookmarkId);
  assert.ok(folderKey && bookmarkKey);
  assert.equal(state.items[folderKey].t, 'f');
  assert.equal(state.items[bookmarkKey].t, 'b');
  assert.equal(state.items[bookmarkKey].u, 'https://example.com/doc');
  assert.equal(state.items[bookmarkKey].p, folderKey, 'bookmark parent should be the folder key');
  assert.equal(state.items[folderKey].p, ROOT_TOOLBAR, 'top-level folder parents to the root literal');
});

test('roots themselves never become items', async () => {
  const bm = new MockBookmarks();
  bm.addBookmark(ROOT_TOOLBAR, 'a', 'https://a.example');
  const { state } = await collect(bm);

  for (const key of Object.keys(state.items)) {
    assert.ok(!isRootFolder(state.items[key].p) === false || isRootFolder(state.items[key].p),
      'a parent may be a root');
  }
  // No item key may equal a root id
  assert.equal(state.items[ROOT_TOOLBAR], undefined);
  assert.equal(state.items[ROOT_MENU], undefined);
  assert.equal(state.items[ROOT_UNFILED], undefined);
});

test('deep nesting: parent keys resolve level by level', async () => {
  const bm = new MockBookmarks();
  let parent = ROOT_TOOLBAR;
  const ids = [];
  for (let i = 0; i < 5; i++) {
    parent = bm.addFolder(parent, `Level ${i}`);
    ids.push(parent);
  }
  const leaf = bm.addBookmark(parent, 'Deepest', 'https://deep.example');

  const { state, scanResult } = await collect(bm);
  assert.equal(scanResult.total, 6);

  // Walk up from the leaf: every level's parent must be the previous key
  let childKey = scanResult.firefoxLocalIdToKey.get(leaf);
  for (let i = ids.length - 1; i >= 0; i--) {
    const parentKey = scanResult.firefoxLocalIdToKey.get(ids[i]);
    assert.equal(state.items[childKey].p, parentKey, `wrong parent key at level ${i}`);
    childKey = parentKey;
  }
  assert.equal(state.items[childKey].p, ROOT_TOOLBAR);
});

test('enabledRoots filter: unchecked roots stay out of state', async () => {
  const bm = new MockBookmarks();
  bm.addBookmark(ROOT_TOOLBAR, 'In toolbar', 'https://t.example');
  bm.addBookmark(ROOT_UNFILED, 'In other', 'https://u.example');

  const scanResult = await scan(bm, { enabledRoots: new Set([ROOT_TOOLBAR]) });
  const out = buildState(scanResult, { v: SCHEMA_VERSION, items: {} }, new HLC(() => NOW), NOW);

  const titles = Object.values(out.state.items).map((item) => item.n);
  assert.deepEqual(titles, ['In toolbar']);
});

test('mobile bookmarks excluded when includeMobile is false', async () => {
  const bm = new MockBookmarks();
  bm.addBookmark(ROOT_TOOLBAR, 'Desktop', 'https://d.example');
  bm.addBookmark('mobile______', 'Phone', 'https://m.example');

  const collected = await collect(bm);
  const titles = Object.values(collected.state.items).map((item) => item.n);
  assert.deepEqual(titles, ['Desktop']);
});

test('mobile bookmarks included when includeMobile is true', async () => {
  const bm = new MockBookmarks();
  bm.addBookmark('mobile______', 'Phone', 'https://m.example');

  const scanResult = await scan(bm, { enabledRoots: ALL_ROOTS, includeMobile: true });
  const out = buildState(scanResult, { v: SCHEMA_VERSION, items: {} }, new HLC(() => NOW), NOW);
  assert.equal(Object.keys(out.state.items).length, 1);
});

test('empty tree yields empty state without errors', async () => {
  const bm = new MockBookmarks();
  const { state, stats } = await collect(bm);
  assert.deepEqual(state.items, {});
  assert.equal(stats.created, 0);
  assert.equal(state.v, SCHEMA_VERSION);
});

test('nodes without url count as folders (Firefox uses url presence for typing)', async () => {
  const bm = new MockBookmarks();
  const folderId = bm.addFolder(ROOT_TOOLBAR, 'Folder');
  // Stuff in a node with an empty url, simulating dirty data
  bm.addBookmark(folderId, 'Empty url', '');
  const { state } = await collect(bm);
  const types = Object.values(state.items).map((item) => item.t);
  assert.deepEqual(types.sort(), ['f', 'f'], 'empty url should count as a folder');
});

// ── Six change scenarios ────────────────────────────────────────────

test('scenario 1 — new: brand-new items get fresh timestamps', async () => {
  const bm = new MockBookmarks();
  bm.addBookmark(ROOT_TOOLBAR, 'New bookmark', 'https://new.example');

  const { state, base, stats } = await collect(bm);
  assert.equal(stats.created, 1);
  assert.equal(stats.updated, 0);
  assert.deepEqual(base, {}, 'new items must not appear in base (they did not exist before)');
  for (const item of Object.values(state.items)) {
    assert.equal(item.m, item.a, 'new items share m and a');
    assert.ok(compare(item.m, HLC_ZERO) > 0);
  }
});

test('scenario 2 — unchanged: old timestamps carried over, never restamped', async () => {
  // The easiest one to get wrong. Restamping unchanged items makes two
  // devices overwrite each other.
  const bm = new MockBookmarks();
  const id = bm.addBookmark(ROOT_TOOLBAR, 'Stable', 'https://stable.example');

  const first = await collect(bm);
  const key = Object.keys(first.state.items)[0];
  const originalM = first.state.items[key].m;

  // Change nothing, collect again
  const second = await collect(bm, { cached: first.state });

  assert.equal(second.state.items[key].m, originalM, 'unchanged items must keep m');
  assert.equal(second.stats.unchanged, 1);
  assert.equal(second.stats.created, 0);
  assert.equal(second.stats.updated, 0);
  assert.equal(second.base[key], originalM, 'base records the last-seen m');
  assert.ok(id);
});

test('scenario 3 — title edit: fresh timestamp', async () => {
  const bm = new MockBookmarks();
  bm.addBookmark(ROOT_TOOLBAR, 'Old title', 'https://x.example');

  const first = await collect(bm);
  const key = Object.keys(first.state.items)[0];
  const beforeM = first.state.items[key].m;

  // Rename
  const node = [...bm.nodes.values()].find((candidate) => candidate.url === 'https://x.example');
  node.title = 'New title';

  const second = await collect(bm, { cached: first.state });
  assert.equal(second.state.items[key].n, 'New title');
  assert.equal(second.stats.updated, 1);
  assert.ok(compare(second.state.items[key].m, beforeM) > 0, 'm must grow after edit');
});

test('scenario 4 — URL edit: looks like "delete old + add new"', async () => {
  const bm = new MockBookmarks();
  bm.addBookmark(ROOT_TOOLBAR, 'Title', 'https://old.example');

  const first = await collect(bm);
  const oldKey = Object.keys(first.state.items)[0];

  const node = [...bm.nodes.values()].find((candidate) => candidate.url === 'https://old.example');
  node.url = 'https://new.example';

  const second = await collect(bm, { cached: first.state });
  const newKey = Object.keys(second.state.items).find((key) => !second.state.items[key].d);

  assert.notEqual(newKey, oldKey, 'a URL change must change the key');
  assert.equal(second.state.items[oldKey].d, true, 'old key should become a tombstone');
  assert.equal(second.state.items[oldKey].x, NOW, 'tombstone carries the deletion time');
  assert.equal(second.state.items[newKey].u, 'https://new.example');
  assert.equal(second.stats.created, 1);
  assert.equal(second.stats.deleted, 1);
});

test('scenario 5 — move: key unchanged, only the parent changes', async () => {
  const bm = new MockBookmarks();
  const folderOne = bm.addFolder(ROOT_TOOLBAR, 'Folder one');
  const folderTwo = bm.addFolder(ROOT_TOOLBAR, 'Folder two');
  bm.addBookmark(folderOne, 'Moving bookmark', 'https://move.example');

  const first = await collect(bm);
  const key = Object.keys(first.state.items).find((candidate) => first.state.items[candidate].u === 'https://move.example');
  const beforeM = first.state.items[key].m;
  const folderTwoKey = [...first.scanResult.firefoxLocalIdToKey].find(([id]) => id === folderTwo)[1];

  // Move to the other folder
  await bm.move([...bm.nodes.values()].find((candidate) => candidate.url === 'https://move.example').id,
    { parentId: folderTwo });

  const second = await collect(bm, { cached: first.state });
  assert.equal(second.state.items[key].u, 'https://move.example', 'a move must not change the key');
  assert.equal(second.state.items[key].p, folderTwoKey, 'parent should become the new folder');
  assert.equal(second.stats.updated, 1);
  assert.equal(second.stats.deleted, 0, 'a move must not produce tombstones');
  assert.ok(compare(second.state.items[key].m, beforeM) > 0);
});

test('scenario 6 — resurrect: previously deleted item reappears', async () => {
  const bm = new MockBookmarks();
  bm.addBookmark(ROOT_TOOLBAR, 'Comeback', 'https://back.example');

  const first = await collect(bm);
  const key = Object.keys(first.state.items)[0];

  // Delete (simulating a local bookmark deletion)
  const node = [...bm.nodes.values()].find((candidate) => candidate.url === 'https://back.example');
  await bm.remove(node.id);

  const second = await collect(bm, { cached: first.state });
  assert.equal(second.state.items[key].d, true, 'should write a tombstone');
  assert.equal(second.stats.deleted, 1);
  const deadM = second.state.items[key].m;

  // Re-add the same URL
  bm.addBookmark(ROOT_TOOLBAR, 'Comeback', 'https://back.example');
  const third = await collect(bm, { cached: second.state });

  assert.ok(!third.state.items[key].d, 'tombstone cleared, bookmark resurrected');
  assert.equal(third.state.items[key].u, 'https://back.example');
  assert.ok(compare(third.state.items[key].m, deadM) > 0,
    'resurrection needs a newer m, or the tombstone keeps winning forever');
  assert.equal(third.stats.resurrected, 1);
});

// ── Deletion propagation ────────────────────────────────────────────

test('deleting a folder writes one tombstone per subtree item', async () => {
  // The server only knows "this item is gone"; it will not infer the children.
  const bm = new MockBookmarks();
  const folderId = bm.addFolder(ROOT_TOOLBAR, 'Folder to delete');
  const subId = bm.addFolder(folderId, 'Subfolder');
  bm.addBookmark(folderId, 'Direct child', 'https://a.example');
  bm.addBookmark(subId, 'Grandchild', 'https://b.example');

  const first = await collect(bm);
  assert.equal(Object.keys(first.state.items).length, 4, 'folder + subfolder + 2 bookmarks');

  // Delete the whole folder
  await bm.remove(folderId);

  const second = await collect(bm, { cached: first.state });
  assert.equal(second.stats.deleted, 4, 'all 4 subtree items should become tombstones');
  for (const [key, item] of Object.entries(second.state.items)) {
    assert.equal(item.d, true, `${key} should be a tombstone`);
    assert.equal(item.x, NOW);
  }
});

test('re-adding a same-name same-place folder is not confused with the old tombstone', async () => {
  const bm = new MockBookmarks();
  const folderId = bm.addFolder(ROOT_TOOLBAR, 'Same-name folder');

  const first = await collect(bm);
  const key = Object.keys(first.state.items)[0];
  assert.equal(first.state.items[key].t, 'f');

  // Delete -> tombstone
  await bm.remove(folderId);
  tickClock(1000);
  const dead = await collect(bm, { cached: first.state });
  assert.equal(dead.state.items[key].d, true, 'should write a tombstone');
  const deadM = dead.state.items[key].m;

  // Recreate same name same place -> same key, resurrected by a newer m
  bm.addFolder(ROOT_TOOLBAR, 'Same-name folder');
  tickClock(1000);
  const revived = await collect(bm, { cached: dead.state });

  assert.equal(Object.keys(revived.state.items).length, 1, 'should not gain a second record');
  // Falsy check, not === false: a resurrected item omits d entirely
  // (omitempty), so the value is undefined. Asserting === false would read as
  // "not resurrected".
  assert.ok(!revived.state.items[key].d, 'should resurrect (omitted d means resurrected)');
  assert.equal(revived.state.items[key].t, 'f');
  assert.ok(compare(revived.state.items[key].m, deadM) > 0,
    'resurrection needs a newer m, or the tombstone keeps winning');
  assert.equal(revived.state.items[key].x, undefined, 'resurrection must not keep the deletion time');
});

test('already-tombstoned items pass through untouched on the next collect (or GC drops them)', async () => {
  const bm = new MockBookmarks();
  bm.addBookmark(ROOT_TOOLBAR, 'To delete', 'https://gone.example');

  const first = await collect(bm);
  const key = Object.keys(first.state.items)[0];
  const node = [...bm.nodes.values()].find((candidate) => candidate.url === 'https://gone.example');
  await bm.remove(node.id);

  const second = await collect(bm, { cached: first.state });
  const deadM = second.state.items[key].m;

  // Still absent locally, collect again
  const third = await collect(bm, { cached: second.state });
  assert.equal(third.state.items[key].d, true, 'tombstone must persist');
  assert.equal(third.state.items[key].m, deadM, 'existing tombstones must not be restamped');
  assert.equal(third.stats.deleted, 0, 'should not count as another deletion');
});

// ── What base is for ────────────────────────────────────────────────

test('base records the old m of every existing key', async () => {
  // base is the server's only way to tell "I changed" from "they changed".
  // Without it, the conflict log is pure noise.
  const bm = new MockBookmarks();
  bm.addBookmark(ROOT_TOOLBAR, 'One', 'https://one.example');
  bm.addBookmark(ROOT_TOOLBAR, 'Two', 'https://two.example');

  const first = await collect(bm);
  const second = await collect(bm, { cached: first.state });

  assert.equal(Object.keys(second.base).length, 2, 'both existing items belong in base');
  for (const [key, modifiedHlc] of Object.entries(second.base)) {
    assert.equal(modifiedHlc, first.state.items[key].m, `base[${key}] should be the last-seen m`);
  }
});

test('title-only edit: base keeps the old m while state holds the new m', async () => {
  const bm = new MockBookmarks();
  bm.addBookmark(ROOT_TOOLBAR, 'Old', 'https://x.example');
  const first = await collect(bm);
  const key = Object.keys(first.state.items)[0];

  [...bm.nodes.values()].find((candidate) => candidate.url === 'https://x.example').title = 'New';
  const second = await collect(bm, { cached: first.state });

  assert.equal(second.base[key], first.state.items[key].m, 'base is the old m');
  assert.ok(compare(second.state.items[key].m, second.base[key]) > 0, 'state holds the new m');
});

// ── contentChanged ──────────────────────────────────────────────────

// contentChanged takes two **different shapes** with different field names:
//   prev — state item, fields p / t / n / u
//   node — scan result node, fields parentKey / type / title / url
// Writing tests with one field-name set for both matches nothing and fakes
// "always changed".
const PREV = { p: 'parent', t: 'b', n: 'Title', u: 'https://example.com' };
const NODE = { parentKey: 'parent', type: 'b', title: 'Title', url: 'https://example.com' };

test('contentChanged: identical content is false', () => {
  assert.equal(contentChanged(PREV, NODE), false);
});

test('contentChanged: ignores m / a / d / x', () => {
  // Timestamps are not content. Both sides re-saving the same bookmark with
  // identical content must not count as a conflict.
  const withTimestamps = { ...PREV, m: '1700000000000-00009', a: '1700000000000-00000', d: true, x: 999 };
  assert.equal(contentChanged(withTimestamps, NODE), false,
    'timestamps and tombstone flags must not affect content comparison');
});

test('contentChanged: spots all four content differences', () => {
  assert.equal(contentChanged(PREV, { ...NODE, parentKey: 'other folder' }), true, 'parent changed');
  assert.equal(contentChanged(PREV, { ...NODE, type: 'f' }), true, 'type changed');
  assert.equal(contentChanged(PREV, { ...NODE, title: 'New title' }), true, 'title changed');
  assert.equal(contentChanged(PREV, { ...NODE, url: 'https://other.example' }), true, 'URL changed');
});

test('contentChanged: missing fields count as empty, no false diffs', () => {
  // Folders have no u; state may hold undefined or ''
  const folderPrev = { p: 'p', t: 'f', n: 'Folder' };
  const folderNode = { parentKey: 'p', type: 'f', title: 'Folder', url: '' };
  assert.equal(contentChanged(folderPrev, folderNode), false);
  assert.equal(contentChanged({ ...folderPrev, u: '' }, folderNode), false);
});

// ── Robustness ──────────────────────────────────────────────────────

test('getTree with a broken shape reports a clear error', async () => {
  await assert.rejects(
    () => scan({ getTree: async () => [{}] }, {}),
    /unexpected shape/,
  );
});

test('empty-items cache (fresh install) does not error', async () => {
  const bm = new MockBookmarks();
  bm.addBookmark(ROOT_TOOLBAR, 'x', 'https://x.example');
  const collected = await collect(bm, { cached: { v: SCHEMA_VERSION } }); // no items field
  assert.equal(Object.keys(collected.state.items).length, 1);
});

test('extra cached keys (on the server, missing locally) become tombstones', async () => {
  // E.g. a fresh profile with a surviving cache
  const bm = new MockBookmarks();
  const collected = await collect(bm, {
    cached: {
      v: SCHEMA_VERSION,
      items: {
        aaaaaaaabbbbbbbbccccccccdddddddd: {
          p: ROOT_TOOLBAR, t: 'b', n: 'Ghost', u: 'https://ghost.example',
          a: '1700000000000-00000', m: '1700000000000-00000',
        },
      },
    },
  });
  const ghost = collected.state.items.aaaaaaaabbbbbbbbccccccccdddddddd;
  assert.equal(ghost.d, true, 'locally missing items should become tombstones');
  assert.equal(collected.stats.deleted, 1);
});

test('two scans are stable (same tree -> same keys)', async () => {
  const bm = new MockBookmarks();
  const folderId = bm.addFolder(ROOT_TOOLBAR, 'Stable folder');
  bm.addBookmark(folderId, 'a', 'https://a.example');
  bm.addBookmark(folderId, 'b', 'https://b.example');

  const first = await collect(bm);
  const second = await collect(bm, { cached: first.state });

  assert.deepEqual(
    Object.keys(first.state.items).sort(),
    Object.keys(second.state.items).sort(),
    'two scans should yield the same key set',
  );
  assert.equal(second.stats.unchanged, 3, 'second run should report no changes');
});

test('key derivation matches keys.js (same URL always yields same key)', async () => {
  const bm = new MockBookmarks();
  bm.addBookmark(ROOT_TOOLBAR, 'Verify', 'https://verify.example');

  const { scanResult } = await collect(bm);
  const [firefoxLocalId, key] = [...scanResult.firefoxLocalIdToKey].find(
    ([, candidate]) => scanResult.nodes.get(candidate).url === 'https://verify.example',
  );
  const expected = await deriveKey('b', { url: 'https://verify.example' });
  assert.equal(key, expected, 'collect must derive keys exactly like keys.js');
  assert.ok(firefoxLocalId);
});
