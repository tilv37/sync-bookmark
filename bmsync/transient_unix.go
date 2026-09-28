//go:build !windows

package main

import (
	"errors"
	"syscall"
)

// isTransientFileError 判断错误是否属于"稍后重试就好"的那一类。
//
// POSIX 上 rename(2) 在同一文件系统内是原子的，不存在 Windows 那种
// "被别的进程短暂占用"的情况。唯一值得重试的是 NFS 等网络文件系统上的
// ESTALE —— 那不是并发问题，而是重试确实能解决的。
//
// 生产环境跑在 Linux 容器里，所以这个实现刻意保持保守：只认网络文件
// 系统这一种情况，其余一律立即返回错误。宁可直接失败，也不要用重试
// 掩盖真正的权限或磁盘问题 —— 那种错误重试只会变成一个慢速的失败。
func isTransientFileError(err error) bool {
	return err != nil && errors.Is(err, syscall.ESTALE)
}
