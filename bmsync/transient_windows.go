//go:build windows

package main

import (
	"errors"
	"io/fs"
	"syscall"
)

// Windows 错误码（来自 winerror.h）。
//
// 之所以手写常量而不用 syscall.ERROR_SHARING_VIOLATION：Go 的 syscall
// 包只导出了它自己用得到的错误码，这两个并不在里面。
const (
	errorSharingViolation = syscall.Errno(32)
	errorLockViolation    = syscall.Errno(33)
	errorAccessDenied     = syscall.Errno(5)
)

// isTransientFileError 判断错误是否属于"稍后重试就好"的那一类。
//
// Windows 上 rename 走 MoveFileEx，遇到杀毒软件实时扫描、文件索引器、
// 编辑器临时锁定等情况会返回 ACCESS_DENIED 或 SHARING_VIOLATION。
// 这些占用通常只持续几毫秒，重试即可成功。
func isTransientFileError(err error) bool {
	if err == nil {
		return false
	}
	if errors.Is(err, fs.ErrPermission) {
		return true
	}
	var errno syscall.Errno
	if errors.As(err, &errno) {
		switch errno {
		case errorSharingViolation, errorLockViolation, errorAccessDenied:
			return true
		}
	}
	return false
}
