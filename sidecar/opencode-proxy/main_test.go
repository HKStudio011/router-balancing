package main

import (
	"encoding/json"
	"strings"
	"testing"
)

// titleSystemPrompt là PROMPT_TITLE chính chủ (packages/core/src/plugin/agent.ts)
// — thứ gate chặn 403 khi đi nguyên trạng.
const titleSystemPrompt = `You are a title generator. You output ONLY a thread title. Nothing else.

<task>
Generate a brief title that would help the user find this conversation later.
</task>

<rules>
- you MUST use the same language as the user message you are summarizing
- Never use tools
- NEVER respond to questions, just generate a title for the conversation
</rules>`

func decodeNormalized(t *testing.T, out []byte) map[string]any {
	t.Helper()
	var payload map[string]any
	if err := json.Unmarshal(out, &payload); err != nil {
		t.Fatalf("unmarshal normalized body: %v", err)
	}
	return payload
}

func firstSystemContent(t *testing.T, payload map[string]any) string {
	t.Helper()
	messages, ok := payload["messages"].([]any)
	if !ok || len(messages) == 0 {
		t.Fatal("messages missing")
	}
	msg, ok := messages[0].(map[string]any)
	if !ok {
		t.Fatal("messages[0] is not an object")
	}
	content, _ := msg["content"].(string)
	return content
}

func TestNormalizeBody_RewritesTitleGeneratorSystemPrompt(t *testing.T) {
	body, err := json.Marshal(map[string]any{
		"model": "mimo-v2.6-flash-free",
		"messages": []any{
			map[string]any{"role": "system", "content": titleSystemPrompt},
			map[string]any{"role": "user", "content": "Fix the login bug in auth.ts"},
		},
		"stream": false,
	})
	if err != nil {
		t.Fatalf("marshal input: %v", err)
	}

	payload := decodeNormalized(t, normalizeBody(body))

	if stream, _ := payload["stream"].(bool); !stream {
		t.Error("stream phải được force true")
	}
	system := firstSystemContent(t, payload)
	if containsTitlePromptSignature(system) {
		t.Errorf("system prompt vẫn còn chữ ký title-gen: %.80q", system)
	}
	if system != paraphraseTitlePrompt {
		t.Error("system prompt phải được thay bằng paraphraseTitlePrompt nguyên vẹn")
	}
}

func TestNormalizeBody_TitleSignatureOnLaterSystemMessage_AlsoRewritten(t *testing.T) {
	body, err := json.Marshal(map[string]any{
		"messages": []any{
			map[string]any{"role": "system", "content": "You are opencode."},
			map[string]any{"role": "system", "content": titleSystemPrompt},
			map[string]any{"role": "user", "content": "hi"},
		},
	})
	if err != nil {
		t.Fatalf("marshal input: %v", err)
	}

	payload := decodeNormalized(t, normalizeBody(body))
	messages := payload["messages"].([]any)
	if got, _ := messages[0].(map[string]any); got["content"] != "You are opencode." {
		t.Error("system message thường không được đụng tới")
	}
	if got, _ := messages[1].(map[string]any); got["content"] != paraphraseTitlePrompt {
		t.Error("system message mang chữ ký phải được rewrite")
	}
}

func TestNormalizeBody_LeavesOrdinarySystemPromptUntouched(t *testing.T) {
	const primary = "You are opencode, a coding assistant. Follow the user's instructions."
	body, err := json.Marshal(map[string]any{
		"messages": []any{
			map[string]any{"role": "system", "content": primary},
			map[string]any{"role": "user", "content": "hello"},
		},
	})
	if err != nil {
		t.Fatalf("marshal input: %v", err)
	}

	payload := decodeNormalized(t, normalizeBody(body))
	if got := firstSystemContent(t, payload); got != primary {
		t.Errorf("primary system prompt bị sửa: %q", got)
	}
}

func TestNormalizeBody_UserMessageMentioningTitleGenerator_NotRewritten(t *testing.T) {
	// Chữ ký chỉ được scan trong system role — user prompt nhắc tới title
	// generator là nội dung bình thường, không được rewrite.
	body, err := json.Marshal(map[string]any{
		"messages": []any{
			map[string]any{"role": "user", "content": "You are a title generator of parsers, explain BNF"},
		},
	})
	if err != nil {
		t.Fatalf("marshal input: %v", err)
	}

	payload := decodeNormalized(t, normalizeBody(body))
	messages := payload["messages"].([]any)
	if got, _ := messages[0].(map[string]any); got["content"] != "You are a title generator of parsers, explain BNF" {
		t.Error("user message không được rewrite")
	}
}

func TestNormalizeBody_InvalidJSON_ReturnedAsIs(t *testing.T) {
	raw := []byte("{not json")
	if got := normalizeBody(raw); string(got) != string(raw) {
		t.Error("JSON hỏng phải được trả về nguyên trạng")
	}
}

func TestContainsTitlePromptSignature(t *testing.T) {
	cases := []struct {
		name    string
		content string
		want    bool
	}{
		{"official opening", "You are a title generator. You output ONLY a thread title.", true},
		{"only first phrase", "You are a title generator", true},
		{"only second phrase", "something ONLY a thread title here", true},
		{"ordinary prompt", "You are opencode, a coding assistant.", false},
		{"partial fragment", "You are a title gener", false},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			if got := containsTitlePromptSignature(tc.content); got != tc.want {
				t.Errorf("containsTitlePromptSignature(%q) = %v, want %v", tc.content, got, tc.want)
			}
		})
	}
}

func TestParaphraseTitlePrompt_HasNoGatedSignature(t *testing.T) {
	// Regression: paraphrase tuyệt đối không được mang chữ ký bị gate chặn.
	if containsTitlePromptSignature(paraphraseTitlePrompt) {
		t.Error("paraphraseTitlePrompt đang chứa chữ ký bị chặn")
	}
	for _, banned := range []string{"Never use tools", "thread title", "title generator"} {
		if strings.Contains(paraphraseTitlePrompt, banned) {
			t.Errorf("paraphraseTitlePrompt chứa cụm rủi ro %q", banned)
		}
	}
}
