package main

import (
	"testing"
	"time"
)

const day = int64(24 * 60 * 60 * 1000)

func TestGCRemovesOnlyExpiredTombstones(t *testing.T) {
	now := t0
	ttl := 90 * 24 * time.Hour

	items := map[string]Item{
		// 活跃项：无论多旧都不动
		keyOf("live-old"):    bookmark(RootToolbar, "很久以前加的", "https://a.example", 1, 0),
		keyOf("live-new"):    bookmark(RootToolbar, "刚加的", "https://b.example", 999_999, 0),
		// 墓碑 91 天前 → 应被清理
		keyOf("dead-91d"):  tomb(bookmark(RootToolbar, "x", "https://c.example", 100, 0), now-91*day, 100, 0),
		// 墓碑 89 天前 → 保留
		keyOf("dead-89d"):  tomb(bookmark(RootToolbar, "y", "https://d.example", 100, 0), now-89*day, 100, 0),
		// 墓碑刚删 → 保留
		keyOf("dead-today"): tomb(bookmark(RootToolbar, "z", "https://e.example", 100, 0), now-1*day, 100, 0),
	}

	out, removed := GC(items, now, ttl)

	if removed != 1 {
		t.Errorf("应清理 1 条墓碑，实际 %d", removed)
	}
	if _, ok := out[keyOf("dead-91d")]; ok {
		t.Error("91 天前的墓碑应被清理")
	}
	for _, label := range []string{"dead-89d", "dead-today", "live-old", "live-new"} {
		if _, ok := out[keyOf(label)]; !ok {
			t.Errorf("%s 不应被清理", label)
		}
	}
	if len(out) != 4 {
		t.Errorf("清理后应剩 4 项，实际 %d", len(out))
	}
}

// GC 出 bug 的后果是静默丢书签，所以这几条约束必须被钉死。
func TestGCNeverTouchesActiveItems(t *testing.T) {
	now := t0
	items := map[string]Item{
		keyOf("a"): bookmark(RootToolbar, "a", "https://a.example", 1, 0),
		keyOf("b"): folder(RootUnfiled, "b", 1, 0),
		keyOf("c"): {P: RootMenu, T: TypeFolder, N: "c", M: Encode(1, 0), A: Encode(1, 0)},
	}
	out, removed := GC(items, now, 0) // TTL = 0，什么都该过期
	if removed != 0 {
		t.Errorf("没有任何墓碑时不应清理任何东西，却清理了 %d 条", removed)
	}
	if len(out) != 3 {
		t.Fatalf("活跃项被误删：%d/3 存活", len(out))
	}
}

// x == 0 表示"不知道什么时候删的"。宁可留着一个墓碑（浪费几百字节），
// 也不能删掉一个"删除时间未知"的事实 —— 那会让删除同步静默失效。
func TestGCKeepsTombstoneWithoutDeletionTime(t *testing.T) {
	now := t0
	it := bookmark(RootToolbar, "x", "https://x.example", 100, 0)
	it.D = true
	it.X = 0 // 缺失

	out, removed := GC(map[string]Item{keyOf("x"): it}, now, 0)
	if removed != 0 {
		t.Errorf("x=0 的墓碑不应被清理，实际清理了 %d 条", removed)
	}
	if _, ok := out[keyOf("x")]; !ok {
		t.Error("x=0 的墓碑被丢掉了")
	}
}

func TestGCEdgeCases(t *testing.T) {
	t.Run("空 map", func(t *testing.T) {
		out, removed := GC(map[string]Item{}, t0, 90*24*time.Hour)
		if removed != 0 || len(out) != 0 {
			t.Errorf("空 map 处理异常：removed=%d len=%d", removed, len(out))
		}
	})

	t.Run("nil map", func(t *testing.T) {
		out, removed := GC(nil, t0, 90*24*time.Hour)
		if removed != 0 || len(out) != 0 {
			t.Errorf("nil map 处理异常：removed=%d len=%d", removed, len(out))
		}
	})

	t.Run("恰好等于 TTL 边界", func(t *testing.T) {
		ttl := 90 * 24 * time.Hour
		at := t0 - ttl.Milliseconds()
		it := tomb(bookmark(RootToolbar, "x", "https://x.example", 1, 0), at, 1, 0)
		// 判定是 x < cutoff（严格小于），恰好等于边界时保留
		_, removed := GC(map[string]Item{keyOf("x"): it}, t0, ttl)
		if removed != 0 {
			t.Error("恰好等于 TTL 边界的墓碑应保留（判定为严格小于）")
		}
		// 再早 1 毫秒就该被清理
		it.X = at - 1
		if _, removed := GC(map[string]Item{keyOf("x"): it}, t0, ttl); removed != 1 {
			t.Error("比边界早 1 毫秒的墓碑应被清理")
		}
	})

	t.Run("原 map 不被修改", func(t *testing.T) {
		items := map[string]Item{
			keyOf("d"): tomb(bookmark(RootToolbar, "x", "https://x.example", 1, 0), t0-day, 1, 0),
		}
		GC(items, t0, 90*24*time.Hour)
		if len(items) != 1 {
			t.Error("GC 修改了传入的 map")
		}
	})
}

func TestGCTombstonesInPlace(t *testing.T) {
	s := stateOf(
		kv("live", bookmark(RootToolbar, "活", "https://a.example", 1, 0)),
		kv("dead", tomb(bookmark(RootToolbar, "死", "https://b.example", 1, 0), t0-100*day, 1, 0)),
	)
	removed := GCTombstones(&s, t0, 90*24*time.Hour)
	if removed != 1 {
		t.Errorf("GCTombstones 清理了 %d 条，期望 1", removed)
	}
	if len(s.Items) != 1 {
		t.Errorf("清理后应剩 1 项，实际 %d", len(s.Items))
	}

	// 无事可做时不应替换 map 指针（避免无谓的分配）
	before := s.Items
	GCTombstones(&s, t0, 90*24*time.Hour)
	if &before != &s.Items && len(before) != len(s.Items) {
		t.Error("无事可做时不应重建 map")
	}
}

// 完整生命周期：创建 → 同步 → 删除 → 同步 → GC 清理。
// 确认墓碑真的会在 90 天后消失，不会无限堆积。
func TestGCTombstoneLifecycle(t *testing.T) {
	k := keyOf("b")
	s := NewState()
	s.Items[k] = bookmark(RootToolbar, "标题", "https://example.com", 100, 0)

	// 100 天后被删除
	deleteAt := t0 + 100*day
	s.Items[k] = tomb(s.Items[k], deleteAt, 200, 0)
	if s.Count() != 0 {
		t.Error("墓碑应不计入活跃数")
	}

	// 删除后 89 天：仍在
	if f := GCTombstones(&s, deleteAt+89*day, 90*24*time.Hour); f != 0 {
		t.Errorf("删除后 89 天不应清理，实际清理了 %d 条", f)
	}
	// 删除后 91 天：清理
	if f := GCTombstones(&s, deleteAt+91*day, 90*24*time.Hour); f != 1 {
		t.Errorf("删除后 91 天应清理 1 条，实际 %d", f)
	}
	if len(s.Items) != 0 {
		t.Errorf("清理后 state 应为空，实际 %d 项", len(s.Items))
	}
}
