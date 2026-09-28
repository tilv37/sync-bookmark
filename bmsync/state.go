package main

import (
	"errors"
	"fmt"
	"sort"
)

// SchemaVersion 是 state.json 的 schema 版本。
//
// 版本不匹配时服务**拒绝启动**而非自动迁移：书签是用户不可再生的数据，
// 静默改写它的风险远大于「升级后手工处理一次」的麻烦。见 design.md §8.7。
const SchemaVersion = 1

// ItemType 是 item 的类型。
type ItemType string

const (
	TypeBookmark ItemType = "b" // 书签：有 URL
	TypeFolder   ItemType = "f" // 文件夹：无 URL
)

// Item 是同步的最小单位，对应一个书签或一个文件夹。
//
// 字段名用单字母是因为体积：5000 个书签的 JSON 用全字段名约 1.1 MB，
// 用短名约 800 KB，而每次同步都是全量传输。
type Item struct {
	P string   `json:"p"`            // parent key（父目录）；顶层 item 为四个根目录 id 之一
	T ItemType `json:"t"`            // 类型
	N string   `json:"n"`            // 标题 / 文件夹名
	U string   `json:"u,omitempty"`  // URL，仅书签有
	A string   `json:"a"`            // HLC：创建时间，不参与合并
	M string   `json:"m"`            // HLC：最后修改时间 —— LWW 的唯一比较依据
	D bool     `json:"d,omitempty"`  // deleted：墓碑标记
	X int64    `json:"x,omitempty"`  // deletedAt：删除的墙钟毫秒，仅用于 GC 判定
}

// State 是完整的同步状态，即 state.json 的内存表示。
type State struct {
	V     int            `json:"v"`
	HLC   string         `json:"hlc,omitempty"`
	Items map[string]Item `json:"items"`
}

func NewState() State {
	return State{V: SchemaVersion, Items: make(map[string]Item)}
}

// Clone 返回一份深拷贝。
//
// 为什么必须深拷贝：Merge 会把 server 与 incoming 的 item 放进结果 map。
// 若直接复用原 map 的 value 副本（Item 是值类型，拷贝本身是安全的），
// 真正危险的是 map 本身被共享 —— 后续若有人就地修改 result.Items，
// 会连带污染 server.State。所以这里连 map 一起复制。
func (s State) Clone() State {
	out := State{V: s.V, HLC: s.HLC, Items: make(map[string]Item, len(s.Items))}
	for k, v := range s.Items {
		out.Items[k] = v
	}
	return out
}

// sameContent 判断两个 item 的**内容**是否相同。
//
// 关键：只比较 (P, T, N, U)，不比较 M / A / D / X。
// M 是时间戳而不是内容——同一个书签在两台机器上若被各自重新保存一次，
// M 会不同但内容完全一样，不应该被当成冲突。
func sameContent(a, b Item) bool {
	return a.P == b.P && a.T == b.T && a.N == b.N && a.U == b.U
}

// 四个 Firefox 系统根目录的固定 id。
//
// 这些 id 在 Firefox 各版本与各语言界面下都是稳定的，因此可以安全地
// 直接当作顶层 item 的 parent key 使用 —— 跨设备、跨语言都不需要映射表。
// 见 design.md §3.4。
const (
	RootToolbar = "toolbar_____" // 书签栏
	RootMenu    = "menu________" // 菜单
	RootUnfiled = "unfiled_____" // 其他书签
	RootMobile  = "mobile______" // 移动设备书签（默认不同步）
)

var rootFolders = map[string]bool{
	RootToolbar: true,
	RootMenu:    true,
	RootUnfiled: true,
	RootMobile:  true,
}

func IsRootFolder(key string) bool { return rootFolders[key] }

// Validate 校验一份 state 是否可以安全参与合并。
//
// 严格程度是刻意分级的：
//
//   - **硬错误**（返回 error）：会导致解析出错或资源失控的问题 —— 版本不符、
//     key 格式非法、类型非法、HLC 格式非法、超出数量/深度/长度上限。
//     这些情况说明输入损坏或恶意，必须拒绝。
//   - **软警告**（返回 []string，不阻断）：树结构层面的问题 —— 父节点缺失、
//     父节点不是文件夹、存在环。这些理论上不该出现（采集端总是从真实的
//     Firefox 树生成，父节点必然存在），但一旦出现，让整份上传被拒会非常
//     挫伤用户。改为记录警告，由 apply 阶段做兜底（父节点找不到就跳过该 item）。
func (s State) Validate() (warnings []string, err error) {
	if s.V != SchemaVersion {
		return nil, fmt.Errorf("schema 版本不符：期望 %d，收到 %d", SchemaVersion, s.V)
	}
	if len(s.Items) > MaxItems {
		return nil, fmt.Errorf("item 数量 %d 超过上限 %d", len(s.Items), MaxItems)
	}

	for key, it := range s.Items {
		if len(key) != KeyLen || !isHex(key) {
			return nil, fmt.Errorf("item key 非法：%q（期望 %d 位十六进制）", key, KeyLen)
		}
		switch it.T {
		case TypeBookmark:
			if it.U == "" {
				return nil, fmt.Errorf("item %s：书签必须有 url", key)
			}
			if len(it.U) > MaxURLLen {
				return nil, fmt.Errorf("item %s：url 长度 %d 超过上限 %d", key, len(it.U), MaxURLLen)
			}
		case TypeFolder:
			if it.U != "" {
				return nil, fmt.Errorf("item %s：文件夹不应有 url", key)
			}
		default:
			return nil, fmt.Errorf("item %s：类型非法 %q", key, it.T)
		}
		if len(it.N) > MaxTitleLen {
			return nil, fmt.Errorf("item %s：标题长度 %d 超过上限 %d", key, len(it.N), MaxTitleLen)
		}
		if _, _, e := Decode(it.M); e != nil {
			return nil, fmt.Errorf("item %s：m 字段不是合法 HLC：%q", key, it.M)
		}
		if it.A != "" {
			if _, _, e := Decode(it.A); e != nil {
				return nil, fmt.Errorf("item %s：a 字段不是合法 HLC：%q", key, it.A)
			}
		}
		if it.D && it.X == 0 {
			warnings = append(warnings, fmt.Sprintf("item %s 是墓碑但缺少删除时间 x", key))
		}
	}

	// 树结构检查
	for key, it := range s.Items {
		if IsRootFolder(it.P) {
			continue // 顶层 item，父为系统根目录，正常
		}
		parent, ok := s.Items[it.P]
		if !ok {
			warnings = append(warnings, fmt.Sprintf("item %s 的父节点 %s 不存在", key, it.P))
			continue
		}
		if parent.T != TypeFolder {
			warnings = append(warnings, fmt.Sprintf("item %s 的父节点 %s 不是文件夹", key, it.P))
			continue
		}
		// 父节点是墓碑而子节点存活，一定是状态不一致：apply 阶段无法把它
		// 挂到任何地方。记录下来但继续（该 item 在 apply 时会被安全跳过）。
		if parent.D && !it.D {
			warnings = append(warnings, fmt.Sprintf("item %s 存活但父节点 %s 已删除", key, it.P))
		}
	}

	// 深度检查（带环检测，防御性：损坏数据可能构造出环导致死循环）
	for key := range s.Items {
		d, err := s.depthOf(key)
		if err != nil {
			warnings = append(warnings, fmt.Sprintf("item %s 的父链异常：%v", key, err))
			continue
		}
		if d > MaxDepth {
			return nil, fmt.Errorf("item %s 深度 %d 超过上限 %d", key, d, MaxDepth)
		}
	}

	return warnings, nil
}

// depthOf 返回 key 所在位置的深度（根目录的直接子项深度为 1）。
func (s State) depthOf(key string) (int, error) {
	seen := make(map[string]bool, 8)
	depth := 0
	cur := key
	for {
		it, ok := s.Items[cur]
		if !ok {
			return 0, fmt.Errorf("父节点 %s 缺失", cur)
		}
		if IsRootFolder(it.P) {
			return depth + 1, nil
		}
		if seen[cur] {
			return 0, errors.New("父链成环")
		}
		seen[cur] = true
		cur = it.P
		depth++
		if depth > MaxDepth+1 {
			// 提前退出，避免构造出的长链让 depthOf 跑很久
			return depth, fmt.Errorf("父链超过 %d 层", MaxDepth+1)
		}
	}
}

// MaxHLC 返回本 state 中最大的 HLC（扫描所有 a 与 m 字段）。
//
// 服务端在处理一次同步后用它推进自己的 HLC，从而保证服务端发出的
// 时间戳一定大于它见过的所有客户端时间戳。见 design.md §6.4。
func (s State) MaxHLC() string {
	out := HLCZero
	for _, it := range s.Items {
		if Compare(it.M, out) > 0 {
			out = it.M
		}
		if Compare(it.A, out) > 0 {
			out = it.A
		}
	}
	return out
}

// Count 返回活跃（非墓碑）item 数。
func (s State) Count() int {
	n := 0
	for _, it := range s.Items {
		if !it.D {
			n++
		}
	}
	return n
}

func isHex(s string) bool {
	for i := 0; i < len(s); i++ {
		c := s[i]
		if (c < '0' || c > '9') && (c < 'a' || c > 'f') {
			return false
		}
	}
	return len(s) > 0
}

// keysSorted 返回全部 key 的升序切片。
//
// 确定性很重要：合并遍历 map 的顺序是随机的，而冲突列表、HLC 碰撞时的
// 兜底决胜都依赖顺序稳定，否则同样的输入会产生不同的输出。
func (s State) keysSorted() []string {
	out := make([]string, 0, len(s.Items))
	for k := range s.Items {
		out = append(out, k)
	}
	sort.Strings(out)
	return out
}
