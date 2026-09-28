// background.js —— 入口：消息路由、badge 状态、同步编排
//
// ⚠️ Firefox MV3 的 background.scripts 是**经典脚本**，不支持 import/export
//    语法。需要用到 ES module 时，一律走动态 import()。
//    （`background.type: "module"` 要 Firefox 121+，本项目基线是 115。）
//
// ── 同步互斥 ──────────────────────────────────────────────────────────
//
// 正在进行中的同步再次触发时直接忽略并返回"进行中"，而不是排队。
// 合并本身是幂等的，所以排队也不会破坏数据 —— 但两次同步同时跑会让
// badge 状态和用户的心智对不上（用户看到"完成"其实是第一次的结果）。

/** @type {Record<string, (msg: any) => Promise<any> | any>} */
const listeners = Object.create(null);

browser.runtime.onMessage.addListener((msg) => {
  const handler = listeners[msg && msg.type];
  if (!handler) {
    return Promise.resolve({ ok: false, error: `unknown message type: ${msg && msg.type}` });
  }
  return Promise.resolve(handler(msg)).catch((err) => ({
    ok: false,
    error: (err && err.message) || String(err),
  }));
});

let inFlight = null;

const BADGE = {
  idle: { text: '', color: '#4a4a4a' },
  syncing: { text: '…', color: '#0060df' },
  ok: { text: '✓', color: '#17803d' },
  error: { text: '!', color: '#c50042' },
  partial: { text: '~', color: '#c47f00' },
};

let flashTimer = null;

async function setBadge(state) {
  try {
    await browser.action.setBadgeText({ text: state.text });
    await browser.action.setBadgeBackgroundColor({ color: state.color });
  } catch {
    // 扩展被卸载/重载时这里会抛，忽略即可 —— 那时本来就不该再更新 UI
  }
}

function flash(state, ms = 3000) {
  clearTimeout(flashTimer);
  setBadge(state);
  flashTimer = setTimeout(() => setBadge(BADGE.idle), ms);
}

/** 惰性加载 ES module（lib/ 下的代码全部是 ESM）。 */
function load(path) {
  return import(browser.runtime.getURL(path));
}

listeners.sync = async () => {
  if (inFlight) {
    return { ok: false, error: '同步进行中，请稍候' };
  }

  setBadge(BADGE.syncing);
  try {
    const [{ runSync }, store] = await Promise.all([load('lib/sync.js'), load('lib/store.js')]);
    const settings = await store.loadSettings();
    const result = await runSync(browser.bookmarks, {
      onPhase: (phase) => {
        if (typeof phase === 'string' && phase.length <= 4) setBadge(BADGE.syncing);
      },
    });
    const meta = await store.loadMeta();
    flash(result.partial ? BADGE.partial : BADGE.ok);
    return { ok: true, result, meta, settings };
  } catch (err) {
    flash(BADGE.error, 5000);
    return { ok: false, error: (err && err.message) || String(err) };
  } finally {
    inFlight = null;
  }
};

listeners.getState = async () => {
  const { loadSettings, loadMeta, loadState } = await load('lib/store.js');
  return {
    ok: true,
    settings: await loadSettings(),
    meta: await loadMeta(),
    cachedItems: Object.keys((await loadState()).items || {}).length,
  };
};

listeners.testConnection = async (msg) => {
  const { healthCheck } = await load('lib/client.js');
  try {
    const data = await healthCheck(msg.serverUrl);
    return { ok: true, data };
  } catch (err) {
    return { ok: false, error: (err && err.message) || String(err) };
  }
};

listeners.getConflicts = async (msg) => {
  const { fetchConflicts } = await load('lib/client.js');
  const { loadSettings } = await load('lib/store.js');
  const s = await loadSettings();
  try {
    const data = await fetchConflicts({ serverUrl: s.serverUrl, token: s.token, limit: msg?.limit ?? 50 });
    return { ok: true, conflicts: data.conflicts || [], total: data.total ?? 0 };
  } catch (err) {
    return { ok: false, error: (err && err.message) || String(err) };
  }
};

listeners.getHistory = async () => {
  const { fetchHistory } = await load('lib/client.js');
  const { loadSettings } = await load('lib/store.js');
  const s = await loadSettings();
  try {
    const data = await fetchHistory({ serverUrl: s.serverUrl, token: s.token });
    return { ok: true, snapshots: data.snapshots || [] };
  } catch (err) {
    return { ok: false, error: (err && err.message) || String(err) };
  }
};

listeners.saveSettings = async (msg) => {
  const { saveSettings } = await load('lib/store.js');
  const saved = await saveSettings(msg.patch || {});
  return { ok: true, settings: saved };
};

listeners.resetCache = async () => {
  const { resetCache } = await load('lib/store.js');
  await resetCache();
  flash(BADGE.ok);
  return { ok: true };
};

// 浏览器重启后清掉可能残留的 badge（事件页是非持久的，正常不会残留，
// 但扩展热重载时可能留下）。
setBadge(BADGE.idle);
