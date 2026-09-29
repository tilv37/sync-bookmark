# Bookmark Sync — 实施计划

> 状态：草案 v0.1，待评审
> 关联文档：[`design.md`](./design.md)
> 最后更新：2026-09-29（服务端已迁移到 .NET 10）

> ⚠️ **本文是历史记录，不要按里面的路径找文件。** P0–P5 各阶段的任务清单写的是
> **Go 版**的文件名（`bmsync/main.go`、`bmsync/merge.go` 等）。之后发生了两次
> 结构性变更：
>
> 1. **目录重构**（第 14 条）：Go 代码拆成 `cmd/bmsync` + `internal/{bookmarks,store,server}`。
> 2. **迁移到 .NET 10**（第 16 条）：服务端现在是 `bmsync/src/BookmarkSync.{Domain,Store,Server,Cli}`，
>    Go 实现原封不动保留在 `legacy-go/` 作为跨实现比对的参照。
>
> 当前目录结构见 [`README.md`](../README.md)；两次变更的原因分别记在
> [附录 D 第 14 条](#补充说明go-代码重构第-14-条) 与
> [第 16 条](#补充说明迁移到-net-10第-16-条)。

---

## 0. 计划说明

### 0.1 目标

把设计文档 §2 的架构分 7 个阶段落地。每个阶段有**独立可验证的产出**，前一阶段不验收通过不进入下一阶段。

### 0.2 核心原则

1. **合并算法先行。** 合并是整个项目风险最高、也最难手工验证的部分。先在 Go 端用纯函数 + 大量单测把它钉死，扩展和 UI 都往后放。
2. **每阶段都要有"能跑"的东西。** 不存在"先写完所有代码再联调"的阶段。
3. **先假数据，后真书签。** 前 5 个阶段全部使用构造的测试数据，真实书签在阶段 4 末端才接入。
4. **文档随代码走。** 合并算法、HLC 这些核心模块的 README 注释是必交付物，不是可选项。

### 0.3 阶段总览

| 阶段 | 名称 | 产出 | 规模 | 前置 | 状态 |
|---|---|---|---|---|---|
| P0 | 仓库骨架 | 可编译的空项目 | S | 设计评审通过 | ✅ 完成 |
| P1 | 核心算法（Go） | HLC + 合并器 + 单测通过 | **L** | P0 | ✅ 完成 |
| P2 | 服务端 HTTP 层 | curl 可跑通的完整 API | M | P1 | ✅ 完成 |
| P3 | 扩展数据层（纯 JS） | Node 中可测的采集/应用 | **L** | P1 | ⚠️ 代码完成，测试待跑 |
| P4 | 扩展集成 + UI | 真机双端可手动同步 | M | P2 + P3 | ⚠️ 代码完成，真机验证待做 |
| P5 | Docker Compose 部署 | 全新 VPS 一键起服务 | S | P2 | ⚠️ 文件就绪，未实机部署 |
| P6 | 文档与收尾 | README、运维手册、发布包 | S | P4 | ⚠️ README 完成，其余待做 |

关键路径：**P1 是瓶颈**（计划阶段的判断成立——它确实吃掉了最多的时间与返工）。

### 0.4 当前状态与阻塞项

`./test/all.sh` 五层全绿：**191 个测试**（扩展 90 + Go 101）+ 冒烟 28 项断言 + 一致性 13 项。
Go 覆盖率 84.4%，Linux 静态交叉编译通过。

| 阶段 | 状态 |
|---|---|
| P0–P3 | ✅ 完成（代码 + 测试） |
| P4 | ⚠️ UI 与消息层完成，**真机双端同步未验证** |
| P5 | ⚠️ Dockerfile 与 compose 已写好，未在真 VPS 上跑过 |
| P6 | ⚠️ README 完成；`docs/OPERATIONS.md` 未写 |

**剩余的验证缺口**（都是环境所限，不是代码问题）：

| 待办 | 缺什么 | 风险 |
|---|---|---|
| P4 真机双端同步 | 一台装了扩展的 Firefox | 中。`collect`/`apply` 已用 mock 覆盖，但 mock 毕竟不是 Firefox —— 尤其 `bookmarks.move` 不带 `index`、以及新建节点在树中的插入位置 |
| P5 部署 | Docker + 一台 VPS | 低。compose 与 Dockerfile 已就位；`FROM scratch` + `healthcheck` 子命令的组合值得真机验一次 |
| P6 运维文档 | — | 低 |
| **公司网络能否访问自建域名** | 一台公司电脑 | **高**。若被网络策略拦截，整个方案在公司侧不可用（design.md §12.6） |

> 补 Node 测试的过程里改掉了 3 个真 bug（见附录 D 第 10–12 条），
> 它们都"不会抛异常、只是让结果悄悄地不对"。真机验证的重点也应放在这类问题上。

### 0.5 开工前需确认的事项

- [ ] 设计文档 §13 的 Q1–Q5 五个待确认项（代码按默认值实现，改动面很小）
- [ ] 服务器域名（影响 Nginx Proxy Manager 的 Proxy Host 配置，P4 前必须有）
- [ ] 首次接入真实书签的时间点（建议放在 P4 全部验收通过之后）

---

## P0 — 仓库骨架

**规模 S｜预估：半天**

### 任务

- [ ] 建立目录结构
  ```
  sync-bookmark/
  ├── docs/            design.md, plan.md
  ├── bmsync/          Go 服务端
  ├── extension/       Firefox 扩展
  ├── deploy/          docker-compose.yml, .env, backup.sh
  ├── test/            跨端集成测试脚本、测试数据生成器
  └── README.md
  ```
- [ ] `bmsync/go.mod`（module `bmsync`，go 1.23，**不引入任何第三方依赖**）
- [ ] `.gitignore`：`deploy/.env`、`deploy/data/`、`*.exe`、`node_modules`（虽然不用 npm 但留作兜底）
- [ ] `bmsync/main.go` 最小可运行：监听 `:8080`，`GET /api/health` 返回 `{"ok":true}`
- [ ] `extension/manifest.json` 最小可版本，能被 Firefox 临时载入（注册 action + background）
- [ ] 确认本机工具链：`go version`、Firefox 版本 ≥ 115、`docker compose version`
- [ ] 初始化 git 仓库，首次 commit

### 产出物

- `go build ./...` 通过
- Firefox 能载入 `extension/manifest.json` 且工具栏出现图标
- `curl localhost:8080/api/health` 返回 200

### 验收标准

- [ ] 三条产出物全部满足
- [ ] `git status` 干净，`.env` 与 `data/` 未被跟踪

---

## P1 — 核心算法（Go）

**规模 L｜预估：这是最需要耐心的阶段**

> 本阶段**只写纯函数和单元测试，不写任何 HTTP、不碰文件 IO**。目的是让合并算法在没有外部依赖干扰的情况下被彻底验证。

### P1.1 类型与 Schema

- [ ] `state.go`
  - `type Item struct { P, T, N, U, A, M string; D bool; X int64 }`
  - JSON tag 用短名 `p/t/n/u/a/m/d/x`
  - `type State struct { V int; HLC string; Items map[string]Item }`
  - `func (s State) Validate(limits Limits) error`
    - 校验：`v == 1`；key 长度为 32 且为 hex；`t ∈ {b, f}`；`f` 不带 `u`；`b` 必须带 `u`；`m`/`a` 可被 HLC 解析；树深度 ≤ 32；items 数 ≤ 50000
  - `func sameContent(a, b Item) bool` — 只比较 `(P, T, N, U)`
  - `func depthOf(items map[string]Item, key string) (int, bool)` — 用于限额校验

- [ ] `limits.go` — 集中管理各项限额常量，便于调整

### P1.2 HLC

- [ ] `hlc.go`
  - `type HLC struct { mu sync.Mutex; l uint64; c uint32 }`
  - `func (h *HLC) Now() string`
  - `func (h *HLC) Update(remote string) string`
  - `func encode(l uint64, c uint32) string` / `func decode(s string) (uint64, uint32, error)`
  - 计数溢出处理：`c > 99999` → `l++; c = 0`
  - `func Compare(a, b string) int` — 内部走 `strings.Compare`（定宽编码保证等价）
  - `func Max(a, b string) string`
  - `func MaxOfState(items map[string]Item) string` — 遍历取所有 `m`/`a` 的最大值

- [ ] `hlc_test.go`（**至少 12 个用例**）
  - 连续 `Now()` 严格单调递增
  - 本地时钟远超逻辑时钟时 `c` 归零
  - `Update(remote)` 后本地时间戳 > remote
  - 因果性：A 产生 t1 → B `Update(t1)` → B 产生 t2，断言 `t2 > t1`
  - 物理时钟回拨到过去，`l` 不下降
  - 物理时钟回拨后产生的时间戳仍 > 回拨前
  - 编码格式匹配 `^\d{13}-\d{5}$`
  - 编码的字典序与时间序等价（随机 1000 组交叉验证）
  - 计数溢出边界（`c = 99998, 99999, 100000`）
  - `Compare` 对相等/大小/非法输入的行为
  - `MaxOfState` 在空 map、单元素、多元素下的行为
  - **双端一致性测试**：写一个表驱动的"固定输入序列"，Go 与 JS 各自跑一遍，输出必须逐字节相同（见 P3.4）

### P1.3 合并算法 ★

- [ ] `merge.go`
  - `type Conflict struct { At int64; Device, Key, Reason, Field, Winner, Loser, WinnerValue, LoserValue, URL string }`
  - `func Merge(server, incoming State, base map[string]string, device string, now int64) (State, []Conflict, Summary)`
  - `type Summary struct { Created, Updated, Deleted, Unchanged int }`
  - 严格按 design.md §5.2 伪代码实现
  - 冲突字段级 diff：从 `(P, T, N, U)` 中逐字段找出差异，生成 `Field/WinnerValue/LoserValue`；有多个字段差异则产出多条 conflict
  - 保护性约束（写成断言或注释明确的 invariant）：
    - 合并结果的 items 集合 == 两边 items 的并集，**不得出现第三种来源**
    - 任何 `D == false` 的 item 绝不出现在结果之外

- [ ] `merge_test.go`（**至少 20 个用例，全部表驱动**）

  **基础合并**
  - 服务端为空 + 客户端有 → 全部接收
  - 客户端为空 + 服务端有 → 全部保留
  - 两端各增不同项 → 合并后两端项都在
  - 客户端项比服务端新 → 客户端赢
  - 服务端项比客户端新 → 服务端赢
  - `m` 相同且内容相同 → 保留
  - `m` 相同但内容不同 → 字典序兜底 + 记 `hlc_collision` 冲突

  **删除与墓碑**
  - 客户端墓碑（`m` 新）覆盖服务端活跃项 → 删除传播
  - 客户端墓碑（`m` 旧）不覆盖服务端已修改项 → 保持
  - 墓碑 + 墓碑 → 保留 `m` 更新的，保留较早的 `x`（GC 依据）
  - **复活**：服务端墓碑，客户端同 key 重新新增且 `m` 更新 → `D=false`
  - 墓碑 91 天前的会被 GC、89 天前的保留（GC 边界）

  **幂等与顺序无关**
  - `Merge(Merge(S, A), A) == Merge(S, A)`（**重复同步幂等**）
  - `Merge(S, A)` 后 `Merge(S, B)` 的结果 == `Merge(Merge(S,A), B)`
  - 交换 A/B 上送顺序后结果一致（**乱序同步收敛**）

  **冲突检测**
  - 首次同步（`base` 为空）→ **零冲突**（防噪声）
  - 单边编辑（客户端改，服务端没动）→ 零冲突
  - 真并发编辑（两端都改同一项且内容不同）→ 恰好 1+ 条冲突
  - 冲突记录了正确的 `Winner` / `Loser` / 字段值
  - 冲突不改变决胜结果（纯观测，不影响 merge outcome）

  **Summary 统计**
  - `Created` / `Updated` / `Deleted` / `Unchanged` 在各场景下的准确值

- [ ] `gc.go` + `gc_test.go`
  - `func GC(items map[string]Item, now int64, ttl time.Duration) (map[string]Item, int)`
  - 断言：只删 `D==true` 且 `x < cutoff`；`x == 0` 的活跃项绝不被删；空 map 安全

### P1.4 属性测试（可选但强烈建议）

- [ ] 引入 `testing/quick`（标准库）做随机化验证
  - 生成随机 state 对，随机顺序 merge 1000 轮，断言**最终一定收敛**（任一顺序的 Merge 链结果相同）
  - 这是对"乱序同步收敛"最强的验证，比手写用例更能抓 bug

### 产出物

- `go test ./... -v` 全部通过
- `go test -race ./...` 无竞态
- 覆盖率报告（HLC + merge + gc 合计 ≥ 85%）
- 核心模块的 doc comment 完整

### 验收标准

- [ ] 全部单测通过，无 skipped
- [ ] `Merge` 的三个"并集不变量"有对应的显式断言
- [ ] 幂等性、乱序收敛性、可检出真并发冲突 —— 三条均有独立用例覆盖
- [ ] 代码中**没有任何** HTTP、文件 IO、CLI 相关代码

> **这是全项目最关键的验收关口。若 P1 合并算法没有足够信心，不应进入 P2/P3。**

---

## P2 — 服务端 HTTP 层

**规模 M｜预估**

> 本阶段与 P3 可并行。

### 任务

- [ ] `config.go` — 从环境变量读取 `BMSYNC_ADDR` / `BMSYNC_TOKEN` / `BMSYNC_DATA` / `BMSYNC_HISTORY_KEEP` / `BMSYNC_TOMBSTONE_TTL` / `BMSYNC_TLS_TTL`，启动时校验
- [ ] `auth.go` — `crypto/subtle.ConstantTimeCompare` 比对 token；401 响应
- [ ] `store.go`
  - `type Store struct { mu sync.Mutex; dir string; state State; ... }`
  - `Load()` — 启动时读 `state.json`；schema 不匹配则**拒绝启动并明确报错**
  - `save(state)` — 原子写（tmp → fsync → rename → fsync dir）
  - `Sync(incoming, base, device)` — 临界区内完成「读 → merge → GC → 写」，临界区外做快照
- [ ] `snapshot.go` — 写前快照到 `history/`，保留最近 N 份；**失败不阻断同步**
- [ ] `conflict.go` — 环形缓冲追加（上限 500），落盘 `conflicts.json`；读取接口
- [ ] `main.go` 路由
  - `POST /api/sync`（请求体上限 16 MB）
  - `GET /api/health`
  - `GET /api/conflicts?limit=50`
  - `GET /api/history`
  - 优雅关闭（SIGTERM → 等待进行中的请求完成）
- [x] 限流：按 IP **60 次/分钟**。手写令牌桶（`ratelimit.go`），不用
      `golang.org/x/time/rate`，保持零第三方依赖。
      **不要用 10 次/分钟** —— 这是手动触发的工具，一次正常会话就有
      首次同步 + 拉取 + 补同步 + 失败重试 + 拉冲突列表，10 次很快用光，
      之后一分钟内全被 429。这是 P4 冒烟测试实测出来的（见附录 D）。
- [ ] 结构化日志（标准库 `log/slog`），**不记录 body 与 token**
- [ ] `healthcheck` 子命令 —— `bmsync healthcheck` 自请求 `/api/health`，200 → `exit 0`。
      存在的理由：`FROM scratch` 镜像无 shell，docker healthcheck 只能用 exec 形式（design.md §10.5）
- [ ] 单元测试补充：`store_test.go`（原子写、并发写）、`auth_test.go`、`main_test.go`（各端点 httptest）

### 测试脚本

> 实际实现里 `test/smoke.sh` 扩到了 28 项断言、9 个分组，比计划多。原因是
> 单元测试走 `httptest` 内存服务器，**不会暴露序列化层的 bug**；冒烟走真实
> socket + 真实落盘，能验证「JSON 编解码 + 鉴权 + 限流 + 原子写 + 快照」
> 这条完整链路。另外还多了个 `test/jsonq/` —— 冒烟脚本要读 JSON，但测试机上
> 不一定有 jq 或 python，Go 一定有。

- [x] `test/smoke.sh` — 纯 HTTP 驱动真实服务端
  - 鉴权：health 免鉴权、错误 token 401
  - 收敛：A 上传 5 项 → B 空白拉取拿到 5 项
  - 删除传播：B 删 2 项 → A 看到 2 墓碑 + 3 活跃
  - 幂等：同一请求连发 3 次结果稳定
  - 冲突：两端基于同一基线改同一项 → `concurrent_edit` + 字段定位
  - 畸形输入：schema 不符 / 非法 key / 坏 JSON / 未知字段
  - 快照、限流、落盘文件可解析、无临时文件残留
- [x] `test/all.sh` — 一条命令跑全部四层检查
- [x] `test/check-extension.sh` — 扩展一致性（不需要 Node）
- [x] `test/jsonq/` — 冒烟用的迷你 JSON 读取器

### 验收标准

- [x] `go test ./...` 通过（100 个用例）
- [x] `test/smoke.sh` 全部断言通过（28 项）
- [x] `kill -9` 后 `state.json` 仍可解析（原子性）
- [x] 连续 100 次并发 `POST /api/sync` 无丢失更新
- [x] 覆盖率 ≥ 80%（实际 84.4%）
- [x] Linux 静态交叉编译通过（10.4 MB）

---

## P3 — 扩展数据层（纯 JS）

**规模 L｜预估**

> 与 P2 并行。本阶段**不写 manifest、不写 UI、不碰 `browser.*` 全局对象**，全部逻辑在 Node 中可测。

### P3.1 模块骨架

- [ ] `extension/lib/hlc.js` — 逐字对应 P1.2 的 Go 实现
> **实际实现与计划有出入，理由记在下面**：`diff.js` 独立模块被取消了 ——
> `sameContent` 的实现必须与 Go 端 `sameContent` **逐字对应**（都只比较 `P/T/N/U`，
> 都不比较时间戳），拆成两个文件各写一遍反而更容易走偏。现在它是
> `collect.js` 里的 `contentChanged()`，`check-extension.sh` 会检查它还在。
> 另外新增了计划里没有的 `sync.js`（一次同步的编排）和 `client.js` 的权限申请逻辑。

- [x] `extension/lib/hlc.js` — 逐字对应 P1.2 的 Go 实现
- [x] `extension/lib/keys.js`
  - `deriveKey(type, parts)` / `deriveKeys(inputs)`（批量版，逐层批量哈希）
  - 书签 `SHA-256("u:" + url)`；文件夹 `SHA-256("f:" + parentKey + NUL + title)`
  - `ROOT_TOOLBAR` / `ROOT_MENU` / `ROOT_UNFILED` / `ROOT_MOBILE` 四个常量
  - `resolveRoots(children)` — 按 id 匹配，不符则按下标兜底并置 `warned`
  - `isRootEnabled` / `isValidKey`
- [x] `extension/lib/collect.js`
  - `scan(api, opts)` — 拍平 + 按层批量算 key（依赖注入，不碰 `browser.*`）
  - `buildState(scanResult, cached, clock, nowMs)` — 实现 §5.1 的六分支判定
  - `contentChanged(prev, node)` — 只比较 `P/T/N/U`
- [x] `extension/lib/apply.js`
  - `planApply(liveNodes, incomingState, ffKeyToId)` → 指令列表（可单测）
  - `executePlan(api, plan, opts)` — 先删后建、按深度升序、类型变更走删建
  - 父目录缺失时**记入 skipped 而不是丢到根目录**（静默挪位置比报错更难排查）
- [x] `extension/lib/store.js` — IndexedDB 薄封装（单 store `kv`）
- [x] `extension/lib/client.js` — 超时、错误码映射、host 权限申请
- [x] `extension/lib/sync.js` — 一次同步的完整编排（计划外新增）

### P3.2 HLC 跨端一致性测试 ★

- [x] `test/hlc_vectors.json` — Go 侧 `TestHLCExportVectors` 生成的 15 个向量
- [ ] `extension/lib/hlc.test.js` — **待写**，读向量逐条断言
- [ ] 把它加进 `test/all.sh`（有 Node 时自动跑，无 Node 时明确跳过）

> 这保证两端 HLC **逐字节一致**。不一致会导致合并在 `m` 相同的分支上非确定。
> `check-extension.sh` 目前只比对两端的位宽常量（13/5）——那只能挡住最粗的
> 抄错，挡不住分支顺序写反这类问题。**必须补上向量测试才算真的验过。**

### P3.3 单元测试（`node --test`，零依赖）

- [ ] `keys.test.js`
  - 同一输入两次计算结果相同（稳定性）
  - URL / 标题的细微差异导致 key 不同（区分性）
  - 书签 key **不含 parentKey**（移动场景的前提）
  - 标题含 `\x00` 或 `|` 不产生 key 歧义
  - 根目录 sentinel 解析
- [ ] `collect.test.js`（用 mock 树）
  - 新增 / 修改标题 / 修改 URL / 移动 / 删除 / 复活 —— **六种情形逐一覆盖**
  - 未变化项沿用 cached 的 `m`（不重打时间戳）
  - 删除文件夹时，子树**所有** key 都写入墓碑
  - 根目录 sentinel 不作为 item 出现在 state 中
  - `mobile` 排除开关生效
  - 40 层嵌套正确报错而非栈溢出
- [ ] `apply.test.js`
  - 创建、更新、移动、删除
  - **顺序约束**：父文件夹的创建指令严格早于其子项
  - 删除文件夹只发出一次 `remove`（利用 Firefox 递归语义）
  - 目标父目录不存在时的处理（应被 create 阶段先建好）
  - 空 diff → 不产生任何 API 调用
- [ ] `roundtrip.test.js` — **collect → apply 的往返一致性**：在 mock 树上 collect 出的 state，apply 到另一棵空树后，再 collect 应得到**语义等价**的 state

### 产出物

- `node --test` 全绿
- `lib/` 下所有模块无 `browser.*` 硬依赖（全部注入）

### 验收标准

- [ ] `node --test` 全绿，无 skipped
- [ ] HLC 向量测试通过（跨端逐字节一致）
- [ ] collect/apply 的六种基本情形全部有独立用例
- [ ] 往返一致性测试通过

---

## P4 — 扩展集成 + UI

**规模 M｜预估**

### P4.1 集成

- [ ] `manifest.json` 定稿（严格按 design.md §9.3）
- [ ] `background.js`
  - 消息路由：`sync` / `getState` / `testConnection` / `getConflicts`
  - badge 状态机：空闲 / 同步中 / 成功(绿) / 失败(红)，3 秒后恢复
  - 同步互斥：进行中忽略重复触发
  - 启动时 `HLC.update()` 用本地缓存里的最大 `m`
- [ ] 权限授予引导：检测 `host_permissions` 未被授予时，popup 显示明确引导文案与"重新请求"按钮
- [ ] 错误处理：严格遵守 design.md §9.6 的四条规则

### P4.2 UI

- [ ] `popup/` — 同步按钮、上次同步时间、结果摘要、冲突入口、日志折叠区
- [ ] `options/` — 服务器地址、token（显隐切换）、测试连接、三个根目录开关、清空缓存
- [ ] `icons/` — 16/32/48/128 四档
- [ ] 样式：跟随 Firefox 主题（`prefers-color-scheme`）

### P4.3 端到端验证

- [ ] 按 design.md §11.3 的**完整手工清单**逐条执行
- [ ] 双 Profile 同机测试（Profile A / Profile B），先于真机测试
- [ ] 全部通过后，才在个人 PC 上接入真实书签
- [ ] 真实书签接入后，至少完整跑一轮"备份 → 清空 → 还原"演练

### 验收标准

- [ ] §11.3 全部 12 条手工用例通过
- [ ] `about:debugging` 下无任何报错/警告
- [ ] 关闭浏览器重开后设置与缓存仍在
- [ ] 一次真实的"清空后还原"演练成功，且书签数量与标题完全一致

---

## P5 — Docker Compose 部署

**规模 S｜预估**

### 任务

- [ ] `bmsync/Dockerfile`（多阶段，scratch 基础镜像 + 二进制自带的 `healthcheck` 子命令 —— 见 design.md §10.5）
- [ ] `bmsync` 增加 `healthcheck` 子命令（P2 任务中一并实现，此处仅补测）
- [ ] `deploy/docker-compose.yml`（严格按 §10.3，端口绑定 `127.0.0.1:8080:8080`）
- [ ] `deploy/.env.example`，`.env` 加入 `.gitignore`
- [ ] `deploy/backup.sh` — 每日 cron 打包 `data/`，保留最近 14 份
- [ ] 在 NPM 中配置 Proxy Host（域名、Let's Encrypt、`client_max_body_size 32m`、gzip on）
- [ ] 在 VPS 上从零部署演练

### 验收标准

- [ ] 全新 VPS + `docker compose up -d` + NPM 建好 Proxy Host 后，`https://<域名>/api/health` 返回 200
- [ ] `http://<VPS公网IP>:8080/api/health` **无法访问**（确认只绑定了回环地址）
- [ ] 容器重启后数据完好
- [ ] 镜像体积 ≤ 20 MB，`docker inspect` 显示 healthcheck 为 `healthy`
- [ ] 浏览器上 `https://<域名>` 显示有效证书
- [ ] 构造一个约 1.5 MB 的 state 走完整同步，确认 **未被 nginx 以 413 拒绝**（`client_max_body_size` 验证）
- [ ] 公司 PC 上的 Firefox 能正常访问该域名（**design.md §12.6 的实测**）

---

## P6 — 文档与收尾

**规模 S｜预估**

### 任务

- [ ] `README.md`
  - 这是什么 / 30 秒上手
  - 前置条件（VPS、域名、Go ≥1.23、Firefox ≥115、Docker）
  - 部署步骤
  - 安装扩展步骤
  - 常见问题
- [ ] `docs/OPERATIONS.md` — 运维手册
  - 备份与恢复（含**从 `history/` 快照手动回滚**的操作步骤）
  - token 轮换流程
  - 日志排查
  - 数据量监控
- [ ] `docs/DATA-FORMAT.md` — state schema 规范（供未来迁移/工具使用）
- [ ] 发布包：`bmsync` 二进制 + `extension.xpi`（打包脚本）
- [ ] GitHub 仓库整理：README、Issues 模板、MIT/许可证声明

### 验收标准

- [ ] 一个**没参与过开发的人**能照 README 从零部署成功
- [ ] 快照回滚流程在真实环境演练过一次

---

## 附录 A — 里程碑演示点

| 里程碑 | 位置 | 演示内容 |
|---|---|---|
| **M1** | P1 结束 | `go test` 全绿，现场展示幂等性与乱序收敛的测试输出 |
| **M2** | P2 结束 | 终端里 `curl` 完成两客户端交替同步，`state.json` 内容肉眼可见 |
| **M3** | P3 结束 | `node --test` 全绿，现场展示往返一致性测试 |
| **M4** | P4 结束 | **两个 Firefox Profile 真实同步，含删除文件夹与恢复演练** |
| **M5** | P5 结束 | 浏览器访问 HTTPS 端点，扩展连上真实 VPS |
| **M6** | P6 结束 | 交付物齐备 |

---

## 附录 B — 风险登记

| # | 风险 | 影响 | 概率 | 应对 | 归属阶段 |
|---|---|---|---|---|---|
| R1 | 合并算法存在未覆盖的边界 bug，**静默丢书签** | 严重 | 中 | P1 穷举单测 + 属性测试 + 每次写前快照 + P4 真实数据演练 | P1 / P4 |
| R2 | Go 与 JS 的 HLC 实现不一致 | 中 | 低 | P3.2 跨端向量测试 | P3 |
| R3 | Firefox 书签树深度超限导致 apply 失败 | 低 | 低 | 深度限制 32 + 明确错误提示 | P3 |
| R4 | 公司网络拦截自建域名，工具不可用 | 严重 | 中 | **P5 阶段必须实测**，不可等到最后 | P5 |
| R5 | 无 HLC 时钟漂移导致覆盖 | 严重 | 低 | 已用 HLC 解决；另加长期离线假设（§12.3） | P1 |
| R6 | 需求蔓延（想加 tags / 排序 / 密码） | 中 | 高 | 非目标清单写进 design.md §1.3，评审时逐条对照 | 全程 |
| R7 | 长期离线设备导致数据复活 | 低 | 低 | 接受现状，README 写明假设 | P6 |
| R8 | VPS 故障 / 域名到期 | 严重 | 低 | 每日备份 + `history/` 快照 | P5 / P6 |

---

## 附录 C — 每个阶段的"完成"定义（Definition of Done）

一个阶段被认为完成，必须同时满足：

1. 该阶段全部**验收标准**打勾
2. 所有测试通过，且**没有被 skip 或注释掉**的用例
3. `git status` 干净，提交信息说明改了什么、为什么
4. 新增的核心模块有文档注释，说明**为什么这样设计**（不是复述代码在做什么）
5. 严重度高的已知问题已记录在案，而不是默默跳过

---

## 附录 D — 变更记录

| 日期 | 版本 | 变更 |
|---|---|---|
| 2026-09-28 | v0.1 | 初稿。经讨论确定：HLC、90 天墓碑 GC、Docker Compose 部署、全手写零构建。冲突日志细节待沟通。 |
| 2026-09-28 | v0.2 | 反代层从 Caddy 改为**由用户已有的 Nginx Proxy Manager 承担**。端口改绑 `127.0.0.1`（公网直连不通），新增 `client_max_body_size 32m` 的坑，`FROM scratch` 无 shell 故 healthcheck 改为调用二进制子命令。 |
| 2026-09-28 | v1.0 | P0–P3 实现完成。实施过程中发现并修复的实质问题见下表。 |

### 实施期间发现的实质问题

| # | 问题 | 影响 | 处置 |
|---|---|---|---|
| 1 | **HLC `update` 的分支顺序错误**：第三条写成 `max == p → c = 0`，应为 `max == rl → c = rc + 1` | 严重。物理时钟恰好追平远端毫秒时，产生的时间戳**小于**已收到的远端时间戳 → 因果性被破坏 → 两端交替获胜，书签随机丢失，且用户完全无感 | 已修。已固化回归测试 `TestHLCCausalCatchupWhenRemoteLeadsWithinSameMillisecond`，并在 design.md §6.2 记录了反例与分支顺序不可交换的原因 |
| 2 | **Windows 上 `rename` 偶发 ACCESS_DENIED** | 中。100 次并发写的测试稳定复现"丢一个更新"。线上表现为"偶尔同步失败，重试就好" | 加指数退避重试。平台差异拆到 `transient_windows.go` / `transient_unix.go` —— Windows 认 ACCESS_DENIED/SHARING_VIOLATION，Linux 只认 ESTALE |
| 3 | **限流 10 次/分钟过紧** | 中。手动触发场景下一次正常会话就耗光，之后一分钟全被 429 | 放宽到 60/min，并在 `limits.go` 写明为什么不能用个位数 |
| 4 | **`syscall.ERROR_SHARING_VIOLATION` 在 Linux 不存在** | 阻塞 Docker 构建 | 平台特定文件拆分 |
| 5 | **冒烟脚本 `c=$(api ...)` 让函数跑在子 shell** | 阻塞。`BODY` 随子 shell 消失，所有 JSON 查询返回空串，28 项断言里 22 项假失败 | 改为全局变量回传 `$CODE` / `$BODY` |
| 6 | **冒烟脚本路径基于 CWD 解析** | 阻塞。`bash test/smoke.sh` 与 `cd test && bash smoke.sh` 指向不同二进制 | 改为基于 `$0` 自身目录 |
| 7 | **`check-extension.sh` 字符类不含 `-`** | 假绿灯。id 大多是 `first-run` 这类带连字符的，匹配不到却被判为"通过" | 修正字符类，并用变异测试（注入 5 个不存在的 id）确认能全部抓到 |
| 8 | **jsonq 只在文件缺失时才编译** | 假绿灯。改了 `main.go` 之后脚本仍用旧二进制，报错完全指不到原因 | 改成每次都重建 |
| 9 | **设计文档里混入 NUL 字节** | 低。写 `\x00` 时落进了真实 NUL，导致文件被误判为二进制、无法 grep | 已替换为文字描述，并在 `keys.js` 里注明用 `String.fromCharCode(0)` 而非字面量的原因 |
| 10 | **`collect.js` 把四个系统根目录当成 item** | 严重。会给云端和本地各凭空多出 4 个空文件夹 | 已修。根目录只作为顶层项的 `p` 值，不进 items |
| 11 | **`apply.js` 查不到"本地已有但本次不变"的父目录** | 严重。书签移动到这类目录时静默失败，move 报"父目录不存在"但同步报告成功 | 已修。`planApply` 现在返回 `ffIdByKey`（全部已存在节点的 id） |
| 12 | **`collect.js` 用了未 import 的 `ROOT_MOBILE`** | 阻塞。一调用 `scan()` 就抛 `is not defined` | 已修。`check-extension.sh` 只能查 import 路径是否存在，查不出漏掉的符号——这属于"必须真跑一遍"的盲区 |
| 13 | **HLC 向量生成器自身有 bug** | 高（诊断误导）。`step` 闭包里无条件多调一次 `now()`，把错误状态固化进向量文件。症状表现为"Go 与 JS 实现不一致"，指向完全错误的方向 | 已修生成器，并加了独立的回放自检 `TestTraceVectors` —— 抓"生成器自己验证自己"这类盲区 |
| 14 | **Go 代码 20+ 文件平铺在一个目录** | 中（可维护性）。`merge.go` 这个全项目最该被精读的算法，和 `ratelimit.go`、`transient_windows.go` 混在一起；`package main` 意味着领域代码无法作为库被 import 或单独测试；改动 `api.go` 之类的基础设施文件时无从判断影响面 | 拆成 `cmd/bmsync` + `internal/{bookmarks,store,server}` 三层，依赖单向。顺带修掉两处被平铺掩盖的问题：`config_test.go` 里混着 store 的 `renameWithRetry` 测试和 healthcheck 测试；`RunHealthcheck` 原本在 `package main` 里，只能靠起真进程测。详见下方补充说明 |
| 15 | **HLC 向量导出用 `MkdirAll`，路径写错时静默成功** | 高（假绿灯）。包目录移动后 `TestHLCExportVectors` 把向量写到了新造的 `bmsync/internal/test/` 里，而 Go 的 `TestTraceVectors` 和 JS 侧读的仍是仓库根那份旧文件。**两边全绿，但跨端验证从未真的发生** —— 和第 13 条同类的"验证器自己骗自己" | 去掉 `MkdirAll`，目标目录不存在时直接 `t.Fatal`（路径错必须是硬错误，不能靠建目录"解决"）；写入后立刻回读比对。已用两次变异测试确认能拦住：路径退回旧写法、路径指向不存在的更深目录，都明确报错 |
| 16 | **服务端从 Go 迁移到 .NET 10** | 中（风险）。`FROM scratch` 镜像要求产物不依赖任何系统库，而 .NET 的 self-contained 发布仍需要 `libcoreclr.so` / `libicu` / OpenSSL —— 只有 NativeAOT 能编进单个二进制 | AOT 禁用反射式 JSON（`Reflection-based serialization has been disabled`），改用 System.Text.Json 源生成器。同时发现 AOT **不能交叉编译**（Windows 上报 `Cross-OS native compilation is not supported`），所以 AOT 只能由 Dockerfile 在 Linux 容器里做，本地只验 IL 发布。Go 实现保留在 `legacy-go/`，`test/hlc_vectors.json` 仍由它生成，C# 侧逐条回放以证明两套实现字节等价 |

#### 补充说明：Go 代码重构（第 14 条）

按依赖方向切成三层，理由是**让 `merge.go` 不再被基础设施淹没**：

```
cmd/bmsync  →  server  →  store  →  bookmarks
```

`bookmarks` 层（`state.go` / `merge.go` / `hlc.go` / `gc.go`）不 import 任何
外部包，也完全不引用 store / server —— 合并算法因此能在不启动 HTTP 的情况
下被完整测试（该包覆盖率 94.9%，绝大部分是合并路径）。

过程中值得记下的三件事：

1. **`store` 原本依赖 `server.Config` 和 `server.SnapshotInfo`**，是反向依赖。
   处置是给 `store` 一个自己的最小 `Config`（3 个字段），
   `SnapshotInfo` 移入 `store`（它描述的是磁盘上那个文件，属于存储层事实）。
2. **测试脚手架必须各包复制一份**。Go 不支持跨包共享 `_test.go`，且
   `store.Config` 与 `server.Config` 本来就是两个类型。重复是标准做法，
   不值得为了消除重复把测试辅助代码塞进生产包。
3. **两处统计脚本因目录变化而静默失效**：`all.sh` 的用例计数用
   `bmsync/*_test.go` 硬编码 glob，移动后匹配到 0 个却仍报"通过"（实际是
   0 用例）；覆盖率取 `min` 时把无测试文件的 `cmd/bmsync` 的
   `coverage: 0.0%` 算了进去，永远显示 0%。**假绿灯比红灯更危险**——
   修法不只是改路径，而是让脚本在这两种情况下显式报错。

#### 补充说明：迁移到 .NET 10（第 16 条）

Go 代码原封不动挪到 `legacy-go/`，不参与构建与部署，但仍可单独编译。
`test/hlc_vectors.json` 继续由它生成，C# 侧逐条回放 —— 这是"两套实现
字节等价"最直接的证据。

迁移过程中真正花时间的三件事，都不是翻译语法，而是**跨语言/跨框架的
隐式行为差异**：

1. **JSON 的 `omitempty` 在 C# 里不成立**。`JsonIgnoreCondition.WhenWritingDefault`
   对 `string` 判断的是 `null`，而 `""` 不是 null，所以照样写出 `"u":""`；
   Go 的 `omitempty` 判断 `== ""`，会省略。已用最小复现程序确认，不靠记忆。
   处置是手写 `ItemJsonConverter` / `StateJsonConverter`（同时解决 key 排序：
   Go 序列化 map 会排序，.NET 的 Dictionary 按插入顺序）。

2. **非 ASCII 转义会让体积涨 6 倍**。.NET 默认把中文写成 `中`，
   Go 原样输出 UTF-8。书签标题大量含中文，默认设置会让 5000 条书签的请求体
   从约 800KB 涨到 4MB，直接撞上 nginx 的 `client_max_body_size`。
   处置是全局用 `UnsafeRelaxedJsonEscaping`。
   实测确认它仍会转义非 BMP 字符（emoji），而
   `JavaScriptEncoder.Create(UnicodeRanges.All)` 更糟（把 `& < > "` 也转义）。
   这处**已知且刻意接受**的分歧记在 `JsonParityTests` 里，由测试钉住现状。

3. **源生成会绕过自定义转换器**。为满足 AOT 用了
   `JsonSerializerContext` 之后，源生成器会为 `State` 生成自己的元数据，
   绕过上面那个负责 key 排序与 omitempty 的转换器。症状是
   "state.json 顺序乱了、体积大了" —— 编译通过、测试通过、接口照常 200，
   没有任何报错。唯一能发现它的方式是拿两份输出逐字节对比。

### 补上 Node 测试后又发现

第 10–12 条是"开发机没有 Node"时完全看不见的。它们都不会抛异常、不会让程序崩，
只是让结果悄悄地不对：

- 10 会让两端凭空多出 4 个空文件夹
- 11 会让书签移动静默失败
- 12 一调用就崩（最良性的一种）

共 90 个 JS 用例覆盖 `hlc` / `keys` / `collect` / `apply`，
其中 `apply.test.js` 的**往返一致性**用例（采集→应用→再采集，结果语义等价）
是组合起来最强的一条性质。

### 尚待人工确认（design.md §13）

Q1–Q5 未变：冲突日志详细程度、可见范围、popup 摘要口径、是否要手动解决冲突的入口、移动设备书签是否纳入同步。代码里按 design.md 记的默认值实现，改动面很小。
