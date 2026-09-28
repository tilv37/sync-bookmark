package main

import (
	"encoding/json"
	"math/rand"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"testing"
	"time"
)

// fixedClock 是可手工推进的时钟，让 HLC 测试完全确定、不依赖真实时间。
type fixedClock struct{ ms int64 }

func (c *fixedClock) now() int64        { return c.ms }
func (c *fixedClock) set(ms int64)      { c.ms = ms }
func (c *fixedClock) add(d int64) int64 { c.ms += d; return c.ms }

// ── 编码 ──────────────────────────────────────────────────────────────

func TestHLCEncodeFormat(t *testing.T) {
	cases := []struct {
		l, c uint64
		want  string
	}{
		{0, 0, "0000000000000-00000"},
		{1000, 0, "0000000001000-00000"},
		{1000, 42, "0000000001000-00042"},
		{1790000000000, 99999, "1790000000000-99999"},
		{9223372036854, 1, "9223372036854-00001"}, // 13 位上限
	}
	for _, tc := range cases {
		if got := Encode(tc.l, uint32(tc.c)); got != tc.want {
			t.Errorf("Encode(%d, %d) = %q, want %q", tc.l, tc.c, got, tc.want)
		}
	}
}

func TestHLCDecodeRoundTrip(t *testing.T) {
	rnd := rand.New(rand.NewSource(1))
	for i := 0; i < 2000; i++ {
		l := uint64(rnd.Int63n(9_000_000_000_000))
		c := uint32(rnd.Intn(100_000))
		s := Encode(l, c)
		gl, gc, err := Decode(s)
		if err != nil {
			t.Fatalf("Decode(%q) 出错: %v", s, err)
		}
		if gl != l || gc != c {
			t.Fatalf("往返失败: Encode(%d,%d)=%q → Decode 得到 (%d,%d)", l, c, s, gl, gc)
		}
	}
}

func TestHLCDecodeRejectsGarbage(t *testing.T) {
	bad := []string{
		"", "123", "0000000001000-0000", "0000000001000000000",
		"0000000001000_00000", "0000000001000-0000x", "abc-00000",
		"0000000001000-999999", // 6 位计数
		"-0000000001000-00000",
	}
	for _, s := range bad {
		if _, _, err := Decode(s); err == nil {
			t.Errorf("Decode(%q) 应当报错却通过了", s)
		}
		if ValidHLC(s) {
			t.Errorf("ValidHLC(%q) 应当为 false", s)
		}
	}
}

// 字典序 == 时间序。这是整个编码格式存在的理由：合并时可以在任何语言、
// 任何数据结构里直接比较字符串，不需要解析。
func TestHLCStringOrderEqualsTimeOrder(t *testing.T) {
	rnd := rand.New(rand.NewSource(2))
	enc := make([]string, 0, 1000)
	for i := 0; i < 1000; i++ {
		// 刻意让大量样本落在同一毫秒内，逼出「物理部分相同、只比计数」的路径
		enc = append(enc, Encode(uint64(1_000_000_000_000+rnd.Intn(5)), uint32(rnd.Intn(100_000))))
	}
	sorted := append([]string(nil), enc...)
	sort.Strings(sorted)
	for i := 1; i < len(sorted); i++ {
		if Compare(sorted[i-1], sorted[i]) > 0 {
			t.Fatalf("排序后仍不单调: %q(%d) > %q(%d)", sorted[i-1], i-1, sorted[i], i)
		}
	}
}

// ── 单调性与归零 ──────────────────────────────────────────────────────

func TestHLCNowIsStrictlyMonotonic(t *testing.T) {
	clk := &fixedClock{ms: 1_000_000_000_000}
	h := NewHLCWithClock(clk.now)

	prev := h.Now()
	for i := 0; i < 1000; i++ {
		cur := h.Now()
		if Compare(cur, prev) <= 0 {
			t.Fatalf("第 %d 次 Now() 未递增: %q 之后是 %q", i, prev, cur)
		}
		prev = cur
	}
}

func TestHLCNowResetsCounterOnNewMillisecond(t *testing.T) {
	clk := &fixedClock{ms: 1000}
	h := NewHLCWithClock(clk.now)

	if got := h.Now(); got != "0000000001000-00000" {
		t.Fatalf("首次 Now() = %q", got)
	}
	if got := h.Now(); got != "0000000001000-00001" {
		t.Fatalf("同毫秒内第二次 Now() = %q", got)
	}
	clk.set(2000)
	if got := h.Now(); got != "0000000002000-00000" {
		t.Fatalf("新毫秒后 Now() = %q，计数应归零", got)
	}
}

func TestHLCCounterOverflowCarriesIntoPhysical(t *testing.T) {
	clk := &fixedClock{ms: 1000}
	h := NewHLCWithClock(clk.now)

	h.mu.Lock()
	h.l, h.c = 1000, hlcCounterMax-2
	h.mu.Unlock()

	seq := []string{h.Now(), h.Now(), h.Now(), h.Now(), h.Now()}
	for i := 1; i < len(seq); i++ {
		if Compare(seq[i-1], seq[i]) > 0 {
			t.Fatalf("溢出附近未单调: %q → %q", seq[i-1], seq[i])
		}
		if len(seq[i]) != 19 {
			t.Fatalf("溢出后编码长度变化: %q", seq[i])
		}
	}
	if !strings.HasPrefix(seq[len(seq)-1], "0000000001001-") {
		t.Errorf("溢出后应进位到下一毫秒，实际得到 %q", seq[len(seq)-1])
	}
}

// ── 因果性：HLC 存在的全部理由 ─────────────────────────────────────────
//
// design.md §6.1：一旦这些测试失败，就意味着「两台机器时钟有偏差会静默
// 丢书签」这个最初的问题重新出现了。

func TestHLCCausalityAcrossDevices(t *testing.T) {
	a := NewHLCWithClock((&fixedClock{ms: 1_000_000_000_000}).now)
	b := NewHLCWithClock((&fixedClock{ms: 1_000_000_000_000}).now)

	var last string
	for i := 0; i < 5; i++ {
		last = a.Now()
	}
	for i := 0; i < 5; i++ {
		b.Update(last)
		if got := b.Now(); Compare(got, last) <= 0 {
			t.Fatalf("B 的第 %d 个后续事件 %q 未大于 A 的 %q —— 因果性被破坏", i, got, last)
		}
	}
}

// TestHLCCausalCatchupWhenRemoteLeadsWithinSameMillisecond 是本项目
// 曾经写错、并在写测试时才发现的那个分支。
//
// 场景：A 在第 1000ms 内产生 6 个事件，最后一个是 (1000, 00005)。
// 接收方 B 的物理时钟恰好也是 1000ms，但自身逻辑计数为 0。
//
// 错误写法（Update 的第三个分支写成 "max == p 则 c = 0"）会让 B 得到
// (1000, 0)，其后的 Now() 产生 (1000, 1) < (1000, 5) —— 因果性被破坏，
// 且症状是"书签随机丢失"，极难排查。
func TestHLCCausalCatchupWhenRemoteLeadsWithinSameMillisecond(t *testing.T) {
	clkA := &fixedClock{ms: 1000}
	clkB := &fixedClock{ms: 1000}
	a := NewHLCWithClock(clkA.now)
	b := NewHLCWithClock(clkB.now)

	var remote string
	for i := 0; i < 6; i++ {
		remote = a.Now()
	}
	if want := "0000000001000-00005"; remote != want {
		t.Fatalf("A 的最后一个事件 = %q，期望 %q", remote, want)
	}

	b.Update(remote)
	got := b.Now()

	// 关键性质：B 的后续事件必须严格大于它刚收到的事件
	if Compare(got, remote) <= 0 {
		t.Fatalf("B 的后续事件 %q 未大于远端的 %q —— 追平场景下因果性被破坏", got, remote)
	}
	// 精确值：Update 本身是一次接收事件，消耗了计数 6（rc+1），
	// 随后的 Now() 再 +1 得到 7。比 6 更"浪费"一个计数是正确的 ——
	// 论文的算法把 receiveEvent 也算作一个事件。
	if want := "0000000001000-00007"; got != want {
		t.Errorf("B 的后续事件 = %q，期望 %q", got, want)
	}
}

func TestHLCCausalWithClockSkew(t *testing.T) {
	// B 的物理时钟比 A 快 1 小时。这个偏差绝不能影响因果性。
	a := NewHLCWithClock((&fixedClock{ms: 1_000_000_000_000}).now)
	b := NewHLCWithClock((&fixedClock{ms: 1_000_000_000_000 + 3_600_000}).now)

	t1 := a.Now()
	b.Update(t1)
	t2 := b.Now()
	if Compare(t2, t1) <= 0 {
		t.Fatalf("B 事件 %q 未大于 A 事件 %q", t2, t1)
	}
	a.Update(t2)
	if t3 := a.Now(); Compare(t3, t2) <= 0 {
		t.Fatalf("A 事件 %q 未大于 B 事件 %q", t3, t2)
	}
}

// 双向多轮传递，每一轮都必须严格递增。
func TestHLCCausalPingPong(t *testing.T) {
	clkA := &fixedClock{ms: 1_000_000_000_000}
	clkB := &fixedClock{ms: 1_000_000_000_000}
	a := NewHLCWithClock(clkA.now)
	b := NewHLCWithClock(clkB.now)

	var lastA, lastB string
	for round := 0; round < 50; round++ {
		lastA = a.Now()
		b.Update(lastA)
		lastB = b.Now()
		if Compare(lastB, lastA) <= 0 {
			t.Fatalf("第 %d 轮：B 的 %q 未大于 A 的 %q", round, lastB, lastA)
		}
		a.Update(lastB)
		if back := a.Now(); Compare(back, lastB) <= 0 {
			t.Fatalf("第 %d 轮：A 的 %q 未大于 B 的 %q", round, back, lastB)
		}
	}
}

// 物理时钟被调回过去时，HLC 的逻辑时间必须继续前进，
// 否则已发出的时间戳会与新生成的撞在一起。
func TestHLCClockRollbackDoesNotRewind(t *testing.T) {
	clk := &fixedClock{ms: 2_000_000_000_000}
	h := NewHLCWithClock(clk.now)

	first := h.Now()
	clk.set(2_000_000_000_000 - 3_600_000) // 用户或 NTP 把时钟调回 1 小时前

	prev := first
	for i := 0; i < 100; i++ {
		cur := h.Now()
		if Compare(cur, prev) <= 0 {
			t.Fatalf("时钟回拨后第 %d 次 Now() 未递增: %q → %q", i, prev, cur)
		}
		prev = cur
	}
	if Compare(h.Current(), first) < 0 {
		t.Fatalf("回拨后当前时间 %q 小于回拨前的 %q", h.Current(), first)
	}
}

// ── 比较与工具函数 ────────────────────────────────────────────────────

func TestHLCCompare(t *testing.T) {
	smaller := Encode(1000, 5)
	larger := Encode(1000, 6)
	nextMs := Encode(1001, 0)

	if Compare(smaller, larger) != -1 {
		t.Error("同毫秒不同计数：smaller 应更小")
	}
	if Compare(larger, smaller) != 1 {
		t.Error("同毫秒不同计数：larger 应更大")
	}
	if Compare(smaller, smaller) != 0 {
		t.Error("相同时间戳应返回 0")
	}
	if Compare(nextMs, larger) != 1 {
		t.Error("跨毫秒：物理部分优先于计数")
	}
	// 非法值排在合法值之前，保证被污染的一侧永远输，不会污染权威状态
	if Compare("garbage", smaller) != -1 {
		t.Error("非法时间戳应小于合法时间戳")
	}
	if Compare(smaller, "garbage") != 1 {
		t.Error("合法时间戳应大于非法时间戳")
	}
	if Compare("a", "b") != strings.Compare("a", "b") {
		t.Error("两侧均非法时应退化为字典序，保证确定性")
	}
}

func TestHLCMaxHLC(t *testing.T) {
	if got := MaxHLC(); got != HLCZero {
		t.Errorf("空列表 MaxHLC = %q，期望 %q", got, HLCZero)
	}
	a := Encode(1000, 1)
	b := Encode(2000, 0)
	c := Encode(999, 99)
	if got := MaxHLC(a, b, c); got != b {
		t.Errorf("MaxHLC = %q，期望 %q", got, b)
	}
	if got := MaxHLC("garbage", a); got != a {
		t.Errorf("MaxHLC 应忽略非法值，得到 %q", got)
	}
}

func TestHLCStateMaxHLC(t *testing.T) {
	s := NewState()
	if got := s.MaxHLC(); got != HLCZero {
		t.Errorf("空 state 的 MaxHLC = %q", got)
	}
	s.Items["k1"] = Item{M: Encode(1000, 0), A: Encode(500, 0)}
	s.Items["k2"] = Item{M: Encode(900, 0), A: Encode(2000, 3)}
	if got := s.MaxHLC(); got != Encode(2000, 3) {
		t.Errorf("state.MaxHLC = %q，期望取到 a 字段的最大值 %q", got, Encode(2000, 3))
	}
}

func TestHLCObserveManyAdoptsMax(t *testing.T) {
	h := NewHLCWithClock((&fixedClock{ms: 500}).now)
	remote := Encode(9000, 12)

	got := h.ObserveMany([]string{Encode(100, 0), remote, "garbage"})
	if Compare(got, remote) < 0 {
		t.Fatalf("ObserveMany 后 %q 未追上远端 %q", got, remote)
	}
	if next := h.Now(); Compare(next, remote) <= 0 {
		t.Fatalf("ObserveMany 后的 Now() = %q，未大于远端 %q", next, remote)
	}
}

func TestHLCUpdateWithInvalidRemoteDoesNotBreak(t *testing.T) {
	clk := &fixedClock{ms: 1000}
	h := NewHLCWithClock(clk.now)

	first := h.Now()
	got := h.Update("完全不是 HLC")
	if !ValidHLC(got) {
		t.Fatalf("非法远端之后仍应产出合法 HLC，得到 %q", got)
	}
	if Compare(got, first) <= 0 {
		t.Fatalf("非法远端之后 %q 未大于 %q", got, first)
	}
}

// HLC 会被多个 goroutine 碰到（HTTP handler + 快照），必须无竞态。
func TestHLCConcurrentSafety(t *testing.T) {
	h := NewHLC()
	var wg sync.WaitGroup
	for i := 0; i < 50; i++ {
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			for j := 0; j < 200; j++ {
				switch i % 3 {
				case 0:
					h.Now()
				case 1:
					h.Update(Encode(uint64(time.Now().UnixMilli()+int64(j)), uint32(j)))
				default:
					h.ObserveMany([]string{Encode(uint64(j), 0)})
				}
			}
		}(i)
	}
	wg.Wait()
}

// ── 跨端一致性向量 ────────────────────────────────────────────────────

type hlcVector struct {
	Op     string `json:"op"`     // "now" | "update"
	Remote string `json:"remote"` // 仅 update 使用
	At     int64  `json:"at"`     // 该步注入的物理时钟（毫秒）
	Out    string `json:"out"`    // 该步的输出时间戳
}

type hlcVectorFile struct {
	Note    string      `json:"note"`
	Layout  string      `json:"layout"`
	Vectors []hlcVector `json:"vectors"`
}

// TestHLCExportVectors 把 Go 端的行为固化成 test/hlc_vectors.json，
// 供 extension/lib/hlc.test.js 逐条复现。
//
// 这是 docs/plan.md P3.2 的前置。两端 HLC 不一致会让合并在 "m 恰好相等"
// 的分支上产生非确定行为 —— 同一对书签在不同轮次里交替获胜，两端发散。
//
// 用法：go test -run TestHLCExportVectors -v ./...
func TestHLCExportVectors(t *testing.T) {
	clk := &fixedClock{ms: 1_700_000_000_000}
	h := NewHLCWithClock(clk.now)
	var v []hlcVector

	// step 执行一步操作并记录结果。
	//
	// ⚠️ 这里**只能调用一个**方法。早期的写法是无条件先 h.Now()、再在
	// op=="update" 时用 h.Update() 的返回值覆盖 —— 于是每个 update 步都
	// 偷偷多执行了一次 now()，把状态推进了，而那次 now() 的输出被丢弃。
	//
	// 后果：向量记录的是"多走了一步"的状态，与任何忠实的回放器都不符。
	// JS 侧忠实按 op 执行，于是从第一个 update 起就错位，症状是
	// "Go 与 JS 实现不一致" —— 指向完全错误的怀疑方向。
	step := func(op, remote string) {
		var out string
		if op == "update" {
			out = h.Update(remote)
		} else {
			out = h.Now()
		}
		v = append(v, hlcVector{Op: op, Remote: remote, At: clk.now(), Out: out})
	}

	// 1) 物理时钟不动，连续本地事件（靠逻辑计数区分）
	for i := 0; i < 5; i++ {
		step("now", "")
	}
	// 2) 物理时钟推进到新毫秒（计数应归零）
	clk.set(clk.add(1500))
	step("now", "")

	// 3) 远端在同一毫秒内计数更高 —— 曾经写错而破坏因果性的分支
	step("update", Encode(uint64(clk.now()), 40))
	step("now", "")

	// 4) 远端毫秒更大
	step("update", Encode(uint64(clk.now())+60_000, 3))
	step("now", "")

	// 5) 物理时钟大幅回拨
	clk.set(clk.add(-120_000))
	step("now", "")

	// 6) 非法远端时间戳（应退化为纯本地推进，不崩溃）
	step("update", "not-an-hlc")
	step("now", "")

	// 7) 远端计数溢出边界
	step("update", Encode(uint64(clk.now()), 99_999))
	step("now", "")

	// 自检：输出必须严格单调
	for i := 1; i < len(v); i++ {
		if Compare(v[i-1].Out, v[i].Out) >= 0 {
			t.Fatalf("向量 %d（%s）未严格大于前一步（%s）", i, v[i].Out, v[i-1].Out)
		}
	}
	if len(v) < 15 {
		t.Fatalf("向量数量 %d 偏少，跨端验证覆盖不足", len(v))
	}

	out := hlcVectorFile{
		Note: "由 bmsync/hlc_test.go 的 TestHLCExportVectors 生成。" +
			"JS 侧必须逐条复现完全相同的 out —— 两端 HLC 不一致会导致合并非确定。",
		Layout:  hlcLayout,
		Vectors: v,
	}
	blob, err := json.MarshalIndent(out, "", "  ")
	if err != nil {
		t.Fatal(err)
	}
	dst := filepath.Join("..", "test", "hlc_vectors.json")
	if err := os.MkdirAll(filepath.Dir(dst), 0o755); err != nil {
		t.Fatalf("创建 test 目录失败: %v", err)
	}
	if err := os.WriteFile(dst, append(blob, '\n'), 0o644); err != nil {
		t.Fatalf("写入 %s 失败: %v", dst, err)
	}
	t.Logf("已写出 %d 个向量到 %s", len(v), dst)
}
