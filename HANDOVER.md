# 交接文档 —— 真机调试

> 面向的场景：把本仓库打包复制到另一台设备，在**真实 Firefox** 上验证书签同步。
>
> 本文档只写「文档和代码里查不到的东西」：环境陷阱、验证步骤、
> 以及"看起来成功其实没成功"的地方。设计与算法说明在 `docs/design.md`。

---

## 0. 一分钟速览

| 问题 | 答案 |
|---|---|
| 这是什么 | Firefox 扩展 + 自建服务端，手动触发的书签双向同步，**删除会同步** |
| 服务端在哪跑 | 你的 VPS，用 Docker Compose 部署；反代用你已有的 Nginx Proxy Manager |
| 本地怎么跑测试 | `./test/all.sh`（五层，258 个用例） |
| 扩展怎么装 | `about:debugging` → 「临时载入附加组件」→ 选 `extension/manifest.json` |
| **现在缺什么** | 真机双端同步未验证、Docker 部署未验证、运维文档未写 |
| **最大的未知数** | 公司网络能否访问你的 VPS 域名 —— 拦了整个方案就废了，且无法绕过 |

---

## 1. 环境陷阱（最容易浪费时间的部分）

### 1.1 本仓库的开发环境不是干净的

下面这些在**开发机**上全部踩过，换设备后大概率重演：

| 现象 | 真实原因 | 怎么办 |
|---|---|---|
| `dotnet: 无法识别` | .NET SDK 装了但**不在 PATH** | 见 §1.2 |
| `go: 无法识别` | Go 只在跑 `legacy-go/` 对照实现时才需要，不在 PATH | 同上（加 Go 路径） |
| `node: 无法识别` | 同上 | 同上 |
| `bash` 跑起来是 WSL | PATH 上的 `bash.exe` 是 Windows 的 WSL 占位符 | 必须显式用 Git 的 bash |
| `curl --version` 报"无法解析 --version" | **PowerShell 把 `curl` 定义成了 `Invoke-WebRequest` 的别名**，而别名优先级高于 PATH —— 调 PATH 不管用 | PowerShell 里必须写 `curl.exe`。`test/*.sh` 在 bash 里跑，不受别名影响 |
| `python3: Python was not found` | PATH 上是 Microsoft Store 的占位符 | 脚本会自动降级到 `test/jsonq`，不依赖它 |
| `npm --version` 被拒绝执行 | PowerShell 执行策略禁止 `.ps1` | 用 `npm.cmd`，或者干脆不用 npm（本项目零 npm 依赖） |
| `dotnet publish -r linux-x64 -p:PublishAot=true` 报 `Cross-OS native compilation is not supported` | NativeAOT **不能交叉编译** | 正常现象。AOT 只能由 `Dockerfile` 在 Linux 容器里做；本地只验不带 AOT 的发布 |

### 1.2 一次性设置 PATH

开发机上的实际安装位置（**新设备需要自己确认**）：

```
C:\Program Files\dotnet           .NET SDK 10.0.401
C:\Program Files\Go\bin           Go 1.27.1（仅 legacy-go/ 对照实现需要）
C:\Program Files\nodejs          Node 24.21.0
C:\Program Files\Git\bin         Git bash
C:\Program Files\Git\mingw64\bin  Git 自带的 curl（见下方注意事项）
C:\Program Files (x86)\Mozilla Firefox\firefox.exe    Firefox 156.0.1
```

在 PowerShell 里给当前会话加好：

```powershell
$env:Path = "C:\Program Files\dotnet;C:\Program Files\nodejs;C:\Program Files\Git\bin;" + $env:Path

# 验证
dotnet --version      # 必须是 10.0.x
node --version
bash --version        # 必须是 GNU bash，不是 WSL
```

**关于 curl，两件事：**

1. **Git 的 curl 在 `mingw64\bin`，不在 `usr\bin`。** 但通常**不必专门加** ——
   Windows 10+ 自带一个真正的 `curl.exe`（`C:\Windows\System32\curl.exe`），
   Git bash 默认就能找到。冒烟脚本在 bash 里跑，不受下面的别名问题影响。

2. **在 PowerShell 里 `curl` 永远是别名**（指向 `Invoke-WebRequest`），
   改 PATH 也没用。所以本项目所有 PowerShell 命令一律写 `curl.exe`：

```powershell
Get-Command curl | Select-Object CommandType, Definition
# CommandType=Alias, Definition=Invoke-WebRequest  →  确认是别名
```

### 1.3 工具链要求

| 工具 | 版本 | 必需性 |
|---|---|---|
| .NET SDK | ≥ 10.0 | 跑服务端与全部 C# 测试 |
| Node | ≥ 18 | 只用于扩展单元测试；**没有也能跑 `check-extension.sh` 和冒烟** |
| bash | 任意 | 跑 `test/*.sh` |
| curl | 任意 | 跑 `test/smoke.sh` |
| Python 或 jq | 任意 | **可选**。都没有时冒烟脚本会自动编译 `test/jsonq` |
| Go | ≥ 1.23 | **可选**。只在编译 `legacy-go/` 对照实现、跑它的跨端向量验证时需要 |
| Docker | 任意 | 部署到 VPS |

> 扩展是纯 ESM，**没有构建步骤、不依赖任何 npm 包**。所以没有 Node 也能改代码、
> 在 Firefox 里加载，只是跑不了单元测试。
>
> 服务端**零第三方 NuGet 包**：唯一的外部引用是
> `Microsoft.Extensions.Logging.Abstractions`（只取 `ILogger` 接口），
> 测试用的 xunit 另算。所以没有 NuGet 源也能构建 —— 首次需要还原一次。

---

## 2. 先跑一遍自动化检查

```bash
cd sync-bookmark
./test/all.sh
```

五层，从快到慢：

| 层 | 内容 | 依赖 |
|---|---|---|
| 1 | 语法检查 + C# 编译（警告即错误） | bash, dotnet |
| 2 | 扩展一致性（17 项） | bash |
| 3 | 扩展单元测试（90 个用例） | Node 18+ |
| 4 | .NET 单元测试（140 个）+ 覆盖率 + Linux 发布 | dotnet |
| 5 | 端到端冒烟（28 项断言） | curl, dotnet |

**期望输出**：`全部通过  12 项通过`。有任何 `✗` 先别往下走。

层与层之间出问题时，先看 `docs/plan.md` 附录 D —— 那 15 条记录了开发期
发现的实质问题，包括 3 个是补 Node 测试才抓出来的（见 §6）。

---

## 3. 本地起服务端（不上 VPS 也能测扩展）

正式部署走 Docker，但**调试扩展时本地跑一个裸二进制更快**：

```powershell
# 编译（入口在 src/BookmarkSync.Cli）
cd bmsync
dotnet publish src/BookmarkSync.Cli -c Release -o out

# 起服务（token 必须 ≥ 32 字符）
$env:BMSYNC_TOKEN = "0123456789abcdef0123456789abcdef01234567"
$env:BMSYNC_DATA  = "$PWD\..\tmp-data"
$env:BMSYNC_ADDR  = "127.0.0.1:18099"
.\out\bmsync.exe
```

> 或者直接用 `./test/dev.sh`（Git Bash 里跑）：它会编译、预检配置、
> **前台**起服务，并打印扩展设置页该填的地址和 token，Ctrl+C 停止。
> 常用参数：`--reset`（清空数据目录重来）、`--addr`（换端口，
> 默认 127.0.0.1:18099）、`--no-build`（跳过编译）。完整用法见
> `./test/dev.sh -h`。
>
> 调试扩展时用它比手动敲 `dotnet publish` + 设环境变量省事：它会先跑
> `-config-check` 预检，token 写错或数据目录不可用会立刻报错并退出，
> 而不是等服务起一半再从日志里翻。

另开一个窗口验证（**必须写 `curl.exe`**，见 §1.1）：

```powershell
curl.exe http://127.0.0.1:18099/api/health
# {"ok":true,"service":"bmsync","schema":1,...}
```

`/api/health` **不需要 token** —— 扩展设置页的「测试连接」依赖这一点。

配置有问题时用这个子命令自查，它只校验配置和数据目录、不会监听端口：

```powershell
.\out\bmsync.exe -config-check    # 正常输出 "config ok" 并退出码 0
```

### 加载扩展

1. Firefox 地址栏输入 `about:debugging#/runtime/this-firefox`
2. 「临时载入附加组件」→ 选 `extension/manifest.json`
3. 工具栏应出现扩展图标

**改完代码后**：回到 `about:debugging` 点「重新加载」即可，不需要重启浏览器。

---

## 4. 真机验证清单

**先用一个 Firefox 的两个 Profile 试**（比两台机器快得多，改书签也方便）：

```powershell
& "C:\Program Files (x86)\Mozilla Firefox\firefox.exe" -P
```

建 Profile A（模拟个人电脑）和 Profile B（模拟公司电脑），各自加载扩展。

> ⚠️ 这个命令会**打开 Profile 管理器窗口并一直阻塞**，直到你关掉它。
> 看起来像"卡住了"，其实是正常的。Ctrl+C 结束不了的话直接关窗口。

⚠️ 两个 Profile **不要同时开着**同一个 profile。它们会共用同一个服务端 ——
这正是我们要测的场景。

### 4.1 必测项（对应 `docs/design.md` §11.3）

| # | 操作 | 期望 | 出问题说明 |
|---|---|---|---|
| 1 | B 填好设置 → 点「测试连接」 | 显示服务端 item 数 | 权限没授予（见 §4.3） |
| 2 | B 点「立即同步」→ 允许站点权限 | **B 上出现 A 的全部书签** | 首次同步是"云端没有就上传、有就合并" |
| 3 | A 新增 3 个书签 → A 同步 → B 同步 | B 出现 3 个 | |
| 4 | A 删 1 个 → A 同步 → B 同步 | B 上消失 | **核心功能** |
| 5 | A 删整个文件夹（含 20 个子书签）→ 同步 | B 上整个文件夹消失 | 验证"子树每一项都写墓碑" |
| 6 | A 改标题 → 同步 | B 标题更新 | |
| 7 | A 把书签拖到**另一个已存在的文件夹** → 同步 | B 上位置一致 | ⚠️ **重点**：这条曾因 `apply.js` 查不到父目录而静默失败 |
| 8 | A 连续点 3 次同步 | 书签数不变 | 幂等性 |
| 9 | A 离线新增 5 个，期间 B 在线新增 3 个 → A 同步 → B 同步 | 两端都是 8 个 | 并发合并 |
| 10 | 改错 token → 同步 | 明确报错，**本地书签零变化** | 失败路径 |
| 11 | 断网 → 同步 | 明确报错，**本地书签零变化** | 失败路径 |
| 12 | 两端同时改同一书签标题 → 同步 | 静默按时间戳决胜，冲突列表里能看到 | 设置页 → 冲突记录 |

### 4.2 时钟偏差（可选但建议做）

LWW 靠时间戳决胜，HLC 用来保证因果性。这个测试验证它真的有效：

1. 在 A 上把系统时间**往前调 1 小时**（设置 → 时间和语言）
2. A 新增/修改几个书签 → A 同步
3. 改回正确时间（或不管）
4. B 同步 → B 应该拿到 A 的改动
5. 反向再做一次

**如果这时书签被覆盖了，说明 HLC 没起作用** —— 回头看 `lib/hlc.js` 的 `update()`。

### 4.3 Firefox MV3 的坑

**站点权限是手动授予的。** MV3 把权限从"安装时授予"改成了"用户手动授予"，
所以第一次点同步一定会弹权限框。这是预期行为，不是 bug。

如果点了没反应：

```
about:addons → 找到 BM Sync → 齿轮图标 → 检查「网站访问权限」
```

### 4.4 书签顺序：不要当成 bug

**同步不保证文件夹内的排列顺序。** 新书签一律追加到父目录末尾。
这是有意的简化（见 `docs/design.md` §5.4）。

所以：**内容一致即通过，顺序不同不算失败。**

---

## 5. 部署到 VPS

### 5.1 反代在 NPM 上，项目不自带

`bmsync` 只监听明文 HTTP 端口，**且只应在内网可达**。compose 里已经绑
`127.0.0.1:8080:8080`，公网直连是不通的。

Nginx Proxy Manager 里建 Proxy Host：

| 字段 | 值 |
|---|---|
| Domain Names | 你的域名 |
| Scheme | `http` |
| Forward Hostname / Port | `127.0.0.1` / `8080` |
| Block Exploits | 开 |
| Websockets Support | 关 |
| SSL | Let's Encrypt，Force SSL 开 |

### 5.2 ★ Advanced 面板必须加这一行

```nginx
client_max_body_size 32m;
```

nginx 默认是 `1m`。书签超过约 6000 条时 state.json 就会超过 1MB，
**请求会在到达 bmsync 之前就被 nginx 以 413 拒绝**，而且报错来自 nginx、
看起来跟你的服务无关。这是最容易被忽略的一个坑。

### 5.3 部署

```bash
cd deploy
cp .env.example .env
# 生成 token 填进去
openssl rand -hex 32
docker compose up -d --build
```

### 5.4 ★ 必须实测：公司网络能否访问

**这是整个方案最大的未知数。** 如果公司网络策略拦了你的域名，
方案在公司侧就完全不可用，且**没有任何技术手段能绕过**（也不该绕）。

```powershell
# 在公司电脑上跑
curl.exe -I https://你的域名/api/health
```

拦了的话，只能考虑：换域名、走公司已有的反向代理、或者放弃在公司侧同步。

---

## 6. 补测试时抓出来的 3 个 bug（真机重点看这些地方）

这 3 个都是"**不会抛异常、不会让程序崩，只是让结果悄悄地不对**"的类型。
自动化测试已经覆盖，但 mock 毕竟不是 Firefox，真机要重点复核：

| bug | 症状 | 已修位置 | 真机该怎么验 |
|---|---|---|---|
| 四个系统根目录被当成书签 | 两端凭空多出 4 个空文件夹 | `collect.js` 第一趟拍平 | 清单 #2：同步后数一下空文件夹 |
| 移动书签静默失败 | 书签没动，但 UI 显示同步成功 | `apply.js` 的 `ffIdByKey` | 清单 #7：拖到**已存在的**文件夹 |
| 未 import 的 `ROOT_MOBILE` | 一调用就崩 | `collect.js` | 已修，真机不会遇到 |

另外这些是"看起来成功其实没成功"的坑：

- **非 200 响应绝不修改本地书签** —— 断网/token 错时，本地必须零变化（清单 #10、#11）
- **应用阶段中途失败不回滚** —— 会在 popup 显示"部分完成"并建议重试。**重试是安全的**
- **父目录缺失时会跳过而不是丢到根目录** —— 宁可少同步，也不静默挪动用户书签的位置

---

## 7. 排查手册

| 症状 | 先查 |
|---|---|
| 同步报"未获得访问 xxx 的权限" | §4.3，Firefox 的站点权限 |
| 同步报 413 | §5.2，`client_max_body_size` |
| 同步报 401 | 扩展设置里的 token 与服务端 `BMSYNC_TOKEN` 是否一致 |
| 同步报"无法连接服务器" | 服务端活着吗？`/api/health` 通吗？NPM 的 Forward Hostname/Port 对吗？ |
| 一直 429 | 60 次/分钟用完了。手动触发时不太可能，除非有脚本在刷 |
| 书签顺序不对 | **不是 bug**，见 §4.4 |
| 服务端 413 但扩展说"数据超过限制" | 同 §5.2 |
| 书签"复活"了 | 某台设备离线超过 90 天，它删除的项的墓碑已被 GC。见 `docs/design.md` §12.3 |
| 两端内容不一致 | 设置页 → 冲突记录，看有没有 `concurrent_edit` |
| 想看服务端到底存了什么 | `docker compose logs -f bmsync`（**只记 URL 不记 body**，`/api/sync` 每天最多 60 条） |

### 从快照回滚

服务端每次成功写入前会自动快照，保留最近 30 份：

```bash
cd deploy
ls -lt data/history/          # 文件名是时间戳，最新在前
docker compose down
cp data/history/<快照名>.json data/state.json
docker compose up -d
```

⚠️ **回滚后两端都要重新同步**，且客户端的本地缓存里可能有更新的数据会被推回去。
拿不准就先看 `docs/design.md` 的数据结构。

---

## 8. 接下来该做什么

按优先级：

1. **§4 的 12 项真机验证**（尤其是 #4、#5、#7）
2. **§5.4 确认公司网络可达** —— 这个不通过，其他都白做
3. Docker 部署演练（`FROM scratch` + `healthcheck` 子命令的组合值得真机跑一次）
4. 写 `docs/OPERATIONS.md`（备份/恢复/快照回滚/token 轮换）

### 待你确认的产品决策

`docs/design.md` §13 有 5 个问题一直没定，代码里按默认值实现了，改动面都很小：

| # | 问题 | 当前默认 |
|---|---|---|
| Q1 | 冲突日志详细程度 | 字段级 diff（哪个字段、两边分别是什么值） |
| Q2 | 冲突日志可见范围 | 存服务端，任一设备可看 |
| Q3 | popup 摘要口径 | 云端总数 + 本次变化，两行 |
| Q4 | 要不要手动解决冲突的入口 | 不要，纯 LWW |
| Q5 | 移动设备书签是否同步 | 排除 |

---

## 9. 文件地图

```
sync-bookmark/
├── HANDOVER.md            ← 你在读的这份
├── README.md              ← 项目说明 + 快速上手
├── docs/
│   ├── design.md          设计文档：架构、算法、API、风险
│   └── plan.md            实施计划 + 15 条实施期问题记录
├── bmsync/                .NET 10 服务端
│   ├── global.json        SDK 钉在 10.0.401
│   ├── src/
│   │   ├── BookmarkSync.Domain/   ★ 领域层：数据模型 + 合并 + HLC + GC
│   │   │   ├── Merge.cs        ★ 合并算法（最核心）
│   │   │   ├── Hlc.cs          混合逻辑时钟
│   │   │   ├── State.cs        State / Item 与校验
│   │   │   ├── TombstoneGc.cs  墓碑清理
│   │   │   └── StateJsonConverters.cs  JSON 对齐 Go 的 omitempty + key 排序
│   │   ├── BookmarkSync.Store/    持久化：原子写、锁、快照
│   │   ├── BookmarkSync.Server/   HTTP：路由、鉴权限流、handlers、配置
│   │   └── BookmarkSync.Cli/      入口（程序集名 bmsync）
│   ├── tests/             三个测试项目，与 src 一一对应
│   └── Dockerfile         多阶段：测试 → NativeAOT → FROM scratch
├── legacy-go/             ★ Go 对照实现（迁移自 .NET 前保留，不参与构建）
│   └── bmsync/            完整可编译；test/hlc_vectors.json 由它生成
├── extension/             Firefox 扩展（纯 ESM，无构建）
│   ├── background.js      消息路由与同步编排
│   ├── lib/
│   │   ├── hlc.js         ★ 必须与服务端逐字节一致
│   │   ├── keys.js        身份派生
│   │   ├── collect.js     采集
│   │   ├── apply.js       应用
│   │   ├── mock-bookmarks.js  Firefox API 的内存替身
│   │   └── *.test.js      90 个用例
│   ├── popup/  options/
├── deploy/                docker-compose、.env 模板、备份脚本
└── test/
    ├── all.sh             一条命令跑全部五层
    ├── smoke.sh           端到端冒烟
    ├── dev.sh             本地联调：编译 + 前台起服务（Ctrl+C 停）
    ├── check-extension.sh 扩展一致性
    ├── hlc_vectors.json   Go↔C#↔JS 跨端验证向量
    └── jsonq/             冒烟用的迷你 JSON 读取器
```

> 找文件时按这个顺序猜：`lib/hlc.js` ↔ `src/BookmarkSync.Domain/Hlc.cs`，
> `lib/collect.js` ↔ `src/BookmarkSync.Domain/State.cs` + `Merge.cs`，
> `background.js` ↔ `src/BookmarkSync.Server/`。**改协议要同时看
> `src/BookmarkSync.Server/ApiTypes.cs`**（JSON 字段名）和 `lib/collect.js`。

### 改 HLC 之后必须重新生成向量

```bash
cd bmsync && dotnet test --filter "FullyQualifiedName~HlcVector"
```

这条命令同时跑两个方向：回放 `test/hlc_vectors.json`（证明 C# 与 Go 一致），
以及 C# 独立从头算一遍（抓"回放器和实现共享同一个 bug"的盲区）。

向量是**派生物**，不要手工编辑 —— 开发期就因为生成器有 bug 而误导过一次
排查方向（docs/plan.md 附录 D 第 13 条）。要重新生成设
`BMSYNC_WRITE_VECTORS=1`，且目标目录必须已存在（第 15 条）。
