// extension/lib/keys.js — deriving identity keys
//
// ── Why this exists ───────────────────────────────────────────────────
// Two-way merge needs a way to decide "these two bookmarks on two devices
// are the same thing". The Firefox bookmarks API only gives a **local
// integer id**, unusable across devices (and ids get recycled); Places also
// exposes no custom-metadata slot (annos need a private API).
//
// So the key must be computed directly from content, storing **no metadata**:
//
//   bookmark: key = SHA-256("u:" + url)              first 16 bytes
//   folder:   key = SHA-256("f:" + parentKey + SEP + title)
//
// SEP is U+0000 (see constant below). It is chosen over | or : because URLs
// and folder names can legally contain | and :, while NUL never appears in
// UTF-8 text — it keeps the concatenation unambiguous.
//
// ── Why bookmark keys exclude the parent ──────────────────────────────
// If bookmark keys included parentKey, **renaming a parent folder would
// change the keys of the whole subtree**, which then looks like "delete
// everything + create everything" and produces thousands of tombstones.
//
// With the scheme above:
//   · moving a bookmark to another folder → key unchanged, only p changes → correctly syncs as "move"
//   · renaming a folder                   → subtree keys change → rebuild, but LWW keeps the content
//
// ── Known limitations (accepted, see docs/architecture.md §2) ──────────────────
//  1. Same URL in different folders → merged into one
//  2. Same URL twice in one folder → deduped to one
//  3. Folder rename → subtree rebuild (content kept, but many tombstones)
//  4. Bookmark URL edit → looks like "delete old + add new" (invisible to users)

/** Fixed ids of the four system roots. Stable across languages and devices; usable directly as parent keys of top-level items. */
export const ROOT_TOOLBAR = 'toolbar_____'; // Bookmarks Toolbar
export const ROOT_MENU = 'menu________'; // Bookmarks Menu
export const ROOT_UNFILED = 'unfiled_____'; // Other Bookmarks
export const ROOT_MOBILE = 'mobile______'; // Mobile Bookmarks

const ROOT_SET = new Set([ROOT_TOOLBAR, ROOT_MENU, ROOT_UNFILED, ROOT_MOBILE]);

export function isRootFolder(key) {
  return ROOT_SET.has(key);
}

/** Item type. Matches the ItemType constants on the C# side. */
export const TYPE_BOOKMARK = 'b';
export const TYPE_FOLDER = 'f';

/** Separator inside the hashed material, see the header. */
const SEP = String.fromCharCode(0);

/**
 * Build the hash input for a key.
 *
 * Exported so tests can directly assert the influence of
 * parent / title / URL without running SHA-256 first.
 */
export function buildMaterial(type, { parentKey = '', title = '', url = '' } = {}) {
  if (type === TYPE_FOLDER) {
    return 'f:' + parentKey + SEP + title;
  }
  return 'u:' + url;
}

const encoder = new TextEncoder();

/** Take the first 16 bytes of the digest as 32 lowercase hex chars. */
function toKey(buf) {
  const bytes = new Uint8Array(buf);
  let out = '';
  for (let i = 0; i < 16; i++) {
    out += bytes[i].toString(16).padStart(2, '0');
  }
  return out;
}

/**
 * Derive a single key.
 *
 * @param {'b'|'f'} type
 * @param {{parentKey?: string, title?: string, url?: string}} parts
 * @returns {Promise<string>} 32-char hex
 */
export async function deriveKey(type, parts) {
  const digest = await crypto.subtle.digest('SHA-256', encoder.encode(buildMaterial(type, parts)));
  return toKey(digest);
}

/**
 * Derive keys in bulk.
 *
 * Why not await one by one: thousands of bookmarks would mean thousands of
 * microtask rounds. Batching into WebCrypto runs concurrently, measured an
 * order of magnitude faster.
 *
 * @param {Array<{type: string, parentKey?: string, title?: string, url?: string}>} inputs
 * @returns {Promise<string[]>} same length and order as inputs
 */
export function deriveKeys(inputs) {
  return Promise.all(
    inputs.map((parts) =>
      crypto.subtle.digest('SHA-256', encoder.encode(buildMaterial(parts.type, parts))).then(toKey),
    ),
  );
}

/**
 * Resolve the roots from getTree(), mapping them to the keys we use.
 *
 * The four Firefox system root ids are fixed across versions and UI
 * languages ("Bookmarks Toolbar" and its localized names all map to
 * toolbar_____), so **never match by title** — a language change breaks it.
 *
 * Fallback: if the id shape is unexpected (theoretically never happens),
 * map the first 4 children by array position and set warned so the UI warns once.
 *
 * @param {Array} rootChildren getTree()[0].children
 * @returns {{roots: string[], warned: boolean, message?: string}}
 */
export function resolveRoots(rootChildren) {
  const expected = [ROOT_TOOLBAR, ROOT_MENU, ROOT_UNFILED, ROOT_MOBILE];
  const children = rootChildren || [];

  const found = expected.filter((id) => children.some((child) => child && child.id === id));
  if (found.length === expected.length) {
    return { roots: expected, warned: false };
  }

  let message =
    `Root ids differ from expected (recognized ${found.length}/${expected.length}), fell back to positional mapping.`;
  if (children.length < expected.length) {
    message += ` Only ${children.length} subdirectories found; some roots will not sync.`;
  }
  return { roots: expected, warned: true, message };
}

/**
 * Whether a root should be included in sync.
 *
 * mobile is excluded by default: rarely useful on desktop and generates
 * pointless churn on phones. A debatable trade-off, see docs/architecture.md §11 Q5.
 */
export function isRootEnabled(rootId, enabledRoots) {
  if (rootId === ROOT_MOBILE) return false;
  const set = enabledRoots instanceof Set
    ? enabledRoots
    : new Set(enabledRoots || [ROOT_TOOLBAR, ROOT_MENU, ROOT_UNFILED]);
  return set.has(rootId);
}

/** Whether a key is valid 32-char lowercase hex. */
export function isValidKey(key) {
  return typeof key === 'string' && /^[0-9a-f]{32}$/.test(key);
}

export const KEY_LENGTH = 32;
