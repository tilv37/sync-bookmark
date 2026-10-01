// background.js — entry: message routing, badge state, sync orchestration
//
// WARNING: Firefox MV3 background.scripts are **classic scripts** without
// import/export support. Anything needing ES modules must go through dynamic
// import(). (`background.type: "module"` needs Firefox 121+; this project
// baselines 115.)
//
// ── Sync mutex ────────────────────────────────────────────────────────
//
// A second trigger while sync is in flight is ignored with an "in progress"
// reply instead of queueing. Merging is idempotent so queueing would not
// corrupt data — but two concurrent runs desync badge state from the user's
// mental model (the user sees "done" that belongs to the first run).

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
    // Throws when the extension is unloaded/reloaded — no UI left to update anyway
  }
}

function flash(state, ms = 3000) {
  clearTimeout(flashTimer);
  setBadge(state);
  flashTimer = setTimeout(() => setBadge(BADGE.idle), ms);
}

/** Lazily load an ES module (everything under lib/ is ESM). */
function load(path) {
  return import(browser.runtime.getURL(path));
}

listeners.sync = async () => {
  if (inFlight) {
    return { ok: false, error: 'Sync already in progress, please wait' };
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
  const { healthCheck, hasHostPermission } = await load('lib/client.js');
  try {
    const granted = await hasHostPermission(msg.serverUrl);
    if (!granted) {
      return { ok: false, error: `No permission to access ${msg.serverUrl} yet — permission must be granted from a settings-page click. Reload the extension (about:debugging) and click Test connection again` };
    }
    const data = await healthCheck(msg.serverUrl);
    return { ok: true, data };
  } catch (err) {
    return { ok: false, error: (err && err.message) || String(err) };
  }
};

listeners.getConflicts = async (msg) => {
  const { fetchConflicts } = await load('lib/client.js');
  const { loadSettings } = await load('lib/store.js');
  const settings = await loadSettings();
  try {
    const data = await fetchConflicts({ serverUrl: settings.serverUrl, token: settings.token, limit: msg?.limit ?? 50 });
    return { ok: true, conflicts: data.conflicts || [], total: data.total ?? 0 };
  } catch (err) {
    return { ok: false, error: (err && err.message) || String(err) };
  }
};

listeners.getHistory = async () => {
  const { fetchHistory } = await load('lib/client.js');
  const { loadSettings } = await load('lib/store.js');
  const settings = await loadSettings();
  try {
    const data = await fetchHistory({ serverUrl: settings.serverUrl, token: settings.token });
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

// Clear a possibly stale badge after a browser restart (the event page is
// non-persistent so it normally cannot linger, but hot reloads can leave one).
setBadge(BADGE.idle);
