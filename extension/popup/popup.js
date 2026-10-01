// popup.js — toolbar popup
//
// Only two jobs: start a sync and show the result. All logic lives in
// background; this page holds no business logic.
// Reason: the popup DOM is short-lived (gone once closed) while a sync can
// run for many seconds, so state must live in background to survive.
// Exception: host permission must be requested in the click gesture, so only
// this page can call it directly.

import { requestHostPermissionFromGesture } from '../lib/client.js';

const $ = (id) => document.getElementById(id);

let busy = false;
let cachedServerUrl = '';

function setBusy(on) {
  busy = on;
  $('sync').disabled = on;
  $('sync').textContent = on ? 'Syncing…' : 'Sync now';
}

function show(kind, text) {
  const el = $('status');
  el.textContent = text;
  el.className = kind; // ok / err / muted
}

function fmtTime(ms) {
  if (!ms) return 'Never synced';
  const diff = Date.now() - ms;
  if (diff < 60_000) return 'Just now';
  if (diff < 3_600_000) return `${Math.floor(diff / 60_000)} min ago`;
  if (diff < 86_400_000) return `${Math.floor(diff / 3_600_000)} hours ago`;
  return new Date(ms).toLocaleString();
}

function fmtSummary(syncResult) {
  const applied = syncResult.applied || {};
  const bits = [];
  if (applied.created) bits.push(`Added ${applied.created}`);
  if (applied.updated) bits.push(`Updated ${applied.updated}`);
  if (applied.moved) bits.push(`Moved ${applied.moved}`);
  if (applied.deleted) bits.push(`Deleted ${applied.deleted}`);
  if (!bits.length) bits.push('No changes');

  const total = syncResult.server
    ? `Cloud holds ${syncResult.server.created + syncResult.server.updated + syncResult.server.deleted + syncResult.server.unchanged} items`
    : '';
  return `${bits.join(', ')}${total ? ' · ' + total : ''}`;
}

async function refresh() {
  const res = await browser.runtime.sendMessage({ type: 'getState' });
  if (!res || !res.ok) {
    show('err', res?.error || 'Failed to read state');
    return;
  }
  $('last-sync').textContent = `Last sync: ${fmtTime(res.meta?.lastSyncAt)}`;
  $('cached').textContent = res.cachedItems
    ? `Local cache: ${res.cachedItems} items`
    : 'Not synced yet, no local cache';

  const settings = res.settings || {};
  cachedServerUrl = settings.serverUrl || '';
  const configured = Boolean(settings.serverUrl && settings.token);
  if (!configured) {
    show('muted', 'Server and token are not configured yet');
    $('sync').disabled = true;
  }
  $('open-options').hidden = configured;
  $('first-run').hidden = configured;
}

async function doSync() {
  if (busy) return;
  setBusy(true);
  show('muted', 'Syncing, keep the browser open…');
  try {
    // Call request() first thing in the gesture context: any await before it
    // drops the gesture.
    if (cachedServerUrl) {
      let granted = false;
      try {
        granted = await requestHostPermissionFromGesture(cachedServerUrl);
      } catch (err) {
        show('err', err?.message || String(err));
        return;
      }
      if (!granted) {
        show('err', 'Site access was denied; allow it before syncing');
        return;
      }
    }
    const res = await browser.runtime.sendMessage({ type: 'sync' });
    if (!res || !res.ok) {
      console.error(`[bmsync] Sync failed: ${res?.error || 'sync failed'}`, res);
      show('err', res?.error || 'Sync failed');
      return;
    }
    const syncResult = res.result;
    console.log('[bmsync] Sync succeeded', syncResult);
    if (syncResult.partial) {
      show('err', `Partially done: ${fmtSummary(syncResult)}. Retrying is safe.`);
    } else {
      show('ok', fmtSummary(syncResult));
    }
    if (syncResult.clockWarning) $('clock-warning').textContent = syncResult.clockWarning;
    if (syncResult.conflicts?.length) {
      $('conflicts').textContent = `${syncResult.conflicts.length} conflicts this run (resolved silently by timestamp)`;
    }
    if (syncResult.warnings?.length) {
      $('warnings').textContent = syncResult.warnings.join('; ');
    }
    await refresh();
  } finally {
    setBusy(false);
  }
}

$('sync').addEventListener('click', doSync);
$('open-options').addEventListener('click', () => browser.runtime.openOptionsPage());
$('view-conflicts').addEventListener('click', () => browser.runtime.openOptionsPage());

refresh();
