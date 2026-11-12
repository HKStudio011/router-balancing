package main

import (
	"encoding/json"
	"sort"
	"strings"
)

// pinnedChatBodyKeys là thứ tự key khi client OpenCode 2.x serialize chat body.
// Gate fingerprint theo byte order nên phần prefix này phải giữ đúng vị trí —
// không được sort alphabet toàn bộ object như json.Marshal(map) làm.
var pinnedChatBodyKeys = []string{
	"model", "messages", "tools", "stream", "stream_options", "max_completion_tokens",
}

// defaultMaxCompletionTokens là limit mặc định khi client chưa gửi limit nào —
// khớp giá trị client 2.x dùng cho free lane.
const defaultMaxCompletionTokens = 32000

// shapeChatBody buộc body theo contract free-lane chat: stream=true,
// stream_options.include_usage=true, và thêm max_completion_tokens=32000 khi
// client chưa gửi limit nào (không ghi đè max_tokens/max_completion_tokens có sẵn).
// Kết quả serialize theo key order client 2.x (pinnedChatBodyKeys rồi alphabet).
// JSON không parse được trả về nguyên input — upstream tự trả lỗi rõ cho client.
func shapeChatBody(body []byte) []byte {
	var payload map[string]any
	if err := json.Unmarshal(body, &payload); err != nil || payload == nil {
		// payload == nil: top-level "null" unmarshal thành nil map không lỗi —
		// assign vào nil map sẽ panic, coi như không shape được.
		return body
	}
	payload["stream"] = true
	options, ok := payload["stream_options"].(map[string]any)
	if !ok {
		options = map[string]any{}
	}
	options["include_usage"] = true
	payload["stream_options"] = options
	if _, hasMCT := payload["max_completion_tokens"]; !hasMCT {
		if _, hasMT := payload["max_tokens"]; !hasMT {
			payload["max_completion_tokens"] = defaultMaxCompletionTokens
		}
	}
	out := marshalOrdered(payload)
	if out == nil {
		return body
	}
	return out
}

// marshalOrdered hand-assemble JSON theo thứ tự: các key trong pinnedChatBodyKeys
// (nếu present) trước, phần còn lại sort alphabet. Mỗi value được json.Marshal
// riêng để nested content giữ escaping đúng — KHÔNG dùng json.Marshal(map) cho
// cả object vì Go sort toàn bộ key alphabet và phá vỡ prefix pinned.
// Trả nil nếu không thể serialize (chỉ có thể với map không phải từ JSON).
func marshalOrdered(payload map[string]any) []byte {
	keys := make([]string, 0, len(payload))
	for _, key := range pinnedChatBodyKeys {
		if _, ok := payload[key]; ok {
			keys = append(keys, key)
		}
	}
	var rest []string
	for key := range payload {
		if !isPinnedKey(key) {
			rest = append(rest, key)
		}
	}
	sort.Strings(rest)
	keys = append(keys, rest...)

	parts := make([]string, 0, len(keys))
	for _, key := range keys {
		keyJSON, err := json.Marshal(key)
		if err != nil {
			return nil // string Go luôn marshal được — thực tế không tới đây.
		}
		valueJSON, err := json.Marshal(payload[key])
		if err != nil {
			// Chỉ xảy ra với value không phải JSON type (channel, func...) —
			// map từ json.Unmarshal không bao giờ trigger; trả nil để caller
			// fallback về body gốc thay vì gửi JSON hỏng.
			return nil
		}
		parts = append(parts, string(keyJSON)+":"+string(valueJSON))
	}
	return []byte("{" + strings.Join(parts, ",") + "}")
}

// isPinnedKey báo key có nằm trong pinnedChatBodyKeys không.
func isPinnedKey(key string) bool {
	for _, pinned := range pinnedChatBodyKeys {
		if key == pinned {
			return true
		}
	}
	return false
}
