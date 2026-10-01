// extension/lib/apply.test.js — apply: sync state -> Firefox bookmark tree
//
// The only step in the pipeline that **really mutates user data**. Getting it
// wrong means:
//
//   · missed deletes → things deleted on the work PC resurrect here
//   · create-before-delete → locally present items look "already there" and
//     deletions never propagate
//   · missing parent → create fails on an invalid parentId, losing a subtree
//
// So the point is not "can it edit the tree" but the **three ordering
// constraints** (see the apply.js header).

import test from 'node:test';
import assert from 'node:assert/strict';

import { HLC, compare, encode } from './hlc.js';
import { deriveKey, ROOT_TOOLBAR, ROOT_MENU, ROOT_UNFILED, TYPE_FOLDER, TYPE_BOOKMARK } from './keys.js';
import { scan, buildState, SCHEMA_VERSION } from './collect.js';
import { planApply, executePlan } from './apply.js';
import { MockBookmarks } from './mock-bookmarks.js';

let clockMs = 1_700_000_000_000;
const clock = new HLC(() => clockMs);

/** Scan a mock tree, returning the scan result (apply needs the Firefox id map). */
async function live(bm) {
  return scan(bm, { enabledRoots: new Set([ROOT_TOOLBAR, ROOT_MENU, ROOT_UNFILED]) });
}

/** Build one item */
function item(type, parentKey, title, url, ms = 1000, extra = {}) {
  const base = { p: parentKey, t: type, n: title, a: encode(ms, 0), m: encode(ms, 1) };
  return type === TYPE_BOOKMARK ? { ...base, u: url } : { ...base, ...extra };
}

function stateOf(items) {
  return { v: SCHEMA_VERSION, items: Object.fromEntries(Object.entries(items)) };
}

/** Run the full "plan + execute". */
async function apply(bm, incomingState) {
  const scanned = await live(bm);
  const plan = planApply(scanned.nodes, incomingState, scanned.firefoxIdBySyncKey);
  const result = await executePlan(bm, plan);
  return { plan, result, after: await live(bm) };
}

// deriveKey is **async** (via crypto.subtle.digest) and must be awaited.
//
// It was once written as `return deriveKey(...)` — returning a Promise
// instead of a key string. The symptom is subtle: Promises used as object
// keys ("[object Promise]") all "match" (all wrong by the same amount), so
// assertions like "parent changed -> move" report result.moved === 0 and look
// like broken move logic in apply, when the test really passed a junk key.
const keyOf = (type, parts) => deriveKey(type, parts);

// ── Plan: no writes when nothing should happen ────────────────────────

test('no write ops when state already matches local', async () => {
  const bm = new MockBookmarks();
  const folderId = bm.addFolder(ROOT_TOOLBAR, 'Folder');
  bm.addBookmark(folderId, 'Bookmark', 'https://x.example');

  // Collect once, treat the result as "the server-returned state"
  const scanned = await live(bm);
  const asState = {
    v: SCHEMA_VERSION,
    items: Object.fromEntries(
      [...scanned.nodes].map(([key, node]) => [key, item(node.type, node.parentKey, node.title, node.url)]),
    ),
  };

  const plan = planApply(scanned.nodes, asState, scanned.firefoxIdBySyncKey);
  assert.equal(plan.counts.create, 0);
  assert.equal(plan.counts.update, 0);
  assert.equal(plan.counts.remove, 0);
  assert.equal(plan.skipped.length, 0);

  bm.clearCalls();
  const res = await executePlan(bm, plan);
  assert.equal(bm.ops().length, 0, `expected no API calls, got: ${bm.ops()}`);
  assert.deepEqual(res, { created: 0, updated: 0, moved: 0, deleted: 0, failed: [] });
});

// ── Creates ───────────────────────────────────────────────────────────

test('creates a cloud bookmark locally', async () => {
  const bm = new MockBookmarks();
  const bookmarkKey = await keyOf(TYPE_BOOKMARK, { url: 'https://new.example' });

  const { result, after } = await apply(bm, stateOf({
    [bookmarkKey]: item(TYPE_BOOKMARK, ROOT_TOOLBAR, 'From cloud', 'https://new.example'),
  }));

  assert.equal(result.created, 1);
  assert.equal(result.failed.length, 0);
  assert.equal(after.nodes.size, 1);
  const created = [...bm.nodes.values()].find((node) => node.url === 'https://new.example');
  assert.ok(created, 'should create this bookmark');
  assert.equal(created.parentId, ROOT_TOOLBAR, 'top-level items hang under the root');
});

test('parent folder and children: parent first, children after', async () => {
  // The most critical ordering constraint. create needs an existing parent
  // id, or the whole subtree fails.
  const bm = new MockBookmarks();
  const folderKey = await keyOf(TYPE_FOLDER, { parentKey: ROOT_TOOLBAR, title: 'Folder' });
  const childOneKey = await keyOf(TYPE_BOOKMARK, { url: 'https://one.example' });
  const childTwoKey = await keyOf(TYPE_BOOKMARK, { url: 'https://two.example' });

  const { result, after } = await apply(bm, stateOf({
    [childOneKey]: item(TYPE_BOOKMARK, folderKey, 'One', 'https://one.example'),
    [folderKey]: item(TYPE_FOLDER, ROOT_TOOLBAR, 'Folder'),
    [childTwoKey]: item(TYPE_BOOKMARK, folderKey, 'Two', 'https://two.example'),
  }));

  assert.equal(result.failed.length, 0, `no failures expected: ${JSON.stringify(result.failed)}`);
  assert.equal(result.created, 3);

  // Execution order: the folder create must precede the children
  const creates = bm.calls.filter((call) => call.op === 'create');
  const folderIdx = creates.findIndex((call) => call.details.title === 'Folder');
  assert.ok(folderIdx >= 0, 'should create the folder');
  for (const [index, call] of creates.entries()) {
    if (call.details.title !== 'Folder') {
      assert.ok(index > folderIdx,
        `${call.details.title} create ran before the folder (${index} vs ${folderIdx})`);
    }
  }
  assert.equal(after.nodes.size, 3);
});

test('three-level nesting builds correctly', async () => {
  const bm = new MockBookmarks();
  const levelOne = await keyOf(TYPE_FOLDER, { parentKey: ROOT_TOOLBAR, title: 'L1' });
  const levelTwo = await keyOf(TYPE_FOLDER, { parentKey: levelOne, title: 'L2' });
  const leaf = await keyOf(TYPE_BOOKMARK, { url: 'https://leaf.example' });

  const { result, after } = await apply(bm, stateOf({
    [leaf]: item(TYPE_BOOKMARK, levelTwo, 'Leaf', 'https://leaf.example'),
    [levelTwo]: item(TYPE_FOLDER, levelOne, 'L2'),
    [levelOne]: item(TYPE_FOLDER, ROOT_TOOLBAR, 'L1'),
  }));

  assert.equal(result.failed.length, 0, JSON.stringify(result.failed));
  assert.equal(result.created, 3);
  assert.equal(after.nodes.size, 3);

  // Verify the levels really link up: trace from the leaf upward.
  // (Do not use depth from bm.nodes — that is scan() output; mock nodes lack it.)
  const leafNode = [...bm.nodes.values()].find((node) => node.url === 'https://leaf.example');
  const l2Node = bm.nodes.get(leafNode.parentId);
  const l1Node = bm.nodes.get(l2Node.parentId);
  assert.equal(l2Node.title, 'L2', 'leaf parent should be L2');
  assert.equal(l1Node.title, 'L1', 'L2 parent should be L1');
  assert.equal(l1Node.parentId, ROOT_TOOLBAR, 'L1 hangs under the root');
});

// ── Updates ───────────────────────────────────────────────────────────

test('title change -> update', async () => {
  const bm = new MockBookmarks();
  bm.addBookmark(ROOT_TOOLBAR, 'Old title', 'https://x.example');
  const bookmarkKey = await keyOf(TYPE_BOOKMARK, { url: 'https://x.example' });

  const { result } = await apply(bm, stateOf({
    [bookmarkKey]: item(TYPE_BOOKMARK, ROOT_TOOLBAR, 'New title', 'https://x.example', 2000),
  }));

  assert.equal(result.updated, 1);
  assert.equal(result.created, 0);
  const node = [...bm.nodes.values()].find((candidate) => candidate.url === 'https://x.example');
  assert.equal(node.title, 'New title');
});

test('URL change -> cloud declares only the new URL, the old one is deleted as absent', async () => {
  const bm = new MockBookmarks();
  bm.addBookmark(ROOT_TOOLBAR, 'Title', 'https://old.example');
  const newKey = await keyOf(TYPE_BOOKMARK, { url: 'https://new.example' });

  const { result } = await apply(bm, stateOf({
    [newKey]: item(TYPE_BOOKMARK, ROOT_TOOLBAR, 'Title', 'https://new.example'),
  }));

  assert.equal(result.created, 1, 'new URL is a new identity');
  assert.equal(result.deleted, 1, 'old URL missing from cloud state -> local copy deleted');
  const urls = [...bm.nodes.values()].map((node) => node.url).filter(Boolean);
  assert.deepEqual(urls, ['https://new.example'],
    'only the cloud-declared entry should remain locally');
});

test('local-only bookmarks the cloud never mentions get deleted', async () => {
  // Direct consequence of "cloud is authoritative". Sounds aggressive, but it
  // is exactly the "deleted on the work PC disappears here" semantic.
  const bm = new MockBookmarks();
  bm.addBookmark(ROOT_TOOLBAR, 'Local only', 'https://local-only.example');

  const { result } = await apply(bm, stateOf({}));
  assert.equal(result.deleted, 1);
  assert.equal([...bm.nodes.values()].some((node) => node.url), false);
});

// ── Moves ─────────────────────────────────────────────────────────────

test('parent change -> move (key unchanged)', async () => {
  const bm = new MockBookmarks();
  const folderOne = bm.addFolder(ROOT_TOOLBAR, 'Folder one');
  const folderTwo = bm.addFolder(ROOT_MENU, 'Folder two');
  bm.addBookmark(folderOne, 'Moving', 'https://move.example');

  const folderOneKey = await keyOf(TYPE_FOLDER, { parentKey: ROOT_TOOLBAR, title: 'Folder one' });
  const folderTwoKey = await keyOf(TYPE_FOLDER, { parentKey: ROOT_MENU, title: 'Folder two' });
  const bookmarkKey = await keyOf(TYPE_BOOKMARK, { url: 'https://move.example' });

  const { result } = await apply(bm, stateOf({
    [folderOneKey]: item(TYPE_FOLDER, ROOT_TOOLBAR, 'Folder one'),
    [folderTwoKey]: item(TYPE_FOLDER, ROOT_MENU, 'Folder two'),
    [bookmarkKey]: item(TYPE_BOOKMARK, folderTwoKey, 'Moving', 'https://move.example', 2000),
  }));

  assert.equal(result.moved, 1);
  assert.equal(result.created, 0, 'a move must not count as delete+create');
  const node = [...bm.nodes.values()].find((candidate) => candidate.url === 'https://move.example');
  assert.equal(node.parentId, folderTwo);
});

// ── Deletes ───────────────────────────────────────────────────────────

test('remotely deleted, locally present -> deleted locally', async () => {
  const bm = new MockBookmarks();
  bm.addBookmark(ROOT_TOOLBAR, 'To delete', 'https://gone.example');

  const { result, after } = await apply(bm, stateOf({}));

  assert.equal(result.deleted, 1);
  assert.equal(after.nodes.size, 0);
  assert.equal([...bm.nodes.values()].some((node) => node.url === 'https://gone.example'), false);
});

test('deleting a folder sends one remove (using Firefox recursive semantics)', async () => {
  // bookmarks.remove on a folder is recursive. A second remove on a child
  // would fail because the node is already gone with it.
  const bm = new MockBookmarks();
  const folderId = bm.addFolder(ROOT_TOOLBAR, 'Folder');
  const subId = bm.addFolder(folderId, 'Subfolder');
  bm.addBookmark(folderId, 'Child', 'https://a.example');
  bm.addBookmark(subId, 'Grandchild', 'https://b.example');

  const { result, after } = await apply(bm, stateOf({}));

  assert.equal(result.deleted, 1, 'one remove covers the whole subtree');
  assert.equal(bm.countOp('remove'), 1, `actual remove count: ${bm.countOp('remove')}`);
  assert.equal(after.nodes.size, 0);
  assert.equal(result.failed.length, 0, JSON.stringify(result.failed));
});

test('delete-before-create: doomed items must not be skipped as "already present"', async () => {
  // With the order reversed, locally doomed items look "present" and survive.
  const bm = new MockBookmarks();
  bm.addBookmark(ROOT_TOOLBAR, 'Deleted remotely', 'https://deleted.example');

  const bookmarkKey = await keyOf(TYPE_BOOKMARK, { url: 'https://kept.example' });
  const { result, after } = await apply(bm, stateOf({
    [bookmarkKey]: item(TYPE_BOOKMARK, ROOT_TOOLBAR, 'Still on cloud', 'https://kept.example'),
  }));

  assert.equal(result.deleted, 1);
  assert.equal(result.created, 1);
  const urls = [...after.nodes.values()].map((node) => node.url).filter(Boolean);
  assert.deepEqual(urls, ['https://kept.example'], 'only the cloud-declared entry should remain');
});

// ── Type changes ──────────────────────────────────────────────────────

test('bookmark turned folder -> delete + recreate (update cannot change types)', async () => {
  const bm = new MockBookmarks();
  bm.addBookmark(ROOT_TOOLBAR, 'Was a bookmark', 'https://x.example');
  const key = await keyOf(TYPE_FOLDER, { parentKey: ROOT_TOOLBAR, title: 'Was a bookmark' });

  const { result } = await apply(bm, stateOf({
    [key]: item(TYPE_FOLDER, ROOT_TOOLBAR, 'Was a bookmark', undefined, 2000),
  }));

  assert.equal(result.created, 1, 'should rebuild');
  assert.equal(bm.countOp('remove'), 1, 'old one should be removed');
  assert.equal(result.failed.length, 0, JSON.stringify(result.failed));
});

// ── Robustness ────────────────────────────────────────────────────────

test('parent missing from cloud state -> skipped, never parked at the root', async () => {
  // Silently moving a bookmark under "Other Bookmarks" is worse than an
  // error: the user never notices until it is lost one day.
  const bm = new MockBookmarks();
  const bookmarkKey = await keyOf(TYPE_BOOKMARK, { url: 'https://orphan.example' });
  const ghostParent = await keyOf(TYPE_FOLDER, { parentKey: ROOT_TOOLBAR, title: 'Missing folder' });

  const { plan, result, after } = await apply(bm, stateOf({
    [bookmarkKey]: item(TYPE_BOOKMARK, ghostParent, 'Orphan', 'https://orphan.example'),
  }));

  assert.equal(plan.skipped.length, 1);
  assert.equal(plan.skipped[0].reason, 'parent-missing');
  assert.equal(result.created, 0, 'must not be created elsewhere');
  assert.equal(after.nodes.size, 0, 'nothing should appear locally');
});

test('cyclic parent chain -> no infinite loop, safely skipped', async () => {
  const bm = new MockBookmarks();
  const keyA = await keyOf(TYPE_FOLDER, { parentKey: 'x', title: 'A' });
  const keyB = await keyOf(TYPE_FOLDER, { parentKey: 'x', title: 'B' });

  const { plan } = await apply(bm, stateOf({
    [keyA]: item(TYPE_FOLDER, keyB, 'A'),
    [keyB]: item(TYPE_FOLDER, keyA, 'B'),
  }));
  assert.equal(plan.skipped.length, 2, 'both should be skipped');
});

test('one failing create does not affect the others', async () => {
  const bm = new MockBookmarks();
  const okKey = await keyOf(TYPE_BOOKMARK, { url: 'https://ok.example' });
  const badKey = await keyOf(TYPE_BOOKMARK, { url: 'https://bad.example' });

  // Fail only the second one
  let callCount = 0;
  const orig = bm.create.bind(bm);
  bm.create = async (details) => {
    callCount += 1;
    if (callCount === 2) throw new Error('Simulated failure');
    return orig(details);
  };

  const { result, after } = await apply(bm, stateOf({
    [okKey]: item(TYPE_BOOKMARK, ROOT_TOOLBAR, 'Good', 'https://ok.example'),
    [badKey]: item(TYPE_BOOKMARK, ROOT_TOOLBAR, 'Bad', 'https://bad.example'),
  }));

  assert.equal(result.failed.length, 1, 'failure recorded, not thrown');
  assert.equal(result.created, 1, 'the other one still succeeds');
  assert.equal(after.nodes.size, 1);
});

test('deleting a node the user already removed manually is not a failure', async () => {
  const bm = new MockBookmarks();
  bm.addBookmark(ROOT_TOOLBAR, 'x', 'https://x.example');
  const scanned = await live(bm);

  // Cloud state omits it -> plan deletes
  const plan = planApply(scanned.nodes, stateOf({}), scanned.firefoxIdBySyncKey);
  assert.equal(plan.counts.remove, 1);

  // But the user deleted it before execution
  const id = [...bm.nodes.values()].find((node) => node.url === 'https://x.example').id;
  await bm.remove(id);

  const res = await executePlan(bm, plan);
  assert.equal(res.failed.length, 0, 'an already-gone node must not count as failed');
});

test('empty incoming state -> deletes everything local', async () => {
  const bm = new MockBookmarks();
  const folderId = bm.addFolder(ROOT_TOOLBAR, 'Folder');
  bm.addBookmark(folderId, 'One', 'https://one.example');
  bm.addBookmark(ROOT_MENU, 'Two', 'https://two.example');

  const { result, after } = await apply(bm, { v: SCHEMA_VERSION, items: {} });
  assert.ok(result.deleted >= 1);
  assert.equal(after.nodes.size, 0);
});

test('incoming without items field -> treated as empty', async () => {
  const bm = new MockBookmarks();
  bm.addBookmark(ROOT_TOOLBAR, 'x', 'https://x.example');
  const { after } = await apply(bm, { v: SCHEMA_VERSION });
  assert.equal(after.nodes.size, 0);
});

test('tombstones never land in the local bookmark tree', async () => {
  const bm = new MockBookmarks();
  const bookmarkKey = await keyOf(TYPE_BOOKMARK, { url: 'https://x.example' });
  await apply(bm, stateOf({
    [bookmarkKey]: { p: ROOT_TOOLBAR, t: 'b', n: 'Deleted', u: 'https://x.example',
            a: encode(1, 0), m: encode(2, 0), d: true, x: 1700000000000 },
  }));
  assert.equal([...bm.nodes.values()].some((node) => node.url === 'https://x.example'), false,
    'a tombstone is just "the fact of deletion", not a record to keep locally');
});

// ── Round-trip consistency ──────────────────────────────────────────

test('round-trip: state collected here lands on an empty tree and re-collects equivalently', async () => {
  // The strongest combined property of collect + apply.
  // Collect on A -> apply to empty B -> collect on B -> equivalent result.
  const source = new MockBookmarks();
  const folderA = source.addFolder(ROOT_TOOLBAR, 'Work');
  const folderB = source.addFolder(folderA, 'Subfolder');
  source.addBookmark(folderA, 'One', 'https://one.example');
  source.addBookmark(folderB, 'Two', 'https://two.example');
  source.addBookmark(ROOT_UNFILED, 'Three', 'https://three.example');

  const firstScan = await live(source);
  const asState = buildState(firstScan, { v: SCHEMA_VERSION, items: {} }, clock, clockMs);

  const target = new MockBookmarks();
  const { result, after } = await apply(target, asState.state);
  assert.equal(result.failed.length, 0, JSON.stringify(result.failed));

  // Re-collect on the target tree
  const roundTwo = buildState(after, { v: SCHEMA_VERSION, items: {} }, clock, clockMs);

  assert.equal(Object.keys(roundTwo.state.items).length, Object.keys(asState.state.items).length,
    `item counts should match: source ${Object.keys(asState.state.items).length} vs target ${Object.keys(roundTwo.state.items).length}`);

  for (const [key, orig] of Object.entries(asState.state.items)) {
    const back = roundTwo.state.items[key];
    assert.ok(back, `lost ${key} on the round-trip`);
    assert.equal(back.t, orig.t, `${key} type changed`);
    assert.equal(back.n, orig.n, `${key} title changed`);
    assert.equal(back.u, orig.u, `${key} URL changed`);
    assert.equal(back.p, orig.p, `${key} parent changed`);
  }
});

test('round-trip: tombstones stay stable after delete + round-trip', async () => {
  const source = new MockBookmarks();
  const folderId = source.addFolder(ROOT_TOOLBAR, 'To delete');
  source.addBookmark(folderId, 'Child', 'https://gone.example');
  source.addBookmark(ROOT_TOOLBAR, 'Keep', 'https://keep.example');

  // First sync: both devices have everything
  const firstScan = await live(source);
  const asState = buildState(firstScan, { v: SCHEMA_VERSION, items: {} }, clock, clockMs);
  assert.equal(Object.keys(asState.state.items).length, 3,
    'first collect yields 3 items (folder + child + keep), excluding system roots');

  const deviceB = new MockBookmarks();
  await apply(deviceB, asState.state);
  assert.equal((await live(deviceB)).nodes.size, 3);

  // Delete the whole folder on A, collect
  await source.remove(folderId);
  clockMs += 1000;
  const secondScan = await live(source);
  const afterDelete = buildState(secondScan, asState.state, clock, clockMs);
  // The tree held 3 items (folder + child + keep); deleting the folder with
  // its child = 2 tombstones
  assert.equal(afterDelete.stats.deleted, 2, 'folder + child, two tombstones');
  assert.equal(afterDelete.stats.unchanged, 1, '"keep" is untouched and must not be restamped');

  // Sync to B: should delete
  const { result, after: bAfter } = await apply(deviceB, afterDelete.state);
  assert.ok(result.deleted >= 1, 'B should run deletes');
  const remaining = [...bAfter.nodes.values()].filter((node) => node.url);
  assert.equal(remaining.length, 1, 'only "keep" remains');
  assert.equal(remaining[0].url, 'https://keep.example');
});
