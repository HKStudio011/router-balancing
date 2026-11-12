// Command opencode-proxy là sidecar HTTP giả hình client OpenCode CLI cho lane
// free của opencode.ai (host .NET router gửi request vào; sidecar dựng lại request
// đúng shape client chính chủ rồi stream response về).
//
// Bối cảnh (design spec v2, 2026-11-10): gate opencode.ai chỉ nhận traffic
// "trông như" CLI chính chủ 2.1.1 — system prompt official, bộ tool clint_ +
// official, thứ tự key body, identity session/project hợp lệ. Sidecar tái tạo
// đúng những gì gate fingerprint; stage 1 vẫn dùng crypto/tls Go, mô phỏng
// ClientHello bằng uTLS (stage 2 — spec §5.10) chỉ kích hoạt nếu live probe buộc.
//
// Pipeline (chi tiết trong handleChat):
//   - Lane split theo credential (§5.2): keyless (không Authorization hoặc
//     "Bearer public" bất kể case) → free lane: inject official prompt, rename
//     tool client thành clint_*, append bộ official, ép body contract, gửi
//     "Bearer public" case-sensitive. Keyed (key thật) → body đi upstream
//     byte-identical, không parse/shape, không restore SSE.
//   - Title request (không tools, ≤3 message, mang chữ ký prompt title): chỉ
//     inject prompt; upstream 429 hoặc lỗi transport → sidecar tự trả 200 + title
//     tổng hợp (X-Sidecar-Title: synthesized). Mọi status khác pass-through
//     nguyên body để host C# phân loại gate (§5.9) — không mask 403.
//   - Identity (§5.3/§5.4): sidecar tự set header, không copy identity của client;
//     child session giữ từ client nếu plausible; cấm x-opencode-request.
//   - SSE về host được restore tên tool (free lane, chỉ dòng data: hoàn chỉnh).
//   - 2 header tín hiệu (§5.7): X-Sidecar-Shaped: tools+prompt|none và
//     X-Sidecar-Title: synthesized (chỉ khi sidecar tổng hợp title).
//
// Giao thức với host .NET (không đổi):
//   - Startup in "LISTENING <port>\n" ra stdout sau khi bind xong (port ngẫu nhiên).
//   - GET /health → 200 OK (host dùng để health-check định kỳ).
//   - POST /v1/chat/completions → forward; header X-Proxy-Url (tùy chọn, dạng
//     socks5://user:pass@host:port hoặc http://user:pass@host:port) chỉ định
//     outbound proxy cho request đó — credentials không bao giờ hardcode trong binary.
package main

import (
	"bufio"
	"bytes"
	"context"
	"crypto/tls"
	"encoding/json"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"net/url"
	"strings"
	"time"

	"golang.org/x/net/proxy"
)

// upstreamBaseURL là origin của lane free. Tách thành biến package để test trỏ
// sang httptest; route path giữ nguyên theo spec §5.1.
var upstreamBaseURL = "https://opencode.ai/inference"

// upstreamChatPath là route chat family duy nhất sidecar dùng (§5.1).
const upstreamChatPath = "/openai/v1/chat/completions"

// clientVersion là bản OpenCode CLI mà sidecar giả hình — spec §5.3 pin 2.1.1/cli.
// Capture wire 2026-11-10 của bản standalone thấy "/2.1.1/desktop" +
// x-opencode-client: desktop; nếu live probe trả shape-403, đổi suffix cli→desktop
// là biến đầu tiên cần thử — không đổi trước (ruling capture-fidelity).
const clientVersion = "2.1.1"

// Giá trị header tín hiệu sidecar → host (spec §5.7): free lane đã shaping tools
// + prompt, hay không đụng tools (title lane / lane keyed).
const (
	shapedToolsPrompt = "tools+prompt"
	shapedNone        = "none"
)

func main() {
	// Fail-fast (ruling T7-R4): asset lệch capture phải nổ ở boot, không phải ở
	// request đầu tiên của user. Cả hai asset panic khi thiếu sentinel/parse hỏng
	// (officialPromptTemplate, shapedOfficialToolsJSON) — warm một lần trước Serve
	// để lỗi build asset lộ ngay, thay vì rơi vào handler qua lazy validation.
	officialPromptTemplate()
	shapedOfficialToolsJSON()

	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		log.Fatalf("listen: %v", err)
	}
	port := ln.Addr().(*net.TCPAddr).Port

	mux := http.NewServeMux()
	mux.HandleFunc("/health", func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(http.StatusOK)
	})
	mux.HandleFunc("/v1/chat/completions", handleChat)

	srv := &http.Server{
		Handler:           mux,
		ReadHeaderTimeout: 15 * time.Second,
		// Không set ReadTimeout/WriteTimeout — SSE stream dài, host policy
		// không tự cắt request (AGENTS: timeout vô hạn, client disconnect cắt).
	}

	fmt.Printf("LISTENING %d\n", port)
	if err := srv.Serve(ln); err != nil && err != http.ErrServerClosed {
		log.Fatalf("serve: %v", err)
	}
}

// handleChat là pipeline chính: detect lane theo credential, dựng request
// upstream đúng contract v2, stream response về host.
func handleChat(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}

	proxyURL := r.Header.Get("X-Proxy-Url")
	transport, err := buildTransport(proxyURL)
	if err != nil {
		// 502 để host phân biệt lỗi cấu hình proxy với lỗi upstream thật.
		http.Error(w, fmt.Sprintf("bad proxy url: %v", err), http.StatusBadGateway)
		return
	}

	// Body đọc tối đa 32MB — chat request có attachments nhỏ, vượt là lỗi client.
	body, err := io.ReadAll(io.LimitReader(r.Body, 32<<20))
	if err != nil {
		http.Error(w, "read body: "+err.Error(), http.StatusBadRequest)
		return
	}

	// Lane split theo credential (spec §5.2). Equal-fold chứ không case-sensitive:
	// "Bearer PUBLIC" bị gate chặn nên vẫn tính keyless, sidecar ghi lại đúng giá
	// trị case-sensitive trước khi gửi.
	keyless := isKeylessLane(r.Header.Get("Authorization"))

	// Root session = signal của client (nếu có) hoặc hash body; sessionID tự mint
	// lại khi signal không plausible. Child session: giữ id client gửi nếu
	// "well-formed" — ở đây định nghĩa well-formed = plausible official session
	// (T7-R3), tức regex shape VÀ decoded time còn trong cửa sổ 31 ngày: id đúng
	// shape nhưng stale vẫn bị gate 429 y hệt quota hết, nên chỉ check shape là
	// không đủ để đảm bảo request không bị 429 oan.
	rootSession := sessionID(sessionSignal(r, body))
	childSession := rootSession
	if clientChild := r.Header.Get("x-opencode-session-id"); clientChild != "" &&
		isPlausibleOfficialSession(clientChild, time.Now()) {
		childSession = clientChild
	}

	prepared := prepareUpstreamRequest(body, keyless)

	upReq, err := http.NewRequestWithContext(r.Context(), http.MethodPost, upstreamBaseURL+upstreamChatPath, bytes.NewReader(prepared.body))
	if err != nil {
		http.Error(w, "build request: "+err.Error(), http.StatusInternalServerError)
		return
	}

	// Copy header client TRỪ identity/fingerprint và hop-by-hop — sidecar set lại
	// đúng bộ §5.3 bên dưới. X-Proxy-Url và 2 header tín hiệu không được lọt lên
	// upstream; Accept-Encoding bỏ để Go transport tự decompress: SSE restore cần
	// plaintext, body nén sẽ không đọc được line.
	stripped := map[string]bool{
		"Host": true, "Content-Length": true, "Connection": true, "Transfer-Encoding": true,
		"Upgrade": true, "Te": true, "Trailer": true, "X-Proxy-Url": true,
		"Accept-Encoding": true, "X-Sidecar-Shaped": true, "X-Sidecar-Title": true,
		// Identity/fingerprint — sidecar tự set:
		"User-Agent": true, "X-Opencode-Client": true, "X-Opencode-Session": true,
		"X-Opencode-Session-Id": true, "X-Session-Affinity": true, "X-Session-Id": true,
		"X-Opencode-Project": true, "X-Opencode-Request": true, "Accept": true,
	}
	for name, values := range r.Header {
		canonical := http.CanonicalHeaderKey(name)
		if stripped[canonical] {
			continue
		}
		for _, v := range values {
			upReq.Header.Add(canonical, v)
		}
	}
	upReq.ContentLength = int64(len(prepared.body))

	upReq.Header.Set("User-Agent", "opencode/latest/"+clientVersion+"/cli")
	upReq.Header.Set("Accept", "application/json, text/event-stream")
	upReq.Header.Set("x-opencode-client", "cli")
	upReq.Header.Set("x-opencode-session", rootSession)
	upReq.Header.Set("x-opencode-session-id", childSession)
	upReq.Header.Set("x-session-affinity", rootSession)
	upReq.Header.Set("X-Session-Id", rootSession)
	// x-opencode-project theo §5.4: client gửi 40-hex thì giữ, giá trị khác bị
	// hash, thiếu thì random mỗi request (quota free sổ theo (project, egress)).
	upReq.Header.Set("x-opencode-project", projectID(r.Header.Get("x-opencode-project")))
	if keyless {
		// Anonymous credential — case-sensitive, keyless bị 403.
		upReq.Header.Set("Authorization", "Bearer public")
	}

	client := &http.Client{
		Transport: transport,
		// Không timeout tổng — SSE stream; lỗi network trả về qua Transport.
	}
	resp, err := client.Do(upReq)
	if err != nil {
		// Lỗi transport: title request được trả title tổng hợp (§5.8, T7-R1);
		// còn lại 502 để host coi là proxy-connect failure và attempt proxy khác.
		if prepared.title != nil {
			writeSynthesizedTitle(w, prepared.title)
			return
		}
		http.Error(w, "upstream: "+err.Error(), http.StatusBadGateway)
		return
	}
	defer resp.Body.Close()

	// Title request + 429: synthesize. Chỉ 429 (hoặc lỗi transport ở trên) mới
	// synthesize — mọi status khác pass-through nguyên body để host phân loại
	// shape-403 / IP-abuse-403 / quota-429 (§5.9): mask 403 sẽ mù diagnostics.
	if prepared.title != nil && resp.StatusCode == http.StatusTooManyRequests {
		writeSynthesizedTitle(w, prepared.title)
		return
	}

	for name, values := range resp.Header {
		// Bỏ hop-by-hop + Content-Length: Go server tự lo transfer framing, và
		// Content-Length của upstream sẽ cắt cụt body khi restore đổi độ dài dòng.
		if hopByHopResponseHeaders[http.CanonicalHeaderKey(name)] {
			continue
		}
		for _, v := range values {
			w.Header().Add(name, v)
		}
	}
	w.Header().Set("X-Sidecar-Shaped", prepared.shaped)
	w.WriteHeader(resp.StatusCode)
	// Flush ngay header — host thấy response mở trước byte SSE đầu tiên.
	if flusher, ok := w.(http.Flusher); ok {
		flusher.Flush()
	}

	streamUpstreamBody(w, resp.Body, prepared.renames, keyless)
}

// isKeylessLane báo request thuộc free lane: không có Authorization, hoặc giá trị
// equal-fold (không phân biệt case) với "Bearer public" — spec §5.2.
func isKeylessLane(authorization string) bool {
	return authorization == "" || strings.EqualFold(authorization, "Bearer public")
}

// upstreamRequest là body + metadata của request sẽ gửi upstream: giá trị header
// tín hiệu X-Sidecar-Shaped, payload title (khi cần synthesize), và mapping
// restore tên tool cho SSE.
type upstreamRequest struct {
	body    []byte
	shaped  string
	title   map[string]any
	renames *ToolRenames
}

// prepareUpstreamRequest dựng body upstream theo lane (spec §5.2, §5.5, §5.6,
// §5.8). Keyed lane không parse — byte fidelity tuyệt đối. Free lane:
//   - body không parse được hoặc không phải JSON object → forward nguyên trạng,
//     không bịa shaping (upstream trả lỗi rõ ràng của chính nó);
//   - title request → chỉ inject official prompt;
//   - chat còn lại → inject prompt + rename clint_ + append bộ official + body
//     contract, serialize theo key order chuẩn rồi shape lần cuối.
func prepareUpstreamRequest(body []byte, keyless bool) upstreamRequest {
	if !keyless {
		return upstreamRequest{body: body, shaped: shapedNone}
	}

	var payload map[string]any
	if err := json.Unmarshal(body, &payload); err != nil || payload == nil {
		return upstreamRequest{body: body, shaped: shapedNone}
	}

	titleRequest := isTitleRequest(payload)
	model, _ := payload["model"].(string)
	messages, _ := payload["messages"].([]any)
	payload["messages"] = injectOfficialPrompt(messages, model)

	if titleRequest {
		if serialized := marshalOrdered(payload); serialized != nil {
			return upstreamRequest{body: serialized, shaped: shapedNone, title: payload}
		}
		return upstreamRequest{body: body, shaped: shapedNone, title: payload}
	}

	// Tên client phải đọc TRƯỚC khi RewriteBody đổi tên tại chỗ.
	renames := NewToolRenames(collectClientToolNames(payload))
	renames.RewriteBody(payload)
	renames.AppendOfficial(payload)

	serialized := marshalOrdered(payload)
	if serialized == nil {
		// Chỉ xảy ra với value không phải JSON type (không thể đến từ
		// json.Unmarshal): gửi body gốc thay vì JSON hỏng — không dán nhãn shaping.
		return upstreamRequest{body: body, shaped: shapedNone}
	}
	return upstreamRequest{body: shapeChatBody(serialized), shaped: shapedToolsPrompt, renames: renames}
}

// collectClientToolNames trả tên tool client theo shape chat tools[].function.name,
// giữ thứ tự gốc và bỏ entry không đọc được (không đoán nghĩa dữ liệu lạ).
func collectClientToolNames(payload map[string]any) []string {
	tools, ok := payload["tools"].([]any)
	if !ok {
		return nil
	}
	names := make([]string, 0, len(tools))
	for _, entry := range tools {
		tool, ok := entry.(map[string]any)
		if !ok {
			continue
		}
		fn, ok := tool["function"].(map[string]any)
		if !ok {
			continue
		}
		if name, ok := fn["name"].(string); ok {
			names = append(names, name)
		}
	}
	return names
}

// hopByHopResponseHeaders là header không copy từ upstream về host: Go server tự
// quản transfer framing, còn giữ Content-Length của upstream sẽ cắt cụt body khi
// restore đổi độ dài một dòng SSE (frame chứa tên tool).
var hopByHopResponseHeaders = map[string]bool{
	"Content-Length":    true,
	"Transfer-Encoding": true,
	"Connection":        true,
	"Keep-Alive":        true,
	"Trailer":           true,
	"Upgrade":           true,
	"Proxy-Connection":  true,
}

// streamUpstreamBody chuyển tiếp body upstream về host theo từng dòng: restore
// tên tool trên dòng SSE hoàn chỉnh (free lane), write + flush mỗi dòng, tail
// thiếu newline ghi nguyên trạng, lỗi đọc giữa stream dừng im lặng — client
// disconnect là ca thường gặp, không log ồn.
func streamUpstreamBody(w http.ResponseWriter, body io.Reader, renames *ToolRenames, freeLane bool) {
	flusher, _ := w.(http.Flusher)
	reader := bufio.NewReader(body)
	for {
		line, err := reader.ReadString('\n')
		if len(line) > 0 {
			if err == nil && freeLane {
				// Chỉ dòng hoàn chỉnh (đã đủ '\n') mới restore: frame SSE không
				// kết thúc giữa tên tool, còn tail cụt là stream hỏng — ghi nguyên
				// trạng thay vì đoán. renames nil (title lane) là no-op an toàn.
				line = renames.RestoreSSELine(line)
			}
			if _, writeErr := io.WriteString(w, line); writeErr != nil {
				return
			}
			if flusher != nil {
				flusher.Flush()
			}
		}
		if err != nil {
			return
		}
	}
}

// writeSynthesizedTitle trả title tự tổng hợp thay upstream (spec §5.8): 200
// text/event-stream gồm một chunk OpenAI-compatible mang title từ user message
// đầu tiên, rồi data: [DONE]. Header X-Sidecar-Title để host skip record dataset.
func writeSynthesizedTitle(w http.ResponseWriter, payload map[string]any) {
	model, _ := payload["model"].(string)
	chunk := map[string]any{
		"id":      "chatcmpl-sidecar-title",
		"object":  "chat.completion.chunk",
		"created": time.Now().Unix(),
		"model":   model,
		"choices": []any{
			map[string]any{
				"index": 0,
				"delta": map[string]any{
					"role":    "assistant",
					"content": synthesizeTitle(payload),
				},
				"finish_reason": nil,
			},
		},
	}
	encoded, err := json.Marshal(chunk)
	if err != nil {
		http.Error(w, "marshal title chunk: "+err.Error(), http.StatusInternalServerError)
		return
	}

	w.Header().Set("Content-Type", "text/event-stream")
	w.Header().Set("X-Sidecar-Shaped", shapedNone)
	w.Header().Set("X-Sidecar-Title", "synthesized")
	w.WriteHeader(http.StatusOK)
	fmt.Fprintf(w, "data: %s\n\ndata: [DONE]\n\n", encoded)
	if flusher, ok := w.(http.Flusher); ok {
		flusher.Flush()
	}
}

// buildTransport trả về transport outbound: direct (proxyURL rỗng), SOCKS5
// (x/net/proxy hỗ trợ user:pass), hoặc HTTP proxy. Mỗi request có thể dùng
// proxy khác nhau — host resolve từ pool rồi truyền qua header.
func buildTransport(proxyURL string) (*http.Transport, error) {
	transport := &http.Transport{
		TLSClientConfig:   &tls.Config{MinVersion: tls.VersionTLS12},
		ForceAttemptHTTP2: true,
	}
	if proxyURL == "" {
		return transport, nil
	}
	u, err := url.Parse(proxyURL)
	if err != nil {
		return nil, err
	}
	switch u.Scheme {
	case "socks5":
		// FromURL đọc user/pass từ URL; thiếu auth → proxy.Direct làm base.
		var auth *proxy.Auth
		if u.User != nil {
			password, _ := u.User.Password()
			auth = &proxy.Auth{User: u.User.Username(), Password: password}
		}
		dialer, err := proxy.SOCKS5("tcp", u.Host, auth, proxy.Direct)
		if err != nil {
			return nil, err
		}
		transport.DialContext = func(ctx context.Context, network, addr string) (net.Conn, error) {
			return dialer.Dial(network, addr)
		}
	case "http", "https":
		transport.Proxy = http.ProxyURL(u)
	default:
		return nil, fmt.Errorf("unsupported proxy scheme %q", u.Scheme)
	}
	return transport, nil
}
