// extension/lib/hlc.test.js —— HLC 跨端一致性验证 ★
//
// 这是整个项目里最重要的一个测试文件。
//
// 为什么：合并用 HLC 时间戳决胜，而 HLC 的 `update` 有四条分支，顺序错了
// 不会报错、不会崩，只会在"物理时钟恰好追平远端毫秒"时产生**小于**已收
// 时间戳的新值 —— 两端于是交替获胜，书签随机丢失，且用户完全无感。
//
// 那种 bug 光靠"看代码对不对"抓不到，因为四条分支单看每条都合理。
// 必须有一组**固定输入序列 + 期望输出**，两端各自跑一遍。
//
// 向量来源：bmsync/hlc_test.go 的 TestHLCExportVectors
// 重新生成：cd bmsync && go test -run TestHLCExportVectors -v ./...
//
// 运行：node --test lib/hlc.test.js

import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

import { HLC, encode, decode, compare, isValidHLC, HLC_ZERO } from './hlc.js';

const here = dirname(fileURLToPath(import.meta.url));
const VECTORS = join(here, '..', '..', 'test', 'hlc_vectors.json');

// ── 编码与比较（本地性质，不依赖 Go 端）────────────────────────────

test('encode 输出定宽 19 字符', () => {
  const cases = [
    [0, 0, '0000000000000-00000'],
    [1000, 0, '0000000001000-00000'],
    [1000, 42, '0000000001000-00042'],
    [1790000000000, 99999, '1790000000000-99999'],
    [9223372036854, 1, '9223372036854-00001'],
  ];
  for (const [l, c, want] of cases) {
    assert.equal(encode(l, c), want, `encode(${l}, ${c})`);
  }
});

test('encode/decode 往返一致', () => {
  for (let i = 0; i < 500; i++) {
    const l = Math.floor(Math.random() * 9_000_000_000_000);
    const c = Math.floor(Math.random() * 100_000);
    const s = encode(l, c);
    assert.equal(isValidHLC(s), true);
    const d = decode(s);
    assert.equal(d.l, l);
    assert.equal(d.c, c);
  }
});

test('decode 拒绝畸形输入', () => {
  for (const s of [
    '', '123', '0000000001000-0000', '0000000001000000000',
    '0000000001000_00000', '0000000001000-0000x', 'abc-00000',
    '0000000001000-999999', '-0000000001000-00000',
  ]) {
    assert.equal(decode(s), null, `不应接受 ${JSON.stringify(s)}`);
    assert.equal(isValidHLC(s), false);
  }
});

test('compare：同毫秒按计数比，跨毫秒物理优先', () => {
  assert.equal(compare(encode(1000, 5), encode(1000, 6)), -1);
  assert.equal(compare(encode(1000, 6), encode(1000, 5)), 1);
  assert.equal(compare(encode(1000, 5), encode(1000, 5)), 0);
  assert.equal(compare(encode(1001, 0), encode(1000, 99999)), 1);
});

test('compare：非法值小于一切合法值（污染的一侧永远输）', () => {
  assert.equal(compare('garbage', encode(1000, 0)), -1);
  assert.equal(compare(encode(1000, 0), 'garbage'), 1);
  assert.equal(compare('a', 'b'), 'a'.localeCompare('b') === 0 ? 0 : -1);
});

test('字典序 == 时间序（整个编码格式存在的理由）', () => {
  const xs = [];
  for (let i = 0; i < 800; i++) {
    // 大量样本落在同一毫秒，逼出"物理相同、只比计数"的路径
    xs.push(encode(1_000_000_000_000 + Math.floor(Math.random() * 5),
                   Math.floor(Math.random() * 100_000)));
  }
  const sorted = [...xs].sort();
  for (let i = 1; i < sorted.length; i++) {
    assert.ok(compare(sorted[i - 1], sorted[i]) <= 0,
      `${sorted[i - 1]} 应 <= ${sorted[i]}`);
  }
});

// ── 本地行为 ────────────────────────────────────────────────────────

test('now() 严格单调', () => {
  const h = new HLC(() => 1_000_000_000_000);
  let prev = h.now();
  for (let i = 0; i < 500; i++) {
    const cur = h.now();
    assert.ok(compare(cur, prev) > 0, `第 ${i} 次未递增: ${prev} → ${cur}`);
    prev = cur;
  }
});

test('物理时钟推进后计数归零', () => {
  let t = 1000;
  const h = new HLC(() => t);
  assert.equal(h.now(), '0000000001000-00000');
  assert.equal(h.now(), '0000000001000-00001');
  t = 2000;
  assert.equal(h.now(), '0000000002000-00000');
});

test('因果性：A 的事件严格小于 B 观察它之后产生的事件', () => {
  const a = new HLC(() => 1_000_000_000_000);
  const b = new HLC(() => 1_000_000_000_000);
  let last = a.now();
  for (let i = 0; i < 5; i++) last = a.now();
  for (let i = 0; i < 5; i++) {
    b.update(last);
    const got = b.now();
    assert.ok(compare(got, last) > 0, `B 的后续事件 ${got} 未大于 ${last}`);
  }
});

// 这个是当初写错的那条分支的回归测试。
//
// A 的事件毫秒领先，B 的本地时钟落后 —— 此时 max == 远端毫秒。
// 错误的写法（"物理时钟领先则计数归零"）会让 B 得到 (max, 0)，
// 其后 now() 产生的 (max, 1) 小于已收到的 (max, rc) —— 因果性被破坏。
test('★ 远端毫秒领先时必须接在远端计数之后', () => {
  const a = new HLC(() => 1500);
  const b = new HLC(() => 1000);

  // A 在 1500ms 内产生 6 个事件 → 最后一个是 (1500, 00005)
  let remote;
  for (let i = 0; i < 6; i++) remote = a.now();
  assert.equal(remote, '0000000001500-00005');

  b.update(remote);
  const got = b.now();

  assert.ok(compare(got, remote) > 0,
    `因果性被破坏：${got} 未大于远端的 ${remote}`);
  // update 本身消耗了 rc+1，所以 now() 拿到 rc+2。
  assert.equal(got, '0000000001500-00007');
});

test('★ 远端与本地同一毫秒时同样必须接在远端计数之后', () => {
  // 另一个反例：max 同时等于本地与远端，走的是第一条分支
  const a = new HLC(() => 1000);
  const b = new HLC(() => 1000);
  let remote;
  for (let i = 0; i < 6; i++) remote = a.now();
  assert.equal(remote, '0000000001000-00005');

  b.update(remote);
  const got = b.now();
  assert.ok(compare(got, remote) > 0, `因果性被破坏：${got} 未大于 ${remote}`);
});

test('时钟回拨后逻辑时间继续前进', () => {
  let t = 2_000_000_000_000;
  const h = new HLC(() => t);
  const first = h.now();

  t = 2_000_000_000_000 - 3_600_000; // 用户把时钟调回 1 小时前
  let prev = first;
  for (let i = 0; i < 100; i++) {
    const cur = h.now();
    assert.ok(compare(cur, prev) > 0, `回拨后第 ${i} 次未递增`);
    prev = cur;
  }
  assert.ok(compare(h.current(), first) >= 0);
});

test('计数溢出进位到下一毫秒，且长度不变', () => {
  // 直接把私有状态推到边界。用对象内部字段访问模拟。
  const h = new HLC(() => 1000);
  // 连续调用直到进位：100000 次太多，这里用 observeMany 把计数顶上去
  h.observeMany([encode(1000, 99_999)]);
  const a = h.now();
  const b = h.now();
  assert.ok(compare(b, a) > 0);
  assert.equal(a.length, 19);
  assert.equal(b.length, 19);
});

test('update 收到非法远端不崩，且仍产出合法时间戳', () => {
  const h = new HLC(() => 1000);
  const first = h.now();
  const got = h.update('完全不是 HLC');
  assert.equal(isValidHLC(got), true);
  assert.ok(compare(got, first) > 0);
});

test('observeMany 追上所有远端时间戳的最大值', () => {
  const h = new HLC(() => 500);
  const remote = encode(9000, 12);
  const got = h.observeMany([encode(100, 0), remote, 'garbage']);
  assert.ok(compare(got, remote) >= 0);
  assert.ok(compare(h.now(), remote) > 0);
});

test('HLC_ZERO 是最小值', () => {
  assert.equal(HLC_ZERO, '0000000000000-00000');
  assert.equal(compare(HLC_ZERO, encode(0, 1)), -1);
});

// ── ★ 跨端向量验证 ────────────────────────────────────────────────

test('★ Go 与 JS 的 HLC 实现逐条一致（test/hlc_vectors.json）', () => {
  const raw = JSON.parse(readFileSync(VECTORS, 'utf-8'));
  const vectors = raw.vectors;

  assert.ok(Array.isArray(vectors) && vectors.length >= 15,
    `向量数量 ${vectors?.length} 偏少，跨端验证覆盖不足 —— 重新生成：` +
    'cd bmsync && go test -run TestHLCExportVectors');

  // 每一步都把物理时钟设成向量指定的 at，再执行该步操作。
  //
  // ⚠️ 必须是 `() => t` 而不是 `() => 0`：HLC 的 now() 拿物理时钟和已知的
  // 逻辑时间取最大值，喂 0 的话会走"逻辑时间领先"分支，
  // 而向量记录的是"物理时钟"分支的结果 —— 症状是第一条就不匹配，
  // 看起来像两端实现有偏差，其实是这个箭头函数写错了。
  let t = 0;
  const h = new HLC(() => t);

  for (const [i, v] of vectors.entries()) {
    t = v.at;
    const got = v.op === 'update' ? h.update(v.remote) : h.now();
    assert.equal(got, v.out,
      `第 ${i + 1} 条（op=${v.op} remote=${v.remote} at=${v.at}）不一致：\n` +
      `  Go: ${v.out}\n  JS: ${got}\n` +
      `  两端实现出现偏差，合并在"时间戳恰好相等"时会非确定。`);
  }
});

test('★ 向量序列本身严格单调', () => {
  const { vectors } = JSON.parse(readFileSync(VECTORS, 'utf-8'));
  for (let i = 1; i < vectors.length; i++) {
    assert.ok(compare(vectors[i - 1].out, vectors[i].out) < 0,
      `向量 ${i}（${vectors[i].out}）未严格大于前一步（${vectors[i - 1].out}）`);
  }
});

test('★ 向量覆盖了"远端领先"这个曾经出错的分支', () => {
  const { vectors } = JSON.parse(readFileSync(VECTORS, 'utf-8'));
  // 必须包含远端毫秒领先、且远端计数非零的 update
  const remoteLeads = vectors.filter((v) => {
    if (v.op !== 'update' || !isValidHLC(v.remote)) return false;
    const r = decode(v.remote);
    return r.l > 1000_000 && r.c > 0;
  });
  assert.ok(remoteLeads.length > 0,
    '向量里没有"远端毫秒领先且计数非零"的样本 —— ' +
    '当初出错的分支没被覆盖到');
});
