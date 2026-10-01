// extension/lib/store.js — local cache (IndexedDB)
//
// ── What the cache holds ──────────────────────────────────────────────
// A full mirror of the server-authoritative state. Three uses:
//  1. collection needs a baseline, or it cannot tell what is "newly changed"
//  2. provides the three-way merge base (key -> m)
//  3. nothing is lost offline
//
// ── Key constraint ────────────────────────────────────────────────────
// **After every sync, the cache is fully replaced by the returned state.**
// No incremental merge. Tombstone GC then only needs one place (the
// server); the client follows passively, so both sides never disagree on
// cleanup cadence (docs/architecture.md §1).
//
// ── Why IndexedDB instead of storage.local ────────────────────────────
// storage.local caps at 5MB by default (larger with unlimitedStorage) and is
// a synchronous API — thousands of bookmarks would block the UI thread.
// IndexedDB is async with far larger capacity.

const DB_NAME = 'bmsync';
const DB_VERSION = 1;
const STORE = 'kv';

/** Keys for each cached dataset. */
export const K_STATE = 'state'; // server state mirror
export const K_SETTINGS = 'settings'; // user settings
export const K_META = 'meta'; // deviceId, lastSyncAt, etc.

let dbPromise = null;

function openDB() {
  if (dbPromise) return dbPromise;
  dbPromise = new Promise((resolve, reject) => {
    const req = indexedDB.open(DB_NAME, DB_VERSION);
    req.onupgradeneeded = () => {
      const db = req.result;
      if (!db.objectStoreNames.contains(STORE)) {
        db.createObjectStore(STORE);
      }
    };
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  });
  return dbPromise;
}

async function withStore(mode, fn) {
  const db = await openDB();
  return new Promise((resolve, reject) => {
    const tx = db.transaction(STORE, mode);
    const store = tx.objectStore(STORE);
    let req;
    try {
      req = fn(store);
    } catch (err) {
      reject(err);
      return;
    }
    // fn returns an IDBRequest: resolve with req.result, never the request itself.
    // Otherwise a missing key yields undefined result while the caller receives
    // an IDBRequest object, which then throws
    // "IDBRequest object could not be cloned." via runtime.sendMessage
    // (reproduces on every first sync with an empty cache).
    tx.oncomplete = () => resolve(req != null && typeof req === 'object' && 'result' in req ? req.result : req);
    tx.onerror = () => reject(tx.error);
    tx.onabort = () => reject(tx.error);
  });
}

export async function get(key) {
  return withStore('readonly', (store) => store.get(key));
}

export async function set(key, value) {
  return withStore('readwrite', (store) => store.put(value, key));
}

export async function remove(key) {
  return withStore('readwrite', (store) => store.delete(key));
}

export async function clearAll() {
  return withStore('readwrite', (store) => store.clear());
}

// ── High-level wrappers ───────────────────────────────────────────────

 /** Empty state. */
export function emptyState() {
  return { v: 1, items: {} };
}

/** Read the cached state. Returns an empty state (not null) on first run. */
export async function loadState() {
  const cached = await get(K_STATE);
  if (!cached || typeof cached !== 'object' || !cached.items) return emptyState();
  // Shape fallback: on a corrupt cache, restart from empty rather than
  // carrying undefined into merge logic
  return { v: cached.v || 1, hlc: cached.hlc || '', items: cached.items || {} };
}

/** Fully replace the cache with the authoritative server state. */
export async function saveState(state) {
  await set(K_STATE, { v: state.v || 1, hlc: state.hlc || '', items: state.items || {} });
}

const DEFAULT_SETTINGS = {
  serverUrl: '',
  token: '',
  deviceId: '',
  deviceName: '',
  enabledRoots: ['toolbar_____', 'menu________', 'unfiled_____'],
  includeMobile: false,
};

export async function loadSettings() {
  const cached = await get(K_SETTINGS);
  return { ...DEFAULT_SETTINGS, ...(cached || {}) };
}

export async function saveSettings(patch) {
  const current = await loadSettings();
  const next = { ...current, ...patch };
  await set(K_SETTINGS, next);
  return next;
}

/** Device id: generated once on first run and persisted, for conflict records and log triage. */
export async function ensureDeviceId(generate) {
  const meta = (await get(K_META)) || {};
  if (meta.deviceId) return meta.deviceId;
  meta.deviceId = generate();
  await set(K_META, meta);
  return meta.deviceId;
}

export async function loadMeta() {
  return (await get(K_META)) || {};
}

export async function saveMeta(patch) {
  const meta = await loadMeta();
  const next = { ...meta, ...patch };
  await set(K_META, next);
  return next;
}

/**
 * Clear the local cache and settings, keeping the deviceId.
 *
 * Used by "Settings -> Danger zone -> Reinitialize". The deviceId is kept so
 * the server conflict log still recognizes this as the same device.
 */
export async function resetCache() {
  const meta = await loadMeta();
  await clearAll();
  if (meta.deviceId) await set(K_META, meta);
}
