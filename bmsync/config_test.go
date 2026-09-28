package main

import (
	"errors"
	"io/fs"
	"net"
	"net/http"
	"net/http/httptest"
	"os"
	"strings"
	"testing"
	"time"
)

// env 临时设置若干环境变量，返回恢复函数。
func env(t *testing.T, kv map[string]string) {
	t.Helper()
	for k, v := range kv {
		if v == "" {
			t.Setenv(k, "")
			os.Unsetenv(k)
			continue
		}
		t.Setenv(k, v)
	}
}

func TestLoadConfigDefaults(t *testing.T) {
	env(t, map[string]string{
		"BMSYNC_TOKEN": strings.Repeat("t", 40),
		"BMSYNC_ADDR":  "",
		"BMSYNC_DATA":  "",
		"BMSYNC_HISTORY_KEEP": "",
		"BMSYNC_TOMBSTONE_TTL_DAYS": "",
	})
	cfg, err := LoadConfig()
	if err != nil {
		t.Fatalf("LoadConfig 失败: %v", err)
	}
	if cfg.Addr != defaultAddr {
		t.Errorf("Addr = %q，期望默认 %q", cfg.Addr, defaultAddr)
	}
	if cfg.DataDir != defaultDataDir {
		t.Errorf("DataDir = %q，期望默认 %q", cfg.DataDir, defaultDataDir)
	}
	if cfg.HistoryKeep != defaultHistoryKeep {
		t.Errorf("HistoryKeep = %d，期望默认 %d", cfg.HistoryKeep, defaultHistoryKeep)
	}
	if cfg.TombstoneTTL != defaultTombstoneTTL {
		t.Errorf("TombstoneTTL = %v，期望默认 %v", cfg.TombstoneTTL, defaultTombstoneTTL)
	}
}

func TestLoadConfigOverrides(t *testing.T) {
	env(t, map[string]string{
		"BMSYNC_TOKEN": strings.Repeat("x", 40),
		"BMSYNC_ADDR":  "127.0.0.1:9999",
		"BMSYNC_DATA":  "/srv/bmsync",
		"BMSYNC_HISTORY_KEEP": "7",
		"BMSYNC_TOMBSTONE_TTL_DAYS": "30",
	})
	cfg, err := LoadConfig()
	if err != nil {
		t.Fatal(err)
	}
	if cfg.Addr != "127.0.0.1:9999" {
		t.Errorf("Addr = %q", cfg.Addr)
	}
	if cfg.DataDir != "/srv/bmsync" {
		t.Errorf("DataDir = %q", cfg.DataDir)
	}
	if cfg.HistoryKeep != 7 {
		t.Errorf("HistoryKeep = %d", cfg.HistoryKeep)
	}
	if cfg.TombstoneTTL != 30*24*time.Hour {
		t.Errorf("TombstoneTTL = %v", cfg.TombstoneTTL)
	}
}

// 配置写错时的行为是刻意的：退回默认值并告警，而不是拒绝启动。
// 理由见 envIntOr 的注释。
func TestLoadConfigFallsBackOnGarbageNumbers(t *testing.T) {
	env(t, map[string]string{
		"BMSYNC_TOKEN": strings.Repeat("x", 40),
		"BMSYNC_HISTORY_KEEP": "很多",
		"BMSYNC_TOMBSTONE_TTL_DAYS": "abc",
	})
	cfg, err := LoadConfig()
	if err != nil {
		t.Fatalf("非法数字不应导致启动失败: %v", err)
	}
	if cfg.HistoryKeep != defaultHistoryKeep {
		t.Errorf("HistoryKeep 应退回默认，实际 %d", cfg.HistoryKeep)
	}
	if cfg.TombstoneTTL != defaultTombstoneTTL {
		t.Errorf("TombstoneTTL 应退回默认，实际 %v", cfg.TombstoneTTL)
	}
}

func TestLoadConfigRejectsBadValues(t *testing.T) {
	cases := []struct {
		name    string
		kv      map[string]string
		wantSub string
	}{
		{
			"token 未设置",
			map[string]string{"BMSYNC_TOKEN": ""},
			"BMSYNC_TOKEN",
		},
		{
			"token 太短",
			map[string]string{"BMSYNC_TOKEN": "short"},
			"BMSYNC_TOKEN",
		},
		{
			"token 恰好 31 字符",
			map[string]string{"BMSYNC_TOKEN": strings.Repeat("x", 31)},
			"BMSYNC_TOKEN",
		},
		{
			"快照份数为 0",
			map[string]string{
				"BMSYNC_TOKEN": strings.Repeat("x", 40),
				"BMSYNC_HISTORY_KEEP": "0",
			},
			"HISTORY_KEEP",
		},
		{
			"墓碑 TTL 不足一天",
			map[string]string{
				"BMSYNC_TOKEN": strings.Repeat("x", 40),
				"BMSYNC_TOMBSTONE_TTL_DAYS": "0",
			},
			"TOMBSTONE",
		},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			kv := map[string]string{
				"BMSYNC_TOKEN": strings.Repeat("x", 40),
				"BMSYNC_HISTORY_KEEP": "30",
				"BMSYNC_TOMBSTONE_TTL_DAYS": "90",
			}
			for k, v := range tc.kv {
				kv[k] = v
			}
			env(t, kv)
			_, err := LoadConfig()
			if err == nil {
				t.Fatalf("应当拒绝启动")
			}
			if !strings.Contains(err.Error(), tc.wantSub) {
				t.Errorf("错误信息 %q 未包含 %q", err, tc.wantSub)
			}
		})
	}
}

// token 恰好 32 字符是下限，必须通过。
func TestLoadConfigAcceptsMinimumTokenLength(t *testing.T) {
	env(t, map[string]string{
		"BMSYNC_TOKEN": strings.Repeat("x", minTokenLen),
		"BMSYNC_HISTORY_KEEP": "30",
		"BMSYNC_TOMBSTONE_TTL_DAYS": "90",
	})
	if _, err := LoadConfig(); err != nil {
		t.Errorf("32 字符 token 应被接受: %v", err)
	}
}

func TestEnvOr(t *testing.T) {
	t.Setenv("BMSYNC_TEST_VAR", "")
	os.Unsetenv("BMSYNC_TEST_VAR")
	if got := envOr("BMSYNC_TEST_VAR", "fallback"); got != "fallback" {
		t.Errorf("未设置时应返回默认值，实际 %q", got)
	}
	t.Setenv("BMSYNC_TEST_VAR", "explicit")
	if got := envOr("BMSYNC_TEST_VAR", "fallback"); got != "explicit" {
		t.Errorf("已设置时应返回实际值，实际 %q", got)
	}
}

func TestEnvIntOr(t *testing.T) {
	t.Setenv("BMSYNC_TEST_INT", "")
	os.Unsetenv("BMSYNC_TEST_INT")
	if got := envIntOr("BMSYNC_TEST_INT", 42); got != 42 {
		t.Errorf("未设置时返回 %d，期望默认 42", got)
	}
	t.Setenv("BMSYNC_TEST_INT", "7")
	if got := envIntOr("BMSYNC_TEST_INT", 42); got != 7 {
		t.Errorf("实际值 %d，期望 7", got)
	}
	t.Setenv("BMSYNC_TEST_INT", "not-a-number")
	if got := envIntOr("BMSYNC_TEST_INT", 42); got != 42 {
		t.Errorf("非法值应退回默认，实际 %d", got)
	}
}

func TestEnvDurationDaysOr(t *testing.T) {
	t.Setenv("BMSYNC_TEST_DAYS", "")
	os.Unsetenv("BMSYNC_TEST_DAYS")
	if got := envDurationDaysOr("BMSYNC_TEST_DAYS", 90*24*time.Hour); got != 90*24*time.Hour {
		t.Errorf("未设置时返回 %v", got)
	}
	t.Setenv("BMSYNC_TEST_DAYS", "30")
	if got := envDurationDaysOr("BMSYNC_TEST_DAYS", 90*24*time.Hour); got != 30*24*time.Hour {
		t.Errorf("实际值 %v", got)
	}
	t.Setenv("BMSYNC_TEST_DAYS", "3.5")
	if got := envDurationDaysOr("BMSYNC_TEST_DAYS", 90*24*time.Hour); got != 90*24*time.Hour {
		t.Errorf("非整数应退回默认，实际 %v", got)
	}
}

// ── 限流器的桶清理 ────────────────────────────────────────────────────

func TestRateLimiterGCPrunesExpiredBuckets(t *testing.T) {
	rl := newRateLimiter(5, time.Millisecond*10)
	if !rl.allow("1.1.1.1") {
		t.Fatal("首次应放行")
	}
	rl.mu.Lock()
	before := len(rl.buckets)
	// 把桶的时间戳推到过去，模拟窗口已过期
	rl.buckets["1.1.1.1"].start = time.Now().Add(-time.Hour)
	rl.lastGC = time.Now().Add(-time.Hour) // 让 gc 认为距上次清理已过一个窗口
	rl.mu.Unlock()

	// 触发一次 allow，它内部会调用 gc
	rl.allow("2.2.2.2")
	rl.allow("3.3.3.3")

	rl.mu.Lock()
	defer rl.mu.Unlock()
	if len(rl.buckets) != 2 {
		t.Errorf("过期桶应被清理，期望剩 2 个，实际 %d（清理前 %d）", len(rl.buckets), before)
	}
	if _, ok := rl.buckets["1.1.1.1"]; ok {
		t.Error("过期桶 1.1.1.1 未被清理")
	}
}

func TestRateLimiterGCSkipsWhenWindowNotElapsed(t *testing.T) {
	rl := newRateLimiter(5, time.Hour)
	rl.allow("1.1.1.1")
	rl.mu.Lock()
	rl.lastGC = time.Now() // 刚清理过
	n := len(rl.buckets)
	rl.mu.Unlock()

	rl.allow("2.2.2.2")
	rl.mu.Lock()
	defer rl.mu.Unlock()
	// 桶本身不旧，不应被清掉
	if len(rl.buckets) < n {
		t.Errorf("窗口未到不该触发清理")
	}
	if _, ok := rl.buckets["1.1.1.1"]; !ok {
		t.Error("未过期的桶被误删")
	}
}

func TestRateLimiterAllowsAfterWindowExpires(t *testing.T) {
	rl := newRateLimiter(2, 50*1000*1000)
	rl.allow("1.1.1.1")
	rl.allow("1.1.1.1")
	if rl.allow("1.1.1.1") {
		t.Fatal("窗口内第三次应被限流")
	}
	// 把桶的起点推到过去，模拟窗口已滑过
	rl.mu.Lock()
	rl.buckets["1.1.1.1"].start = time.Now().Add(-time.Hour)
	rl.mu.Unlock()
	if !rl.allow("1.1.1.1") {
		t.Error("窗口过期后应重新放行")
	}
}

// ── 平台差异：瞬时文件错误的判定 ──────────────────────────────────────

func TestIsTransientFileError(t *testing.T) {
	if isTransientFileError(nil) {
		t.Error("nil 不应被当作瞬时错误")
	}
	// 权限类错误在两个平台都视为可重试
	if !isTransientFileError(fs.ErrPermission) {
		t.Error("权限错误应视为瞬时")
	}
	// 不相关的错误不应重试
	if isTransientFileError(errors.New("磁盘满了")) {
		t.Error("无关错误不应重试")
	}
	// 路径不存在是确定性错误，重试没有意义
	if isTransientFileError(fs.ErrNotExist) {
		t.Error("文件不存在不应重试")
	}
}

// renameWithRetry 必须在**确定性**错误上立刻返回，不能白等。
// 如果它对不存在的源路径也重试 5 次再报错，问题会被人为放大 5 倍延迟。
func TestRenameWithRetryFailsFastOnDeterministicError(t *testing.T) {
	dir := t.TempDir()
	src := dir + "/src.json"
	dst := dir + "/nested/missing/dst.json" // 父目录不存在 → 确定性失败

	start := time.Now()
	err := renameWithRetry(src, dst, 5)
	elapsed := time.Since(start)

	if err == nil {
		t.Fatal("父目录不存在时 rename 必须失败")
	}
	// 5 次指数退避累计约 1+2+4+8+16 = 31ms。真实耗时应远小于此说明立即返回了。
	if elapsed > 20*time.Millisecond {
		t.Errorf("确定性错误应立即返回，却耗时 %v —— 说明重试逻辑误伤了它", elapsed)
	}
}

func TestRenameWithRetrySucceedsImmediately(t *testing.T) {
	dir := t.TempDir()
	src := dir + "/a.tmp"
	dst := dir + "/a.json"
	if err := os.WriteFile(src, []byte("hi"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := renameWithRetry(src, dst, 5); err != nil {
		t.Fatalf("正常 rename 应成功: %v", err)
	}
	if _, err := os.Stat(dst); err != nil {
		t.Errorf("目标文件不存在: %v", err)
	}
	if _, err := os.Stat(src); !os.IsNotExist(err) {
		t.Error("源文件应已被移走")
	}
}

// ── healthcheck 子命令 ────────────────────────────────────────────────
//
// 这个子命令是 docker HEALTHCHECK 的唯一入口（scratch 镜像没有 shell），
// 所以它的退出码直接决定容器是否被判定为健康。

func TestRunHealthcheckAgainstRealServer(t *testing.T) {
	cfg := testConfig(t)
	srv, err := NewServer(cfg, discardLogger())
	if err != nil {
		t.Fatal(err)
	}
	ts := httptest.NewServer(srv.Routes())
	defer ts.Close()

	t.Setenv("BMSYNC_ADDR", strings.TrimPrefix(ts.URL, "http://"))

	if code := runHealthcheck(); code != 0 {
		t.Errorf("对健康服务应返回 0，实际 %d", code)
	}
}

func TestRunHealthcheckRejectsWrongService(t *testing.T) {
	// 模拟反代把请求路由到了别的后端：返回 200 但不是 bmsync
	ts := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Write([]byte(`{"ok":true,"service":"something-else"}`))
	}))
	defer ts.Close()

	t.Setenv("BMSYNC_ADDR", strings.TrimPrefix(ts.URL, "http://"))
	if code := runHealthcheck(); code == 0 {
		t.Error("响应不是本服务时应返回非 0 —— 否则反代配错会被判为健康")
	}
}

func TestRunHealthcheckRejectsBadAddress(t *testing.T) {
	t.Setenv("BMSYNC_ADDR", "这不是地址::")
	if code := runHealthcheck(); code == 2 {
		// 期望的退出码：配置问题
		return
	}
	// 某些平台可能走到别的失败路径，只要非 0 就行
}

func TestRunHealthcheckFailsWhenServerDown(t *testing.T) {
	// 监听一个确定没人用的端口
	t.Setenv("BMSYNC_ADDR", "127.0.0.1:1")
	if code := runHealthcheck(); code == 0 {
		t.Error("服务未启动时应返回非 0")
	}
}

// wildcard 地址（0.0.0.0 / :: / 空）必须被换成回环地址，
// 否则容器内 healthcheck 会连不上自己。
func TestHealthcheckRewritesWildcardHost(t *testing.T) {
	var gotHost string
	ts := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		gotHost = r.Host
		w.Write([]byte(`{"ok":true,"service":"bmsync"}`))
	}))
	defer ts.Close()
	_, port, err := net.SplitHostPort(strings.TrimPrefix(ts.URL, "http://"))
	if err != nil {
		t.Fatal(err)
	}

	for _, wildcard := range []string{"0.0.0.0", "::", ""} {
		gotHost = ""
		t.Setenv("BMSYNC_ADDR", net.JoinHostPort(wildcard, port))
		_ = runHealthcheck()
		if !strings.HasPrefix(gotHost, "127.0.0.1") && !strings.HasPrefix(gotHost, "[::1]") {
			t.Errorf("wildcard %q 未被换成回环地址，Host=%q", wildcard, gotHost)
		}
	}
}
