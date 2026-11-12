//go:build liveprobe

// Live probe free lane (design spec v2 §8): gửi MỘT request thật tới
// opencode.ai/inference qua handleChat — không mở listener, không qua host .NET —
// để verify shape v2 (prompt official + clint_ rename + official tools + body
// contract + identity header) được gate chấp nhận.
//
// Build tag liveprobe giữ instrument này ngoài mọi suite/CI thường (không có tag
// thì file không tồn tại với compiler). Chạy tay DUY NHẤT:
//
//	go test -tags liveprobe -run TestLiveProbe_FreeChat -v -count=1
//
// Kỷ luật budget (§8): 1 lần chạy = 1 request; không retry tự động. Kết quả
// (status, upstream request id, X-Sidecar-Shaped, tối đa 200 ký tự đầu của error
// body — CHỈ body non-2xx) đi vào task report. KHÔNG BAO GIỜ log nội dung hội
// thoại hay completion text — mọi log ở đây chỉ chứa field chẩn đoán được phép.
package main

import (
	"bytes"
	"context"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"sort"
	"strings"
	"testing"
	"time"
)

const (
	// liveProbeModel là model free dùng cho probe. 400 "Model is unavailable" /
	// 410 nghĩa là model đã chết — khi đó đổi hằng này sang model khác trong live
	// set (spec §1, capture 2026-11-10) rồi chạy lại; mỗi lần chạy vẫn tính một
	// request trong budget 3.
	liveProbeModel = "glm-5.3-flash-free"

	// liveProbeTimeout chặn treo vô hạn khi đường mạng đen: context hủy làm
	// handleChat trả 502 "...context deadline exceeded" và test kết thúc sạch.
	liveProbeTimeout = 180 * time.Second

	// liveProbeSnippetCap là số ký tự tối đa của body lỗi được phép lọt vào log
	// (§8) — đủ để report phân loại gate/abuse mà không phơi toàn bộ payload.
	liveProbeSnippetCap = 200
)

// TestLiveProbe_FreeChat dựng request y hệt host .NET gửi vào sidecar (route
// /v1/chat/completions, keyless — không Authorization, direct egress — không
// X-Proxy-Url) rồi cho chạy qua đúng pipeline handleChat. Nhận định:
//   - 200 + ít nhất một dòng SSE data: mang completion content ⇒ shape v2 được
//     gate chấp nhận (PASS);
//   - status ngoài 2xx ⇒ FAIL kèm phân loại và error snippet ≤200 ký tự;
//   - 200 thiếu dòng data ⇒ FAIL kèm metadata không phải nội dung — body 200 có
//     thể chứa completion text nên không bao giờ được quote (§8).
func TestLiveProbe_FreeChat(t *testing.T) {
	// Guard: probe chỉ có nghĩa khi trỏ đúng lane free thật — chặn trước khi gửi
	// request nếu ai đó sửa default (đổi điểm đến là đổi contract của instrument).
	if upstreamBaseURL != "https://opencode.ai/inference" {
		t.Fatalf("live probe yêu cầu upstream mặc định thật, đang là %q — dừng trước khi gửi request nào", upstreamBaseURL)
	}

	body, err := json.Marshal(liveProbeChatBody())
	if err != nil {
		t.Fatalf("marshal probe body: %v", err)
	}

	req := httptest.NewRequest(http.MethodPost, "/v1/chat/completions", bytes.NewReader(body))
	req.Header.Set("Content-Type", "application/json")
	// Keyless lane: KHÔNG Authorization. Direct egress: KHÔNG X-Proxy-Url.
	// Không set identity header — sidecar tự mint session/project như với host thật.

	ctx, cancel := context.WithTimeout(context.Background(), liveProbeTimeout)
	defer cancel()
	req = req.WithContext(ctx)

	recorder := httptest.NewRecorder()
	handleChat(recorder, req)
	resp := recorder.Result()
	defer resp.Body.Close()
	respBody, err := io.ReadAll(resp.Body)
	if err != nil {
		t.Fatalf("đọc response recorder: %v", err)
	}

	// Chỉ các field được phép của §8 lọt ra log.
	t.Logf("live probe: status=%d shaped=%q title=%q body_bytes=%d",
		resp.StatusCode, resp.Header.Get("X-Sidecar-Shaped"), resp.Header.Get("X-Sidecar-Title"), len(respBody))
	for _, name := range liveProbeUpstreamIDHeaders(resp.Header) {
		t.Logf("live probe: upstream header %s: %s", name, strings.Join(resp.Header.Values(name), ", "))
	}

	// Invariant cục bộ: request khai 2 tool nên free lane BẮT BUỘC shaping. Lệch
	// nghĩa là probe không chạy đúng nhánh (hoặc transport fail trước khi set).
	if got := resp.Header.Get("X-Sidecar-Shaped"); got != shapedToolsPrompt {
		t.Errorf("X-Sidecar-Shaped = %q, want %q — probe phải đi qua nhánh rename+append", got, shapedToolsPrompt)
	}

	if resp.StatusCode != http.StatusOK {
		classification := liveProbeClassify(resp.StatusCode, respBody)
		if !liveProbeErrorBodyStatus(resp.StatusCode) {
			// 2xx không phải 200 (201/204...): vẫn FAIL — contract của probe là
			// đúng 200 — nhưng body 2xx có thể chứa completion text nên không
			// được quote bất kỳ phần nào.
			t.Fatalf("live probe: upstream trả %d (2xx khác 200) — %s; body_bytes=%d — body 2xx không log snippet (§8)",
				resp.StatusCode, classification, len(respBody))
		}
		t.Fatalf("live probe: upstream trả %d — %s; error snippet: %q",
			resp.StatusCode, classification, liveProbeErrorSnippet(respBody))
	}

	content := liveProbeCompletionContent(respBody)
	if content == "" {
		count := liveProbeDataLineCount(respBody)
		if count > 0 {
			t.Fatalf("live probe: 200 nhưng %d dòng data không có completion content — instrument cần xem lại", count)
		}
		// Body 200 không có dòng data: nhiều khả năng upstream bỏ qua stream:true
		// và trả JSON thường chứa completion text (choices[].message.content) —
		// KHÔNG quote bất kỳ phần nào của body. Chỉ log metadata không phải nội
		// dung theo §8: never completion text.
		t.Fatalf("live probe: 200 nhưng body không có dòng SSE data nào (content_type=%q body_bytes=%d data_line_count=%d) — body 200 có thể chứa completion text nên không log snippet; instrument cần xem lại",
			resp.Header.Get("Content-Type"), len(respBody), count)
	}
	t.Logf("live probe PASS: 200 + completion content (content_chars=%d)", len([]rune(content)))
}

// liveProbeChatBody trả payload chat production-like mà host gửi vào sidecar:
// một user message, hai client tool đúng shape host dùng trong test pipeline
// (read/bash-style) để nhánh rename clint_ + append official tools chạy thật.
func liveProbeChatBody() map[string]any {
	return map[string]any{
		"model": liveProbeModel,
		"messages": []any{
			map[string]any{"role": "user", "content": "Reply with exactly: OK"},
		},
		"tools": []any{
			map[string]any{"type": "function", "function": map[string]any{
				"name":        "read",
				"description": "Read the contents of a file.",
				"parameters":  map[string]any{"type": "object", "properties": map[string]any{}},
			}},
			map[string]any{"type": "function", "function": map[string]any{
				"name":        "bash",
				"description": "Run a shell command.",
				"parameters":  map[string]any{"type": "object", "properties": map[string]any{}},
			}},
		},
		"stream": true,
	}
}

// liveProbeCompletionContent gom content từ các dòng SSE "data:" (chunk streaming
// choices[].delta.content, hoặc message đầy đủ khi upstream trả non-stream) và
// trả về đã trim. Chỉ dùng để biết content có tồn tại — caller không log nó.
func liveProbeCompletionContent(body []byte) string {
	var out strings.Builder
	for _, line := range strings.Split(string(body), "\n") {
		raw, ok := strings.CutPrefix(line, "data:")
		if !ok {
			continue
		}
		raw = strings.TrimSpace(raw)
		if raw == "" || raw == "[DONE]" {
			continue
		}
		var chunk struct {
			Choices []struct {
				Delta struct {
					Content string `json:"content"`
				} `json:"delta"`
				Message struct {
					Content string `json:"content"`
				} `json:"message"`
			} `json:"choices"`
		}
		if err := json.Unmarshal([]byte(raw), &chunk); err != nil {
			continue
		}
		for _, choice := range chunk.Choices {
			out.WriteString(choice.Delta.Content)
			out.WriteString(choice.Message.Content)
		}
	}
	return strings.TrimSpace(out.String())
}

// liveProbeDataLineCount đếm số dòng data: để phân biệt "stream rỗng" với "body
// không phải SSE" khi không tìm thấy completion content.
func liveProbeDataLineCount(body []byte) int {
	count := 0
	for _, line := range strings.Split(string(body), "\n") {
		if strings.HasPrefix(strings.TrimSpace(line), "data:") {
			count++
		}
	}
	return count
}

// liveProbeClassify gán nhãn outcome theo kỷ luật §8 để report mang đúng ngữ
// nghĩa (hard-stop / inconclusive / cần người quyết). Thuần chẩn đoán — không
// điều khiển thêm request nào.
func liveProbeClassify(status int, body []byte) string {
	lower := strings.ToLower(string(body))
	switch {
	case strings.Contains(lower, "abusive traffic") ||
		strings.Contains(lower, "abuse traffic") ||
		strings.Contains(lower, "abuse of traffic") ||
		(strings.Contains(lower, "free models aren't available") && strings.Contains(lower, "from your current network")):
		return "ip-abuse marker — HARD STOP toàn bộ probe, không request nào thêm"
	case strings.Contains(lower, "within opencode"):
		return "shape-403 (gate coi request không phải client chính chủ) — STOP, chờ quyết định stage-2 TLS (uTLS)"
	case status == http.StatusTooManyRequests:
		return "429 quota/rate-limit — inconclusive, KHÔNG retry"
	case status == http.StatusGone ||
		(status == http.StatusBadRequest && strings.Contains(lower, "model is unavailable")):
		return "model unavailable — inconclusive, có thể đổi model trong live set rồi chạy lại (vẫn tính 1 request)"
	case status == http.StatusBadGateway && strings.HasPrefix(string(body), "upstream:"):
		return "transport error — không kết nối được upstream, inconclusive (request có thể chưa tới gate)"
	default:
		return "chưa phân loại — cần người xem xét (không tự chạy lại)"
	}
}

// liveProbeErrorBodyStatus cho biết body của status này là body lỗi (ngoài 2xx)
// và do đó mới được phép quote snippet. Body 2xx — kể cả 201/204 — có thể chứa
// completion text nên tuyệt đối không được log.
func liveProbeErrorBodyStatus(status int) bool {
	return status < http.StatusOK || status >= http.StatusMultipleChoices
}

// liveProbeErrorSnippet trả tối đa liveProbeSnippetCap ký tự (rune — không cắt
// giữa ký tự UTF-8) của body lỗi; dùng cho report, không bao giờ cho completion.
// Chỉ được gọi khi liveProbeErrorBodyStatus báo đây là body lỗi — body 2xx có
// thể chứa completion text nên không nhánh nào được quote nó (§8).
func liveProbeErrorSnippet(body []byte) string {
	runes := []rune(string(body))
	if len(runes) > liveProbeSnippetCap {
		return string(runes[:liveProbeSnippetCap])
	}
	return string(runes)
}

// liveProbeUpstreamIDHeaders trả tên các header mang request id của upstream
// (x-request-id / request-id / cf-ray / trace-id ...) để report log nguyên
// tên+giá trị. Chỉ lọc theo danh sách này — không dump toàn bộ header response.
func liveProbeUpstreamIDHeaders(header http.Header) []string {
	var names []string
	for name := range header {
		lower := strings.ToLower(name)
		if strings.Contains(lower, "request-id") || lower == "cf-ray" ||
			strings.Contains(lower, "trace-id") || lower == "x-correlation-id" {
			names = append(names, name)
		}
	}
	sort.Strings(names)
	return names
}
