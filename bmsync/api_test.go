package main

import (
	"bytes"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

const testToken = "tttttttttttttttttttttttttttttttttttttttt" // 40 字符

func newTestServer(t *testing.T) (*Server, *httptest.Server) {
	t.Helper()
	srv, ts := newTestServerWithToken(t, testToken)
	return srv, ts
}

func newTestServerWithToken(t *testing.T, token string) (*Server, *httptest.Server) {
	t.Helper()
	cfg := testConfig(t)
	cfg.Token = token
	srv, err := NewServer(cfg, discardLogger())
	if err != nil {
		t.Fatalf("NewServer 失败: %v", err)
	}
	ts := httptest.NewServer(srv.Routes())
	t.Cleanup(ts.Close)
	return srv, ts
}

// doRaw 发一个原始字符串请求体，并确保 resp 被关闭。
// 用于测各种畸形输入 —— 那些输入无法用 json.Marshal 构造。
func doRaw(t *testing.T, method, url, token, body string) *http.Response {
	t.Helper()
	req, err := http.NewRequest(method, url, strings.NewReader(body))
	if err != nil {
		t.Fatal(err)
	}
	if token != "" {
		req.Header.Set("Authorization", "Bearer "+token)
	}
	req.Header.Set("Content-Type", "application/json")
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { resp.Body.Close() })
	return resp
}

func doJSON(t *testing.T, method, url, token string, body any) (*http.Response, []byte) {
	t.Helper()
	var buf bytes.Buffer
	if body != nil {
		if err := json.NewEncoder(&buf).Encode(body); err != nil {
			t.Fatal(err)
		}
	}
	resp := doRaw(t, method, url, token, buf.String())
	raw := new(bytes.Buffer)
	_, _ = raw.ReadFrom(resp.Body)
	return resp, raw.Bytes()
}

func TestAPIHealthNeedsNoAuth(t *testing.T) {
	_, ts := newTestServer(t)
	// options 页的"测试连接"要在用户填完令牌前就能确认服务活着
	resp, body := doJSON(t, http.MethodGet, ts.URL+"/api/health", "", nil)
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("health 不应要求鉴权，却返回 %d: %s", resp.StatusCode, body)
	}
	var h HealthResponse
	if err := json.Unmarshal(body, &h); err != nil {
		t.Fatal(err)
	}
	if !h.OK || h.Service != "bmsync" {
		t.Errorf("health 返回异常: %+v", h)
	}
	// health 绝不能泄露书签内容
	if strings.Contains(string(body), `"items":{`) {
		t.Error("health 响应中不应包含书签数据")
	}
}

func TestAPISyncRequiresValidToken(t *testing.T) {
	_, ts := newTestServer(t)
	good := SyncRequest{Device: "test", State: NewState()}

	t.Run("无 token", func(t *testing.T) {
		resp, body := doJSON(t, http.MethodPost, ts.URL+"/api/sync", "", good)
		if resp.StatusCode != http.StatusUnauthorized {
			t.Errorf("期望 401，实际 %d: %s", resp.StatusCode, body)
		}
	})

	t.Run("错误 token", func(t *testing.T) {
		resp, _ := doJSON(t, http.MethodPost, ts.URL+"/api/sync", strings.Repeat("x", 40), good)
		if resp.StatusCode != http.StatusUnauthorized {
			t.Errorf("期望 401，实际 %d", resp.StatusCode)
		}
	})

	t.Run("正确 token", func(t *testing.T) {
		resp, body := doJSON(t, http.MethodPost, ts.URL+"/api/sync", testToken, good)
		if resp.StatusCode != http.StatusOK {
			t.Fatalf("期望 200，实际 %d: %s", resp.StatusCode, body)
		}
	})
}

func TestAPIBearerSchemeIsCaseInsensitive(t *testing.T) {
	_, ts := newTestServer(t)
	req, err := http.NewRequest(http.MethodGet, ts.URL+"/api/conflicts", nil)
	if err != nil {
		t.Fatal(err)
	}
	req.Header.Set("Authorization", "bearer "+testToken) // 小写
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		t.Errorf("Bearer 前缀应大小写不敏感，实际 %d", resp.StatusCode)
	}
}

// 两台设备通过服务端收敛：这是整个系统的端到端主干路径。
func TestAPISyncRoundTrip(t *testing.T) {
	_, ts := newTestServer(t)

	// 设备 A（个人电脑）上报 3 个书签
	up := stateOf(
		kv("f", folder(RootToolbar, "目录", 1000, 0)),
		kv("b1", bookmark(keyOf("f"), "一", "https://one.example", 1000, 1)),
		kv("b2", bookmark(RootUnfiled, "二", "https://two.example", 1000, 2)),
	)
	resp, body := doJSON(t, http.MethodPost, ts.URL+"/api/sync", testToken,
		SyncRequest{Device: "pc-personal", State: up})
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("同步失败 %d: %s", resp.StatusCode, body)
	}
	var sr SyncResponse
	if err := json.Unmarshal(body, &sr); err != nil {
		t.Fatal(err)
	}
	if len(sr.State.Items) != 3 {
		t.Fatalf("服务端应存下 3 项，实际 %d", len(sr.State.Items))
	}
	if sr.Summary.Created != 3 {
		t.Errorf("created = %d，期望 3", sr.Summary.Created)
	}
	if sr.HLC == "" {
		t.Error("响应必须带 hlc 供客户端校时")
	}

	// 设备 B（公司电脑，本地空白）上报空状态，应拿回全部 3 项
	resp, body = doJSON(t, http.MethodPost, ts.URL+"/api/sync", testToken,
		SyncRequest{Device: "pc-work", State: NewState()})
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("同步失败 %d: %s", resp.StatusCode, body)
	}
	var sr2 SyncResponse
	if err := json.Unmarshal(body, &sr2); err != nil {
		t.Fatal(err)
	}
	if len(sr2.State.Items) != 3 {
		t.Fatalf("工作电脑应收到全部 3 项，实际 %d", len(sr2.State.Items))
	}

	// B 删除其中一项后重新上报。
	//
	// deletedAt 必须用**真实当前时间**：t0 是 2023 年的固定常量，相对
	// 2026 年的"现在"早已超过 90 天 TTL，会被 GC 当场清掉，测试就会
	// 误以为"删除没有传播"。
	now := time.Now().UnixMilli()
	del := stateOf(
		kv("f", folder(RootToolbar, "目录", 1000, 0)),
		kv("b1", bookmark(keyOf("f"), "一", "https://one.example", 1000, 1)),
		kv("b2", tomb(bookmark(RootUnfiled, "二", "https://two.example", 1000, 2), now, 2000, 0)),
	)
	resp, body = doJSON(t, http.MethodPost, ts.URL+"/api/sync", testToken,
		SyncRequest{Device: "pc-work", State: del})
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("删除同步失败 %d: %s", resp.StatusCode, body)
	}

	// A 再拉取，应看到删除已经传播过来
	resp, body = doJSON(t, http.MethodPost, ts.URL+"/api/sync", testToken,
		SyncRequest{Device: "pc-personal", State: NewState()})
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("同步失败 %d: %s", resp.StatusCode, body)
	}
	var sr3 SyncResponse
	if err := json.Unmarshal(body, &sr3); err != nil {
		t.Fatal(err)
	}
	if !sr3.State.Items[keyOf("b2")].D {
		t.Error("删除应传播到另一台设备")
	}
}

func TestAPIRejectsMalformedRequests(t *testing.T) {
	_, ts := newTestServer(t)

	t.Run("空 body", func(t *testing.T) {
		if resp := doRaw(t, http.MethodPost, ts.URL+"/api/sync", testToken, ""); resp.StatusCode != http.StatusBadRequest {
			t.Errorf("期望 400，实际 %d", resp.StatusCode)
		}
	})

	t.Run("非法 JSON", func(t *testing.T) {
		if resp := doRaw(t, http.MethodPost, ts.URL+"/api/sync", testToken, "{oops"); resp.StatusCode != http.StatusBadRequest {
			t.Errorf("期望 400，实际 %d", resp.StatusCode)
		}
	})

	t.Run("尾随多余内容", func(t *testing.T) {
		body := `{"device":"a","state":{"v":1,"items":{}}}{"extra":1}`
		if resp := doRaw(t, http.MethodPost, ts.URL+"/api/sync", testToken, body); resp.StatusCode != http.StatusBadRequest {
			t.Errorf("期望 400，实际 %d", resp.StatusCode)
		}
	})

	t.Run("未知字段", func(t *testing.T) {
		// DisallowUnknownFields：字段名拼错的客户端应该得到明确错误，
		// 而不是静默忽略后行为诡异
		body := `{"device":"a","state":{"v":1,"items":{}},"stat":"typo"}`
		if resp := doRaw(t, http.MethodPost, ts.URL+"/api/sync", testToken, body); resp.StatusCode != http.StatusBadRequest {
			t.Errorf("期望 400，实际 %d", resp.StatusCode)
		}
	})

	t.Run("schema 版本不符", func(t *testing.T) {
		bad := SyncRequest{Device: "a", State: State{V: 99, Items: map[string]Item{}}}
		if resp, _ := doJSON(t, http.MethodPost, ts.URL+"/api/sync", testToken, bad); resp.StatusCode != http.StatusBadRequest {
			t.Errorf("期望 400，实际 %d", resp.StatusCode)
		}
	})

	t.Run("item key 非法", func(t *testing.T) {
		bad := SyncRequest{Device: "a", State: State{V: 1, Items: map[string]Item{
			"tooshort": {P: RootToolbar, T: TypeBookmark, N: "x", U: "https://x.example", M: Encode(1, 0)},
		}}}
		resp, body := doJSON(t, http.MethodPost, ts.URL+"/api/sync", testToken, bad)
		if resp.StatusCode != http.StatusBadRequest {
			t.Errorf("key 非法的 state 期望 400，实际 %d: %s", resp.StatusCode, body)
		}
		if !strings.Contains(string(body), errValidation) {
			t.Errorf("错误码应为 %q，实际 %s", errValidation, body)
		}
	})

	t.Run("item 类型非法", func(t *testing.T) {
		bad := SyncRequest{Device: "a", State: State{V: 1, Items: map[string]Item{
			keyOf("b"): {P: RootToolbar, T: "z", N: "x", M: Encode(1, 0)},
		}}}
		if resp, _ := doJSON(t, http.MethodPost, ts.URL+"/api/sync", testToken, bad); resp.StatusCode != http.StatusBadRequest {
			t.Errorf("期望 400，实际 %d", resp.StatusCode)
		}
	})
}

func TestAPIStructuralProblemIsNotRejected(t *testing.T) {
	// 父节点缺失只是结构问题，不应拒掉整份上传 —— 那会让人完全没法同步。
	_, ts := newTestServer(t)
	orphan := SyncRequest{Device: "a", State: State{V: 1, Items: map[string]Item{
		keyOf("b"): {P: keyOf("ghost"), T: TypeBookmark, N: "孤儿", U: "https://x.example", M: Encode(1, 0)},
	}}}
	if resp, body := doJSON(t, http.MethodPost, ts.URL+"/api/sync", testToken, orphan); resp.StatusCode != http.StatusOK {
		t.Errorf("父节点缺失应被接受，实际 %d: %s", resp.StatusCode, body)
	}
}

func TestAPIRateLimit(t *testing.T) {
	_, ts := newTestServer(t)
	good := SyncRequest{Device: "test", State: NewState()}

	var last int
	for i := 0; i < RateLimitPerMinute+3; i++ {
		resp, _ := doJSON(t, http.MethodPost, ts.URL+"/api/sync", testToken, good)
		last = resp.StatusCode
		if resp.StatusCode == http.StatusTooManyRequests {
			if i < RateLimitPerMinute {
				t.Errorf("第 %d 次请求就被限流了（上限 %d）", i+1, RateLimitPerMinute)
			}
			return
		}
	}
	t.Errorf("连续 %d 次请求都未被限流，最后状态 %d", RateLimitPerMinute+3, last)
}

func TestRateLimiterIsPerKey(t *testing.T) {
	rl := newRateLimiter(2, 50*1000*1000) // 50 秒窗口，测试里不真等
	if !rl.allow("1.1.1.1") || !rl.allow("1.1.1.1") {
		t.Fatal("前两次应放行")
	}
	if rl.allow("1.1.1.1") {
		t.Error("第三次应被限流")
	}
	if !rl.allow("2.2.2.2") {
		t.Error("不同来源应有独立配额")
	}
}

func TestAPIConflictsAndHistory(t *testing.T) {
	_, ts := newTestServer(t)
	k := keyOf("b")
	orig := bookmark(RootToolbar, "原标题", "https://example.com", 1000, 0)

	// 1) 初始状态
	doJSON(t, http.MethodPost, ts.URL+"/api/sync", testToken,
		SyncRequest{Device: "pc-personal", State: stateOf(kv("b", orig))})

	// 2) 服务端这一侧被改成"云端改的"（基于 orig）
	serverSide := stateOf(kv("b", bookmark(RootToolbar, "云端改的", "https://example.com", 2000, 0)))
	doJSON(t, http.MethodPost, ts.URL+"/api/sync", testToken,
		SyncRequest{Device: "pc-personal", State: serverSide, Base: map[string]string{k: orig.M}})

	// 3) 客户端基于同一个 orig 也改了 —— 这才是真并发编辑
	clientSide := stateOf(kv("b", bookmark(RootToolbar, "本地改的", "https://example.com", 2001, 0)))
	doJSON(t, http.MethodPost, ts.URL+"/api/sync", testToken,
		SyncRequest{Device: "pc-work", State: clientSide, Base: map[string]string{k: orig.M}})

	resp, body := doJSON(t, http.MethodGet, ts.URL+"/api/conflicts?limit=10", testToken, nil)
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("读取冲突失败 %d: %s", resp.StatusCode, body)
	}
	var cr ConflictsResponse
	if err := json.Unmarshal(body, &cr); err != nil {
		t.Fatal(err)
	}
	if len(cr.Conflicts) != 1 {
		t.Fatalf("应有 1 条并发冲突，实际 %d: %+v", len(cr.Conflicts), cr.Conflicts)
	}
	c := cr.Conflicts[0]
	if c.Reason != ReasonConcurrentEdit {
		t.Errorf("Reason = %q，期望 %q", c.Reason, ReasonConcurrentEdit)
	}
	if c.Device != "pc-work" {
		t.Errorf("Device = %q，应记录上报方 pc-work", c.Device)
	}

	// 第 2 步是单边编辑（服务端改、客户端没改），不该记为冲突
	// 上面的 len==1 断言已经隐含了这一点

	resp, body = doJSON(t, http.MethodGet, ts.URL+"/api/history", testToken, nil)
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("读取历史失败 %d: %s", resp.StatusCode, body)
	}
	var hr HistoryResponse
	if err := json.Unmarshal(body, &hr); err != nil {
		t.Fatal(err)
	}
	if hr.Snapshots == nil {
		t.Error("history 应返回数组而非 null")
	}
}

func TestDecodeBodyLimit(t *testing.T) {
	// MaxBodyBytes 是 16MB，在测试里真的造 16MB 太浪费。
	// 直接用小限额验证 decodeBody 的超限分支。
	rec := httptest.NewRecorder()
	// 必须是**合法 JSON**，否则解码会在第一个字符就报语法错误，
	// 永远走不到限额检查。
	oversized := `{"a":"` + strings.Repeat("x", 1000) + `"}`
	req := httptest.NewRequest(http.MethodPost, "/", strings.NewReader(oversized))

	var dst map[string]any
	if err := decodeBody(rec, req, &dst, 100); err == nil {
		t.Fatal("超出限额应返回错误")
	}
	if rec.Code != http.StatusRequestEntityTooLarge {
		t.Errorf("期望 413，实际 %d", rec.Code)
	}
}

func TestClientIPPrefersForwardedFor(t *testing.T) {
	req := httptest.NewRequest(http.MethodPost, "/", nil)
	req.RemoteAddr = "10.0.0.1:1234"
	if got := clientIP(req); got != "10.0.0.1" {
		t.Errorf("无 XFF 时应取 RemoteAddr，实际 %q", got)
	}

	req.Header.Set("X-Forwarded-For", "203.0.113.7, 10.0.0.1")
	if got := clientIP(req); got != "203.0.113.7" {
		t.Errorf("应取 XFF 的第一段，实际 %q", got)
	}

	// 伪造的后续段不应影响判定
	req.Header.Set("X-Forwarded-For", "  203.0.113.7  ,  evil")
	if got := clientIP(req); got != "203.0.113.7" {
		t.Errorf("应忽略伪造的后续段，实际 %q", got)
	}
}
