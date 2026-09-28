// extension/lib/keys.js —— 身份标识（identity）的派生
//
// ── 为什么需要它 ──────────────────────────────────────────────────────
//
// 双向合并的前提是能判定"两台设备上的这两个书签是同一个东西"。Firefox 的
// bookmarks API 只给出**本地整数 id**，跨设备不可用（且 id 会被回收复用）；
// Places 也没有暴露存储自定义元数据的接口（annos 需要私有 API）。
//
// 所以 key 必须由内容直接算出来，**不存任何元数据**：
//
//   书签:   key = SHA-256("u:" + url)                 取前 16 字节
//   文件夹: key = SHA-256("f:" + parentKey + SEP + title)
//
// SEP 是 U+0000（见下方常量）。选它而不是 | 或 : ，是因为 URL 与文件夹名
// 里理论上可以出现 | 和 : ，而 NUL 在 UTF-8 文本中不会出现 —— 它保证拼接
// 结果无歧义。
//
// ── 为什么书签的 key 不带父目录 ────────────────────────────────────────
//
// 若书签 key 也带 parentKey，则**重命名父文件夹会让整棵子树的 key 全变**，
// 子树被判定为「全部删除 + 全部新建」，产生成千上万条墓碑。
//
// 按上表设计：
//   · 书签移动到别的文件夹 → key 不变，仅 p 变化 → 正确同步为"移动"
//   · 文件夹重命名         → 子树 key 变化 → 重建，但 LWW 下内容不丢
//
// ── 已知局限（明确接受，见 design.md §3.3）────────────────────────────
//
//  1. 同一 URL 出现在不同文件夹 → 合并为一条
//  2. 同一 URL 在同一文件夹出现多次 → 去重保留一条
//  3. 文件夹重命名 → 子树重建（内容不丢，但产生大量墓碑）
//  4. 书签改 URL → 表现为「删旧增新」（用户无感）

/** 四个系统根目录的固定 id。跨语言、跨设备稳定，可直接当作顶层 item 的父 key。 */
export const ROOT_TOOLBAR = 'toolbar_____'; // 书签栏
export const ROOT_MENU = 'menu________'; // 菜单
export const ROOT_UNFILED = 'unfiled_____'; // 其他书签
export const ROOT_MOBILE = 'mobile______'; // 移动设备书签

const ROOT_SET = new Set([ROOT_TOOLBAR, ROOT_MENU, ROOT_UNFILED, ROOT_MOBILE]);

export function isRootFolder(key) {
  return ROOT_SET.has(key);
}

/** item 类型。与 Go 端 ItemType 常量一致。 */
export const TYPE_BOOKMARK = 'b';
export const TYPE_FOLDER = 'f';

/** 哈希输入里的分隔符，见文件头说明。 */
const SEP = String.fromCharCode(0);

/**
 * 计算 key 所需的哈希输入。
 *
 * 导出出来是为了让测试能直接断言"父目录 / 标题 / URL 各自的影响"，
 * 而不必先跑一次 SHA-256。
 */
export function buildMaterial(type, { parentKey = '', title = '', url = '' } = {}) {
  if (type === TYPE_FOLDER) {
    return 'f:' + parentKey + SEP + title;
  }
  return 'u:' + url;
}

const encoder = new TextEncoder();

/** 取摘要的前 16 字节并转成 32 字符小写十六进制。 */
function toKey(buf) {
  const bytes = new Uint8Array(buf);
  let out = '';
  for (let i = 0; i < 16; i++) {
    out += bytes[i].toString(16).padStart(2, '0');
  }
  return out;
}

/**
 * 派生单个 key。
 *
 * @param {'b'|'f'} type
 * @param {{parentKey?: string, title?: string, url?: string}} parts
 * @returns {Promise<string>} 32 字符十六进制
 */
export async function deriveKey(type, parts) {
  const digest = await crypto.subtle.digest('SHA-256', encoder.encode(buildMaterial(type, parts)));
  return toKey(digest);
}

/**
 * 批量派生 key。
 *
 * 为什么不逐个 await：几千个书签会产生几千轮微任务。批量提交给 WebCrypto
 * 后并发执行，实测快一个数量级。
 *
 * @param {Array<{type: string, parentKey?: string, title?: string, url?: string}>} inputs
 * @returns {Promise<string[]>} 与 inputs 等长、同序
 */
export function deriveKeys(inputs) {
  return Promise.all(
    inputs.map((parts) =>
      crypto.subtle.digest('SHA-256', encoder.encode(buildMaterial(parts.type, parts))).then(toKey),
    ),
  );
}

/**
 * 解析 getTree() 的根目录，映射成我们用的 key。
 *
 * Firefox 的四个系统根目录 id 在各版本与各语言界面下都是固定的
 * （"书签栏" 和 "书签工具栏" 都对应 toolbar_____），所以**绝不能按标题匹配**
 * —— 界面语言一变就全错了。
 *
 * 兜底策略：若 id 形态与预期不符（理论上不会发生），按前 4 个子项的数组
 * 下标依次映射，并置 warned 让 UI 提示一次。
 *
 * @param {Array} rootChildren getTree()[0].children
 * @returns {{roots: string[], warned: boolean, message?: string}}
 */
export function resolveRoots(rootChildren) {
  const expected = [ROOT_TOOLBAR, ROOT_MENU, ROOT_UNFILED, ROOT_MOBILE];
  const children = rootChildren || [];

  const found = expected.filter((id) => children.some((c) => c && c.id === id));
  if (found.length === expected.length) {
    return { roots: expected, warned: false };
  }

  let message =
    `根目录 id 与预期不符（识别到 ${found.length}/${expected.length} 个），已按位置兜底映射。`;
  if (children.length < expected.length) {
    message += ` 实际只有 ${children.length} 个子目录，某些根目录不会被同步。`;
  }
  return { roots: expected, warned: true, message };
}

/**
 * 判断某个根目录是否应纳入同步。
 *
 * mobile 默认排除：桌面端通常用不上，而且在手机上会产生大量无意义同步。
 * 这是一个可讨论的取舍，见 design.md §13 Q5。
 */
export function isRootEnabled(rootId, enabledRoots) {
  if (rootId === ROOT_MOBILE) return false;
  const set = enabledRoots instanceof Set
    ? enabledRoots
    : new Set(enabledRoots || [ROOT_TOOLBAR, ROOT_MENU, ROOT_UNFILED]);
  return set.has(rootId);
}

/** key 是否是合法的 32 字符小写十六进制。 */
export function isValidKey(key) {
  return typeof key === 'string' && /^[0-9a-f]{32}$/.test(key);
}

export const KEY_LENGTH = 32;
