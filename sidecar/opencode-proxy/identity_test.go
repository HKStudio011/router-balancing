package main

import (
	"encoding/hex"
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
	word := ^((uint64(ts)<<12) | 1) & 0xFFFFFFFFFFFF
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
	ts := utcMidnightMs(time.Now()) - int64((5*24*time.Hour).Milliseconds())
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
