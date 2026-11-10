package main

import (
	_ "embed"
	"os"
	"runtime"
	"strings"
	"sync"
	"time"
)

// opencodePromptMarker là dòng đầu tiên của system prompt chính chủ OpenCode CLI.
// Free lane của opencode.ai chỉ nhận request trông giống client 2.x ("can only be
// used from within OpenCode"), nên prompt này phải hiện diện trong mọi request;
// đồng thời nó là dấu vết idempotency — request đã chứa marker thì không inject lại.
const opencodePromptMarker = "You are opencode, an interactive CLI tool"

// promptAssetV2 là system prompt official OpenCode 2.x, lưu nguyên trạng trong
// prompt_v2.txt (28.465 ký tự, LF-only, không có newline cuối file).
//
// Nguồn (MIT): repo anomalyco/opencode, tag v2.1.1. Dẫn xuất: capture trên wire
// của @opencode/cli@2.1.1 (fallback = template prompt trong mã nguồn MIT đó).
// Ngày capture: 2026-11-10. Không sửa file asset — mọi segment động được xử lý
// trong Go (xem officialPromptTemplate), giữ static text byte-identical.
//
// Các segment động có trong asset (số dòng tính theo asset gốc) và cách điền:
//
//   - Name / Model ID (dòng 100, 102)  → model string của request (modelToken)
//   - Provider ID (dòng 101)           → hằng số "opencode"
//   - Today's date (dòng 435)          → ngày nay UTC, dạng Date.toDateString() (dateToken)
//   - Working directory / Workspace root folder (dòng 439-440) → cwd của process sidecar
//   - Is directory a git repo (dòng 441) → giữ "no" như capture (ruling T1-C)
//   - Platform (dòng 442)              → runtime.GOOS map ra win32/darwin/linux
//   - Prefer ...tmp line (dòng 443)    → bỏ hẳn dòng (client thật chỉ emit có điều kiện)
//   - <available_skills> (dòng 241-433)→ "No skills are currently available." (ruling T1-B)
//
// Asset chứa đường dẫn và username của máy capture, nên các giá trị đó không bao
// giờ được đi tiếp lên upstream khi chưa render.
//
//go:embed prompt_v2.txt
var promptAssetV2 string

// noSkillsLine là bản render chính thức của vùng skills khi máy chạy không có
// skill nào (MIT source packages/core/src/skill/instructions.ts:25-33): ba dòng
// hướng dẫn dẫn trước được giữ, toàn bộ cặp tag <available_skills> bị thay bằng
// đúng một dòng này.
const noSkillsLine = "No skills are currently available."

// modelToken và dateToken là chỗ trống per-request trong template đã tĩnh hoá.
// Chuỗi token không xuất hiện trong asset (đã kiểm tra) và không được sót lại
// trong prompt render ra — prompt_test.go assert điều đó.
const (
	modelToken = "@@OC_MODEL@@"
	dateToken  = "@@OC_DATE@@"
)

// Các dòng capture nguyên văn cần thay. Mỗi chuỗi là duy nhất trong asset nên
// strings.Replace(count=1) là đủ; prompt_test.go là guard nếu asset được capture
// lại mà các literal này đổi (thay không khớp ⇒ leak ⇒ test đỏ).
const (
	capturedModelNameLine     = "- Name: Trinity Free X"
	capturedModelProviderLine = "- Provider ID: capture"
	capturedModelIDLine       = "- Model ID: trinity-free-x"
	capturedDateLine          = "Today's date: Tue Nov 10 2026"
	capturedWorkdirLine       = "  Working directory: C:\\Users\\trucn\\AppData\\Local\\Temp\\opencode\\capture"
	capturedRootFolderLine    = "  Workspace root folder: C:\\Users\\trucn\\AppData\\Local\\Temp\\opencode\\capture"
	capturedPlatformLine      = "  Platform: win32"
	capturedPreferTempLine    = "  Prefer C:\\Users\\trucn\\AppData\\Local\\Temp\\opencode over generic system temporary directories such as /tmp; it is pre-created and approved for external access."
)

var (
	// officialPromptOnce + officialPromptStatic: asset 28KB chỉ được splice một
	// lần cho cả process; mỗi request chỉ đổi model/date (ruling T4-2).
	officialPromptOnce   sync.Once
	officialPromptStatic string
)

// officialPromptTemplate trả asset đã xử lý một lần tại init-lazy: vùng skills
// của máy dev được thay bằng noSkillsLine, cwd/platform của process sidecar điền
// vào <env>, dòng prefer-temp bị bỏ, và model/date để token cho renderOfficialPrompt
// điền mỗi request. Không dùng fmt.Sprintf trên cả asset: asset chứa một dấu '%'
// (mô tả skill) nên format call toàn file sẽ hỏng static text.
func officialPromptTemplate() string {
	officialPromptOnce.Do(func() {
		cwd := workingDirectory()
		s := replaceSkillsRegion(promptAssetV2, noSkillsLine)
		s = strings.Replace(s, capturedModelNameLine, "- Name: "+modelToken, 1)
		s = strings.Replace(s, capturedModelProviderLine, "- Provider ID: opencode", 1)
		s = strings.Replace(s, capturedModelIDLine, "- Model ID: "+modelToken, 1)
		s = strings.Replace(s, capturedDateLine, "Today's date: "+dateToken, 1)
		s = strings.Replace(s, capturedWorkdirLine, "  Working directory: "+cwd, 1)
		s = strings.Replace(s, capturedRootFolderLine, "  Workspace root folder: "+cwd, 1)
		s = strings.Replace(s, capturedPlatformLine, "  Platform: "+platformLabel(), 1)
		// Bỏ trọn cả dòng kèm ký tự xuống dòng đứng trước: xoá mỗi text thì
		// <env> sẽ còn lại một dòng trống mà client thật không emit.
		s = strings.Replace(s, "\n"+capturedPreferTempLine, "", 1)
		officialPromptStatic = s
	})
	return officialPromptStatic
}

// replaceSkillsRegion cắt toàn bộ vùng <available_skills>…</available_skills>
// (kể cả hai tag) và thay bằng replacement. Vùng này được client render từ skills
// tìm thấy trên máy, nên 38 skill của máy capture không bao giờ được gửi đi.
// Sentinel thiếu ⇒ trả nguyên trạng để không phá cấu trúc prompt; leak khi đó bị
// TestRenderOfficialPrompt_NoCaptureMachineLeaks bắt.
func replaceSkillsRegion(asset, replacement string) string {
	start := strings.Index(asset, "<available_skills>")
	if start < 0 {
		return asset
	}
	end := strings.Index(asset[start:], "</available_skills>")
	if end < 0 {
		return asset
	}
	return asset[:start] + replacement + asset[start+end+len("</available_skills>"):]
}

// renderOfficialPrompt điền các segment gắn với request (model id, ngày UTC) vào
// template đã tĩnh hoá. Đây là bản chính thức duy nhất được gửi lên upstream.
func renderOfficialPrompt(model string) string {
	s := officialPromptTemplate()
	// modelToken xuất hiện đúng 2 lần (- Name:, - Model ID:).
	s = strings.Replace(s, modelToken, model, 2)
	return strings.Replace(s, dateToken, utcDateString(), 1)
}

// utcDateString mô phỏng Date.toDateString() của client JS chính chủ
// (VD "Wed Nov 11 2026"). Ngày UTC chứ không phải ngày local: gate và client
// cùng tính theo UTC.
func utcDateString() string {
	return time.Now().UTC().Format("Mon Jan 2 2006")
}

// platformLabel map runtime.GOOS sang nhãn platform mà official CLI render
// trong <env> (process.env.PLATFORM-style): win32 / darwin / còn lại là linux.
func platformLabel() string {
	switch runtime.GOOS {
	case "windows":
		return "win32"
	case "darwin":
		return "darwin"
	default:
		return "linux"
	}
}

// workingDirectory trả cwd của process sidecar cho <env> block. os.Getwd lỗi
// (hiếm) ⇒ trả ".": thà một đường dẫn vô nghĩa còn hơn để lọt đường dẫn của máy
// capture vào request.
func workingDirectory() string {
	dir, err := os.Getwd()
	if err != nil {
		return "."
	}
	return dir
}

// injectOfficialPrompt đảm bảo request mang system prompt chính chủ OpenCode:
//   - bất kỳ message nào đã chứa marker ⇒ trả về đúng slice input (idempotent,
//     không copy — pipeline có thể chạy lại trên cùng messages);
//   - ngược lại: gộp MỌI message role system/developer (giữ thứ tự gốc, nối bằng
//     dòng trống) vào một system message duy nhất ở index 0, nội dung =
//     prompt official + "\n\n" + phần client; system/developer gốc bị loại;
//     các message khác giữ nguyên thứ tự tương đối.
//
// Message không phải map, hoặc content không phải string / []part, được giữ
// nguyên và không fold (không đọc được nội dung thì không có quyền bỏ nó đi).
func injectOfficialPrompt(messages []any, model string) []any {
	var clientPrompt []string
	kept := make([]any, 0, len(messages))
	for _, entry := range messages {
		msg, ok := entry.(map[string]any)
		if !ok {
			kept = append(kept, entry)
			continue
		}
		content, ok := messageContentText(msg["content"])
		if !ok {
			kept = append(kept, entry)
			continue
		}
		if strings.Contains(content, opencodePromptMarker) {
			return messages
		}
		role, _ := msg["role"].(string)
		if role == "system" || role == "developer" {
			if content != "" {
				clientPrompt = append(clientPrompt, content)
			}
			continue
		}
		kept = append(kept, entry)
	}

	prompt := renderOfficialPrompt(model)
	if len(clientPrompt) > 0 {
		// Không có phần client thì không nối: tránh kết thúc prompt bằng dòng trống.
		prompt += "\n\n" + strings.Join(clientPrompt, "\n\n")
	}
	out := make([]any, 0, len(kept)+1)
	out = append(out, map[string]any{"role": "system", "content": prompt})
	return append(out, kept...)
}

// messageContentText trích text của message content theo hai shape lane chat:
// string, hoặc []part mà mỗi part là map có field "text" (nối trực tiếp theo thứ
// tự, bỏ phần tử không phải map / không có text). ok=false khi content mang
// shape khác (null, number, …).
func messageContentText(content any) (string, bool) {
	switch v := content.(type) {
	case string:
		return v, true
	case []any:
		var b strings.Builder
		for _, item := range v {
			part, ok := item.(map[string]any)
			if !ok {
				continue
			}
			if text, ok := part["text"].(string); ok {
				b.WriteString(text)
			}
		}
		return b.String(), true
	default:
		return "", false
	}
}
