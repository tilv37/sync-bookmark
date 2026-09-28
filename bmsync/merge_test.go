package main

import (
	"crypto/sha256"
	"encoding/hex"
	"math/rand"
	"sort"
	"testing"
)

// ── 测试辅助 ──────────────────────────────────────────────────────────

// keyOf 由标签确定性地生成一个合法 key（32 位十六进制）。
func keyOf(label string) string {
	sum := sha256.Sum256([]byte("bmsync-test:" + label))
	return hex.EncodeToString(sum[:16])
}

type keyedItem struct {
	k string
	i Item
}

func kv(label string, it Item) keyedItem { return keyedItem{keyOf(label), it} }

func bookmark(parent, title, url string, ms uint64, c uint32) Item {
	return Item{P: parent, T: TypeBookmark, N: title, U: url,
		M: Encode(ms, c), A: Encode(ms, 0)}
}

func folder(parent, title string, ms uint64, c uint32) Item {
	return Item{P: parent, T: TypeFolder, N: title, M: Encode(ms, c), A: Encode(ms, 0)}
}

func tomb(it Item, at int64, ms uint64, c uint32) Item {
	it.D = true
	it.X = at
	it.M = Encode(ms, c)
	return it
}

func stateOf(items ...keyedItem) State {
	s := NewState()
	for _, ki := range items {
		s.Items[ki.k] = ki.i
	}
	return s
}

const (
	tA = "device-a"
	tB = "device-b"
	t0 = int64(1_700_000_000_000) // 固定墙钟，保证测试确定
)

func names(s State) []string {
	out := make([]string, 0, len(s.Items))
	for k := range s.Items {
		out = append(out, k)
	}
	sort.Strings(out)
	return out
}

// ── 基础合并 ──────────────────────────────────────────────────────────

func TestMergeEmptyBothSides(t *testing.T) {
	r := Merge(NewState(), NewState(), nil, tA, t0)
	if len(r.State.Items) != 0 {
		t.Fatalf("空合并产生了 %d 个 item", len(r.State.Items))
	}
	if r.Summary.Created+r.Summary.Updated+r.Summary.Deleted+r.Summary.Unchanged != 0 {
		t.Errorf("空合并的 summary 应全为 0，实际 %+v", r.Summary)
	}
}

func TestMergeServerEmptyClientHasItems(t *testing.T) {
	// 工作电脑第一次同步：本地有书签，云端还是空的
	incoming := stateOf(
		kv("f1", folder(RootToolbar, "工作", 100, 0)),
		kv("b1", bookmark(keyOf("f1"), "示例", "https://example.com", 100, 1)),
	)
	r := Merge(NewState(), incoming, nil, tB, t0)

	if len(r.State.Items) != 2 {
		t.Fatalf("应有 2 个 item，实际 %d", len(r.State.Items))
	}
	if r.Summary.Created != 2 {
		t.Errorf("Summary.Created = %d，期望 2", r.Summary.Created)
	}
	if len(r.Conflicts) != 0 {
		t.Errorf("首次同步不应产生冲突，实际 %d 条", len(r.Conflicts))
	}
}

func TestMergeClientEmptyServerKeepsEverything(t *testing.T) {
	// 云端有数据，另一台设备本地什么都没有（例如换了新 profile）
	server := stateOf(kv("b1", bookmark(RootUnfiled, "示例", "https://example.com", 100, 0)))
	r := Merge(server, NewState(), nil, tB, t0)

	if len(r.State.Items) != 1 {
		t.Fatalf("服务端数据被丢掉了：%d 个 item", len(r.State.Items))
	}
	if r.Summary.Unchanged != 1 || r.Summary.Created != 0 {
		t.Errorf("summary = %+v，期望 unchanged=1", r.Summary)
	}
}

func TestMergeDisjointItemsFromBothSides(t *testing.T) {
	server := stateOf(kv("a", bookmark(RootToolbar, "A", "https://a.example", 100, 0)))
	incoming := stateOf(kv("b", bookmark(RootToolbar, "B", "https://b.example", 100, 0)))

	r := Merge(server, incoming, nil, tB, t0)
	if len(r.State.Items) != 2 {
		t.Fatalf("并集应为 2，实际 %d", len(r.State.Items))
	}
	if r.Summary.Created != 1 || r.Summary.Unchanged != 1 {
		t.Errorf("summary = %+v", r.Summary)
	}
}

func TestMergeLWWNewerWins(t *testing.T) {
	older := bookmark(RootToolbar, "旧标题", "https://example.com", 100, 0)
	newer := bookmark(RootToolbar, "新标题", "https://example.com", 200, 0)
	k := keyOf("b")

	t.Run("客户端更新", func(t *testing.T) {
		s := NewState()
		s.Items[k] = older
		i := NewState()
		i.Items[k] = newer
		r := Merge(s, i, nil, tB, t0)
		if r.State.Items[k].N != "新标题" {
			t.Errorf("客户端较新却没赢：%q", r.State.Items[k].N)
		}
		if r.Summary.Updated != 1 {
			t.Errorf("summary = %+v，期望 updated=1", r.Summary)
		}
	})

	t.Run("服务端更新", func(t *testing.T) {
		s := NewState()
		s.Items[k] = newer // M = 200
		i := NewState()
		i.Items[k] = older // M = 100
		r := Merge(s, i, nil, tB, t0)
		if r.State.Items[k].N != "新标题" {
			t.Errorf("服务端较新却没赢：%q", r.State.Items[k].N)
		}
	})
}

func TestMergeEqualTimestampSameContent(t *testing.T) {
	k := keyOf("b")
	it := bookmark(RootToolbar, "标题", "https://example.com", 100, 3)
	s, i := NewState(), NewState()
	s.Items[k], i.Items[k] = it, it

	r := Merge(s, i, nil, tB, t0)
	if r.State.Items[k] != it {
		t.Errorf("内容与时间戳都相同时应原样保留")
	}
	if len(r.Conflicts) != 0 {
		t.Errorf("内容一致不应记为碰撞冲突，实际 %+v", r.Conflicts)
	}
	if r.Summary.Unchanged != 1 {
		t.Errorf("summary = %+v，期望 unchanged=1", r.Summary)
	}
}

func TestMergeEqualTimestampDifferentContentIsDeterministic(t *testing.T) {
	k := keyOf("b")
	s, i := NewState(), NewState()
	s.Items[k] = bookmark(RootToolbar, "AAA", "https://example.com", 100, 3)
	i.Items[k] = bookmark(RootToolbar, "BBB", "https://example.com", 100, 3)

	var first string
	for run := 0; run < 50; run++ {
		r := Merge(s, i, nil, tB, t0)
		if run == 0 {
			first = r.State.Items[k].N
		} else if r.State.Items[k].N != first {
			t.Fatalf("第 %d 次运行结果不同：%q vs %q —— 合并非确定", run, r.State.Items[k].N, first)
		}
	}
	if r0 := Merge(s, i, nil, tB, t0).Conflicts; len(r0) != 1 {
		t.Fatalf("HLC 碰撞应记 1 条冲突，实际 %d", len(r0))
	}
}

// ── 删除与墓碑 ────────────────────────────────────────────────────────

func TestMergeDeletePropagates(t *testing.T) {
	k := keyOf("b")
	alive := bookmark(RootToolbar, "标题", "https://example.com", 100, 0)
	dead := tomb(alive, t0, 200, 0)

	s, i := NewState(), NewState()
	s.Items[k], i.Items[k] = alive, dead

	r := Merge(s, i, nil, tB, t0)
	if !r.State.Items[k].D {
		t.Error("客户端的删除（较新）没有传播到服务端状态")
	}
	if r.Summary.Deleted != 1 {
		t.Errorf("summary = %+v，期望 deleted=1", r.Summary)
	}
}

func TestMergeOlderDeleteLoses(t *testing.T) {
	// 设备 A 删了书签，设备 B 在此之后又改了这个书签（说明它还在用）
	k := keyOf("b")
	dead := tomb(bookmark(RootToolbar, "标题", "https://example.com", 100, 0), t0, 100, 0)
	revived := bookmark(RootToolbar, "标题", "https://example.com", 200, 0)

	s, i := NewState(), NewState()
	s.Items[k], i.Items[k] = dead, revived

	r := Merge(s, i, nil, tB, t0)
	if r.State.Items[k].D {
		t.Error("较旧的删除不应覆盖较新的修改")
	}
}

func TestMergeBothSidesTombstoned(t *testing.T) {
	k := keyOf("b")
	alive := bookmark(RootToolbar, "标题", "https://example.com", 100, 0)
	s, i := NewState(), NewState()
	s.Items[k] = tomb(alive, t0, 200, 0)
	i.Items[k] = tomb(alive, t0, 100, 0) // 较旧的删除

	r := Merge(s, i, nil, tB, t0)
	got := r.State.Items[k]
	if !got.D {
		t.Fatal("两侧都是墓碑，结果必须是墓碑")
	}
	if Compare(got.M, Encode(200, 0)) != 0 {
		t.Errorf("m 应保留较新的 %q，实际 %q", Encode(200, 0), got.M)
	}
	// x 保留较早的那个：GC 按 x 判定，越早清理越省空间
	if got.X != t0 {
		t.Errorf("x 应保留较早的删除时间 %d，实际 %d", t0, got.X)
	}
}

func TestMergeResurrection(t *testing.T) {
	// 墓碑 + 客户端以更新的时间戳重新添加同一 URL → 书签复活
	k := keyOf("b")
	alive := bookmark(RootToolbar, "标题", "https://example.com", 100, 0)

	s, i := NewState(), NewState()
	s.Items[k] = tomb(alive, t0, 200, 0)
	alive.M = Encode(300, 0) // 客户端以更新的时间戳重新添加
	alive.D = false
	alive.X = 0
	i.Items[k] = alive

	r := Merge(s, i, nil, tB, t0)
	if r.State.Items[k].D {
		t.Error("较新的重新添加应让书签复活")
	}
	if r.State.Items[k].N != "标题" {
		t.Errorf("复活后内容错误：%q", r.State.Items[k].N)
	}
}

// ── 幂等性与收敛性 ────────────────────────────────────────────────────

// 重复同步同一个请求，结果必须完全一致。
// 这是"用户连点三次同步按钮"不会把书签搞乱的前提。
func TestMergeIsIdempotent(t *testing.T) {
	server := stateOf(
		kv("f", folder(RootToolbar, "目录", 100, 0)),
		kv("b1", bookmark(keyOf("f"), "一", "https://one.example", 100, 1)),
		kv("b2", tomb(bookmark(RootToolbar, "二", "https://two.example", 100, 0), t0, 100, 2)),
	)
	incoming := stateOf(
		kv("f", folder(RootToolbar, "目录", 100, 0)),
		kv("b1", bookmark(keyOf("f"), "一改", "https://one.example", 200, 0)),
		kv("b3", bookmark(RootMenu, "三", "https://three.example", 200, 1)),
		kv("b2", tomb(bookmark(RootToolbar, "二", "https://two.example", 100, 0), t0, 100, 2)),
	)

	once := Merge(server, incoming, nil, tB, t0).State
	for n := 0; n < 5; n++ {
		again := Merge(server, incoming, nil, tB, t0).State
		if !sameStates(once, again) {
			t.Fatalf("第 %d 次重复同步结果不同", n+1)
		}
	}
	// 把结果再当服务端、拿同一个请求合并一次，也不应变化
	twice := Merge(once, incoming, nil, tB, t0).State
	if !sameStates(once, twice) {
		t.Error("以合并结果为服务端再次合并，结果发生了变化")
	}
}

// 乱序同步最终必须收敛：无论 A、B 的到达顺序如何，服务端的权威状态相同。
func TestMergeConvergesRegardlessOfOrder(t *testing.T) {
	base := stateOf(
		kv("f", folder(RootToolbar, "目录", 100, 0)),
		kv("b1", bookmark(keyOf("f"), "一", "https://one.example", 100, 1)),
		kv("b2", bookmark(RootUnfiled, "二", "https://two.example", 100, 2)),
	)
	patchA := stateOf(
		kv("b1", bookmark(keyOf("f"), "一改", "https://one.example", 300, 0)),
		kv("b3", bookmark(RootUnfiled, "三", "https://three.example", 250, 0)),
	)
	patchB := stateOf(
		kv("b2", bookmark(RootUnfiled, "二改", "https://two.example", 400, 0)),
	)

	ab := Merge(Merge(base, patchA, nil, tA, t0).State, patchB, nil, tB, t0).State
	ba := Merge(Merge(base, patchB, nil, tB, t0).State, patchA, nil, tA, t0).State
	if !sameStates(ab, ba) {
		t.Errorf("A→B 与 B→A 顺序结果不一致：\n AB: %v\n BA: %v", summarize(ab), summarize(ba))
	}
	if len(ab.Items) != 4 {
		t.Errorf("最终应有 4 个 item（含目录），实际 %d", len(ab.Items))
	}
	if ab.Items[keyOf("b2")].N != "二改" {
		t.Errorf("b2 应由较新的 B 胜出，实际 %q", ab.Items[keyOf("b2")].N)
	}
	if ab.Items[keyOf("b3")].N != "三" {
		t.Errorf("b3 是 A 独有的新增，应保留，实际 %q", ab.Items[keyOf("b3")].N)
	}
}

// ── 冲突检测 ──────────────────────────────────────────────────────────

// 首次同步（base 为空）绝不能报冲突，否则用户第一次点同步就被
// 一屏冲突记录淹没，这个功能就废了。
func TestMergeNoConflictWhenBaseIsEmpty(t *testing.T) {
	server := stateOf(kv("b", bookmark(RootToolbar, "云端标题", "https://example.com", 200, 0)))
	incoming := stateOf(kv("b", bookmark(RootToolbar, "本地标题", "https://example.com", 100, 0)))

	r := Merge(server, incoming, nil, tB, t0) // base = nil
	if len(r.Conflicts) != 0 {
		t.Errorf("首次同步不应报冲突，实际 %+v", r.Conflicts)
	}
	if r.State.Items[keyOf("b")].N != "云端标题" {
		t.Error("base 为空时按 LWW 决胜，云端较新应胜出")
	}
}

// 只有一端改动 → 不是冲突。
func TestMergeNoConflictOnOneSidedEdit(t *testing.T) {
	orig := bookmark(RootToolbar, "原标题", "https://example.com", 100, 0)
	base := map[string]string{keyOf("b"): orig.M}

	t.Run("只有客户端改", func(t *testing.T) {
		server := stateOf(kv("b", orig))
		incoming := stateOf(kv("b", bookmark(RootToolbar, "新标题", "https://example.com", 300, 0)))
		r := Merge(server, incoming, base, tB, t0)
		if len(r.Conflicts) != 0 {
			t.Errorf("单边编辑不应记为冲突，实际 %+v", r.Conflicts)
		}
	})

	t.Run("只有服务端改", func(t *testing.T) {
		server := stateOf(kv("b", bookmark(RootToolbar, "云端改", "https://example.com", 300, 0)))
		incoming := stateOf(kv("b", orig))
		r := Merge(server, incoming, base, tB, t0)
		if len(r.Conflicts) != 0 {
			t.Errorf("单边编辑不应记为冲突，实际 %+v", r.Conflicts)
		}
	})
}

func TestMergeDetectsConcurrentEdit(t *testing.T) {
	orig := bookmark(RootToolbar, "原标题", "https://example.com", 100, 0)
	base := map[string]string{keyOf("b"): orig.M}

	server := stateOf(kv("b", bookmark(RootToolbar, "云端改的", "https://example.com", 300, 0)))
	incoming := stateOf(kv("b", bookmark(RootToolbar, "本地改的", "https://example.com", 200, 0)))

	r := Merge(server, incoming, base, tB, t0)
	if len(r.Conflicts) != 1 {
		t.Fatalf("真并发编辑应记 1 条冲突，实际 %d: %+v", len(r.Conflicts), r.Conflicts)
	}
	c := r.Conflicts[0]
	if c.Reason != ReasonConcurrentEdit {
		t.Errorf("Reason = %q，期望 %q", c.Reason, ReasonConcurrentEdit)
	}
	if c.Field != "title" {
		t.Errorf("Field = %q，期望 title", c.Field)
	}
	if c.Winner != ConflictWinnerServer || c.Loser != tB {
		t.Errorf("决胜方记录错误：winner=%q loser=%q", c.Winner, c.Loser)
	}
	if c.WinnerValue != "云端改的" || c.LoserValue != "本地改的" {
		t.Errorf("字段值记录错误：winner=%q loser=%q", c.WinnerValue, c.LoserValue)
	}
	if c.URL != "https://example.com" {
		t.Errorf("URL = %q", c.URL)
	}
	// 冲突是纯观测：决胜结果仍按 LWW
	if r.State.Items[keyOf("b")].N != "云端改的" {
		t.Errorf("决胜结果错误：%q", r.State.Items[keyOf("b")].N)
	}
}

func TestMergeConflictReportsCorrectField(t *testing.T) {
	orig := bookmark(RootToolbar, "标题", "https://example.com", 100, 0)
	base := map[string]string{keyOf("b"): orig.M}
	k := keyOf("b")

	cases := []struct {
		name      string
		serverIt  Item
		clientIt  Item
		wantField string
	}{
		{"标题变了", orig, bookmark(RootToolbar, "新", "https://example.com", 200, 0), "title"},
		{"URL 变了", orig, bookmark(RootToolbar, "标题", "https://other.example", 200, 0), "url"},
		{"父目录变了", orig, bookmark(RootMenu, "标题", "https://example.com", 200, 0), "parent"},
		// 书签变成文件夹：P 相同、N 相同，只有 T 不同 —— firstDifferingField
		// 按 parent→type→title→url 的固定顺序取第一个差异，所以是 "type"
		{"书签变成文件夹", orig, folder(RootToolbar, "标题", 200, 0), "type"},
		// P 与 T 同时不同：固定顺序保证一定报 "parent"，不随同步轮次变化
		{"父目录和类型都变了", orig, folder(RootMenu, "标题", 200, 0), "parent"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			s, i := NewState(), NewState()
			s.Items[k], i.Items[k] = tc.serverIt, tc.clientIt
			// 两侧都"改过"：服务端基线也要晚于 base
			tc.serverIt.M = Encode(300, 0)
			s.Items[k] = tc.serverIt

			r := Merge(s, i, base, tB, t0)
			if len(r.Conflicts) != 1 {
				t.Fatalf("期望 1 条冲突，实际 %d", len(r.Conflicts))
			}
			if r.Conflicts[0].Field != tc.wantField {
				t.Errorf("Field = %q，期望 %q", r.Conflicts[0].Field, tc.wantField)
			}
		})
	}
}

// ── 不变量 ────────────────────────────────────────────────────────────

func TestMergeDoesNotMutateInputs(t *testing.T) {
	server := stateOf(kv("b", bookmark(RootToolbar, "一", "https://one.example", 100, 0)))
	incoming := stateOf(kv("b", bookmark(RootToolbar, "二", "https://one.example", 200, 0)))
	base := map[string]string{keyOf("b"): Encode(50, 0)}

	serverBefore := server.Clone()
	incomingBefore := incoming.Clone()
	baseBefore := make(map[string]string, len(base))
	for k, v := range base {
		baseBefore[k] = v
	}

	Merge(server, incoming, base, tB, t0)

	if !sameStates(server, serverBefore) {
		t.Error("Merge 修改了入参 server")
	}
	if !sameStates(incoming, incomingBefore) {
		t.Error("Merge 修改了入参 incoming")
	}
	for k, v := range base {
		if baseBefore[k] != v {
			t.Errorf("Merge 修改了入参 base[%s]", k)
		}
	}
	if len(base) != len(baseBefore) {
		t.Error("Merge 往入参 base 里写了东西")
	}
}

// 服务端有的 item 绝不会凭空消失 —— 删除必须以墓碑的形式显式表达。
// 这是"合并绝不会静默丢书签"这条承诺的直接体现。
func TestMergeNeverDropsServerItem(t *testing.T) {
	server := stateOf(
		kv("a", bookmark(RootToolbar, "一", "https://one.example", 100, 0)),
		kv("b", bookmark(RootToolbar, "二", "https://two.example", 100, 0)),
		kv("c", folder(RootUnfiled, "目录", 100, 0)),
	)
	incoming := stateOf() // 客户端什么都没上报

	r := Merge(server, incoming, nil, tB, t0)
	for _, k := range names(server) {
		if _, ok := r.State.Items[k]; !ok {
			t.Fatalf("服务端 item %s 从结果中消失了", k)
		}
	}
}

func TestMergeResultIsSelfConsistent(t *testing.T) {
	server := stateOf(
		kv("f", folder(RootToolbar, "目录", 100, 0)),
		kv("b", tomb(bookmark(keyOf("f"), "一", "https://one.example", 100, 0), t0, 100, 0)),
	)
	incoming := stateOf(
		kv("f", folder(RootToolbar, "目录", 100, 0)),
		kv("b", bookmark(keyOf("f"), "一", "https://one.example", 300, 0)), // 复活
	)

	r := Merge(server, incoming, nil, tB, t0)
	if _, err := r.State.Validate(); err != nil {
		t.Errorf("合并结果未通过自身的 Validate: %v", err)
	}
}

// ── 随机化性质测试 ────────────────────────────────────────────────────
//
// 手写用例只能覆盖"想得到的场景"。真正能抓住边界 bug 的是随机化收敛测试：
// 生成随机的操作序列，以任意顺序重放，服务端必须收敛到同一个状态。

func TestMergePropertyConvergence(t *testing.T) {
	rnd := rand.New(rand.NewSource(20260928))

	// 构造一个共享的"真相"集合，模拟三台设备各自的副本
	const devices = 3
	truth := make([]State, devices)
	for d := range truth {
		truth[d] = NewState()
	}
	clock := uint64(1000)

	// 随机操作：每个操作作用于一台设备
	for op := 0; op < 400; op++ {
		clock += uint64(rnd.Intn(3))
		d := rnd.Intn(devices)
		action := rnd.Intn(4)
		k := keyOf("item" + string(rune('a'+rnd.Intn(20))))

		switch action {
		case 0: // 新增或覆盖
			truth[d].Items[k] = bookmark(RootToolbar, "t"+k[:4], "https://e.example/"+k[:4], clock, uint32(rnd.Intn(50)))
		case 1: // 删除（墓碑）
			if prev, ok := truth[d].Items[k]; ok {
				truth[d].Items[k] = tomb(prev, t0, clock, uint32(rnd.Intn(50)))
			}
		case 2: // 改标题
			if prev, ok := truth[d].Items[k]; ok {
				prev.N = "changed" + k[:4]
				prev.M = Encode(clock, uint32(rnd.Intn(50)))
				truth[d].Items[k] = prev
			}
		case 3: // 删除本地副本（模拟设备换了新 profile，上报空状态）
			// 此时设备会上报它缓存里的内容，这里不改动 truth，只影响提交内容
		}
	}

	// 把三台设备的副本以各种顺序合并到服务端
	server := NewState()
	order := []int{0, 1, 2}
	rnd.Shuffle(len(order), func(i, j int) { order[i], order[j] = order[j], order[i] })
	for _, d := range order {
		server = Merge(server, truth[d], nil, "dev", t0).State
	}

	// 再以另一个顺序合并一遍，结果必须不变（收敛）
	reverseOrder := []int{2, 0, 1}
	again := NewState()
	for _, d := range reverseOrder {
		again = Merge(again, truth[d], nil, "dev", t0).State
	}
	if !sameStates(server, again) {
		t.Errorf("不同合并顺序得到不同结果，收敛性被破坏")
	}

	// 最终结果必须通过校验
	if _, err := server.Validate(); err != nil {
		t.Errorf("随机化合并结果未通过 Validate: %v", err)
	}

	// 关键：服务端的结果必须包含每台设备上报过的**所有** key
	allKeys := map[string]bool{}
	for d := range truth {
		for k := range truth[d].Items {
			allKeys[k] = true
		}
	}
	for k := range allKeys {
		if _, ok := server.Items[k]; !ok {
			t.Fatalf("随机化测试中 key %s 消失了", k)
		}
	}
	t.Logf("随机化收敛测试：%d 个 key，合并顺序 %v", len(allKeys), order)
}

// ── 辅助 ──────────────────────────────────────────────────────────────

func sameStates(a, b State) bool {
	if len(a.Items) != len(b.Items) {
		return false
	}
	for k, va := range a.Items {
		vb, ok := b.Items[k]
		if !ok || va != vb {
			return false
		}
	}
	return true
}

func summarize(s State) map[string]string {
	out := make(map[string]string, len(s.Items))
	for k, v := range s.Items {
		out[k[:6]] = v.N + fmtDeleted(v)
	}
	return out
}

func fmtDeleted(it Item) string {
	if it.D {
		return "(deleted)"
	}
	return ""
}
