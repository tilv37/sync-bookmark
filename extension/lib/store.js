// extension/lib/store.js —— 本地缓存（IndexedDB）
//
// ── 缓存里存什么 ──────────────────────────────────────────────────────
//
// 服务端权威 state 的完整镜像。用途有三个：
//
//  1. 采集时需要基线，否则无法判断哪些项是"本次新改的"
//  2. 提供三方合并的 base（key → m）
//  3. 离线时不丢东西
//
// ── 关键约束 ──────────────────────────────────────────────────────────
//
// **每次同步后，缓存被服务端返回的 state 完整替换。**
// 不做增量合并。这让墓碑 GC 只需要在服务端做一处，客户端被动跟随，
// 不会出现两端清理步调不一致的问题（design.md §2.2）。
//
// ── 为什么用 IndexedDB 而不是 storage.local ────────────────────────────
//
// storage.local 默认上限 5MB（unlimitedStorage 权限下更大），但它是同步
// API，几千条书签读写会阻塞 UI 线程。IndexedDB 异步且容量大得多。

const DB_NAME = 'bmsync';
const DB_VERSION = 1;
const STORE = 'kv';

/** 缓存里各类数据的 key。 */
export const K_STATE = 'state'; // 服务端 state 镜像
export const K_SETTINGS = 'settings'; // 用户设置
export const K_META = 'meta'; // deviceId、lastSyncAt 等

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
    // fn 返回的是 IDBRequest：必须取 req.result，不能把 request 本身 resolve 出去。
    // 否则 key 不存在时 result 为 undefined，调用方会拿到 IDBRequest 对象，
    // 再经 runtime.sendMessage 做结构化克隆时直接抛
    // "IDBRequest object could not be cloned."（首次同步必现：缓存全空）。
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

// ── 高层封装 ──────────────────────────────────────────────────────────

/** 空 state。 */
export function emptyState() {
  return { v: 1, items: {} };
}

/** 读取缓存的 state。首次运行时返回空 state 而不是 null。 */
export async function loadState() {
  const s = await get(K_STATE);
  if (!s || typeof s !== 'object' || !s.items) return emptyState();
  // 结构兜底：缓存损坏时宁可从空开始，也不要把 undefined 带进合并逻辑
  return { v: s.v || 1, hlc: s.hlc || '', items: s.items || {} };
}

/** 用服务端的权威 state 完整替换缓存。 */
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
  const s = await get(K_SETTINGS);
  return { ...DEFAULT_SETTINGS, ...(s || {}) };
}

export async function saveSettings(patch) {
  const cur = await loadSettings();
  const next = { ...cur, ...patch };
  await set(K_SETTINGS, next);
  return next;
}

/** 设备 id：首次运行时生成并持久化，用于冲突记录与日志排查。 */
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
 * 清空本地缓存与设置，保留 deviceId。
 *
 * 用于"设置页 → 危险操作 → 重新初始化"。保留 deviceId 是为了让服务端
 * 的冲突日志仍能认出这是同一台设备。
 */
export async function resetCache() {
  const meta = await loadMeta();
  await clearAll();
  if (meta.deviceId) await set(K_META, meta);
}
