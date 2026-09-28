// extension/lib/keys.test.js —— 身份标识的派生
//
// 这里的规则决定了"两台设备上的这两个书签是不是同一个东西"。
// 改错了不会报错、不会崩，只会让合并静默地把同一个书签当成两个，
// 或者把两个不同的书签当成同一个而丢掉一个。

import test from 'node:test';
import assert from 'node:assert/strict';

import {
  deriveKey,
  deriveKeys,
  buildMaterial,
  isRootFolder,
  isRootEnabled,
  isValidKey,
  resolveRoots,
  ROOT_TOOLBAR,
  ROOT_MENU,
  ROOT_UNFILED,
  ROOT_MOBILE,
  TYPE_BOOKMARK,
  TYPE_FOLDER,
  KEY_LENGTH,
} from './keys.js';

// ── 编码格式 ────────────────────────────────────────────────────────

test('key 恒为 32 位小写十六进制', async () => {
  const k = await deriveKey(TYPE_BOOKMARK, { url: 'https://example.com' });
  assert.equal(k.length, KEY_LENGTH);
  assert.match(k, /^[0-9a-f]{32}$/);
  assert.equal(isValidKey(k), true);
});

test('分隔符是 NUL，保证拼接无歧义', () => {
  // 选 NUL 而不是 | 或 :，是因为 URL 和文件夹名里理论上可以出现后两者。
  const m = buildMaterial(TYPE_FOLDER, { parentKey: 'abc', title: 'def' });
  assert.equal(m, `f:abc${String.fromCharCode(0)}def`);
  assert.ok(m.includes(String.fromCharCode(0)));
});

// ── 稳定性与区分性 ──────────────────────────────────────────────────

test('同一输入两次计算结果相同', async () => {
  const a = await deriveKey(TYPE_BOOKMARK, { url: 'https://example.com/a' });
  const b = await deriveKey(TYPE_BOOKMARK, { url: 'https://example.com/a' });
  assert.equal(a, b);
});

test('不同 URL 得到不同 key', async () => {
  const a = await deriveKey(TYPE_BOOKMARK, { url: 'https://example.com/a' });
  const b = await deriveKey(TYPE_BOOKMARK, { url: 'https://example.com/b' });
  assert.notEqual(a, b);
});

test('URL 的大小写/尾斜杠差异会产生不同 key', async () => {
  // 这是刻意的：https://x.com 与 https://x.com/ 在浏览器里通常是同一个
  // 页面，但作为身份我们认为它们是两条记录。合并后会出现两条。
  const a = await deriveKey(TYPE_BOOKMARK, { url: 'https://example.com' });
  const b = await deriveKey(TYPE_BOOKMARK, { url: 'https://example.com/' });
  const c = await deriveKey(TYPE_BOOKMARK, { url: 'https://Example.com' });
  assert.notEqual(a, b);
  assert.notEqual(a, c);
});

// 演示为什么分隔符必须是 NUL。
//
// 反例：若用 | 做分隔符，文件夹 (parent="a|b", title="c") 与
// (parent="a", title="b|c") 拼出的哈希输入**完全相同** → 同一个 key →
// 合并时两个不同的文件夹被认成一个，其中一个的内容被另一个覆盖，静默丢失。
//
// NUL 在 UTF-8 文本中不会出现，所以这个歧义不成立。
test('★ NUL 分隔符避免了 | 会产生的碰撞', async () => {
  // 换成 | 就会撞车的这一对
  const withPipe1 = await deriveKey(TYPE_FOLDER, { parentKey: 'a|b', title: 'c' });
  const withPipe2 = await deriveKey(TYPE_FOLDER, { parentKey: 'a', title: 'b|c' });
  // 注意：这两个是**正确的** key（我们用 NUL 不是 |），它们必须不同
  assert.notEqual(withPipe1, withPipe2, '父目录/标题里的 | 不应造成身份混淆');

  // 直接验证分隔符方案：把同样的输入喂给 "用 | 拼接" 的对照实现
  const pipeMaterial1 = 'f:a|b' + '|' + 'c';
  const pipeMaterial2 = 'f:a' + '|' + 'b|c';
  assert.equal(pipeMaterial1, pipeMaterial2, '若换成 | 就会真的撞车 —— 这正是要避免的');

  // 而 NUL 方案下，同样的两种输入产生不同的待哈希字符串
  assert.notEqual(
    buildMaterial(TYPE_FOLDER, { parentKey: 'a|b', title: 'c' }),
    buildMaterial(TYPE_FOLDER, { parentKey: 'a', title: 'b|c' }),
  );
});

// ── ★ 书签的 key 不含父目录 ────────────────────────────────────────
//
// 这是整个设计里最关键的一条约束。它保证了"把书签拖到另一个文件夹"
// 能被正确同步为移动，而不是"删旧增新"。
// 反过来说，如果哪��天有人把 parentKey 加进书签的哈希输入，
// 重命名父目录就会导致整棵子树的 key 全变、产生成千上万条墓碑。

test('★ 书签的 key 不依赖父目录', async () => {
  const a = await deriveKey(TYPE_BOOKMARK, { url: 'https://example.com/x' });
  const b = await deriveKey(TYPE_BOOKMARK, {
    url: 'https://example.com/x',
    parentKey: '完全不同的父目录',
  });
  assert.equal(a, b, '书签 key 若依赖父目录，移动书签就会变成删旧增新');
});

test('★ 文件夹的 key 依赖父目录', async () => {
  const a = await deriveKey(TYPE_FOLDER, { parentKey: 'p1', title: '工作' });
  const b = await deriveKey(TYPE_FOLDER, { parentKey: 'p2', title: '工作' });
  assert.notEqual(a, b, '同名文件夹在不同父目录下必须是不同身份');
});

test('★ 同名同级文件夹才是同一个身份', async () => {
  const a = await deriveKey(TYPE_FOLDER, { parentKey: 'p', title: '工作' });
  const b = await deriveKey(TYPE_FOLDER, { parentKey: 'p', title: '工作' });
  assert.equal(a, b);
});

test('书签与文件夹不会撞 key', async () => {
  const a = await deriveKey(TYPE_BOOKMARK, { url: 'u:同样的内容' });
  const b = await deriveKey(TYPE_FOLDER, { parentKey: '', title: '同样的内容' });
  assert.notEqual(a, b);
});

// ── 批量派生 ────────────────────────────────────────────────────────

test('deriveKeys 与逐个 deriveKey 结果一致，且保持顺序', async () => {
  const inputs = [
    { type: TYPE_BOOKMARK, url: 'https://a.example' },
    { type: TYPE_FOLDER, parentKey: 'root', title: '目录' },
    { type: TYPE_BOOKMARK, url: 'https://b.example' },
  ];
  const batched = await deriveKeys(inputs);
  assert.equal(batched.length, inputs.length);
  for (let i = 0; i < inputs.length; i++) {
    const one = await deriveKey(inputs[i].type, inputs[i]);
    assert.equal(batched[i], one, `第 ${i} 项不一致`);
  }
});

test('deriveKeys 处理空数组', async () => {
  assert.deepEqual(await deriveKeys([]), []);
});

// ── 根目录常量 ──────────────────────────────────────────────────────

test('isRootFolder 识别四个系统根目录', () => {
  for (const id of [ROOT_TOOLBAR, ROOT_MENU, ROOT_UNFILED, ROOT_MOBILE]) {
    assert.equal(isRootFolder(id), true, `${id} 应被识别为根目录`);
  }
  assert.equal(isRootFolder('某个普通 key'), false);
  assert.equal(isRootFolder(''), false);
});

// 这四个 id 必须与 bmsync/state.go 完全一致，否则两端的父级引用对不上。
// check-extension.sh 也会查一遍，但这里给出失败时的具体信息。
test('★ 根目录 id 与 Firefox Places 的固定值一致', () => {
  assert.equal(ROOT_TOOLBAR, 'toolbar_____');
  assert.equal(ROOT_MENU, 'menu________');
  assert.equal(ROOT_UNFILED, 'unfiled_____');
  assert.equal(ROOT_MOBILE, 'mobile______');
  for (const id of [ROOT_TOOLBAR, ROOT_MENU, ROOT_UNFILED, ROOT_MOBILE]) {
    assert.equal(id.length, 12, `${id} 长度应为 12`);
  }
});

test('isRootEnabled 默认排除 mobile', () => {
  const on = new Set([ROOT_TOOLBAR, ROOT_MENU, ROOT_UNFILED]);
  assert.equal(isRootEnabled(ROOT_TOOLBAR, on), true);
  assert.equal(isRootEnabled(ROOT_UNFILED, on), true);
  assert.equal(isRootEnabled(ROOT_MOBILE, on), false, '移动设备书签默认排除');
  assert.equal(isRootEnabled(ROOT_MOBILE, new Set([ROOT_MOBILE])), false,
    '即使显式列出，mobile 仍需由 includeMobile 单独控制');
  assert.equal(isRootEnabled('未知 id', on), false);
});

test('isRootEnabled 接受数组', () => {
  assert.equal(isRootEnabled(ROOT_TOOLBAR, [ROOT_TOOLBAR]), true);
  assert.equal(isRootEnabled(ROOT_TOOLBAR, []), false);
});

// ── 根目录解析 ──────────────────────────────────────────────────────

test('resolveRoots 正常识别四个 id', () => {
  const children = [
    { id: ROOT_TOOLBAR, title: '书签栏' },
    { id: ROOT_MENU, title: '菜单' },
    { id: ROOT_UNFILED, title: '其他书签' },
    { id: ROOT_MOBILE, title: '移动设备书签' },
  ];
  const r = resolveRoots(children);
  assert.equal(r.warned, false);
  assert.deepEqual(r.roots, [ROOT_TOOLBAR, ROOT_MENU, ROOT_UNFILED, ROOT_MOBILE]);
});

test('★ resolveRoots 不按标题匹配（界面语言会变）', () => {
  // 英文界面的 Firefox 标题是 Bookmarks Toolbar / Menu / Other Bookmarks。
  // 若按标题匹配就会全错 —— 所以只能按 id。
  const children = [
    { id: ROOT_TOOLBAR, title: 'Bookmarks Toolbar' },
    { id: ROOT_MENU, title: 'Menu' },
    { id: ROOT_UNFILED, title: 'Other Bookmarks' },
    { id: ROOT_MOBILE, title: 'Mobile Bookmarks' },
  ];
  const r = resolveRoots(children);
  assert.equal(r.warned, false, '换了界面语言就应该认不出来才对 —— 说明实现按标题匹配了');
  assert.deepEqual(r.roots, [ROOT_TOOLBAR, ROOT_MENU, ROOT_UNFILED, ROOT_MOBILE]);
});

test('resolveRoots 在 id 形态不符时按下标兜底并告警', () => {
  // 模拟 Firefox 改版导致 id 变化的极端情况。
  const children = [
    { id: 'xxx-toolbar', title: '书签栏' },
    { id: 'xxx-menu', title: '菜单' },
    { id: 'xxx-unfiled', title: '其他书签' },
    { id: 'xxx-mobile', title: '移动设备书签' },
  ];
  const r = resolveRoots(children);
  assert.equal(r.warned, true, 'id 认不出来时必须告警，而不是静默用错映射');
  assert.match(r.message, /根目录/);
  assert.equal(r.roots.length, 4);
});

test('resolveRoots 容忍缺失的子目录', () => {
  const r = resolveRoots([{ id: ROOT_TOOLBAR, title: '书签栏' }]);
  assert.equal(r.warned, true);
  assert.match(r.message, /根目录/);
});

test('resolveRoots 处理空输入', () => {
  const r = resolveRoots([]);
  assert.equal(r.warned, true);
  const r2 = resolveRoots(null);
  assert.equal(r2.warned, true);
});

// ── key 合法性 ──────────────────────────────────────────────────────

test('isValidKey 拒绝非法形式', () => {
  for (const k of ['', 'abc', 'z'.repeat(32), 'A'.repeat(32), '1'.repeat(31), '1'.repeat(33)]) {
    assert.equal(isValidKey(k), false, `不应接受 ${JSON.stringify(k)}`);
  }
  assert.equal(isValidKey('1'.repeat(32)), true);
  assert.equal(isValidKey('a'.repeat(32)), true);
});
