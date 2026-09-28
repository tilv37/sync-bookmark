// extension/lib/hlc.js —— 混合逻辑时钟（Hybrid Logical Clock）
//
// ⚠️ 本文件与 bmsync/hlc.go 必须**逐行等价**。两端的编码格式、分支顺序、
//    溢出处理只要有一处不同，合并在 "m 恰好相等" 的分支上就会产生非确定
//    行为 —— 同一对书签在不同轮次里交替获胜，最终两端发散且无法察觉。
//
//    一致性由 test/hlc_vectors.json 保证：Go 端导出输入序列与期望输出，
//    JS 端逐条复现（见 hlc.test.js）。
//
// 论文：Kulkarni et al., "Logical Physical Clocks and Consistent Snapshots
//       in Globally Distributed Databases", 2014

const PHYSICAL_DIGITS = 13;
const COUNTER_DIGITS = 5;
const COUNTER_MAX = 99_999;

/** 最小时间戳，用作"还没见过任何事件"的初值。 */
export const HLC_ZERO = '0000000000000-00000';

const FORMAT_RE = /^\d{13}-\d{5}$/;

/**
 * 把 (物理毫秒, 逻辑计数) 编码成定宽字符串。
 * 定宽保证「字符串字典序 == 时间戳全序」，因此 compare 直接用字符串比较，
 * 不需要解析。
 */
export function encode(l, c) {
  return String(l).padStart(PHYSICAL_DIGITS, '0') + '-' + String(c).padStart(COUNTER_DIGITS, '0');
}

/** 解析 HLC 字符串。失败返回 null（而不是抛异常）—— 非法输入不该让同步崩掉。 */
export function decode(s) {
  if (typeof s !== 'string' || s.length !== PHYSICAL_DIGITS + 1 + COUNTER_DIGITS) return null;
  if (s[PHYSICAL_DIGITS] !== '-') return null;
  const ls = s.slice(0, PHYSICAL_DIGITS);
  const cs = s.slice(PHYSICAL_DIGITS + 1);
  if (!/^\d+$/.test(ls) || !/^\d{5}$/.test(cs)) return null;
  return { l: Number(ls), c: Number(cs) };
}

export function isValidHLC(s) {
  return typeof s === 'string' && FORMAT_RE.test(s);
}

/**
 * 比较两个 HLC：-1 / 0 / 1。
 *
 * 非法时间戳被当作比任何合法值都**小**，与 Go 端行为一致 ——
 * 这样被污染的一侧永远输，不会污染权威状态。
 */
export function compare(a, b) {
  const da = decode(a);
  const db = decode(b);
  if (da === null && db === null) return a < b ? -1 : a > b ? 1 : 0;
  if (da === null) return -1;
  if (db === null) return 1;
  if (da.l !== db.l) return da.l < db.l ? -1 : 1;
  if (da.c !== db.c) return da.c < db.c ? -1 : 1;
  return 0;
}

export function maxHLC(...list) {
  let out = HLC_ZERO;
  for (const s of list) {
    if (compare(s, out) > 0) out = s;
  }
  return out;
}

/** 混合逻辑时钟实例。 */
export class HLC {
  #l = 0;
  #c = 0;
  #now;

  /**
   * @param {() => number} nowMs 返回当前墙钟毫秒。测试时注入固定时钟。
   */
  constructor(nowMs = () => Date.now()) {
    this.#now = nowMs;
  }

  #normalize() {
    if (this.#c > COUNTER_MAX) {
      this.#l += 1;
      this.#c = 0;
    }
  }

  /** 为一个本地事件产生时间戳。 */
  now() {
    const p = this.#now();
    if (p > this.#l) {
      this.#l = p;
      this.#c = 0;
    } else {
      this.#c += 1;
    }
    this.#normalize();
    return encode(this.#l, this.#c);
  }

  /**
   * 收到远端时间戳后推进本地时钟。
   *
   * 分支顺序至关重要（与 Go 端一一对应）：
   *   1. max == l && max == p → c = max(c, rc) + 1
   *   2. max == l            → c = c + 1
   *   3. max == rl            → c = rc + 1
   *   4. 否则（全新毫秒）      → c = 0
   *
   * 第 3 条必须排在"物理时钟领先"之前。反例：A 在第 1000ms 内产生计数为
   * 5 的事件，接收方物理时钟也是 1000ms 但自身计数为 0。若此时走第 4 条
   * 得到 (1000, 0)，其后的 (1000, 1) 会**小于**收到的 (1000, 5)，
   * 因果性被破坏，症状是"书签随机丢失"。
   */
  update(remote) {
    const p = this.#now();
    const rd = decode(remote);

    if (rd === null) {
      // 远端非法：退化成纯本地推进
      if (p > this.#l) {
        this.#l = p;
        this.#c = 0;
      } else {
        this.#c += 1;
      }
      this.#normalize();
      return encode(this.#l, this.#c);
    }

    let max = this.#l;
    if (p > max) max = p;
    if (rd.l > max) max = rd.l;

    if (max === this.#l && max === p) {
      if (rd.c > this.#c) this.#c = rd.c;
      this.#c += 1;
    } else if (max === this.#l) {
      this.#c += 1;
    } else if (max === rd.l) {
      this.#c = rd.c + 1;
    } else {
      this.#c = 0;
    }
    this.#l = max;

    this.#normalize();
    return encode(this.#l, this.#c);
  }

  /** 返回当前时间戳但不推进计数。 */
  current() {
    return encode(this.#l, this.#c);
  }

  /** 批量吸收远端时间戳，把本地时钟推进到它们的最大值之后。 */
  observeMany(list) {
    for (const s of list) {
      const d = decode(s);
      if (d === null) continue;
      if (d.l > this.#l || (d.l === this.#l && d.c > this.#c)) {
        this.#l = d.l;
        this.#c = d.c;
      }
    }
    return this.current();
  }
}

/**
 * 编码格式说明，与 Go 端 hlcLayout 常量保持一致。
 * 13 位物理毫秒 + '-' + 5 位逻辑计数，例：1790000000000-00042
 */
export const HLC_LAYOUT = '13 位物理毫秒 + "-" + 5 位逻辑计数，例：1790000000000-00042';
