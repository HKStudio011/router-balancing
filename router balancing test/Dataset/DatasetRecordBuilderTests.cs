using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RouterBalancing.Core.Dataset;
using RouterBalancing.Core.Engine;

namespace router_balancing_test.Dataset;

public class DatasetRecordBuilderTests
{
    private static readonly DateTimeOffset FinishedAt = new(2026, 11, 6, 10, 12, 3, TimeSpan.Zero);

    [Fact]
    public void Build_ChatNonStreamCompletion_ExtractsChoiceMessage()
    {
        const string body = """{"model":"m","messages":[{"role":"user","content":"hi"}]}""";
        // CompletionJson của chat non-stream là CẢ object chat.completion — builder phải tự lấy choices[0].message.
        const string completion = """{"id":"1","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"hello"},"finish_reason":"stop"}],"usage":{"prompt_tokens":2,"completion_tokens":3}}""";

        var result = DatasetRecordBuilder.Build(Capture(body, completion, promptTokens: 2, completionTokens: 3));

        Assert.Null(result.Skip);
        var line = Line(result);
        Assert.Equal("hello", Text(line["completion"]!["content"]!));
        Assert.Equal("stop", Text(line["finish_reason"]!));
        Assert.Equal(2, Int(line["usage"]!["prompt_tokens"]!));
        Assert.Equal("chat", Text(line["endpoint"]!));
        Assert.Equal("hi", Text(((JsonArray)line["messages"]!)[0]!["content"]!));
        // ts ISO-8601 UTC đúng shape mẫu spec §7.4 + thread_hint 16 hex.
        Assert.Equal("2026-11-06T10:12:03Z", Text(line["ts"]!));
        Assert.Matches("^[0-9a-f]{16}$", Text(line["thread_hint"]!));
    }

    [Fact]
    public void Build_ChatToolCallCompletion_ToolCallsPreserved_NotSkippedEmpty()
    {
        // content null + tool_calls là mẫu HỢP LỆ — không được tính là Empty (Review Focus 3).
        const string body = """{"model":"m","messages":[{"role":"user","content":"ls"}]}""";
        const string completion = """{"id":"1","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":null,"tool_calls":[{"id":"c1","type":"function","function":{"name":"bash","arguments":"{\"cmd\":\"ls\"}"}}]},"finish_reason":"tool_calls"}]}""";

        var result = DatasetRecordBuilder.Build(Capture(body, completion));

        Assert.Null(result.Skip);
        var line = Line(result);
        var toolCall = ((JsonArray)line["completion"]!["tool_calls"]!)[0]!;
        Assert.Equal("bash", Text(toolCall["function"]!["name"]!));
        // content null giữ nguyên shape OpenAI (null tường minh, không mất key) — kiểm qua JsonElement
        // vì indexer JsonObject trả C#-null cả với property JSON null.
        var completionEl = JsonSerializer.SerializeToElement(line["completion"]!);
        Assert.True(completionEl.TryGetProperty("content", out var contentEl));
        Assert.Equal(JsonValueKind.Null, contentEl.ValueKind);
        // Body không khai báo tool nào → "bash" là unknown.
        var unknown = (JsonArray)line["unknown_tool_calls"]!;
        Assert.Single(unknown);
        Assert.Equal("bash", Text(unknown[0]!));
    }

    [Fact]
    public void Build_OpenCodeV2ToolNames_UnknownSetMatches()
    {
        // Bộ tool OpenCode v2 (execute + opencode.*) — chứng minh unknown_tool_calls tính động,
        // không dựa vào danh sách constants (spec §7.8/§11).
        const string body = """{"model":"m","messages":[{"role":"user","content":"go"}],"tools":[{"type":"function","function":{"name":"execute","parameters":{}}},{"type":"function","function":{"name":"opencode.session_move","parameters":{}}}]}""";
        const string completion = """{"id":"1","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":null,"tool_calls":[{"id":"c1","type":"function","function":{"name":"execute","arguments":"{}"}},{"id":"c2","type":"function","function":{"name":"read_file","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}""";

        var result = DatasetRecordBuilder.Build(Capture(body, completion));

        Assert.Null(result.Skip);
        var unknown = (JsonArray)Line(result)["unknown_tool_calls"]!;
        Assert.Single(unknown);
        Assert.Equal("read_file", Text(unknown[0]!));
    }

    [Fact]
    public void Build_ChatV1Quartet_KnownToolsProduceNoUnknownKey()
    {
        // Quartet v1: completion gọi tool đã khai báo trong body → không có key unknown_tool_calls.
        const string body = """{"model":"m","messages":[{"role":"user","content":"ls"}],"tools":[{"type":"function","function":{"name":"bash","parameters":{}}},{"type":"function","function":{"name":"glob","parameters":{}}},{"type":"function","function":{"name":"grep","parameters":{}}},{"type":"function","function":{"name":"read","parameters":{}}}]}""";
        const string completion = """{"id":"1","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":null,"tool_calls":[{"id":"c1","type":"function","function":{"name":"bash","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}""";

        var result = DatasetRecordBuilder.Build(Capture(body, completion));

        Assert.Null(result.Skip);
        Assert.Null(Line(result)["unknown_tool_calls"]);
    }

    [Fact]
    public void Build_ResponsesInputItems_MapToChatMessages()
    {
        // Bảng map spec §7.5: instructions → system đầu; message nhiều part → nối \n;
        // function_call → assistant tool_calls; function_call_output → tool message.
        const string body = """{"model":"m","instructions":"You are terse.","input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"part one"},{"type":"input_text","text":"part two"}]},{"type":"function_call","name":"bash","arguments":"{}","call_id":"fc1"},{"type":"function_call_output","call_id":"fc1","output":"ok"}]}""";
        const string completion = """{"id":"r1","object":"response","status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"done"}]}]}""";

        var result = DatasetRecordBuilder.Build(Capture(body, completion, endpoint: ProxyEndpoint.Responses));

        Assert.Null(result.Skip);
        var messages = (JsonArray)Line(result)["messages"]!;
        Assert.Equal(4, messages.Count);
        Assert.Equal("system", Text(messages[0]!["role"]!));
        Assert.Equal("You are terse.", Text(messages[0]!["content"]!));
        Assert.Equal("user", Text(messages[1]!["role"]!));
        Assert.Equal("part one\npart two", Text(messages[1]!["content"]!));
        Assert.Equal("assistant", Text(messages[2]!["role"]!));
        var toolCall = ((JsonArray)messages[2]!["tool_calls"]!)[0]!;
        Assert.Equal("fc1", Text(toolCall["id"]!));
        Assert.Equal("bash", Text(toolCall["function"]!["name"]!));
        Assert.Equal("tool", Text(messages[3]!["role"]!));
        Assert.Equal("fc1", Text(messages[3]!["tool_call_id"]!));
        Assert.Equal("ok", Text(messages[3]!["content"]!));

        // input là string → đúng một message user (sau system).
        const string stringInputBody = """{"model":"m","instructions":"sys","input":"hello"}""";
        var line2 = Line(DatasetRecordBuilder.Build(Capture(stringInputBody, completion, endpoint: ProxyEndpoint.Responses)));
        var messages2 = (JsonArray)line2["messages"]!;
        Assert.Equal(2, messages2.Count);
        Assert.Equal("user", Text(messages2[1]!["role"]!));
        Assert.Equal("hello", Text(messages2[1]!["content"]!));
    }

    [Fact]
    public void Build_ResponsesOutputWithFunctionCall_CompletionHasToolCalls()
    {
        const string body = """{"model":"m","input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"run it"}]}]}""";
        const string completion = """{"id":"r1","object":"response","status":"completed","output":[{"type":"function_call","name":"execute","arguments":"{\"cmd\":\"ls\"}","call_id":"c9"}]}""";

        var result = DatasetRecordBuilder.Build(Capture(body, completion, endpoint: ProxyEndpoint.Responses));

        Assert.Null(result.Skip);
        var line = Line(result);
        var toolCall = ((JsonArray)line["completion"]!["tool_calls"]!)[0]!;
        Assert.Equal("execute", Text(toolCall["function"]!["name"]!));
        Assert.Equal("c9", Text(toolCall["id"]!));
        // Không có item message nào → content null tường minh (shape OpenAI) — kiểm qua JsonElement
        // vì indexer JsonObject trả C#-null cả với property JSON null.
        var completionEl = JsonSerializer.SerializeToElement(line["completion"]!);
        Assert.True(completionEl.TryGetProperty("content", out var contentEl));
        Assert.Equal(JsonValueKind.Null, contentEl.ValueKind);
    }

    [Fact]
    public void Build_ResponsesMultipleMessageItems_ContentJoinedByNewline()
    {
        const string body = """{"model":"m","input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"answer"}]}]}""";
        const string completion = """{"id":"r1","object":"response","status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"first"},{"type":"output_text","text":"second"}]},{"type":"message","role":"assistant","content":[{"type":"output_text","text":"third"}]}]}""";

        var result = DatasetRecordBuilder.Build(Capture(body, completion, endpoint: ProxyEndpoint.Responses));

        Assert.Null(result.Skip);
        // Nhiều item message (kể cả phần nhiều part trong một item) → MỘT content nối bằng \n.
        Assert.Equal("first\nsecond\nthird", Text(Line(result)["completion"]!["content"]!));
    }

    [Fact]
    public void Build_ResponsesReasoningItem_GoesToExtraNotMessages()
    {
        const string body = """{"model":"m","input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"think"}]}]}""";
        const string completion = """{"id":"r1","object":"response","status":"completed","output":[{"type":"reasoning","summary":[{"type":"summary_text","text":"hmm"}]},{"type":"message","role":"assistant","content":[{"type":"output_text","text":"ok"}]}]}""";

        var result = DatasetRecordBuilder.Build(Capture(body, completion, endpoint: ProxyEndpoint.Responses));

        Assert.Null(result.Skip);
        var line = Line(result);
        // Reasoning xuống extra.reasoning (giữ nguyên object), không vào messages/completion.
        var reasoning = ((JsonArray)line["extra"]!["reasoning"]!)[0]!;
        Assert.Equal("reasoning", Text(reasoning["type"]!));
        Assert.Equal("ok", Text(line["completion"]!["content"]!));
        var messages = (JsonArray)line["messages"]!;
        Assert.Single(messages);
        Assert.Equal("think", Text(messages[0]!["content"]!));
    }

    [Fact]
    public void Build_CompletionIncomplete_SkipsTruncated()
    {
        const string body = """{"model":"m","messages":[{"role":"user","content":"hi"}]}""";
        const string completion = """{"id":"1","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"half"},"finish_reason":"stop"}]}""";

        var result = DatasetRecordBuilder.Build(Capture(body, completion, complete: false));

        Assert.Equal(DatasetSkipReason.Truncated, result.Skip);
        Assert.Null(result.JsonLine);
    }

    [Fact]
    public void Build_ResponsesStatusIncomplete_SkipsTruncated()
    {
        // Non-stream /v1/responses không có terminal event để soi (CompletionComplete mặc định
        // true) — object response tự khai `status`: incomplete/failed là truncation indicator
        // (spec §7.6, vd. max_output_tokens cắt giữa chừng) → phải skip Truncated, không ghi record cụt.
        const string body = """{"model":"m","input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"hi"}]}]}""";
        const string incomplete = """{"id":"r1","object":"response","status":"incomplete","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"half"}]}],"incomplete_details":{"reason":"max_output_tokens"}}""";

        var result = DatasetRecordBuilder.Build(Capture(body, incomplete, endpoint: ProxyEndpoint.Responses));

        Assert.Equal(DatasetSkipReason.Truncated, result.Skip);
        Assert.Null(result.JsonLine);

        // "failed" cùng là indicator (spec §7.6) — kể cả khi output rỗng, Truncated vẫn ưu tiên Empty.
        const string failed = """{"id":"r1","object":"response","status":"failed","output":[]}""";
        Assert.Equal(DatasetSkipReason.Truncated,
            DatasetRecordBuilder.Build(Capture(body, failed, endpoint: ProxyEndpoint.Responses)).Skip);
    }

    [Fact]
    public void Build_ResponsesStatusCompleted_WritesNormally()
    {
        const string body = """{"model":"m","input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"hi"}]}]}""";
        const string completion = """{"id":"r1","object":"response","status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"ok"}]}]}""";

        var result = DatasetRecordBuilder.Build(Capture(body, completion, endpoint: ProxyEndpoint.Responses));

        Assert.Null(result.Skip);
        Assert.Equal("ok", Text(Line(result)["completion"]!["content"]!));
    }

    [Fact]
    public void Build_ResponsesNoStatus_WritesNormally()
    {
        // Upstream không gửi property `status` → không đoán, record vẫn ghi (chỉ xét khi có mặt).
        const string body = """{"model":"m","input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"hi"}]}]}""";
        const string completion = """{"id":"r1","object":"response","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"ok"}]}]}""";

        var result = DatasetRecordBuilder.Build(Capture(body, completion, endpoint: ProxyEndpoint.Responses));

        Assert.Null(result.Skip);
        Assert.Equal("ok", Text(Line(result)["completion"]!["content"]!));
    }

    [Fact]
    public void Build_EmptyCompletionJson_SkipsEmpty()
    {
        const string body = """{"model":"m","messages":[{"role":"user","content":"hi"}]}""";

        Assert.Equal(DatasetSkipReason.Empty, DatasetRecordBuilder.Build(Capture(body, null)).Skip);
        Assert.Equal(DatasetSkipReason.Empty, DatasetRecordBuilder.Build(Capture(body, "")).Skip);
        Assert.Equal(DatasetSkipReason.Empty, DatasetRecordBuilder.Build(Capture(body, "   ")).Skip);
    }

    [Fact]
    public void Build_BodyAndCompletionThresholds_UseLiteralBoundaries()
    {
        const int inlineLimit = 1_048_576;
        const int exportLimit = 16_777_216;
        Assert.Equal(1_048_576, DatasetRecordBuilder.InlineMaxFieldBytes);
        Assert.Equal(16_777_216, DatasetRecordBuilder.MaxExportFieldBytes);

        const string bodyHead = "{\"model\":\"m\",\"messages\":[{\"role\":\"user\",\"content\":\"";
        const string bodyTail = "\"}]}";
        const string smallBody = """{"model":"m","messages":[{"role":"user","content":"hi"}]}""";
        const string completionHead = "{\"id\":\"1\",\"object\":\"chat.completion\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"";
        const string completionTail = "\"}}]}";
        const string smallCompletion = """{"id":"1","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""";

        foreach (var targetBytes in new[]
                 {
                     inlineLimit - 1, inlineLimit, inlineLimit + 1,
                     exportLimit - 1, exportLimit, exportLimit + 1,
                 })
        {
            // Mỗi fixture chứa cả ký tự hai byte để ma trận kiểm UTF-8, không đếm nhầm char.
            var body = ExactUtf8LengthJson(bodyHead, bodyTail, targetBytes);
            Assert.Equal(targetBytes, Encoding.UTF8.GetByteCount(body));
            AssertBoundary(DatasetRecordBuilder.Build(Capture(body, smallCompletion)), targetBytes);

            var completion = ExactUtf8LengthJson(completionHead, completionTail, targetBytes);
            Assert.Equal(targetBytes, Encoding.UTF8.GetByteCount(completion));
            AssertBoundary(DatasetRecordBuilder.Build(Capture(smallBody, completion)), targetBytes);
        }

        static void AssertBoundary(DatasetBuildResult result, int byteCount)
        {
            if (byteCount > 16_777_216)
            {
                Assert.Equal(DatasetSkipReason.TooLarge, result.Skip);
                Assert.Null(result.JsonLine);
                Assert.False(result.IsLargeRecord);
                return;
            }

            Assert.Null(result.Skip);
            Assert.NotNull(result.JsonLine);
            Assert.Equal(byteCount > 1_048_576, result.IsLargeRecord);
        }
    }

    [Fact]
    public void Build_BodyAtInlineThreshold_KeepsNormalClassification()
    {
        const string head = "{\"model\":\"m\",\"messages\":[{\"role\":\"user\",\"content\":\"";
        const string tail = "\"}]}";
        var body = ExactLengthJson(head, tail, DatasetRecordBuilder.InlineMaxFieldBytes);
        Assert.Equal(DatasetRecordBuilder.InlineMaxFieldBytes, Encoding.UTF8.GetByteCount(body));

        const string completion = """{"id":"1","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""";
        var result = DatasetRecordBuilder.Build(Capture(body, completion));

        Assert.Null(result.Skip);
        Assert.False(result.IsLargeRecord);
        var content = Text(((JsonArray)Line(result)["messages"]!)[0]!["content"]!);
        Assert.Equal(DatasetRecordBuilder.InlineMaxFieldBytes - head.Length - tail.Length, content.Length);
    }

    [Fact]
    public void Build_BodyOverInlineThreshold_MarksLargeWithoutSkipping()
    {
        const string head = "{\"model\":\"m\",\"messages\":[{\"role\":\"user\",\"content\":\"";
        const string tail = "\"}]}";
        var body = ExactLengthJson(head, tail, DatasetRecordBuilder.InlineMaxFieldBytes + 1);
        const string completion = """{"id":"1","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""";

        var result = DatasetRecordBuilder.Build(Capture(body, completion));

        Assert.Null(result.Skip);
        Assert.NotNull(result.JsonLine);
        Assert.True(result.IsLargeRecord);
    }

    [Fact]
    public void Build_BodyAtExportThreshold_WritesLargeRecord()
    {
        // Biên export: body đúng 16 MiB vẫn ghi nguyên vẹn, nhưng được đánh dấu là record lớn.
        const string head = "{\"model\":\"m\",\"messages\":[{\"role\":\"user\",\"content\":\"";
        const string tail = "\"}]}";
        var body = ExactLengthJson(head, tail, DatasetRecordBuilder.MaxExportFieldBytes);
        Assert.Equal(DatasetRecordBuilder.MaxExportFieldBytes, Encoding.UTF8.GetByteCount(body));

        const string completion = """{"id":"1","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""";
        var result = DatasetRecordBuilder.Build(Capture(body, completion));

        Assert.Null(result.Skip);
        Assert.True(result.IsLargeRecord);
        var content = Text(((JsonArray)Line(result)["messages"]!)[0]!["content"]!);
        Assert.Equal(DatasetRecordBuilder.MaxExportFieldBytes - head.Length - tail.Length, content.Length);
    }

    [Fact]
    public void Build_BodyOverExportThreshold_SkipsTooLarge()
    {
        // Vượt cap export ngay 1 byte → bỏ hẳn record, không trả cờ phân loại cho record đã skip.
        const string head = "{\"model\":\"m\",\"messages\":[{\"role\":\"user\",\"content\":\"";
        const string tail = "\"}]}";
        var body = ExactLengthJson(head, tail, DatasetRecordBuilder.MaxExportFieldBytes + 1);
        const string completion = """{"id":"1","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""";

        var result = DatasetRecordBuilder.Build(Capture(body, completion));

        Assert.Equal(DatasetSkipReason.TooLarge, result.Skip);
        Assert.Null(result.JsonLine);
        Assert.False(result.IsLargeRecord);

        // Spec §7.6: "body HOẶC completion" vượt cap đều bỏ — completion to cũng phải skip.
        const string cHead = "{\"id\":\"1\",\"object\":\"chat.completion\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"";
        const string cTail = "\"}}]}";
        var bigCompletion = ExactLengthJson(cHead, cTail, DatasetRecordBuilder.MaxExportFieldBytes + 1);
        var completionResult = DatasetRecordBuilder.Build(
            Capture("""{"model":"m","messages":[{"role":"user","content":"hi"}]}""", bigCompletion));
        Assert.Equal(DatasetSkipReason.TooLarge, completionResult.Skip);
        Assert.False(completionResult.IsLargeRecord);
    }

    [Fact]
    public void Build_CompletionUtf8OverInlineThreshold_MarksLarge()
    {
        const string body = """{"model":"m","messages":[{"role":"user","content":"hi"}]}""";
        const string cHead = "{\"id\":\"1\",\"object\":\"chat.completion\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"";
        const string cTail = "\"}}]}";
        var completion = cHead + new string('é', DatasetRecordBuilder.InlineMaxFieldBytes / 2 + 1) + cTail;
        Assert.True(completion.Length < DatasetRecordBuilder.InlineMaxFieldBytes);
        Assert.True(Encoding.UTF8.GetByteCount(completion) > DatasetRecordBuilder.InlineMaxFieldBytes);

        var result = DatasetRecordBuilder.Build(Capture(body, completion));

        Assert.Null(result.Skip);
        Assert.NotNull(result.JsonLine);
        Assert.True(result.IsLargeRecord);
    }

    [Fact]
    public void Build_CompletionRefusalPhrase_SkipsRefusal()
    {
        const string body = """{"model":"m","messages":[{"role":"user","content":"hi"}]}""";
        const string completion = """{"id":"1","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"Sorry, OpenCode's free tier can only be used from within OpenCode."},"finish_reason":"stop"}]}""";

        var result = DatasetRecordBuilder.Build(Capture(body, completion));

        Assert.Equal(DatasetSkipReason.Refusal, result.Skip);
        Assert.Null(result.JsonLine);

        // Heuristic kiểm không phân biệt hoa/thường (spec §7.8).
        const string upper = """{"id":"1","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"FREE TIER CAN ONLY BE USED FROM WITHIN OPENCODE"},"finish_reason":"stop"}]}""";
        Assert.Equal(DatasetSkipReason.Refusal, DatasetRecordBuilder.Build(Capture(body, upper)).Skip);
    }

    [Fact]
    public void Build_CompletionInvalidCredentialPhrase_SkipsRefusal()
    {
        const string body = """{"model":"m","messages":[{"role":"user","content":"hi"}]}""";
        const string completion = """{"id":"1","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"Upstream request failed: Invalid credential"},"finish_reason":"stop"}]}""";

        var result = DatasetRecordBuilder.Build(Capture(body, completion));

        Assert.Equal(DatasetSkipReason.Refusal, result.Skip);
        Assert.Null(result.JsonLine);
    }

    [Fact]
    public void ThreadHint_SameFirstMessage_SameHint()
    {
        var firstA = (JsonNode)JsonNode.Parse("""{"role":"user","content":"hi"}""")!;
        var firstB = (JsonNode)JsonNode.Parse("""{"role":"user","content":"hi"}""")!;

        var hintA = DatasetRecordBuilder.ComputeThreadHint(firstA);
        var hintB = DatasetRecordBuilder.ComputeThreadHint(firstB);

        Assert.NotNull(hintA);
        Assert.Equal(hintA, hintB);
        Assert.Matches("^[0-9a-f]{16}$", hintA!);
    }

    [Fact]
    public void ThreadHint_DifferentFirstMessage_DifferentHint()
    {
        var firstA = (JsonNode)JsonNode.Parse("""{"role":"user","content":"hi"}""")!;
        var firstB = (JsonNode)JsonNode.Parse("""{"role":"user","content":"hello world"}""")!;

        Assert.NotEqual(DatasetRecordBuilder.ComputeThreadHint(firstA), DatasetRecordBuilder.ComputeThreadHint(firstB));
    }

    [Fact]
    public void Build_ClientBodyInvalidJson_SkipsEmpty()
    {
        // Body không phải JSON hợp lệ: skip, tuyệt đối không ném exception (fail-open).
        const string completion = """{"id":"1","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""";

        var result = DatasetRecordBuilder.Build(Capture("this is not json{{{", completion));

        Assert.Equal(DatasetSkipReason.Empty, result.Skip);
        Assert.Null(result.JsonLine);
    }

    private static DatasetCapture Capture(
        string bodyJson,
        string? completionJson = null,
        bool complete = true,
        ProxyEndpoint endpoint = ProxyEndpoint.Chat,
        int? promptTokens = null,
        int? completionTokens = null) => new(
            RequestId: "req-1",
            Endpoint: endpoint,
            FinishedAt: FinishedAt,
            ClientBody: Encoding.UTF8.GetBytes(bodyJson),
            CompletionJson: completionJson,
            CompletionComplete: complete,
            Provider: "openrouter",
            Account: "acct-1",
            ModelUpstream: "gpt-x-2026",
            ModelRequested: "opencode-free/gpt-x",
            Mode: "balancer",
            ClientKeyId: 3,
            PromptTokens: promptTokens,
            CompletionTokens: completionTokens,
            Lane: DatasetLane.Plain,
            ToolsRewritten: false);

    private static string ExactUtf8LengthJson(string head, string tail, int totalBytes)
    {
        var contentBytes = totalBytes - Encoding.UTF8.GetByteCount(head) - Encoding.UTF8.GetByteCount(tail);
        const int multibyteCharacters = 16;
        Assert.True(contentBytes >= multibyteCharacters * 2);
        var json = new StringBuilder(head)
            .Append('é', multibyteCharacters)
            .Append('a', contentBytes - multibyteCharacters * 2)
            .Append(tail)
            .ToString();
        Assert.Equal(totalBytes, Encoding.UTF8.GetByteCount(json));
        return json;
    }

    /// <summary>
    /// Dựng JSON hợp lệ có độ dài byte chính xác: <paramref name="head"/> kết thúc bằng dấu mở của
    /// chuỗi cần pad, <paramref name="tail"/> bắt đầu bằng dấu đóng — phần giữa lấp đầy bằng 'a'.
    /// </summary>
    private static string ExactLengthJson(string head, string tail, int totalBytes)
    {
        var sb = new StringBuilder(head);
        sb.Append('a', totalBytes - head.Length - tail.Length);
        sb.Append(tail);
        return sb.ToString();
    }

    private static JsonObject Line(DatasetBuildResult result)
    {
        Assert.NotNull(result.JsonLine);
        return (JsonObject)JsonNode.Parse(result.JsonLine!)!;
    }

    private static string Text(JsonNode node) => node.GetValue<string>();

    private static int Int(JsonNode node) => node.GetValue<int>();
}
