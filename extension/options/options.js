// options.js —— 设置页
//
// 与 popup 一样，所有读写都经由 background 消息，本页不直接碰
// IndexedDB —— 设置只有一页，没必要在两处各写一份加载逻辑。
// 唯一的例外是 host 权限申请：permissions.request 必须在点击手势里调，
// background 的 onMessage 已无手势，只能由本页直接调。

import { requestHostPermissionFromGesture } from '../lib/client.js';

const $ = (id) => document.getElementById(id);

const ROOT_FIELDS = {
  rootToolbar: 'toolbar_____',
  rootMenu: 'menu________',
  rootUnfiled: 'unfiled_____',
};

let settings = null;

function setStatus(el, kind, text) {
  el.textContent = text;
  el.className = `status ${kind}`;
}

async function load() {
  const res = await browser.runtime.sendMessage({ type: 'getState' });
  if (!res?.ok) {
    setStatus($('connStatus'), 'err', res?.error || '读取设置失败');
    return;
  }
  settings = res.settings;
  $('serverUrl').value = settings.serverUrl || '';
  $('token').value = settings.token || '';
  for (const [id, root] of Object.entries(ROOT_FIELDS)) {
    $(id).checked = (settings.enabledRoots || []).includes(root);
  }
  $('rootMobile').checked = Boolean(settings.includeMobile);
}

async function save() {
  const enabledRoots = Object.entries(ROOT_FIELDS)
    .filter(([id]) => $(id).checked)
    .map(([, root]) => root);

  const patch = {
    serverUrl: $('serverUrl').value.trim(),
    token: $('token').value.trim(),
    enabledRoots,
    includeMobile: $('rootMobile').checked,
  };

  // 全不勾选会让同步变成"什么都不传"，看起来像同步成功但其实没做事。
  if (enabledRoots.length === 0 && !patch.includeMobile) {
    setStatus($('connStatus'), 'err', '至少要选择一个同步的根目录');
    return;
  }

  const res = await browser.runtime.sendMessage({ type: 'saveSettings', patch });
  if (!res?.ok) {
    setStatus($('connStatus'), 'err', res?.error || '保存失败');
    return;
  }
  settings = res.settings;
  setStatus($('connStatus'), 'ok', '已保存');
}

$('save').addEventListener('click', save);

$('toggleToken').addEventListener('click', () => {
  const input = $('token');
  const showing = input.type === 'text';
  input.type = showing ? 'password' : 'text';
  $('toggleToken').textContent = showing ? '显示' : '隐藏';
});

$('test').addEventListener('click', async () => {
  const url = $('serverUrl').value.trim();
  if (!url) {
    setStatus($('connStatus'), 'err', '请先填写服务器地址');
    return;
  }
  setStatus($('connStatus'), 'muted', '正在测试…');
  // 手势上下文中第一时间调 request()：前面加任何 await（哪怕是 contains）
  // 都会丢手势导致 "may only be called from a user input handler"。
  try {
    const granted = await requestHostPermissionFromGesture(url);
    if (!granted) {
      setStatus($('connStatus'), 'err', '已拒绝站点访问权限，允许后才能测试连接');
      return;
    }
  } catch (err) {
    setStatus($('connStatus'), 'err', err?.message || String(err));
    return;
  }
  const res = await browser.runtime.sendMessage({ type: 'testConnection', serverUrl: url });
  if (!res?.ok) {
    setStatus($('connStatus'), 'err', res?.error || '连接失败');
    return;
  }
  const d = res.data || {};
  setStatus(
    $('connStatus'),
    'ok',
    `连接正常 —— 服务端 schema v${d.schema}，共 ${d.items} 项（活跃 ${d.active}），` +
      `已有 ${d.conflicts} 条冲突记录`,
  );
});

function renderConflicts(list) {
  const box = $('conflictBox');
  if (!list.length) {
    box.innerHTML = '<p class="status muted">没有冲突记录。</p>';
    return;
  }
  const rows = list
    .map((c) => {
      const when = new Date(c.at).toLocaleString();
      const field = { parent: '所在目录', type: '类型', title: '标题', url: 'URL' }[c.field] || c.field;
      const who = c.winner === 'server' ? '云端胜出' : `${c.loser || '对方'} 修改被保留`;
      return `<tr>
        <td>${when}</td>
        <td>${field}</td>
        <td>“${escapeHtml(c.loserValue ?? '')}” → “${escapeHtml(c.winnerValue ?? '')}”<br />
            <span class="muted">${who}</span></td>
      </tr>`;
    })
    .join('');
  box.innerHTML = `<table>
      <thead><tr><th>时间</th><th>字段</th><th>变更</th></tr></thead>
      <tbody>${rows}</tbody>
    </table>`;
}

function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, (ch) =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[ch],
  );
}

$('loadConflicts').addEventListener('click', async () => {
  const res = await browser.runtime.sendMessage({ type: 'getConflicts', limit: 50 });
  if (!res?.ok) {
    $('conflictBox').innerHTML = `<p class="status err">${escapeHtml(res?.error || '读取失败')}</p>`;
    return;
  }
  renderConflicts(res.conflicts || []);
});

$('loadHistory').addEventListener('click', async () => {
  const res = await browser.runtime.sendMessage({ type: 'getHistory' });
  const box = $('historyBox');
  if (!res?.ok) {
    box.innerHTML = `<p class="status err">${escapeHtml(res?.error || '读取失败')}</p>`;
    return;
  }
  const list = res.snapshots || [];
  if (!list.length) {
    box.innerHTML = '<p class="status muted">还没有快照。首次成功同步后会自动创建。</p>';
    return;
  }
  const rows = list
    .map(
      (s) =>
        `<tr><td>${new Date(s.at).toLocaleString()}</td><td>${s.items} 项</td>
         <td>${(s.size / 1024).toFixed(1)} KB</td></tr>`,
    )
    .join('');
  box.innerHTML = `<table>
      <thead><tr><th>时间</th><th>条目数</th><th>大小</th></tr></thead>
      <tbody>${rows}</tbody>
    </table>
    <p class="status muted">回滚是手工操作，见 docs/OPERATIONS.md。</p>`;
});

$('reset').addEventListener('click', async () => {
  if (!confirm('确定清空本地缓存？\n\n云端数据不受影响，下次同步会重新拉取。')) return;
  const res = await browser.runtime.sendMessage({ type: 'resetCache' });
  if (!res?.ok) {
    setStatus($('resetStatus'), 'err', res?.error || '操作失败');
    return;
  }
  setStatus($('resetStatus'), 'ok', '本地缓存已清空');
});

load();
