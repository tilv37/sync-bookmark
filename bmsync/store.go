package main

import (
	"encoding/json"
	"errors"
	"fmt"
	"io/fs"
	"log/slog"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"time"
)

// Store 持有全部持久化状态，并串行化对 state.json 的读写。
//
// 并发模型：单进程 + 一个互斥锁覆盖整个「读 → 合并 → GC → 写」临界区。
// 不用文件锁，因为服务是单容器单进程，没有横向扩展需求（design.md §8.4）。
// 临界区里不写历史快照 —— 快照在临界区外做，避免拖长锁持有时间。

type Store struct {
	mu  sync.Mutex
	dir string
	log *slog.Logger
	cfg *Config

	// clock 是跨请求持久的 HLC。它必须比服务端见过的所有客户端时间戳都大，
	// 这样返回给客户端的 hlc 才是权威的。进程重启后由 Load() 用 state 里的
	// 最大值重新校准。
	clock *HLC

	state     State
	conflicts []Conflict
}

// 状态文件名
const (
	stateFileName     = "state.json"
	stateTmpFileName  = "state.json.tmp"
	conflictFileName  = "conflicts.json"
	historyDirName    = "history"
	maxConflictBuffer = 500
)

func NewStore(cfg *Config, log *slog.Logger) (*Store, error) {
	st := &Store{
		dir:   cfg.DataDir,
		log:   log,
		cfg:   cfg,
		clock: NewHLC(),
		state: NewState(),
	}
	if err := st.ensureDirs(); err != nil {
		return nil, err
	}
	if err := st.Load(); err != nil {
		return nil, err
	}
	return st, nil
}

func (st *Store) ensureDirs() error {
	if err := os.MkdirAll(st.dir, 0o755); err != nil {
		return fmt.Errorf("创建数据目录 %s 失败: %w", st.dir, err)
	}
	if err := os.MkdirAll(filepath.Join(st.dir, historyDirName), 0o755); err != nil {
		return fmt.Errorf("在 %s 下创建快照目录失败: %w", st.dir, err)
	}
	return nil
}

// Load 读取磁盘上的状态。首次运行（文件不存在）时初始化一个空 state。
//
// schema 版本不匹配时**拒绝启动**而不是自动迁移：书签是用户不可再生的
// 数据，静默改写的风险远大于"升级后手工处理一次"（design.md §8.7）。
func (st *Store) Load() error {
	path := filepath.Join(st.dir, stateFileName)

	raw, err := os.ReadFile(path)
	if errors.Is(err, fs.ErrNotExist) {
		st.log.Info("未发现已有状态，从空状态开始", "path", path)
		// 注意：这里不能直接 return。state.json 不存在只说明状态是空的，
		// 冲突缓冲是独立的文件，可能已经存在（比如只同步过墓碑的极端情况）。
		// 早退会让 conflicts.json 永远读不回来。
		if err := st.loadConflicts(); err != nil {
			st.log.Warn("读取冲突记录失败，将从空缓冲开始", "err", err)
		}
		return nil
	}
	if err != nil {
		return fmt.Errorf("读取 %s 失败: %w", path, err)
	}

	var s State
	if err := json.Unmarshal(raw, &s); err != nil {
		return fmt.Errorf("解析 %s 失败（文件可能已损坏）: %w", path, err)
	}
	if s.V != SchemaVersion {
		return fmt.Errorf("%s 的 schema 版本是 %d，本服务只支持 %d。"+
			"请确认 bmsync 版本是否匹配；为避免静默改写你的书签，服务拒绝启动。",
			path, s.V, SchemaVersion)
	}
	if s.Items == nil {
		s.Items = map[string]Item{}
	}
	if _, err := s.Validate(); err != nil {
		return fmt.Errorf("%s 未通过校验: %w", path, err)
	}

	st.state = s
	// 重启后用磁盘上的最大值重新校准时钟，否则服务可能发出比历史记录更小的时间戳
	st.clock.Update(s.MaxHLC())

	st.log.Info("已载入状态", "items", len(s.Items), "活跃", s.Count())

	if err := st.loadConflicts(); err != nil {
		st.log.Warn("读取冲突记录失败，将从空缓冲开始", "err", err)
	}
	return nil
}

// Sync 是服务的核心路径：合并、GC、落盘。
//
// 锁覆盖整个过程，因为「读旧状态 → 合并 → 写新状态」必须是一个原子步骤。
// 两次并发同步如果各自读到了同一份旧状态，后写入的那次会覆盖前一次的结果，
// 丢掉中间的改动。
func (st *Store) Sync(incoming State, base map[string]string, device string) (MergeResult, error) {
	st.mu.Lock()
	defer st.mu.Unlock()

	now := time.Now().UnixMilli()

	// 快照必须在写新状态**之前**完成 —— 它保存的是"这次同步发生前"的状态，
	// 正是出问题时需要回滚到的那个点。
	if err := st.snapshotLocked(); err != nil {
		// 快照失败不该阻断同步：它只是保险，不是必需品。
		st.log.Warn("创建历史快照失败（同步继续）", "err", err)
	}

	res := Merge(st.state, incoming, base, device, now)

	// GC 清理过期墓碑，清理量计入 summary，用户能看到"服务端清理了多少"
	if removed := GCTombstones(&res.State, now, st.cfg.TombstoneTTL); removed > 0 {
		res.Summary.Deleted += removed
	}

	// 服务端时钟追平它见过的最大时间戳
	res.State.HLC = st.clock.Update(res.State.MaxHLC())

	if _, err := res.State.Validate(); err != nil {
		return MergeResult{}, fmt.Errorf("合并结果未通过校验，已放弃本次同步: %w", err)
	}
	if err := st.saveLocked(res.State); err != nil {
		return MergeResult{}, err
	}

	st.state = res.State
	if len(res.Conflicts) > 0 {
		st.appendConflictsLocked(res.Conflicts)
	}
	return res, nil
}

// State 返回当前状态的深拷贝（供 /api/history 之类只读用途）。
func (st *Store) State() State {
	st.mu.Lock()
	defer st.mu.Unlock()
	return st.state.Clone()
}

// Conflicts 返回最近的冲突记录。
func (st *Store) Conflicts(limit int) []Conflict {
	st.mu.Lock()
	defer st.mu.Unlock()
	if limit <= 0 || limit > len(st.conflicts) {
		limit = len(st.conflicts)
	}
	out := make([]Conflict, limit)
	copy(out, st.conflicts[len(st.conflicts)-limit:])
	return out
}

// Snapshots 列出历史快照（只读，不提供回滚 —— 回滚是手工操作，见运维文档）。
func (st *Store) Snapshots() ([]SnapshotInfo, error) {
	st.mu.Lock()
	defer st.mu.Unlock()

	dir := filepath.Join(st.dir, historyDirName)
	entries, err := os.ReadDir(dir)
	if err != nil {
		if errors.Is(err, fs.ErrNotExist) {
			return []SnapshotInfo{}, nil
		}
		return nil, err
	}

	out := make([]SnapshotInfo, 0, len(entries))
	for _, e := range entries {
		if e.IsDir() || !strings.HasSuffix(e.Name(), ".json") {
			continue
		}
		info, err := e.Info()
		if err != nil {
			continue
		}
		si := SnapshotInfo{
			ID:   strings.TrimSuffix(e.Name(), ".json"),
			At:   info.ModTime().UnixMilli(),
			Size: info.Size(),
		}
		// 读一下 item 数，只读快照文件的头部信息即可
		if raw, err := os.ReadFile(filepath.Join(dir, e.Name())); err == nil {
			var s State
			if json.Unmarshal(raw, &s) == nil {
				si.Items = len(s.Items)
			}
		}
		out = append(out, si)
	}
	sort.Slice(out, func(i, j int) bool { return out[i].At > out[j].At })
	return out, nil
}

// ── 落盘 ──────────────────────────────────────────────────────────────

// saveLocked 原子写入 state.json。
//
// 原子性来自 rename(2)：POSIX 保证把新文件 rename 到已存在的路径上时，
// 其他进程要么看到完整旧内容，要么看到完整新内容，不存在中间态。
// 这条性质是"kill -9 之后数据仍可解析"的唯一依据（design.md §8.3）。
//
// 调用方必须持有 st.mu。
func (st *Store) saveLocked(s State) error {
	final := filepath.Join(st.dir, stateFileName)
	tmp := filepath.Join(st.dir, stateTmpFileName)

	raw, err := json.Marshal(s)
	if err != nil {
		return fmt.Errorf("序列化状态失败: %w", err)
	}
	if err := os.WriteFile(tmp, raw, 0o644); err != nil {
		return fmt.Errorf("写入临时文件失败: %w", err)
	}
	// 落盘缓冲，否则 rename 可能先于数据真正写入磁盘
	if f, err := os.Open(tmp); err == nil {
		_ = f.Sync()
		_ = f.Close()
	}
	if err := renameWithRetry(tmp, final, 5); err != nil {
		return fmt.Errorf("原子替换 %s 失败: %w", final, err)
	}
	// 目录 fsync 是"重命名本身也要落盘"的耐久化保险。它在 Linux 上有效，
	// 但在 Windows 上对目录调用 Sync 会直接失败 —— 那只是少一层保险，
	// 原子性仍然由 rename 保证，所以这里不当作错误。
	if err := syncDir(filepath.Dir(final)); err != nil {
		st.log.Debug("目录 fsync 失败（不影响正确性）", "err", err)
	}
	return nil
}

// renameWithRetry 在瞬时文件占用时重试 rename。
//
// Linux 上 rename(2) 在同一文件系统内是原子的，不会被别的进程"占用"。
// **Windows 不同**：MoveFileEx 会因为杀毒软件、索引器、编辑器等短暂持有
// 文件句柄而返回 ERROR_ACCESS_DENIED / ERROR_SHARING_VIOLATION。
// Go 不内置重试，于是一次偶发的占用就会让整个同步失败。
//
// 这个 bug 在 100 次并发写的测试里稳定复现（每次约丢一个更新），但线上
// 只在低频触发时才会被用户注意到 —— 表现是"偶尔同步失败，重试就好"。
// 显式加重试让它不再发生。
//
// 平台差异见 transient_windows.go / transient_other.go。
func renameWithRetry(oldPath, newPath string, attempts int) error {
	var err error
	for i := 0; i < attempts; i++ {
		if err = os.Rename(oldPath, newPath); err == nil {
			return nil
		}
		if !isTransientFileError(err) {
			return err
		}
		time.Sleep(time.Duration(1<<i) * time.Millisecond) // 指数退避
	}
	return err
}

func syncDir(dir string) error {
	d, err := os.Open(dir)
	if err != nil {
		return err
	}
	defer d.Close()
	return d.Sync()
}

// snapshotLocked 把当前 state 复制到 history/。
//
// 命名用时间戳而不是随机数：天然按时间排序，且人眼可读。
// 调用方必须持有 st.mu。
func (st *Store) snapshotLocked() error {
	if len(st.state.Items) == 0 {
		return nil // 空状态没什么可备份的
	}
	dir := filepath.Join(st.dir, historyDirName)
	name := time.Now().UTC().Format("20060102T150405.000Z") + ".json"

	raw, err := json.Marshal(st.state)
	if err != nil {
		return err
	}
	if err := os.WriteFile(filepath.Join(dir, name), raw, 0o644); err != nil {
		return err
	}
	return st.pruneSnapshotsLocked(dir)
}

func (st *Store) pruneSnapshotsLocked(dir string) error {
	entries, err := os.ReadDir(dir)
	if err != nil {
		return err
	}
	var names []string
	for _, e := range entries {
		if !e.IsDir() && strings.HasSuffix(e.Name(), ".json") {
			names = append(names, e.Name())
		}
	}
	if len(names) <= st.cfg.HistoryKeep {
		return nil
	}
	// 文件名以时间戳开头，字典序 == 时间序，直接按名字排序
	sort.Strings(names)
	for _, n := range names[:len(names)-st.cfg.HistoryKeep] {
		if err := os.Remove(filepath.Join(dir, n)); err != nil {
			st.log.Warn("删除旧快照失败", "file", n, "err", err)
		}
	}
	return nil
}

// ── 冲突缓冲 ──────────────────────────────────────────────────────────

func (st *Store) conflictPath() string { return filepath.Join(st.dir, conflictFileName) }

func (st *Store) loadConflicts() error {
	raw, err := os.ReadFile(st.conflictPath())
	if errors.Is(err, fs.ErrNotExist) {
		st.conflicts = nil
		return nil
	}
	if err != nil {
		return err
	}
	return json.Unmarshal(raw, &st.conflicts)
}

// appendConflictsLocked 追加冲突并落盘。调用方必须持有 st.mu。
func (st *Store) appendConflictsLocked(list []Conflict) {
	st.conflicts = append(st.conflicts, list...)
	if len(st.conflicts) > maxConflictBuffer {
		st.conflicts = st.conflicts[len(st.conflicts)-maxConflictBuffer:]
	}
	raw, err := json.Marshal(st.conflicts)
	if err != nil {
		st.log.Warn("序列化冲突记录失败", "err", err)
		return
	}
	if err := os.WriteFile(st.conflictPath(), raw, 0o644); err != nil {
		st.log.Warn("写入冲突记录失败", "err", err)
	}
}

// ── 便捷读取 ──────────────────────────────────────────────────────────

func (st *Store) ItemCount() int {
	st.mu.Lock()
	defer st.mu.Unlock()
	return len(st.state.Items)
}

func (st *Store) ActiveCount() int {
	st.mu.Lock()
	defer st.mu.Unlock()
	return st.state.Count()
}
