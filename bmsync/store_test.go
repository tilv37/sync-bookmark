package main

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"
)

func testConfig(t *testing.T) *Config {
	t.Helper()
	return &Config{
		Addr:         ":0",
		Token:        strings.Repeat("t", 40),
		DataDir:      t.TempDir(),
		HistoryKeep:  3,
		TombstoneTTL: 90 * 24 * time.Hour,
	}
}

func newTestStore(t *testing.T) (*Store, *Config) {
	t.Helper()
	cfg := testConfig(t)
	st, err := NewStore(cfg, discardLogger())
	if err != nil {
		t.Fatalf("NewStore 失败: %v", err)
	}
	return st, cfg
}

func TestStoreLoadOnEmptyDir(t *testing.T) {
	st, _ := newTestStore(t)
	if len(st.state.Items) != 0 {
		t.Errorf("全新目录应从空状态开始，实际 %d 项", len(st.state.Items))
	}
	if _, err := os.Stat(filepath.Join(st.dir, stateFileName)); !os.IsNotExist(err) {
		t.Error("全新目录不应已有 state.json")
	}
}

func TestStoreLoadRejectsWrongSchemaVersion(t *testing.T) {
	cfg := testConfig(t)
	raw := []byte(`{"v":99,"items":{}}`)
	if err := os.WriteFile(filepath.Join(cfg.DataDir, stateFileName), raw, 0o644); err != nil {
		t.Fatal(err)
	}
	_, err := NewStore(cfg, discardLogger())
	if err == nil {
		t.Fatal("schema 版本不符时必须拒绝启动")
	}
	if !strings.Contains(err.Error(), "schema") {
		t.Errorf("错误信息应点明 schema，实际 %q", err)
	}
}

// 自动迁移看起来"方便"，但对不可再生的用户数据来说风险远大于收益。
func TestStoreLoadRejectsCorruptFile(t *testing.T) {
	cfg := testConfig(t)
	if err := os.WriteFile(filepath.Join(cfg.DataDir, stateFileName), []byte("{not json"), 0o644); err != nil {
		t.Fatal(err)
	}
	if _, err := NewStore(cfg, discardLogger()); err == nil {
		t.Error("损坏的 state.json 必须导致启动失败，而不是静默从空开始")
	}
}

func TestStoreLoadRestoresState(t *testing.T) {
	cfg := testConfig(t)
	saved := stateOf(kv("b", validBookmark(RootToolbar)))
	raw, _ := json.Marshal(saved)
	if err := os.WriteFile(filepath.Join(cfg.DataDir, stateFileName), raw, 0o644); err != nil {
		t.Fatal(err)
	}

	st, err := NewStore(cfg, discardLogger())
	if err != nil {
		t.Fatal(err)
	}
	if len(st.state.Items) != 1 {
		t.Errorf("应恢复 1 项，实际 %d", len(st.state.Items))
	}
}

func TestStoreSyncWritesAndReloads(t *testing.T) {
	st, cfg := newTestStore(t)
	incoming := stateOf(kv("b", validBookmark(RootToolbar)))

	if _, err := st.Sync(incoming, nil, tA); err != nil {
		t.Fatalf("Sync 失败: %v", err)
	}

	// 磁盘上应立刻出现完整可解析的 state.json
	raw, err := os.ReadFile(filepath.Join(cfg.DataDir, stateFileName))
	if err != nil {
		t.Fatal(err)
	}
	var reloaded State
	if err := json.Unmarshal(raw, &reloaded); err != nil {
		t.Fatalf("落盘的文件无法解析: %v", err)
	}
	if len(reloaded.Items) != 1 {
		t.Errorf("落盘内容应有 1 项，实际 %d", len(reloaded.Items))
	}
	// 临时文件不应残留
	if _, err := os.Stat(filepath.Join(cfg.DataDir, stateTmpFileName)); !os.IsNotExist(err) {
		t.Error("临时文件未被清理")
	}
}

func TestStoreSyncRejectsCorruptResult(t *testing.T) {
	// 构造一个会让合并结果通不过校验的输入（例如父节点成环的极端情况）
	// 确认 Sync 会放弃写入而不是把坏状态落盘。
	st, cfg := newTestStore(t)
	good := stateOf(kv("b", validBookmark(RootToolbar)))
	if _, err := st.Sync(good, nil, tA); err != nil {
		t.Fatal(err)
	}
	before, _ := os.ReadFile(filepath.Join(cfg.DataDir, stateFileName))

	// 一个 depth 超过上限的 state
	deep := NewState()
	parent := RootToolbar
	for i := 0; i <= MaxDepth+3; i++ {
		k := keyOf("deep" + string(rune('a'+i%26)) + string(rune('a'+i/26)))
		deep.Items[k] = validFolder(parent)
		parent = k
	}
	if _, err := st.Sync(deep, nil, tB); err == nil {
		t.Error("超出深度上限的 state 应被拒绝")
	}

	after, _ := os.ReadFile(filepath.Join(cfg.DataDir, stateFileName))
	if string(before) != string(after) {
		t.Error("被拒绝的同步不应修改已落盘的状态")
	}
}

func TestStoreCreatesSnapshotBeforeWrite(t *testing.T) {
	st, cfg := newTestStore(t)

	// 第一次同步：状态为空，不产生快照
	if _, err := st.Sync(stateOf(kv("b1", validBookmark(RootToolbar))), nil, tA); err != nil {
		t.Fatal(err)
	}
	// 第二次同步：应产生一个"本次同步前"的快照
	if _, err := st.Sync(stateOf(
		kv("b1", validBookmark(RootToolbar)),
		kv("b2", validBookmark(RootUnfiled)),
	), nil, tA); err != nil {
		t.Fatal(err)
	}

	snaps, err := st.Snapshots()
	if err != nil {
		t.Fatal(err)
	}
	if len(snaps) != 1 {
		t.Fatalf("应产生 1 个快照，实际 %d", len(snaps))
	}
	// 快照内容应是"同步前"的状态：只有 b1，没有 b2
	raw, err := os.ReadFile(filepath.Join(cfg.DataDir, historyDirName, snaps[0].ID+".json"))
	if err != nil {
		t.Fatal(err)
	}
	var snap State
	if err := json.Unmarshal(raw, &snap); err != nil {
		t.Fatal(err)
	}
	if len(snap.Items) != 1 {
		t.Errorf("快照应保存同步前的状态（1 项），实际 %d 项", len(snap.Items))
	}
	if _, ok := snap.Items[keyOf("b1")]; !ok {
		t.Error("快照里应有 b1")
	}
}

func TestStorePrunesOldSnapshots(t *testing.T) {
	st, cfg := newTestStore(t) // HistoryKeep = 3
	base := stateOf(kv("b1", validBookmark(RootToolbar)))

	for i := 0; i < 8; i++ {
		if _, err := st.Sync(base, nil, tA); err != nil {
			t.Fatal(err)
		}
		time.Sleep(2 * time.Millisecond) // 快照名是毫秒级时间戳
	}

	entries, err := os.ReadDir(filepath.Join(cfg.DataDir, historyDirName))
	if err != nil {
		t.Fatal(err)
	}
	if len(entries) > cfg.HistoryKeep {
		t.Errorf("快照数 %d 超过保留上限 %d", len(entries), cfg.HistoryKeep)
	}
}

// 锁覆盖整个「读 → 合并 → 写」，所以并发同步不会丢更新。
// 100 次并发写入后，100 个书签必须一个不少。
func TestStoreConcurrentSyncNoLostUpdates(t *testing.T) {
	st, _ := newTestStore(t)
	const n = 100

	var wg sync.WaitGroup
	errs := make(chan error, n)
	for i := 0; i < n; i++ {
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			item := validBookmark(RootToolbar)
			item.N = "bookmark-" + string(rune('a'+i%26)) + string(rune('a'+(i/26)%26)) + "-" + itoa(i)
			item.U = "https://e.example/" + itoa(i)
			incoming := stateOf(kv("concurrent"+itoa(i), item))
			if _, err := st.Sync(incoming, nil, "dev-"+itoa(i)); err != nil {
				errs <- err
			}
		}(i)
	}
	wg.Wait()
	close(errs)
	for err := range errs {
		t.Errorf("并发同步出错: %v", err)
	}

	if got := st.ItemCount(); got != n {
		t.Errorf("并发同步后应有 %d 项，实际 %d —— 存在丢失更新", n, got)
	}
	// 落盘文件也必须完整
	raw, err := os.ReadFile(filepath.Join(st.dir, stateFileName))
	if err != nil {
		t.Fatal(err)
	}
	var reloaded State
	if err := json.Unmarshal(raw, &reloaded); err != nil {
		t.Fatalf("并发写入后文件损坏: %v", err)
	}
	if len(reloaded.Items) != n {
		t.Errorf("落盘状态应有 %d 项，实际 %d", n, len(reloaded.Items))
	}
}

func TestStoreSyncIsIdempotent(t *testing.T) {
	st, _ := newTestStore(t)
	in := stateOf(kv("b", validBookmark(RootToolbar)))

	first, err := st.Sync(in, nil, tA)
	if err != nil {
		t.Fatal(err)
	}
	for i := 0; i < 4; i++ {
		again, err := st.Sync(in, nil, tA)
		if err != nil {
			t.Fatal(err)
		}
		if !sameStates(first.State, again.State) {
			t.Fatalf("第 %d 次重复同步结果不同", i+1)
		}
		if again.Summary.Created != 0 {
			t.Errorf("重复同步不应继续报新增，实际 created=%d", again.Summary.Created)
		}
	}
}

func TestStoreConflictRingBuffer(t *testing.T) {
	st, _ := newTestStore(t)
	orig := validBookmark(RootToolbar)
	k := keyOf("b")
	base := map[string]string{k: orig.M}

	if _, err := st.Sync(stateOf(kv("b", orig)), base, tA); err != nil {
		t.Fatal(err)
	}

	// 制造多次真并发编辑。
	//
	// 关键：光有"客户端改了"不算冲突 —— base 记录的是双方上次见到的样子，
	// 只有当**两端都基于同一个基线改动了同一项**时才是并发编辑。
	// 所以下面每轮都先让服务端侧也改一次。

	for round := 0; round < 10; round++ {
		// 服务端侧先改
		serverSide := validBookmark(RootToolbar)
		serverSide.N = "云端改" + itoa(round)
		serverSide.M = Encode(uint64(2000+round*2), 0)
		if _, err := st.Sync(stateOf(kv("b", serverSide)), base, tA); err != nil {
			t.Fatal(err)
		}

		// 客户端基于同一个 orig 也改 → 真并发
		clientSide := validBookmark(RootToolbar)
		clientSide.N = "本地改" + itoa(round)
		clientSide.M = Encode(uint64(2001+round*2), 0)

		before := len(st.Conflicts(0))
		if _, err := st.Sync(stateOf(kv("b", clientSide)), base, tB); err != nil {
			t.Fatal(err)
		}
		if len(st.Conflicts(0)) <= before {
			t.Fatalf("第 %d 轮应记录冲突", round)
		}
	}

	// 环形缓冲上限
	all := st.Conflicts(0)
	if len(all) > maxConflictBuffer {
		t.Errorf("冲突缓冲 %d 条，超过上限 %d", len(all), maxConflictBuffer)
	}
	if got := st.Conflicts(3); len(got) != 3 {
		t.Errorf("limit=3 应返回 3 条，实际 %d", len(got))
	}
}

// 重启后冲突缓冲应能恢复
func TestStoreConflictBufferPersists(t *testing.T) {
	cfg := testConfig(t)
	st, err := NewStore(cfg, discardLogger())
	if err != nil {
		t.Fatal(err)
	}
	st.appendConflictsLocked([]Conflict{
		{Key: "a", Reason: ReasonConcurrentEdit, Field: "title"},
		{Key: "b", Reason: ReasonHLCCollision, Field: "url"},
	})

	reopened, err := NewStore(cfg, discardLogger())
	if err != nil {
		t.Fatal(err)
	}
	if got := len(reopened.Conflicts(0)); got != 2 {
		t.Errorf("重启后应恢复 2 条冲突，实际 %d", got)
	}
}

func TestStoreStateReturnsDeepCopy(t *testing.T) {
	st, _ := newTestStore(t)
	if _, err := st.Sync(stateOf(kv("b", validBookmark(RootToolbar))), nil, tA); err != nil {
		t.Fatal(err)
	}
	got := st.State()
	delete(got.Items, keyOf("b"))
	if st.ItemCount() != 1 {
		t.Error("State() 返回的不是深拷贝：外部删除影响了内部状态")
	}
}

func TestStoreSnapshotsOnEmptyDir(t *testing.T) {
	st, _ := newTestStore(t)
	snaps, err := st.Snapshots()
	if err != nil {
		t.Fatal(err)
	}
	if len(snaps) != 0 {
		t.Errorf("没有快照时不应报错或返回内容，实际 %d 条", len(snaps))
	}
}

func itoa(n int) string {
	if n == 0 {
		return "0"
	}
	var b []byte
	for n > 0 {
		b = append([]byte{byte('0' + n%10)}, b...)
		n /= 10
	}
	return string(b)
}
