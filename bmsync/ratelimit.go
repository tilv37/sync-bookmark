package main

import (
	"sync"
	"time"
)

// rateLimiter 是一个按来源计数的令牌桶。
//
// 为什么需要：这是一个暴露在公网（经由反代）的接口。虽然有 token 保护，
// 但如果没有限流，一个拿着 token 的脚本（或者单纯的好奇者反复试 token）
// 可以无限次打 /api/sync。每次请求都要做一次全量合并 + 落盘，
// 足够把一台小 VPS 打满。
//
// 手写而不用 golang.org/x/time/rate，是为了保持"零第三方依赖"这条约束
// （design.md §8.1）。功能上只需要每分钟 N 次，精度要求很低。
type rateLimiter struct {
	mu      sync.Mutex
	limit   int
	window  time.Duration
	buckets map[string]*bucket
	lastGC  time.Time
}

type bucket struct {
	count int
	start time.Time
}

func newRateLimiter(limit int, window time.Duration) *rateLimiter {
	return &rateLimiter{
		limit:   limit,
		window:  window,
		buckets: make(map[string]*bucket),
		lastGC:  time.Now(),
	}
}

// allow 判断这次请求是否放行，并消耗一个配额。
func (rl *rateLimiter) allow(key string) bool {
	now := time.Now()

	rl.mu.Lock()
	defer rl.mu.Unlock()

	rl.gc(now)

	b, ok := rl.buckets[key]
	if !ok || now.Sub(b.start) >= rl.window {
		rl.buckets[key] = &bucket{count: 1, start: now}
		return true
	}
	if b.count >= rl.limit {
		return false
	}
	b.count++
	return true
}

// gc 清理过期的桶。手工触发而非后台定时器：调用频率就是请求频率，
// 不值得为它开一个 goroutine。
func (rl *rateLimiter) gc(now time.Time) {
	if now.Sub(rl.lastGC) < rl.window {
		return
	}
	rl.lastGC = now
	for k, b := range rl.buckets {
		if now.Sub(b.start) >= rl.window {
			delete(rl.buckets, k)
		}
	}
}
