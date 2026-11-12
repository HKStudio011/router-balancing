package main

import (
	"bytes"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"net/url"
	"reflect"
	"strings"
	"testing"
	"time"
)

// runSidecar phục vụ handleChat thật trên một httptest server cục bộ và trỏ
// upstream của sidecar sang `upstream` (test server giả). Trả về response host
// nhận được cùng toàn bộ body đã đọc.
//
// upstreamBaseURL là biến package chỉ được đổi trong lúc test chạy tuần tự —
// các test pipeline không được gọi t.Parallel().
func runSidecar(t *testing.T, upstream *httptest.Server, req *http.Request) (*http.Response, []byte) {
	t.Helper()

	previousBaseURL := upstreamBaseURL
	upstreamBaseURL = upstream.URL
	t.Cleanup(func() { upstreamBaseURL = previousBaseURL })

	mux := http.NewServeMux()
	mux.HandleFunc("/v1/chat/completions", handleChat)
	sidecar := httptest.NewServer(mux)
	t.Cleanup(sidecar.Close)

	base, err := url.Parse(sidecar.URL)
	if err != nil {
		t.Fatalf("parse sidecar url: %v", err)
	}
	outReq := req.Clone(req.Context())
	outReq.URL.Scheme = base.Scheme
	outReq.URL.Host = base.Host
	outReq.Host = base.Host
	outReq.RequestURI = ""

	resp, err := sidecar.Client().Do(outReq)
	if err != nil {
		t.Fatalf("gọi sidecar: %v", err)
	}
	defer resp.Body.Close()
	body, err := io.ReadAll(resp.Body)
	if err != nil {
		t.Fatalf("đọc response sidecar: %v", err)
	}
	return resp, body
}

// upstreamCapture ghi lại request mà sidecar gửi lên upstream giả.
type upstreamCapture struct {
	body   []byte
	header http.Header
	method string
	path   string
}

// captureUpstream bọc handler giả: handler con chạy sau khi request đã được ghi
// lại. Trả về server cùng con trỏ capture để test assert sau khi request xong.
func captureUpstream(t *testing.T, status int, respBody string, handler func(w http.ResponseWriter, r *http.Request, c *upstreamCapture)) (*httptest.Server, *upstreamCapture) {
	t.Helper()
	capture := &upstreamCapture{}
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		body, err := io.ReadAll(r.Body)
		if err != nil {
			t.Errorf("đọc body upstream: %v", err)
		}
		capture.body = body
		capture.header = r.Header.Clone()
		capture.method = r.Method
		capture.path = r.URL.Path
		if handler != nil {
			handler(w, r, capture)
			return
		}
		w.Header().Set("Content-Type", "text/event-stream")
		w.WriteHeader(status)
		if _, err := io.WriteString(w, respBody); err != nil {
			t.Errorf("ghi body upstream: %v", err)
		}
	}))
	t.Cleanup(server.Close)
	return server, capture
}

// decodeObjectBody parse body upstream đã capture thành JSON object.
func decodeObjectBody(t *testing.T, body []byte) map[string]any {
	t.Helper()
	var payload map[string]any
	if err := json.Unmarshal(body, &payload); err != nil {
		t.Fatalf("body upstream không phải JSON object: %v\n%.200s", err, body)
	}
	if payload == nil {
		t.Fatal("body upstream parse ra nil object")
	}
	return payload
}

// toolNamesOf trả tên tool theo thứ tự trong body (tools[].function.name).
func toolNamesOf(t *testing.T, payload map[string]any) []string {
	t.Helper()
	tools, ok := payload["tools"].([]any)
	if !ok {
		t.Fatalf("body upstream thiếu mảng tools: %T", payload["tools"])
	}
	names := make([]string, 0, len(tools))
	for _, entry := range tools {
		tool, ok := entry.(map[string]any)
		if !ok {
			t.Fatalf("tools entry không phải object: %T", entry)
		}
		fn, ok := tool["function"].(map[string]any)
		if !ok {
			t.Fatalf("tools entry thiếu function object: %T", tool["function"])
		}
		name, _ := fn["name"].(string)
		names = append(names, name)
	}
	return names
}

// firstMessage trả message đầu tiên trong payload.
func firstMessage(t *testing.T, payload map[string]any) map[string]any {
	t.Helper()
	messages, ok := payload["messages"].([]any)
	if !ok || len(messages) == 0 {
		t.Fatalf("body upstream thiếu messages: %T", payload["messages"])
	}
	first, ok := messages[0].(map[string]any)
	if !ok {
		t.Fatalf("messages[0] không phải object: %T", messages[0])
	}
	return first
}

// pinnedKeyOrderIndexes trả vị trí byte của 6 key pinned trong body thô. Body đã
// serialize phải có đủ key và đúng thứ tự này (khớp cách client 2.x ghi).
func pinnedKeyOrderIndexes(t *testing.T, body []byte) []int {
	t.Helper()
	keys := []string{
		`"model":`, `"messages":`, `"tools":`, `"stream":`, `"stream_options":`, `"max_completion_tokens":`,
	}
	indexes := make([]int, 0, len(keys))
	for _, key := range keys {
		i := bytes.Index(body, []byte(key))
		if i < 0 {
			t.Fatalf("body upstream thiếu key %s: %.200s", key, body)
		}
		indexes = append(indexes, i)
	}
	return indexes
}

// canonicalHeaderValue lấy giá trị header đầu tiên không phụ thuộc cách viết hoa.
func canonicalHeaderValue(t *testing.T, header http.Header, name string) string {
	t.Helper()
	values := header.Values(name)
	if len(values) == 0 {
		return ""
	}
	return values[0]
}

// clientChatBody dựng body chat free lane: 2 client tool + history nhiều lượt.
func clientChatBody(t *testing.T) []byte {
	t.Helper()
	body, err := json.Marshal(map[string]any{
		"model": testModelID,
		"messages": []any{
			map[string]any{"role": "system", "content": "You are a helpful coding assistant."},
			map[string]any{"role": "user", "content": "which files changed?"},
			map[string]any{"role": "assistant", "content": "let me check"},
			map[string]any{"role": "user", "content": "thanks, now run the tests"},
		},
		"tools": []any{
			map[string]any{"type": "function", "function": map[string]any{"name": "my_editor"}},
			map[string]any{"type": "function", "function": map[string]any{"name": "run_bash"}},
		},
		"stream": false,
	})
	if err != nil {
		t.Fatalf("marshal client body: %v", err)
	}
	return body
}

func TestPipeline_FreeLane_RequestShape(t *testing.T) {
	// Không test nào khác chạm giá trị production của 2 hằng này (runSidecar
	// override upstreamBaseURL), nên ghim tại đây: sai host/path là lỗi âm thầm
	// toàn hệ thống mà test khác không bắt được.
	if upstreamBaseURL != "https://opencode.ai/inference" {
		t.Errorf("upstreamBaseURL mặc định = %q, want https://opencode.ai/inference", upstreamBaseURL)
	}
	if upstreamChatPath != "/openai/v1/chat/completions" {
		t.Errorf("upstreamChatPath = %q, want /openai/v1/chat/completions", upstreamChatPath)
	}

	upstream, capture := captureUpstream(t, http.StatusOK, "data: {\"choices\":[{\"delta\":{}}]}\n\n", nil)

	// Client là host .NET: keyless (không Authorization), gửi kèm identity header
	// v1 legacy phải bị sidecar thay/thải — session sai shape và x-opencode-request
	// không được lọt lên upstream (spec §5.3). Project 40-hex client gửi phải được
	// chuyển tiếp nguyên giá trị qua projectID (wiring §5.4).
	childSession := buildOfficialSession(t, time.Now().UnixMilli())
	const clientProject = "0123456789abcdef0123456789abcdef01234567"
	req := httptest.NewRequest(http.MethodPost, "/v1/chat/completions", bytes.NewReader(clientChatBody(t)))
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("x-opencode-session", "ses_0000000000000000000000000000")
	req.Header.Set("x-opencode-session-id", childSession)
	req.Header.Set("x-opencode-request", "legacy-request-id")
	req.Header.Set("x-opencode-project", clientProject)

	resp, _ := runSidecar(t, upstream, req)

	if capture.method != http.MethodPost {
		t.Errorf("upstream method = %q, want POST", capture.method)
	}
	if want := "/openai/v1/chat/completions"; capture.path != want {
		t.Errorf("upstream path = %q, want %q", capture.path, want)
	}
	if got := canonicalHeaderValue(t, capture.header, "Authorization"); got != "Bearer public" {
		t.Errorf("upstream Authorization = %q, want %q", got, "Bearer public")
	}
	if got := canonicalHeaderValue(t, capture.header, "User-Agent"); got != "opencode/latest/2.1.1/cli" {
		t.Errorf("upstream User-Agent = %q, want opencode/latest/2.1.1/cli", got)
	}
	if got := canonicalHeaderValue(t, capture.header, "x-opencode-client"); got != "cli" {
		t.Errorf("upstream x-opencode-client = %q, want cli", got)
	}

	root := canonicalHeaderValue(t, capture.header, "x-opencode-session")
	if !canonicalSessionPattern.MatchString(root) || !isPlausibleOfficialSession(root, time.Now()) {
		t.Errorf("upstream x-opencode-session = %q, không phải session plausible", root)
	}
	for _, name := range []string{"x-session-affinity", "X-Session-Id"} {
		if got := canonicalHeaderValue(t, capture.header, name); got != root {
			t.Errorf("upstream %s = %q, want root %q", name, got, root)
		}
	}
	if got := canonicalHeaderValue(t, capture.header, "x-opencode-session-id"); got != childSession {
		t.Errorf("upstream x-opencode-session-id = %q, want child %q", got, childSession)
	}
	project := canonicalHeaderValue(t, capture.header, "x-opencode-project")
	if project != clientProject {
		t.Errorf("upstream x-opencode-project = %q, want %q (giữ 40-hex của client)", project, clientProject)
	}
	if got := canonicalHeaderValue(t, capture.header, "x-opencode-request"); got != "" {
		t.Errorf("x-opencode-request lọt lên upstream: %q", got)
	}

	payload := decodeObjectBody(t, capture.body)
	first := firstMessage(t, payload)
	if first["role"] != "system" {
		t.Errorf("messages[0].role = %v, want system (official prompt prepended)", first["role"])
	}
	content, _ := first["content"].(string)
	if !strings.Contains(content, opencodePromptMarker) {
		t.Errorf("messages[0] thiếu marker prompt chính chủ: %.120q", content)
	}
	if !strings.Contains(content, "You are a helpful coding assistant.") {
		t.Error("system prompt của client phải được fold vào official prompt")
	}
	if messages, _ := payload["messages"].([]any); len(messages) != 4 {
		t.Errorf("messages = %d, want 4 (1 folded system + 3 lượt history)", len(messages))
	}

	wantNames := append([]string{wireNameOf(t, "my_editor"), wireNameOf(t, "run_bash")}, expectedOfficialNames...)
	if got := toolNamesOf(t, payload); !reflect.DeepEqual(got, wantNames) {
		t.Errorf("tools = %v\nwant %v", got, wantNames)
	}
	if stream, _ := payload["stream"].(bool); !stream {
		t.Error("body upstream phải force stream=true")
	}

	indexes := pinnedKeyOrderIndexes(t, capture.body)
	for i := 1; i < len(indexes); i++ {
		if indexes[i] < indexes[i-1] {
			t.Fatalf("key order sai tại %d: %v", i, indexes)
		}
	}
	if !bytes.Contains(capture.body, []byte(`"stream":true`)) {
		t.Error("body upstream thiếu \"stream\":true")
	}
	if !bytes.Contains(capture.body, []byte(`"max_completion_tokens":32000`)) {
		t.Error("body upstream thiếu max_completion_tokens=32000 mặc định")
	}

	if got := resp.Header.Get("X-Sidecar-Shaped"); got != "tools+prompt" {
		t.Errorf("X-Sidecar-Shaped = %q, want tools+prompt", got)
	}
	if got := resp.Header.Get("X-Sidecar-Title"); got != "" {
		t.Errorf("X-Sidecar-Title = %q, want rỗng (không synthesize)", got)
	}
}

func TestPipeline_KeyedLane_BodyByteIdentical(t *testing.T) {
	// Body cố tình có khoảng trắng + thứ tự key lạ: mọi re-serialize (dù nội dung
	// tương đương) sẽ lộ ra qua so sánh byte.
	keyedBody := []byte("{\"extra_key\" : 1,\n  \"model\": \"m\", \"messages\": [{\"role\":\"user\",\"content\":\"hi\"}],\n  \"tools\": [{\"type\":\"function\",\"function\":{\"name\":\"Bash\"}}], \"stream\": false}")
	sseFrame := "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"name\":\"shell\",\"arguments\":\"\"}}]}}]}\n\ndata: [DONE]\n\n"
	upstream, capture := captureUpstream(t, http.StatusOK, sseFrame, nil)

	req := httptest.NewRequest(http.MethodPost, "/v1/chat/completions", bytes.NewReader(keyedBody))
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Authorization", "Bearer sk-real")

	resp, respBody := runSidecar(t, upstream, req)

	if !bytes.Equal(capture.body, keyedBody) {
		t.Errorf("body upstream bị sửa:\n got %.240s\nwant %.240s", capture.body, keyedBody)
	}
	if got := canonicalHeaderValue(t, capture.header, "Authorization"); got != "Bearer sk-real" {
		t.Errorf("upstream Authorization = %q, want Bearer sk-real", got)
	}
	if got := canonicalHeaderValue(t, capture.header, "x-opencode-client"); got != "cli" {
		t.Errorf("upstream x-opencode-client = %q, want cli (identity headers vẫn set)", got)
	}
	root := canonicalHeaderValue(t, capture.header, "x-opencode-session")
	if root == "" || canonicalHeaderValue(t, capture.header, "x-opencode-session-id") != root {
		t.Errorf("keyed lane phải set session headers: session=%q session-id=%q", root, canonicalHeaderValue(t, capture.header, "x-opencode-session-id"))
	}
	if got := canonicalHeaderValue(t, capture.header, "x-opencode-request"); got != "" {
		t.Errorf("x-opencode-request lọt lên upstream: %q", got)
	}

	if got := resp.Header.Get("X-Sidecar-Shaped"); got != "none" {
		t.Errorf("X-Sidecar-Shaped = %q, want none", got)
	}
	if string(respBody) != sseFrame {
		t.Errorf("keyed lane phải nhận SSE nguyên byte:\n got %.240q\nwant %.240q", respBody, sseFrame)
	}
}

func TestPipeline_KeyedPublicValue_TreatedKeyless(t *testing.T) {
	upstream, capture := captureUpstream(t, http.StatusOK, "data: [DONE]\n\n", nil)

	body, err := json.Marshal(map[string]any{
		"model": testModelID,
		"messages": []any{
			map[string]any{"role": "system", "content": "You are a helpful assistant."},
			map[string]any{"role": "user", "content": "hello"},
		},
	})
	if err != nil {
		t.Fatalf("marshal client body: %v", err)
	}

	req := httptest.NewRequest(http.MethodPost, "/v1/chat/completions", bytes.NewReader(body))
	req.Header.Set("Content-Type", "application/json")
	// Sai case: gate yêu cầu đúng "Bearer public" case-sensitive — sidecar phải
	// nhận ra đây là keyless và ghi lại đúng giá trị.
	req.Header.Set("Authorization", "Bearer PUBLIC")

	resp, _ := runSidecar(t, upstream, req)

	if got := canonicalHeaderValue(t, capture.header, "Authorization"); got != "Bearer public" {
		t.Errorf("upstream Authorization = %q, want Bearer public (case-sensitive)", got)
	}
	if got := resp.Header.Get("X-Sidecar-Shaped"); got != "tools+prompt" {
		t.Errorf("X-Sidecar-Shaped = %q, want tools+prompt (free lane shaping)", got)
	}
	payload := decodeObjectBody(t, capture.body)
	if got := toolNamesOf(t, payload); !reflect.DeepEqual(got, expectedOfficialNames) {
		t.Errorf("tools = %v, want official set %v", got, expectedOfficialNames)
	}
}

func TestPipeline_SSERestore_EndToEnd(t *testing.T) {
	wire := wireNameOf(t, "Bash")
	frame := `data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"name":"` + wire + `","arguments":""}}]}}]}` + "\n\n"
	done := "data: [DONE]\n\n"

	splitAt := strings.Index(frame, wire) + len(wire)/2
	upstream, _ := captureUpstream(t, http.StatusOK, "", func(w http.ResponseWriter, _ *http.Request, _ *upstreamCapture) {
		flusher, ok := w.(http.Flusher)
		if !ok {
			t.Error("upstream giả không flush được")
		}
		w.Header().Set("Content-Type", "text/event-stream")
		w.WriteHeader(http.StatusOK)
		// Cắt đôi tên wire giữa frame: bufio.Reader của sidecar phải nối đủ dòng
		// trước khi restore — nếu restore chạy trên mảnh thì tên không khôi phục được.
		if _, err := io.WriteString(w, frame[:splitAt]); err != nil {
			t.Errorf("ghi mảnh đầu: %v", err)
		}
		if ok {
			flusher.Flush()
		}
		if _, err := io.WriteString(w, frame[splitAt:]); err != nil {
			t.Errorf("ghi mảnh hai: %v", err)
		}
		if ok {
			flusher.Flush()
		}
		if _, err := io.WriteString(w, done); err != nil {
			t.Errorf("ghi done: %v", err)
		}
	})

	clientBody, err := json.Marshal(map[string]any{
		"model": testModelID,
		"messages": []any{
			map[string]any{"role": "user", "content": "run the tests"},
		},
		"tools": []any{
			map[string]any{"type": "function", "function": map[string]any{"name": "Bash"}},
		},
		"stream": true,
	})
	if err != nil {
		t.Fatalf("marshal client body: %v", err)
	}
	req := httptest.NewRequest(http.MethodPost, "/v1/chat/completions", bytes.NewReader(clientBody))
	req.Header.Set("Content-Type", "application/json")

	_, respBody := runSidecar(t, upstream, req)

	want := strings.Replace(frame, `"name":"`+wire+`"`, `"name":"Bash"`, 1) + done
	if string(respBody) != want {
		t.Errorf("SSE restore sai:\n got %.300q\nwant %.300q", respBody, want)
	}
	if strings.Contains(string(respBody), wire) {
		t.Error("wire name còn sót trong response về client")
	}
	if !strings.Contains(string(respBody), `"name":"Bash"`) {
		t.Error("tên tool client chưa được restore")
	}
}

// titleRequestBody dựng body request title-gen thật của client 2.1.1 (không key
// tools, system = PROMPT_TITLE, user = nội dung cần đặt title).
func titleRequestBody(t *testing.T, userText string) []byte {
	t.Helper()
	body, err := json.Marshal(titlePayload([]any{
		titleSystemMessage(capturedPromptTitle),
		map[string]any{"role": "user", "content": userText},
	}))
	if err != nil {
		t.Fatalf("marshal title body: %v", err)
	}
	return body
}

// firstSSEDataPayload trả payload JSON của dòng "data:" hoàn chỉnh đầu tiên.
func firstSSEDataPayload(t *testing.T, body []byte) map[string]any {
	t.Helper()
	for _, line := range strings.Split(string(body), "\n") {
		if !strings.HasPrefix(line, "data:") {
			continue
		}
		raw := strings.TrimPrefix(line, "data:")
		raw = strings.TrimPrefix(raw, " ")
		var payload map[string]any
		if err := json.Unmarshal([]byte(raw), &payload); err != nil {
			t.Fatalf("parse SSE data payload %.80q: %v", raw, err)
		}
		return payload
	}
	t.Fatalf("body không có dòng data: nào: %.200q", body)
	return nil
}

func TestPipeline_TitleRequest_Upstream429_Synthesized200(t *testing.T) {
	const userText = "Debugging the failing login flow across all production servers"
	upstreamError := `{"error":{"message":"FreeUsageLimitError"}}`
	upstream, capture := captureUpstream(t, http.StatusTooManyRequests, upstreamError, nil)

	req := httptest.NewRequest(http.MethodPost, "/v1/chat/completions", bytes.NewReader(titleRequestBody(t, userText)))
	req.Header.Set("Content-Type", "application/json")

	resp, respBody := runSidecar(t, upstream, req)

	// Upstream nhận title lane: prompt được inject nhưng KHÔNG rename/append tool
	// (body title thật không có key tools — sidecar cũng không được thêm vào).
	payload := decodeObjectBody(t, capture.body)
	if _, hasTools := payload["tools"]; hasTools {
		t.Error("title lane không được append bộ tool official")
	}
	if content, _ := firstMessage(t, payload)["content"].(string); !strings.Contains(content, opencodePromptMarker) {
		t.Error("title lane phải inject official prompt")
	}

	if resp.StatusCode != http.StatusOK {
		t.Fatalf("status = %d, want 200 (title synthesize)", resp.StatusCode)
	}
	if got := resp.Header.Get("X-Sidecar-Title"); got != "synthesized" {
		t.Errorf("X-Sidecar-Title = %q, want synthesized", got)
	}
	if got := resp.Header.Get("X-Sidecar-Shaped"); got != "none" {
		t.Errorf("X-Sidecar-Shaped = %q, want none", got)
	}
	if got := resp.Header.Get("Content-Type"); got != "text/event-stream" {
		t.Errorf("Content-Type = %q, want text/event-stream", got)
	}
	if !strings.HasSuffix(string(respBody), "data: [DONE]\n\n") {
		t.Errorf("body synthesize phải kết thúc bằng data: [DONE] + dòng trống: %.200q", respBody)
	}
	if bytes.Contains(respBody, []byte("FreeUsageLimitError")) {
		t.Error("body 429 của upstream không được lọt vào response synthesize")
	}

	chunk := firstSSEDataPayload(t, respBody)
	if object, _ := chunk["object"].(string); object != "chat.completion.chunk" {
		t.Errorf("chunk object = %q, want chat.completion.chunk", object)
	}
	choices, _ := chunk["choices"].([]any)
	if len(choices) != 1 {
		t.Fatalf("chunk choices = %d, want 1", len(choices))
	}
	choice, _ := choices[0].(map[string]any)
	delta, _ := choice["delta"].(map[string]any)
	content, _ := delta["content"].(string)
	if content == "" {
		t.Fatal("chunk thiếu title trong choices[0].delta.content")
	}
	if content != "Debugging the failing login flow across" {
		t.Errorf("title tổng hợp = %q, want %q", content, "Debugging the failing login flow across")
	}
}

func TestPipeline_ErrorPassthrough_KeyedAndFree(t *testing.T) {
	gateError := []byte(`{"error":{"message":"Your request looks like abusive traffic"}}`)

	t.Run("keyed", func(t *testing.T) {
		keyedBody := []byte("{\"model\":\"m\",\"messages\":[],\"stream\":false}")
		upstream, _ := captureUpstream(t, http.StatusForbidden, string(gateError), nil)

		req := httptest.NewRequest(http.MethodPost, "/v1/chat/completions", bytes.NewReader(keyedBody))
		req.Header.Set("Content-Type", "application/json")
		req.Header.Set("Authorization", "Bearer sk-real")

		resp, respBody := runSidecar(t, upstream, req)

		if resp.StatusCode != http.StatusForbidden {
			t.Errorf("status = %d, want 403", resp.StatusCode)
		}
		if !bytes.Equal(respBody, gateError) {
			t.Errorf("error body bị sửa:\n got %.200q\nwant %.200q", respBody, gateError)
		}
		if got := resp.Header.Get("X-Sidecar-Shaped"); got != "none" {
			t.Errorf("X-Sidecar-Shaped = %q, want none", got)
		}
	})

	t.Run("free", func(t *testing.T) {
		upstream, _ := captureUpstream(t, http.StatusForbidden, string(gateError), nil)

		req := httptest.NewRequest(http.MethodPost, "/v1/chat/completions", bytes.NewReader(clientChatBody(t)))
		req.Header.Set("Content-Type", "application/json")

		resp, respBody := runSidecar(t, upstream, req)

		if resp.StatusCode != http.StatusForbidden {
			t.Errorf("status = %d, want 403", resp.StatusCode)
		}
		if !bytes.Equal(respBody, gateError) {
			t.Errorf("error body bị sửa:\n got %.200q\nwant %.200q", respBody, gateError)
		}
	})
}

func TestPipeline_TitleRequest_TransportError_Synthesized200(t *testing.T) {
	// T7-R1: lỗi transport với title request cũng phải được sidecar trả title
	// tổng hợp — upstream abort kết nối, client.Do trả lỗi transport.
	upstream := httptest.NewServer(http.HandlerFunc(func(http.ResponseWriter, *http.Request) {
		panic(http.ErrAbortHandler)
	}))
	t.Cleanup(upstream.Close)

	req := httptest.NewRequest(http.MethodPost, "/v1/chat/completions", bytes.NewReader(titleRequestBody(t, "Fix the login bug in auth.ts")))
	req.Header.Set("Content-Type", "application/json")

	resp, respBody := runSidecar(t, upstream, req)

	if resp.StatusCode != http.StatusOK {
		t.Fatalf("status = %d, want 200 (transport error → synthesize)", resp.StatusCode)
	}
	if got := resp.Header.Get("X-Sidecar-Title"); got != "synthesized" {
		t.Errorf("X-Sidecar-Title = %q, want synthesized", got)
	}
	if !strings.HasSuffix(string(respBody), "data: [DONE]\n\n") {
		t.Errorf("body synthesize phải kết thúc bằng data: [DONE] + dòng trống: %.200q", respBody)
	}
	chunk := firstSSEDataPayload(t, respBody)
	choices, _ := chunk["choices"].([]any)
	if len(choices) != 1 {
		t.Fatalf("chunk choices = %d, want 1", len(choices))
	}
	delta, _ := choices[0].(map[string]any)["delta"].(map[string]any)
	if content, _ := delta["content"].(string); content != "Fix the login bug in auth.ts" {
		t.Errorf("title tổng hợp = %q, want %q", content, "Fix the login bug in auth.ts")
	}
}

func TestPipeline_FreeLane_MalformedBody_ForwardedUnshaped(t *testing.T) {
	// Quyết định composition của dispatch T7: body free lane không parse được
	// hoặc không phải JSON object thì KHÔNG bịa shaping — forward nguyên trạng
	// để upstream trả lỗi rõ ràng của nó, nhãn shaped giữ "none".
	cases := []struct {
		name string
		body []byte
	}{
		{"invalid json", []byte("{not json")},
		{"non-object json", []byte(`[{"role":"user"}]`)},
		{"null json", []byte("null")},
	}
	upstreamError := []byte(`{"error":{"message":"invalid request"}}`)

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			upstream, capture := captureUpstream(t, http.StatusBadRequest, string(upstreamError), nil)

			req := httptest.NewRequest(http.MethodPost, "/v1/chat/completions", bytes.NewReader(tc.body))
			req.Header.Set("Content-Type", "application/json")

			resp, respBody := runSidecar(t, upstream, req)

			if !bytes.Equal(capture.body, tc.body) {
				t.Errorf("body phải đi upstream nguyên trạng:\n got %.120q\nwant %.120q", capture.body, tc.body)
			}
			if got := canonicalHeaderValue(t, capture.header, "Authorization"); got != "Bearer public" {
				t.Errorf("upstream Authorization = %q, want Bearer public (vẫn free lane)", got)
			}
			if got := resp.Header.Get("X-Sidecar-Shaped"); got != "none" {
				t.Errorf("X-Sidecar-Shaped = %q, want none", got)
			}
			if resp.StatusCode != http.StatusBadRequest {
				t.Errorf("status = %d, want 400", resp.StatusCode)
			}
			if !bytes.Equal(respBody, upstreamError) {
				t.Errorf("error body không được sửa:\n got %.120q\nwant %.120q", respBody, upstreamError)
			}
		})
	}
}

func TestPipeline_StaleChildSession_FallsBackToRoot(t *testing.T) {
	// T7-R3: "well-formed" = plausible official session (shape + cửa sổ 31 ngày),
	// không chỉ regex shape — id đúng shape nhưng stale bị gate 429 y hệt quota
	// hết, nên sidecar không được forward nó nguyên trạng.
	staleChild := buildOfficialSession(t, time.Now().AddDate(0, 0, -200).UnixMilli())
	upstream, capture := captureUpstream(t, http.StatusOK, "data: [DONE]\n\n", nil)

	req := httptest.NewRequest(http.MethodPost, "/v1/chat/completions", bytes.NewReader(clientChatBody(t)))
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("x-opencode-session-id", staleChild)

	_, _ = runSidecar(t, upstream, req)

	got := canonicalHeaderValue(t, capture.header, "x-opencode-session-id")
	if got == staleChild {
		t.Error("child session stale (ngoài cửa sổ plausible) không được forward nguyên trạng")
	}
	root := canonicalHeaderValue(t, capture.header, "x-opencode-session")
	if got != root {
		t.Errorf("child fallback = %q, want root %q", got, root)
	}
	if !isPlausibleOfficialSession(got, time.Now()) {
		t.Errorf("child fallback = %q, không phải session plausible", got)
	}
}

func TestPipeline_TitleRequest_Upstream403_PassedThrough(t *testing.T) {
	// T7-R1: chỉ 429/lỗi transport mới synthesize; 403 (shape-gate/abuse) phải
	// surface nguyên trạng để C# phân loại — che nó đi là mù diagnostics.
	gateError := []byte(`{"error":{"message":"This model can only be used from within OpenCode"}}`)
	upstream, _ := captureUpstream(t, http.StatusForbidden, string(gateError), nil)

	req := httptest.NewRequest(http.MethodPost, "/v1/chat/completions", bytes.NewReader(titleRequestBody(t, "Rename the deploy script")))
	req.Header.Set("Content-Type", "application/json")

	resp, respBody := runSidecar(t, upstream, req)

	if resp.StatusCode != http.StatusForbidden {
		t.Errorf("status = %d, want 403 (không mask gate failure)", resp.StatusCode)
	}
	if !bytes.Equal(respBody, gateError) {
		t.Errorf("403 body phải pass-through nguyên byte:\n got %.240q\nwant %.240q", respBody, gateError)
	}
	if got := resp.Header.Get("X-Sidecar-Title"); got != "" {
		t.Errorf("X-Sidecar-Title = %q, want rỗng khi không synthesize", got)
	}
	if got := resp.Header.Get("X-Sidecar-Shaped"); got != "none" {
		t.Errorf("X-Sidecar-Shaped = %q, want none", got)
	}
}
