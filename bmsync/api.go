package main

import (
	"crypto/subtle"
	"encoding/json"
	"errors"
	"io"
	"log/slog"
	"net"
	"net/http"
	"strconv"
	"strings"
	"time"
)

// ── API 的请求 / 响应结构 ─────────────────────────────────────────────

// SyncRequest 是 POST /api/sync 的请求体。
type SyncRequest struct {
	// Device 是上报方的标识，仅用于冲突记录与日志排查。
	Device string `json:"device"`

	// State 是客户端本地的完整状态（不是增量）。
	State State `json:"state"`

	// Base 是客户端「上次从服务端收到的状态」里每个 key 的 HLC 快照。
	// 它的唯一用途是区分「我改了」与「对面改了」，从而只记录真正的并发
	// 冲突。首次同步时为空。见 docs/design.md §5.2。
	Base map[string]string `json:"base"`
}

// SyncResponse 是 POST /api/sync 的响应体。
type SyncResponse struct {
	// State 是合并后的权威状态。客户端应**完整替换**自己的本地缓存。
	State State `json:"state"`

	// Summary 是本次合并的统计。
	Summary Summary `json:"summary"`

	// Conflicts 是本次新产生的冲突。全量缓冲在 /api/conflicts。
	Conflicts []Conflict `json:"conflicts"`

	// HLC 是服务端当前时间戳，客户端应用后应调用 HLC.update() 校准时钟。
	HLC string `json:"hlc"`

	// ServerTime 是服务端墙钟毫秒，仅供客户端检测明显异常的本地时钟。
	ServerTime int64 `json:"serverTime"`
}

type HealthResponse struct {
	OK         bool   `json:"ok"`
	Service    string `json:"service"`
	Schema     int    `json:"schema"`
	Items      int    `json:"items"`
	Active     int    `json:"active"`
	Conflicts  int    `json:"conflicts"`
	Uptime     int64  `json:"uptime"`
	ServerTime int64  `json:"serverTime"`
}

type ConflictsResponse struct {
	Conflicts []Conflict `json:"conflicts"`
	Total     int        `json:"total"`
}

// SnapshotInfo 是一个历史快照的元信息。
type SnapshotInfo struct {
	ID    string `json:"id"`
	At    int64  `json:"at"`
	Items int    `json:"items"`
	Size  int64  `json:"size"`
}

type HistoryResponse struct {
	Snapshots []SnapshotInfo `json:"snapshots"`
}

type ErrorResponse struct {
	Code    string `json:"code"`
	Message string `json:"message"`
}

// 错误码（与 design.md §7.6 一致）
const (
	errBadRequest    = "bad_request"
	errUnauthorized  = "unauthorized"
	errPayloadTooBig = "payload_too_large"
	errValidation    = "validation_failed"
	errRateLimited   = "rate_limited"
	errInternal      = "internal_error"
)

// ── Server ───────────────────────────────────────────────────────────

type Server struct {
	cfg   *Config
	log   *slog.Logger
	store *Store
	rl    *rateLimiter
	start time.Time
}

// NewServer 构造服务端。Store 初始化失败（数据目录不可写、state.json 损坏、
// schema 版本不匹配）时直接返回错误 —— 带着半残状态继续服务只会放大问题。
func NewServer(cfg *Config, log *slog.Logger) (*Server, error) {
	st, err := NewStore(cfg, log)
	if err != nil {
		return nil, err
	}
	return &Server{
		cfg:   cfg,
		log:   log,
		store: st,
		rl:    newRateLimiter(RateLimitPerMinute, time.Minute),
		start: time.Now(),
	}, nil
}

func (s *Server) Routes() http.Handler {
	mux := http.NewServeMux()

	// /api/health 不鉴权：options 页的"测试连接"需要在用户填完令牌前
	// 就能确认服务活着。它不返回任何书签内容。
	mux.HandleFunc("GET /api/health", s.handleHealth)

	mux.HandleFunc("GET /api/conflicts", s.protect(s.handleConflicts))
	mux.HandleFunc("GET /api/history", s.protect(s.handleHistory))
	mux.HandleFunc("POST /api/sync", s.protect(s.handleSync))

	return logRequests(s.log, mux)
}

// ── 鉴权与限流 ────────────────────────────────────────────────────────

// protect 是所有数据接口共用的中间件：限流 + Bearer token 鉴权。
//
// 顺序很重要：先限流再鉴权。否则一个拿到错误 token 的人可以用
// 429/401 的响应差异来区分"token 是否存在"。
func (s *Server) protect(next http.HandlerFunc) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		ip := clientIP(r)
		if !s.rl.allow(ip) {
			w.Header().Set("Retry-After", "60")
			writeError(w, http.StatusTooManyRequests, errRateLimited, "请求过于频繁，请稍后再试")
			return
		}

		presented := bearerToken(r)
		// 恒定时间比较：防止通过响应时间差异逐字节猜出 token
		if subtle.ConstantTimeCompare([]byte(presented), []byte(s.cfg.Token)) != 1 {
			s.log.Warn("鉴权失败", "ip", ip, "path", r.URL.Path)
			writeError(w, http.StatusUnauthorized, errUnauthorized, "访问令牌无效")
			return
		}
		next(w, r)
	}
}

func bearerToken(r *http.Request) string {
	const prefix = "Bearer "
	h := r.Header.Get("Authorization")
	if len(h) > len(prefix) && strings.EqualFold(h[:len(prefix)], prefix) {
		return h[len(prefix):]
	}
	return ""
}

// clientIP 取真实客户端 IP。反代场景下 X-Forwarded-For 的第一段才是真实
// 来源；只取第一段，避免伪造的头绕过限流。
func clientIP(r *http.Request) string {
	if xff := r.Header.Get("X-Forwarded-For"); xff != "" {
		for i := 0; i < len(xff); i++ {
			if xff[i] == ',' {
				return strings.TrimSpace(xff[:i])
			}
		}
		return strings.TrimSpace(xff)
	}
	host, _, err := net.SplitHostPort(r.RemoteAddr)
	if err != nil {
		return r.RemoteAddr
	}
	return host
}

// ── 处理器 ────────────────────────────────────────────────────────────

func (s *Server) handleHealth(w http.ResponseWriter, r *http.Request) {
	writeJSON(w, http.StatusOK, HealthResponse{
		OK:         true,
		Service:    "bmsync",
		Schema:     SchemaVersion,
		Items:      s.store.ItemCount(),
		Active:     s.store.ActiveCount(),
		Conflicts:  len(s.store.Conflicts(0)),
		Uptime:     int64(time.Since(s.start).Seconds()),
		ServerTime: time.Now().UnixMilli(),
	})
}

func (s *Server) handleSync(w http.ResponseWriter, r *http.Request) {
	var req SyncRequest
	if err := decodeBody(w, r, &req, MaxBodyBytes); err != nil {
		return // decodeBody 已经写过错误响应
	}

	if req.State.V != SchemaVersion {
		writeError(w, http.StatusBadRequest, errBadRequest,
			"state 的 schema 版本不匹配，请确认扩展与服务端版本配套")
		return
	}
	// 老版本扩展可能漏发 schema 版本
	if req.State.Items == nil {
		req.State.Items = map[string]Item{}
	}

	// 硬错误（key 格式、类型、HLC、超限额）直接拒绝 —— 这类输入说明数据
	// 损坏或格式不兼容，放行只会把问题带进合并。
	// 结构性问题（父节点缺失、父链成环）只记日志不拒绝：为一个孤立节点
	// 拒掉整份上传，对用户来说比跳过那个节点糟糕得多。
	warnings, err := req.State.Validate()
	if err != nil {
		s.log.Warn("拒绝不合法的上传", "device", req.Device, "err", err)
		writeError(w, http.StatusBadRequest, errValidation, "书签数据校验失败: "+err.Error())
		return
	}
	for _, warn := range warnings {
		s.log.Warn("上传数据存在结构问题", "device", req.Device, "warn", warn)
	}

	res, err := s.store.Sync(req.State, req.Base, req.Device)
	if err != nil {
		s.log.Error("同步失败", "device", req.Device, "err", err)
		writeError(w, http.StatusInternalServerError, errInternal, "服务端处理失败，本次未修改任何数据")
		return
	}

	s.log.Info("同步完成",
		"device", req.Device,
		"created", res.Summary.Created,
		"updated", res.Summary.Updated,
		"deleted", res.Summary.Deleted,
		"unchanged", res.Summary.Unchanged,
		"conflicts", len(res.Conflicts),
		"items", len(res.State.Items),
	)

	if res.Conflicts == nil {
		res.Conflicts = []Conflict{}
	}
	writeJSON(w, http.StatusOK, SyncResponse{
		State:      res.State,
		Summary:    res.Summary,
		Conflicts:  res.Conflicts,
		HLC:        res.State.HLC,
		ServerTime: time.Now().UnixMilli(),
	})
}

func (s *Server) handleConflicts(w http.ResponseWriter, r *http.Request) {
	limit := 50
	if v := r.URL.Query().Get("limit"); v != "" {
		if n, err := strconv.Atoi(v); err == nil && n > 0 {
			limit = n
		}
	}
	all := s.store.Conflicts(limit)
	writeJSON(w, http.StatusOK, ConflictsResponse{Conflicts: all, Total: len(all)})
}

func (s *Server) handleHistory(w http.ResponseWriter, r *http.Request) {
	snaps, err := s.store.Snapshots()
	if err != nil {
		s.log.Error("读取快照列表失败", "err", err)
		writeError(w, http.StatusInternalServerError, errInternal, "读取快照列表失败")
		return
	}
	writeJSON(w, http.StatusOK, HistoryResponse{Snapshots: snaps})
}

// ── 通用工具 ──────────────────────────────────────────────────────────

func decodeBody(w http.ResponseWriter, r *http.Request, dst any, limit int64) error {
	r.Body = http.MaxBytesReader(w, r.Body, limit)
	dec := json.NewDecoder(r.Body)
	dec.DisallowUnknownFields()

	if err := dec.Decode(dst); err != nil {
		var maxErr *http.MaxBytesError
		if errors.As(err, &maxErr) {
			writeError(w, http.StatusRequestEntityTooLarge, errPayloadTooBig,
				"书签数据超过上限，请检查是否有异常条目")
			return err
		}
		if errors.Is(err, io.EOF) {
			writeError(w, http.StatusBadRequest, errBadRequest, "请求体为空")
			return err
		}
		writeError(w, http.StatusBadRequest, errBadRequest, "请求体解析失败: "+err.Error())
		return err
	}
	// 拒绝尾随的第二个 JSON 值：通常是客户端 bug，也可能是攻击试探
	if dec.More() {
		writeError(w, http.StatusBadRequest, errBadRequest, "请求体包含多余内容")
		return errors.New("trailing content after JSON value")
	}
	return nil
}

func writeJSON(w http.ResponseWriter, code int, v any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	// 本服务只提供 API，不被任何浏览器页面直接加载，无需 CORS。
	w.WriteHeader(code)
	if err := json.NewEncoder(w).Encode(v); err != nil {
		slog.Error("写入响应失败", "err", err)
	}
}

func writeError(w http.ResponseWriter, code int, errCode, msg string) {
	writeJSON(w, code, ErrorResponse{Code: errCode, Message: msg})
}

// logRequests 记录方法、路径、状态、耗时。
//
// **刻意不记录请求体**：body 里含有全部书签 URL，把它们写进日志等同于
// 制造一份浏览历史副本。反代（NPM）侧的访问日志同理，只记 URL。
func logRequests(log *slog.Logger, next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		start := time.Now()
		sw := &statusWriter{ResponseWriter: w, code: http.StatusOK}
		next.ServeHTTP(sw, r)
		if strings.HasPrefix(r.URL.Path, "/api/") {
			log.Info("http",
				"method", r.Method,
				"path", r.URL.Path,
				"status", sw.code,
				"ms", time.Since(start).Milliseconds(),
			)
		}
	})
}

type statusWriter struct {
	http.ResponseWriter
	code int
}

func (w *statusWriter) WriteHeader(code int) {
	w.code = code
	w.ResponseWriter.WriteHeader(code)
}
