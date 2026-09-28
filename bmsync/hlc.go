package main

import (
	"errors"
	"fmt"
	"strconv"
	"strings"
	"sync"
	"time"
)

// HLC —— 混合逻辑时钟（Hybrid Logical Clock）
//
// 论文：Kulkarni et al., "Logical Physical Clocks and Consistent Snapshots
//       in Globally Distributed Databases", 2014
//
// 解决的问题：合并用 LWW（Last-Write-Wins）决胜，而 LWW 的正确性完全依赖
// 时间戳的可比性。若直接用 Date.now()，两台机器时钟偏差 5 分钟就会导致
// 「我 10:00 加的书签」被「你 10:03 看到的旧版本」覆盖 —— 而且用户完全
// 无感。这是同步工具最糟糕的失败方式。
//
// HLC 保证**因果序**（causal order）：只要 A 端先发生的操作、B 端后看到了它，
// B 端此后产生的任何时间戳都一定严格大于 A 端那个。这与两台机器的实际时钟
// 差多少无关。
//
// ── 编码格式 ──────────────────────────────────────────────────────────
//
//	encode(l, c) = sprintf("%013d-%05d", l, c)
//
// 13 位物理毫秒（可用至公元 2286 年）+ 5 位逻辑计数。定宽，因此
// **字符串字典序 == 时间戳全序**，存储在 JSON 里也天然有序。
//
// ⚠️ 本文件的算法与 extension/lib/hlc.js 必须逐行等价。两端不一致会导致
//    合并在「m 恰好相等」时产生非确定行为。两端由 test/hlc_vectors.json
//    做交叉验证（见 docs/plan.md P3.2）。

const (
	hlcPhysicalDigits = 13
	hlcCounterDigits  = 5

	// hlcCounterMax 是逻辑计数的上限。溢出时进位到物理部分（l+1, c=0），
	// 从而保持编码定宽。
	hlcCounterMax = 99_999

	hlcFormat = "0000000000000-00000"
)

// HLCZero 是「最小」时间戳，用于"该 key 在 base 中不存在"之外的比较起点。
const HLCZero = HLCZeroValue

// HLCZeroValue 是一个常量表达式，避免 HLCZero 变成 var（那样任何包都能改它）。
const HLCZeroValue = "0000000000000-00000"

var errBadHLC = errors.New("HLC 格式非法")

// HLC 是一个可并发使用的混合逻辑时钟。
type HLC struct {
	mu sync.Mutex

	l uint64 // 最后一个已知的物理时间（毫秒）
	c uint32 // 该毫秒内的逻辑计数

	// nowFn 便于测试注入确定性的时钟。生产环境为 time.Now().UnixMilli。
	nowFn func() int64
}

func NewHLC() *HLC {
	return &HLC{nowFn: func() int64 { return time.Now().UnixMilli() }}
}

func NewHLCWithClock(nowFn func() int64) *HLC {
	return &HLC{nowFn: nowFn}
}

// Now 为一个**本地事件**产生时间戳。
func (h *HLC) Now() string {
	h.mu.Lock()
	defer h.mu.Unlock()

	p := uint64(h.nowFn())
	if p > h.l {
		h.l = p
		h.c = 0
	} else {
		h.c++
	}
	h.normalize()
	return Encode(h.l, h.c)
}

// Update 收到一个远端时间戳后推进本地时钟，并返回一个新的本地时间戳。
//
// 调用时机：客户端收到服务端返回的 hlc 之后；服务端收到请求里所有
// item 的最大 HLC 之后。见 docs/design.md §6.4。
func (h *HLC) Update(remote string) string {
	h.mu.Lock()
	defer h.mu.Unlock()

	p := uint64(h.nowFn())

	rl, rc, err := Decode(remote)
	if err != nil {
		// 远端时间戳非法：退化成纯本地推进。
		// 之所以不返回错误，是因为一次非法输入不应该让整个同步失败。
		if p > h.l {
			h.l = p
			h.c = 0
		} else {
			h.c++
		}
		h.normalize()
		return Encode(h.l, h.c)
	}

	max := h.l
	if p > max {
		max = p
	}
	if rl > max {
		max = rl
	}

	switch {
	case max == h.l && max == p:
		// 本地时钟与物理时钟同时领先：逻辑计数取较大者再加一
		if rc > h.c {
			h.c = rc
		}
		h.c++
	case max == h.l:
		// 本地时钟领先：只在已有计数上前进
		h.c++
	case max == rl:
		// 远端时间戳领先（含"物理时钟刚好追平远端"这一情形）：
		// 必须接在远端计数之后，否则下一次生成的本地事件会小于刚收到的
		// 远端事件，因果性被破坏。
		h.c = rc + 1
	default:
		// max == p 且严格大于 h.l 与 rl：进入了一个全新的毫秒
		h.c = 0
	}
	h.l = max

	h.normalize()
	return Encode(h.l, h.c)
}

// Current 返回当前时间戳但不推进逻辑计数（等价于 Now，但不改变状态）。
// 用途：生成响应里给客户端看的 hlc 字段。
func (h *HLC) Current() string {
	h.mu.Lock()
	defer h.mu.Unlock()
	return Encode(h.l, h.c)
}

func (h *HLC) normalize() {
	if h.c > hlcCounterMax {
		h.l++
		h.c = 0
	}
}

// ObserveMany 批量吸收多个远端时间戳，把本地时钟推进到它们的最大值之后。
//
// 服务端在合并前用它校准时钟：把请求里所有 item 的 a / m 都喂进来，
// 于是服务端之后发出的任何时间戳都严格大于它见过的所有客户端时间戳。
// 见 docs/design.md §6.4。
func (h *HLC) ObserveMany(ts []string) string {
	h.mu.Lock()
	defer h.mu.Unlock()
	for _, t := range ts {
		l, c, err := Decode(t)
		if err != nil {
			continue
		}
		if CompareEncoded(l, c, h.l, h.c) > 0 {
			h.l, h.c = l, c
		}
	}
	return Encode(h.l, h.c)
}

// Encode 把 (物理毫秒, 逻辑计数) 编码成定宽字符串。
func Encode(l uint64, c uint32) string {
	return fmt.Sprintf("%0*d-%0*d", hlcPhysicalDigits, l, hlcCounterDigits, c)
}

// Decode 解析 HLC 字符串。
func Decode(s string) (l uint64, c uint32, err error) {
	if len(s) != hlcPhysicalDigits+1+hlcCounterDigits || s[hlcPhysicalDigits] != '-' {
		return 0, 0, fmt.Errorf("%w: %q", errBadHLC, s)
	}
	l, err = strconv.ParseUint(s[:hlcPhysicalDigits], 10, 64)
	if err != nil {
		return 0, 0, fmt.Errorf("%w: %q", errBadHLC, s)
	}
	cv, err := strconv.ParseUint(s[hlcPhysicalDigits+1:], 10, 32)
	if err != nil {
		return 0, 0, fmt.Errorf("%w: %q", errBadHLC, s)
	}
	return l, uint32(cv), nil
}

// ValidHLC 判断字符串是否为合法 HLC。
func ValidHLC(s string) bool {
	_, _, err := Decode(s)
	return err == nil
}

// Compare 比较两个 HLC：-1 / 0 / 1。
//
// 非法时间戳被当作比任何合法值都**小**，这样合并时非法的一侧永远输，
// 不会污染权威状态。两侧都非法时退化为字典序比较以保证确定性。
func Compare(a, b string) int {
	al, ac, ae := Decode(a)
	bl, bc, be := Decode(b)
	switch {
	case ae != nil && be != nil:
		return strings.Compare(a, b)
	case ae != nil:
		return -1
	case be != nil:
		return 1
	}
	return CompareEncoded(al, ac, bl, bc)
}

// CompareEncoded 比较已经解析好的 (l, c) 二元组，避免重复解析。
func CompareEncoded(al uint64, ac uint32, bl uint64, bc uint32) int {
	switch {
	case al < bl:
		return -1
	case al > bl:
		return 1
	case ac < bc:
		return -1
	case ac > bc:
		return 1
	}
	return 0
}

// MaxHLC 返回列表中最大的时间戳；列表为空时返回 HLCZero。
func MaxHLC(list ...string) string {
	out := HLCZero
	for _, s := range list {
		if Compare(s, out) > 0 {
			out = s
		}
	}
	return out
}

// hlcLayout 供文档与测试使用，说明编码格式。
const hlcLayout = "13 位物理毫秒 + '-' + 5 位逻辑计数，例：1790000000000-00042"
