package main

import (
	"strings"
	"unicode/utf8"
)

// titleRequestMaxMessages giới hạn số message của một request title-gen: client
// 2.1.1 chỉ gửi system (PROMPT_TITLE) + user (nội dung cần đặt title). Nhiều hơn
// 3 message, hoặc có lượt assistant/tool, nghĩa là hội thoại thật.
const titleRequestMaxMessages = 3

// fallbackSessionTitle là title hiển thị khi không trích được nội dung dùng được
// từ request (không có user text, hoặc từ đầu tiên vượt cap ký tự).
const fallbackSessionTitle = "New Session"

const (
	// titleMaxWords / titleMaxRunes là cap của title tổng hợp, khớp spec Task 6.
	titleMaxWords = 6
	titleMaxRunes = 40
)

// titleRequestSignatures là các cụm nhận diện request title-gen, lưu lowercase
// để so khớp một lần trên content đã lowercase.
//
// Sáu cụm đầu do spec Task 6 quy định. Hai cụm cuối là chữ ký thật của
// PROMPT_TITLE client 2.1.1 (MIT, packages/core/src/plugin/agent.ts:19-62), xác
// nhận từ capture 2026-11-10 (ruling T6-R1). Chúng là bản lowercase của hai
// phần tử trong titlePromptSignatureParts (main.go) nhưng phục vụ mục đích
// khác: bên đó rewrite prompt để qua content gate, bên này phân loại request —
// gộp hai danh sách sẽ khiến một thay đổi gate âm thầm đổi cách nhận diện title.
var titleRequestSignatures = []string{
	"you name conversations so the user can find them later in a list of past sessions",
	"you generate a descriptive title for this conversation",
	"you decide whether the session title still matches the conversation",
	"reply with a title and nothing else",
	"reply with a title for it and nothing else",
	"generate a title for this conversation",
	"you are a title generator",
	"only a thread title",
}

// isTitleRequest báo payload có phải request sinh title của client chính chủ
// không. Điều kiện cấu trúc: không có key tools, tối đa 3 message, không có lượt
// assistant/tool; điều kiện nội dung: ít nhất một message mang chữ ký title.
//
// Key tools phải VẮNG MẶT, không phải rỗng: request title thật của client 2.1.1
// không serialize key này (capture 2026-11-10), còn chat thường có thể gửi tools
// rỗng nhưng vẫn là lượt chat có tool capability.
//
// Guard cấu trúc giữ false-positive ở mức không đáng kể: user hoàn toàn có thể
// dán nguyên prompt title vào chat thật, nhưng khi đó request thường có tools
// key, nhiều message, hoặc lượt assistant.
func isTitleRequest(payload map[string]any) bool {
	if payload == nil {
		return false
	}
	if _, hasTools := payload["tools"]; hasTools {
		return false
	}
	messages, ok := payload["messages"].([]any)
	if !ok || len(messages) > titleRequestMaxMessages {
		return false
	}
	hasSignature := false
	for _, entry := range messages {
		msg, ok := entry.(map[string]any)
		if !ok {
			continue
		}
		role, _ := msg["role"].(string)
		if role == "assistant" || role == "tool" {
			return false
		}
		content, ok := messageContentText(msg["content"])
		if ok && containsTitleRequestSignature(content) {
			hasSignature = true
		}
	}
	return hasSignature
}

// containsTitleRequestSignature so khớp không phân biệt hoa/thường: sáu cụm spec
// viết lowercase còn PROMPT_TITLE thật viết hoa — so khớp phân biệt hoa/thường sẽ
// không bao giờ bắt được văn bản thật.
func containsTitleRequestSignature(content string) bool {
	lower := strings.ToLower(content)
	for _, signature := range titleRequestSignatures {
		if strings.Contains(lower, signature) {
			return true
		}
	}
	return false
}

// synthesizeTitle tạo title dự phòng khi upstream không trả title: lấy dòng
// không rỗng đầu tiên của user message ĐẦU TIÊN, rồi cắt còn tối đa 6 từ và 40
// ký tự tại ranh giới từ.
//
// Chỉ xét user message đầu tiên: đó là lượt mở đầu hội thoại mà title cần đại
// diện. User message rỗng hoặc content không đọc được nghĩa là không có nguồn
// title — không đi tìm message sau, vì title theo câu trả lời dở dang còn tệ hơn
// fallback.
//
// Từ đầu tiên dài hơn 40 ký tự luôn trả fallback: cắt giữa một identifier cho ra
// title vô nghĩa, tệ hơn hẳn "New Session".
func synthesizeTitle(payload map[string]any) string {
	messages, ok := payload["messages"].([]any)
	if !ok {
		return fallbackSessionTitle
	}
	for _, entry := range messages {
		msg, ok := entry.(map[string]any)
		if !ok {
			continue
		}
		role, _ := msg["role"].(string)
		if role != "user" {
			continue
		}
		content, ok := messageContentText(msg["content"])
		if !ok {
			return fallbackSessionTitle
		}
		return trimSessionTitle(firstNonEmptyLine(content))
	}
	return fallbackSessionTitle
}

// firstNonEmptyLine trả dòng đầu tiên có nội dung sau khi trim khoảng trắng hai
// đầu (bao gồm \r của line ending CRLF).
func firstNonEmptyLine(text string) string {
	for _, line := range strings.Split(text, "\n") {
		if trimmed := strings.TrimSpace(line); trimmed != "" {
			return trimmed
		}
	}
	return ""
}

// trimSessionTitle ghép từng từ cho tới khi chạm cap 6 từ hoặc 40 ký tự. Duyệt
// theo từ (không cắt chuỗi) nên kết quả luôn kết thúc ở word boundary.
func trimSessionTitle(line string) string {
	words := strings.Fields(line)
	var b strings.Builder
	runes := 0
	for i, word := range words {
		if i == titleMaxWords {
			break
		}
		wordRunes := utf8.RuneCountInString(word)
		separator := 0
		if i > 0 {
			separator = 1
		}
		if runes+separator+wordRunes > titleMaxRunes {
			break
		}
		if i > 0 {
			b.WriteByte(' ')
		}
		b.WriteString(word)
		runes += separator + wordRunes
	}
	if b.Len() == 0 {
		// Dòng không có từ nào, hoặc từ đầu tiên đã vượt cap — xem comment đầu
		// hàm về lựa chọn fallback thay vì cắt giữa từ.
		return fallbackSessionTitle
	}
	return b.String()
}
