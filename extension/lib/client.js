// extension/lib/client.js —— 与服务端通信
//
// 职责：把 fetch 的原始结果翻译成「可读的错误 + 结构化的数据」。
//
// ── 最重要的一条规则 ──────────────────────────────────────────────────
//
// **任何非 200 响应都不得导致本地书签被修改。**
// 调用方（sync 流程）必须遵守：先成功拿到 response，再去动 Firefox 树。
// 见 design.md §9.6。

import { saveMeta } from './store.js';

export const ERR_NO_SETTINGS = 'no_settings';
export const ERR_NO_PERMISSION = 'no_permission';
export const ERR_NETWORK = 'network_error';
export const ERR_UNAUTHORIZED = 'unauthorized';
export const ERR_RATE_LIMITED = 'rate_limited';
export const ERR_SERVER = 'server_error';
export const ERR_TIMEOUT = 'timeout';

const DEFAULT_TIMEOUT_MS = 60_000;

/** 把地址规范化成不带尾斜杠的 origin + /api 前缀。 */
export function normalizeBaseUrl(raw) {
  const s = String(raw || '').trim();
  if (!s) return '';
  const withScheme = /^https?:\/\//i.test(s) ? s : `https://${s}`;
  let url;
  try {
    url = new URL(withScheme);
  } catch {
    return '';
  }
  if (url.protocol !== 'https:' && url.hostname !== 'localhost' && url.hostname !== '127.0.0.1') {
    // 书签 URL 会经过公网传输，明文 HTTP 等于把浏览历史裸奔。
    return '';
  }
  return `${url.origin}/api`;
}

/** 当前配置下所需的 host 权限。 */
export function requiredOrigin(serverUrl) {
  try {
    return `${new URL(serverUrl).origin}/*`;
  } catch {
    return null;
  }
}

/**
 * 检查并申请 host 权限。
 *
 * Firefox 的 MV3 把站点权限从「安装时授予」改成了「用户手动授予」，
 * 所以首次同步一定会走到这里。见 design.md §9.3。
 */
export async function ensureHostPermission(serverUrl) {
  const origin = requiredOrigin(serverUrl);
  if (!origin) return false;

  const has = await browser.permissions.contains({ origins: [origin] });
  if (has) return true;

  // request() 必须在用户手势的上下文中调用，否则会被静默拒绝
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
      throw new SyncError(ERR_TIMEOUT, '请求超时，请检查网络或服务端状态');
    }
    throw new SyncError(ERR_NETWORK, `无法连接服务器：${err?.message || err}`);
  } finally {
    clearTimeout(timer);
  }

  const text = await resp.text();
  let data = null;
  if (text) {
    try {
      data = JSON.parse(text);
    } catch {
      // 反代返回了 HTML 错误页是常见情况（比如 413/502）
      if (!resp.ok) {
        throw new SyncError(
          ERR_SERVER,
          `服务端返回了非 JSON 内容（HTTP ${resp.status}）。` +
            '若是 413，请在 Nginx Proxy Manager 中调大 client_max_body_size。',
        );
      }
    }
  }

  if (!resp.ok) {
    throw toSyncError(resp.status, data);
  }
  return data;
}

function toSyncError(status, data) {
  const msg = (data && data.message) || '';
  switch (status) {
    case 401:
      return new SyncError(ERR_UNAUTHORIZED, '访问令牌无效，请在设置中检查', data);
    case 403:
      return new SyncError(ERR_NO_PERMISSION, '缺少访问该服务器的权限', data);
    case 413:
      return new SyncError(
        ERR_SERVER,
        '书签数据超过服务端限制。若使用了 Nginx Proxy Manager，请在 Advanced 面板' +
          '加入 client_max_body_size 32m;',
        data,
      );
    case 429:
      return new SyncError(ERR_RATE_LIMITED, '请求过于频繁，请稍后再试', data);
    case 400:
    case 422:
      return new SyncError(ERR_SERVER, `服务端拒绝了这次同步：${msg}`, data);
    default:
      return new SyncError(ERR_SERVER, `服务端返回 HTTP ${status}${msg ? '：' + msg : ''}`, data);
  }
}

/** 连通性检查。不需要 token —— /api/health 是公开的。 */
export async function healthCheck(serverUrl) {
  const base = normalizeBaseUrl(serverUrl);
  if (!base) throw new SyncError(ERR_NO_SETTINGS, '服务器地址无效');
  return request(`${base}/health`, { method: 'GET' });
}

/** 上报 state 并取回合并后的权威 state。 */
export async function sync({ serverUrl, token, device, state, base }) {
  const url = normalizeBaseUrl(serverUrl);
  if (!url) throw new SyncError(ERR_NO_SETTINGS, '尚未配置服务器地址，请先在设置中填写');
  if (!token) throw new SyncError(ERR_NO_SETTINGS, '尚未配置访问令牌');

  const data = await request(`${url}/sync`, {
    method: 'POST',
    token,
    // 书签多时请求体可能几百 KB，超时要给得比普通请求宽裕
    timeoutMs: 120_000,
    body: { device, state, base },
  });

  await saveMeta({ lastSyncAt: Date.now(), lastServerTime: data?.serverTime ?? 0 });
  return data;
}

/** 读取冲突列表。 */
export async function fetchConflicts({ serverUrl, token, limit = 50 }) {
  const base = normalizeBaseUrl(serverUrl);
  if (!base) throw new SyncError(ERR_NO_SETTINGS, '服务器地址无效');
  return request(`${base}/conflicts?limit=${limit}`, { token });
}

/** 读取历史快照列表。 */
export async function fetchHistory({ serverUrl, token }) {
  const base = normalizeBaseUrl(serverUrl);
  if (!base) throw new SyncError(ERR_NO_SETTINGS, '服务器地址无效');
  return request(`${base}/history`, { token });
}

/**
 * 检测本地时钟是否明显异常。
 *
 * HLC 保证了因果性，所以时钟偏差本身不会导致丢数据。但若本地时钟被大幅
 * 调快（误设为 2035 年之类），它产生的时间戳会压制其他所有设备的改动，
 * 且**不会自愈**。这里给一个提示，让用户有机会去改回来。
 * 见 design.md §12.4。
 */
export function clockWarning(localNow, serverTime) {
  if (!serverTime) return null;
  const diffDays = Math.abs(localNow - serverTime) / 86_400_000;
  if (diffDays > 365) {
    return `本地系统时间与服务器相差约 ${Math.round(diffDays)} 天。` +
      '这不会导致丢数据，但本机新产生的时间戳会长期压制其他设备的改动。建议校正系统时间。';
  }
  return null;
}
