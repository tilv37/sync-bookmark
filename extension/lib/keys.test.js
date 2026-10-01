// extension/lib/keys.test.js — deriving identity keys
//
// These rules decide "whether two bookmarks on two devices are the same
// thing". Getting them wrong throws nothing and crashes nothing — it only
// makes merges silently treat one bookmark as two, or two different
// bookmarks as one and drop one.

import test from 'node:test';
import assert from 'node:assert/strict';

import {
  deriveKey,
  deriveKeys,
  buildMaterial,
  isRootFolder,
  isRootEnabled,
  isValidKey,
  resolveRoots,
  ROOT_TOOLBAR,
  ROOT_MENU,
  ROOT_UNFILED,
  ROOT_MOBILE,
  TYPE_BOOKMARK,
  TYPE_FOLDER,
  KEY_LENGTH,
} from './keys.js';

// ── Encoding format ─────────────────────────────────────────────────

test('key is always 32 lowercase hex chars', async () => {
  const key = await deriveKey(TYPE_BOOKMARK, { url: 'https://example.com' });
  assert.equal(key.length, KEY_LENGTH);
  assert.match(key, /^[0-9a-f]{32}$/);
  assert.equal(isValidKey(key), true);
});

test('separator is NUL, keeping concatenation unambiguous', () => {
  // NUL instead of | or :, because URLs and folder names can legally contain the latter.
  const material = buildMaterial(TYPE_FOLDER, { parentKey: 'abc', title: 'def' });
  assert.equal(material, `f:abc${String.fromCharCode(0)}def`);
  assert.ok(material.includes(String.fromCharCode(0)));
});

// ── Stability and discrimination ────────────────────────────────────

test('same input hashes to the same key twice', async () => {
  const first = await deriveKey(TYPE_BOOKMARK, { url: 'https://example.com/a' });
  const second = await deriveKey(TYPE_BOOKMARK, { url: 'https://example.com/a' });
  assert.equal(first, second);
});

test('different URLs get different keys', async () => {
  const first = await deriveKey(TYPE_BOOKMARK, { url: 'https://example.com/a' });
  const second = await deriveKey(TYPE_BOOKMARK, { url: 'https://example.com/b' });
  assert.notEqual(first, second);
});

test('URL case / trailing-slash differences produce different keys', async () => {
  // Deliberate: https://x.com and https://x.com/ usually render the same
  // page, but as identities they are two records. Merging yields two entries.
  const plain = await deriveKey(TYPE_BOOKMARK, { url: 'https://example.com' });
  const slashed = await deriveKey(TYPE_BOOKMARK, { url: 'https://example.com/' });
  const upperHost = await deriveKey(TYPE_BOOKMARK, { url: 'https://Example.com' });
  assert.notEqual(plain, slashed);
  assert.notEqual(plain, upperHost);
});

// Why the separator must be NUL.
//
// Counter-example: with | as separator, folders (parent="a|b", title="c")
// and (parent="a", title="b|c") hash from **identical** input -> same key ->
// two different folders merge as one and one side's content silently overwrites.
// NUL never appears in UTF-8 text, so the ambiguity cannot arise.
test('NUL separator avoids the collision | would produce', async () => {
  // This pair would collide under |
  const first = await deriveKey(TYPE_FOLDER, { parentKey: 'a|b', title: 'c' });
  const second = await deriveKey(TYPE_FOLDER, { parentKey: 'a', title: 'b|c' });
  // Note: these are the **correct** keys (we use NUL, not |); they must differ
  assert.notEqual(first, second, 'pipes in parent/title must not confuse identity');

  // Direct check of the scheme: feeding the same inputs to a "join with |" reference
  const pipeMaterial1 = 'f:a|b' + '|' + 'c';
  const pipeMaterial2 = 'f:a' + '|' + 'b|c';
  assert.equal(pipeMaterial1, pipeMaterial2, 'joining with | really collides — exactly what we avoid');

  // Under NUL, the same two inputs hash from different material
  assert.notEqual(
    buildMaterial(TYPE_FOLDER, { parentKey: 'a|b', title: 'c' }),
    buildMaterial(TYPE_FOLDER, { parentKey: 'a', title: 'b|c' }),
  );
});

// ── Bookmark keys exclude the parent ────────────────────────────────
//
// The most critical constraint in the design. It lets "drag a bookmark into
// another folder" sync correctly as a move instead of "delete + recreate".
// Conversely, if parentKey ever enters the bookmark hash input, renaming a
// parent folder changes the keys of the whole subtree and yields thousands
// of tombstones.

test('bookmark keys ignore the parent folder', async () => {
  const plain = await deriveKey(TYPE_BOOKMARK, { url: 'https://example.com/x' });
  const withParent = await deriveKey(TYPE_BOOKMARK, {
    url: 'https://example.com/x',
    parentKey: 'a totally different parent',
  });
  assert.equal(plain, withParent, 'a parent-dependent bookmark key would turn moves into delete+add');
});

test('folder keys depend on the parent folder', async () => {
  const first = await deriveKey(TYPE_FOLDER, { parentKey: 'p1', title: 'Work' });
  const second = await deriveKey(TYPE_FOLDER, { parentKey: 'p2', title: 'Work' });
  assert.notEqual(first, second, 'same-name folders under different parents are different identities');
});

test('same-name sibling folders are the same identity', async () => {
  const first = await deriveKey(TYPE_FOLDER, { parentKey: 'p', title: 'Work' });
  const second = await deriveKey(TYPE_FOLDER, { parentKey: 'p', title: 'Work' });
  assert.equal(first, second);
});

test('bookmarks and folders never share a key', async () => {
  const bookmarkKey = await deriveKey(TYPE_BOOKMARK, { url: 'u:same content' });
  const folderKey = await deriveKey(TYPE_FOLDER, { parentKey: '', title: 'same content' });
  assert.notEqual(bookmarkKey, folderKey);
});

// ── Bulk derivation ─────────────────────────────────────────────────

test('deriveKeys matches repeated deriveKey and preserves order', async () => {
  const inputs = [
    { type: TYPE_BOOKMARK, url: 'https://a.example' },
    { type: TYPE_FOLDER, parentKey: 'root', title: 'Folder' },
    { type: TYPE_BOOKMARK, url: 'https://b.example' },
  ];
  const batched = await deriveKeys(inputs);
  assert.equal(batched.length, inputs.length);
  for (let i = 0; i < inputs.length; i++) {
    const single = await deriveKey(inputs[i].type, inputs[i]);
    assert.equal(batched[i], single, `item ${i} differs`);
  }
});

test('deriveKeys handles an empty array', async () => {
  assert.deepEqual(await deriveKeys([]), []);
});

// ── Root constants ──────────────────────────────────────────────────

test('isRootFolder recognizes the four system roots', () => {
  for (const id of [ROOT_TOOLBAR, ROOT_MENU, ROOT_UNFILED, ROOT_MOBILE]) {
    assert.equal(isRootFolder(id), true, `${id} should be a root`);
  }
  assert.equal(isRootFolder('some ordinary key'), false);
  assert.equal(isRootFolder(''), false);
});

// These four ids must exactly match BookmarkSync.Domain/State.cs, or parent
// references disagree across sides. check-extension.sh also checks, but here
// with a concrete failure message.
test('root ids match the fixed Firefox Places values', () => {
  assert.equal(ROOT_TOOLBAR, 'toolbar_____');
  assert.equal(ROOT_MENU, 'menu________');
  assert.equal(ROOT_UNFILED, 'unfiled_____');
  assert.equal(ROOT_MOBILE, 'mobile______');
  for (const id of [ROOT_TOOLBAR, ROOT_MENU, ROOT_UNFILED, ROOT_MOBILE]) {
    assert.equal(id.length, 12, `${id} should be length 12`);
  }
});

test('isRootEnabled excludes mobile by default', () => {
  const on = new Set([ROOT_TOOLBAR, ROOT_MENU, ROOT_UNFILED]);
  assert.equal(isRootEnabled(ROOT_TOOLBAR, on), true);
  assert.equal(isRootEnabled(ROOT_UNFILED, on), true);
  assert.equal(isRootEnabled(ROOT_MOBILE, on), false, 'mobile bookmarks excluded by default');
  assert.equal(isRootEnabled(ROOT_MOBILE, new Set([ROOT_MOBILE])), false,
    'even when listed explicitly, mobile still needs includeMobile');
  assert.equal(isRootEnabled('unknown id', on), false);
});

test('isRootEnabled accepts arrays', () => {
  assert.equal(isRootEnabled(ROOT_TOOLBAR, [ROOT_TOOLBAR]), true);
  assert.equal(isRootEnabled(ROOT_TOOLBAR, []), false);
});

// ── Root resolution ─────────────────────────────────────────────────

test('resolveRoots recognizes the four ids', () => {
  const children = [
    { id: ROOT_TOOLBAR, title: 'Bookmarks Toolbar' },
    { id: ROOT_MENU, title: 'Bookmarks Menu' },
    { id: ROOT_UNFILED, title: 'Other Bookmarks' },
    { id: ROOT_MOBILE, title: 'Mobile Bookmarks' },
  ];
  const resolved = resolveRoots(children);
  assert.equal(resolved.warned, false);
  assert.deepEqual(resolved.roots, [ROOT_TOOLBAR, ROOT_MENU, ROOT_UNFILED, ROOT_MOBILE]);
});

test('resolveRoots never matches by title (UI language changes)', () => {
  // English Firefox titles are Bookmarks Toolbar / Menu / Other Bookmarks.
  // Matching by title would break everywhere — only ids work.
  const children = [
    { id: ROOT_TOOLBAR, title: 'Bookmarks Toolbar' },
    { id: ROOT_MENU, title: 'Menu' },
    { id: ROOT_UNFILED, title: 'Other Bookmarks' },
    { id: ROOT_MOBILE, title: 'Mobile Bookmarks' },
  ];
  const resolved = resolveRoots(children);
  assert.equal(resolved.warned, false, 'a title-matching implementation would fail on another locale');
  assert.deepEqual(resolved.roots, [ROOT_TOOLBAR, ROOT_MENU, ROOT_UNFILED, ROOT_MOBILE]);
});

test('resolveRoots falls back by position with a warning on odd ids', () => {
  // Extreme case simulating a Firefox rework of the ids.
  const children = [
    { id: 'xxx-toolbar', title: 'Toolbar' },
    { id: 'xxx-menu', title: 'Menu' },
    { id: 'xxx-unfiled', title: 'Other' },
    { id: 'xxx-mobile', title: 'Mobile' },
  ];
  const resolved = resolveRoots(children);
  assert.equal(resolved.warned, true, 'unrecognized ids must warn instead of silently mismapping');
  assert.match(resolved.message, /Root/);
  assert.equal(resolved.roots.length, 4);
});

test('resolveRoots tolerates missing subdirectories', () => {
  const resolved = resolveRoots([{ id: ROOT_TOOLBAR, title: 'Toolbar' }]);
  assert.equal(resolved.warned, true);
  assert.match(resolved.message, /Root/);
});

test('resolveRoots handles empty input', () => {
  const empty = resolveRoots([]);
  assert.equal(empty.warned, true);
  const nulled = resolveRoots(null);
  assert.equal(nulled.warned, true);
});

// ── Key validity ────────────────────────────────────────────────────

test('isValidKey rejects malformed forms', () => {
  for (const key of ['', 'abc', 'z'.repeat(32), 'A'.repeat(32), '1'.repeat(31), '1'.repeat(33)]) {
    assert.equal(isValidKey(key), false, `should reject ${JSON.stringify(key)}`);
  }
  assert.equal(isValidKey('1'.repeat(32)), true);
  assert.equal(isValidKey('a'.repeat(32)), true);
});
