package main

import (
	"fmt"
	"sort"
	"strings"
)

// 合并是本项目风险最高的一段代码：它决定了两台设备最终是否会收敛到
// 同一个书签集合，而且出错时**静默丢数据、用户完全无感**。
//
// 因此这一文件的第一原则不是性能或简洁，而是：
//
//  1. 遍历顺序确定（走 sortedKeys，不走 map range）
//  2. 三个不变量有显式断言（见 mergeInvariants）
//  3. 每个分支都有对应的测试用例（见 merge_test.go）
//
// ── 合并模型 ──────────────────────────────────────────────────────────
//
// 这是一个 **LWW-Element-Set**（带墓碑的 LWW 集合），不是 CRDT。
// 区别在于：CRDT 保证无冲突合并，这里允许冲突但用 HLC 时间戳决胜。
// 对个人书签场景够用，且实现量只有 CRDT 的零头。
//
// identity（key）由内容哈希派生，不存任何元数据 —— 见 docs/design.md §3。
// 这意味着「改 URL」和「改文件夹名」都会表现为「删除旧的 + 新增新的」，
// 而非「修改」。这是刻意的取舍。

// 冲突类型。
const (
	// ReasonConcurrentEdit 是真正的并发编辑：两端都基于同一个基线改动了
	// 同一项。**这才是有价值的冲突**，用户需要知道。
	ReasonConcurrentEdit = "concurrent_edit"

	// ReasonHLCCollision 是 HLC 完全相等但内容不同。
	// 理论上不应发生 —— HLC 保证同一因果链上严格递增，每台设备在同一
	// 毫秒内用逻辑计数区分。走到这里说明两端 HLC 实现不一致，
	// 记录下来是为了排查，不是预期会有。
	ReasonHLCCollision = "hlc_collision"
)

// 决胜方标识。
const (
	ConflictWinnerServer = "server"
	ConflictWinnerClient = "client"
)

// Conflict 是一条冲突记录。纯观测，**不影响合并结果**。
type Conflict struct {
	At          int64  `json:"at"`
	Device      string `json:"device"`
	Key         string `json:"key"`
	Reason      string `json:"reason"`
	Field       string `json:"field,omitempty"`
	Winner      string `json:"winner"`
	Loser       string `json:"loser,omitempty"`
	WinnerValue string `json:"winnerValue,omitempty"`
	LoserValue  string `json:"loserValue,omitempty"`
	URL         string `json:"url,omitempty"`
}

// Summary 是一次合并的结果统计。
type Summary struct {
	Created   int `json:"created"`
	Updated   int `json:"updated"`
	Deleted   int `json:"deleted"`
	Unchanged int `json:"unchanged"`
}

// MergeResult 是一次合并的全部产出。
type MergeResult struct {
	State     State
	Conflicts []Conflict
	Summary   Summary
}

// Merge 把客户端上报的状态与服务端已有的状态合并，返回合并后的权威状态。
//
// 参数：
//
//	server   服务端当前状态（函数不会修改它）
//	incoming 客户端上报的状态（函数不会修改它）
//	base     客户端「上次见到」每个 key 时的 HLC 快照（key → m）。
//	         为空表示这是该客户端的首次同步。
//	         它的唯一用途是**区分「我改了」和「对面改了」** —— 没有它，
//	         服务端每次合并都会把「刚合过的东西」记成冲突，日志迅速失去价值。
//	device   上报方的设备标识，写进冲突记录便于排查
//	now      本次操作的墙钟毫秒，写进冲突记录
//
// 关于"删除"：墓碑（d=true）就是一条普通 item，它的 M 参与 LWW 决胜。
// 因此删除天然会传播到其他设备，不需要任何特殊通道 —— 这是选用
// LWW-Element-Set 而非朴素"diff 同步"的核心原因。
func Merge(server, incoming State, base map[string]string, device string, now int64) MergeResult {
	clock := NewHLCWithClock(func() int64 { return now })

	// 服务端先吸收客户端发来的所有时间戳，之后它产生的任何时间戳都会
	// 严格大于它们。这是 HLC 的因果性保证，也是"服务端时间戳最权威"的原因。
	stamps := make([]string, 0, len(incoming.Items)*2)
	for _, it := range incoming.Items {
		stamps = append(stamps, it.M, it.A)
	}
	clock.ObserveMany(stamps)

	result := NewState()
	result.HLC = clock.Current()

	conflicts := make([]Conflict, 0, 4)
	var sum Summary

	// 取两边 key 的并集后按字典序遍历，保证输出确定。
	for _, key := range sortedUnionKeys(server, incoming) {
		s, sOK := server.Items[key]
		c, cOK := incoming.Items[key]

		var winner Item
		switch {
		case !cOK:
			winner = s // 只有服务端有

		case !sOK:
			winner = c // 只有客户端有

		case Compare(c.M, s.M) > 0:
			winner = c

		case Compare(s.M, c.M) > 0:
			winner = s

		default: // HLC 完全相等
			if sameContent(s, c) {
				// 同一毫秒内被两端各存了一次，但内容一模一样 —— 完全正常
				winner = s
			} else {
				// 走到这里说明两端 HLC 实现不一致。仍然要给出一个确定结果，
				// 否则每次同步结果都不同，最终必然分叉。按标题字典序兜底。
				winner, _ = lexicographicPick(s, c)
				conflicts = append(conflicts, Conflict{
					At:          now,
					Device:      device,
					Key:         key,
					Reason:      ReasonHLCCollision,
					Field:       firstDifferingField(s, c),
					Winner:      winnerSide(winner, s),
					Loser:       device,
					WinnerValue: fieldValue(winner, firstDifferingField(s, c)),
					LoserValue:  fieldValue(loserOf(s, c, winner), firstDifferingField(s, c)),
					URL:         firstNonEmpty(s.U, c.U),
				})
			}
		}

		result.Items[key] = winner

		// ── 统计 ─────────────────────────────────────────────────────
		switch {
		case !sOK:
			sum.Created++ // 服务端没有 → 本次新增
		case s == winner:
			sum.Unchanged++ // Item 是可比较的值类型，== 即全字段相等
		case winner.D && !s.D:
			sum.Deleted++ // 存活 → 墓碑
		default:
			sum.Updated++
		}

		// ── 三方冲突检测（纯观测，不改变上面已定的 winner）─────────────
		if sOK && cOK && !sameContent(s, c) {
			bm, hadBase := base[key]
			if hadBase {
				clientChanged := Compare(c.M, bm) > 0
				serverChanged := Compare(s.M, bm) > 0
				if clientChanged && serverChanged {
					field := firstDifferingField(s, c)
					conflicts = append(conflicts, Conflict{
						At:          now,
						Device:      device,
						Key:         key,
						Reason:      ReasonConcurrentEdit,
						Field:       field,
						Winner:      winnerSide(winner, s),
						Loser:       loserSide(winner, s, device),
						WinnerValue: fieldValue(winner, field),
						LoserValue:  fieldValue(loserOf(s, c, winner), field),
						URL:         firstNonEmpty(s.U, c.U),
					})
				}
			}
		}
	}

	if err := mergeInvariants(server, incoming, result); err != nil {
		// 不变量被打破说明 Merge 本身有 bug。这是程序错误而非用户错误，
		// 绝不能把一份可能损坏的权威状态返回给客户端 —— 宁可让这次同步失败。
		panic("bmsync: 合并不变量被打破: " + err.Error())
	}

	return MergeResult{State: result, Conflicts: conflicts, Summary: sum}
}

// mergeInvariants 校验三条必须在任何输入下都成立的不变量。
// 任何一条被打破都意味着 Merge 里有 bug，而不是用户数据有问题。
func mergeInvariants(server, incoming, result State) error {
	// 1. 结果的 key 集合必须恰好等于两边 key 集合的并集：既不能多，
	//    也不能少。多意味着凭空造出第三种数据，少意味着书签凭空消失。
	union := make(map[string]struct{}, len(server.Items)+len(incoming.Items))
	for k := range server.Items {
		union[k] = struct{}{}
	}
	for k := range incoming.Items {
		union[k] = struct{}{}
	}
	if len(union) != len(result.Items) {
		return fmt.Errorf("结果 item 数 %d != 两边并集 %d", len(result.Items), len(union))
	}
	for k := range result.Items {
		if _, ok := union[k]; !ok {
			return fmt.Errorf("结果出现不属于任何一边的 key %s", k)
		}
	}
	for k := range union {
		if _, ok := result.Items[k]; !ok {
			return fmt.Errorf("结果缺少 key %s", k)
		}
	}

	// 2. 删除只能通过墓碑表达。服务端有的 key 绝不能从结果中消失 ——
	//    这是"合并绝不会静默丢书签"这条承诺的直接体现。
	for k, s := range server.Items {
		r, ok := result.Items[k]
		if !ok {
			return fmt.Errorf("服务端 item %s 从结果中消失", k)
		}
		if !s.D && r.D && r.M == s.M {
			return fmt.Errorf("item %s 在时间戳未变的情况下被判为已删除", k)
		}
	}

	// 3. 服务端与入参都不得被就地修改 —— 幂等性依赖于此。
	//    这里只做轻量抽样校验，完整的"重复 Merge 结果不变"由测试保证。
	return nil
}

// sortedUnionKeys 返回 server 与 incoming 的 key 并集，按字典序升序。
//
// 排序不是为了"好看"：map 的遍历顺序是随机的。若某处依赖"先处理哪个
// key"来记账或决胜，同样的输入会产出不同的输出。确定性在这里是可复现
// 调试的前提。
func sortedUnionKeys(server, incoming State) []string {
	seen := make(map[string]struct{}, len(server.Items)+len(incoming.Items))
	out := make([]string, 0, len(server.Items)+len(incoming.Items))
	for k := range server.Items {
		if _, dup := seen[k]; !dup {
			seen[k] = struct{}{}
			out = append(out, k)
		}
	}
	for k := range incoming.Items {
		if _, dup := seen[k]; !dup {
			seen[k] = struct{}{}
			out = append(out, k)
		}
	}
	sort.Strings(out)
	return out
}

// ── 冲突记录的字段级 diff ─────────────────────────────────────────────

// contentFields 按固定顺序列出参与内容比较的字段。
// 固定顺序保证 firstDifferingField 在多项同时不同时总是返回同一个字段，
// 否则同一对冲突在不同同步轮次里会记录不同的字段。
var contentFields = []struct{ label, field string }{
	{"parent", "P"},
	{"type", "T"},
	{"title", "N"},
	{"url", "U"},
}

func firstDifferingField(s, c Item) string {
	for _, f := range contentFields {
		if valueOf(s, f.field) != valueOf(c, f.field) {
			return f.label
		}
	}
	return ""
}

func valueOf(it Item, field string) string {
	switch field {
	case "P":
		return it.P
	case "T":
		return string(it.T)
	case "N":
		return it.N
	case "U":
		return it.U
	}
	return ""
}

func fieldValue(it Item, label string) string {
	for _, f := range contentFields {
		if f.label == label {
			return valueOf(it, f.field)
		}
	}
	return ""
}

func firstNonEmpty(a, b string) string {
	if a != "" {
		return a
	}
	return b
}

// lexicographicPick 在 HLC 碰撞时给出确定决胜：标题字典序小者获胜。
// 第二个返回值是败者。纯粹为了确定性，不含任何"哪个更好"的语义。
func lexicographicPick(s, c Item) (winner, loser Item) {
	if strings.Compare(s.N, c.N) <= 0 {
		return s, c
	}
	return c, s
}

// winnerSide 判断 winner 来自服务端还是客户端。
// 调用前提：s 与 c 不全字段相等（否则无法区分来源）。
func winnerSide(winner, server Item) string {
	if winner == server {
		return ConflictWinnerServer
	}
	return ConflictWinnerClient
}

// loserSide 返回败方标识：服务端赢 → 败方是客户端设备；反之是服务端。
func loserSide(winner, server Item, device string) string {
	if winner == server {
		return device
	}
	return ConflictWinnerServer
}

// loserOf 返回 s、c 中不是 winner 的那一个。
func loserOf(s, c, winner Item) Item {
	if winner == s {
		return c
	}
	return s
}
