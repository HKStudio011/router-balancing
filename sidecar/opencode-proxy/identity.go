package main

import (
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"math/big"
	"net/http"
	"regexp"
	"strconv"
	"time"
)

// bootNonce ngẫu nhiên mỗi lần process khởi động: cùng signal mint ra id khác
// nhau giữa các instance sidecar, nhưng ổn định trong đời process (theo ngày UTC).
var bootNonce [16]byte

func init() {
	if _, err := rand.Read(bootNonce[:]); err != nil {
		// Entropy source hỏng = máy không dùng được — fail-fast hơn là mint id suy yếu.
		panic("opencode-proxy: đọc boot nonce: " + err.Error())
	}
}

var (
	// slowClockSpanMs là offset tối đa slow clock lùi lại trước UTC midnight hôm nay.
	// Giữ < 31d để ts mint luôn nằm trong cửa sổ plausible của gate.
	slowClockSpanMs = int64((30 * 24 * time.Hour).Milliseconds())

	// plausibilityWindowMs là cửa sổ gate chấp nhận cho time field session:
	// session ngoài cửa sổ bị 429 y hệt quota hết (design spec §5.4).
	plausibilityWindowMs = uint64((31 * 24 * time.Hour).Milliseconds())
)

// sessionTimeMod: word thời gian 48-bit chỉ giữ được 36 bit ts (ms) —
// ~(ts<<12|counter) wrap ở 2^36 ms (~795 ngày). Gate so sánh plausible trong
// chính không gian wrap này (capture CLI 2.1.1: time part decode ≈ now_ms mod 2^36),
// nên mọi so sánh cửa sổ phải làm trên vòng modulo này, không phải epoch tuyệt đối.
const sessionTimeMod = uint64(1) << 36

// canonicalSessionPattern matches OpenCode session chính thức:
// "ses_" + 12 lowercase hex + 14 Base62.
var canonicalSessionPattern = regexp.MustCompile(`^ses_[0-9a-f]{12}[0-9A-Za-z]{14}$`)

const base62Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"

// utcMidnightMs trả epoch ms của UTC midnight ngày chứa now.
func utcMidnightMs(now time.Time) int64 {
	utc := now.UTC()
	return time.Date(utc.Year(), utc.Month(), utc.Day(), 0, 0, 0, 0, time.UTC).UnixMilli()
}

// sessionID trả signal nguyên trạng nếu đã là session chính chủ với timestamp
// plausible (giữ prompt-cache affinity); ngược lại mint deterministic theo
// (signal, bootNonce, ngày UTC) — slow clock: ts = UTC midnight hôm nay − offset
// (offset < 30d từ hash), cùng signal trong cùng process + cùng ngày → cùng id.
func sessionID(signal string) string {
	now := time.Now()
	if isPlausibleOfficialSession(signal, now) {
		return signal
	}
	sum := sha256.Sum256(append([]byte("ses\x00"+signal+"\x00"), bootNonce[:]...))
	offset := new(big.Int).SetBytes(sum[0:5]).Uint64() % uint64(slowClockSpanMs)
	ts := utcMidnightMs(now) - int64(offset)
	randomPart := base62Fixed(new(big.Int).SetBytes(sum[5:15]), 14)
	return "ses_" + encodeSessionTimePart(ts) + randomPart
}

// isPlausibleOfficialSession báo s có phải session chính chủ mà time field decode
// ra vẫn trong cửa sổ plausible hay không.
func isPlausibleOfficialSession(s string, now time.Time) bool {
	if !canonicalSessionPattern.MatchString(s) {
		return false
	}
	word, err := strconv.ParseUint(s[4:16], 16, 64)
	if err != nil {
		return false
	}
	return plausibleSessionTime((^word&0xFFFFFFFFFFFF)>>12, now)
}

// encodeSessionTimePart render ts (epoch ms) theo layout Identifier.descending():
// ~(ts<<12|counter) trong 48-bit, counter 1 — low 12 hex digits luôn đọc "ffe".
// Field wrap ở 2^36 ms; Go arithmetic tự cho đúng kết quả wrap như client thật.
func encodeSessionTimePart(ts int64) string {
	word := ^((uint64(ts) << 12) | 1) & 0xFFFFFFFFFFFF
	b := make([]byte, 6)
	for i := 5; i >= 0; i-- {
		b[i] = byte(word)
		word >>= 8
	}
	return hex.EncodeToString(b)
}

// plausibleSessionTime báo decoded (36-bit time field) có nằm trong plausibilityWindowMs
// tính lùi từ now không — so trên vòng modulo sessionTimeMod vì cả hai phía đều wrap.
func plausibleSessionTime(decoded uint64, now time.Time) bool {
	nowMod := uint64(now.UnixMilli()) % sessionTimeMod
	age := (nowMod + sessionTimeMod - decoded) % sessionTimeMod
	return age <= plausibilityWindowMs
}

// base62Fixed render n thành chuỗi base62 đúng width ký tự (lấp đầu bằng 0).
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

// projectHexPattern matches giá trị x-opencode-project hợp lệ của official client:
// 40-hex lowercase (CLI thật gửi git root commit của repo).
var projectHexPattern = regexp.MustCompile(`^[0-9a-f]{40}$`)

// projectID render header x-opencode-project theo contract v2:
//   - "" → random 40-hex mỗi request — quota free sổ theo (project id, egress),
//     id cố định chia sẻ là một bucket "pre-burned" ở exit phổ biến;
//   - 40-hex từ client → giữ nguyên (hành vi giống hệt CLI thật, bucket stable theo repo);
//   - giá trị khác (kể cả prj_*) → hash sha256("opencode-project\0"+value)[:20] ra 40-hex.
func projectID(clientValue string) string {
	switch {
	case clientValue == "":
		var b [20]byte
		if _, err := rand.Read(b[:]); err != nil {
			panic("opencode-proxy: đọc random project id: " + err.Error())
		}
		return hex.EncodeToString(b[:])
	case projectHexPattern.MatchString(clientValue):
		return clientValue
	default:
		sum := sha256.Sum256([]byte("opencode-project\x00" + clientValue))
		return hex.EncodeToString(sum[:20])
	}
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
