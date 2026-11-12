package main

import (
	"os"
	"reflect"
	"regexp"
	"runtime"
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

// Bảng GOOS→label chính thức của official CLI; branch default gom mọi GOOS còn
// lại (plan9, js, aix, …) về linux. Không có test này thì cả map không được kiểm
// và render chỉ cần giữ nguyên dòng "  Platform: win32" của asset là vẫn pass.
func TestPlatformLabelFor(t *testing.T) {
	cases := []struct {
		goos string
		want string
	}{
		{"windows", "win32"},
		{"darwin", "darwin"},
		{"linux", "linux"},
		{"plan9", "linux"},
	}
	for _, c := range cases {
		if got := platformLabelFor(c.goos); got != c.want {
			t.Errorf("platformLabelFor(%q) = %q, want %q", c.goos, got, c.want)
		}
	}
	if platformLabelFor(runtime.GOOS) == "" {
		t.Error("platformLabelFor(runtime.GOOS) rỗng")
	}
}

// Guard chống fail-im lặng ở phía template: template đã build phải không còn bất kỳ
// sentinel capture nào (asset capture lại ⇒ replaceExact panic, code bỏ sót replace
// ⇒ test này đỏ), và phải còn đủ token để render điền.
func TestOfficialPromptTemplate_NoUnreplacedSentinels(t *testing.T) {
	tpl := officialPromptTemplate()

	for _, sentinel := range capturedSentinels() {
		if strings.Contains(tpl, sentinel) {
			t.Errorf("template chưa thay sentinel %q", sentinel)
		}
	}
	for _, token := range []string{modelToken, dateToken, platformToken} {
		if !strings.Contains(tpl, token) {
			t.Errorf("template thiếu token %q — segment động sẽ không được điền theo request", token)
		}
	}
}

// Chốt giá trị segment động bằng input cố định: ngày/platform được truyền vào chứ
// không suy từ đồng hồ, nên bug "token không được thay" hoặc "token đặt sai dòng"
// luôn đỏ, kể cả khi máy test chạy đúng vào ngày capture.
func TestFillPromptTemplate_DynamicSegmentsFilled(t *testing.T) {
	const (
		pinnedDate     = "Mon Jan 2 2006"
		pinnedPlatform = "linux"
	)
	out := fillPromptTemplate(officialPromptTemplate(), testModelID, pinnedDate, pinnedPlatform)

	for _, want := range []string{
		"Today's date: " + pinnedDate,
		"  Platform: " + pinnedPlatform,
		"- Name: " + testModelID,
		"- Model ID: " + testModelID,
	} {
		if !strings.Contains(out, want) {
			t.Errorf("thiếu %q — segment động không nằm đúng chỗ hoặc không được điền (got %q)", want, firstLineContaining(out, want))
		}
	}
	for _, bad := range append(capturedSentinels(), modelToken, dateToken, platformToken) {
		if strings.Contains(out, bad) {
			t.Errorf("còn giá trị capture/token %q trong kết quả đã fill", bad)
		}
	}
}

// Carry-forward T4→T7: model id là dữ liệu không tin được — id chứa token không
// được phép (a) chiếm slot điền của dòng date/platform thật, (b) để lại text token
// trong prompt gửi đi. Pin cả hai mặt bằng model chứa nguyên văn cả hai token.
func TestFillPromptTemplate_ModelContainingTokens_NoTokenLeaks(t *testing.T) {
	const (
		pinnedDate     = "Mon Jan 2 2006"
		pinnedPlatform = "linux"
		trickyModel    = "evil@@OC_DATE@@-mid-@@OC_PLATFORM@@-end"
	)
	out := fillPromptTemplate(officialPromptTemplate(), trickyModel, pinnedDate, pinnedPlatform)

	if strings.Contains(out, "@@OC_") {
		t.Errorf("prompt còn token sót lại: %q", firstLineContaining(out, "@@OC_"))
	}
	for _, want := range []string{
		"Today's date: " + pinnedDate,
		"  Platform: " + pinnedPlatform,
	} {
		if !strings.Contains(out, want) {
			t.Errorf("dòng thật không được điền %q (got %q)", want, firstLineContaining(out, strings.TrimPrefix(want, "  ")))
		}
	}
	// Model vẫn phải được nhúng (phần không phải token) — không biến mất khỏi Name.
	if !strings.Contains(out, "- Name: evil") {
		t.Errorf("dòng Name thiếu model id: %q", firstLineContaining(out, "- Name:"))
	}
}

// capturedSentinels là 8 dòng capture nguyên văn phải biến mất khỏi prompt gửi đi.
func capturedSentinels() []string {
	return []string{
		capturedModelNameLine,
		capturedModelProviderLine,
		capturedModelIDLine,
		capturedDateLine,
		capturedWorkdirLine,
		capturedRootFolderLine,
		capturedPlatformLine,
		capturedPreferTempLine,
	}
}

// without trả về slice bỏ phần tử value — dùng để loại khỏi danh sách cấm ở render
// đúng những sentinel mà output đúng của run hiện tại trùng byte với capture.
func without(values []string, value string) []string {
	out := make([]string, 0, len(values))
	for _, v := range values {
		if v != value {
			out = append(out, v)
		}
	}
	return out
}

// Bất kỳ segment nào còn giữ giá trị của máy capture (đường dẫn, username,
// model capture, skills của máy dev) đều là leak quyền riêng tư hoặc sai spec.
func TestRenderOfficialPrompt_NoCaptureMachineLeaks(t *testing.T) {
	dateBefore := utcDateString()
	out := renderOfficialPrompt(testModelID)
	dateAfter := utcDateString() // chạy qua nửa đêm UTC thì before != after

	cwd, err := os.Getwd()
	if err != nil {
		t.Fatalf("os.Getwd: %v", err)
	}
	home, err := os.UserHomeDir()
	if err != nil {
		t.Fatalf("os.UserHomeDir: %v", err)
	}

	// Toàn bộ 8 sentinel capture phải biến mất khỏi kết quả render. Chỉ những dòng
	// mà output ĐÚNG cũng trùng byte với capture mới được bỏ qua, và danh sách đó
	// suy ra từ chính giá trị kỳ vọng của run này (không hard-code máy nào):
	//   - date: hôm nay đúng ngày capture (UTC) thì dòng date đúng == dòng capture;
	//   - platform: GOOS=windows ⇒ label "win32" == dòng capture.
	// Hai case bị loại được chốt bằng assertion giá trị-đúng bên dưới, cộng
	// TestOfficialPromptTemplate_NoUnreplacedSentinels và
	// TestFillPromptTemplate_DynamicSegmentsFilled (input cố định, không phụ thuộc
	// ngày giờ hay GOOS) — nên guard vẫn total.
	dateOf := func(line string) string { return strings.TrimPrefix(line, "Today's date: ") }
	banned := capturedSentinels()
	if dateOf(capturedDateLine) == dateBefore || dateOf(capturedDateLine) == dateAfter {
		banned = without(banned, capturedDateLine)
	}
	if capturedPlatformLine == "  Platform: "+platformLabelFor(runtime.GOOS) {
		banned = without(banned, capturedPlatformLine)
	}
	// Cấm cả prefix đường dẫn capture để bắt mọi biến thể, và "Prefer C:" vì dòng
	// prefer-tmp phải biến mất trọn vẹn (asset dòng 443). Không cấm trần
	// "C:\Users\trucn": chính máy test có home là C:\Users\trucn nên cwd process hợp
	// lệ cũng chia sẻ prefix đó; bất biến "không có đường dẫn máy khác" được kiểm
	// tra riêng bên dưới (controller đã chấp nhận cách thay thế này).
	banned = append(banned,
		`C:\Users\trucn\AppData\Local\Temp\opencode`,
		"Prefer C:",
		"<available_skills>",
		"</available_skills>",
		modelToken,
		dateToken,
		platformToken,
	)
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
		// Platform phải là dẫn xuất từ runtime.GOOS, không phải dòng asset nguyên văn.
		"  Platform: " + platformLabelFor(runtime.GOOS),
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

	// Date: đúng một dòng, và giá trị là ngày UTC đọc quanh thời điểm render
	// (midnight-tolerant). Regex giữ coverage định dạng toDateString() — kể cả khi
	// ngày render trùng ngày capture thì định dạng vẫn bị bắt nếu layout sai.
	dateLines := []string{}
	for _, line := range strings.Split(out, "\n") {
		if strings.Contains(line, "Today's date: ") {
			dateLines = append(dateLines, strings.TrimSpace(line))
		}
	}
	if len(dateLines) != 1 {
		t.Fatalf("phải có đúng một dòng Today's date, thấy %d: %v", len(dateLines), dateLines)
	}
	dateLine := dateLines[0]
	if dateLine != "Today's date: "+dateBefore && dateLine != "Today's date: "+dateAfter {
		t.Errorf("dòng date = %q, phải bằng ngày UTC tại thời điểm render (%q / %q)", dateLine, dateBefore, dateAfter)
	}
	datePattern := regexp.MustCompile(`^Today's date: (?:Sun|Mon|Tue|Wed|Thu|Fri|Sat) (?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec) [ 1]\d \d{4}$`)
	if !datePattern.MatchString(dateLine) {
		t.Errorf("dòng date sai định dạng toDateString (Go layout %q): %q", "Mon Jan 2 2006", dateLine)
	}
}

// Ruling T4-R1: marker chỉ có ý nghĩa idempotency cho chính phần inject. Marker
// nằm ở message do client/để model nói (user paste prompt của OpenCode, model echo
// lại) KHÔNG được phép chặn inject — request đi tiếp mà thiếu prompt chính chủ là
// đúng lỗi gate 403 task family này sửa.
func TestInjectOfficialPrompt_MarkerInUserMessage_StillInjects(t *testing.T) {
	for _, role := range []string{"user", "assistant"} {
		t.Run(role, func(t *testing.T) {
			quoted := "prompt mà tôi thấy trong docs: " + opencodePromptMarker + ", an interactive CLI tool..."
			messages := []any{
				map[string]any{"role": "user", "content": quoted},
				map[string]any{"role": role, "content": "[client-rule-A]"},
			}

			got := injectOfficialPrompt(messages, testModelID)

			content := injectedSystem(t, got)
			if !strings.HasPrefix(content, opencodePromptMarker) {
				t.Errorf("marker ở role %q không được chặn inject, got %.80q", role, content)
			}
			if len(got) != 3 {
				t.Fatalf("số message = %d, want 3 (inject + 2 message gốc giữ nguyên)", len(got))
			}
			for i, want := range []string{quoted, "[client-rule-A]"} {
				msg, ok := got[i+1].(map[string]any)
				if !ok {
					t.Fatalf("messages[%d] không phải map: %T", i+1, got[i+1])
				}
				if msg["content"] != want {
					t.Errorf("messages[%d].content = %v, want giữ nguyên %q", i+1, msg["content"], want)
				}
			}
			if !strings.Contains(got[1].(map[string]any)["content"].(string), opencodePromptMarker) {
				t.Error("marker phải vẫn nằm nguyên trong message user gốc, không bị xoá hay di chuyển")
			}
		})
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
