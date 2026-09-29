# bmsync

个人书签同步工具。Firefox 扩展 + 自建服务端，手动触发的双向合并，**删除会同步**。

为"个人电脑用 Firefox 同步书签，公司电脑不能登录个人账号"这个具体场景而做。

> **要开始真机调试 → 先读 [`HANDOVER.md`](HANDOVER.md)。**
> 里面有环境陷阱（这台开发机的 PATH 就有 5 个坑）、12 项验证清单、排查手册。

---

## 它解决什么

- **个人 PC**：书签走自己的通道，存到你的 VPS
- **公司 PC**：点一下同步，书签树（含文件夹层级）完整还原
- 两边各自新增、修改、**删除**，最终收敛到一致
- 冲突静默 LWW，但留下可查的字段级日志
- 服务端约 20–30 MB 单二进制（NativeAOT），零第三方依赖，无反代层（用你自己的 Nginx Proxy Manager）

不是 Floccus 的复刻。Floccus 是完整的 CRDT 引擎（自动同步、加密同步、多后端）；
这个只做手动触发的最小内核，.NET 10 服务端 + 纯 JS 扩展，无构建步骤。

---

## 快速上手

### 1. 服务端

```bash
git clone <你的仓库> sync-bookmark
cd sync-bookmark/deploy
cp .env.example .env
# 生成 token 并填进 .env
openssl rand -hex 32
docker compose up -d --build
```

在 Nginx Proxy Manager 里新建 Proxy Host：

| 字段 | 值 |
|---|---|
| Domain Names | 你的域名 |
| Scheme | `http` |
| Forward Hostname / Port | `127.0.0.1` / `8080` |
| SSL | Let's Encrypt，Force SSL 开 |

**Advanced 面板必须加这一行**，否则书签多了会被 nginx 以 413 拒掉：

```nginx
client_max_body_size 32m;
```

验证：

```bash
curl https://你的域名/api/health
# {"ok":true,"service":"bmsync",...}
```

### 2. 扩展

1. 打开 `about:debugging#/runtime/this-firefox`
2. 「临时载入附加组件」→ 选 `extension/manifest.json`
3. 打开扩展的设置页，填服务器地址与 token
4. 首次点同步会弹出站点权限请求，允许即可
5. 点工具栏图标 → 「立即同步」

正式使用请打包签名（`docs/plan.md` P6 阶段）：

```bash
cd extension && zip -r ../bmsync.xpi *
```

---

## 同步是怎么工作的

```
扩展采集书签树 → POST /api/sync (state + base)
                      ↓
              服务端做合并（LWW + HLC）
                      ↓
              返回权威 state → 扩展应用到本地书签树
```

**合并只在服务端做一份实现。** 扩展只有"采集"和"应用"两个方向的机械活。
所以操作天然幂等，重复点同步按钮是安全的。

### 身份标识（identity）

双向合并的前提是能认出"两台设备上的这两个书签是同一个东西"。Firefox 的
bookmarks API 只给本地整数 id，跨设备不可用，所以 key 由内容哈希派生：

```
书签:   SHA-256("u:" + url)                 取前 16 字节 → 32 位 hex
文件夹: SHA-256("f:" + parentKey + \x00 + title)
```

好处是**不需要在任何地方存元数据**。代价是"改 URL"和"改文件夹名"会表现为
删除+新增。完整的取舍见 [docs/design.md §3](docs/design.md)。

### 为什么删除能同步

删除会产生一条**墓碑**（`d: true` 的 item），它的 HLC 时间戳参与正常的 LWW
决胜。墓碑在 state 里保留 90 天，足够传播到所有设备，之后由服务端 GC 掉。

这是 LWW-Element-Set，不是朴素 diff —— 朴素 diff 分不清"被删了"和"从没见过"。

### 为什么用 HLC

合并靠时间戳决胜。如果直接用 `Date.now()`，两台机器时钟差 5 分钟就会出现
"我 10:00 加的书签被你 10:03 的旧版本覆盖"，而且**用户完全无感**。

HLC（混合逻辑时钟）保证因果序：只要 A 端先发生的操作、B 端后看到了，B 端此后
产生的任何时间戳都严格大于它。与实际时钟差多少无关。

服务端（C#）与扩展（JS）两端的 HLC 实现必须逐字节一致，由
`test/hlc_vectors.json` 交叉验证 —— 两侧各自逐条复现同一份向量。

---

## 开发

### 一条命令跑全部检查

```bash
./test/all.sh
```

五层，从快到慢：

| 层 | 内容 | 依赖 |
|---|---|---|
| 1 | 语法检查 + C# 编译（警告即错误） | bash / dotnet |
| 2 | 扩展一致性（id、import、两端常量） | bash |
| 3 | 扩展单元测试 | **Node 18+** |
| 4 | .NET 单元测试 + 覆盖率 + Linux 发布 | dotnet |
| 5 | 端到端冒烟（真 HTTP + 真落盘） | curl + dotnet |

**不硬依赖 Node** —— 有就跑第 3 层，没有就明确跳过（而不是假装通过）。

当前状态：**258 个测试全绿**

```
扩展    90 个用例（hlc / keys / collect / apply）
.NET   140 个用例（Domain 109 / Store 25 / Server 46）
冒烟    28 项断言
一致性  17 项
```

覆盖率按包看（最低的是 Domain 那一档）：

```
BookmarkSync.Domain.Tests  41.2%
BookmarkSync.Server.Tests  59.2%
BookmarkSync.Store.Tests   76.8%
```

> 覆盖率数字**只统计被测项目本身**，所以 Domain 那一档偏低是正常的：
> 它下面的 Store 与 Server 代码由各自的测试项目统计，不重复计入。

### 单独跑某一层

```bash
./test/check-extension.sh    # 扩展一致性：id/import/常量两端比对
./test/smoke.sh              # 端到端：起真服务，跑真实 HTTP
./test/dev.sh                # 本地联调：编译并前台起服务，打印地址与 token
cd bmsync && dotnet test     # .NET 单元测试（整个解决方案）
cd bmsync && dotnet test --collect:"XPlat Code Coverage"   # 带覆盖率
```

> 需要 .NET SDK 10。`global.json` 钉在 10.0.401 并设 `rollForward: latestFeature`，
> 所以装了任何 10.0.x 都能用。没有 SDK 时 `all.sh` 会明确跳过 .NET 那一层，
> 而不是假装通过。

`check-extension.sh` 会在**没有 Node 的机器上**替你抓住这类问题：
后台注册了但界面没调的 handler、引用了不存在的 HTML id、import 路径写错、
根目录常量 / HLC 位宽 / schema 版本与服务端不一致。

> 它自己也做过变异测试：故意注入 5 个不存在的 id，确认每一个都被抓到。
> 检查器抓不到问题时，它给出的"通过"是假的。

### 改过 HLC 之后必须重新生成跨端向量

C# 与 JS 的 HLC 必须逐字节一致，否则合并在"时间戳恰好相等"时非确定：

```bash
cd bmsync && dotnet test --filter "FullyQualifiedName~HlcVector"
```

这条命令同时跑两个方向的验证：

- `HlcVectorTests.回放Go生成的向量` —— 逐条复现 `test/hlc_vectors.json`。
- `从头计算的结果与向量一致` —— C# 独立从头算一遍。两者互为镜像：
  只有一个的话，"回放器和实现共享同一个 bug"这类盲区就抓不到
  （详见 docs/plan.md 附录 D 第 13 条）。

`HlcVectorExportTests.BuildVectors()` 是生成器，默认**不**写文件。
要真的重新生成给 JS 用，设 `BMSYNC_WRITE_VECTORS=1`；且目标目录必须
已存在，否则直接失败 —— 路径写错必须是硬错误（附录 D 第 15 条）。

### 跨平台构建

生产镜像跑在 Linux 上，改完顺手验一下：

```bash
cd bmsync
dotnet publish src/BookmarkSync.Cli -c Release -r linux-x64 --self-contained true -o out
```

入口在 `src/BookmarkSync.Cli`，程序集名固定为 `bmsync`（healthcheck 依赖它）。

**NativeAOT 留给 Docker**：生产镜像要用 `FROM scratch`，那要求产物不依赖
任何 `.so` / `libicu` / `libssl`，只有 AOT 能做到。但 AOT 需要 C++ 链接器，
而且**不能交叉编译** —— 在 Windows 上加 `-p:PublishAot=true` 会报
"Cross-OS native compilation is not supported"。所以本地只验 IL 发布，
AOT 交给 `Dockerfile` 在 Linux 容器里做。

### 扩展

无构建步骤。改完在 `about:debugging` 里点"重新加载"即可。

单元测试用 Node 内置 test runner（需要 Node 18+），零依赖：

```bash
cd extension && node --test lib/*.test.js
```

| 文件 | 测什么 |
|---|---|
| `lib/hlc.test.js` | **跨端向量验证** —— 逐条复现 `test/hlc_vectors.json`（与 C#、Go 三方共用） |
| `lib/keys.test.js` | 身份派生：稳定性、NUL 分隔符无歧义、书签 key 不含父目录、根目录常量 |
| `lib/collect.test.js` | 六种变更情形、删除子树全量墓碑、base 的作用 |
| `lib/apply.test.js` | 三条顺序约束、删除只发一次 remove、**往返一致性** |

`lib/mock-bookmarks.js` 是 `browser.bookmarks` 的内存替身，刻意模拟了
Firefox 的真实行为（用有无 url 区分类型、四个根目录固定 id、remove 对文件夹递归）。

---

## 目录结构

```
bmsync/            .NET 10 服务端
  global.json      SDK 版本钉在 10.0.401
  Directory.Build.props / Directory.Packages.props
  src/
    BookmarkSync.Domain/  ★ 领域层：数据模型 + 合并算法 + HLC + GC
      Hlc.cs            混合逻辑时钟（与 lib/hlc.js 逐字节对应）
      State.cs          State / Item 类型与校验、schema 版本
      Merge.cs          ★ 合并算法，项目最核心的代码
      TombstoneGc.cs    墓碑清理
      Limits.cs         领域限额
      StateJsonConverters.cs  JSON 逐字节对齐 Go 的 omitempty + key 排序
    BookmarkSync.Store/   持久化：原子写、互斥串行化、历史快照
    BookmarkSync.Server/  HTTP 层：路由、鉴权限流、handlers、配置
    BookmarkSync.Cli/     入口（程序集名 bmsync）：启动、优雅关闭、healthcheck
  tests/                  三个测试项目，与 src 一一对应
  Dockerfile              多阶段构建：测试 → NativeAOT → FROM scratch
extension/         Firefox 扩展（纯 ESM，无构建、无框架）
  background.js    消息路由与同步编排
  lib/hlc.js       必须与服务端逐字节一致
  lib/keys.js      身份派生
  lib/collect.js   采集：书签树 → state
  lib/apply.js     应用：state → 书签树
  lib/sync.js      一次同步的完整流程
  lib/store.js     IndexedDB
  lib/client.js    HTTP 与错误映射
  popup/           工具栏弹窗
  options/         设置、冲突列表、历史快照
deploy/            docker-compose、.env 模板、备份脚本
docs/              design.md（设计）、plan.md（实施计划 + 实施期问题记录）
HANDOVER.md         交接文档：真机调试用的环境陷阱、验证清单、排查手册
test/
  all.sh                 一条命令跑全部检查
  smoke.sh               端到端冒烟（真实 HTTP + 真实落盘）
  dev.sh                 本地联调：编译并前台起服务，打印地址与 token
  check-extension.sh     扩展一致性（不需要 Node）
  hlc_vectors.json       C#↔JS 的 HLC 交叉验证向量（两端口径一致）
  jsonq/                 冒烟脚本用的迷你 JSON 读取器
```

### 为什么分成三个项目

依赖是单向的，从上到下没人反向引用：

```
Cli  →  Server  →  Store  →  Domain
（入口）  （HTTP）    （落盘）    （数据模型 + 合并算法）
```

这么分的主要收益是 **`Merge.cs` 不再被 HTTP 代码淹没**。合并算法是这个
项目里唯一需要反复精读的东西，而它必须能在完全不启动 HTTP 的情况下被
完整测试。

配套的两条约定：

- **`BookmarkSync.Domain` 不引用任何其他项目**，连日志接口都不碰。
  它是纯领域逻辑，输入输出都是值类型。
- **`BookmarkSync.Store` 有自己的最小 `StoreOptions`**（只要 `DataDir` /
  `HistoryKeep` / `TombstoneTtl`），不复用 `ServerOptions`。让持久化层
  依赖 HTTP 层会把依赖方向反过来，而那两个多余字段（`Token` / `Addr`）
  对存储层毫无意义。

---

## 已知限制

- **不排序**：书签内容一致，但文件夹内的排列顺序不保证一致。新书签追加到末尾。
- **同一 URL 出现在不同文件夹会合并成一条**。书签的 key 只依赖 URL，这是
  "移动书签能被正确同步"的代价。
- **重命名文件夹会重建整棵子树**（内容不丢，但会产生大量墓碑）。
  文件夹的 key 依赖父级 key，这是无法两全的取舍。
- **不跨设备防复活**：设备离线超过 90 天，它删除的项的墓碑可能已被 GC。
  详见 [docs/design.md §12.3](docs/design.md)。
- **不同步 tags / 备注 / 阅读列表**。bookmarks API 不暴露 annotations。

## 开发过程中修掉的几个坑

留个记录，因为它们的症状都和原因相距很远：

| 症状 | 真实原因 |
|---|---|
| 书签随机丢失，两端交替获胜 | HLC `update` 的分支顺序写反了。第三条必须是 `max == rl` 而非 `max == p`——否则物理时钟追平远端时会产生**小于**已收时间戳的新值 |
| **云端多出 4 个空文件夹** | `collect.js` 把 Firefox 的四个系统根目录也当成 item 收了进去。它们只应作为顶层项的 `p` 值存在 |
| **书签移到本地已有目录时静默失败** | `apply.js` 只从"待改动项"里查父目录 id，本地已有但本次不变的目录查不到 → move 报"父目录不存在"但同步报告成功 |
| **扫描直接抛 `ROOT_MOBILE is not defined`** | `collect.js` 用了未 import 的符号。`check-extension.sh` 只查 import 路径存不存在，抓不到漏掉的符号 |
| "Go 与 JS 的 HLC 不一致" | 其实是**向量生成器**的 bug：`step` 闭包里无条件多调了一次 `now()`，把错误状态固化进了向量文件 |
| 并发同步偶发丢一个更新 | Windows 上 `rename` 会因杀毒软件短暂占用而返回 ACCESS_DENIED。加了指数退避重试 |
| 冒烟测试大面积"假失败" | 写成 `c=$(api ...)` 让函数在子 shell 里跑，`BODY` 随子 shell 消失。状态码与响应体必须用全局变量回传 |
| `check-extension.sh` 报"通过"但其实没检查 | 字符类 `[A-Za-z0-9_]` 不含 `-`，而 id 大多是 `first-run` 这种带连字符的——匹配不到等于假绿灯 |
| 首次同步后 10 次操作内全被 429 | 限流按"脚本攻击"设成 10 次/分钟，但这工具是手动触发的。已放宽到 60/min |

这些 bug 有个共同特征：**都不会抛异常、不会让程序崩**，只是让结果悄悄地不对。
这正是为什么"从未跑过的 500 行"值得专门补测试。

## 文档

- [设计文档](docs/design.md) —— 架构、数据模型、算法、API、风险
- [实施计划](docs/plan.md) —— 7 个阶段、验收标准、风险登记
