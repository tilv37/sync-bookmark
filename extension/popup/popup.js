// popup.js —— 工具栏弹窗
//
// 只做两件事：发起同步、展示结果。所有逻辑在 background 里，这里不发业务逻辑。
// 原因：弹窗的 DOM 生命周期很短（关掉就没了），同步可能跑十几秒，
// 状态必须放在 background 才不会丢。

const $ = (id) => document.getElementById(id);

let busy = false;

function setBusy(on) {
  busy = on;
  $('sync').disabled = on;
  $('sync').textContent = on ? '同步中…' : '立即同步';
}

function show(kind, text) {
  const el = $('status');
  el.textContent = text;
  el.className = kind; // ok / err / muted
}

function fmtTime(ms) {
  if (!ms) return '从未同步';
  const diff = Date.now() - ms;
  if (diff < 60_000) return '刚刚';
  if (diff < 3_600_000) return `${Math.floor(diff / 60_000)} 分钟前`;
  if (diff < 86_400_000) return `${Math.floor(diff / 3_600_000)} 小时前`;
  return new Date(ms).toLocaleString();
}

function fmtSummary(r) {
  const a = r.applied || {};
  const bits = [];
  if (a.created) bits.push(`新增 ${a.created}`);
  if (a.updated) bits.push(`更新 ${a.updated}`);
  if (a.moved) bits.push(`移动 ${a.moved}`);
  if (a.deleted) bits.push(`删除 ${a.deleted}`);
  if (!bits.length) bits.push('无变化');

  const total = r.server
    ? `云端共 ${r.server.created + r.server.updated + r.server.deleted + r.server.unchanged} 项`
    : '';
  return `${bits.join('，')}${total ? ' · ' + total : ''}`;
}

async function refresh() {
  const res = await browser.runtime.sendMessage({ type: 'getState' });
  if (!res || !res.ok) {
    show('err', res?.error || '读取状态失败');
    return;
  }
  $('last-sync').textContent = `上次同步：${fmtTime(res.meta?.lastSyncAt)}`;
  $('cached').textContent = res.cachedItems
    ? `本地缓存 ${res.cachedItems} 项`
    : '尚未同步过，本地无缓存';

  const s = res.settings || {};
  const configured = Boolean(s.serverUrl && s.token);
  if (!configured) {
    show('muted', '尚未配置服务器与令牌');
    $('sync').disabled = true;
  }
  $('open-options').hidden = configured;
  $('first-run').hidden = configured;
}

async function doSync() {
  if (busy) return;
  setBusy(true);
  show('muted', '同步中，请勿关闭浏览器…');
  try {
    const res = await browser.runtime.sendMessage({ type: 'sync' });
    if (!res || !res.ok) {
      show('err', res?.error || '同步失败');
      return;
    }
    const r = res.result;
    if (r.partial) {
      show('err', `部分完成：${fmtSummary(r)}。重试是安全的。`);
    } else {
      show('ok', fmtSummary(r));
    }
    if (r.clockWarning) $('clock-warning').textContent = r.clockWarning;
    if (r.conflicts?.length) {
      $('conflicts').textContent = `本次产生 ${r.conflicts.length} 条冲突（已静默按时间戳决胜）`;
    }
    if (r.warnings?.length) {
      $('warnings').textContent = r.warnings.join('；');
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
