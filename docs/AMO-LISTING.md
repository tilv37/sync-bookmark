# Firefox Add-ons (AMO) listing — BM Sync

Store listing copy in English and Chinese. Paste into the Developer Hub
listing fields (`Summary` ≤ 120 characters; `Description` accepts Markdown).

---

## English

### Summary (99 chars)

```text
Two-way Firefox bookmark sync with delete propagation. Requires your own self-hosted bmsync server.
```

### Description

```markdown
**BM Sync** keeps the same bookmark tree on two or more Firefox installations
(your desktop and your work machine, for example). Adding, renaming, moving and
**deleting** bookmarks all converge, folder structure included.

⚠️ **This extension does nothing on its own.** It is one half of an open-source,
self-hosted sync system: you must deploy the companion server on a machine you
control (Docker Compose, single ~25 MB binary), then point the extension at its
URL with an access token. If you are not willing to run your own server, please
use a hosted alternative instead.

## Why

Firefox Account sync can't be used on machines where signing in is not allowed
(work computers, managed profiles), and most sync tools either push your data
through a third-party service or ignore deletions. BM Sync's server is yours:
**your bookmarks only ever travel to your own server — never to any third
party, and there is zero analytics or telemetry.**

## How it works

- Click the toolbar icon → **Sync now**. The extension reads your bookmark
  tree, uploads it, the server merges, and the result is applied locally.
- The merge is implemented once, on the server, with tombstones — so a
  deletion on one machine is a deletion everywhere after both sides sync.
- Concurrent edits resolve automatically (last writer wins, using a hybrid
  logical clock so machine clock skew can't silently destroy data). Every
  resolved conflict is recorded with field-level detail and viewable in the
  options page.
- Syncing is idempotent: pressing the button twice, or from two machines in a
  row, is always safe.

## Features

- Two-way merge: adds, renames, moves, and **deletions** (with folder subtree)
- Root-folder selection: bookmarks toolbar / menu / other bookmarks, optional
  mobile bookmarks
- Conflict log (field-level: which field, both values, who won)
- Server-side history snapshots (last 30) listed in the options page
- No auto-sync, no background polling — you are in control of every upload
- Lightweight: no frameworks, no build step, no third-party libraries in the
  extension; the server needs nothing installed on your browser

## Requirements

- A self-hosted bmsync server (Docker) reachable over HTTPS (plain HTTP is
  supported for LAN use, not recommended over the internet)
- Firefox 140+ on desktop (142+ on Android)

## Getting started

1. Deploy the server: `git clone`, set a token in `.env`,
   `docker compose up -d` (full steps in the README)
2. Install this extension, open its settings, enter the server URL + token,
   press **Test connection**
3. Click **Sync now** — the first run asks once for permission to reach your
   server's domain

## Open source

Code, server, deployment and operations docs:
[github.com/tilv37/sync-bookmark](https://github.com/tilv37/sync-bookmark)
(MIT license — inspect everything before you run it; issues welcome).

## Known limitations

- Bookmark **order** inside folders is not synced (new items append at the
  end); only content converges
- The same URL saved in two folders merges into one entry
- Renaming a folder rebuilds its subtree internally (content is preserved)
- Devices offline for more than 90 days may re-upload bookmarks deleted
  elsewhere (tombstone expiry)
- Tags/keywords, annotations, and the reading list are not synced
```

---

## 中文 (zh-CN)

### 摘要（55 字符）

```text
双向同步 Firefox 书签，删除也会同步。需搭配自部署的 bmsync 服务端使用，不连接任何第三方服务。
```

### 描述

```markdown
**BM Sync** 让多个 Firefox（例如家里和公司两台电脑）保持同一棵书签树：新增、
改名、移动、**删除**都会收敛到一致，包含文件夹层级。

⚠️ **本插件无法独立使用。** 它是一套开源自部署同步系统的一半：你需要在自己
控制的机器上部署配套服务端（Docker Compose，单文件二进制约 25 MB），然后在
插件设置里填入服务端地址和访问令牌。如果不想自己部署服务端，请选择其他
托管类同步方案。

## 为什么做这个

在不能登录个人 Firefox 账号的设备（公司电脑、受管控的环境）上，官方书签同步
用不了；而多数第三方同步工具要么把数据经过别人的服务器，要么根本不同步删除。
BM Sync 的服务器是你自己的：**书签只会传输到你自己部署的服务器，绝不经过任何
第三方；插件没有任何统计、埋点或遥测。**

## 工作原理

- 点击工具栏图标 → 「立即同步」。插件读取本地书签树上传，服务端完成合并，
  再把权威结果应用到本地。
- 合并算法只实现一份（服务端），基于墓碑机制——一台电脑删掉的书签，其他电脑
  同步后也会删除。
- 并发修改自动按「最后写入优先」解决，时间戳采用混合逻辑时钟（HLC），两台
  机器系统时间不一致也不会悄悄丢数据。每次冲突都会留字段级记录，可在设置页
  查看。
- 同步操作幂等：连点两次、两台电脑先后点，都安全。

## 功能特性

- 双向合并：新增、改名、移动、**删除**（含整个文件夹子树）
- 可选同步范围：书签栏 / 书签菜单 / 其他书签，移动设备书签可选
- 冲突日志：精确到字段（哪个字段、两边各自的值、谁胜出）
- 服务端自动保留最近 30 份历史快照，设置页可查看
- 无自动同步、无后台轮询——每一次上传都由你按下按钮触发
- 轻量：插件无框架、无构建步骤、无第三方库

## 运行要求

- 自部署的 bmsync 服务端（Docker），建议通过 HTTPS 访问（局域网可用 HTTP，
  公网不推荐）
- 桌面版 Firefox 140 及以上（Android 版 142 及以上）

## 快速上手

1. 部署服务端：`git clone` 仓库 → `.env` 里设置随机令牌 →
   `docker compose up -d`（详见 README 完整步骤）
2. 安装本插件，打开设置页，填入服务器地址与令牌，点「测试连接」
3. 点「立即同步」——首次会请求访问你的服务器域名的权限，允许即可

## 开源

插件、服务端、部署与运维文档全部开源：
[github.com/tilv37/sync-bookmark](https://github.com/tilv37/sync-bookmark)
（MIT 协议，欢迎审阅代码、提 Issue）。

## 已知限制

- **不同步文件夹内的排序**（新条目追加到末尾），只保证内容一致
- 同一 URL 保存在两个文件夹时，会合并为一条
- 重命名文件夹在内部会重建子树（内容不丢失）
- 离线超过 90 天的设备重新上线，可能把别处已删除的书签传回来（墓碑过期机制）
- 不同步标签（tags）、备注、阅读列表
```

---

## Keywords (for the listing form)

```text
bookmarks, sync, self-hosted, two-way, deletion, merge, firefox, private, docker
书签, 同步, 自建服务器, 双向, 删除同步
```

## Category suggestion

`Bookmarks` (primary). No monetization, no tracking — leave all consent/telemetry
declarations off apart from the manifest's `bookmarksInfo` data-collection entry.
