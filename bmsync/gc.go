package main

import "time"

// GC 清理过期墓碑。
//
// ── 为什么需要墓碑 ────────────────────────────────────────────────────
//
// 墓碑（d=true 的 item）存在的唯一理由是**把删除传播到其他设备**。
// 一台设备删了书签，这个事实在 state 里必须留痕，否则另一台设备会
// 认为自己本地那个书签是"服务端没有的新增"，从而重新上传它 ——
// 书签会复活，而且用户会觉得莫名其妙。
//
// ── 为什么要清理 ──────────────────────────────────────────────────────
//
// 墓碑永远留着会让 state.json 无限膨胀。且它的信息价值有保质期：
// 只要**所有**设备的本地书签树里都已经没有这个书签，墓碑就没有用了。
//
// 90 天这个数字的依据：它远大于任何"两台设备之间最长可能的不同步间隔"。
// 超过 90 天没同步过的设备，其数据本身就已经不可靠了（见 design.md §12.3）。
//
// ── 安全约束 ──────────────────────────────────────────────────────────
//
//   1. **只删墓碑。** 任何 d=false 的活跃 item 都不会被 GC 碰。
//      这是最重要的一条 —— GC 出 bug 的后果是静默丢书签。
//   2. **只看 x（删除墙钟时间），不看 m。** m 是 HLC，时钟被改过时
//      它的含义会漂移；x 是普通墙钟，虽然也不可靠但语义明确。
//   3. **x == 0 的墓碑不删。** 删不掉的比删错好：它可能是刚产生的
//      墓碑但时间戳没写上，留着只是浪费几百字节。
func GC(items map[string]Item, now int64, ttl time.Duration) (map[string]Item, int) {
	cutoff := now - ttl.Milliseconds()
	out := make(map[string]Item, len(items))
	removed := 0

	for k, it := range items {
		if it.D && it.X > 0 && it.X < cutoff {
			removed++
			continue
		}
		out[k] = it
	}
	return out, removed
}

// GCTombstones 是 GC 的原地版本，直接替换 s.Items。
// 返回被清理的数量，供 summary.Deleted 统计使用。
func GCTombstones(s *State, now int64, ttl time.Duration) int {
	items, removed := GC(s.Items, now, ttl)
	if removed > 0 {
		s.Items = items
	}
	return removed
}
