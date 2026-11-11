package main

import (
	"bytes"
	"crypto/sha256"
	_ "embed"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"runtime"
	"strconv"
	"strings"
	"sync"
)

// wireNamePrefix là prefix bắt buộc của tên tool client trên wire. Chuỗi đúng
// "clint_" theo capture — KHÔNG phải "client_": gate fingerprint theo shape đó,
// đổi một ký tự cũng đủ để request lộ là không chính chủ.
const wireNamePrefix = "clint_"

// officialToolNames là 12 tool official OpenCode v2.1.1 đúng thứ tự asset
// (tools_v2.json): bộ đã sort + "execute" (codemode) append cuối. Ruling T1-A:
// v2 không có "patch" (GPT-gated, không cùng tồn tại với prompt family này) —
// 12 là ground truth; plan text nói 13 là lỗi.
var officialToolNames = []string{
	"edit", "glob", "grep", "question", "read", "shell",
	"skill", "subagent", "webfetch", "websearch", "write", "execute",
}

//go:embed tools_v2.json
var officialToolsAsset []byte

// capturedShellDescFragment là dòng capture trong description của tool "shell"
// (asset capture trên Windows, duy nhất trong tools_v2.json — dòng 215). Client
// thật render "Commands run on ${OS} using ${shell}." theo platform (MIT source
// packages/core/src/tool/plugin/shell.ts) — bên dưới thay đúng fragment này.
const capturedShellDescFragment = "Commands run on Windows using pwsh."

// shellDescFragmentFor trả bản render của fragment shell theo GOOS. Tên shell là
// default best-effort của từng platform (client thật detect shell của user,
// sidecar không) — lệch chỉ ảnh hưởng text description, không ảnh hưởng gate.
func shellDescFragmentFor(goos string) (string, bool) {
	switch goos {
	case "windows":
		return "Commands run on Windows using pwsh.", true
	case "darwin":
		return "Commands run on macOS using zsh.", true
	case "linux":
		return "Commands run on Linux using bash.", true
	default:
		return "", false // GOOS khác: giữ nguyên text asset.
	}
}

var (
	officialToolsOnce sync.Once
	// officialToolsShaped là bytes asset sau khi render fragment shell theo
	// platform — splice một lần cho cả process (giống prompt template), mọi
	// request khác byte-identical với asset gốc.
	officialToolsShaped []byte
)

// shapedOfficialToolsJSON trả asset đã render đúng một lần tại init-lazy.
// Self-check sentinel (như prompt.go): asset là capture of record — parse hỏng,
// sai số lượng, hoặc lệch tên/thứ tự ⇒ panic ngay thay vì âm thầm ship toolset
// lệch spec lên upstream. Thay fragment làm trên raw bytes để giữ nguyên order
// và byte layout của asset (không re-marshal).
func shapedOfficialToolsJSON() []byte {
	officialToolsOnce.Do(func() {
		b := append([]byte(nil), officialToolsAsset...)
		if frag, ok := shellDescFragmentFor(runtime.GOOS); ok && frag != capturedShellDescFragment {
			if !bytes.Contains(b, []byte(capturedShellDescFragment)) {
				panic("opencode-proxy: tools asset không còn fragment shell capture " +
					strconv.Quote(capturedShellDescFragment) + " — asset bị capture lại, phải cập nhật toolshape.go")
			}
			b = bytes.Replace(b, []byte(capturedShellDescFragment), []byte(frag), 1)
		}
		var parsed []any
		if err := json.Unmarshal(b, &parsed); err != nil {
			panic("opencode-proxy: parse tools asset: " + err.Error())
		}
		if len(parsed) != len(officialToolNames) {
			panic(fmt.Sprintf("opencode-proxy: tools asset có %d entry, want %d (ruling T1-A)", len(parsed), len(officialToolNames)))
		}
		for i, entry := range parsed {
			fn, ok := entry.(map[string]any)["function"].(map[string]any)
			if !ok {
				panic(fmt.Sprintf("opencode-proxy: tools asset entry %d không phải function tool", i))
			}
			name, _ := fn["name"].(string)
			if name != officialToolNames[i] {
				panic(fmt.Sprintf("opencode-proxy: tools asset entry %d = %q, want %q (asset lệch capture)", i, name, officialToolNames[i]))
			}
		}
		officialToolsShaped = b
	})
	return officialToolsShaped
}

// ToolRenames giữ hai chiều mapping giữa tên tool client và tên wire
// (clint_+digest). Zero value / nil là no-op: mọi method kiểm tra receiver
// trước khi đọc map — pipeline T7 có thể gọi an toàn trước khi construct.
type ToolRenames struct {
	toWire   map[string]string // client name → wire name
	toClient map[string]string // wire name → client name
	aliases  map[string]string // official tool name → client name (xem buildOfficialAliases)
}

// NewToolRenames xây mapping cho danh sách tên tool client. Mỗi tên → wire name
// "clint_" + 12 hex đầu của sha256(name); collision (cùng digest prefix) tách bằng
// hậu tố _2, _3, … qua wireNameFor. Wire name luôn ≤ 64 ký tự vì base cố định
// 18 ký tự — không truncate (truncate sẽ phá uniqueness). Tên rỗng hoặc trùng
// trong input chỉ mint một wire name duy nhất.
func NewToolRenames(clientNames []string) *ToolRenames {
	r := &ToolRenames{
		toWire:   make(map[string]string, len(clientNames)),
		toClient: make(map[string]string, len(clientNames)),
		aliases:  buildOfficialAliases(clientNames),
	}
	used := make(map[string]bool, len(clientNames))
	for _, name := range clientNames {
		if name == "" || r.toWire[name] != "" {
			continue
		}
		wire := wireNameFor(name, used)
		r.toWire[name] = wire
		r.toClient[wire] = name
	}
	return r
}

// wireNameFor sinh wire name cho `name` và ghi nhận kết quả vào `used`. Candidate
// "clint_"+12hex đã bị chiếm thì thử _2, _3, … tới khi rảnh. Tách thành seam để
// test collision gọi trực tiếp với used map pre-seeded — không cần brute-force
// tìm cặp name thật cùng digest prefix (ruling pre-flight).
func wireNameFor(name string, used map[string]bool) string {
	sum := sha256.Sum256([]byte(name))
	base := wireNamePrefix + hex.EncodeToString(sum[:])[:12]
	if !used[base] {
		used[base] = true
		return base
	}
	for suffix := 2; ; suffix++ {
		candidate := fmt.Sprintf("%s_%d", base, suffix)
		if !used[candidate] {
			used[candidate] = true
			return candidate
		}
	}
}

// officialSynonyms là bảng đồng nghĩa conservative giữa tên tool client phổ biến
// và tên official tương ứng — chỉ các cặp đã verify trên wire. Bảng cố ý nhỏ:
// thêm entry không có bằng chứng wire là mở đường cho Resolve đoán sai.
var officialSynonyms = map[string][]string{
	"shell": {"bash"},
}

// buildOfficialAliases map tên tool official → client name mà nó alias, khi và chỉ
// khi ĐÚNG MỘT declared client name khớp (case-insensitive equality hoặc synonym).
// Hai candidate cùng khớp ⇒ bỏ alias đó — Resolve không bao giờ đoán giữa hai.
func buildOfficialAliases(clientNames []string) map[string]string {
	aliases := map[string]string{}
	for _, official := range officialToolNames {
		var match string
		found := false
		for _, client := range clientNames {
			if !clientMatchesOfficial(client, official) {
				continue
			}
			if found {
				match = "" // hai candidate — huỷ.
				break
			}
			match = client
			found = true
		}
		if found && match != "" {
			aliases[official] = match
		}
	}
	return aliases
}

// clientMatchesOfficial báo client name có phải alias của official name không:
// case-insensitive equality, hoặc nằm trong synonym table của official.
func clientMatchesOfficial(client, official string) bool {
	lc, lo := strings.ToLower(client), strings.ToLower(official)
	if lc == lo {
		return true
	}
	for _, syn := range officialSynonyms[lo] {
		if lc == syn {
			return true
		}
	}
	return false
}

// RewriteBody đổi toàn bộ tên tool client trong payload sang wire name để request
// tự nhất quán: tools[].function.name, assistant tool_calls, tool_choice (cả string
// lẫn object form), và loại bỏ cặp orphan — tool_call tham chiếu tool không được
// declare cùng với role:"tool" message ghép với nó qua tool_call_id. Tên không đọc
// được (không phải string) giữ nguyên — không đoán nghĩa của dữ liệu lạ; re-marshal
// là việc của T7 (marshalOrdered trong body.go).
func (r *ToolRenames) RewriteBody(payload map[string]any) {
	if r == nil || payload == nil {
		return
	}
	if tools, ok := payload["tools"].([]any); ok {
		for _, entry := range tools {
			tool, ok := entry.(map[string]any)
			if !ok {
				continue
			}
			fn, ok := tool["function"].(map[string]any)
			if !ok {
				continue
			}
			name, ok := fn["name"].(string)
			if !ok {
				continue
			}
			if wire, renamed := r.toWire[name]; renamed {
				fn["name"] = wire
			}
		}
	}

	if messages, ok := payload["messages"].([]any); ok {
		payload["messages"] = rewriteToolReferences(r, messages)
	}

	switch choice := payload["tool_choice"].(type) {
	case string:
		if wire, renamed := r.toWire[choice]; renamed {
			payload["tool_choice"] = wire
		}
	case map[string]any:
		if fn, ok := choice["function"].(map[string]any); ok {
			if name, ok := fn["name"].(string); ok {
				if wire, renamed := r.toWire[name]; renamed {
					fn["name"] = wire
				}
			}
		}
	}
}

// rewriteToolReferences đi qua messages hai lượt: (1) assistant tool_calls — rename
// call đã declare, bỏ call orphan và ghi nhận id của chúng; (2) role:"tool" message
// chỉ được giữ khi ghép được với một call còn sống — message pair với call bị bỏ,
// hoặc không có call gốc trong request, đều bị API reject ("must be a response to
// a preceding message with tool_calls") nên cũng phải bỏ. Entry không đọc được
// shape (không phải object) coi như orphan vì không thể verify reference.
func rewriteToolReferences(r *ToolRenames, messages []any) []any {
	orphanIDs := map[string]bool{}
	liveIDs := map[string]bool{}
	for _, entry := range messages {
		msg, ok := entry.(map[string]any)
		if !ok || msg["role"] != "assistant" {
			continue
		}
		calls, ok := msg["tool_calls"].([]any)
		if !ok {
			continue
		}
		kept := make([]any, 0, len(calls))
		for _, callEntry := range calls {
			call, isMap := callEntry.(map[string]any)
			name := ""
			if isMap {
				if fn, ok := call["function"].(map[string]any); ok {
					name, _ = fn["name"].(string)
				}
			}
			wire, renamed := r.toWire[name]
			if !renamed {
				// Orphan: tham chiếu tool không declare trong request này.
				if isMap {
					if id, ok := call["id"].(string); ok && id != "" {
						orphanIDs[id] = true
					}
				}
				continue
			}
			if isMap {
				call["function"].(map[string]any)["name"] = wire
				if id, ok := call["id"].(string); ok && id != "" {
					liveIDs[id] = true
				}
			}
			kept = append(kept, callEntry)
		}
		msg["tool_calls"] = kept
	}

	out := make([]any, 0, len(messages))
	for _, entry := range messages {
		msg, ok := entry.(map[string]any)
		if !ok || msg["role"] != "tool" {
			out = append(out, entry)
			continue
		}
		id, _ := msg["tool_call_id"].(string)
		if id == "" || orphanIDs[id] || !liveIDs[id] {
			continue
		}
		out = append(out, entry)
	}
	return out
}

// AppendOfficial nối 12 tool official (asset order, asset bytes — không reorder,
// không sửa value ngoài fragment shell theo platform) sau client tools. Idempotent
// theo tên: entry nào đã có mặt trong payload thì không append lại — pipeline T7
// chạy lại trên cùng payload mà không nhân đôi toolset.
func (r *ToolRenames) AppendOfficial(payload map[string]any) {
	if r == nil || payload == nil {
		return
	}
	var official []any
	if err := json.Unmarshal(shapedOfficialToolsJSON(), &official); err != nil {
		panic("opencode-proxy: parse tools asset đã shape: " + err.Error())
	}
	tools, _ := payload["tools"].([]any)
	existing := make(map[string]bool, len(tools))
	for _, entry := range tools {
		if m, ok := entry.(map[string]any); ok {
			if fn, ok := m["function"].(map[string]any); ok {
				if name, ok := fn["name"].(string); ok {
					existing[name] = true
				}
			}
		}
	}
	for _, entry := range official {
		fn := entry.(map[string]any)["function"].(map[string]any) // đã verify trong shapedOfficialToolsJSON
		name, _ := fn["name"].(string)
		if existing[name] {
			continue
		}
		tools = append(tools, entry)
	}
	payload["tools"] = tools
}

// Resolve khôi phục tên tool client từ tên mà upstream trả về trong response.
// Bậc ưu tiên (dừng ở bước khớp đầu tiên):
//  1. wire name chính xác (toClient) — case thường;
//  2. official alias — model gọi tool official mà client đã declare tương đương;
//  3. "clint_" prefix bị model echo kèm client name ("clint_Bash") — strip rồi fold-match;
//  4. case/separator fold trên declared names — CHỈ khi đúng một candidate khớp;
//     hai hay nhiều hơn ⇒ không đoán (trả ok=false).
func (r *ToolRenames) Resolve(wire string) (string, bool) {
	if r == nil || wire == "" {
		return "", false
	}
	if client, ok := r.toClient[wire]; ok {
		return client, true
	}
	if client, ok := r.aliases[wire]; ok {
		return client, true
	}
	if rest, ok := strings.CutPrefix(wire, wireNamePrefix); ok && rest != "" {
		if client, found := foldMatch(r.toWire, rest); found {
			return client, true
		}
	}
	if client, found := foldMatch(r.toWire, wire); found {
		return client, true
	}
	return "", false
}

// foldMatch so target với declared client names sau khi chuẩn hoá (lowercase + bỏ
// separator -_./); trả kết quả chỉ khi ĐÚNG MỘT name khớp — nhiều hơn thì từ chối.
func foldMatch(toWire map[string]string, target string) (string, bool) {
	norm := normalizeToolName(target)
	var match string
	found := false
	for name := range toWire {
		if normalizeToolName(name) != norm {
			continue
		}
		if found {
			return "", false // hai candidate — không đoán.
		}
		match = name
		found = true
	}
	return match, found
}

// normalizeToolName chuẩn hoá cho fold: lowercase + bỏ separator thường gặp trong
// tên tool (-, _, ., /) — "My-Tool", "my_tool", "MYTOOL" cùng về "mytool".
func normalizeToolName(s string) string {
	var b strings.Builder
	for _, r := range strings.ToLower(s) {
		switch r {
		case '-', '_', '.', '/':
			continue
		default:
			b.WriteRune(r)
		}
	}
	return b.String()
}

// sseToolCallProbe là struct tối thiểu để verify một SSE data: payload có mang
// delta.tool_calls hay không — chỉ dùng để lấy giá trị name cần restore; KHÔNG bao
// giờ re-marshal cả payload (mất key order/whitespace của upstream).
type sseToolCallProbe struct {
	Choices []struct {
		Delta struct {
			ToolCalls []struct {
				Function struct {
					Name string `json:"name"`
				} `json:"function"`
			} `json:"tool_calls"`
		} `json:"delta"`
	} `json:"choices"`
}

// RestoreSSELine restore tên tool trong MỘT dòng SSE: chỉ các dòng bắt đầu bằng
// "data:" có payload JSON object chứa choices[].delta.tool_calls[].function.name
// mới được đụng — mỗi name đó thay bằng kết quả Resolve; mọi byte khác của line
// giữ nguyên. Non-data lines, payload không parse được, và payload không có tool
// calls trả về verbatim.
//
// Thay thế làm surgical trên raw string theo pattern `"name":"<escaped>"`: trong
// JSON compact hợp lệ, chuỗi byte này chỉ xuất hiện như field thật (mọi quote bên
// trong string value đều bị escape \") nên arguments chứa text giống pattern không
// bao giờ bị đụng. Upstream stream compact JSON — nếu upstream đổi sang có khoảng
// trắng, name sẽ không restore được (degrade an toàn, không corrupt).
func (r *ToolRenames) RestoreSSELine(line string) string {
	if r == nil || !strings.HasPrefix(line, "data:") {
		return line
	}
	payload := line[len("data:"):]
	if strings.HasPrefix(payload, " ") { // SSE: tối đa một khoảng trắng sau dấu hai chấm.
		payload = payload[1:]
	}
	var probe sseToolCallProbe
	if err := json.Unmarshal([]byte(payload), &probe); err != nil {
		return line
	}

	type rename struct{ old, new string }
	seen := map[string]bool{}
	var rewrites []rename
	for _, choice := range probe.Choices {
		for _, tc := range choice.Delta.ToolCalls {
			name := tc.Function.Name
			if name == "" || seen[name] {
				continue
			}
			seen[name] = true
			restored, ok := r.Resolve(name)
			if !ok || restored == name {
				continue
			}
			rewrites = append(rewrites, rename{
				old: `"name":` + jsonQuoted(name),
				new: `"name":` + jsonQuoted(restored),
			})
		}
	}
	if len(rewrites) == 0 {
		return line
	}
	out := line
	for _, rw := range rewrites {
		out = strings.ReplaceAll(out, rw.old, rw.new)
	}
	return out
}

// jsonQuoted render s thành JSON string literal theo escaping chuẩn tối thiểu
// (\" \\ và control chars \u00XX) — khớp dạng upstream emit cho tên tool ASCII
// thường gặp. Tên đặc biệt hơn thế có thể không khớp pattern: degrade an toàn
// (không restore) thay vì corrupt byte.
func jsonQuoted(s string) string {
	var b strings.Builder
	b.WriteByte('"')
	for _, r := range s {
		switch {
		case r == '"' || r == '\\':
			b.WriteByte('\\')
			b.WriteRune(r)
		case r < 0x20:
			fmt.Fprintf(&b, `\u%04x`, r)
		default:
			b.WriteRune(r)
		}
	}
	b.WriteByte('"')
	return b.String()
}
