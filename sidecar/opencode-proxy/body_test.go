package main

import (
	"bytes"
	"encoding/json"
	"strings"
	"testing"
)

// decodeChatPayload unmarshal body đã shape để test kiểm tra nội dung —
// tách khỏi assertion byte-level (key order) ở các test khác.
func decodeChatPayload(t *testing.T, out []byte) map[string]any {
	t.Helper()
	var payload map[string]any
	if err := json.Unmarshal(out, &payload); err != nil {
		t.Fatalf("unmarshal shaped body: %v", err)
	}
	return payload
}

func TestShapeChatBody_ForceStreamUsageAndMaxTokens(t *testing.T) {
	body, err := json.Marshal(map[string]any{
		"model":    "mimo-v2.6-flash-free",
		"messages": []any{map[string]any{"role": "user", "content": "hi"}},
		"stream":   false,
	})
	if err != nil {
		t.Fatalf("marshal input: %v", err)
	}

	payload := decodeChatPayload(t, shapeChatBody(body))

	if stream, _ := payload["stream"].(bool); !stream {
		t.Error("stream phải được force true")
	}
	options, ok := payload["stream_options"].(map[string]any)
	if !ok {
		t.Fatal("stream_options thiếu hoặc không phải object")
	}
	if includeUsage, _ := options["include_usage"].(bool); !includeUsage {
		t.Error("stream_options.include_usage phải được force true")
	}
	// JSON number decode ra float64.
	if maxTokens, _ := payload["max_completion_tokens"].(float64); maxTokens != 32000 {
		t.Errorf("max_completion_tokens = %v, want 32000", payload["max_completion_tokens"])
	}
}

func TestShapeChatBody_ExistingLimitNotOverridden(t *testing.T) {
	body, err := json.Marshal(map[string]any{
		"model":      "mimo-v2.6-flash-free",
		"messages":   []any{map[string]any{"role": "user", "content": "hi"}},
		"max_tokens": 500,
	})
	if err != nil {
		t.Fatalf("marshal input: %v", err)
	}

	payload := decodeChatPayload(t, shapeChatBody(body))

	if _, exists := payload["max_completion_tokens"]; exists {
		t.Error("không được thêm max_completion_tokens khi client đã gửi limit")
	}
	if got, _ := payload["max_tokens"].(float64); got != 500 {
		t.Errorf("max_tokens = %v, want 500 (giữ nguyên)", payload["max_tokens"])
	}
}

func TestShapeChatBody_KeyOrderMatchesClient2x(t *testing.T) {
	// Order là một phần fingerprint của client 2.x — phải đúng theo byte index.
	body, err := json.Marshal(map[string]any{
		"model":    "mimo-v2.6-flash-free",
		"messages": []any{map[string]any{"role": "user", "content": "hi"}},
		"tools":    []any{},
		"stream":   false,
	})
	if err != nil {
		t.Fatalf("marshal input: %v", err)
	}

	out := string(shapeChatBody(body))

	idxModel := strings.Index(out, `"model"`)
	idxMessages := strings.Index(out, `"messages"`)
	idxTools := strings.Index(out, `"tools"`)
	idxStream := strings.Index(out, `"stream"`)
	idxStreamOptions := strings.Index(out, `"stream_options"`)
	idxMaxTokens := strings.Index(out, `"max_completion_tokens"`)

	inOrder := idxModel < idxMessages &&
		idxMessages < idxTools &&
		idxTools < idxStream &&
		idxStream < idxStreamOptions &&
		idxStreamOptions < idxMaxTokens
	if !inOrder {
		t.Errorf("key order sai: model=%d messages=%d tools=%d stream=%d stream_options=%d max_completion_tokens=%d\n%s",
			idxModel, idxMessages, idxTools, idxStream, idxStreamOptions, idxMaxTokens, out)
	}
}

func TestShapeChatBody_ExtraKeysSortedAlphabetically(t *testing.T) {
	body, err := json.Marshal(map[string]any{
		"model":    "mimo-v2.6-flash-free",
		"messages": []any{map[string]any{"role": "user", "content": "hi"}},
		"zeta":     "last-extra",
		"alpha":    "first-extra",
	})
	if err != nil {
		t.Fatalf("marshal input: %v", err)
	}

	out := string(shapeChatBody(body))

	idxAlpha := strings.Index(out, `"alpha"`)
	idxZeta := strings.Index(out, `"zeta"`)
	idxMaxTokens := strings.Index(out, `"max_completion_tokens"`)

	if idxAlpha < 0 || idxZeta < 0 {
		t.Fatalf("thiếu key extra: alpha=%d zeta=%d\n%s", idxAlpha, idxZeta, out)
	}
	// Extra keys nằm SAU pinned prefix và sort alphabet với nhau.
	if !(idxMaxTokens < idxAlpha && idxAlpha < idxZeta) {
		t.Errorf("extra keys phải sau pinned prefix và alphabet: max_completion_tokens=%d alpha=%d zeta=%d\n%s",
			idxMaxTokens, idxAlpha, idxZeta, out)
	}
}

func TestShapeChatBody_MalformedJSON_ReturnedUnchanged(t *testing.T) {
	raw := []byte(`{"model":`)
	if got := shapeChatBody(raw); !bytes.Equal(got, raw) {
		t.Errorf("JSON hỏng phải được trả về nguyên trạng, got %q", got)
	}
}
