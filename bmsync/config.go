package main

import (
	"fmt"
	"log/slog"
	"os"
	"strconv"
	"time"
)

// Config 是服务端的全部运行期配置。全部来自环境变量，便于容器化部署。
type Config struct {
	// Addr 是 HTTP 监听地址，如 ":8080"。可能与 DataDir 分属不同来源
	// （前者来自 BMSYNC_ADDR/flag，后者来自 BMSYNC_DATA），故单独保留。
	Addr string

	// Token 是 Bearer 令牌，客户端必须在每个请求的 Authorization 头携带。
	Token string

	// DataDir 是 state.json / conflicts.json / history/ 所在目录。
	DataDir string

	// HistoryKeep 是保留的历史快照份数。
	HistoryKeep int

	// TombstoneTTL 是墓碑（已删除项）的保留时长，超期后由 GC 清理。
	TombstoneTTL time.Duration
}

const (
	defaultAddr         = ":8080"
	defaultDataDir      = "/data"
	defaultHistoryKeep  = 30
	defaultTombstoneTTL = 90 * 24 * time.Hour
	minTokenLen         = 32
)

func LoadConfig() (*Config, error) {
	cfg := &Config{
		Addr:         envOr("BMSYNC_ADDR", defaultAddr),
		Token:        os.Getenv("BMSYNC_TOKEN"),
		DataDir:      envOr("BMSYNC_DATA", defaultDataDir),
		HistoryKeep:  envIntOr("BMSYNC_HISTORY_KEEP", defaultHistoryKeep),
		TombstoneTTL: envDurationDaysOr("BMSYNC_TOMBSTONE_TTL_DAYS", defaultTombstoneTTL),
	}

	if len(cfg.Token) < minTokenLen {
		return nil, fmt.Errorf(
			"BMSYNC_TOKEN 未设置或短于 %d 字符（当前 %d）——请设置 `openssl rand -hex 32` 的输出",
			minTokenLen, len(cfg.Token))
	}
	if cfg.HistoryKeep < 1 {
		return nil, fmt.Errorf("BMSYNC_HISTORY_KEEP 必须 ≥ 1，当前 %d", cfg.HistoryKeep)
	}
	if cfg.TombstoneTTL < 24*time.Hour {
		return nil, fmt.Errorf("BMSYNC_TOMBSTONE_TTL_DAYS 过短（%v），墓碑太早清理会导致删除同步失效",
			cfg.TombstoneTTL)
	}
	return cfg, nil
}

func envOr(key, def string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return def
}

func envIntOr(key string, def int) int {
	v := os.Getenv(key)
	if v == "" {
		return def
	}
	n, err := strconv.Atoi(v)
	if err != nil {
		// 配置写错时退回默认值而不是崩溃：宁可行为不理想，也不要起不来。
		slog.Warn("环境变量不是合法整数，使用默认值", "key", key, "value", v, "default", def)
		return def
	}
	return n
}

func envDurationDaysOr(key string, def time.Duration) time.Duration {
	v := os.Getenv(key)
	if v == "" {
		return def
	}
	n, err := strconv.Atoi(v)
	if err != nil {
		slog.Warn("环境变量不是合法整数，使用默认值", "key", key, "value", v, "default", def)
		return def
	}
	return time.Duration(n) * 24 * time.Hour
}
