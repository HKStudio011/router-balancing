package main

import (
	"os"
	"reflect"
	"regexp"
	"strings"
	"testing"
)

// Chữ ký phân biệt phần prompt của client trong test — phải không xuất hiện
// trong asset official để assertion "A đứng trước B" có nghĩa thật.
const (
	clientRuleA = "[client-rule-A]"
	clientRuleB = "[client-rule-B]"
)

const testModelID = "mimo-v2.6-flash-free"

// injectedSystem trả content của message ở index 0, bắt buộc là system message
// string content — fail nhanh nếu inject đặt sai role/vị trí/shape.
func injectedSystem(t *testing.T, messages []any) string {
	t.Helper()
	if len(messages) == 0 {
		t.Fatal("kết quả rỗng, thiếu system message inject ở index 0")
	}
	msg, ok := messages[0].(map[string]any)
	if !ok {
		t.Fatalf("messages[0] không phải map[string]any: %T", messages[0])
	}
	if msg["role"] != "system" {
		t.Fatalf("messages[0].role = %v, want \"system\"", msg["role"])
	}
	content, ok := msg["content"].(string)
	if !ok {
		t.Fatalf("messages[0].content không phải string: %T", msg["content"])
	}
	return content
}

func TestInjectOfficialPrompt_PrependsAndFoldsSystemMessages(t *testing.T) {
	messages := []any{
		map[string]any{"role": "system", "content": clientRuleA},
		map[string]any{"role": "user", "content": "hi"},
		map[string]any{"role": "developer", "content": clientRuleB},
	}

	got := injectOfficialPrompt(messages, testModelID)

	content := injectedSystem(t, got)
	if !strings.HasPrefix(content, opencodePromptMarker) {
		t.Errorf("system message phải bắt đầu bằng marker official, got %.80q", content)
	}
	idxA := strings.Index(content, clientRuleA)
	idxB := strings.Index(content, clientRuleB)
	if idxA < 0 || idxB < 0 {
		t.Fatalf("phải fold đủ cả system lẫn developer vào prompt: A=%d B=%d", idxA, idxB)
	}
	if idxA >= idxB {
		t.Errorf("phải giữ thứ tự gốc khi fold: A=%d B=%d", idxA, idxB)
	}
	if idxB < strings.Index(content, opencodePromptMarker)+len(opencodePromptMarker) {
		t.Error("phần client phải nằm SAU prompt official")
	}
	if len(got) != 2 {
		t.Fatalf("số message = %d, want 2 (system inject + user, system/developer gốc bị loại)\n%v", len(got), got)
	}
	user, ok := got[1].(map[string]any)
	if !ok {
		t.Fatalf("messages[1] không phải map: %T", got[1])
	}
	if user["role"] != "user" || user["content"] != "hi" {
		t.Errorf("user message phải giữ nguyên ở vị trí tương đối, got %v", user)
	}
	for i, entry := range got[1:] {
		if msg, ok := entry.(map[string]any); ok && (msg["role"] == "system" || msg["role"] == "developer") {
			t.Errorf("messages[%d] vẫn còn role %v — mọi system/developer gốc phải bị bỏ", i+1, msg["role"])
		}
	}
}

func TestInjectOfficialPrompt_MarkerPresent_NotInjected(t *testing.T) {
	messages := []any{
		map[string]any{"role": "system", "content": opencodePromptMarker + "\nđã có prompt chính chủ"},
		map[string]any{"role": "user", "content": "hi"},
	}
	before := []any{}
	before = append(before, messages...)

	got := injectOfficialPrompt(messages, testModelID)

	if !reflect.DeepEqual(got, before) {
		t.Errorf("đã có marker thì nội dung phải giữ nguyên, got %v", got)
	}
	// Slice gốc phải được trả về y nguyên (không copy) — T7 gọi pipeline nhiều lần.
	if reflect.ValueOf(got).Pointer() != reflect.ValueOf(messages).Pointer() {
		t.Error("phải trả về đúng slice input, không phải bản copy")
	}
}

func TestInjectOfficialPrompt_NoSystemMessage_StillPrepends(t *testing.T) {
	messages := []any{map[string]any{"role": "user", "content": "hi"}}

	got := injectOfficialPrompt(messages, testModelID)

	content := injectedSystem(t, got)
	if !strings.HasPrefix(content, opencodePromptMarker) {
		t.Errorf("thiếu system vẫn phải inject prompt official, got %.80q", content)
	}
	if strings.HasSuffix(content, "\n\n") {
		t.Error("không có phần client thì không được để lại dòng trống thừa")
	}
	if len(got) != 2 {
		t.Fatalf("số message = %d, want 2", len(got))
	}
	user, ok := got[1].(map[string]any)
	if !ok || user["role"] != "user" || user["content"] != "hi" {
		t.Errorf("user message phải giữ nguyên, got %v", got[1])
	}
}

// OpenCode gửi content dạng part list khi message có nhiều phần; cả marker
// scan lẫn fold phải đọc được shape này (design ruling T4-3).
func TestInjectOfficialPrompt_PartListContentFolded(t *testing.T) {
	userParts := []any{map[string]any{"type": "text", "text": "hi"}}
	messages := []any{
		map[string]any{"role": "system", "content": []any{
			map[string]any{"type": "text", "text": clientRuleA},
			map[string]any{"type": "text", "text": clientRuleB},
		}},
		map[string]any{"role": "user", "content": userParts},
	}

	got := injectOfficialPrompt(messages, testModelID)

	content := injectedSystem(t, got)
	// Các part trong cùng một message nối trực tiếp (không chèn separator).
	if !strings.Contains(content, clientRuleA+clientRuleB) {
		t.Errorf("part list content phải được fold vào prompt, got %.300q", content)
	}
	if len(got) != 2 {
		t.Fatalf("số message = %d, want 2", len(got))
	}
	user, ok := got[1].(map[string]any)
	if !ok {
		t.Fatalf("messages[1] không phải map: %T", got[1])
	}
	if !reflect.DeepEqual(user["content"], userParts) {
		t.Errorf("part list của user phải giữ nguyên, got %v", user["content"])
	}
}

// Bất kỳ segment nào còn giữ giá trị của máy capture (đường dẫn, username,
// model capture, skills của máy dev) đều là leak quyền riêng tư hoặc sai spec.
func TestRenderOfficialPrompt_NoCaptureMachineLeaks(t *testing.T) {
	out := renderOfficialPrompt(testModelID)

	cwd, err := os.Getwd()
	if err != nil {
		t.Fatalf("os.Getwd: %v", err)
	}
	home, err := os.UserHomeDir()
	if err != nil {
		t.Fatalf("os.UserHomeDir: %v", err)
	}

	banned := []string{
		"Trinity Free X",
		"trinity-free-x",
		"Provider ID: capture",
		// Đường dẫn capture (asset dòng 439-440) và đường dẫn trong dòng prefer-tmp
		// (dòng 443) — phải biến mất khỏi prompt. Cấm cả prefix của chúng để bắt
		// mọi biến thể. Không cấm trần "C:\Users\trucn": chính máy test có home là
		// C:\Users\trucn nên cwd process hợp lệ cũng chia sẻ prefix đó; bất biến
		// "không có đường dẫn máy khác" được kiểm tra riêng bên dưới.
		`C:\Users\trucn\AppData\Local\Temp\opencode`,
		"Prefer C:",
		"<available_skills>",
		"</available_skills>",
		modelToken,
		dateToken,
	}
	for _, bad := range banned {
		if strings.Contains(out, bad) {
			t.Errorf("prompt render ra chứa giá trị cấm %q", bad)
		}
	}
	// Bất biến tổng quát: mọi dòng nhắc tới home của máy chạy phải là dòng cwd của
	// chính process này — không được còn đường dẫn của máy capture.
	for _, line := range strings.Split(out, "\n") {
		if strings.Contains(line, home) && !strings.Contains(line, cwd) {
			t.Errorf("dòng chứa đường dẫn không thuộc cwd process (%s): %q", cwd, strings.TrimSpace(line))
		}
	}

	required := []string{
		opencodePromptMarker,
		"- Name: " + testModelID,
		"- Provider ID: opencode",
		"- Model ID: " + testModelID,
		"No skills are currently available.",
		"Platform: ",
	}
	for _, want := range required {
		if !strings.Contains(out, want) {
			t.Errorf("prompt thiếu %q", want)
		}
	}
	// <env> phải mang cwd của process đang chạy (không phải giá trị tĩnh).
	for _, want := range []string{"  Working directory: " + cwd, "  Workspace root folder: " + cwd} {
		if !strings.Contains(out, want) {
			t.Errorf("prompt thiếu %q — <env> phải là cwd của sidecar process", want)
		}
	}
	if !strings.HasPrefix(out, opencodePromptMarker) {
		t.Errorf("prompt phải bắt đầu bằng marker, got %.80q", out)
	}
	// Date theo JS Date.toDateString() (Go layout "Mon Jan 2 2006") — regex để
	// không phụ thuộc thời điểm test chạy qua nửa đêm UTC.
	datePattern := regexp.MustCompile(`Today's date: (?:Sun|Mon|Tue|Wed|Thu|Fri|Sat) (?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec) [ 1]\d \d{4}`)
	if !datePattern.MatchString(out) {
		t.Errorf("thiếu dòng date đúng định dạng toDateString, got %.120q", firstLineContaining(out, "Today's date:"))
	}
}

// firstLineContaining trả dòng đầu chứa substr (msg lỗi gọn hơn in cả 28KB prompt).
func firstLineContaining(s, substr string) string {
	for _, line := range strings.Split(s, "\n") {
		if strings.Contains(line, substr) {
			return line
		}
	}
	return ""
}
