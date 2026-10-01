// extension/lib/client.js — talking to the server
//
// Job: translate raw fetch results into "readable errors + structured data".
//
// ── The most important rule ─────────────────────────────────────────────
// **No non-200 response may ever mutate local bookmarks.**
// The caller (sync flow) must obey: only touch the Firefox tree after a
// successful response is in hand. See docs/architecture.md §4.

import { saveMeta } from './store.js';

export const ERR_NO_SETTINGS = 'no_settings';
export const ERR_NO_PERMISSION = 'no_permission';
export const ERR_NETWORK = 'network_error';
export const ERR_UNAUTHORIZED = 'unauthorized';
export const ERR_RATE_LIMITED = 'rate_limited';
export const ERR_SERVER = 'server_error';
export const ERR_TIMEOUT = 'timeout';

const DEFAULT_TIMEOUT_MS = 60_000;

/** Normalize an address to origin + /api prefix without a trailing slash. */
export function normalizeBaseUrl(raw) {
  const trimmedInput = String(raw || '').trim();
  if (!trimmedInput) return '';
  const withScheme = /^https?:\/\//i.test(trimmedInput) ? trimmedInput : `https://${trimmedInput}`;
  let url;
  try {
    url = new URL(withScheme);
  } catch {
    return '';
  }
  if (url.protocol !== 'https:' && url.hostname !== 'localhost' && url.hostname !== '127.0.0.1') {
    // Bookmark URLs travel over the public internet; cleartext HTTP exposes browsing history.
    return '';
  }
  return `${url.origin}/api`;
}

/** Host permission required for the current config (no port: manifest matching ignores ports). */
export function requiredOrigin(serverUrl) {
  try {
    const parsed = new URL(serverUrl);
    return `${parsed.protocol}//${parsed.hostname}/*`;
  } catch {
    return null;
  }
}

/**
 * Check and request host permission.
 *
 * Firefox MV3 moved site permission from "granted at install" to "granted
 * manually by the user", so the first sync always lands here. See
 * docs/architecture.md §7.
 *
 * WARNING: must be called in a user-gesture context (directly in a popup /
 * options click handler). background runtime.onMessage has lost the gesture,
 * and request() there throws
 * "permissions.request may only be called from a user input handler".
 * In background, only hasHostPermission() (a contains check) is allowed.
 */
export async function ensureHostPermission(serverUrl) {
  const origin = requiredOrigin(serverUrl);
  if (!origin) return false;

  const has = await browser.permissions.contains({ origins: [origin] });
  if (has) return true;

  // request() must run in a user-gesture context, or it is silently denied
  return browser.permissions.request({ origins: [origin] });
}

/**
 * Gesture-context permission request: call request() synchronously with no await before it.
 *
 * Why a separate function: ensureHostPermission() awaits contains() before
 * request(), and that await drops the click gesture, so Firefox throws
 * "permissions.request may only be called from a user input handler".
 * A UI click handler must call request() immediately (requiredOrigin is
 * synchronous and keeps the gesture); when already granted, request()
 * resolves true without a second prompt.
 */
export function requestHostPermissionFromGesture(serverUrl) {
  const origin = requiredOrigin(serverUrl);
  if (!origin) return Promise.resolve(false);
  return browser.permissions.request({ origins: [origin] });
}

export async function hasHostPermission(serverUrl) {
  const origin = requiredOrigin(serverUrl);
  if (!origin) return false;
  return browser.permissions.contains({ origins: [origin] });
}

class SyncError extends Error {
  constructor(code, message, detail) {
    super(message);
    this.name = 'SyncError';
    this.code = code;
    this.detail = detail;
  }
}

async function request(url, { token, method = 'GET', body, timeoutMs = DEFAULT_TIMEOUT_MS }) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);

  // Request summary goes to the local console only: method, path, body bytes,
  // token presence — never the token itself.
  try {
    const bodyBytes = body ? JSON.stringify(body).length : 0;
    const itemCount = body?.state?.items ? Object.keys(body.state.items).length : null;
    console.log(
      `[bmsync] -> ${method} ${url} body ~${bodyBytes}B` +
        (itemCount !== null ? ` (state ${itemCount} items)` : '') +
        (token ? ' (with token)' : ' (no token)'),
    );
  } catch {
    // Logging must never block the request
  }

  let resp;
  try {
    resp = await fetch(url, {
      method,
      signal: controller.signal,
      headers: {
        ...(token ? { Authorization: `Bearer ${token}` } : {}),
        ...(body ? { 'Content-Type': 'application/json' } : {}),
      },
      ...(body ? { body: JSON.stringify(body) } : {}),
    });
  } catch (err) {
    if (err?.name === 'AbortError') {
      throw new SyncError(ERR_TIMEOUT, 'Request timed out; check the network or server status');
    }
    throw new SyncError(ERR_NETWORK, `Cannot reach the server: ${err?.message || err}`);
  } finally {
    clearTimeout(timer);
  }

  const text = await resp.text();
  let data = null;
  if (text) {
    try {
      data = JSON.parse(text);
    } catch {
      // A reverse proxy returning an HTML error page is common (e.g. 413/502)
      if (!resp.ok) {
        throw new SyncError(
          ERR_SERVER,
          `Server returned non-JSON content (HTTP ${resp.status}).` +
            'For 413, raise client_max_body_size in Nginx Proxy Manager.',
        );
      }
    }
  }

  if (!resp.ok) {
    const syncErr = toSyncError(resp.status, data);
    try {
      console.warn(`[bmsync] <- ${method} ${url} HTTP ${resp.status}: ${syncErr.message}`);
    } catch {
      // Logging must never change the error
    }
    throw syncErr;
  }
  try {
    console.log(`[bmsync] <- ${method} ${url} HTTP ${resp.status} OK`);
  } catch {
    // Logging must not affect the return
  }
  return data;
}

function toSyncError(status, data) {
  const serverMsg = (data && data.message) || '';
  switch (status) {
    case 401:
      return new SyncError(ERR_UNAUTHORIZED, 'Access token is invalid; check it on the settings page', data);
    case 403:
      return new SyncError(ERR_NO_PERMISSION, 'Missing permission to access this server', data);
    case 413:
      return new SyncError(
        ERR_SERVER,
        'Bookmark data exceeds the server limit. With Nginx Proxy Manager, add' +
          ' client_max_body_size 32m; in the Advanced panel',
        data,
      );
    case 429:
      return new SyncError(ERR_RATE_LIMITED, 'Too many requests; try again later', data);
    case 400:
    case 422:
      return new SyncError(ERR_SERVER, `Server rejected this sync: ${serverMsg}`, data);
    default:
      return new SyncError(ERR_SERVER, `Server returned HTTP ${status}${serverMsg ? ': ' + serverMsg : ''}`, data);
  }
}

/** Connectivity check. No token needed — /api/health is public. */
export async function healthCheck(serverUrl) {
  const base = normalizeBaseUrl(serverUrl);
  if (!base) throw new SyncError(ERR_NO_SETTINGS, 'Server URL is invalid');
  return request(`${base}/health`, { method: 'GET' });
}

/** Upload state and fetch back the merged authoritative state. */
export async function sync({ serverUrl, token, device, state, base }) {
  const url = normalizeBaseUrl(serverUrl);
  if (!url) throw new SyncError(ERR_NO_SETTINGS, 'Server URL is not configured yet; fill it in on the settings page first');
  if (!token) throw new SyncError(ERR_NO_SETTINGS, 'Access token is not configured yet');

  const data = await request(`${url}/sync`, {
    method: 'POST',
    token,
    // Many bookmarks can mean hundreds of KB; allow a more generous timeout
    timeoutMs: 120_000,
    body: { device, state, base },
  });

  await saveMeta({ lastSyncAt: Date.now(), lastServerTime: data?.serverTime ?? 0 });
  return data;
}

/** Read the conflict list. */
export async function fetchConflicts({ serverUrl, token, limit = 50 }) {
  const base = normalizeBaseUrl(serverUrl);
  if (!base) throw new SyncError(ERR_NO_SETTINGS, 'Server URL is invalid');
  return request(`${base}/conflicts?limit=${limit}`, { token });
}

/** Read the history snapshot list. */
export async function fetchHistory({ serverUrl, token }) {
  const base = normalizeBaseUrl(serverUrl);
  if (!base) throw new SyncError(ERR_NO_SETTINGS, 'Server URL is invalid');
  return request(`${base}/history`, { token });
}

/**
 * Detect a badly skewed local clock.
 *
 * HLC preserves causality, so skew alone never loses data. But if the local
 * clock jumps far into the future (mistakenly set to 2035, say), its stamps
 * suppress every other device's edits and **never self-heal**. Warn so the
 * user can fix it. See docs/architecture.md §11.
 */
export function clockWarning(localNow, serverTime) {
  if (!serverTime) return null;
  const diffDays = Math.abs(localNow - serverTime) / 86_400_000;
  if (diffDays > 365) {
    return `Local system time differs from the server by about ${Math.round(diffDays)} days.` +
      ' No data will be lost, but stamps from this machine will keep overriding other devices. Please correct the system time.';
  }
  return null;
}
