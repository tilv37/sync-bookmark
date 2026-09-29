# Bookmark Sync — 设计文档

> 状态：草案 v0.1，待评审
> 最后更新：2026-09-28

---

## 1. 背景与目标

### 1.1 场景

- **个人 PC**：日常主力机，Firefox 已登录个人账号（书签走原生同步，密码已迁移至 Bitwarden）
- **公司 PC**：策略限制无法登录个人 Firefox 账号，密码用 Bitwarden 替代，**书签完全空白**
- 两台机器均使用 Firefox 桌面版
- 用户拥有个人 VPS

### 1.2 目标

构建一个**轻量**的书签同步工具：

1. Firefox 扩展，工具栏点击**手动触发**同步
2. 书签数据存放在用户自己的 VPS 上
3. 支持**双向合并**：两端各自新增/修改/删除都能收敛到一致状态
4. **删除必须能同步**（这是与多数简易方案的核心差异点）
5. 冲突采用**静默 LWW**（Last-Write-Wins），但留下可查日志
6. 部署形态支持 **Docker Compose**

### 1.3 非目标（明确不做）

| 不做的事 | 原因 |
|---|---|
| 密码同步 | 已由 Bitwarden 解决 |
| 书签 tags（标签） | `bookmarks` API 可读但增加合并维度，个人场景收益低 |
| 书签描述 / annotations | `bookmarks` API **不暴露**，需绕过私有接口，稳定性风险高 |
| 阅读列表 / 书签截图 | Firefox 特有，API 支持有限 |
| 手动拖拽排序同步 | 明确简化，见 §5.4 |
| 本地排除机制（不同步某些目录） | 确认两端书签集合完全对等 |
| 多根目录选择性同步 | 全部根目录一起同步 |
| 增量传输 / 压缩 | 书签量级下全量传输足够，见 §12.1 |
| 多用户 / 团队协作 | 单用户自用 |

### 1.4 与 Floccus 的差异

Floccus 是完整的 CRDT 书签同步引擎（自动同步、加密同步、多后端）。本项目刻意只做**手动触发的最小可用内核**，代码量目标在 **1500 行以内（不含注释与测试）**。

---

## 2. 总体架构

```
┌──────────────────────────────┐         ┌─────────────────────────────┐
│  个人 PC (Firefox)            │         │  公司 PC (Firefox)           │
│  ┌────────────────────────┐  │         │  ┌────────────────────────┐  │
│  │  bmsync 扩展           │  │         │  │  bmsync 扩展           │  │
│  │  ├ collect.js  采集    │  │         │  │  ├ collect.js  采集    │  │
│  │  ├ apply.js    应用    │  │         │  │  ├ apply.js    应用    │  │
│  │  └ hlc.js     时钟     │  │         │  │  └ hlc.js     时钟     │  │
│  └───────────┬────────────┘  │         │  └───────────┬────────────┘  │
└──────────────┼───────────────┘         └──────────────┼───────────────┘
               │  ① POST /api/sync                    │
               │     (state + base)                    │
               │  ② GET  /api/health                   │
               │  ③ GET  /api/conflicts                │
               ▼                                       ▼
        ┌──────────────────────────────────────────────────┐
        │  VPS                                              │
        │  ┌────────────────────────────────────────────┐  │
        │  │ bmsync (.NET 10 NativeAOT 单二进制)         │  │
        │  │  ├ auth      Bearer token                  │  │
        │  │  ├ merge     ★ 唯一的合并算法实现          │  │
        │  │  ├ store     原子写 + 互斥串行化            │  │
        │  │  ├ snapshot  历史快照 (保留 30 份)          │  │
        │  │  └ gc        墓碑清理 (90 天)               │  │
        │  └────────────────────────────────────────────┘  │
        │  data/                                           │
        │   ├ state.json          当前权威状态           │
        │   ├ conflicts.json      冲突环形缓冲 (500 条)   │
        │   └ history/            快照目录                │
        └──────────────────────────────────────────────────┘
                    ▲
                    │  http://bmsync:8080   (内网, 明文)
         ┌──────────────────────────────────────────────────┐
         │ Nginx Proxy Manager                              │
         │   证书签发/续期 · HTTPS 终结 · 反向代理            │
         │   https://<你的域名>/api/*   ← 扩展只连这一层      │
         └──────────────────────────────────────────────────┘
```

> **反代层说明: 项目不自带反代。** TLS 证书签发与续期、HTTPS 终结、gzip、访问日志
> 统一由已有的 **Nginx Proxy Manager** 承担。`bmsync` 只监听明文 HTTP 端口,
> 且**只应在内网可达**。

### 2.1 核心设计决策：服务端是唯一的合并点

**插件不做合并。** 插件只有两个方向的机械工作：

- **采集**：读 Firefox 书签树 → 与本地缓存对比 → 给发生变化的项打上新的 HLC 时间戳 → POST 完整 state
- **应用**：收到服务端返回的合并结果 → 与采集时的快照做 diff → 把差异 create/update/delete 到 Firefox

**合并算法在整个系统中只存在一份实现（服务端）。** 这带来三个好处：

1. 不会出现两端合并逻辑不一致导致的分叉
2. 操作天然幂等，重复点击同步按钮是安全的
3. 客户端不需要维护版本号 / lastSyncSeq 等易错状态

### 2.2 客户端本地缓存的角色

插件在 IndexedDB 中保存一份 `state.json` 的镜像。用途：

1. 计算 diff 时需要基线（否则无法判断哪些项是"本次新改的"）
2. 支持三方合并的 base 数据
3. 离线状态下不丢东西

**关键约束：每次同步后，客户端缓存被服务端返回的 state 完整替换。**
因此墓碑 GC 只在服务端做一处，客户端被动跟随，不会出现两端 GC 步调不一致的问题。

---

## 3. 身份标识（Identity）—— 本项目最核心的设计

双向合并的前提是能判定"两端这两个书签是同一个东西"。Firefox 的 `bookmarks` API 只提供**本地整数 id**，跨设备不可用（且 id 会被回收复用）。Places 也没有暴露存储自定义元数据的接口（annos 需要私有 API）。

### 3.1 方案：内容派生确定性 key

**不存储任何元数据，key 直接由内容哈希得出：**

```
bookmark:  key = hash("u:" + url)
folder:    key = hash("f:" + parentKey + "\x00" + title)
```

- 哈希算法：**SHA-256，取前 16 字节，hex 编码 → 32 字符**（与服务端无耦合，
  因为只有扩展端计算 key；服务端只做校验与合并）
- 分隔符是 **U+0000（NUL）**，避免 URL 或标题中包含分隔符导致的歧义。
  选它而不是 `|` 或 `:`，是因为 URL 与文件夹名里理论上可以出现后两者，
  而 NUL 在 UTF-8 文本中不会出现 —— 它保证拼接结果无歧义。
  实现见 `extension/lib/keys.js` 的 `SEP` 常量，它写作 `String.fromCharCode(0)`
  而不是字面量：源码里直接写 NUL 会在很多编辑器里变成不可见字符，
  而且会让本文件这样的 Markdown 被误判为二进制。

### 3.2 为什么书签的 key 不包含父目录

若书签 key 也带 parentKey，则**重命名父文件夹会导致整棵子树的 key 全部改变**，子树被判定为「全部删除 + 全部新建」，产生成千上万条墓碑。

按上表设计：

| 操作 | 判定结果 | 是否正确 |
|---|---|---|
| 书签移动到别的文件夹 | key 不变，仅 `p` 字段变化 | ✅ 正确同步为"移动" |
| 书签改标题 | key 不变，`n` 变化 | ✅ |
| 书签改 URL | key **变化** | ⚠️ 表现为「删除旧的 + 新增新的」，视觉与语义上无感 |
| 文件夹重命名 | key 变化，子树重建 | ⚠️ 内容不丢（LWW 下新项时间戳更晚会赢），但产生大量墓碑 |
| 文件夹移动 | key 变化（依赖 parentKey） | ⚠️ 同上 |

### 3.3 已知局限（明确接受）

1. **同一 URL 在不同文件夹出现 → 合并为一条**。设计上接受。
2. **同一 URL 在同一文件夹出现多次 → 去重保留一条**。设计上接受。
3. **文件夹重命名 → 子树重建**。若某个大目录被重命名，会产生大量墓碑。90 天后自动清理。
4. **书签改 URL → 表现为删旧增新**。用户无感（标题通常也不变）。
5. **根文件夹（书签栏/菜单/其他书签）不参与身份计算**，用固定哨兵值，见 §3.4。

### 3.4 根目录的处理

Firefox `bookmarks.getTree()` 返回的四个系统根目录，其 id 是**跨语言环境固定的**（不受界面语言影响）：

| id | 中文界面显示 | 处理方式 |
|---|---|---|
| `toolbar_____` | 书签栏 | **参与同步**，其 key 即字面量 `"toolbar_____"` |
| `menu________` | 菜单 | **参与同步** |
| `unfiled_____` | 其他书签 | **参与同步** |
| `mobile______` | 移动设备书签 | **默认排除**（见下） |

**顶层的直属条目，其 `p` 字段直接填上表的根目录 id 字面量。** 这样跨设备、跨语言都稳定。

兜底策略：若 `getTree()` 的 children 数量或 id 形态与预期不符（理论上不会发生），按前 4 个子项的数组下标依次映射为 `toolbar_____` / `menu________` / `unfiled_____` / `mobile______`，并在 popup 中显示一次警告。

`mobile______` 默认排除：移动设备书签在桌面端通常无用，且会在手机上产生大量无意义同步。是否纳入由后续版本决定。

---

## 4. 数据模型

### 4.1 State Schema

```jsonc
{
  "v": 1,                        // schema 版本
  "hlc": "1790000000000-00042",  // 服务端当前 HLC，供客户端校时
  "items": {
    // key = 32 字符 hex（不放进 item 内部，map 的键就是它）
    "9f2a1c8b7d6e5f4a3b2c1d0e9f8a7b6c": {
      "p": "toolbar_____",       // parent key（根目录用字面量 id）
      "t": "b",                  // b = bookmark, f = folder
      "n": "示例标题",           // name / title
      "u": "https://example.com",// url（仅 t=b）
      "a": "1790000000000-00000",// HLC: created
      "m": "1790000000000-00007",// HLC: last modified ← LWW 比较依据
      "d": false,                // deleted（墓碑标记）
      "x": 0                     // deletedAt（0 = 未删除）
    }
  }
}
```

### 4.2 字段语义

| 字段 | 类型 | 说明 |
|---|---|---|
| `p` | string | 父目录 key。指向文件夹的 key，或四个根目录 id 之一 |
| `t` | `"b"` / `"f"` | item 类型 |
| `n` | string | 书签标题 / 文件夹名 |
| `u` | string | URL，**仅 `t=b` 存在** |
| `a` | HLC string | 创建时间。用于可选展示，**不参与合并** |
| `m` | HLC string | 最后修改时间，**LWW 的唯一比较依据** |
| `d` | bool | 是否墓碑 |
| `x` | number | 删除发生的墙钟时间（毫秒），**仅用于 GC 判定** |

**要点：合并只比较 `m`。`a` 和 `x` 不参与任何决胜逻辑。**

### 4.3 尺寸估算

单条书签约 **130–170 字节**（JSON，无缩进）。

| 书签数 | state.json 体积 |
|---|---|
| 500 | ~80 KB |
| 2,000 | ~320 KB |
| 5,000 | ~800 KB |
| 20,000 | ~3.2 MB |

全量 JSON 传输在这个量级完全可接受。服务端可开启 gzip（见 §12.1）。

---

## 5. 同步流程

### 5.1 采集（客户端 → 出站 state）

```
1. tree = browser.bookmarks.getTree()
2. 递归遍历，建立两个映射：
     ffIdToKey:  Firefox 本地 id → key      （本次运行内有效）
     keyToNode:  key → {type, title, url, parentKey, ffId}
3. 与本地缓存 cached.items 逐项对比：
   ┌────────────────────────────────────────────────────────┐
   │ key 在 live 中 & 不在 cached  →  NEW       m = HLC.now()│
   │ key 在 live 中 & 在 cached     →  内容有变? MODIFIED     │
   │                                →  m = HLC.now()          │
   │                                →  内容无变? 沿用 cached.m │
   │ key 在 live 中 & 在 cached 标记为 d=true → 复活           │
   │                                →  d=false, m = HLC.now()│
   │ key 不在 live 中 & 在 cached   →  DELETED                │
   │                                →  d=true, x=now, m=now  │
   │ key 不在 live 中 & 不在 cached →  忽略（说明 GC 已清理）  │
   └────────────────────────────────────────────────────────┘
   「内容」= 比较 (p, t, n, u) 四元组，不比较 m / a / d
4. base = { key: cached[key].m }  ← 精简为 key→m 映射，用于三方冲突检测
5. POST /api/sync { device, state, base }
```

**根目录的 4 个 sentinel 不作为 item 参与**，只作为顶层 item 的 `p` 值。

### 5.2 合并（服务端）

三方检测 + HLC 决胜：

```
for key in union(server.items, incoming.items):
    S = server.items[key]        // 服务端当前（记为 base' 的最新）
    C = incoming.items[key]      // 客户端送来
    B = incoming.base[key]       // 客户端上次见到该 key 时的 m（可能不存在）

    if S 不存在:  result[key] = C
    elif C 不存在: result[key] = S
    elif C.m > S.m: result[key] = C
    elif S.m > C.m: result[key] = S
    else:                      # m 完全相等
        if 内容相同(S, C):      result[key] = S
        else:                  # 理论上不应发生，按 key 字典序定胜负兜底
                            result[key] = (S.n < C.n) ? S : C
                            记为 conflict(reason="hlc_collision")

    # 三方冲突检测（不改变决胜结果，只记日志）
    Bm = B (不存在则视为 -∞)
    clientChanged = C.m > Bm
    serverChanged = S.m > Bm
    if clientChanged && serverChanged && 内容不同(S, C):
        记 conflict(key, field, winner, loser_device)
```

伪代码（Go）：

```go
func Merge(server, incoming State, base map[string]string, device string) (State, []Conflict) {
    result := State{V: 1, Items: map[string]Item{}}
    conflicts := []Conflict{}
    for k := range unionKeys(server.Items, incoming.Items) {
        s, sOK := server.Items[k]
        c, cOK := incoming.Items[k]
        switch {
        case !cOK:
            result.Items[k] = s
        case !sOK:
            result.Items[k] = c
        case compareHLC(c.M, s.M) > 0:
            result.Items[k] = c
        case compareHLC(s.M, c.M) > 0:
            result.Items[k] = s
        default:
            if sameContent(s, c) {
                result.Items[k] = s
            } else {
                if s.N < c.N { result.Items[k] = s } else { result.Items[k] = c }
                conflicts = append(conflicts, Conflict{Key: k, Reason: "hlc_collision", ...})
            }
        }
        if bm, ok := base[k]; ok {
            clientChanged := compareHLC(c.M, bm) > 0
            serverChanged := compareHLC(s.M, bm) > 0
            if clientChanged && serverChanged && !sameContent(s, c) {
                conflicts = append(conflicts, Conflict{Key: k, Reason: "concurrent_edit", ...})
            }
        }
    }
    return result, conflicts
}
```

> **为什么做三方检测而不是两方？** 两方 LWW 只能告诉你「谁赢了」，无法区分「我改了」和「对面改了又改了两次」。没有三方基线，冲突日志会塞满噪声（每次同步都会把"服务端刚合过的东西"记成冲突），日志很快失去价值。代价是请求体多一个 `base` 映射（约 50 字节/条），可接受。

### 5.3 应用（入站 state → Firefox）

```
1. desired = { key → item | item.d == true 剔除 }
2. live    = 重新扫描当前 Firefox 树（同 §5.1 步骤 1-2）

3. 【删除阶段】自顶向下：找到 live 中存在但 desired 中缺失的 key，
   取其中 parentKey 最深的那些调用 browser.bookmarks.remove(ffId)
   （Firefox 的 remove 对文件夹是递归的，因此只需处理"最上层"的删除节点）

4. 【创建/更新阶段】按 depth 升序处理（保证父文件夹先存在）：
   for key in sorted(desired, by depth):
       if key ∉ live:
           ffId = browser.bookmarks.create({ parentId, title, url })
           live[key] = { ffId, depth: depth+1 }
       else:
           if desired.p ≠ live.parentKey:  browser.bookmarks.move(ffId, {parentId})
           if desired.n ≠ live.title   ||  desired.u ≠ live.url:
                                          browser.bookmarks.update(ffId, {...})
5. 统计 diff → { created, updated, deleted } → 写入本地缓存
6. 本地缓存 = 服务端返回的 state（完整替换）
```

**顺序为什么重要：**
- 删除阶段先做：否则新建的项可能被误判为"已存在"而跳过
- 创建阶段按深度升序：父文件夹必须先落地，否则 `create` 会因 parentId 无效而失败
- 移动（`move`）必须在删除阶段之后、创建阶段中处理，因为移动可能把项挂到尚未创建的目录下

### 5.4 排序策略（已确认简化）

不同步手动拖拽。具体规则：

- **不携带任何 sortKey 字段**
- 采集时 `bookmarks.create` 不传 `index` → 新项追加到父目录末尾
- 应用时同理，接收方的新项也追加到末尾

**结果：书签内容一定一致；文件夹内的排列顺序不保证一致。**
两台设备各自新增的项，合并后顺序可能与任何一端的原始顺序都不同。不影响使用。

> 未来若要支持，需要引入字符串分数索引（fractional index），并监听 `bookmarks.onMoved` 重排。列入 backlog。

---

## 6. 时钟：HLC（Hybrid Logical Clock）

### 6.1 为什么需要

LWW 完全依赖时间戳。裸用 `Date.now()` 在两台机器时钟偏差 5 分钟的情况下，"我 10:00 加的书签" 会被 "你 10:03 看到的旧版本" 覆盖 —— **静默丢数据，且用户完全无感**。这是同步工具最糟糕的失败方式。

HLC 的作用：保证**因果序**（causal order）严格成立。只要 A 端先做了某操作、B 端后看到了它，B 端后续产生的任何时间戳一定大于 A 端那个。

### 6.2 算法定义（两端必须严格一致）

状态：`(l uint64 物理毫秒, c uint32 逻辑计数)`

```
now():                                   # 本地事件
    p = currentTimeMillis()
    if p > l:  l = p;  c = 0
    else:      c = c + 1
    return encode(l, c)


update(remote):                          # 收到远端时间戳 (rl, rc)
    p   = currentTimeMillis()
    max = max(l, p, rl)
    if   max == l  &&  max == p:  c = max(c, rc) + 1
    elif max == l:                  c = c + 1
    elif max == rl:                 c = rc + 1
    else:                           c = 0
    l = max
    return encode(l, c)
```

**分支顺序不可交换 —— 第三条必须是 `max == rl`，不能写成 `max == p`。**

反例：A 在第 1000ms 内产生 6 个事件，最后一个是 `(1000, 00005)`；
接收方 B 的物理时钟恰好也是 1000ms，但自身逻辑计数为 0（`max` 三者相等，
走的是第一条）。而若物理时钟恰为 1001ms，则 `max == p` 成立 ——
写成 `elif max == p: c = 0` 会让 B 得到 `(1000, 00000)`……

更准确的反例是另一条路径：**A 的事件毫秒领先**（`(1500, 00005)`），
B 的本地时钟 `(1000, 00003)`。此时 `max == rl`。
若误写成 `elif max == p: c = 0`，B 得到 `(1500, 00000)`，其后 `now()`
产生 `(1500, 00001) < (1500, 00005)` —— **因果性被破坏**。
正确的 `c = rc + 1` 让 B 得到 `(1500, 00006)`。

> 这个 bug 在实现阶段真实发生过，由
> `TestHLCCausalCatchupWhenRemoteLeadsWithinSameMillisecond` 捕获。
> 症状是"书签随机丢失"：两个设备都认为自己的版本更新，交替获胜。

**溢出处理**：若 `c > 99999`，则 `l = l + 1; c = 0`。（两端一致）

### 6.3 编码格式

```
encode(l, c) = sprintf("%013d-%05d", l, c)
```

例：`"1790000000000-00042"`

- 13 位物理毫秒（可用至公元 2286 年）
- 5 位逻辑计数，定宽
- **字符串字典序 == 时间戳全序**，因此 `compareHLC(a, b) == strings.Compare(a, b)`，JSON 存储也天然有序

### 6.4 校时流程

```
客户端                                    服务端
  │  POST /api/sync { state, base }          │
  │ ───────────────────────────────────────► │
  │                                          ├─ 解析所有 m / a，取 max
  │                                          ├─ HCL.update(maxRemote)
  │                                          └─ merge
  │  200 { state, summary, conflicts, hlc }  │
  │ ◄─────────────────────────────────────── │
  ├─ HCL.update(response.hlc)                │
  └─ 应用                                    │
```

**每次同步都是一次校时。** 只要用户在两台机器都同步过，时钟偏差就会被拉回因果序范围内。

### 6.5 边界情况

| 场景 | 处理 |
|---|---|
| 设备时钟被手动改到过去 | HLC 的 `l` 只增不减，改时钟不影响已发出的时间戳 |
| 设备长期离线后首次同步 | 离线期间产生的时间戳会偏小，可能被覆盖 → 列入风险表 §12.3 |
| 两端 HLC 计数器同时溢出 | `l+1` 兜底，冲突概率极低且结果确定 |

---

## 7. HTTP API

Base path: `/api`。所有接口要求 HTTPS。

### 7.1 认证

```
Authorization: Bearer <token>
```

- token：≥32 字符随机串（`openssl rand -hex 32`）
- 服务端使用**恒定时间比较**（`crypto/subtle.ConstantTimeCompare`）防时序侧信道
- token 通过环境变量 `BMSYNC_TOKEN` 注入，不写入代码 / 镜像 / 仓库

### 7.2 `POST /api/sync`

**Request**

```jsonc
{
  "device": "pc-personal",     // 设备标识，字符串
  "state": { "v": 1, "items": { ... } },
  "base":  { "<key>": "<hlc>", ... }   // 上次从服务端拿到的 m 快照
}
```

**Response 200**

```jsonc
{
  "state":  { "v": 1, "hlc": "...", "items": { ... } },   // 合并后的完整状态
  "summary": { "created": 12, "updated": 5, "deleted": 3, "unchanged": 1980 },
  "conflicts": [ /* 本次新增的冲突 */ ],
  "hlc": "1790000000000-00123",    // 服务端 HLC，供客户端校时
  "serverTime": 1790000000123
}
```

`summary` 语义（**服务端视角**，客户端 UI 文案需相应调整，见 §10.3）：

- `created` / `deleted`：本次合并**真正新增/删除**的 item 数（两端口径一致）
- `updated`：服务端已有 item 的字段被客户端覆盖的数量
- `unchanged`：合并后与合并前服务端状态完全一致的 item 数

> **UI 展示口径待定**：用户想看的是「我这台机器的书签变了多少」还是「云端总共有多少」。初稿按**云端累计总数** + **本次变化数**两行展示。**见 §13 待确认事项 Q3。**

**Response 200（无需变更的幂等短路）**

若 `state` 内容与服务端完全一致，仍返回 200 与完整 state —— 幂等，不做特殊短路（省去分支带来的复杂度）。

### 7.3 `GET /api/health`

```jsonc
{ "ok": true, "version": 1, "items": 2001, "uptime": 86400, "serverTime": 1790000000123 }
```

用于扩展 options 页的「测试连接」按钮。

### 7.4 `GET /api/conflicts?limit=50`

```jsonc
{
  "conflicts": [
    {
      "at": 1790000000123,
      "device": "pc-work",
      "key": "9f2a1c8b...",
      "reason": "concurrent_edit",
      "field": "n",
      "winner": "server",
      "loser": "pc-work",
      "winnerValue": "新标题",
      "loserValue": "旧标题",
      "url": "https://example.com"
    }
  ]
}
```

存储于 `data/conflicts.json`，**环形缓冲，上限 500 条**，最旧的被覆盖。

### 7.5 `GET /api/history`

列出快照，用于人工回滚（**v1 仅提供只读列表 + 说明，不实现一键回滚 UI**）。

```jsonc
{ "snapshots": [ { "id": "20260928T101500Z", "at": 1790000000123, "items": 2001 }, ... ] }
```

### 7.6 错误码

| HTTP | code | 含义 | 客户端行为 |
|---|---|---|---|
| 400 | `bad_request` | JSON 解析失败 / schema 不符 | 提示「服务端数据格式异常」 |
| 401 | `unauthorized` | token 错误 | 提示「令牌无效，请检查设置」 |
| 413 | `payload_too_large` | 请求体超限 | 提示「书签数量超出限制」 |
| 422 | `validation_failed` | item 数量/深度超限 | 弹窗展示具体原因 |
| 429 | `rate_limited` | 触发限流 | 退避重试 |
| 500 | `internal_error` | 服务端异常 | 提示「同步失败，请稍后重试」，**不修改本地书签** |

**关键约束：任何非 200 响应都不得导致客户端对本地书签做任何修改。**

### 7.7 服务端防护

| 项 | 值 |
|---|---|
| 请求体上限 | 16 MB |
| item 数量上限 | 50,000 |
| 树深度上限 | 32 层 |
| 单次请求耗时上限 | 10s |
| 限流 | 按 IP，10 次/分钟 |
| CORS | 不设置（扩展走 fetch，非浏览器页面） |
| 日志 | 记录 method/path/status/duration，**不记录 body，不记录 token** |

---

## 8. 服务端设计（Go）

### 8.1 目录结构

```
bmsync/
├── global.json            SDK 版本（10.0.401，rollForward: latestFeature）
├── Directory.Build.props   可空引用、隐式 using、警告即错误
├── Directory.Packages.props  集中包版本管理
├── src/
│   ├── BookmarkSync.Domain/   领域层：数据模型 + 合并算法 + HLC + GC
│   │   ├── State.cs          State / Item 类型、Schema 校验
│   │   ├── Merge.cs          ★ 合并算法
│   │   ├── Hlc.cs            HLC 实现
│   │   ├── TombstoneGc.cs    墓碑清理
│   │   ├── Limits.cs         领域限额（MaxItems / MaxDepth / 字段长度）
│   │   ├── StateJsonConverters.cs  JSON 对齐 Go 的 omitempty + key 排序
│   │   └── DomainJsonContext.cs     源生成上下文（AOT 用）
│   ├── BookmarkSync.Store/    读写、原子写、互斥串行化、快照
│   │   ├── BookmarkStore.cs
│   │   ├── StoreOptions.cs
│   │   ├── FileOps.cs        平台相关的 rename 重试
│   │   └── *_test.cs
│   ├── BookmarkSync.Server/   HTTP 层
│   │   ├── BmsyncServer.cs    组装 WebApplication、路由表
│   │   ├── ApiTypes.cs        请求/响应结构（★ 线上协议，改这里要配扩展）
│   │   ├── ApiEndpoints.cs    四个端点
│   │   ├── Auth.cs            Bearer token 校验 + 限流中间件（信任边界）
│   │   ├── HttpJson.cs        解码、编码、限流参数
│   │   ├── RateLimiter.cs     令牌桶（手写，保持零依赖）
│   │   ├── ServerOptions.cs   环境变量与配置
│   │   ├── Healthcheck.cs     docker HEALTHCHECK 实现
│   │   └── ApiJsonContext.cs  源生成上下文
│   └── BookmarkSync.Cli/      入口、优雅关闭、healthcheck 子命令
│       ├── Program.cs         进程 Main
│       └── Cli.cs
├── tests/                     三个测试项目，与 src 一一对应
└── Dockerfile                 多阶段：测试 → NativeAOT → FROM scratch
```

依赖单向：`Cli → Server → Store → Domain`，无人反向引用。
这样划分的主要目的是让 `Merge.cs` 能脱离 HTTP 被完整测试，同时让
"信任边界"（`Auth.cs`）在目录上一眼可辨。

**零第三方 NuGet 包。** 生产代码唯一的外部引用是
`Microsoft.Extensions.Logging.Abstractions`（只取 `ILogger` 接口）；
HTTP 层用 ASP.NET Core 自带的 Minimal API，不引入 web 框架。

**为什么用源生成的 JSON**：`PublishAot` 会禁用反射式 JSON，
一旦用到反射就是运行时的硬错误（而不是变慢）。源生成顺带让"哪些类型参与
序列化"写在代码里，改协议时会看到编译错误。代价是所有 JSON 写出/读入都要
走 `JsonTypeInfo<T>` 重载（`ApiJson.TypeInfoOf<T>()`）。

### 8.2 存储布局

```
/data/
├── state.json           # 当前权威状态
├── state.json.tmp       # 写入临时文件（原子 rename 用）
├── conflicts.json       # 冲突环形缓冲
├── meta.json            # { schemaVersion, lastSyncAt, deviceCount }
└── history/
    ├── 20260928T101500Z.json
    ├── 20260928T140230Z.json
    └── ...              # 保留最近 30 份
```

### 8.3 原子写

```
1. json.MarshalIndent → state.json.tmp
2. fsync(tmp)
3. os.Rename(tmp, state.json)     // POSIX 原子操作
4. fsync(dir)
```

### 8.4 并发控制

单进程 + `sync.Mutex` 保护整个「读 state → merge → 写 state」临界区。
不使用文件锁（单容器单进程，VPS 场景无横向扩展需求）。

**临界区内不写 history 快照**（快照在临界区外、rename 成功后执行），避免拖慢锁持有时间。

### 8.5 墓碑 GC（90 天）

在每次成功同步后执行：

```
cutoff = serverTime - 90*24*3600*1000
for key, item in state.items:
    if item.d == true && item.x < cutoff:
        delete(state.items, key)
```

- 依据 `x`（删除墙钟时间），不依据 HLC
- **只删墓碑，绝不删活跃 item**
- GC 结果计入当次 summary 的 `deleted`（这样用户能看到 GC 清理了多少）
- 客户端因「完整替换缓存」自动跟随，无需各自的 GC 逻辑

### 8.6 快照

- 每次**成功写入 state** 之前，把当前 state.json 复制到 `history/<ISO时间戳>.json`
- 命名冲突时追加毫秒
- 超过 30 份时按时间删除最旧的
- 快照创建失败**不阻断同步**（记 warning 日志即可）

### 8.7 启动自检

- `/data` 可读写
- `state.json` 存在且 schema 版本匹配；不匹配则**拒绝启动**并给出明确错误（不自动迁移，避免数据被静默改写）
- token 非空且长度 ≥ 32

---

## 9. 扩展设计

### 9.1 技术栈

**纯 JavaScript ES Module，零构建、零 node_modules、零框架。**
直接用 `about:debugging` → 「此 Firefox」→ 「临时载入附加组件」加载 `manifest.json` 即可调试。

### 9.2 目录结构

```
extension/
├── manifest.json
├── background.js       # 入口：消息路由、badge 状态、同步编排
├── lib/
│   ├── hlc.js          # HLC（与 Go 端算法逐字对应）
│   ├── keys.js         # key 派生、根目录 id 解析
│   ├── collect.js      # Firefox 树 → state + diff
│   ├── apply.js        # state → Firefox 树变更
│   ├── diff.js         # 内容比较、summary 统计
│   ├── client.js       # HTTP 封装、错误映射
│   └── store.js        # IndexedDB 封装
├── popup/
│   ├── popup.html
│   ├── popup.css
│   └── popup.js        # 状态摘要、冲突列表、设置入口
├── options/
│   ├── options.html
│   └── options.js      # 服务器地址、token、测试连接、根目录开关
└── icons/
```

### 9.3 manifest 要点

```jsonc
{
  "manifest_version": 3,
  "name": "BM Sync",
  "version": "0.1.0",
  "browser_specific_settings": {
    "gecko": { "id": "bmsync@local", "strict_min_version": "115.0" }
  },
  "permissions": ["bookmarks", "storage"],
  "host_permissions": ["https://<你的域名>/api/*"],
  "background": { "scripts": ["background.js"] },   // 注意：Firefox MV3 用 scripts，非 service_worker
  "action": { "default_popup": "popup/popup.html", "default_icon": {...} }
}
```

**Firefox MV3 的两个坑（必须提前知道）：**

1. **站点权限需用户手动授予。** MV3 强制权限分离，首次点击同步时会弹出「允许访问 xxx.com」。必须在 popup 里做引导文案，否则用户会以为插件坏了。
2. **`background.service_worker` 不被支持**，Firefox MV3 使用非持久化事件页，即 `background.scripts`。

### 9.4 bookmarks API 使用要点

| 事实 | 影响 |
|---|---|
| `bookmarks.create` **支持 `index` 参数** | v1 不用（不传即追加末尾），但排序功能扩展时无需受限 |
| `bookmarks.remove` 对文件夹是**递归**的 | 删除阶段只需处理「最上层」的待删节点 |
| **无法设置 `dateAdded`** | `a` 字段在新设备上会是本地时间，仅用于展示，不参与合并 |
| id 为本地整数，**会被回收复用** | 绝不可作为身份标识 |
| `getTree()` 根目录 id 跨语言固定 | 见 §3.4 |

### 9.5 同步编排（background.js）

```
点击 popup 的「同步」按钮
  → runtime.sendMessage({type: "sync"})
  → 设置 badge = 同步中
  → collect()  →  POST  →  HLC 校时  →  apply()  →  写缓存
  → badge = 成功(绿) / 失败(红)，3 秒后恢复
  → popup 展示 summary
```

**并发保护**：同步进行中再次点击 → 直接忽略并提示「同步进行中」。
（即使不保护，合并是幂等的，但避免 UI 抖动。）

### 9.6 失败时的行为（重要）

| 失败点 | 行为 |
|---|---|
| 采集阶段异常 | 不发请求，不动本地，提示错误 |
| 网络失败 / 非 200 | **绝不动本地书签**，提示错误，保留缓存 |
| 校时失败 | 忽略，沿用本地 HLC |
| 应用阶段中途异常 | **不做回滚**（Firefox 树无事务），但：① 已应用的部分是正确收敛的方向，② 在 popup 显著位置提示「本次同步部分完成，建议重试」，③ 重试是幂等的 |

**不做应用阶段回滚是刻意的选择**：实现回滚需要记录并反向操作每一步，复杂度远超收益；且 LWW 合并的方向性保证「重试只会更接近正确状态，不会造成破坏」。

### 9.7 UI

**popup（工具栏点击）**
- 大号「立即同步」按钮
- 上次同步时间（相对时间 + 精确时间 tooltip）
- 本次结果摘要：新增 / 更新 / 删除 / 云端总计
- 冲突提示：有新冲突时显示「查看 N 条冲突」
- 设置 / 冲突列表 / 同步日志入口

**options（设置页）**
- 服务器地址（必填，校验 `https://` 前缀）
- 访问令牌（密码框，可显示/隐藏）
- 「测试连接」按钮 → `GET /api/health` → 显示结果与服务端 item 数
- 同步范围：三个根目录的开关（书签栏 / 菜单 / 其他书签），默认全开
- 危险操作区：「清空本地缓存并重新同步」

---

## 10. Docker Compose 部署

### 10.1 部署边界

**本项目不提供反代层。** TLS 证书签发与续期、HTTPS 终结、gzip、访问日志全部由用户已有的 **Nginx Proxy Manager（NPM）** 负责。

`bmsync` 的职责范围仅限：

- 监听一个明文 HTTP 端口（默认 `8080`）
- 提供 `/api/*` 接口
- 读写 `/data` 目录

**因此 `bmsync` 绝不应直接暴露到公网。** compose 中只绑定到 `127.0.0.1`，由 NPM 在同机访问。

### 10.2 目录结构

```
deploy/
├── docker-compose.yml
├── .env                     # BMSYNC_TOKEN（不入库）
├── data/                    # 数据卷（不入库）
└── ../bmsync/Dockerfile
```

### 10.3 docker-compose.yml

```yaml
services:
  bmsync:
    build: ../bmsync
    container_name: bmsync
    restart: unless-stopped
    environment:
      BMSYNC_TOKEN: ${BMSYNC_TOKEN:?请在 .env 中设置 BMSYNC_TOKEN}
      BMSYNC_DATA: /data
      BMSYNC_ADDR: ":8080"
    volumes:
      - ./data:/data
    # 只绑定回环地址，公网无法直连；NPM 通过宿主机 8080 反代
    ports:
      - "127.0.0.1:8080:8080"
    networks: [bmsync]
    read_only: true
    tmpfs: [/tmp]
    cap_drop: [ALL]
    security_opt: [no-new-privileges:true]
    healthcheck:
      # scratch 镜像无 shell / wget，改用二进制自带的子命令
      test: ["CMD", "/bmsync", "healthcheck"]
      interval: 30s
      timeout: 3s
      retries: 3
      start_period: 5s

networks:
  bmsync:
```

> `127.0.0.1` 绑定意味着只有**同一台机器**上的 NPM 能访问。
> 若 NPM 跑在另一台机器或另一个容器网络里，把绑定地址改成内网 IP（如 `192.168.1.10:8080:8080`），
> 或让两个容器加入同一个外部 network 后用 `bmsync:8080` 直连（此时可完全去掉 `ports`）。

### 10.4 Nginx Proxy Manager 配置要点

在 NPM 中为 `bmsync` 新建一个 Proxy Host：

| 字段 | 取值 |
|---|---|
| Domain Names | 你的域名，如 `bmsync.example.com` |
| Scheme | `http` |
| Forward Hostname / Port | `127.0.0.1` / `8080`（NPM 与 bmsync 同机时） |
| Block Exploits | 开 |
| Websockets Support | 关（本项目不用） |
| SSL | Let's Encrypt 证书 |
| Force SSL | 开 |
| HSTS | 建议开 |

**两个容易踩的坑：**

- **请求体大小**：nginx 的 `client_max_body_size` 默认 `1m`，**超过 1 MB 的 state 会被直接拒绝（413）**。
  书签多时务必在 NPM 的 Advanced 面板加入：
  ```nginx
  client_max_body_size 32m;
  ```
- **gzip**：NPM 默认开启 `gzip on`。若在 Advanced 面板改过配置，确认 `gzip_types` 至少包含
  `application/json`，否则 JSON 响应不会压缩（体积差约 5 倍）。

NPM 默认的访问日志只记录 URL，**不含请求体**，不需要额外处理。

### 10.5 Dockerfile（多阶段）

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/*/*.csproj  src/
COPY tests/*/*.csproj tests/
RUN dotnet restore
COPY . .
RUN dotnet test -c Release --nologo          # 先测试，再做最慢的 AOT 编译
RUN dotnet publish src/BookmarkSync.Cli -c Release -r linux-x64 \
        -p:PublishAot=true --output /out

FROM scratch
COPY --from=build /out/bmsync /bmsync
VOLUME ["/data"]
EXPOSE 8080
ENTRYPOINT ["/bmsync"]
```

`FROM scratch` + NativeAOT → 最终镜像约 **20–30 MB**（Go 版是 8–12 MB；
体积差来自 AOT 运行时本身，但换来了"不需要任何 `.so` / `libicu` / `libssl`"）。

> **为什么必须用 NativeAOT**：`scratch` 里没有任何系统库。普通
> self-contained 发布仍需要 `libcoreclr.so`、`libicu`、OpenSSL 等一堆
> 文件，得逐个 `COPY` 进去；AOT 把它们全编译进单个二进制。
>
> **AOT 不能交叉编译**，所以只能在 Linux 容器里做（Windows 上加
> `-p:PublishAot=true` 会报 `Cross-OS native compilation is not supported`）。
> 本地开发因此只验不带 AOT 的发布。
>
> **AOT 禁用反射式 JSON**，因此本项目用 System.Text.Json 的源生成器。
> 代价是所有 JSON 读写都走 `JsonTypeInfo<T>` 重载；收益是协议层的类型名单
> 写在代码里，且体积/启动都更快。

> **`scratch` 没有 shell，因此 healthcheck 必须用 exec 形式调用二进制自身。**
> 为此给二进制增加一个 `healthcheck` 子命令：向
> `http://127.0.0.1:<BMSYNC_ADDR>/api/health` 发 GET，校验响应里的
> `service` 字段是 `bmsync` 再退出。校验 body 是为了防一种坑：反代配错端口，
> 请求被路由到别的后端，那个后端也返回 200 —— 状态码过关但没人在服务。
>
> 备选方案：改用 `alpine` 基础镜像（约 +7 MB），healthcheck 写成
> `["CMD", "wget", "-qO-", "http://127.0.0.1:8080/api/health"]`。
> **默认采用 scratch + 二进制子命令方案**，exec 形式不依赖 shell，行为更确定。

### 10.6 部署前置条件

1. VPS 已安装 Docker + Docker Compose v2
2. 一个域名 A 记录指向 VPS IP
3. 80 / 443 端口开放（供 NPM 签发证书与对外服务）
4. Nginx Proxy Manager 中已配置好 Proxy Host
5. `.env` 中写入随机 token
6. **已在公司网络环境下实测该域名可达**（见 §12.6）

### 10.7 数据备份

`data/` 目录是全部状态所在，备份它即可（NPM 的证书目录另行备份）。

```bash
# deploy/backup.sh —— 由 crontab 每日调用
set -euo pipefail
cd "$(dirname "$0")"
STAMP=$(date -u +%Y%m%dT%H%M%SZ)
tar -czf "backup/data-$STAMP.tar.gz" data/
# 只保留最近 14 份
ls -1t backup/data-*.tar.gz | tail -n +15 | xargs -r rm --
```

---

## 11. 测试策略

### 11.1 服务端（`dotnet test`）

三个测试项目，与 `src/` 下的项目一一对应：

| 项目 | 测什么 |
|---|---|
| `BookmarkSync.Domain.Tests` | HLC、合并、GC、JSON 编码 —— 全部纯逻辑，不需要 HTTP 或磁盘 |
| `BookmarkSync.Store.Tests` | 原子写、快照、冲突缓冲、并发同步不丢更新 |
| `BookmarkSync.Server.Tests` | 四个端点、鉴权、限流、配置、healthcheck、进程入口 |

HTTP 层的测试**起真实的 Kestrel**（端口 0 由系统分配）而不是
TestServer：中间件顺序、限流在鉴权之前、Content-Type 这些恰恰是
"处理器本身完全正确、但管道装错了"的部分，单元测试结构上无法发现。
（比如 `AddSingleton(async sp => ...)` 被推断成 `Task<T>`，
所有处理器单测全绿，DI 里却根本没注册那个服务。）

主要测试项：

| 测试项 | 内容 |
|---|---|
| HLC 单调性 | 连续 now() 严格递增；update(remote) 后大于 remote；编码字典序 == 时间序 |
| HLC 因果性 | A 产生 t1 → B update(t1) → B 产生 t2，断言 t2 > t1 |
| HLC 跨端向量 | 逐条回放 `test/hlc_vectors.json`（Go 生成），证明 C# 实现与之一致 |
| HLC 时钟回拨 | 手动将物理时钟调回，`l` 不下降 |
| 合并·单边新增 | 服务端有 A，客户端有 B → 结果含 A、B |
| 合并·单边删除 | 客户端删 B → 服务端也被删 |
| 合并·两端改不同项 | A 改 x，B 改 y → 两个改动都保留 |
| 合并·两端改同一项 | LWW 决胜正确 |
| 合并·墓碑复活 | 客户端删 → 服务端删 → 客户端重新加 → 复活成功 |
| 合并·幂等 | merge(M, X) 结果再 merge 结果，状态不变 |
| 合并·乱序收敛 | A→B 与 B→A 两种顺序得到相同状态 |
| 合并·base 缺失 | 首次同步（base 为空）不误报冲突 |
| 合并·冲突检测 | 真并发编辑被记录，单边编辑不被记录 |
| 合并·随机化收敛 | 400 次随机操作后，以任意顺序重放必须收敛 |
| GC | 90 天前的墓碑被删，活跃项保留；`x=0` 的墓碑永不删 |
| 原子写 | 临时文件 + rename；Windows 上占用时指数退避重试 |
| 限额 | 超量请求返回 413；超限速返回 429 + `Retry-After` |

| 测试项 | 内容 |
|---|---|
| HLC 单调性 | 连续 now() 严格递增；update(remote) 后大于 remote；编码字典序 == 时间序 |
| HLC 因果性 | A 产生 t1 → B update(t1) → B 产生 t2，断言 t2 > t1 |
| HLC 时钟回拨 | 手动将物理时钟调回，`l` 不下降 |
| 合并·单边新增 | 服务端有 A，客户端有 B → 结果含 A、B |
| 合并·单边删除 | 客户端删 B → 服务端也被删 |
| 合并·两端改不同项 | A 改 x，B 改 y → 两个改动都保留 |
| 合并·两端改同一项 | LWW 决胜正确 |
| 合并·墓碑复活 | 客户端删 → 服务端删 → 客户端重新加 → 复活成功 |
| 合并·幂等 | merge(M, X) 结果再 merge 结果，状态不变 |
| 合并·base 缺失 | 首次同步（base 为空）不误报冲突 |
| 合并·冲突检测 | 真并发编辑被记录，单边编辑不被记录 |
| GC | 90 天前的墓碑被删，活跃项保留 |
| 原子写 | 写入过程中 kill 进程，state.json 仍可解析且为旧值或新值 |
| 限额 | 超量请求返回 422 |

### 11.2 扩展端（`node --test`，无需浏览器）

`lib/` 下所有纯函数模块设计为**依赖注入**：接受 `api` 对象而非直接调用 `browser.*`，从而可用 mock 在 Node 中测试。

| 测试项 | 内容 |
|---|---|
| key 派生稳定性 | 同一输入两次计算结果相同 |
| key 派生区分性 | URL / 标题差异导致 key 不同 |
| 书签移动 | key 不变，`p` 变化，被判定为 MODIFIED |
| 文件夹重命名 | 子树 key 全部变化，被判定为 DELETE + NEW |
| 根目录 sentinel | 顶层 item 的 `p` 为 `toolbar_____` 等字面量 |
| collect diff | 覆盖 新增/修改/移动/删除/复活 全部 5 种情形 |
| apply | 在 mock 树上验证创建/更新/移动/删除，含「父文件夹先于子项创建」的顺序约束 |
| 深度超限 | 构造 40 层嵌套，apply 正确报错而非栈溢出 |

### 11.3 手工端到端清单

在两台真实机器（或一个机器的两个 Firefox Profile）上：

- [ ] A 新增 3 个书签 → 同步 → B 同步 → B 出现全部 3 个
- [ ] A 删 1 个 → 同步 → B 同步 → B 消失
- [ ] A 删整个文件夹（含 20 个子书签）→ 同步 → B 同步 → B 整个文件夹消失
- [ ] A 改标题 → 同步 → B 同步 → B 标题更新
- [ ] A 移动书签到别的文件夹 → 同步 → B 同步 → B 位置一致
- [ ] A 离线新增 5 个，期间 B 在线新增 3 个 → A 后同步 → 两端最终都是 8 个
- [ ] A 连续点 3 次同步 → 书签数量不变（幂等）
- [ ] 服务端 token 改错 → A 同步 → 明确报错，本地书签零变化
- [ ] 断网 → A 同步 → 明确报错，本地书签零变化
- [ ] 制造时钟偏差（虚拟机改系统时间 ±1 小时）→ 交替同步 → 数据最终一致
- [ ] 故意让两端同时改同一书签标题 → 同步后查看冲突日志
- [ ] 书签标题含中文与 emoji → 同步后标题不乱码（验证 JSON 未被转义成 `\uXXXX`）
- [ ] 书签数超过 6000 条 → 同步不报 413（NPM 的 `client_max_body_size` 放行到位）

---

## 12. 风险与缓解

### 12.1 全量传输的体积

| 风险 | 缓解 |
|---|---|
| 书签极多（>20000）时 state.json 达数 MB | 依赖 NPM 的 gzip（JSON 约压缩至 1/5）；NPM 侧需放行 `client_max_body_size`（§10.4）；实测前不做分片 |

### 12.2 合并算法 bug 导致书签丢失

这是**最高等级的风险**——本项目是自研的同步逻辑。

| 缓解措施 |
|---|
| ① 每次成功写入前自动快照，保留 30 份 |
| ② 合并算法写**大量的**表驱动单元测试，覆盖 §11.1 全部场景 |
| ③ 服务端永不主动删除非墓碑项 |
| ④ 客户端在应用前记录一份 apply 计划到日志，出问题可回放排查 |
| ⑤ 首个版本在测试环境（假数据）先跑通再上真实书签 |

### 12.3 长期离线设备的数据复活

设备 A 离线超过 90 天，期间它删除的某个书签的墓碑已被服务端 GC。此时 A 恢复上线——若 A 的本地书签树中**仍存在**该书签，会被当作「新增」重新上传。

| 缓解 | 取舍 |
|---|---|
| 记录每台设备的 `lastSeenAt`，超过 N 天未上线则**拒绝其上传**，要求重新全量初始化 | 更安全，但多一份状态要维护 |
| 维持现状 | 依赖「90 天不登录」这个假设 |

**默认按现状实现**，并在 README 中说明该假设。

### 12.4 HLC 无法防住的极端情况

若设备 A 的时钟被**大幅调快**（如误设为 2035 年）并在此期间产生大量改动，其时间戳会压制其他所有设备的改动，直到真实时间追上 2035 年。

**缓解**：HLC 的 `l` 只增不减的特性意味着此问题不会因改回时钟而自愈，只能等。可在 popup 显示「本地时钟异常」告警（当前时间显著偏离 serverTime > 1 年）作为提示。**列为 v1.1 待办。**

### 12.5 Firefox API 变更

`bookmarks` API 属稳定 API，风险低。约束 `strict_min_version: 115`，并在该版本上开发测试。

### 12.6 公司网络限制

若公司网络拦截自建域名，扩展无法访问。**这是部署前必须实测的事项**，无法用技术手段绕过（也不应绕过）。

---

## 13. 待确认事项

| # | 事项 | 当前默认 | 状态 |
|---|---|---|---|
| Q1 | 冲突日志的**详细程度**：需要字段级 diff（哪个字段、两边分别是什么值）还是只记录 key + 胜方？ | 字段级 diff，见 §7.4 | **待沟通** |
| Q2 | 冲突日志的**可见范围**：存在服务端（任一设备可看）还是也缓存一份到本地？ | 仅服务端 | 待沟通 |
| Q3 | popup 摘要口径：展示「云端累计总数 + 本次变化」，还是只展示「本次变化」？ | 两者都展示 | 待沟通 |
| Q4 | 冲突时是否需要**用户介入的手动解决入口**（在 popup 里选择保留哪个版本）？ | 不做，纯 LWW | 待沟通 |
| Q5 | `mobile______`（移动设备书签）是否纳入同步？ | 默认排除 | 待沟通 |

---

## 14. 附录

### 14.1 关键术语

| 术语 | 含义 |
|---|---|
| **item** | 一条同步记录，对应一个书签或一个文件夹 |
| **key** | item 的身份标识，由内容哈希派生（§3） |
| **墓碑 (tombstone)** | `d == true` 的 item，表示"已删除"，用于把删除操作传播到其他设备 |
| **HLC** | 混合逻辑时钟，保证因果序的时间戳（§6） |
| **LWW** | Last-Write-Wins，最后写入者获胜的冲突解决策略 |
| **base** | 客户端上次从服务端收到的状态的精简表示（key→m），用于三方冲突检测 |
| **sentinel** | 四个系统根目录的固定 key 字面量（§3.4） |

### 14.2 关键不变量（实现时必须始终成立）

1. **key 一经计算，不可变**——它是身份，不是位置
2. **合并只比较 `m`**，`a` / `x` 永不参与决胜
3. **HLC 时间戳严格单调**，且 `compare(a,b) == strings.Compare(a,b)`
4. **非 200 响应绝不修改本地书签**
5. **服务端只通过 GC 删除 item，绝不因合并而删除活跃 item**
6. **客户端缓存永远等于服务端最近一次返回的 state**

### 14.3 参考

- HLC 原始论文：Kulkarni et al., *Logical Physical Clocks and Consistent Snapshots in Globally Distributed Databases*, 2014
- Firefox WebExtensions `bookmarks` API：https://developer.mozilla.org/docs/Mozilla/Add-ons/WebExtensions/API/bookmarks
- Firefox Manifest V3 差异：https://extensionworkshop.com/documentation/develop/developing-extensions-for-firefox-for-android/
