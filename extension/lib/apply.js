// extension/lib/apply.js —— 应用：同步 state → Firefox 书签树
//
// 与 collect 对称。同样是依赖注入，不直接引用 browser.*。
//
// ── 三条顺序约束（违反其中任何一条都会出错）───────────────────────────
//
//  1. **先删后建**。如果先创建，某个"本地有、云端已删"的项会被误判为
//     "已存在"而跳过，删除就永远同步不过来。
//  2. **删除只处理最上层**。Firefox 的 bookmarks.remove 对文件夹是递归的，
//     子项会跟着消失；对子项再发一次 remove 会因为节点已不存在而报错。
//  3. **按深度升序创建**。父文件夹必须先落地，否则 create 的 parentId 无效。
//     move 同理 —— 可能把项挂到一个尚未创建的目录下。
//
// ── 不做回滚 ──────────────────────────────────────────────────────────
//
// 中途失败时**不回滚**。理由见 design.md §9.6：Firefox 书签树没有事务，
// 实现反向回滚的复杂度远超收益；而 LWW 合并的方向性保证「重试只会更接近
// 正确状态，不会造成破坏」。改为记录已应用的部分并在 UI 上提示重试。

import { TYPE_FOLDER, isRootFolder } from './keys.js';

/**
 * 计算应用计划。
 *
 * 拆成"算计划"和"执行计划"两步，是为了让顺序约束可以被单元测试直接断言，
 * 而不必真的去改一棵 mock 树。
 *
 * @param {Map<string,object>} liveNodes   scan() 的结果：当前本地状态
 * @param {object} incomingState          服务端返回的权威 state
 * @param {Map<string,string>} ffKeyToId   key → Firefox 本地 id
 * @returns {{removes: object[], upserts: object[], skipped: object[], counts: object}}
 */
export function planApply(liveNodes, incomingState, ffKeyToId) {
  const incoming = (incomingState && incomingState.items) || {};

  // desired：服务端状态里所有**存活**的项
  const desired = new Map();
  for (const [key, item] of Object.entries(incoming)) {
    if (item && item.d) continue; // 墓碑不需要落地
    desired.set(key, item);
  }

  // ── 删除阶段 ──────────────────────────────────────────────────
  // 找出「本地有、云端没有」的 key。取其中**父级也不在删除集合里**的那些
  // —— 它们就是删除子树的最上层，递归 remove 会连带清掉子孙。
  const toDelete = new Set();
  for (const key of liveNodes.keys()) {
    if (!desired.has(key)) toDelete.add(key);
  }

  const removes = [];
  for (const key of toDelete) {
    const parentKey = liveNodes.get(key).parentKey;
    if (parentKey && toDelete.has(parentKey)) continue; // 父级也要删，交给它
    const ffId = ffKeyToId.get(key);
    if (ffId === undefined) continue; // 节点已经不在了
    removes.push({ key, ffId, type: liveNodes.get(key).type });
  }

  // ── 创建 / 更新阶段 ───────────────────────────────────────────
  // 计算每个待落地项的深度，用于排序。深度由「顺着 parentKey 一路上溯」
  // 得到；父链断裂的项无法计算深度，记入 skipped。
  const depthOf = (key, seen = new Set()) => {
    if (seen.has(key)) return -1; // 环
    seen.add(key);
    const item = desired.get(key);
    if (!item) return -1;
    if (isRootFolder(item.p)) return 1;
    if (!desired.has(item.p)) return -1; // 父级不在云端状态里
    const parentDepth = depthOf(item.p, seen);
    return parentDepth < 0 ? -1 : parentDepth + 1;
  };

  const upserts = [];
  const skipped = [];
  for (const [key, want] of desired) {
    const depth = depthOf(key);
    if (depth < 0) {
      // 父级缺失或成环。**不能凭空放到根目录** —— 那会把用户的书签
      // 悄悄挪到"其他书签"下面，比跳过更糟。
      skipped.push({ key, reason: 'parent-missing', want });
      continue;
    }

    const live = liveNodes.get(key);
    if (live) {
      const op = { key, depth, ffId: live.ffId, want, live };
      op.titleChanged = live.title !== (want.n || '');
      op.urlChanged = live.url !== (want.u || '');
      op.parentChanged = (live.parentKey || '') !== (want.p || '');
      op.typeChanged = live.type !== want.t;
      if (op.titleChanged || op.urlChanged || op.parentChanged || op.typeChanged) {
        upserts.push(op);
      }
      continue;
    }
    upserts.push({ key, depth, ffId: null, want, live: null, isCreate: true });
  }

  // 深度升序；同深度按 key 排序保证确定性
  upserts.sort((a, b) => a.depth - b.depth || (a.key < b.key ? -1 : a.key > b.key ? 1 : 0));

  return {
    removes,
    upserts,
    skipped,
    // ffIdByKey 是**本地已存在**的 key → Firefox id 全表。
    //
    // 为什么必须单独传：upserts 里只有"需要改动的项"，
    // 而把书签移到一个"本地已有、但本次不需要变动"的目录时，
    // 那个目录根本不在 upserts 里。执行阶段若无从查起就会报
    // "父目录不存在"，结果是 move 静默失败 —— 书签留在原处，
    // 而 sync 报告一切正常。
    //
    // 这个 bug 是靠 apply.test.js 的"父目录变了 → move"用例抓到的。
    ffIdByKey: new Map([...liveNodes].map(([key, n]) => [key, n.ffId]).filter(([, id]) => id)),
    counts: {
      remove: removes.length,
      create: upserts.filter((u) => u.isCreate).length,
      update: upserts.filter((u) => !u.isCreate).length,
      moved: upserts.filter((u) => !u.isCreate && u.parentChanged).length,
      skipped: skipped.length,
    },
  };
}

/**
 * 执行应用计划。
 *
 * @param {object} api browser.bookmarks 的替身
 * @param {object} plan planApply() 的返回值
 * @param {object} [opts]
 * @param {(msg: string) => void} [opts.onProgress] 长任务进度回调
 * @returns {Promise<{created:number, updated:number, moved:number, deleted:number, failed:object[]}>}
 */
export async function executePlan(api, plan, opts = {}) {
  const { onProgress } = opts;
  const result = { created: 0, updated: 0, moved: 0, deleted: 0, failed: [] };

  // ── 阶段 1：删除 ───────────────────────────────────────────────
  for (const r of plan.removes) {
    try {
      await api.remove(r.ffId);
      result.deleted += 1;
    } catch (err) {
      // 节点可能已被用户手动删掉。这不算失败。
      if (!isNotFound(err)) {
        result.failed.push({ op: 'remove', key: r.key, error: String(err) });
      }
    }
  }
  onProgress?.(`已删除 ${result.deleted} 项`);

  // ── 阶段 2：创建 / 更新 ───────────────────────────────────────
  //
  // depth 升序已保证父目录先于子项存在。这里还要处理一种情况：目标父目录
  // 在本地不存在（服务端有但本地的父文件夹被排除了）。这时 create 会失败，
  // 记入 failed 而不是丢到根目录 —— 静默挪位置比报错更难排查。
  // 已存在节点的 id 全表（planApply 提供），再叠加本次新建出来的。
  // 顺序很重要：新建的父目录要在它的子项之前被建好，所以这里
  // 随执行过程不断补充。
  const liveIds = new Map(plan.ffIdByKey || []);
  for (const u of plan.upserts) {
    if (u.ffId && !liveIds.has(u.key)) liveIds.set(u.key, u.ffId);
  }

  for (const u of plan.upserts) {
    try {
      if (u.isCreate) {
        const parentId = resolveParentId(api, u, liveIds);
        if (parentId === null) {
          result.failed.push({ op: 'create', key: u.key, error: '父目录不存在' });
          continue;
        }
        const node = await api.create({
          parentId,
          title: u.want.n || '',
          ...(u.want.t === TYPE_FOLDER ? {} : { url: u.want.u || '' }),
        });
        liveIds.set(u.key, node.id);
        result.created += 1;
        continue;
      }

      // 更新：先 move 再改属性。顺序反了会导致 update 打在旧位置上。
      if (u.parentChanged) {
        const parentId = resolveParentId(api, u, liveIds);
        if (parentId === null) {
          result.failed.push({ op: 'move', key: u.key, error: '父目录不存在' });
          continue;
        }
        await api.move(u.ffId, { parentId });
        result.moved += 1;
      }
      // 类型变了（书签 ↔ 文件夹）只能删了重建：bookmarks.update 不支持改类型。
      if (u.typeChanged) {
        await api.remove(u.ffId);
        const parentId = resolveParentId(api, u, liveIds);
        if (parentId === null) {
          result.failed.push({ op: 'recreate', key: u.key, error: '父目录不存在' });
          continue;
        }
        const node = await api.create({
          parentId,
          title: u.want.n || '',
          ...(u.want.t === TYPE_FOLDER ? {} : { url: u.want.u || '' }),
        });
        liveIds.set(u.key, node.id);
        result.created += 1;
        continue;
      }
      if (u.titleChanged || u.urlChanged) {
        await api.update(u.ffId, {
          ...(u.titleChanged ? { title: u.want.n || '' } : {}),
          ...(u.urlChanged && u.want.u ? { url: u.want.u } : {}),
        });
        result.updated += 1;
      }
    } catch (err) {
      if (!isNotFound(err)) {
        result.failed.push({ op: 'update', key: u.key, error: String(err) });
      }
    }
  }
  onProgress?.(`已创建 ${result.created}、更新 ${result.updated}、移动 ${result.moved} 项`);

  return result;
}

/**
 * 解析父目录的 Firefox id。
 *
 * 顶层项的父级是四个根目录之一 —— 它们本身不在 desired 里，但它们的
 * Firefox id 是**固定字面量**，可以直接用。
 */
function resolveParentId(api, u, liveIds) {
  const parentKey = u.want.p;
  // 顶层项的父级是四个根目录之一 —— 它们不是 item，但 Firefox id
  // 是**固定字面量**，可以直接用。
  if (isRootFolder(parentKey)) return parentKey;
  const id = liveIds.get(parentKey);
  return id === undefined ? null : id;
}

function isNotFound(err) {
  const msg = String(err && err.message ? err.message : err).toLowerCase();
  return msg.includes('no bookmark') || msg.includes('not found') || msg.includes('不存在');
}
