// options.js — settings page
//
// Like the popup, all reads/writes go through background messages; this page
// never touches IndexedDB directly — one settings page needs no second copy
// of the loading logic.
// The only exception is host permission: permissions.request must run in a
// click gesture, and background onMessage has already lost it, so only this
// page can call it directly.

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
    setStatus($('connStatus'), 'err', res?.error || 'Failed to read settings');
    return;
  }
  settings = res.settings;
  $('serverUrl').value = settings.serverUrl || '';
  $('token').value = settings.token || '';
  for (const [fieldId, root] of Object.entries(ROOT_FIELDS)) {
    $(fieldId).checked = (settings.enabledRoots || []).includes(root);
  }
  $('rootMobile').checked = Boolean(settings.includeMobile);
}

async function save() {
  const enabledRoots = Object.entries(ROOT_FIELDS)
    .filter(([fieldId]) => $(fieldId).checked)
    .map(([, root]) => root);

  const patch = {
    serverUrl: $('serverUrl').value.trim(),
    token: $('token').value.trim(),
    enabledRoots,
    includeMobile: $('rootMobile').checked,
  };

  // Selecting nothing would sync "nothing at all" and look like a success
  // that did no work.
  if (enabledRoots.length === 0 && !patch.includeMobile) {
    setStatus($('connStatus'), 'err', 'Select at least one root folder to sync');
    return;
  }

  const res = await browser.runtime.sendMessage({ type: 'saveSettings', patch });
  if (!res?.ok) {
    setStatus($('connStatus'), 'err', res?.error || 'Save failed');
    return;
  }
  settings = res.settings;
  setStatus($('connStatus'), 'ok', 'Saved');
}

$('save').addEventListener('click', save);

$('toggleToken').addEventListener('click', () => {
  const input = $('token');
  const showing = input.type === 'text';
  input.type = showing ? 'password' : 'text';
  $('toggleToken').textContent = showing ? 'Show' : 'Hide';
});

$('test').addEventListener('click', async () => {
  const url = $('serverUrl').value.trim();
  if (!url) {
    setStatus($('connStatus'), 'err', 'Fill in the server URL first');
    return;
  }
  setStatus($('connStatus'), 'muted', 'Testing…');
  // Call request() first thing in the gesture context: any await before it
  // (even contains) drops the gesture with
  // "may only be called from a user input handler".
  try {
    const granted = await requestHostPermissionFromGesture(url);
    if (!granted) {
      setStatus($('connStatus'), 'err', 'Site access was denied; allow it before testing the connection');
      return;
    }
  } catch (err) {
    setStatus($('connStatus'), 'err', err?.message || String(err));
    return;
  }
  const res = await browser.runtime.sendMessage({ type: 'testConnection', serverUrl: url });
  if (!res?.ok) {
    setStatus($('connStatus'), 'err', res?.error || 'Connection failed');
    return;
  }
  const data = res.data || {};
  setStatus(
    $('connStatus'),
    'ok',
    `Connection OK — server schema v${data.schema}, ${data.items} items (${data.active} active), ` +
      `${data.conflicts} conflict records`,
  );
});

function renderConflicts(list) {
  const box = $('conflictBox');
  box.replaceChildren();
  if (!list.length) {
    box.append(statusPara('muted', 'No conflict records.'));
    return;
  }
  box.append(
    buildTable(
      ['Time', 'Field', 'Change'],
      list.map((conflict) => {
        const when = new Date(conflict.at).toLocaleString();
        const field =
          { parent: 'Parent', type: 'Type', title: 'Title', url: 'URL' }[conflict.field] ||
          conflict.field;
        const who = conflict.winner === 'server' ? 'Server won' : `${conflict.loser || 'Peer'} edit kept`;
        // Every dynamic value travels via textContent (see buildTable):
        // server-provided titles, URLs, and device names can never become markup.
        return [when, field, [`"${conflict.loserValue ?? ''}" → "${conflict.winnerValue ?? ''}"`, who]];
      }),
    ),
  );
}

// Builds a <table> purely from text: all cell content is assigned through
// textContent, so unsanitized server strings can never become markup.
// A cell is either a string, or a [mainLine, noteLine] pair rendered as a
// two-line cell with the second line muted.
function buildTable(headers, rows) {
  const table = document.createElement('table');
  const headRow = document.createElement('tr');
  for (const label of headers) {
    const th = document.createElement('th');
    th.textContent = label;
    headRow.append(th);
  }
  const thead = document.createElement('thead');
  thead.append(headRow);
  table.append(thead);
  const tbody = document.createElement('tbody');
  for (const cells of rows) {
    const row = document.createElement('tr');
    for (const cell of cells) {
      const td = document.createElement('td');
      if (Array.isArray(cell)) {
        td.textContent = cell[0];
        const note = document.createElement('span');
        note.className = 'muted';
        note.textContent = cell[1];
        td.append(document.createElement('br'), note);
      } else {
        td.textContent = cell;
      }
      row.append(td);
    }
    tbody.append(row);
  }
  table.append(tbody);
  return table;
}

function statusPara(kind, text) {
  const para = document.createElement('p');
  para.className = `status ${kind}`;
  para.textContent = text;
  return para;
}

$('loadConflicts').addEventListener('click', async () => {
  const res = await browser.runtime.sendMessage({ type: 'getConflicts', limit: 50 });
  const box = $('conflictBox');
  box.replaceChildren();
  if (!res?.ok) {
    box.append(statusPara('err', res?.error || 'Read failed'));
    return;
  }
  renderConflicts(res.conflicts || []);
});

$('loadHistory').addEventListener('click', async () => {
  const res = await browser.runtime.sendMessage({ type: 'getHistory' });
  const box = $('historyBox');
  box.replaceChildren();
  if (!res?.ok) {
    box.append(statusPara('err', res?.error || 'Read failed'));
    return;
  }
  const list = res.snapshots || [];
  if (!list.length) {
    box.append(statusPara('muted', 'No snapshots yet. The first successful sync creates one automatically.'));
    return;
  }
  box.append(
    buildTable(
      ['Time', 'Entries', 'Size'],
      list.map((snapshot) => [
        new Date(snapshot.at).toLocaleString(),
        `${snapshot.items} items`,
        `${(snapshot.size / 1024).toFixed(1)} KB`,
      ]),
    ),
  );
  box.append(statusPara('muted', 'Rollback is manual, see docs/OPERATIONS.md.'));
});

$('reset').addEventListener('click', async () => {
  if (!confirm('Clear the local cache?\n\nCloud data is unaffected; the next sync fetches it again.')) return;
  const res = await browser.runtime.sendMessage({ type: 'resetCache' });
  if (!res?.ok) {
    setStatus($('resetStatus'), 'err', res?.error || 'Operation failed');
    return;
  }
  setStatus($('resetStatus'), 'ok', 'Local cache cleared');
});

load();
