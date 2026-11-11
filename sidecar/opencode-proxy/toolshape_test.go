package main

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"runtime"
	"strings"
	"testing"
)

// digest12 tính lại 12 hex đầu của sha256(s) độc lập với implementation —
// test là nguồn sự thật cho wire naming, không được gọi helper của toolshape.go.
func digest12(t *testing.T, s string) string {
	t.Helper()
	sum := sha256.Sum256([]byte(s))
	return hex.EncodeToString(sum[:])[:12]
}

// wireNameOf trả wire name kỳ vọng cho client tool (không có collision).
func wireNameOf(t *testing.T, s string) string {
	t.Helper()
	return "clint_" + digest12(t, s)
}

// expectedOfficialNames là 12 tool official v2.1.1 đúng thứ tự asset
// (ruling T1-A: không có "patch" — plan text nói 13 là lỗi). Liệt kê lại ở
// đây thay vì đọc hằng số của implementation để assertion không tautological.
var expectedOfficialNames = []string{
	"edit", "glob", "grep", "question", "read", "shell",
	"skill", "subagent", "webfetch", "websearch", "write", "execute",
}

func TestNewToolRenames_DeterministicDigestAndCollision(t *testing.T) {
	r := NewToolRenames([]string{"Read"})

	want := wireNameOf(t, "Read")
	if got := r.toWire["Read"]; got != want {
		t.Errorf("toWire[Read] = %q, want %q (clint_ + 12 hex đầu sha256)", got, want)
	}
	if got := r.toClient[want]; got != "Read" {
		t.Errorf("toClient[%q] = %q, want Read", want, got)
	}

	// Collision qua seam wireNameFor: pre-seed used bằng chính candidate đầu —
	// không cần brute-force tìm cặp name thật cùng digest prefix.
	nameA := "Alpha"
	base := wireNameOf(t, "Alpha")
	used := map[string]bool{base: true}
	if got := wireNameFor(nameA, used); got != base+"_2" {
		t.Errorf("wireNameFor collision = %q, want %q", got, base+"_2")
	}
	// Tên đã chọn phải được ghi vào used — lần collision kế tiếp leo lên _3.
	if got := wireNameFor(nameA, used); got != base+"_3" {
		t.Errorf("wireNameFor collision lần 2 = %q, want %q", got, base+"_3")
	}
}

func TestRewriteBody_RenamesToolsAndAllReferences(t *testing.T) {
	r := NewToolRenames([]string{"Bash"})
	wire := wireNameOf(t, "Bash")
	payload := map[string]any{
		"model": "mimo-v2.6-flash-free",
		"tools": []any{
			map[string]any{"type": "function", "function": map[string]any{"name": "Bash", "description": "run commands"}},
		},
		"messages": []any{
			map[string]any{"role": "user", "content": "hello"},
			map[string]any{"role": "assistant", "content": "", "tool_calls": []any{
				map[string]any{"id": "call_1", "type": "function", "function": map[string]any{"name": "Bash", "arguments": "{}"}},
			}},
			map[string]any{"role": "tool", "tool_call_id": "call_1", "content": "ok"},
		},
		"tool_choice": "Bash",
	}

	r.RewriteBody(payload)

	fn := payload["tools"].([]any)[0].(map[string]any)["function"].(map[string]any)
	if fn["name"] != wire {
		t.Errorf("tools[0].function.name = %v, want %q", fn["name"], wire)
	}
	asst := payload["messages"].([]any)[1].(map[string]any)
	call := asst["tool_calls"].([]any)[0].(map[string]any)
	if got := call["function"].(map[string]any)["name"]; got != wire {
		t.Errorf("assistant tool_call name = %v, want %q", got, wire)
	}
	// role:"tool" không mang tên — pairing qua tool_call_id; message phải sống.
	toolMsg := payload["messages"].([]any)[2].(map[string]any)
	if toolMsg["role"] != "tool" || toolMsg["tool_call_id"] != "call_1" || toolMsg["content"] != "ok" {
		t.Errorf("role:tool message phải giữ nguyên, got %v", toolMsg)
	}
	if got := payload["tool_choice"]; got != wire {
		t.Errorf("tool_choice = %v, want %q", got, wire)
	}

	// Không một client name nào được sót trong body re-marshal.
	out, err := json.Marshal(payload)
	if err != nil {
		t.Fatalf("marshal payload sau rewrite: %v", err)
	}
	if strings.Contains(string(out), `"Bash"`) {
		t.Errorf("client name \"Bash\" vẫn còn trong body:\n%s", out)
	}
}

func TestRewriteBody_OrphanReferenceDropped(t *testing.T) {
	r := NewToolRenames([]string{"Read"})
	wire := wireNameOf(t, "Read")
	payload := map[string]any{
		"tools": []any{
			map[string]any{"type": "function", "function": map[string]any{"name": "Read"}},
		},
		"messages": []any{
			map[string]any{"role": "user", "content": "hi"},
			map[string]any{"role": "assistant", "tool_calls": []any{
				map[string]any{"id": "call_ghost", "type": "function", "function": map[string]any{"name": "Ghost"}},
				map[string]any{"id": "call_read", "type": "function", "function": map[string]any{"name": "Read"}},
			}},
			map[string]any{"role": "tool", "tool_call_id": "call_ghost", "content": "ghost result"},
			map[string]any{"role": "tool", "tool_call_id": "call_read", "content": "real result"},
			// Tool message không có call gốc trong request — API reject, cũng phải bỏ.
			map[string]any{"role": "tool", "tool_call_id": "call_none", "content": "orphan result"},
			map[string]any{"role": "user", "content": "next"},
		},
	}

	r.RewriteBody(payload)

	msgs := payload["messages"].([]any)
	// user, assistant (1 call sống), tool(call_read), user → 4 message.
	if len(msgs) != 4 {
		t.Fatalf("số message = %d, want 4 (orphan pair + unpaired tool msg bị bỏ): %v", len(msgs), msgs)
	}
	asst := msgs[1].(map[string]any)
	calls := asst["tool_calls"].([]any)
	if len(calls) != 1 {
		t.Fatalf("số tool_call còn lại = %d, want 1: %v", len(calls), calls)
	}
	if got := calls[0].(map[string]any)["function"].(map[string]any)["name"]; got != wire {
		t.Errorf("tool_call sống phải rename sang wire name, got %v", got)
	}
	keptTool := msgs[2].(map[string]any)
	if keptTool["role"] != "tool" || keptTool["tool_call_id"] != "call_read" || keptTool["content"] != "real result" {
		t.Errorf("message kề (pair hợp lệ) phải giữ nguyên, got %v", keptTool)
	}
	last := msgs[3].(map[string]any)
	if last["role"] != "user" || last["content"] != "next" {
		t.Errorf("user message cuối phải giữ nguyên, got %v", last)
	}
	for _, entry := range msgs {
		msg, ok := entry.(map[string]any)
		if !ok {
			continue
		}
		if id, _ := msg["tool_call_id"].(string); id == "call_ghost" || id == "call_none" {
			t.Errorf("orphan tool message %q vẫn còn trong history", id)
		}
	}
}

func TestRewriteBody_ToolChoiceObjectForm_Rewritten(t *testing.T) {
	r := NewToolRenames([]string{"Bash"})
	wire := wireNameOf(t, "Bash")
	payload := map[string]any{
		"tools": []any{
			map[string]any{"type": "function", "function": map[string]any{"name": "Bash"}},
		},
		"tool_choice": map[string]any{"type": "function", "function": map[string]any{"name": "Bash"}},
	}

	r.RewriteBody(payload)

	tc := payload["tool_choice"].(map[string]any)
	if got := tc["function"].(map[string]any)["name"]; got != wire {
		t.Errorf("tool_choice.function.name = %v, want %q", got, wire)
	}
	if tc["type"] != "function" {
		t.Errorf("tool_choice.type phải giữ nguyên, got %v", tc["type"])
	}
}

func TestAppendOfficial_12NamesStrictFalseShellDescPerPlatform(t *testing.T) {
	r := NewToolRenames([]string{"Bash"})
	wire := wireNameOf(t, "Bash")
	payload := map[string]any{
		"tools": []any{
			map[string]any{"type": "function", "function": map[string]any{"name": "Bash"}},
		},
	}
	r.RewriteBody(payload)
	r.AppendOfficial(payload)

	tools := payload["tools"].([]any)
	if len(tools) != 1+len(expectedOfficialNames) {
		t.Fatalf("số tool = %d, want %d (1 client wire + 12 official)", len(tools), 1+len(expectedOfficialNames))
	}
	var shellDesc string
	for i, entry := range tools {
		fn := entry.(map[string]any)["function"].(map[string]any)
		name, _ := fn["name"].(string)
		if i == 0 {
			if name != wire {
				t.Errorf("tools[0].name = %q, want client wire name %q", name, wire)
			}
			continue
		}
		want := expectedOfficialNames[i-1]
		if name != want {
			t.Errorf("tools[%d].name = %q, want %q (asset order)", i, name, want)
		}
		// strict: false có mặt trong asset — phải giữ nguyên trên wire.
		if strict, ok := fn["strict"].(bool); !ok || strict {
			t.Errorf("tools[%d] (%s).strict = %v, want false", i, name, fn["strict"])
		}
		if name == "shell" {
			shellDesc, _ = fn["description"].(string)
		}
	}

	// Fragment shell render theo platform của host đang chạy (ruling T5-A).
	wantFragment, ok := shellDescFragmentFor(runtime.GOOS)
	if !ok {
		t.Skipf("GOOS %s không có render shell — asset text giữ nguyên", runtime.GOOS)
	}
	if !strings.Contains(shellDesc, wantFragment) {
		t.Errorf("shell description thiếu fragment platform %q (GOOS=%s):\n%s", wantFragment, runtime.GOOS, shellDesc)
	}
}

func TestShellDescFragment_PerPlatform(t *testing.T) {
	cases := []struct {
		goos  string
		want  string
		found bool
	}{
		{"windows", "Commands run on Windows using pwsh.", true},
		{"darwin", "Commands run on macOS using zsh.", true},
		{"linux", "Commands run on Linux using bash.", true},
		// GOOS khác: không render — giữ nguyên text asset.
		{"freebsd", "", false},
	}
	for _, tc := range cases {
		got, ok := shellDescFragmentFor(tc.goos)
		if ok != tc.found || (ok && got != tc.want) {
			t.Errorf("shellDescFragmentFor(%q) = (%q, %v), want (%q, %v)", tc.goos, got, ok, tc.want, tc.found)
		}
	}
}

func TestResolve_WireAliasAndPrefixRecovery(t *testing.T) {
	r := NewToolRenames([]string{"Bash"})
	wire := wireNameOf(t, "Bash")

	if got, ok := r.Resolve(wire); !ok || got != "Bash" {
		t.Errorf("Resolve(%q) = (%q, %v), want (Bash, true)", wire, got, ok)
	}
	// Official alias: client declare "Bash", model gọi tool official "shell".
	if got, ok := r.Resolve("shell"); !ok || got != "Bash" {
		t.Errorf("Resolve(official alias \"shell\") = (%q, %v), want (Bash, true)", got, ok)
	}
	// Wire name dạng clint_+digest12 khôi phục về client name.
	if got, ok := r.Resolve("clint_" + digest12(t, "Bash")); !ok || got != "Bash" {
		t.Errorf("Resolve(clint_+digest) = (%q, %v), want (Bash, true)", got, ok)
	}
	// Prefix recovery best-effort: model echo prefix kèm client name ("clint_Bash").
	if got, ok := r.Resolve("clint_Bash"); !ok || got != "Bash" {
		t.Errorf("Resolve(\"clint_Bash\") = (%q, %v), want (Bash, true)", got, ok)
	}
	// Không biết gì về tên lạ — không đoán.
	if got, ok := r.Resolve("unknown_tool"); ok || got != "" {
		t.Errorf("Resolve(\"unknown_tool\") = (%q, %v), want (\"\", false)", got, ok)
	}
}

func TestResolve_CaseFoldNeverGuessesBetweenTwo(t *testing.T) {
	// Hai declared name fold về cùng key — Resolve phải từ chối đoán.
	r := NewToolRenames([]string{"MyTool", "my_tool"})
	if got, ok := r.Resolve("MYTOOL"); ok {
		t.Errorf("Resolve(\"MYTOOL\") với 2 candidate = (%q, true), want ok=false (không đoán giữa hai)", got)
	}
	// Một candidate duy nhất thì fold vẫn khôi phục được.
	solo := NewToolRenames([]string{"MyTool"})
	if got, ok := solo.Resolve("my_tool"); !ok || got != "MyTool" {
		t.Errorf("Resolve(\"my_tool\") = (%q, %v), want (MyTool, true)", got, ok)
	}
}

func TestRestoreSSE_LineScenarios(t *testing.T) {
	r := NewToolRenames([]string{"Bash"})
	wire := wireNameOf(t, "Bash")

	// data: line đầy đủ mang wire tool name → restore về client name.
	dataLine := `data: {"id":"chatcmpl-1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"` + wire + `","arguments":""}}]}}]}`
	want := strings.Replace(dataLine, `"name":"`+wire+`"`, `"name":"Bash"`, 1)
	if got := r.RestoreSSELine(dataLine); got != want {
		t.Errorf("data: line không restore đúng:\ngot  %s\nwant %s", got, want)
	}

	// Official alias trong stream cũng phải restore (model gọi "shell").
	shellLine := `data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"name":"shell","arguments":""}}]}}]}`
	wantShell := strings.Replace(shellLine, `"name":"shell"`, `"name":"Bash"`, 1)
	if got := r.RestoreSSELine(shellLine); got != wantShell {
		t.Errorf("alias shell không restore:\ngot  %s\nwant %s", got, wantShell)
	}

	// Non-data lines và payload không parse được: byte-identical.
	for _, line := range []string{
		"event: message",
		"",
		": keep-alive",
		"data: [DONE]",
		`data: {"broken`,
	} {
		if got := r.RestoreSSELine(line); got != line {
			t.Errorf("line %q phải giữ nguyên byte-identical, got %q", line, got)
		}
	}

	// JSON hợp lệ nhưng không có tool_calls: byte-identical.
	contentLine := `data: {"choices":[{"index":0,"delta":{"content":"hello"}}]}`
	if got := r.RestoreSSELine(contentLine); got != contentLine {
		t.Errorf("line không có tool_calls phải giữ nguyên, got %q", got)
	}

	// Byte fidelity: arguments chứa text giống pattern nhưng escaped — không được đụng.
	tricky := `data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"name":"` + wire + `","arguments":"{\"file\":{\"name\":\"Bash\"}}"}}]}}]}`
	wantTricky := strings.Replace(tricky, `"name":"`+wire+`"`, `"name":"Bash"`, 1)
	if got := r.RestoreSSELine(tricky); got != wantTricky {
		t.Errorf("arguments escaped bị đụng hoặc name không restore:\ngot  %s\nwant %s", got, wantTricky)
	}
}

func TestToolRenames_NilSafeNoOp(t *testing.T) {
	var r *ToolRenames
	payload := map[string]any{
		"tools":       []any{map[string]any{"function": map[string]any{"name": "Bash"}}},
		"messages":    []any{map[string]any{"role": "user", "content": "hi"}},
		"tool_choice": "Bash",
	}

	r.RewriteBody(payload) // không panic, không đổi gì
	if payload["tool_choice"] != "Bash" {
		t.Errorf("nil RewriteBody phải là no-op, tool_choice = %v", payload["tool_choice"])
	}
	r.AppendOfficial(payload)
	if got := len(payload["tools"].([]any)); got != 1 {
		t.Errorf("nil AppendOfficial phải là no-op, số tool = %d", got)
	}
	if got, ok := r.Resolve("anything"); ok || got != "" {
		t.Errorf("nil Resolve = (%q, %v), want (\"\", false)", got, ok)
	}
	line := `data: {"choices":[{"index":0,"delta":{"tool_calls":[{"function":{"name":"Bash"}}]}}]}`
	if got := r.RestoreSSELine(line); got != line {
		t.Errorf("nil RestoreSSELine phải trả nguyên line, got %q", got)
	}
}
