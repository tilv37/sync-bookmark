// extension/lib/collect.test.js —— 采集：书签树 → 同步 state
//
// 采集要回答的问题是："跟上次相比，什么变了"。答案错了会有两种后果：
//
//   · 漏报 → 本地改动永远传不上云端
//   · 误报 → 给没变的项打新时间戳 → 两台设备互相覆盖，永远收敛不了
//
// 所以这里最关键的不是"能不能扫出书签"，而是**没变的项有没有被原样放过**。

import test from 'node:test';
import assert from 'node:assert/strict';

import { HLC, HLC_ZERO, compare } from './hlc.js';
import { deriveKey, isRootFolder, ROOT_TOOLBAR, ROOT_MENU, ROOT_UNFILED } from './keys.js';
import { scan, buildState, contentChanged, SCHEMA_VERSION } from './collect.js';
import { MockBookmarks } from './mock-bookmarks.js';

const ALL_ROOTS = new Set([ROOT_TOOLBAR, ROOT_MENU, ROOT_UNFILED]);
const NOW = 1_700_000_000_000;

// 一个**跨调用持久**的 HLC，外加一个会走动的物理时钟。
//
// 为什么要持久：真实流程里 HLC 只创建一次（sync.js 里），并用缓存中
// 最大的 m 校准。如果每个测试都 new HLC(() => 常量)，
// 两次采集会打出**完全相同**的时间戳，于是"修改后 m 必须更大"这类
// 断言永远失败 —— 症状看起来像实现的 bug，其实是测试没模拟真实时序。
//
// now() 也是走动的：现实里墙钟在推进，HLC 靠它区分不同的事件。
let clockMs = NOW;
const clock = new HLC(() => clockMs);

function tickClock(ms = 1) {
  clockMs += ms;
}

/** 跑一次完整采集：scan + buildState。 */
async function collect(bm, { cached = null, clk = clock } = {}) {
  const scanResult = await scan(bm, { enabledRoots: ALL_ROOTS });
  const out = buildState(scanResult, cached || { v: SCHEMA_VERSION, items: {} }, clk, clockMs);
  return { ...out, scanResult, clock: clk };
}

/** 便捷：取某个 key 的 item */
const itemOf = (state, key) => state.items[key];

// ── 基本扫描 ────────────────────────────────────────────────────────
//
// 每个 test 之间 clock 是共享的（见顶部说明），所以这里不需要重置。
// 需要干净状态的用 nested t.beforeEach 那几个。

test('扫描出书签与文件夹，并算出 key', async () => {
  const bm = new MockBookmarks();
  const f = bm.addFolder(ROOT_TOOLBAR, '工作');
  const b = bm.addBookmark(f, '某文档', 'https://example.com/doc');

  const { state, scanResult } = await collect(bm);

  assert.equal(scanResult.total, 2, '应识别出 1 个文件夹 + 1 个书签');
  assert.equal(Object.keys(state.items).length, 2);

  const fk = scanResult.ffIdToKey.get(f);
  const bk = scanResult.ffIdToKey.get(b);
  assert.ok(fk && bk);
  assert.equal(state.items[fk].t, 'f');
  assert.equal(state.items[bk].t, 'b');
  assert.equal(state.items[bk].u, 'https://example.com/doc');
  assert.equal(state.items[bk].p, fk, '书签的父级应是文件夹的 key');
  assert.equal(state.items[fk].p, ROOT_TOOLBAR, '顶层文件夹的父级是根目录字面量');
});

test('★ 根目录本身不作为 item 参与同步', async () => {
  const bm = new MockBookmarks();
  bm.addBookmark(ROOT_TOOLBAR, 'a', 'https://a.example');
  const { state } = await collect(bm);

  for (const key of Object.keys(state.items)) {
    assert.ok(!isRootFolder(state.items[key].p) === false || isRootFolder(state.items[key].p),
      '父级可以是根目录');
  }
  // items 里不该出现 key 等于根目录 id 的项
  assert.equal(state.items[ROOT_TOOLBAR], undefined);
  assert.equal(state.items[ROOT_MENU], undefined);
  assert.equal(state.items[ROOT_UNFILED], undefined);
});

test('深层嵌套：父级 key 逐层正确解析', async () => {
  const bm = new MockBookmarks();
  let parent = ROOT_TOOLBAR;
  const ids = [];
  for (let i = 0; i < 5; i++) {
    parent = bm.addFolder(parent, `层${i}`);
    ids.push(parent);
  }
  const leaf = bm.addBookmark(parent, '最深', 'https://deep.example');

  const { state, scanResult } = await collect(bm);
  assert.equal(scanResult.total, 6);

  // 从叶子往上走，每一级父级都应该是上一个 key
  let childKey = scanResult.ffIdToKey.get(leaf);
  for (let i = ids.length - 1; i >= 0; i--) {
    const parentKey = scanResult.ffIdToKey.get(ids[i]);
    assert.equal(state.items[childKey].p, parentKey, `第 ${i} 层的父级 key 错误`);
    childKey = parentKey;
  }
  assert.equal(state.items[childKey].p, ROOT_TOOLBAR);
});

test('按 enabledRoots 过滤：未勾选的根目录不入 state', async () => {
  const bm = new MockBookmarks();
  bm.addBookmark(ROOT_TOOLBAR, '在书签栏', 'https://t.example');
  bm.addBookmark(ROOT_UNFILED, '在其他书签', 'https://u.example');

  const scanResult = await scan(bm, { enabledRoots: new Set([ROOT_TOOLBAR]) });
  const out = buildState(scanResult, { v: SCHEMA_VERSION, items: {} }, new HLC(() => NOW), NOW);

  const titles = Object.values(out.state.items).map((i) => i.n);
  assert.deepEqual(titles, ['在书签栏']);
});

test('includeMobile 为假时排除移动设备书签', async () => {
  const bm = new MockBookmarks();
  bm.addBookmark(ROOT_TOOLBAR, '桌面', 'https://d.example');
  bm.addBookmark('mobile______', '手机', 'https://m.example');

  const r = await collect(bm);
  const titles = Object.values(r.state.items).map((i) => i.n);
  assert.deepEqual(titles, ['桌面']);
});

test('includeMobile 为真时纳入移动设备书签', async () => {
  const bm = new MockBookmarks();
  bm.addBookmark('mobile______', '手机', 'https://m.example');

  const scanResult = await scan(bm, { enabledRoots: ALL_ROOTS, includeMobile: true });
  const out = buildState(scanResult, { v: SCHEMA_VERSION, items: {} }, new HLC(() => NOW), NOW);
  assert.equal(Object.keys(out.state.items).length, 1);
});

test('空书签树：产出空 state，不报错', async () => {
  const bm = new MockBookmarks();
  const { state, stats } = await collect(bm);
  assert.deepEqual(state.items, {});
  assert.equal(stats.created, 0);
  assert.equal(state.v, SCHEMA_VERSION);
});

test('无 url 的节点被当作文件夹（Firefox 用有无 url 区分类型）', async () => {
  const bm = new MockBookmarks();
  const f = bm.addFolder(ROOT_TOOLBAR, '目录');
  // 直接塞一个 url 为空串的节点，模拟脏数据
  bm.addBookmark(f, '空 url', '');
  const { state } = await collect(bm);
  const types = Object.values(state.items).map((i) => i.t);
  assert.deepEqual(types.sort(), ['f', 'f'], '空 url 应被当作文件夹');
});

// ── ★ 六种变更情形 ────────────────────────────────────────────────

test('★ 情形1 新增：全新项拿到新时间戳', async () => {
  const bm = new MockBookmarks();
  bm.addBookmark(ROOT_TOOLBAR, '新书签', 'https://new.example');

  const { state, base, stats } = await collect(bm);
  assert.equal(stats.created, 1);
  assert.equal(stats.updated, 0);
  assert.deepEqual(base, {}, '新增项不应出现在 base 里（它上次不存在）');
  for (const it of Object.values(state.items)) {
    assert.equal(it.m, it.a, '新增项的 m 与 a 相同');
    assert.ok(compare(it.m, HLC_ZERO) > 0);
  }
});

test('★ 情形2 未变化：原样沿用旧时间戳，不重新打点', async () => {
  // 这是最容易写错的一条。若未变化也打新时间戳，两台设备会互相覆盖。
  const bm = new MockBookmarks();
  const id = bm.addBookmark(ROOT_TOOLBAR, '稳定', 'https://stable.example');

  const first = await collect(bm);
  const key = Object.keys(first.state.items)[0];
  const originalM = first.state.items[key].m;

  // 什么都没动，再采一次
  const second = await collect(bm, { cached: first.state });

  assert.equal(second.state.items[key].m, originalM, '未变化项的 m 必须保持不变');
  assert.equal(second.stats.unchanged, 1);
  assert.equal(second.stats.created, 0);
  assert.equal(second.stats.updated, 0);
  assert.equal(second.base[key], originalM, 'base 应记录上次见到的 m');
  assert.ok(id);
});

test('★ 情形3 修改标题：打新时间戳', async () => {
  const bm = new MockBookmarks();
  bm.addBookmark(ROOT_TOOLBAR, '旧标题', 'https://x.example');

  const first = await collect(bm);
  const key = Object.keys(first.state.items)[0];
  const beforeM = first.state.items[key].m;

  // 改标题
  const node = [...bm.nodes.values()].find((n) => n.url === 'https://x.example');
  node.title = '新标题';

  const second = await collect(bm, { cached: first.state });
  assert.equal(second.state.items[key].n, '新标题');
  assert.equal(second.stats.updated, 1);
  assert.ok(compare(second.state.items[key].m, beforeM) > 0, '修改后 m 必须更大');
});

test('★ 情形4 修改 URL：表现为"删旧增新"', async () => {
  const bm = new MockBookmarks();
  bm.addBookmark(ROOT_TOOLBAR, '标题', 'https://old.example');

  const first = await collect(bm);
  const oldKey = Object.keys(first.state.items)[0];

  const node = [...bm.nodes.values()].find((n) => n.url === 'https://old.example');
  node.url = 'https://new.example';

  const second = await collect(bm, { cached: first.state });
  const newKey = Object.keys(second.state.items).find((k) => !second.state.items[k].d);

  assert.notEqual(newKey, oldKey, '改 URL 后 key 必然变化');
  assert.equal(second.state.items[oldKey].d, true, '旧 key 应变成墓碑');
  assert.equal(second.state.items[oldKey].x, NOW, '墓碑应带删除时间');
  assert.equal(second.state.items[newKey].u, 'https://new.example');
  assert.equal(second.stats.created, 1);
  assert.equal(second.stats.deleted, 1);
});

test('★ 情形5 移动：key 不变，只改父级', async () => {
  const bm = new MockBookmarks();
  const f1 = bm.addFolder(ROOT_TOOLBAR, '目录一');
  const f2 = bm.addFolder(ROOT_TOOLBAR, '目录二');
  bm.addBookmark(f1, '会动的书签', 'https://move.example');

  const first = await collect(bm);
  const key = Object.keys(first.state.items).find((k) => first.state.items[k].u === 'https://move.example');
  const beforeM = first.state.items[key].m;
  const f2key = [...first.scanResult.ffIdToKey].find(([id]) => id === f2)[1];

  // 移到另一个文件夹
  await bm.move([...bm.nodes.values()].find((n) => n.url === 'https://move.example').id,
    { parentId: f2 });

  const second = await collect(bm, { cached: first.state });
  assert.equal(second.state.items[key].u, 'https://move.example', '移动不应改变 key');
  assert.equal(second.state.items[key].p, f2key, '父级应变为新目录');
  assert.equal(second.stats.updated, 1);
  assert.equal(second.stats.deleted, 0, '移动不应产生墓碑');
  assert.ok(compare(second.state.items[key].m, beforeM) > 0);
});

test('★ 情形6 复活：之前被删的又出现', async () => {
  const bm = new MockBookmarks();
  bm.addBookmark(ROOT_TOOLBAR, '会回来的', 'https://back.example');

  const first = await collect(bm);
  const key = Object.keys(first.state.items)[0];

  // 删掉（模拟本地书签被删）
  const node = [...bm.nodes.values()].find((n) => n.url === 'https://back.example');
  await bm.remove(node.id);

  const second = await collect(bm, { cached: first.state });
  assert.equal(second.state.items[key].d, true, '应写墓碑');
  assert.equal(second.stats.deleted, 1);
  const deadM = second.state.items[key].m;

  // 又加回同一个 URL
  bm.addBookmark(ROOT_TOOLBAR, '会回来的', 'https://back.example');
  const third = await collect(bm, { cached: second.state });

  assert.ok(!third.state.items[key].d, '墓碑应被清除，书签复活');
  assert.equal(third.state.items[key].u, 'https://back.example');
  assert.ok(compare(third.state.items[key].m, deadM) > 0,
    '复活必须给更新的 m，否则墓碑会继续赢，书签永远回不来');
  assert.equal(third.stats.resurrected, 1);
});

// ── 删除的传播 ──────────────────────────────────────────────────────

test('★ 删除文件夹：子树每一项都各自写墓碑', async () => {
  // 服务端只知道"这一项没了"，它不会替我们推断子项也该没了。
  const bm = new MockBookmarks();
  const f = bm.addFolder(ROOT_TOOLBAR, '要删的目录');
  const sub = bm.addFolder(f, '子目录');
  bm.addBookmark(f, '直接子书签', 'https://a.example');
  bm.addBookmark(sub, '孙书签', 'https://b.example');

  const first = await collect(bm);
  assert.equal(Object.keys(first.state.items).length, 4, '目录 + 子目录 + 2 个书签');

  // 删掉整个目录
  await bm.remove(f);

  const second = await collect(bm, { cached: first.state });
  assert.equal(second.stats.deleted, 4, '子树 4 项全部应写墓碑');
  for (const [key, it] of Object.entries(second.state.items)) {
    assert.equal(it.d, true, `${key} 应为墓碑`);
    assert.equal(it.x, NOW);
  }
});

test('★ 删除后重加同名同位置的目录：不会与旧墓碑混淆', async () => {
  const bm = new MockBookmarks();
  const f = bm.addFolder(ROOT_TOOLBAR, '同名目录');

  const first = await collect(bm);
  const key = Object.keys(first.state.items)[0];
  assert.equal(first.state.items[key].t, 'f');

  // 删掉 → 墓碑
  await bm.remove(f);
  tickClock(1000);
  const dead = await collect(bm, { cached: first.state });
  assert.equal(dead.state.items[key].d, true, '应写墓碑');
  const deadM = dead.state.items[key].m;

  // 同名同位置重建 → key 相同，靠更新的 m 复活
  bm.addFolder(ROOT_TOOLBAR, '同名目录');
  tickClock(1000);
  const revived = await collect(bm, { cached: dead.state });

  assert.equal(Object.keys(revived.state.items).length, 1, '不应多出第二条记录');
  // 用 falsy 判断而不是 === false：复活后 d 字段被整体省略（omitempty），
  // 取值是 undefined。断言 === false 会让人以为"没复活"。
  assert.ok(!revived.state.items[key].d, '应复活（d 字段被省略即已复活）');
  assert.equal(revived.state.items[key].t, 'f');
  assert.ok(compare(revived.state.items[key].m, deadM) > 0,
    '复活必须给更新的 m，否则墓碑会继续赢');
  assert.equal(revived.state.items[key].x, undefined, '复活后不应残留删除时间');
});

test('已是墓碑的项在下次采集时原样保留（否则会被 GC，删除就传播不下去）', async () => {
  const bm = new MockBookmarks();
  bm.addBookmark(ROOT_TOOLBAR, '待删', 'https://gone.example');

  const first = await collect(bm);
  const key = Object.keys(first.state.items)[0];
  const node = [...bm.nodes.values()].find((n) => n.url === 'https://gone.example');
  await bm.remove(node.id);

  const second = await collect(bm, { cached: first.state });
  const deadM = second.state.items[key].m;

  // 本地依然没有它，再采一次
  const third = await collect(bm, { cached: second.state });
  assert.equal(third.state.items[key].d, true, '墓碑必须持续存在');
  assert.equal(third.state.items[key].m, deadM, '已是墓碑的不该被重新打时间戳');
  assert.equal(third.stats.deleted, 0, '不应重复计为一次删除');
});

// ── base 的作用 ─────────────────────────────────────────────────────

test('★ base 记录每个已存在的 key 的旧 m', async () => {
  // base 是服务端区分「我改了」与「对面改了」的唯一依据。
  // 缺了它，冲突日志会全是噪声。
  const bm = new MockBookmarks();
  bm.addBookmark(ROOT_TOOLBAR, '一', 'https://one.example');
  bm.addBookmark(ROOT_TOOLBAR, '二', 'https://two.example');

  const first = await collect(bm);
  const second = await collect(bm, { cached: first.state });

  assert.equal(Object.keys(second.base).length, 2, '两个已存在的项都应进 base');
  for (const [key, m] of Object.entries(second.base)) {
    assert.equal(m, first.state.items[key].m, `base[${key}] 应是上次见到的 m`);
  }
});

test('★ 只改了标题的项：base 仍记旧 m，而 state 记新 m', async () => {
  const bm = new MockBookmarks();
  bm.addBookmark(ROOT_TOOLBAR, '旧', 'https://x.example');
  const first = await collect(bm);
  const key = Object.keys(first.state.items)[0];

  [...bm.nodes.values()].find((n) => n.url === 'https://x.example').title = '新';
  const second = await collect(bm, { cached: first.state });

  assert.equal(second.base[key], first.state.items[key].m, 'base 是旧 m');
  assert.ok(compare(second.state.items[key].m, second.base[key]) > 0, 'state 是新 m');
});

// ── contentChanged ──────────────────────────────────────────────────

// contentChanged 的两个参数来自**不同的数据结构**，字段名也不同：
//   prev —— state 里的 item，字段是 p / t / n / u
//   node —— 扫描结果里的节点，字段是 parentKey / type / title / url
// 写测试时把两边写成同一套字段名会全部对不上，得到"总是有变化"的假象。
const PREV = { p: 'parent', t: 'b', n: '标题', u: 'https://example.com' };
const NODE = { parentKey: 'parent', type: 'b', title: '标题', url: 'https://example.com' };

test('contentChanged：内容相同则 false', () => {
  assert.equal(contentChanged(PREV, NODE), false);
});

test('contentChanged：忽略 m / a / d / x', () => {
  // 时间戳不是内容。两端各自把同一个书签重新保存一次，内容完全一样，
  // 不应被当成冲突。
  const withTimestamps = { ...PREV, m: '1700000000000-00009', a: '1700000000000-00000', d: true, x: 999 };
  assert.equal(contentChanged(withTimestamps, NODE), false,
    '时间戳与墓碑标记不应影响内容比较');
});

test('contentChanged：能识别四种内容差异', () => {
  assert.equal(contentChanged(PREV, { ...NODE, parentKey: '别的目录' }), true, '父目录变了');
  assert.equal(contentChanged(PREV, { ...NODE, type: 'f' }), true, '类型变了');
  assert.equal(contentChanged(PREV, { ...NODE, title: '新标题' }), true, '标题变了');
  assert.equal(contentChanged(PREV, { ...NODE, url: 'https://other.example' }), true, 'URL 变了');
});

test('contentChanged：缺失字段按空串处理，不产生假差异', () => {
  // 文件夹没有 u；state 里可能是 undefined 也可能是 ''
  const folderPrev = { p: 'p', t: 'f', n: '目录' };
  const folderNode = { parentKey: 'p', type: 'f', title: '目录', url: '' };
  assert.equal(contentChanged(folderPrev, folderNode), false);
  assert.equal(contentChanged({ ...folderPrev, u: '' }, folderNode), false);
});

// ── 健壮性 ──────────────────────────────────────────────────────────

test('getTree 返回异常结构时报明确错误', async () => {
  await assert.rejects(
    () => scan({ getTree: async () => [{}] }, {}),
    /意外的结构/,
  );
});

test('空的 items 缓存（首次安装）不报错', async () => {
  const bm = new MockBookmarks();
  bm.addBookmark(ROOT_TOOLBAR, 'x', 'https://x.example');
  const r = await collect(bm, { cached: { v: SCHEMA_VERSION } }); // 没有 items 字段
  assert.equal(Object.keys(r.state.items).length, 1);
});

test('缓存里有多余的 key（服务端有的本地没有）→ 写成墓碑', async () => {
  // 例如换了新 profile，但缓存还在
  const bm = new MockBookmarks();
  const r = await collect(bm, {
    cached: {
      v: SCHEMA_VERSION,
      items: {
        aaaaaaaabbbbbbbbccccccccdddddddd: {
          p: ROOT_TOOLBAR, t: 'b', n: '幽灵', u: 'https://ghost.example',
          a: '1700000000000-00000', m: '1700000000000-00000',
        },
      },
    },
  });
  const ghost = r.state.items.aaaaaaaabbbbbbbbccccccccdddddddd;
  assert.equal(ghost.d, true, '本地没有的项应写墓碑');
  assert.equal(r.stats.deleted, 1);
});

test('扫描两次结果稳定（同样的树 → 同样的 key）', async () => {
  const bm = new MockBookmarks();
  const f = bm.addFolder(ROOT_TOOLBAR, '稳定目录');
  bm.addBookmark(f, 'a', 'https://a.example');
  bm.addBookmark(f, 'b', 'https://b.example');

  const first = await collect(bm);
  const second = await collect(bm, { cached: first.state });

  assert.deepEqual(
    Object.keys(first.state.items).sort(),
    Object.keys(second.state.items).sort(),
    '两次扫描应产出同一组 key',
  );
  assert.equal(second.stats.unchanged, 3, '第二次不应有任何变更');
});

test('key 派生与 keys.js 一致（同一 URL 必得同一 key）', async () => {
  const bm = new MockBookmarks();
  bm.addBookmark(ROOT_TOOLBAR, '验证', 'https://verify.example');

  const { scanResult } = await collect(bm);
  const [ffId, key] = [...scanResult.ffIdToKey].find(
    ([, k]) => scanResult.nodes.get(k).url === 'https://verify.example',
  );
  const expected = await deriveKey('b', { url: 'https://verify.example' });
  assert.equal(key, expected, 'collect 用的 key 派生必须与 keys.js 相同');
  assert.ok(ffId);
});
