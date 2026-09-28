package main

import (
	"io"
	"log/slog"
)

// discardLogger 返回一个把日志丢掉的 logger，供单元测试使用。
//
// 刻意不用 slog.Default()：那会把测试日志混进 go test 的输出里，
// 失败时难以定位是哪条日志属于哪条断言。
func discardLogger() *slog.Logger {
	return slog.New(slog.NewTextHandler(io.Discard, &slog.HandlerOptions{Level: slog.LevelError}))
}
