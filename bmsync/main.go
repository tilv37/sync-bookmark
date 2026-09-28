// bmsync —— 书签同步服务端
//
// 设计文档：docs/design.md
//
// 本文件是 HTTP 入口与进程生命周期。合并算法见 merge.go，HLC 见 hlc.go。
package main

import (
	"context"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"log/slog"
	"net"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"
)

func main() {
	if len(os.Args) > 1 && os.Args[1] == "healthcheck" {
		os.Exit(runHealthcheck())
	}
	if err := run(); err != nil {
		slog.Error("fatal", "err", err)
		os.Exit(1)
	}
}

func run() error {
	check := flag.Bool("config-check", false, "只校验配置与数据目录后退出")
	flag.Parse()

	logger := slog.New(slog.NewTextHandler(os.Stderr, &slog.HandlerOptions{Level: slog.LevelInfo}))
	slog.SetDefault(logger)

	cfg, err := LoadConfig()
	if err != nil {
		return err
	}

	srv, err := NewServer(cfg, logger)
	if err != nil {
		return err
	}
	if *check {
		fmt.Println("config ok")
		return nil
	}
	logger.Info("config loaded",
		"addr", cfg.Addr,
		"data", cfg.DataDir,
		"historyKeep", cfg.HistoryKeep,
		"tombstoneTTL", cfg.TombstoneTTL,
	)

	httpSrv := &http.Server{
		Addr:              cfg.Addr,
		Handler:           srv.Routes(),
		ReadHeaderTimeout: 10 * time.Second,
		ReadTimeout:       30 * time.Second,
		WriteTimeout:      30 * time.Second,
		IdleTimeout:       60 * time.Second,
	}

	// 优雅关闭：收到 SIGTERM/SIGINT 后停止接受新请求，
	// 并等待进行中的请求完成（上限 shutdownTimeout）。
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	errCh := make(chan error, 1)
	go func() {
		logger.Info("listening", "addr", cfg.Addr)
		if err := httpSrv.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
			errCh <- err
		}
	}()

	select {
	case err := <-errCh:
		return err
	case <-ctx.Done():
		logger.Info("shutting down")
		shutCtx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
		defer cancel()
		if err := httpSrv.Shutdown(shutCtx); err != nil {
			return fmt.Errorf("shutdown: %w", err)
		}
		logger.Info("bye")
		return nil
	}
}

// runHealthcheck 供 docker HEALTHCHECK 使用。
//
// 为什么需要它：最终镜像基于 FROM scratch，没有 shell 也没有 wget，
// 所以 healthcheck 只能用 exec 形式调用二进制自身。详见 design.md §10.5。
func runHealthcheck() int {
	addr := envOr("BMSYNC_ADDR", ":8080")
	host, port, err := net.SplitHostPort(addr)
	if err != nil {
		fmt.Fprintln(os.Stderr, "healthcheck: bad BMSYNC_ADDR:", err)
		return 2
	}
	if host == "" || host == "0.0.0.0" || host == "::" {
		host = "127.0.0.1"
	}

	url := fmt.Sprintf("http://%s:%s/api/health", host, port)
	client := &http.Client{Timeout: 3 * time.Second}
	resp, err := client.Get(url)
	if err != nil {
		fmt.Fprintln(os.Stderr, "healthcheck:", err)
		return 1
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		fmt.Fprintln(os.Stderr, "healthcheck: status", resp.Status)
		return 1
	}
	// 读掉并校验 body，确保返回的确实是本服务而不是误路由到别处
	var probe struct {
		OK      bool   `json:"ok"`
		Service string `json:"service"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&probe); err != nil {
		fmt.Fprintln(os.Stderr, "healthcheck: bad body:", err)
		return 1
	}
	if !probe.OK || probe.Service != "bmsync" {
		fmt.Fprintln(os.Stderr, "healthcheck: unexpected body")
		return 1
	}
	return 0
}
