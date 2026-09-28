// extension/lib/sync.js —— 同步编排
//
// 把 collect / client / apply / store 串成一次完整的同步。
//
// ── 流程 ──────────────────────────────────────────────────────────────
//
//	1. 读缓存 state（基线）
//	2. 扫描本地书签树 → nodes
//	3. 对比基线，构造待上报的 state + base（给变化的项打新 HLC）
//	4. POST /api/sync
//	5. 用返回的 hlc 校准本地时钟
//	6. 对比返回的权威 state 与第 3 步的结果，算出应用计划
//	7. 执行计划，修改 Firefox 书签树
//	8. 缓存被返回的 state 完整替换
//
// ── 失败处理（design.md §9.6）────────────────────────────────────────
//
//	· 采集失败          → 不发请求，不动本地
//	· 网络/非 200      → **绝不动本地书签**，保留缓存
//	· 应用阶段中途失败  → 不回滚，提示"部分完成，建议重试"（重试是幂等的）
//
// 为什么不做应用阶段回滚：Firefox 书签树没有事务，反向回滚每一步的复杂度
// 远超收益；且 LWW 合并的方向性保证"重试只会更接近正确状态，不会造成破坏"。

import { HLC } from './hlc.js';
import { scan, buildState, SCHEMA_VERSION } from './collect.js';
import { planApply, executePlan } from './apply.js';
import { loadState, saveState, loadSettings, ensureDeviceId } from './store.js';
import { sync as httpSync, hasHostPermission, clockWarning, normalizeBaseUrl } from './client.js';

/** 生成一个设备标识。 */
function randomDeviceId() {
  const bytes = new Uint8Array(8);
  crypto.getRandomValues(bytes);
  return Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
}

/**
 * 执行一次完整同步。
 *
 * @param {object} api browser.bookmarks 的替身
 * @param {object} [opts]
 * @param {(phase: string) => void} [opts.onPhase] 阶段回调，用于更新 UI
 * @param {typeof fetch} [opts.fetchImpl] 注入 fetch，便于测试
 * @returns {Promise<object>} 同步结果摘要
 */
export async function runSync(api, opts = {}) {
  const { onPhase } = opts;
  const settings = await loadSettings();

  if (!normalizeBaseUrl(settings.serverUrl)) {
    throw new Error('尚未配置服务器地址，请先在设置中填写');
  }
  if (!settings.token) {
    throw new Error('尚未配置访问令牌，请先在设置中填写');
  }

  // Firefox MV3 需要用户手动授予 host 权限（design.md §9.3）。
  // 权限申请必须在 popup / options 的 click 手势里完成，这里只做检查：
  // background 的 onMessage 已无手势，调 request() 会直接抛错。
  const granted = await hasHostPermission(settings.serverUrl);
  if (!granted) {
    throw new Error(`未获得访问 ${settings.serverUrl} 的权限，请先在设置页点「测试连接」并在弹窗中允许后重试`);
  }

  onPhase?.('scanning');

  // ── 1–3. 采集 ────────────────────────────────────────────────
  const cached = await loadState();
  const scanResult = await scan(api, {
    enabledRoots: new Set(settings.enabledRoots || []),
    includeMobile: Boolean(settings.includeMobile),
  });

  // 用缓存里的最大 m 校准时钟，保证新打的时间戳大于所有历史记录
  const clock = new HLC();
  clock.observeMany(allStamps(cached));
  if (cached.hlc) clock.update(cached.hlc);

  const nowMs = Date.now();
  const outgoing = buildState(scanResult, cached, clock, nowMs);

  onPhase?.('uploading');

  // ── 4. 上报 ──────────────────────────────────────────────────
  // 调试日志只打到本机控制台（含标题/URL，明码），排查完可无视；
  // 不要把控制台内容贴到公开地方。
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
    throw new Error('服务端返回了无法识别的数据格式，请确认扩展与服务端版本配套');
  }

  // ── 5. 校时 ──────────────────────────────────────────────────
  // 服务端 hlc 是它见过的最大时间戳。吸收它之后，本机之后产生的任何
  // 时间戳都一定大于两台设备历史上的全部记录 —— 因果性由此保证。
  if (response.hlc) clock.update(response.hlc);

  const clockWarningText = clockWarning(nowMs, response.serverTime);

  // ── 6–7. 应用 ────────────────────────────────────────────────
  onPhase?.('applying');

  // 重扫一次：应用计划需要当前的实际状态（ffId 只有 scan() 能给出）
  const freshScan = await scan(api, {
    enabledRoots: new Set(settings.enabledRoots || []),
    includeMobile: Boolean(settings.includeMobile),
  });

  const plan = planApply(freshScan.nodes, response.state, freshScan.ffKeyToId);
  const applied = await executePlan(api, plan, {
    onProgress: (msg) => onPhase?.(msg),
  });

  // ── 8. 完整替换缓存 ──────────────────────────────────────────
  await saveState(response.state);

  const result = {
    ok: applied.failed.length === 0,
    scanned: scanResult.total,
    local: outgoing.stats,
    server: response.summary,
    conflicts: response.conflicts || [],
    applied,
    warnings: [...scanResult.warnings, ...plan.skipped.map((s) => `跳过 ${s.reason}: ${s.key}`)],
    clockWarning: clockWarningText,
    serverTime: response.serverTime,
  };

  if (applied.failed.length > 0) {
    // 不抛错：书签树没有事务，部分完成是既有事实。UI 需要显示"部分完成"，
    // 让用户知道重试是安全的。
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

/** UTF-8 字节数（与 Go 端 len() 口径一致；JS 的 .length 是 UTF-16 单元）。 */
function utf8len(s) {
  try {
    return new TextEncoder().encode(s || '').length;
  } catch {
    return String(s || '').length;
  }
}

/**
 * 上报前打一遍待发送内容的摘要：项数、payload 字节、标题最长的 5 条。
 * 定位"服务端拒绝同步：标题/URL超长"时直接看这份日志即可。
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
      `[bmsync] 上报 state：${keys.length} 项，base ${Object.keys(outgoing?.base || {}).length} 个，` +
        `state JSON 约 ${bytes} 字节，本地统计 ${JSON.stringify(outgoing?.stats || {})}`,
    );
    const ranked = keys
      .map((k) => ({ k, n: utf8len(items[k]?.n), u: utf8len(items[k]?.u), t: items[k]?.t }))
      .sort((a, b) => b.n - a.n)
      .slice(0, 5);
    for (const r of ranked) {
      const it = items[r.k];
      console.log(
        `[bmsync] 标题最长 top5：key=${r.k} type=${r.t} 标题${r.n}字节 url${r.u}字节` +
          ` 标题预览=${JSON.stringify(String(it?.n || '').slice(0, 120))}` +
          ` url预览=${JSON.stringify(String(it?.u || '').slice(0, 120))}`,
      );
    }
  } catch (err) {
    console.log(`[bmsync] 诊断日志生成失败（不影响同步）：${err?.message || err}`);
  }
}

/**
 * 服务端校验失败时，把报错里那个 32 位 key 对应的本地 item 翻出来：
 * 控制台打完整条目，抛给 UI 的信息里追加标题预览（截断 200 字）。
 * 找不到 key 时原样返回。
 */
function augmentValidationError(err, outgoing) {
  try {
    const msg = err?.message || '';
    const m = String(msg).match(/[0-9a-f]{32}/);
    if (!m) return err;
    const item = outgoing?.state?.items?.[m[0]];
    if (!item) return err;
    console.error(
      `[bmsync] 被服务端拒绝的条目：key=${m[0]} type=${item.t} parent=${item.p} ` +
        `标题${utf8len(item.n)}字节 url${utf8len(item.u)}字节 ` +
        `标题全文=${JSON.stringify(item.n)} url=${JSON.stringify(item.u)}`,
    );
    err.message =
      `${msg}（本地标题预览：${JSON.stringify(String(item.n || '').slice(0, 200))}）`;
  } catch {
    // 日志增强失败不改变原错误
  }
  return err;
}
