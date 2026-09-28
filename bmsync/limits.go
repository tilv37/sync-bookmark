package main

import "time"

// 服务端的各类硬限额。
//
// 单独成文件的原因：这些数字会在 design.md §7.7 与实际实现之间反复核对，
// 集中放置便于比对，也避免散落在各处的魔法数字。
const (
	// MaxItems 是单份 state 允许的最大 item 数。
	MaxItems = 50_000

	// MaxDepth 是书签树最大层数（不含根目录本身）。
	//
	// 为什么需要：客户端计算 key 时文件夹 key 依赖父级 key，理论上可以构造
	// 极深的链。限制层数既防止病态数据，也给 apply 阶段的递归留出上界。
	MaxDepth = 32

	// MaxBodyBytes 是 /api/sync 请求体上限。
	//
	// 注意：这只是应用层上限。nginx 侧还有一层更小的限制
	//（client_max_body_size 默认 1m），必须由用户在 NPM 中放开，
	// 见 design.md §10.4。
	MaxBodyBytes = 16 << 20

	// MaxRequestDuration 是单个 /api/sync 请求的服务端处理时限。
	MaxRequestDuration = 10 * time.Second

	// RateLimitPerMinute 是按 IP 的同步请求限流。
	//
	// 为什么是 60 而不是个位数：这是**手动触发**的工具，用户的正常操作是
	// "点一下同步按钮"。一次典型会话包括：首次同步、公司电脑首次拉取、
	// 几次补同步、失败后重试、顺便拉一次冲突列表。10 次/分钟会让这些正当
	// 操作在开始几次之后全部 429，然后用户什么都不用做也得等一分钟 ——
	// 这既挡不住真正的攻击者，又让正常用起来很难受。
	//
	// 真正的防护目标是"挡住脚本化的高频调用"，而 60 次/分钟
	// （每秒 1 次）已经远远超过人的点击速度。数据量也小：60 次 × 800KB
	// 的上传，对任何一台 VPS 都不构成压力。
	RateLimitPerMinute = 60
)

const (
	// KeyLen 是 item key 的十六进制字符数（SHA-256 取前 16 字节）。
	// 128 bit 对个人书签规模而言碰撞概率可忽略。
	KeyLen = 32

	// MaxURLLen / MaxTitleLen 是单字段长度上限，防御异常超长输入。
	// 注意：真实书签标题经常超过 1KB（网页 <title> 原样存进来，真机上见过
	// 2762 字节的），1024 会误伤正常数据。8KB 仍远小于 16MB 请求体上限，
	// 极端病态输入由 MaxBodyBytes 兜底。
	MaxURLLen   = 4096
	MaxTitleLen = 8192
)
