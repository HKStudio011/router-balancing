package main

import (
	_ "embed"
	"os"
	"runtime"
	"strconv"
	"strings"
	"sync"
	"time"
)

// opencodePromptMarker là dòng đầu tiên của system prompt chính chủ OpenCode CLI.
// Free lane của opencode.ai chỉ nhận request trông giống client 2.x ("can only be
// used from within OpenCode"), nên prompt này phải hiện diện trong mọi request;
// đồng thời nó là dấu vết idempotency — system/developer message nào đã chứa marker
// thì không inject lại (marker ở role khác không được chặn inject, xem T4-R1).
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
//   - Platform (dòng 442)              → runtime.GOOS map ra win32/darwin/linux (platformToken)
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

// modelToken, dateToken, platformToken là chỗ trống trong template đã tĩnh hoá:
// model + date gắn với request, platform gắn với process. Chuỗi token không xuất
// hiện trong asset (đã kiểm tra) và không được sót lại trong prompt render ra —
// prompt_test.go assert cả hai chiều (template đủ token, render không còn token).
const (
	modelToken    = "@@OC_MODEL@@"
	dateToken     = "@@OC_DATE@@"
	platformToken = "@@OC_PLATFORM@@"
)

// Các dòng capture nguyên văn cần thay. Mỗi chuỗi là duy nhất trong asset nên
// replaceExact (count=1) là đủ. Đây đồng thời là sentinel: asset được capture lại
// mà thiếu một trong các literal này thì officialPromptTemplate panic tại build
// (không âm thầm gửi tiếp giá trị của máy capture), và TestOfficialPromptTemplate_
// NoUnreplacedSentinels kiểm lại toàn bộ ở phía test.
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
	// lần cho cả process; mỗi request chỉ đổi model/date/platform (ruling T4-2).
	officialPromptOnce   sync.Once
	officialPromptStatic string
)

// officialPromptTemplate trả asset đã xử lý một lần tại init-lazy: vùng skills của
// máy dev thay bằng noSkillsLine, cwd của process điền vào <env>, dòng prefer-temp
// bị bỏ, còn model/date/platform để token cho renderOfficialPrompt điền sau. Không
// dùng fmt.Sprintf trên cả asset: asset chứa một dấu '%' (mô tả skill) nên format
// call toàn file sẽ hỏng static text.
//
// Self-check: mọi sentinel capture phải có thật trong asset. asset được capture lại
// với các dòng khác ⇒ panic ngay lần render đầu tiên, thay vì âm thầm ship ngày /
// platform / đường dẫn của máy capture lên upstream. Asset nhúng là build input nên
// panic này deterministic: binary sai thì không chạy được request nào.
func officialPromptTemplate() string {
	officialPromptOnce.Do(func() {
		cwd := workingDirectory()
		s := replaceSkillsRegion(promptAssetV2, noSkillsLine)
		s = replaceExact(s, capturedModelNameLine, "- Name: "+modelToken)
		s = replaceExact(s, capturedModelProviderLine, "- Provider ID: opencode")
		s = replaceExact(s, capturedModelIDLine, "- Model ID: "+modelToken)
		s = replaceExact(s, capturedDateLine, "Today's date: "+dateToken)
		s = replaceExact(s, capturedWorkdirLine, "  Working directory: "+cwd)
		s = replaceExact(s, capturedRootFolderLine, "  Workspace root folder: "+cwd)
		s = replaceExact(s, capturedPlatformLine, "  Platform: "+platformToken)
		// Bỏ trọn cả dòng kèm ký tự xuống dòng đứng trước: xoá mỗi text thì
		// <env> sẽ còn lại một dòng trống mà client thật không emit.
		s = replaceExact(s, "\n"+capturedPreferTempLine, "")
		officialPromptStatic = s
	})
	return officialPromptStatic
}

// replaceExact thay đúng một occurrence của anchor. Anchor không có trong s nghĩa
// là asset đã đổi so với bản capture: fail loudly, vì mọi fallback mềm đều dẫn tới
// một trong hai hỏng — lộ dữ liệu máy capture (privacy) hoặc prompt lệch spec
// (gate 403).
func replaceExact(s, anchor, replacement string) string {
	if !strings.Contains(s, anchor) {
		panic("opencode-proxy: prompt asset không còn sentinel " + strconv.Quote(anchor) +
			" — asset bị capture lại, phải cập nhật segment động trong prompt.go")
	}
	return strings.Replace(s, anchor, replacement, 1)
}

// replaceSkillsRegion cắt toàn bộ vùng <available_skills>…</available_skills>
// (kể cả hai tag) và thay bằng replacement. Vùng này client render từ skills tìm
// thấy trên máy, nên 38 skill của máy capture không bao giờ được gửi đi.
// Sentinel mất hoặc sai thứ tự ⇒ panic như replaceExact.
func replaceSkillsRegion(asset, replacement string) string {
	start := strings.Index(asset, "<available_skills>")
	end := strings.Index(asset, "</available_skills>")
	if start < 0 || end < start {
		panic("opencode-proxy: prompt asset thiếu vùng <available_skills> hợp lệ")
	}
	return asset[:start] + replacement + asset[end+len("</available_skills>"):]
}

// renderOfficialPrompt điền các segment động (model id của request, ngày UTC,
// platform của process) vào template đã tĩnh hoá. Đây là bản chính thức duy nhất
// được gửi lên upstream; token không được sót lại trong kết quả vì upstream sẽ đọc
// nó như text thật — prompt_test.go assert cả hai chiều.
func renderOfficialPrompt(model string) string {
	return fillPromptTemplate(officialPromptTemplate(), model, utcDateString(), platformLabelFor(runtime.GOOS))
}

// fillPromptTemplate tách phần điền segment động khỏi renderOfficialPrompt để test
// chốt được ngày/platform bằng giá trị cố định: assert "ngày render ra == ngày vừa
// đọc từ đồng hồ" vẫn mù với bug stale-date nếu máy test chạy đúng vào ngày capture,
// còn assert với ngày cố định thì không.
//
// Thứ tự điền: date, platform, rồi model CUỐI CÙNG. Model id là dữ liệu từ request;
// điền model trước thì id chứa token (VD "@@OC_DATE@@") chiếm mất slot một-lần của
// date/platform và để literal token ở dòng thật (carry-forward T4→T7). Đổi thứ tự
// vẫn chưa đủ: model điền cuối có thể mang nguyên văn token vào kết quả, nên quét
// nốt date/platform lần cuối — prompt gửi đi không bao giờ được chứa text token.
func fillPromptTemplate(template, model, date, platform string) string {
	s := strings.Replace(template, dateToken, date, 1)
	s = strings.Replace(s, platformToken, platform, 1)
	// modelToken xuất hiện đúng 2 lần (- Name:, - Model ID:).
	s = strings.Replace(s, modelToken, model, 2)
	s = strings.ReplaceAll(s, dateToken, date)
	return strings.ReplaceAll(s, platformToken, platform)
}

// utcDateString mô phỏng Date.toDateString() của client JS chính chủ
// (VD "Wed Nov 11 2026"). Ngày UTC chứ không phải ngày local: gate và client
// cùng tính theo UTC.
func utcDateString() string {
	return time.Now().UTC().Format("Mon Jan 2 2006")
}

// platformLabelFor map một GOOS sang nhãn platform mà official CLI render trong
// <env>: windows→win32 (nhãn Node/Windows dùng), darwin giữ nguyên, mọi GOOS còn
// lại (linux, plan9, js, …) gộp về linux — client JS chính chủ cũng chỉ có ba
// nhãn đó.
func platformLabelFor(goos string) string {
	switch goos {
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
//   - message role system/developer nào đã chứa marker ⇒ trả về đúng slice input
//     (idempotent, không copy — pipeline có thể chạy lại trên cùng messages, và
//     chính message inject ra là system nên lần chạy lại cũng no-op);
//   - ngược lại: gộp MỌI message role system/developer (giữ thứ tự gốc, nối bằng
//     dòng trống) vào một system message duy nhất ở index 0, nội dung =
//     prompt official + "\n\n" + phần client; system/developer gốc bị loại;
//     các message khác giữ nguyên thứ tự tương đối.
//
// Marker chỉ được tính trong system/developer (ruling T4-R1): user/assistant/tool
// nhắc lại dòng marker đó là chuyện bình thường (user paste prompt của OpenCode,
// model echo lại trong history), và để nó chặn inject thì request đi tiếp sẽ
// KHÔNG có prompt chính chủ — đúng cái lỗi gate 403 mà task family này sửa.
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
		role, _ := msg["role"].(string)
		isClientPrompt := role == "system" || role == "developer"
		content, ok := messageContentText(msg["content"])
		if !ok {
			kept = append(kept, entry)
			continue
		}
		if !isClientPrompt {
			kept = append(kept, entry)
			continue
		}
		if strings.Contains(content, opencodePromptMarker) {
			return messages
		}
		if content != "" {
			clientPrompt = append(clientPrompt, content)
		}
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
