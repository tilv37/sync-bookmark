// jsonq —— JSON 取值工具，专为 test/smoke.sh 服务。
//
// 为什么需要它：冒烟测试要读响应里的几个数字（item 数量、墓碑数量、
// summary.created、冲突的某个字段），但测试机上不一定装了 jq 或 python。
// Go 一定有 —— 服务端本身就是 Go 写的。
//
// 用法：
//   echo '{"a":{"b":[1,2]}}' | jsonq 'len(d["a"]["b"])'
//   echo '[{"r":1},{"r":2}]' | jsonq 'last(d, "r")'
//
// ── 语法 ──────────────────────────────────────────────────────────────
//
// 只支持下面这几个函数，刻意做得极窄：脚本里就用得上这几种。语法越窄，
// 越不容易出现"看起来对其实没解析"的静默失败。
//
//	len(PATH)             数组 / 对象 / 字符串的长度
//	get(PATH, "key")      取 PATH 对象的 key（缺失返回空串）
//	count(PATH, "flag")   数一数 PATH 对象里有多少项的 flag 为真
//	last(PATH, "key")     取 PATH **数组最后一个元素**的 key
//	first(PATH, "key")    取 PATH 数组第一个元素的 key
//	PATH                   直接取值
//
// PATH 形如 d 或 d["state"]["items"]，d 表示根对象。
// 数字去掉小数点，字符串原样输出，布尔输出 true/false。
// 解析失败一律退出码 1 并把原因打到 stderr —— 调用方据此发现问题，
// 而不是静默拿到空串后让断言假失败。
//
// 注意 last/first 与 get 的区别：get 用于**对象**，last/first 用于
// **数组**。冲突列表是数组，所以要 last(d["conflicts"], "reason")，
// 不能 get(d["conflicts"], "reason") —— 那个会返回空串而不是报错，
// 正是最容易被误判成"功能没实现"的那种失败。
package main

import (
	"encoding/json"
	"fmt"
	"io"
	"os"
	"strconv"
	"strings"
)

func main() {
	if len(os.Args) < 2 {
		fmt.Fprintln(os.Stderr, "用法: jsonq <表达式>   (JSON 从 stdin 读)")
		os.Exit(2)
	}
	expr := os.Args[len(os.Args)-1]

	raw, err := io.ReadAll(os.Stdin)
	if err != nil {
		fmt.Fprintln(os.Stderr, "jsonq: 读取 stdin 失败:", err)
		os.Exit(1)
	}
	var root any
	if err := json.Unmarshal(raw, &root); err != nil {
		fmt.Fprintln(os.Stderr, "jsonq: JSON 解析失败:", err)
		fmt.Fprintln(os.Stderr, "  输入前 200 字节:", truncate(string(raw), 200))
		os.Exit(1)
	}

	out, err := eval(expr, root)
	if err != nil {
		fmt.Fprintln(os.Stderr, "jsonq:", err)
		os.Exit(1)
	}
	fmt.Println(out)
}

func truncate(s string, n int) string {
	if len(s) <= n {
		return s
	}
	return s[:n] + "..."
}

func eval(expr string, root any) (string, error) {
	expr = strings.TrimSpace(expr)
	if expr == "" {
		return "", fmt.Errorf("表达式为空")
	}

	// len(...)
	if inner, ok := unwrapCall(expr, "len"); ok {
		v, err := resolvePath(inner, root)
		if err != nil {
			return "", err
		}
		n, err := lengthOf(v)
		if err != nil {
			return "", err
		}
		return strconv.Itoa(n), nil
	}

	// get(PATH, "key")
	if args, ok := unwrapArgs(expr, "get", 2); ok {
		v, err := resolvePath(args[0], root)
		if err != nil {
			return "", err
		}
		obj, ok := v.(map[string]any)
		if !ok {
			return "", nil // 不是对象就当没有
		}
		return render(obj[args[1]]), nil
	}

	// count(PATH, "flag")
	if args, ok := unwrapArgs(expr, "count", 2); ok {
		v, err := resolvePath(args[0], root)
		if err != nil {
			return "", err
		}
		obj, ok := v.(map[string]any)
		if !ok {
			return "0", nil
		}
		n := 0
		for _, item := range obj {
			if m, ok := item.(map[string]any); ok && truthy(m[args[1]]) {
				n++
			}
		}
		return strconv.Itoa(n), nil
	}

	// last(PATH, "key") / first(PATH, "key") —— 取数组首/末元素的字段
	for _, fn := range []string{"last", "first"} {
		if args, ok := unwrapArgs(expr, fn, 2); ok {
			v, err := resolvePath(args[0], root)
			if err != nil {
				return "", err
			}
			arr, ok := v.([]any)
			if !ok || len(arr) == 0 {
				// 空数组不是错误 —— 冒烟测试要区分"没有冲突"和"读取失败"，
				// 所以这里安静地返回空串，由调用方的断言去判断。
				return "", nil
			}
			idx := len(arr) - 1
			if fn == "first" {
				idx = 0
			}
			m, ok := arr[idx].(map[string]any)
			if !ok {
				return "", nil
			}
			return render(m[args[1]]), nil
		}
	}

	// 裸路径
	v, err := resolvePath(expr, root)
	if err != nil {
		return "", err
	}
	return render(v), nil
}

// resolvePath 解析 d / d["a"]["b"] 形式的路径并返回值。
func resolvePath(path string, root any) (any, error) {
	path = strings.TrimSpace(path)
	if !strings.HasPrefix(path, "d") {
		return nil, fmt.Errorf("路径必须以 d 开头（d 表示根对象），得到 %q", path)
	}
	cur := root
	for _, seg := range parsePath(path[1:]) {
		obj, ok := cur.(map[string]any)
		if !ok {
			return nil, fmt.Errorf("在非对象上取 key %q —— 路径写法可能不对", seg)
		}
		next, exists := obj[seg]
		if !exists {
			return nil, fmt.Errorf("key %q 不存在", seg)
		}
		cur = next
	}
	return cur, nil
}

func unwrapCall(expr, name string) (string, bool) {
	prefix := name + "("
	if !strings.HasPrefix(expr, prefix) || !strings.HasSuffix(expr, ")") {
		return "", false
	}
	return strings.TrimSuffix(strings.TrimPrefix(expr, prefix), ")"), true
}

// unwrapArgs 解析 name(a, b) 形式的调用并切分参数（引号内的逗号不切）。
func unwrapArgs(expr, name string, want int) ([]string, bool) {
	inner, ok := unwrapCall(expr, name)
	if !ok {
		return nil, false
	}
	var args []string
	var cur strings.Builder
	var quote byte
	for i := 0; i < len(inner); i++ {
		ch := inner[i]
		switch {
		case quote != 0:
			if ch == quote {
				quote = 0
			} else {
				cur.WriteByte(ch)
			}
		case ch == '"' || ch == '\'':
			quote = ch
		case ch == ',':
			args = append(args, strings.TrimSpace(cur.String()))
			cur.Reset()
		default:
			cur.WriteByte(ch)
		}
	}
	args = append(args, strings.TrimSpace(cur.String()))
	if len(args) != want {
		return nil, false
	}
	return args, true
}

// parsePath 解析 ["a"]["b"] 形式的下标链。
func parsePath(s string) []string {
	var out []string
	for i := 0; i < len(s); {
		if s[i] != '[' {
			i++
			continue
		}
		j := strings.IndexByte(s[i:], ']')
		if j < 0 {
			break
		}
		out = append(out, strings.Trim(s[i+1:i+j], `"'`))
		i += j + 1
	}
	return out
}

func lengthOf(v any) (int, error) {
	switch t := v.(type) {
	case []any:
		return len(t), nil
	case map[string]any:
		return len(t), nil
	case string:
		return len(t), nil
	}
	return 0, fmt.Errorf("len() 不支持 %T", v)
}

func render(v any) string {
	switch t := v.(type) {
	case string:
		return t
	case float64:
		if t == float64(int64(t)) {
			return strconv.FormatInt(int64(t), 10)
		}
		return strconv.FormatFloat(t, 'f', -1, 64)
	case bool:
		return strconv.FormatBool(t)
	case nil:
		return ""
	default:
		b, _ := json.Marshal(t)
		return string(b)
	}
}

func truthy(v any) bool {
	switch t := v.(type) {
	case bool:
		return t
	case string:
		return t != ""
	case float64:
		return t != 0
	}
	return false
}
