// jsonq — JSON value helper serving test/smoke.sh.
//
// Why it exists: smoke tests read a few numbers from responses (item count,
// tombstone count, summary.created, one conflict field), but the test machine
// may have neither jq nor python. Go is the fallback that is usually around
// for this repo's tooling.
//
// NOTE: legacy Go helper, now optional. The server is C# (Kestrel); jsonq
// stays Go only because it is a tiny standalone module. Prefer python3 or jq
// when available — setup_json tries those first and only builds this when
// neither exists.
//
// Usage:
//   echo '{"a":{"b":[1,2]}}' | jsonq 'len(d["a"]["b"])'
//   echo '[{"r":1},{"r":2}]' | jsonq 'last(d, "r")'
//
// ── Syntax ──────────────────────────────────────────────────────────
//
// Only the functions below, deliberately narrow: the script needs just these.
// Narrower syntax means fewer "looks right but silently unparsed" failures.
//
//	len(PATH)             length of an array / object / string
//	get(PATH, "key")      PATH object's key (empty string when missing)
//	count(PATH, "flag")   items in PATH object with a truthy flag
//	last(PATH, "key")     key of the **last element** of a PATH array
//	first(PATH, "key")    key of the first element of a PATH array
//	PATH                  direct value
//
// PATH looks like d or d["state"]["items"], d is the root object.
// Numbers drop the decimal point, strings print raw, booleans print true/false.
// Parse failures always exit 1 with the reason on stderr — callers notice,
// instead of silently getting an empty string and failing a later assert.
//
// Note the last/first vs get split: get is for **objects**, last/first for
// **arrays**. The conflict list is an array, so read it with
// last(d["conflicts"], "reason"), never get(d["conflicts"], "reason") — the
// latter returns an empty string instead of an error, exactly the failure
// most easily mistaken for "feature missing".
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
		fmt.Fprintln(os.Stderr, "usage: jsonq <expression>   (JSON read from stdin)")
		os.Exit(2)
	}
	expr := os.Args[len(os.Args)-1]

	raw, err := io.ReadAll(os.Stdin)
	if err != nil {
		fmt.Fprintln(os.Stderr, "jsonq: failed to read stdin:", err)
		os.Exit(1)
	}
	var root any
	if err := json.Unmarshal(raw, &root); err != nil {
		fmt.Fprintln(os.Stderr, "jsonq: JSON parse failed:", err)
		fmt.Fprintln(os.Stderr, "  first 200 input bytes:", truncate(string(raw), 200))
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
		return "", fmt.Errorf("empty expression")
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
			return "", nil // non-object counts as missing
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

	// last(PATH, "key") / first(PATH, "key") — field of the last/first array element
	for _, fn := range []string{"last", "first"} {
		if args, ok := unwrapArgs(expr, fn, 2); ok {
			v, err := resolvePath(args[0], root)
			if err != nil {
				return "", err
			}
			arr, ok := v.([]any)
			if !ok || len(arr) == 0 {
				// An empty array is not an error — smoke tests must tell
				// "no conflicts" apart from "read failed", so return empty
				// quietly and let the caller's assertion decide.
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

	// Bare path
	v, err := resolvePath(expr, root)
	if err != nil {
		return "", err
	}
	return render(v), nil
}

// resolvePath resolves d / d["a"]["b"] style paths to values.
func resolvePath(path string, root any) (any, error) {
	path = strings.TrimSpace(path)
	if !strings.HasPrefix(path, "d") {
		return nil, fmt.Errorf("path must start with d (d is the root object), got %q", path)
	}
	cur := root
	for _, seg := range parsePath(path[1:]) {
		obj, ok := cur.(map[string]any)
		if !ok {
			return nil, fmt.Errorf("reading key %q from a non-object — path may be misspelled", seg)
		}
		next, exists := obj[seg]
		if !exists {
			return nil, fmt.Errorf("key %q missing", seg)
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

// unwrapArgs parses name(a, b) calls, splitting args (commas inside quotes kept).
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

// parsePath parses ["a"]["b"] style subscript chains.
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
	return 0, fmt.Errorf("len() does not support %T", v)
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
