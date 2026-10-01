// extension/lib/sync.js — sync orchestration
//
// Wires collect / client / apply / store into one full sync.
//
// ── Flow ────────────────────────────────────────────────────────────────
//  1. Read the cached state (baseline)
//  2. Scan the local bookmark tree -> nodes
//  3. Diff against the baseline, build the state + base to upload (fresh HLC for changed items)
//  4. POST /api/sync
//  5. Calibrate the local clock with the returned hlc
//  6. Diff the returned authoritative state against step 3, plan the apply
//  7. Execute the plan against the Firefox bookmark tree
//  8. Replace the cache with the returned state wholesale
//
// ── Failure handling (docs/architecture.md §4) ─────────────────────────────────
//  · collect fails       → no request, local untouched
//  · network / non-200   → **never touch local bookmarks**, keep the cache
//  · apply fails midway  → no rollback, report "partial, retry suggested" (retry is idempotent)
//
// Why no apply-phase rollback: the Firefox bookmark tree has no
// transactions; reverse-rolling every step costs far more than it saves, and
// LWW merge directionality guarantees "retry only gets closer to the correct
// state, never destroys".

import { HLC } from './hlc.js';
import { scan, buildState, SCHEMA_VERSION } from './collect.js';
import { planApply, executePlan } from './apply.js';
import { loadState, saveState, loadSettings, ensureDeviceId } from './store.js';
import { sync as httpSync, hasHostPermission, clockWarning, normalizeBaseUrl } from './client.js';

/** Generate a device id. */
function randomDeviceId() {
  const bytes = new Uint8Array(8);
  crypto.getRandomValues(bytes);
  return Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0')).join('');
}

/**
 * Run one full sync.
 *
 * @param {object} api browser.bookmarks stand-in
 * @param {object} [opts]
 * @param {(phase: string) => void} [opts.onPhase] Phase callback for UI updates
 * @param {typeof fetch} [opts.fetchImpl] Injected fetch, for tests
 * @returns {Promise<object>} Sync result summary
 */
export async function runSync(api, opts = {}) {
  const { onPhase } = opts;
  const settings = await loadSettings();

  if (!normalizeBaseUrl(settings.serverUrl)) {
    throw new Error('Server URL is not configured yet; fill it in on the settings page first');
  }
  if (!settings.token) {
    throw new Error('Access token is not configured yet; fill it in on the settings page first');
  }

  // Firefox MV3 requires the user to grant host permission manually (docs/architecture.md §7).
  // Permission must be requested in a popup/options click gesture; here we only check:
  // background onMessage has lost the gesture, and request() there throws.
  const granted = await hasHostPermission(settings.serverUrl);
  if (!granted) {
    throw new Error(`No permission to access ${settings.serverUrl}; click "Test connection" on the settings page and allow it, then retry`);
  }

  onPhase?.('scanning');

  // ── 1-3. Collect ──────────────────────────────────────────────
  const cached = await loadState();
  const scanResult = await scan(api, {
    enabledRoots: new Set(settings.enabledRoots || []),
    includeMobile: Boolean(settings.includeMobile),
  });

  // Calibrate the clock with the largest cached m, so fresh stamps exceed all history
  const clock = new HLC();
  clock.observeMany(allStamps(cached));
  if (cached.hlc) clock.update(cached.hlc);

  const nowMs = Date.now();
  const outgoing = buildState(scanResult, cached, clock, nowMs);

  onPhase?.('uploading');

  // ── 4. Upload ─────────────────────────────────────────────────
  // Debug logs go to the local console only (they include titles/URLs in
  // cleartext); safe to ignore after debugging. Never paste console output
  // anywhere public.
  logOutgoingDiagnostics(outgoing);
  const deviceId = await ensureDeviceId(randomDeviceId);
  let response;
  try {
    response = await httpSync({
      serverUrl: settings.serverUrl,
      token: settings.token,
      device: settings.deviceName || deviceId,
      state: outgoing.state,
      base: outgoing.base,
    });
  } catch (err) {
    throw augmentValidationError(err, outgoing);
  }

  if (!response || response.state?.v !== SCHEMA_VERSION) {
    throw new Error('Server returned an unrecognized data format; make sure the extension and server versions match');
  }

  // ── 5. Calibrate ──────────────────────────────────────────────
  // The server hlc is the largest stamp it has seen. Absorbing it makes every
  // later local stamp exceed the full history of both devices — that is what
  // guarantees causality.
  if (response.hlc) clock.update(response.hlc);

  const clockWarningText = clockWarning(nowMs, response.serverTime);

  // ── 6-7. Apply ────────────────────────────────────────────────
  onPhase?.('applying');

  // Re-scan: the apply plan needs the current live state (only scan() yields Firefox ids)
  const freshScan = await scan(api, {
    enabledRoots: new Set(settings.enabledRoots || []),
    includeMobile: Boolean(settings.includeMobile),
  });

  const plan = planApply(freshScan.nodes, response.state, freshScan.firefoxIdBySyncKey);
  const applied = await executePlan(api, plan, {
    onProgress: (msg) => onPhase?.(msg),
  });

  // ── 8. Replace the cache wholesale ────────────────────────────
  await saveState(response.state);

  const result = {
    ok: applied.failed.length === 0,
    scanned: scanResult.total,
    local: outgoing.stats,
    server: response.summary,
    conflicts: response.conflicts || [],
    applied,
    warnings: [...scanResult.warnings, ...plan.skipped.map((skipped) => `Skipped ${skipped.reason}: ${skipped.key}`)],
    clockWarning: clockWarningText,
    serverTime: response.serverTime,
  };

  if (applied.failed.length > 0) {
    // Do not throw: without transactions, partial completion is a fact. The
    // UI must show "partial" so users know retrying is safe.
    result.partial = true;
  }
  return result;
}

function allStamps(state) {
  const out = [];
  for (const item of Object.values(state?.items || {})) {
    if (item.m) out.push(item.m);
    if (item.a) out.push(item.a);
  }
  return out;
}

/** UTF-8 byte length (same as C# length semantics; JS .length counts UTF-16 units). */
function utf8len(text) {
  try {
    return new TextEncoder().encode(text || '').length;
  } catch {
    return String(text || '').length;
  }
}

/**
 * Log a summary of the outgoing payload before upload: item count, payload
 * bytes, 5 longest titles. When the server rejects sync for "title/URL too
 * long", this log pinpoints it.
 */
function logOutgoingDiagnostics(outgoing) {
  try {
    const items = outgoing?.state?.items || {};
    const keys = Object.keys(items);
    let bytes = 0;
    try {
      bytes = utf8len(JSON.stringify(outgoing?.state || {}));
    } catch {
      bytes = -1;
    }
    console.log(
      `[bmsync] Uploading state: ${keys.length} items, ${Object.keys(outgoing?.base || {}).length} base entries, ` +
        `state JSON ~${bytes} bytes, local stats ${JSON.stringify(outgoing?.stats || {})}`,
    );
    const ranked = keys
      .map((key) => ({ key, titleLen: utf8len(items[key]?.n), urlLen: utf8len(items[key]?.u), type: items[key]?.t }))
      .sort((left, right) => right.titleLen - left.titleLen)
      .slice(0, 5);
    for (const row of ranked) {
      const item = items[row.key];
      console.log(
        `[bmsync] Longest titles top5: key=${row.key} type=${row.type} title=${row.titleLen}B url=${row.urlLen}B` +
          ` titlePreview=${JSON.stringify(String(item?.n || '').slice(0, 120))}` +
          ` urlPreview=${JSON.stringify(String(item?.u || '').slice(0, 120))}`,
      );
    }
  } catch (err) {
    console.log(`[bmsync] Diagnostic log failed (sync unaffected): ${err?.message || err}`);
  }
}

/**
 * On a server validation failure, look up the local item behind the 32-hex
 * key in the error: log the full entry to the console and append a title
 * preview (truncated to 200 chars) to the UI-facing message. Pass through
 * unchanged when the key is not found.
 */
function augmentValidationError(err, outgoing) {
  try {
    const msg = err?.message || '';
    const match = String(msg).match(/[0-9a-f]{32}/);
    if (!match) return err;
    const item = outgoing?.state?.items?.[match[0]];
    if (!item) return err;
    console.error(
      `[bmsync] Rejected entry: key=${match[0]} type=${item.t} parent=${item.p} ` +
        `title=${utf8len(item.n)}B url=${utf8len(item.u)}B ` +
        `fullTitle=${JSON.stringify(item.n)} url=${JSON.stringify(item.u)}`,
    );
    err.message =
      `${msg} (local title preview: ${JSON.stringify(String(item.n || '').slice(0, 200))})`;
  } catch {
    // Log enrichment must never change the original error
  }
  return err;
}
