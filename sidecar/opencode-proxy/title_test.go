package main

import (
	"strings"
	"testing"
	"unicode/utf8"
)

// titlePayload dựng body giống request title-gen thật của client 2.1.1: chỉ có
// model/messages/stream/stream_options, KHÔNG có key tools. Dùng chung cho các
// test detection để mọi case chỉ khác đúng biến cần kiểm tra.
func titlePayload(messages []any) map[string]any {
	return map[string]any{
		"model":          testModelID,
		"messages":       messages,
		"stream":         true,
		"stream_options": map[string]any{"include_usage": true},
	}
}

// titleSystemMessage dựng system message content string — shape thật của
// PROMPT_TITLE trong capture v2.1.1.
func titleSystemMessage(content string) map[string]any {
	return map[string]any{"role": "system", "content": content}
}

// capturedPromptTitle đại diện system message của request title thật v2.1.1
// (MIT, packages/core/src/plugin/agent.ts:19-62): dòng mở đầu nguyên văn — mang
// cả hai chữ ký — kèm hai dòng rule lấy nguyên từ <rules>. Bản đầy đủ dài
// ~2.1KB; detection chỉ cần chữ ký nên fixture dùng phần tối thiểu, riêng dòng
// mở đầu và hai rule line giữ đúng như capture ngày 2026-11-10.
const capturedPromptTitle = `You are a title generator. You output ONLY a thread title. Nothing else.

<rules>
- Never use tools
- Always output something meaningful, even if the input is minimal.
</rules>`

func TestIsTitleRequest_V2Fragment_NoTools_FewMessages_True(t *testing.T) {
	// Sáu cụm spec bắt buộc phải nhận diện được. Viết hoa chữ đầu như prompt
	// thật: detection phải case-insensitive, nếu không các cụm lowercase trong
	// spec sẽ không bao giờ khớp văn bản thực tế.
	fragments := []struct {
		name string
		text string
	}{
		{"name conversations", "you name conversations so the user can find them later in a list of past sessions"},
		{"descriptive title", "you generate a descriptive title for this conversation"},
		{"title still matches", "you decide whether the session title still matches the conversation"},
		{"reply with title", "reply with a title and nothing else"},
		{"reply with title for it", "reply with a title for it and nothing else"},
		{"generate title", "generate a title for this conversation"},
	}
	for _, fragment := range fragments {
		t.Run(fragment.name, func(t *testing.T) {
			system := strings.ToUpper(fragment.text[:1]) + fragment.text[1:]
			payload := titlePayload([]any{
				titleSystemMessage(system),
				map[string]any{"role": "user", "content": "say hi"},
			})

			if !isTitleRequest(payload) {
				t.Error("request title với fragment spec, không tools, 2 message phải nhận diện là title")
			}
		})
	}
}

func TestIsTitleRequest_WithToolsOrAssistantTurn_False(t *testing.T) {
	signature := capturedPromptTitle
	// Mọi sub-case đều thỏa các điều kiện còn lại (không tools với assistant/
	// message-count case, đủ chữ ký với tools case) để kết quả false quy được
	// đúng về điều kiện đang kiểm tra.
	cases := []struct {
		name    string
		payload func() map[string]any
	}{
		{
			name: "tools non-empty",
			payload: func() map[string]any {
				p := titlePayload([]any{
					titleSystemMessage(signature),
					map[string]any{"role": "user", "content": "say hi"},
				})
				p["tools"] = []any{map[string]any{"type": "function"}}
				return p
			},
		},
		{
			// Key presence, không phải độ dài: request title thật không có key
			// tools; client gửi tools rỗng vẫn là lượt chat có tool capability.
			name: "tools key present but empty",
			payload: func() map[string]any {
				p := titlePayload([]any{
					titleSystemMessage(signature),
					map[string]any{"role": "user", "content": "say hi"},
				})
				p["tools"] = []any{}
				return p
			},
		},
		{
			name: "assistant turn",
			payload: func() map[string]any {
				return titlePayload([]any{
					titleSystemMessage(signature),
					map[string]any{"role": "user", "content": "say hi"},
					map[string]any{"role": "assistant", "content": "hi"},
				})
			},
		},
		{
			name: "four messages",
			payload: func() map[string]any {
				return titlePayload([]any{
					titleSystemMessage(signature),
					map[string]any{"role": "user", "content": "say hi"},
					map[string]any{"role": "user", "content": "still here"},
					map[string]any{"role": "user", "content": "one more"},
				})
			},
		},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			if isTitleRequest(tc.payload()) {
				t.Error("request có tools/assistant turn/quá 3 message không được coi là title")
			}
		})
	}
}

func TestIsTitleRequest_NoFragment_False(t *testing.T) {
	payload := titlePayload([]any{
		titleSystemMessage("You are a helpful assistant."),
		map[string]any{"role": "user", "content": "say hi"},
	})

	if isTitleRequest(payload) {
		t.Error("chat thường không có chữ ký title phải trả false")
	}
}

func TestIsTitleRequest_CapturedV211PromptTitle_True(t *testing.T) {
	// Đúng shape capture 2026-11-10: body chỉ có model/messages/stream/
	// stream_options, system = PROMPT_TITLE, user = "say hi".
	payload := titlePayload([]any{
		titleSystemMessage(capturedPromptTitle),
		map[string]any{"role": "user", "content": `"say hi"`},
	})

	if !isTitleRequest(payload) {
		t.Error("request title thật của client 2.1.1 phải được nhận diện (ruling T6-R1)")
	}
}

func TestTitleHelpers_NilPayload_NoPanic(t *testing.T) {
	if isTitleRequest(nil) {
		t.Error("payload nil không được coi là title request")
	}
	if got := synthesizeTitle(nil); got != "New Session" {
		t.Errorf("synthesizeTitle(nil) = %q, want %q", got, "New Session")
	}
}

func TestSynthesizeTitle_TrimsToSixWordsFortyChars(t *testing.T) {
	cases := []struct {
		name     string
		messages []any
		want     string
	}{
		{
			name: "long first line cuts at word boundary",
			messages: []any{map[string]any{
				"role":    "user",
				"content": "  Debugging the failing login flow across all production servers\r\nsecond line",
			}},
			want: "Debugging the failing login flow across",
		},
		{
			// 6 từ ngắn: cap word phải dừng trước "seven" dù còn dư 40 ký tự.
			name: "word cap before char cap",
			messages: []any{map[string]any{
				"role":    "user",
				"content": "one two three four five six seven",
			}},
			want: "one two three four five six",
		},
		{
			// 4 từ nhưng vượt 40 ký tự: cap char phải cắt trước "identifiers",
			// không cắt giữa từ và không cần chờ đủ 6 từ.
			name: "char cap before word cap",
			messages: []any{map[string]any{
				"role":    "user",
				"content": "Investigating extraordinarily long identifiers everywhere",
			}},
			want: "Investigating extraordinarily long",
		},
		{
			// Biên trên: đúng 6 từ và đúng 40 ký tự vẫn phải giữ nguyên, không
			// được cắt vì dùng so sánh >= thay vì >.
			name: "exactly six words and forty chars kept whole",
			messages: []any{map[string]any{
				"role":    "user",
				"content": "Fix production login failure with urgent",
			}},
			want: "Fix production login failure with urgent",
		},
		{
			// Cap 40 là ký tự (rune), không phải byte: title tiếng Việt nhiều
			// dấu vượt 40 byte nhưng vẫn dưới 40 rune phải giữ đủ 6 từ.
			name: "unicode counted in runes",
			messages: []any{map[string]any{
				"role":    "user",
				"content": "Kiểm tra lỗi đăng nhập trên máy chủ",
			}},
			want: "Kiểm tra lỗi đăng nhập trên",
		},
		{
			// Bỏ dòng trống đầu (kèm \r) và lấy dòng đầu tiên có nội dung.
			name: "first non-empty line",
			messages: []any{map[string]any{
				"role":    "user",
				"content": "\r\n \t\r\nReal title here\nignored second line",
			}},
			want: "Real title here",
		},
		{
			// Content dạng []part phải đi qua messageContentText như chat thường.
			name: "part content",
			messages: []any{map[string]any{
				"role": "user",
				"content": []any{
					map[string]any{"text": "Part one"},
					map[string]any{"text": " of title"},
				},
			}},
			want: "Part one of title",
		},
		{
			name:     "empty user text",
			messages: []any{map[string]any{"role": "user", "content": ""}},
			want:     "New Session",
		},
		{
			name:     "no user message",
			messages: []any{titleSystemMessage("system only")},
			want:     "New Session",
		},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			got := synthesizeTitle(titlePayload(tc.messages))
			if got != tc.want {
				t.Fatalf("synthesizeTitle() = %q, want %q", got, tc.want)
			}
			if words := len(strings.Fields(got)); words > 6 {
				t.Errorf("title có %d từ, vượt cap 6: %q", words, got)
			}
			if runes := utf8.RuneCountInString(got); runes > 40 {
				t.Errorf("title có %d rune, vượt cap 40: %q", runes, got)
			}
		})
	}
}

func TestSynthesizeTitle_SingleWordOverFortyChars_Fallback(t *testing.T) {
	// Một từ dài hơn 40 ký tự không thể cắt mà vẫn giữ word boundary. Cắt giữa
	// identifier cho ra title vô nghĩa, nên fallback có chủ đích.
	payload := titlePayload([]any{map[string]any{
		"role":    "user",
		"content": "SupercalifragilisticexpialidociousAndMoreCharactersThanForty",
	}})

	if got := synthesizeTitle(payload); got != "New Session" {
		t.Errorf("synthesizeTitle() = %q, want fallback %q cho một từ > 40 ký tự", got, "New Session")
	}
}
