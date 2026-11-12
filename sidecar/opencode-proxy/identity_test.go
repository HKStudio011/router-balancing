package main

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"regexp"
	"strconv"
	"strings"
	"testing"
	"time"
)

// decodeSessionTime decode 12-hex time part của session id về lại ts field 36-bit:
// ts = (~word & 0xFFFFFFFFFFFF) >> 12 — đúng công thức gate dùng để đọc ngược.
func decodeSessionTime(t *testing.T, id string) uint64 {
	t.Helper()
	if !strings.HasPrefix(id, "ses_") || len(id) != 4+12+14 {
		t.Fatalf("session %q không đúng shape ses_ + 12 hex + 14 base62", id)
	}
	word, err := strconv.ParseUint(id[4:16], 16, 64)
	if err != nil {
		t.Fatalf("parse time part %q: %v", id[4:16], err)
	}
	return (^word & 0xFFFFFFFFFFFF) >> 12
}

// assertPlausibleWindow kiểm tra ts decode nằm trong [midnightUTC(today)−31d, midnightUTC(today)].
// So sánh trên vòng modulo sessionTimeMod vì field thời gian chỉ giữ 36 bit —
// epoch ms hiện tại (~1.8e12) wrap ở 2^36 ms (~795 ngày), gate cũng so trong không gian wrap này.
func assertPlausibleWindow(t *testing.T, id string) {
	t.Helper()
	decoded := decodeSessionTime(t, id)
	midnightMod := uint64(utcMidnightMs(time.Now())) % sessionTimeMod
	age := (midnightMod + sessionTimeMod - decoded) % sessionTimeMod
	if age > plausibilityWindowMs {
		t.Errorf("session %q ngoài cửa sổ plausible: age = %d ms (> %d ms)", id, age, plausibilityWindowMs)
	}
}

// buildOfficialSession ráp một session đúng shape chính chủ từ ts (epoch ms):
// time part = ~(ts<<12|1) & 0xFFFFFFFFFFFF (counter 1) + 14 ký tự base62 cố định.
func buildOfficialSession(t *testing.T, ts int64) string {
	t.Helper()
	word := ^((uint64(ts) << 12) | 1) & 0xFFFFFFFFFFFF
	var b [6]byte
	for i := 5; i >= 0; i-- {
		b[i] = byte(word)
		word >>= 8
	}
	return "ses_" + hex.EncodeToString(b[:]) + "aB3dE6gH9jK2mN"
}

func TestSessionID_DecodedTimestampWithinPlausibleWindow(t *testing.T) {
	id := sessionID("conv-1")
	if !canonicalSessionPattern.MatchString(id) {
		t.Errorf("id mint phải đúng shape chính chủ: %q", id)
	}
	if got := id[13:16]; got != "ffe" {
		t.Errorf("3 hex cuối của time part phải đọc ffe, được %q (time part %q)", got, id[4:16])
	}
	assertPlausibleWindow(t, id)
}

func TestSessionID_StableWithinSameDay_DiffersAcrossSignals(t *testing.T) {
	a := sessionID("conv-stability")
	b := sessionID("conv-stability")
	if a != b {
		t.Errorf("cùng signal trong cùng process phải mint cùng id: %q vs %q", a, b)
	}
	c := sessionID("conv-different")
	if c == a {
		t.Error("signal khác phải mint id khác")
	}
}

func TestSessionID_OfficialPlausibleSession_KeptVerbatim(t *testing.T) {
	ts := utcMidnightMs(time.Now()) - int64((5 * 24 * time.Hour).Milliseconds())
	official := buildOfficialSession(t, ts)
	if got := sessionID(official); got != official {
		t.Errorf("session chính chủ plausible phải giữ nguyên: got %q want %q", got, official)
	}
}

func TestSessionID_ImplausibleOfficialSession_Reminted(t *testing.T) {
	midnight := utcMidnightMs(time.Now())
	dayMs := int64((24 * time.Hour).Milliseconds())
	cases := map[string]int64{
		"future_60d": midnight + 60*dayMs,
		"past_200d":  midnight - 200*dayMs,
	}
	for name, ts := range cases {
		t.Run(name, func(t *testing.T) {
			official := buildOfficialSession(t, ts)
			got := sessionID(official)
			if got == official {
				t.Fatalf("session implausible phải được mint lại: %q", official)
			}
			assertPlausibleWindow(t, got)
		})
	}
}

func TestProjectID_RandomWhenEmpty_40Hex(t *testing.T) {
	pattern := regexp.MustCompile(`^[0-9a-f]{40}$`)
	seen := map[string]bool{}
	for i := 0; i < 10; i++ {
		p := projectID("")
		if !pattern.MatchString(p) {
			t.Fatalf("gọi thứ %d: %q không phải 40-hex lowercase", i, p)
		}
		seen[p] = true
	}
	if len(seen) < 2 {
		t.Error("10 project id random phải có ít nhất 2 giá trị khác nhau")
	}
}

// chatBodyFor ráp body chat thật (JSON như client gửi) từ danh sách message.
func chatBodyFor(t *testing.T, messages ...any) []byte {
	t.Helper()
	body, err := json.Marshal(map[string]any{"model": "mimo-v2.6-flash-free", "messages": messages})
	if err != nil {
		t.Fatalf("marshal body chat: %v", err)
	}
	return body
}

// sessionSignalFor gọi sessionSignal với request không mang identity header —
// đúng ca production (host không gửi x-opencode-*).
func sessionSignalFor(t *testing.T, body []byte) string {
	t.Helper()
	return sessionSignal(httptest.NewRequest(http.MethodPost, "/v1/chat/completions", nil), body)
}

// Trái tim của fix: seed lấy từ HAI message đầu nên history dài thêm (body khác
// bytes) vẫn ra cùng signal — nếu rơi về body hash thì hai body khác nhau sẽ ra
// signal khác và test đỏ.
func TestSessionSignal_ConversationSeedStableAsHistoryGrows(t *testing.T) {
	system := map[string]any{"role": "system", "content": "bạn là trợ lý"}
	firstUser := map[string]any{"role": "user", "content": "câu hỏi đầu tiên"}
	head := chatBodyFor(t, system, firstUser)
	tail := chatBodyFor(t, system, firstUser,
		map[string]any{"role": "assistant", "content": "trả lời đầu"},
		map[string]any{"role": "user", "content": "turn tiếp theo"})

	gotHead := sessionSignalFor(t, head)
	gotTail := sessionSignalFor(t, tail)
	if gotHead != gotTail {
		t.Errorf("seed hội thoại phải ổn định khi history dài thêm: head %q vs tail %q", gotHead, gotTail)
	}
}

func TestSessionSignal_DifferentFirstUserTurn_DifferentSignal(t *testing.T) {
	a := chatBodyFor(t, map[string]any{"role": "system", "content": "bạn là trợ lý"},
		map[string]any{"role": "user", "content": "câu hỏi A"})
	b := chatBodyFor(t, map[string]any{"role": "system", "content": "bạn là trợ lý"},
		map[string]any{"role": "user", "content": "câu hỏi B"})

	if sessionSignalFor(t, a) == sessionSignalFor(t, b) {
		t.Error("first user turn khác nhau phải cho seed khác nhau")
	}
}

func TestSessionSignal_UnparseableBody_FallsBackToBodyHash(t *testing.T) {
	body := []byte("{không phải JSON")
	sum := sha256.Sum256(body)
	want := hex.EncodeToString(sum[:16])

	got := sessionSignalFor(t, body)
	if got != want {
		t.Errorf("body không parse được phải rơi về hash toàn body: got %q want %q", got, want)
	}
	if again := sessionSignalFor(t, body); again != got {
		t.Errorf("cùng bytes phải ra cùng hash: %q vs %q", got, again)
	}
}

// Thứ tự ưu tiên (1) header client thắng seed (2) — không đổi hành vi cũ.
func TestSessionSignal_ClientHeaderWinsOverSeed(t *testing.T) {
	body := chatBodyFor(t, map[string]any{"role": "user", "content": "hi"})
	for _, header := range []string{"x-opencode-session", "x-opencode-session-id", "x-session-affinity", "X-Session-Id"} {
		req := httptest.NewRequest(http.MethodPost, "/v1/chat/completions", nil)
		req.Header.Set(header, "ses_from_client")
		if got := sessionSignal(req, body); got != "ses_from_client" {
			t.Errorf("header %s phải thắng seed: got %q", header, got)
		}
	}
}

// Seed dùng lại messageContentText của prompt.go: content dạng part-list phải cho
// cùng seed với content string đã nối — shape không đổi nghĩa hội thoại.
func TestSessionSignal_PartListContentMatchesConcatenatedText(t *testing.T) {
	parts := chatBodyFor(t,
		map[string]any{"role": "user", "content": []any{
			map[string]any{"type": "text", "text": "phần một "},
			map[string]any{"type": "text", "text": "phần hai"},
		}})
	plain := chatBodyFor(t, map[string]any{"role": "user", "content": "phần một phần hai"})

	if sessionSignalFor(t, parts) != sessionSignalFor(t, plain) {
		t.Error("content part-list phải được đọc như text đã nối, cho cùng seed")
	}
}

func TestProjectID_Client40HexKept_OtherHashed(t *testing.T) {
	fixed := strings.Repeat("a", 40)
	if got := projectID(fixed); got != fixed {
		t.Errorf("40-hex của client phải giữ nguyên: got %q", got)
	}
	pattern := regexp.MustCompile(`^[0-9a-f]{40}$`)
	for _, in := range []string{"prj_abc", "global"} {
		first := projectID(in)
		if !pattern.MatchString(first) {
			t.Errorf("projectID(%q) = %q, muốn 40-hex", in, first)
			continue
		}
		if again := projectID(in); again != first {
			t.Errorf("projectID(%q) phải deterministic: %q vs %q", in, first, again)
		}
	}
}
