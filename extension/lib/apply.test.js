// extension/lib/apply.test.js —— 应用：同步 state → Firefox 书签树
//
// 这是整个流程里唯一会**真正改动用户数据**的环节。写错了的后果：
//
//   · 该删的没删 → 用户在公司电脑删掉的东西在这边复活
//   · 先建后删   → 本地已存在的项被判成"已存在"而跳过，删除传播不过来
//   · 父目录没建 → create 因 parentId 无效而失败，整棵子树丢失
//
// 所以这里的重点不是"能不能改树"，而是**三条顺序约束**（见 apply.js 头注释）。

import test from 'node:test';
import assert from 'node:assert/strict';

import { HLC, compare, encode } from './hlc.js';
import { deriveKey, ROOT_TOOLBAR, ROOT_MENU, ROOT_UNFILED, TYPE_FOLDER, TYPE_BOOKMARK } from './keys.js';
import { scan, buildState, SCHEMA_VERSION } from './collect.js';
import { planApply, executePlan } from './apply.js';
import { MockBookmarks } from './mock-bookmarks.js';

let clockMs = 1_700_000_000_000;
const clock = new HLC(() => clockMs);

/** 扫一棵 mock 树，返回 scan 的结果（apply 需要 ffId 映射）。 */
async function live(bm) {
  return scan(bm, { enabledRoots: new Set([ROOT_TOOLBAR, ROOT_MENU, ROOT_UNFILED]) });
}

/** 构造一条 item */
function item(type, parentKey, title, url, ms = 1000, extra = {}) {
  const base = { p: parentKey, t: type, n: title, a: encode(ms, 0), m: encode(ms, 1) };
  return type === TYPE_BOOKMARK ? { ...base, u: url } : { ...base, ...extra };
}

function stateOf(items) {
  return { v: SCHEMA_VERSION, items: Object.fromEntries(Object.entries(items)) };
}

/** 跑完整的「规划 + 执行」。 */
async function apply(bm, incomingState) {
  const s = await live(bm);
  const plan = planApply(s.nodes, incomingState, s.ffKeyToId);
  const result = await executePlan(bm, plan);
  return { plan, result, after: await live(bm) };
}

// deriveKey 是**异步**的（走 crypto.subtle.digest），必须 await。
//
// 之前这里写成 `return deriveKey(...)` —— 于是返回的是 Promise 而不是 key 字符串。
// 症状非常隐蔽：Promise 被当作对象键使用（"[object Promise]"），
// 所有 key 都"对得上"（因为都错了同样多），于是"父目录变了 → move"
// 这类断言报 result.moved === 0，看起来像 apply 的 move 逻辑坏了，
// 实际是测试传了个垃圾 key。
const keyOf = (type, parts) => deriveKey(type, parts);

// ── 计划：什么都不该做时就不发任何调用 ────────────────────────────

test('★ 状态与本地一致时不产生任何写操作', async () => {
  const bm = new MockBookmarks();
  const f = bm.addFolder(ROOT_TOOLBAR, '目录');
  bm.addBookmark(f, '书签', 'https://x.example');

  // 先采集一次，把结果当作"服务端返回的状态"
  const s = await live(bm);
  const asState = {
    v: SCHEMA_VERSION,
    items: Object.fromEntries(
      [...s.nodes].map(([key, n]) => [key, item(n.type, n.parentKey, n.title, n.url)]),
    ),
  };

  const plan = planApply(s.nodes, asState, s.ffKeyToId);
  assert.equal(plan.counts.create, 0);
  assert.equal(plan.counts.update, 0);
  assert.equal(plan.counts.remove, 0);
  assert.equal(plan.skipped.length, 0);

  bm.clearCalls();
  const res = await executePlan(bm, plan);
  assert.equal(bm.ops().length, 0, `不该有任何 API 调用，实际: ${bm.ops()}`);
  assert.deepEqual(res, { created: 0, updated: 0, moved: 0, deleted: 0, failed: [] });
});

// ── 创建 ────────────────────────────────────────────────────────────

test('把云端书签建到本地', async () => {
  const bm = new MockBookmarks();
  const bk = await keyOf(TYPE_BOOKMARK, { url: 'https://new.example' });

  const { result, after } = await apply(bm, stateOf({
    [bk]: item(TYPE_BOOKMARK, ROOT_TOOLBAR, '云端来的', 'https://new.example'),
  }));

  assert.equal(result.created, 1);
  assert.equal(result.failed.length, 0);
  assert.equal(after.nodes.size, 1);
  const created = [...bm.nodes.values()].find((n) => n.url === 'https://new.example');
  assert.ok(created, '应创建出这条书签');
  assert.equal(created.parentId, ROOT_TOOLBAR, '顶层项应挂到根目录');
});

test('★ 父文件夹与其子项：父先建、子后建', async () => {
  // 这是最关键的一条顺序约束。create 的 parentId 必须是已存在的 id，
  // 否则整棵子树会失败。
  const bm = new MockBookmarks();
  const fk = await keyOf(TYPE_FOLDER, { parentKey: ROOT_TOOLBAR, title: '目录' });
  const c1k = await keyOf(TYPE_BOOKMARK, { url: 'https://one.example' });
  const c2k = await keyOf(TYPE_BOOKMARK, { url: 'https://two.example' });

  const { result, after } = await apply(bm, stateOf({
    [c1k]: item(TYPE_BOOKMARK, fk, '一', 'https://one.example'),
    [fk]: item(TYPE_FOLDER, ROOT_TOOLBAR, '目录'),
    [c2k]: item(TYPE_BOOKMARK, fk, '二', 'https://two.example'),
  }));

  assert.equal(result.failed.length, 0, `不应有失败: ${JSON.stringify(result.failed)}`);
  assert.equal(result.created, 3);

  // 执行顺序：父目录的 create 必须排在子项之前
  const creates = bm.calls.filter((c) => c.op === 'create');
  const folderIdx = creates.findIndex((c) => c.details.title === '目录');
  assert.ok(folderIdx >= 0, '应创建了目录');
  for (const [i, c] of creates.entries()) {
    if (c.details.title !== '目录') {
      assert.ok(i > folderIdx,
        `${c.details.title} 的 create 出现在目录之前（第 ${i} vs ${folderIdx}）`);
    }
  }
  assert.equal(after.nodes.size, 3);
});

test('三级嵌套也能正确建立', async () => {
  const bm = new MockBookmarks();
  const l1 = await keyOf(TYPE_FOLDER, { parentKey: ROOT_TOOLBAR, title: 'L1' });
  const l2 = await keyOf(TYPE_FOLDER, { parentKey: l1, title: 'L2' });
  const leaf = await keyOf(TYPE_BOOKMARK, { url: 'https://leaf.example' });

  const { result, after } = await apply(bm, stateOf({
    [leaf]: item(TYPE_BOOKMARK, l2, '叶子', 'https://leaf.example'),
    [l2]: item(TYPE_FOLDER, l1, 'L2'),
    [l1]: item(TYPE_FOLDER, ROOT_TOOLBAR, 'L1'),
  }));

  assert.equal(result.failed.length, 0, JSON.stringify(result.failed));
  assert.equal(result.created, 3);
  assert.equal(after.nodes.size, 3);

  // 验证层级真的建对了：从叶子往上追溯，每级 parentId 都对得上。
  // （不能用 bm.nodes 里的 depth —— 那是 scan() 产出的，mock 节点没有）
  const leafNode = [...bm.nodes.values()].find((n) => n.url === 'https://leaf.example');
  const l2Node = bm.nodes.get(leafNode.parentId);
  const l1Node = bm.nodes.get(l2Node.parentId);
  assert.equal(l2Node.title, 'L2', '叶子的父级应是 L2');
  assert.equal(l1Node.title, 'L1', 'L2 的父级应是 L1');
  assert.equal(l1Node.parentId, ROOT_TOOLBAR, 'L1 挂在根目录下');
});

// ── 更新 ────────────────────────────────────────────────────────────

test('标题变了 → update', async () => {
  const bm = new MockBookmarks();
  bm.addBookmark(ROOT_TOOLBAR, '旧标题', 'https://x.example');
  const bk = await keyOf(TYPE_BOOKMARK, { url: 'https://x.example' });

  const { result } = await apply(bm, stateOf({
    [bk]: item(TYPE_BOOKMARK, ROOT_TOOLBAR, '新标题', 'https://x.example', 2000),
  }));

  assert.equal(result.updated, 1);
  assert.equal(result.created, 0);
  const node = [...bm.nodes.values()].find((n) => n.url === 'https://x.example');
  assert.equal(node.title, '新标题');
});

test('URL 变了 → 云端只声明新 URL，旧 URL 因不在状态里而被删除', async () => {
  const bm = new MockBookmarks();
  bm.addBookmark(ROOT_TOOLBAR, '标题', 'https://old.example');
  const newKey = await keyOf(TYPE_BOOKMARK, { url: 'https://new.example' });

  const { result } = await apply(bm, stateOf({
    [newKey]: item(TYPE_BOOKMARK, ROOT_TOOLBAR, '标题', 'https://new.example'),
  }));

  assert.equal(result.created, 1, '新 URL 是新身份');
  assert.equal(result.deleted, 1, '旧 URL 不在云端状态里 → 本地那条应被删');
  const urls = [...bm.nodes.values()].map((n) => n.url).filter(Boolean);
  assert.deepEqual(urls, ['https://new.example'],
    '本地应只剩云端声明的那条');
});

test('★ 用户本地有、云端完全没提的书签会被删除', async () => {
  // 这是"云端是权威状态"的直接后果。听起来激进，但它正是
  // "在公司电脑删掉的东西，这边也会消失"所需要的语义。
  const bm = new MockBookmarks();
  bm.addBookmark(ROOT_TOOLBAR, '只在本地有', 'https://local-only.example');

  const { result } = await apply(bm, stateOf({}));
  assert.equal(result.deleted, 1);
  assert.equal([...bm.nodes.values()].some((n) => n.url), false);
});

// ── 移动 ────────────────────────────────────────────────────────────

test('父目录变了 → move（key 不变）', async () => {
  const bm = new MockBookmarks();
  const f1 = bm.addFolder(ROOT_TOOLBAR, '目录一');
  const f2 = bm.addFolder(ROOT_MENU, '目录二');
  bm.addBookmark(f1, '会动', 'https://move.example');

  const f1k = await keyOf(TYPE_FOLDER, { parentKey: ROOT_TOOLBAR, title: '目录一' });
  const f2k = await keyOf(TYPE_FOLDER, { parentKey: ROOT_MENU, title: '目录二' });
  const bk = await keyOf(TYPE_BOOKMARK, { url: 'https://move.example' });

  const { result } = await apply(bm, stateOf({
    [f1k]: item(TYPE_FOLDER, ROOT_TOOLBAR, '目录一'),
    [f2k]: item(TYPE_FOLDER, ROOT_MENU, '目录二'),
    [bk]: item(TYPE_BOOKMARK, f2k, '会动', 'https://move.example', 2000),
  }));

  assert.equal(result.moved, 1);
  assert.equal(result.created, 0, '移动不应被当成删除+新建');
  const node = [...bm.nodes.values()].find((n) => n.url === 'https://move.example');
  assert.equal(node.parentId, f2);
});

// ── ★ 删除 ──────────────────────────────────────────────────────────

test('★ 云端已删、本地还在 → 本地删除', async () => {
  const bm = new MockBookmarks();
  bm.addBookmark(ROOT_TOOLBAR, '要被删', 'https://gone.example');

  const { result, after } = await apply(bm, stateOf({}));

  assert.equal(result.deleted, 1);
  assert.equal(after.nodes.size, 0);
  assert.equal([...bm.nodes.values()].some((n) => n.url === 'https://gone.example'), false);
});

test('★ 删除文件夹只发一次 remove（利用 Firefox 的递归语义）', async () => {
  // Firefox 的 bookmarks.remove 对文件夹是递归的。若对子项也发一次 remove，
  // 那次调用会因为节点已被连带删除而报错。
  const bm = new MockBookmarks();
  const f = bm.addFolder(ROOT_TOOLBAR, '目录');
  const sub = bm.addFolder(f, '子目录');
  bm.addBookmark(f, '子书签', 'https://a.example');
  bm.addBookmark(sub, '孙书签', 'https://b.example');

  const { result, after } = await apply(bm, stateOf({}));

  assert.equal(result.deleted, 1, '整棵子树只需一次 remove');
  assert.equal(bm.countOp('remove'), 1, `实际发了几次: ${bm.countOp('remove')}`);
  assert.equal(after.nodes.size, 0);
  assert.equal(result.failed.length, 0, JSON.stringify(result.failed));
});

test('★ 先删后建：不能因为"本地已存在"而跳过应删的项', async () => {
  // 若顺序反了，本地被删的项会先被判成"存在"而被保留。
  const bm = new MockBookmarks();
  bm.addBookmark(ROOT_TOOLBAR, '云端已删', 'https://deleted.example');

  const bk = await keyOf(TYPE_BOOKMARK, { url: 'https://kept.example' });
  const { result, after } = await apply(bm, stateOf({
    [bk]: item(TYPE_BOOKMARK, ROOT_TOOLBAR, '云端还在的', 'https://kept.example'),
  }));

  assert.equal(result.deleted, 1);
  assert.equal(result.created, 1);
  const urls = [...after.nodes.values()].map((n) => n.url).filter(Boolean);
  assert.deepEqual(urls, ['https://kept.example'], '应只剩云端声明的那条');
});

// ── 类型变更 ────────────────────────────────────────────────────────

test('书签变文件夹 → 删了重建（update 不支持改类型）', async () => {
  const bm = new MockBookmarks();
  bm.addBookmark(ROOT_TOOLBAR, '曾是书签', 'https://x.example');
  const k = await keyOf(TYPE_FOLDER, { parentKey: ROOT_TOOLBAR, title: '曾是书签' });

  const { result } = await apply(bm, stateOf({
    [k]: item(TYPE_FOLDER, ROOT_TOOLBAR, '曾是书签', undefined, 2000),
  }));

  assert.equal(result.created, 1, '应重建');
  assert.equal(bm.countOp('remove'), 1, '旧的应被移除');
  assert.equal(result.failed.length, 0, JSON.stringify(result.failed));
});

// ── 健壮性 ──────────────────────────────────────────────────────────

test('★ 父目录不在云端状态里 → 记入 skipped，不丢到根目录', async () => {
  // 静默把书签挪到"其他书签"下面比报错更糟：用户不会发现，
  // 直到某天找不到它了。
  const bm = new MockBookmarks();
  const bk = await keyOf(TYPE_BOOKMARK, { url: 'https://orphan.example' });
  const ghostParent = await keyOf(TYPE_FOLDER, { parentKey: ROOT_TOOLBAR, title: '不存在的目录' });

  const { plan, result, after } = await apply(bm, stateOf({
    [bk]: item(TYPE_BOOKMARK, ghostParent, '孤儿', 'https://orphan.example'),
  }));

  assert.equal(plan.skipped.length, 1);
  assert.equal(plan.skipped[0].reason, 'parent-missing');
  assert.equal(result.created, 0, '不应被创建到别处');
  assert.equal(after.nodes.size, 0, '本地不应出现任何东西');
});

test('父链成环 → 不死循环，安全跳过', async () => {
  const bm = new MockBookmarks();
  const a = await keyOf(TYPE_FOLDER, { parentKey: 'x', title: 'A' });
  const b = await keyOf(TYPE_FOLDER, { parentKey: 'x', title: 'B' });

  const { plan } = await apply(bm, stateOf({
    [a]: item(TYPE_FOLDER, b, 'A'),
    [b]: item(TYPE_FOLDER, a, 'B'),
  }));
  assert.equal(plan.skipped.length, 2, '两个都应被跳过');
});

test('单个 create 失败不影响其他项', async () => {
  const bm = new MockBookmarks();
  const okKey = await keyOf(TYPE_BOOKMARK, { url: 'https://ok.example' });
  const badKey = await keyOf(TYPE_BOOKMARK, { url: 'https://bad.example' });

  // 只让第二个失败
  let n = 0;
  const orig = bm.create.bind(bm);
  bm.create = async (d) => {
    n += 1;
    if (n === 2) throw new Error('模拟失败');
    return orig(d);
  };

  const { result, after } = await apply(bm, stateOf({
    [okKey]: item(TYPE_BOOKMARK, ROOT_TOOLBAR, '好的', 'https://ok.example'),
    [badKey]: item(TYPE_BOOKMARK, ROOT_TOOLBAR, '坏的', 'https://bad.example'),
  }));

  assert.equal(result.failed.length, 1, '失败应被记录而不是抛出');
  assert.equal(result.created, 1, '另一个仍应成功');
  assert.equal(after.nodes.size, 1);
});

test('删除已被用户手动删掉的节点不算失败', async () => {
  const bm = new MockBookmarks();
  bm.addBookmark(ROOT_TOOLBAR, 'x', 'https://x.example');
  const s = await live(bm);

  // 云端状态里没有它 → 计划会删除
  const plan = planApply(s.nodes, stateOf({}), s.ffKeyToId);
  assert.equal(plan.counts.remove, 1);

  // 但在执行前它已经被用户删了
  const id = [...bm.nodes.values()].find((n) => n.url === 'https://x.example').id;
  await bm.remove(id);

  const res = await executePlan(bm, plan);
  assert.equal(res.failed.length, 0, '节点已不存在不该记为失败');
});

test('空的 incoming state → 删掉本地全部', async () => {
  const bm = new MockBookmarks();
  const f = bm.addFolder(ROOT_TOOLBAR, '目录');
  bm.addBookmark(f, '一', 'https://one.example');
  bm.addBookmark(ROOT_MENU, '二', 'https://two.example');

  const { result, after } = await apply(bm, { v: SCHEMA_VERSION, items: {} });
  assert.ok(result.deleted >= 1);
  assert.equal(after.nodes.size, 0);
});

test('incoming 缺 items 字段 → 当作空处理', async () => {
  const bm = new MockBookmarks();
  bm.addBookmark(ROOT_TOOLBAR, 'x', 'https://x.example');
  const { after } = await apply(bm, { v: SCHEMA_VERSION });
  assert.equal(after.nodes.size, 0);
});

test('墓碑不会落到本地书签树里', async () => {
  const bm = new MockBookmarks();
  const bk = await keyOf(TYPE_BOOKMARK, { url: 'https://x.example' });
  await apply(bm, stateOf({
    [bk]: { p: ROOT_TOOLBAR, t: 'b', n: '已删', u: 'https://x.example',
            a: encode(1, 0), m: encode(2, 0), d: true, x: 1700000000000 },
  }));
  assert.equal([...bm.nodes.values()].some((n) => n.url === 'https://x.example'), false,
    '墓碑只是"删除的事实"，不该在本地留下一条记录');
});

// ── 往返一致性 ──────────────────────────────────────────────────────

test('★ 往返：collect 出的 state 落到空树后，再 collect 应语义等价', async () => {
  // 这是采集与应用组合起来最强的一条性质。
  // 采集在 A 上跑 → 应用到空的 B → 在 B 上再采集 → 结果应与 A 等价。
  const source = new MockBookmarks();
  const a = source.addFolder(ROOT_TOOLBAR, '工作');
  const b = source.addFolder(a, '子目录');
  source.addBookmark(a, '一', 'https://one.example');
  source.addBookmark(b, '二', 'https://two.example');
  source.addBookmark(ROOT_UNFILED, '三', 'https://three.example');

  const s1 = await live(source);
  const asState = buildState(s1, { v: SCHEMA_VERSION, items: {} }, clock, clockMs);

  const target = new MockBookmarks();
  const { result, after } = await apply(target, asState.state);
  assert.equal(result.failed.length, 0, JSON.stringify(result.failed));

  // 在目标树上重新采集
  const round2 = buildState(after, { v: SCHEMA_VERSION, items: {} }, clock, clockMs);

  assert.equal(Object.keys(round2.state.items).length, Object.keys(asState.state.items).length,
    `项数应一致：源 ${Object.keys(asState.state.items).length} vs 目标 ${Object.keys(round2.state.items).length}`);

  for (const [key, orig] of Object.entries(asState.state.items)) {
    const back = round2.state.items[key];
    assert.ok(back, `往返后丢了 ${key}`);
    assert.equal(back.t, orig.t, `${key} 类型变了`);
    assert.equal(back.n, orig.n, `${key} 标题变了`);
    assert.equal(back.u, orig.u, `${key} URL 变了`);
    assert.equal(back.p, orig.p, `${key} 父级变了`);
  }
});

test('★ 往返：删除后再往返，墓碑应稳定', async () => {
  const source = new MockBookmarks();
  const f = source.addFolder(ROOT_TOOLBAR, '要删的');
  source.addBookmark(f, '子项', 'https://gone.example');
  source.addBookmark(ROOT_TOOLBAR, '保留', 'https://keep.example');

  // 第一次同步：两台设备都有
  const s1 = await live(source);
  const asState = buildState(s1, { v: SCHEMA_VERSION, items: {} }, clock, clockMs);
  assert.equal(Object.keys(asState.state.items).length, 3,
    '首次采集应产出 3 项（目录 + 子书签 + 保留），不含系统根目录本身');

  const deviceB = new MockBookmarks();
  await apply(deviceB, asState.state);
  assert.equal((await live(deviceB)).nodes.size, 3);

  // 在 A 上删掉整个目录，采集
  await source.remove(f);
  clockMs += 1000;
  const s2 = await live(source);
  const afterDelete = buildState(s2, asState.state, clock, clockMs);
  // 树里原有 3 项（目录 + 子书签 + 保留），删掉目录连同子书签 = 2 个墓碑
  assert.equal(afterDelete.stats.deleted, 2, '目录 + 子书签，两个墓碑');
  assert.equal(afterDelete.stats.unchanged, 1, '"保留"那条未变，不该被打时间戳');

  // 同步到 B：应该被删掉
  const { result, after: bAfter } = await apply(deviceB, afterDelete.state);
  assert.ok(result.deleted >= 1, 'B 上应执行删除');
  const remaining = [...bAfter.nodes.values()].filter((n) => n.url);
  assert.equal(remaining.length, 1, '只剩"保留"那条');
  assert.equal(remaining[0].url, 'https://keep.example');
});
