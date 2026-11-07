using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using RouterBalancing.Core.Engine;

namespace RouterBalancing.Core.Dataset;

/// <summary>
/// Biến một <see cref="DatasetCapture"/> thành đúng một trong hai: dòng JSONL schema v:1
/// (<see cref="DatasetBuildResult.JsonLine"/>) hoặc quyết định bỏ record kèm lý do (spec §7.4–§7.6).
/// Chạy trên thread writer — parse/serialize nặng, không chạy trên thread request (spec §7.6).
/// Mọi đầu vào hỏng đều trả về skip có lý do, không ném exception (fail-open).
/// </summary>
public static class DatasetRecordBuilder
{
    /// <summary>
    /// Cap 1MB cho body và completion — điều kiện BỎ record, tuyệt đối không cắt nội dung
    /// (spec §5/§7.3: cắt ở đường export là cắt dữ liệu train).
    /// </summary>
    public const int MaxFieldBytes = 1024 * 1024;

    // Heuristic refusal (spec §7.8): hai chuỗi từ chối đã ghi nhận thật ở upstream OpenCode.
    // Giới hạn của heuristic: upstream đổi cách diễn đạt là bỏ sót — chấp nhận, vì record refusal
    // lặng lẽ dạy model trả lời từ chối còn tệ hơn mất mẫu. Xem ContainsRefusalPhrase.
    private static readonly string[] RefusalPhrases =
    [
        "free tier can only be used from within OpenCode",
        "Upstream request failed: Invalid credential",
    ];

    // Cắt trước khi hash: system prompt dài hàng chục KB không đáng đưa hết vào SHA-256 (spec §7.4).
    private const int ThreadHintMaxChars = 4096;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        // Giữ nguyên <, >, &, ' trong nội dung code — file dataset dễ đọc hơn, vẫn là JSON hợp lệ.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Dựng dòng JSONL v:1 từ capture (spec §7.4–§7.6). Thứ tự kiểm tra bỏ record theo spec §7.6:
    /// TooLarge → Truncated → Empty → Refusal; sau đó chuẩn hoá body/completion và serialize.
    /// </summary>
    /// <param name="capture">Record chờ writer (body gốc của client + completion JSON).</param>
    /// <returns>Dòng JSON (không kèm newline cuối — writer tự thêm) hoặc lý do bỏ record.</returns>
    public static DatasetBuildResult Build(DatasetCapture capture)
    {
        // Cap xét trước mọi thứ: body/completion vượt 1MB gần như chắc chắn là base64 ảnh hay
        // stream bất thường — bỏ hẳn thay vì ghi record nửa vời (spec §7.3/§7.6).
        if (capture.ClientBody.Length > MaxFieldBytes
            || (capture.CompletionJson is not null && Encoding.UTF8.GetByteCount(capture.CompletionJson) > MaxFieldBytes))
        {
            return new DatasetBuildResult(null, DatasetSkipReason.TooLarge);
        }

        // Cụt giữa stream (không có terminal): completion thiếu — lý do cụ thể hơn Empty nên xét trước.
        if (!capture.CompletionComplete)
        {
            return new DatasetBuildResult(null, DatasetSkipReason.Truncated);
        }

        if (string.IsNullOrWhiteSpace(capture.CompletionJson))
        {
            return new DatasetBuildResult(null, DatasetSkipReason.Empty);
        }

        // Heuristic §7.8: upstream trả 2xx nhưng completion là lời từ chối. Chỉ kiểm trên
        // completion (không phải body) để prompt trích dẫn câu từ chối không làm sai record.
        if (ContainsRefusalPhrase(capture.CompletionJson))
        {
            return new DatasetBuildResult(null, DatasetSkipReason.Refusal);
        }

        // Parse body bằng JsonDocument: GetRawText() giữ nguyên byte của messages/tools/input (brief Task 3, điểm a).
        JsonDocument bodyDoc;
        try
        {
            bodyDoc = JsonDocument.Parse(capture.ClientBody);
        }
        catch (JsonException)
        {
            // Body không phải JSON hợp lệ: không có messages/tools để ghi — Empty, không ném.
            return new DatasetBuildResult(null, DatasetSkipReason.Empty);
        }

        var bodyRoot = bodyDoc.RootElement;

        JsonNode? completionDoc;
        try
        {
            completionDoc = JsonNode.Parse(capture.CompletionJson!);
        }
        catch (JsonException)
        {
            return new DatasetBuildResult(null, DatasetSkipReason.Empty);
        }

        JsonArray? messages;
        JsonNode? tools;
        JsonObject? completion;
        string? finishReason = null;
        var extra = new JsonObject();

        if (capture.Endpoint == ProxyEndpoint.Chat)
        {
            // messages/tools lấy nguyên raw text rồi parse lại: giữ byte-for-byte ngữ nghĩa mảng nguồn,
            // không rebuild từng phần tử (brief Task 3, điểm a).
            messages = ParseRaw(bodyRoot, "messages") as JsonArray;
            tools = ParseRaw(bodyRoot, "tools");

            // CompletionJson của chat là CẢ object chat.completion — chỉ lấy choices[0].message,
            // KHÔNG phải cả object response (spec §7.4, Review Focus 2).
            var choice = completionDoc is JsonObject doc && doc["choices"] is JsonArray choices && choices.Count > 0
                ? choices[0] as JsonObject
                : null;
            if (choice?["message"] is not JsonObject message || !HasCompletionContent(message))
            {
                return new DatasetBuildResult(null, DatasetSkipReason.Empty);
            }

            // DeepClone: message vẫn là con của cây completion đã parse — giữ nguyên giá trị/key order.
            completion = (JsonObject)message.DeepClone()!;
            finishReason = Text(choice["finish_reason"]);
        }
        else
        {
            // Response không có field tương đương finish_reason (mọi record ghi được đều completed)
            // nên key này vắng mặt với endpoint responses.
            // input cũng lấy qua GetRawText + parse lại — cùng nguyên tắc byte-for-byte với chat.
            var instructions = bodyRoot.TryGetProperty("instructions", out var ins) && ins.ValueKind == JsonValueKind.String
                ? ins.GetString()
                : null;
            var input = ParseRaw(bodyRoot, "input");
            messages = MapResponsesInput(instructions, input, extra);
            tools = ParseRaw(bodyRoot, "tools");

            var output = completionDoc is JsonObject doc2 ? doc2["output"] as JsonArray : null;
            if (output is null || output.Count == 0)
            {
                // output rỗng → bỏ record + đếm SkippedEmpty (spec §7.4).
                return new DatasetBuildResult(null, DatasetSkipReason.Empty);
            }

            // Nhiều item message → nối content bằng \n thành MỘT message; function_call gộp vào
            // tool_calls — "một shape cho cả hai endpoint" (spec §8.9).
            completion = MapResponsesOutput(output, extra);
            if (completion is null)
            {
                return new DatasetBuildResult(null, DatasetSkipReason.Empty);
            }
        }

        var line = new JsonObject();
        line["v"] = 1;
        line["ts"] = FormatTimestamp(capture.FinishedAt);
        line["request_id"] = capture.RequestId;
        line["endpoint"] = capture.Endpoint == ProxyEndpoint.Chat ? "chat" : "responses";
        AddIfPresent(line, "model_requested", capture.ModelRequested);
        AddIfPresent(line, "model_upstream", capture.ModelUpstream);
        AddIfPresent(line, "mode", capture.Mode);
        AddIfPresent(line, "provider", capture.Provider);
        AddIfPresent(line, "account", capture.Account);
        if (capture.ClientKeyId is long keyId)
        {
            line["client_key_id"] = keyId;
        }
        line["lane"] = capture.Lane switch
        {
            DatasetLane.FreeFingerprint => "free_fingerprint",
            DatasetLane.KeyedFingerprint => "keyed_fingerprint",
            _ => "plain",
        };
        line["tools_rewritten"] = capture.ToolsRewritten;
        AddIfPresent(line, "finish_reason", finishReason);
        if (capture.PromptTokens is int promptTokens || capture.CompletionTokens is int completionTokens)
        {
            var usage = new JsonObject();
            if (capture.PromptTokens is int pt)
            {
                usage["prompt_tokens"] = pt;
            }
            if (capture.CompletionTokens is int ct)
            {
                usage["completion_tokens"] = ct;
            }
            line["usage"] = usage;
        }
        if (messages is not null && messages.Count > 0)
        {
            line["messages"] = messages;
        }
        if (tools is JsonArray toolList)
        {
            line["tools"] = toolList;
        }
        line["completion"] = completion!;
        AddUnknownToolCalls(line, tools, completion!);
        if (extra.Count > 0)
        {
            line["extra"] = extra;
        }
        if (messages is not null && messages.Count > 0 && ComputeThreadHint(messages[0]) is string hint)
        {
            line["thread_hint"] = hint;
        }

        return new DatasetBuildResult(JsonSerializer.Serialize(line, SerializerOptions), null);
    }

    /// <summary>
    /// Tính <c>thread_hint</c>: 16 hex đầu của SHA-256 trên UTF-8 của chuỗi
    /// <c>role + "\n" + content</c> của message đầu (cắt 4096 char đầu) — đủ để offline nhóm/dedup
    /// các lượt của cùng một phiên, vì client không gửi id phiên (spec §7.4).
    /// </summary>
    /// <param name="firstMessage">Message đầu tiên của mảng messages đã dựng; null → trả về null.</param>
    /// <returns>Chuỗi 16 ký tự hex thường, hoặc null khi không có message hợp lệ.</returns>
    public static string? ComputeThreadHint(JsonNode? firstMessage)
    {
        if (firstMessage is not JsonObject msg)
        {
            return null;
        }

        var role = Text(msg["role"]) ?? string.Empty;
        var content = Text(msg["content"]) ?? string.Empty;
        var source = role + "\n" + content;
        if (source.Length > ThreadHintMaxChars)
        {
            source = source[..ThreadHintMaxChars];
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>
    /// Heuristic refusal (spec §7.8): so khớp chuỗi KHÔNG phân biệt hoa/thường trên completion,
    /// chỉ đúng với hai cách diễn đạt đã ghi nhận thật ở upstream — upstream đổi câu từ là bỏ sót.
    /// </summary>
    private static bool ContainsRefusalPhrase(string completionJson) =>
        RefusalPhrases.Any(phrase => completionJson.Contains(phrase, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Completion có nội dung ghi được không. content null/"" KÈM tool_calls vẫn là mẫu hợp lệ
    /// (spec §7.4) — chỉ tính rỗng khi vừa không có text vừa không có tool_calls.
    /// </summary>
    private static bool HasCompletionContent(JsonObject message)
    {
        if (message["tool_calls"] is JsonArray calls && calls.Count > 0)
        {
            return true;
        }

        // Indexer JsonObject trả C#-null cả khi key vắng MẮT lẫn khi giá trị là JSON "null".
        var content = message["content"];
        if (content is null)
        {
            return false;
        }

        if (content is JsonValue v)
        {
            // JsonValue null (JSON "null") và string rỗng đều tính là không có nội dung.
            return v.TryGetValue<string>(out var text) ? !string.IsNullOrWhiteSpace(text) : v.GetValueKind() != JsonValueKind.Null;
        }

        return true; // mảng part/obj — coi như có nội dung
    }

    /// <summary>
    /// Map body <c>/v1/responses</c> thành mảng messages shape chat theo bảng spec §7.5:
    /// <paramref name="instructions"/> → system đứng đầu; <paramref name="input"/> string → một
    /// message user; item message/function_call/function_call_output map 1-1; reasoning + item lạ
    /// xuống <paramref name="extra"/> (không bỏ dữ liệu). Trả về null khi không dựng được message nào.
    /// </summary>
    private static JsonArray? MapResponsesInput(string? instructions, JsonNode? input, JsonObject extra)
    {
        var messages = new JsonArray();
        if (!string.IsNullOrEmpty(instructions))
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = instructions });
        }

        if (input is JsonValue inputValue && inputValue.TryGetValue<string>(out var text))
        {
            // input là string → đúng một message user (spec §7.5).
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = text });
        }
        else if (input is JsonArray items)
        {
            foreach (var item in items)
            {
                MapResponsesInputItem(item, messages, extra);
            }
        }

        return messages.Count > 0 ? messages : null;
    }

    private static void MapResponsesInputItem(JsonNode? item, JsonArray messages, JsonObject extra)
    {
        if (item is not JsonObject obj)
        {
            // Item không phải object: không map được — giữ nguyên vào extra, không bỏ dữ liệu.
            AddExtraItem(extra, "items", item);
            return;
        }

        switch (Text(obj["type"]))
        {
            case "message":
                messages.Add(new JsonObject
                {
                    // DeepClone: node con không tự do đổi parent trong cây JsonNode.
                    ["role"] = obj["role"]?.DeepClone(),
                    // Nhiều part → nối text bằng \n (spec §7.5).
                    ["content"] = JoinContentParts(obj["content"]),
                });
                break;

            case "function_call":
                messages.Add(new JsonObject
                {
                    ["role"] = "assistant",
                    // content null KÈM tool_calls — shape OpenAI, giữ null tường minh.
                    ["content"] = JsonValue.Create<object?>(null),
                    ["tool_calls"] = new JsonArray { MakeToolCall(obj) },
                });
                break;

            case "function_call_output":
                messages.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = obj["call_id"]?.DeepClone(),
                    ["content"] = ToContentString(obj["output"]),
                });
                break;

            case "reasoning":
                AddExtraItem(extra, "reasoning", obj);
                break;

            default:
                if (Text(obj["role"]) is not null)
                {
                    // Item kiểu cũ {role, content} không có type — giữ nguyên (spec §7.5).
                    messages.Add(obj.DeepClone());
                }
                else
                {
                    AddExtraItem(extra, "items", obj);
                }
                break;
        }
    }

    /// <summary>
    /// Map mảng <c>output</c> của response thành MỘT message assistant (spec §7.4/§8.9): nối content
    /// các item message bằng \n, gộp function_call vào tool_calls; reasoning/item lạ xuống
    /// <paramref name="extra"/>. Trả về null khi không còn gì để ghi (→ skip Empty).
    /// </summary>
    private static JsonObject? MapResponsesOutput(JsonArray output, JsonObject extra)
    {
        var contents = new List<string>();
        var toolCalls = new JsonArray();

        foreach (var item in output)
        {
            if (item is not JsonObject obj)
            {
                AddExtraItem(extra, "items", item);
                continue;
            }

            switch (Text(obj["type"]))
            {
                case "message":
                    if (JoinContentParts(obj["content"]) is JsonValue cv && cv.TryGetValue<string>(out var text))
                    {
                        contents.Add(text);
                    }
                    break;

                case "function_call":
                    toolCalls.Add(MakeToolCall(obj));
                    break;

                case "reasoning":
                    AddExtraItem(extra, "reasoning", obj);
                    break;

                default:
                    AddExtraItem(extra, "items", obj);
                    break;
            }
        }

        if (contents.Count == 0 && toolCalls.Count == 0)
        {
            // Output chỉ toàn reasoning/item lạ → completion rỗng, không phải mẫu train (spec §7.6).
            return null;
        }

        var completion = new JsonObject { ["role"] = "assistant" };
        if (contents.Count > 0)
        {
            completion["content"] = string.Join("\n", contents);
        }
        else
        {
            // Shape OpenAI: content null KÈM tool_calls — ghi null tường minh, không bỏ key.
            completion["content"] = JsonValue.Create<object?>(null);
        }
        if (toolCalls.Count > 0)
        {
            completion["tool_calls"] = toolCalls;
        }

        return completion;
    }

    /// <summary>
    /// Nối các part của content thành một chuỗi; chỉ lấy field <c>text</c> của mỗi part (spec §7.5).
    /// Content đã là string → trả về nguyên; mảng không có text nào → null.
    /// </summary>
    private static JsonNode? JoinContentParts(JsonNode? content)
    {
        if (content is null || content is JsonValue value && value.GetValueKind() == JsonValueKind.Null)
        {
            return null;
        }

        if (content is not JsonArray parts)
        {
            return ToContentString(content);
        }

        var texts = new List<string>(parts.Count);
        foreach (var part in parts)
        {
            if (part is JsonObject p && Text(p["text"]) is string t)
            {
                texts.Add(t);
            }
            else if (part is JsonValue pv && pv.TryGetValue<string>(out var direct))
            {
                texts.Add(direct);
            }
        }

        return texts.Count > 0 ? string.Join("\n", texts) : null;
    }

    /// <summary>Đổi item function_call (responses) thành shape tool_call của chat (spec §7.5).</summary>
    private static JsonObject MakeToolCall(JsonObject item) => new()
    {
        // DeepClone: node con không tự do đổi parent trong cây JsonNode.
        ["id"] = item["call_id"]?.DeepClone(),
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = item["name"]?.DeepClone(),
            ["arguments"] = item["arguments"]?.DeepClone(),
        },
    };

    /// <summary>
    /// Tính <c>unknown_tool_calls</c> = hiệu hai tập tên: tool được gọi trong completion trừ tool
    /// khai báo trong body — tính động trên chính record, không có danh sách tool constants nào
    /// (spec §7.8). Rỗng → bỏ hẳn key.
    /// </summary>
    private static void AddUnknownToolCalls(JsonObject line, JsonNode? tools, JsonObject completion)
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);
        if (tools is JsonArray toolList)
        {
            foreach (var tool in toolList)
            {
                if (tool is not JsonObject t)
                {
                    continue;
                }

                // Hai shape: chat để name ở tools[].function.name, responses để ở tools[].name.
                if (Text(t["function"]?["name"]) is string nestedName)
                {
                    declared.Add(nestedName);
                }
                if (Text(t["name"]) is string flatName)
                {
                    declared.Add(flatName);
                }
            }
        }

        var called = new List<string>();
        if (completion["tool_calls"] is JsonArray calls)
        {
            foreach (var call in calls)
            {
                if (call is JsonObject c && Text(c["function"]?["name"]) is string name && !called.Contains(name))
                {
                    called.Add(name);
                }
            }
        }

        var unknown = called.Where(n => !declared.Contains(n)).ToList();
        if (unknown.Count > 0)
        {
            line["unknown_tool_calls"] = new JsonArray(unknown.Select(n => (JsonNode?)n).ToArray());
        }
    }

    /// <summary>
    /// Lấy property nguyên raw text rồi parse lại — giữ byte-for-byte ngữ nghĩa mảng nguồn,
    /// không rebuild từng phần tử (brief Task 3, điểm a).
    /// </summary>
    private static JsonNode? ParseRaw(JsonElement root, string key) =>
        root.TryGetProperty(key, out var prop) ? JsonNode.Parse(prop.GetRawText()) : null;

    private static void AddExtraItem(JsonObject extra, string key, JsonNode? item)
    {
        if (item is null)
        {
            return;
        }

        if (extra[key] is not JsonArray list)
        {
            extra[key] = list = new JsonArray();
        }

        // DeepClone: item vẫn là con của mảng input/output nguồn.
        list.Add(item.DeepClone());
    }

    private static JsonNode? ToContentString(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s))
            {
                return s;
            }

            return v.GetValueKind() == JsonValueKind.Null ? null : (JsonNode)v.ToJsonString();
        }

        // Mảng/obj (vd. output đa part của function_call_output): serialize giữ nguyên dữ liệu.
        return node.ToJsonString();
    }

    private static void AddIfPresent(JsonObject line, string key, string? value)
    {
        // Field null/empty bỏ hẳn key, không ghi null (spec §7.4) — người dùng lọc offline theo presence.
        if (!string.IsNullOrEmpty(value))
        {
            line[key] = value;
        }
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Định dạng UTC ISO-8601 kiểu mẫu spec §7.4 (giây lẻ chỉ ghi khi có).</summary>
    private static string FormatTimestamp(DateTimeOffset finishedAt)
    {
        var utc = finishedAt.ToUniversalTime();
        return utc.Millisecond == 0
            ? utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            : utc.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);
    }
}
