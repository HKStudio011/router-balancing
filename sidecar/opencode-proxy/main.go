// Command opencode-proxy là sidecar HTTP reverse-proxy cho OpenCode free tier.
//
// Gate của opencode.ai (probed 2026-10-14/15, verified bằng jasonxu reference):
//   - Chặn theo TLS fingerprint (JA3/JA4): Schannel/OpenSSL → 403 FreeTierError,
//     Go crypto/tls → 200.
//   - Free tier chỉ chạy trên inference backend:
//     https://opencode.ai/inference/v1/chat/completions (/zen trả 403 keyless).
//   - Validate identity shape (contract 2026-10-14):
//     + UA dạng "opencode/<channel>/<version>/<name>" — dạng cũ bị 403 chủ đích.
//     + Session "ses_" + 12 hex descending (counter window ffc-fff, ends ffe)
//       + 14 base62 — sai encoding dù đúng shape vẫn 403.
//     + Không gửi x-opencode-request (legacy, bị reject).
//     + x-opencode-project phải 40-hex (prj_* 403; "global" từng pass nhưng
//       official client gửi 40-hex — theo cho chắc).
//     + Authorization "Bearer public" bắt buộc, case-sensitive.
//   - Body phải mang genuine OpenCode tool definitions — stub/empty tools bị
//     reject (ensureAnonymousTools thay stub, append toolset còn thiếu).
//
// Do đó sidecar không tin fingerprint/body từ host — tự canonicalize identity
// và normalize body trước khi forward, stream SSE về nguyên trạng.
//
// Giao thức với host .NET:
//   - Startup in "LISTENING <port>\n" ra stdout sau khi bind xong (port ngẫu nhiên).
//   - GET /health → 200 OK (host dùng để health-check định kỳ).
//   - POST /v1/chat/completions → forward; header X-Proxy-Url (tùy chọn, dạng
//     socks5://user:pass@host:port hoặc http://user:pass@host:port) chỉ định
//     outbound proxy cho request đó — credentials không bao giờ hardcode trong binary.
package main

import (
	"bytes"
	"context"
	"crypto/sha256"
	"crypto/tls"
	_ "embed"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"log"
	"math/big"
	"net"
	"net/http"
	"net/url"
	"regexp"
	"time"

	"golang.org/x/net/proxy"
)

//go:embed anonymous-tools.json
var anonymousToolsJSON []byte

const upstreamURL = "https://opencode.ai/inference/v1/chat/completions"

// clientVersion là bản OpenCode sidecar giả làm. Gate không validate version
// cụ thể — chỉ cần shape đúng; bump khi gate bắt đầu reject bản cũ.
const clientVersion = "2.0.29"

func main() {
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

// handleChat forward request sang opencode.ai với TLS stack Go. Identity và
// body được sidecar canonicalize/normalize — không copy nguyên từ client.
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
	body = normalizeBody(body)

	upReq, err := http.NewRequestWithContext(r.Context(), http.MethodPost, upstreamURL, bytes.NewReader(body))
	if err != nil {
		http.Error(w, "build request: "+err.Error(), http.StatusInternalServerError)
		return
	}

	// Copy header client TRỪ các header identity/fingerprint — sidecar set lại
	// đúng contract 2026-10-14 bên dưới. X-Proxy-Url không được lọt lên upstream.
	stripped := map[string]bool{
		"Host": true, "Content-Length": true, "Connection": true, "Transfer-Encoding": true,
		"Upgrade": true, "Te": true, "Trailer": true, "X-Proxy-Url": true,
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
	upReq.ContentLength = int64(len(body))

	session := canonicalSessionID(sessionSignal(r, body))
	upReq.Header.Set("User-Agent", "opencode/latest/"+clientVersion+"/cli")
	upReq.Header.Set("Accept", "application/json, text/event-stream")
	upReq.Header.Set("x-opencode-client", "cli")
	upReq.Header.Set("x-opencode-session", session)
	upReq.Header.Set("x-opencode-session-id", session)
	upReq.Header.Set("x-session-affinity", session)
	upReq.Header.Set("X-Session-Id", session)
	upReq.Header.Set("x-opencode-project", clientProjectID())
	if upReq.Header.Get("Authorization") == "" {
		// Anonymous credential — case-sensitive, keyless bị 403.
		upReq.Header.Set("Authorization", "Bearer public")
	}

	client := &http.Client{
		Transport: transport,
		// Không timeout tổng — SSE stream; lỗi network trả về qua Transport.
	}
	resp, err := client.Do(upReq)
	if err != nil {
		// 502 để host coi là proxy-connect failure và attempt proxy khác.
		http.Error(w, "upstream: "+err.Error(), http.StatusBadGateway)
		return
	}
	defer resp.Body.Close()

	for name, values := range resp.Header {
		for _, v := range values {
			w.Header().Add(name, v)
		}
	}
	w.WriteHeader(resp.StatusCode)
	// Flush từng chunk — bắt buộc cho SSE; không buffer cả response.
	if flusher, ok := w.(http.Flusher); ok {
		flusher.Flush()
	}
	if _, err := io.Copy(w, resp.Body); err != nil {
		// Client đã disconnect giữa stream — bình thường, không log ồn.
		return
	}
}

// clientProjectID render project header theo official client: 40-hex lowercase.
// Giá trị không được upstream check (chỉ shape) — hash cố định làm correlation.
func clientProjectID() string {
	sum := sha256.Sum256([]byte("opencode-project\x00router-balancing"))
	return hex.EncodeToString(sum[:20])
}

// normalizeBody buộc body theo contract anonymous lane: stream=true,
// stream_options.include_usage=true, và tools mang genuine OpenCode definitions
// (stub/empty bị free tier reject). JSON hỏng giữ nguyên để upstream trả lỗi
// rõ ràng cho client.
func normalizeBody(body []byte) []byte {
	var payload map[string]any
	if err := json.Unmarshal(body, &payload); err != nil {
		return body
	}
	payload["stream"] = true
	options, ok := payload["stream_options"].(map[string]any)
	if !ok {
		options = map[string]any{}
	}
	options["include_usage"] = true
	payload["stream_options"] = options
	payload["tools"] = json.RawMessage(anonymousToolsJSON)
	encoded, err := json.Marshal(payload)
	if err != nil {
		return body
	}
	return encoded
}

// canonicalSessionPattern matches OpenCode session chính thức:
// "ses_" + 12 lowercase hex + 14 Base62.
var canonicalSessionPattern = regexp.MustCompile(`^ses_[0-9a-f]{12}[0-9A-Za-z]{14}$`)

// descendingTimePartPattern matches time part do Identifier.descending() mint:
// ~(ts_ms<<12|counter) trong 48-bit — low 12 bits = 0xFFF-counter, gate chấp
// nhận counter 1-63 (0xFC0-0xFFF) và 403 phần còn lại.
var descendingTimePartPattern = regexp.MustCompile(`^[0-9a-f]{9}f[c-f][0-9a-f]$`)

const base62Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"

// canonicalSessionID trả về signal nguyên trạng nếu đã đúng session chính thức
// (giữ prompt-cache affinity); ngược lại re-encode deterministic cùng layout.
func canonicalSessionID(signal string) string {
	if canonicalSessionPattern.MatchString(signal) && descendingTimePartPattern.MatchString(signal[4:16]) {
		return signal
	}
	sum := sha256.Sum256([]byte("ses\x00" + signal))
	timePart := descendingTimePart(sum[:6])
	randomPart := base62Fixed(new(big.Int).SetBytes(sum[6:16]), 14)
	return "ses_" + timePart + randomPart
}

// descendingTimePart render 6 hash byte theo đúng layout Identifier.descending():
// 36-bit dịch lên cao trong 48-bit word, OR counter 1, bitwise-NOT — low 12 hex
// digits luôn đọc "ffe".
func descendingTimePart(hash []byte) string {
	current := (uint64(hash[0])<<28 | uint64(hash[1])<<20 | uint64(hash[2])<<12 |
		uint64(hash[3])<<4 | uint64(hash[4])>>4) << 12
	current |= 1
	inverted := ^current & 0xFFFFFFFFFFFF
	return hex.EncodeToString([]byte{
		byte(inverted >> 40), byte(inverted >> 32), byte(inverted >> 24),
		byte(inverted >> 16), byte(inverted >> 8), byte(inverted),
	})
}

func base62Fixed(n *big.Int, width int) string {
	base := big.NewInt(62)
	out := make([]byte, width)
	remainder := new(big.Int)
	for i := width - 1; i >= 0; i-- {
		n.DivMod(n, base, remainder)
		out[i] = base62Alphabet[remainder.Int64()]
	}
	return string(out)
}

// sessionSignal lấy tín hiệu session từ client headers (nếu có) — mỗi cuộc hội
// thoại cùng session giữ affinity; thiếu thì derive từ body.
func sessionSignal(r *http.Request, body []byte) string {
	for _, h := range []string{"x-opencode-session", "x-opencode-session-id", "x-session-affinity", "X-Session-Id"} {
		if v := r.Header.Get(h); v != "" {
			return v
		}
	}
	// Fallback: hash nguyên body — cùng request body → cùng session.
	sum := sha256.Sum256(body)
	return hex.EncodeToString(sum[:16])
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
