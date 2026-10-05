using System.Text;
using System.Text.Json.Nodes;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Providers;

namespace router_balancing_test.Providers;

/// <summary>
/// Fingerprint OpenCode Free: header x-opencode-* + UA opencode + bộ 4 tool decoy
/// trong body — thiếu bất kỳ thành phần nào → 403 FreeTierError (probe live 2026-10-04).
/// </summary>
public class OpenCodeFreeFingerprintTests
{
    private static Provider OpenCode() => new()
    {
        Name = "OpenCode Free",
        Type = ProviderType.OpenAI,
        BaseUrl = "https://opencode.ai/zen",
    };

    private static ByteArrayContent JsonBody(string json)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return content;
    }

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? values.Single() : null;

    private static async Task<JsonObject> ContentJson(HttpRequestMessage request)
    {
        var json = await request.Content!.ReadAsStringAsync();
        return JsonNode.Parse(json)!.AsObject();
    }

    private static readonly string ChatBody =
        """{"model":"mimo-v2.6-flash-free","messages":[{"role":"user","content":"hi"}],"stream":true}""";

    /// <summary>ChatBody stream:true chèn thêm JSON <paramref name="extra"/> (tools, tool_choice...) vào object.</summary>
    private static string ChatWith(string extra) =>
        $$"""
        {"model":"mimo-v2.6-flash-free","messages":[{"role":"user","content":"hi"}],"stream":true,{{extra}}}
        """;

    [Fact]
    public async Task Create_OpenCodeFreeChat_SetsFingerprintHeaders()
    {
        using var request = ProviderRequestFactory.Create(OpenCode(), string.Empty,
            "/v1/chat/completions", HttpMethod.Post, JsonBody(ChatBody));

        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("public", request.Headers.Authorization?.Parameter);
        Assert.Equal("opencode/1.18.31", Header(request, "User-Agent"));
        Assert.Equal("desktop", Header(request, "x-opencode-client"));
        Assert.Equal("global", Header(request, "x-opencode-project"));
        Assert.Matches(@"^ses_[0-9a-f]{12}[0-9A-Za-z]{14}$", Header(request, "x-opencode-session")!);
        Assert.Matches(@"^msg_[0-9a-f]{12}[0-9A-Za-z]{14}$", Header(request, "x-opencode-request")!);
        Assert.Equal("text/event-stream", Header(request, "Accept"));
        // Body đã qua fingerprint: tools có đủ quartet
        var tools = (await ContentJson(request))["tools"]!.AsArray();
        Assert.Equal(4, tools.Count);
    }

    [Fact]
    public void Create_OpenCodeFreeChat_KeepsSessionStableAcrossRequests()
    {
        using var first = ProviderRequestFactory.Create(OpenCode(), string.Empty,
            "/v1/chat/completions", HttpMethod.Post, JsonBody(ChatBody));
        using var second = ProviderRequestFactory.Create(OpenCode(), string.Empty,
            "/v1/chat/completions", HttpMethod.Post, JsonBody(ChatBody));

        // Session đổi mỗi request → đốt quota free tier (429 FreeUsageLimitError)
        Assert.NotNull(Header(first, "x-opencode-session"));
        Assert.Equal(Header(first, "x-opencode-session"), Header(second, "x-opencode-session"));
    }

    [Fact]
    public void Create_OpenCodeFreeChat_DerivesRequestIdFromLastUserMessage()
    {
        var other = ChatBody.Replace("\"hi\"", "\"another question\"");
        using var first = ProviderRequestFactory.Create(OpenCode(), string.Empty,
            "/v1/chat/completions", HttpMethod.Post, JsonBody(ChatBody));
        using var retry = ProviderRequestFactory.Create(OpenCode(), string.Empty,
            "/v1/chat/completions", HttpMethod.Post, JsonBody(ChatBody));
        using var otherTurn = ProviderRequestFactory.Create(OpenCode(), string.Empty,
            "/v1/chat/completions", HttpMethod.Post, JsonBody(other));

        // Cùng turn (retry) → cùng id; turn khác → id khác
        Assert.Equal(Header(first, "x-opencode-request"), Header(retry, "x-opencode-request"));
        Assert.NotEqual(Header(first, "x-opencode-request"), Header(otherTurn, "x-opencode-request"));
        // session vẫn stable kể cả id đổi
        Assert.Equal(Header(first, "x-opencode-session"), Header(otherTurn, "x-opencode-session"));
    }

    [Fact]
    public void Create_OpenCodeFreeChat_StreamFalse_UsesWildcardAccept()
    {
        var body = ChatBody.Replace("\"stream\":true", "\"stream\":false");
        using var request = ProviderRequestFactory.Create(OpenCode(), string.Empty,
            "/v1/chat/completions", HttpMethod.Post, JsonBody(body));

        Assert.Equal("*/*", Header(request, "Accept"));
    }

    [Fact]
    public void Create_OpenCodeFreeProbe_NoFingerprintHeaders()
    {
        using var request = ProviderRequestFactory.Create(OpenCode(), string.Empty);

        // GET probe đang chạy tốt today — không thêm header fingerprint vào
        Assert.False(request.Headers.Contains("x-opencode-session"));
        Assert.False(request.Headers.Contains("User-Agent"));
        Assert.False(request.Headers.Contains("Accept"));
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public void Create_OpenAiKeyless_NoFingerprint()
    {
        using var request = ProviderRequestFactory.Create(
            new Provider { Name = "P", Type = ProviderType.OpenAI, BaseUrl = "https://api.openai.com" },
            string.Empty, "/v1/chat/completions", HttpMethod.Post, JsonBody(ChatBody));

        // D7: key rỗng = bỏ Authorization; không phải OpenCode thì không fingerprint
        Assert.Null(request.Headers.Authorization);
        Assert.False(request.Headers.Contains("x-opencode-session"));
    }

    [Fact]
    public async Task Create_OpenCodeWithRealKey_AppliesFingerprintButKeepsRealKey()
    {
        using var request = ProviderRequestFactory.Create(OpenCode(), "sk-real",
            "/v1/chat/completions", HttpMethod.Post, JsonBody(ChatBody));

        // Gate kiểm theo client fingerprint, không phân biệt auth (9router opencode-zen.js
        // cũng áp UA + x-opencode-* + quartet tools) — chỉ auth là key thật
        Assert.Equal("sk-real", request.Headers.Authorization?.Parameter);
        Assert.Equal("opencode/1.18.31", Header(request, "User-Agent"));
        Assert.Matches(@"^ses_[0-9a-f]{12}[0-9A-Za-z]{14}$", Header(request, "x-opencode-session")!);
        var tools = (await ContentJson(request))["tools"]!.AsArray();
        Assert.Equal(4, tools.Count);
    }

    [Fact]
    public void Create_OpenCode_DifferentKeys_UseDifferentSessions()
    {
        // Quota free tier tính theo session gắn với identity (key) — trùng session giữa
        // 2 account làm cháy quota oan (9router identityKey = auth digest)
        using var free = ProviderRequestFactory.Create(OpenCode(), string.Empty,
            "/v1/chat/completions", HttpMethod.Post, JsonBody(ChatBody));
        using var keyed = ProviderRequestFactory.Create(OpenCode(), "sk-real",
            "/v1/chat/completions", HttpMethod.Post, JsonBody(ChatBody));
        using var sameKey = ProviderRequestFactory.Create(OpenCode(), "sk-real",
            "/v1/chat/completions", HttpMethod.Post, JsonBody(ChatBody));

        Assert.NotNull(Header(free, "x-opencode-session"));
        Assert.NotNull(Header(keyed, "x-opencode-session"));
        Assert.NotEqual(Header(free, "x-opencode-session"), Header(keyed, "x-opencode-session"));
        Assert.Equal(Header(keyed, "x-opencode-session"), Header(sameKey, "x-opencode-session"));
    }

    [Fact]
    public async Task Create_OpenCodeFreeChat_AppendsMissingFingerprintTools()
    {
        using var request = ProviderRequestFactory.Create(OpenCode(), string.Empty,
            "/v1/chat/completions", HttpMethod.Post, JsonBody(ChatBody));

        var body = await ContentJson(request);
        var tools = body["tools"]!.AsArray();
        Assert.Equal(
            new[] { "bash", "glob", "grep", "read" },
            tools.Select(t => t!["function"]!["name"]!.GetValue<string>()));
        Assert.All(tools, t =>
        {
            Assert.Equal("function", t!["type"]!.GetValue<string>());
            Assert.Equal("This tool is currently unavailable and must not be used.",
                t["function"]!["description"]!.GetValue<string>());
        });
        // Không có client tools → tool_choice "none" để decoy không bị chọn
        Assert.Equal("none", body["tool_choice"]!.GetValue<string>());
    }

    [Fact]
    public async Task Create_OpenCodeFreeChat_KeepsClientToolsAndAddsMissingOnly()
    {
        var body = ChatWith("""
            "tools":[{"type":"function","function":{"name":"web_search","description":"d","parameters":{"type":"object","properties":{}}}}]
            """);
        using var request = ProviderRequestFactory.Create(OpenCode(), string.Empty,
            "/v1/chat/completions", HttpMethod.Post, JsonBody(body));

        var parsed = await ContentJson(request);
        var names = parsed["tools"]!.AsArray()
            .Select(t => t!["function"]!["name"]!.GetValue<string>());
        Assert.Equal(
            new[] { "web_search", "bash", "glob", "grep", "read" },
            names);
        // Client có tools nhưng không khai tool_choice → giữ nguyên (mặc định auto)
        Assert.False(parsed.ContainsKey("tool_choice"));
    }

    [Fact]
    public async Task Create_OpenCodeFreeChat_RenamesCaseVariantQuartetAndRetargetsToolChoice()
    {
        var body = ChatWith("""
            "tools":[
              {"type":"function","function":{"name":"Bash","description":"run","parameters":{"type":"object","properties":{}}}},
              {"type":"function","function":{"name":"bash","description":"dup","parameters":{"type":"object","properties":{}}}},
              {"type":"function","function":{"name":"Glob","description":"files","parameters":{"type":"object","properties":{}}}}
            ],
            "tool_choice":{"type":"function","function":{"name":"Bash"}}
            """);
        using var request = ProviderRequestFactory.Create(OpenCode(), string.Empty,
            "/v1/chat/completions", HttpMethod.Post, JsonBody(body));

        var parsed = await ContentJson(request);
        var names = parsed["tools"]!.AsArray()
            .Select(t => t!["function"]!["name"]!.GetValue<string>()).ToList();
        // Bash + bash là duplicate bị upstream từ chối → giữ 1 bản lowercase; Glob → glob
        Assert.Equal(new[] { "bash", "glob", "grep", "read" }, names);
        Assert.Equal("bash", parsed["tool_choice"]!["function"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Create_OpenCodeFreeChat_InvalidJson_LeavesBodyUntouched()
    {
        using var request = ProviderRequestFactory.Create(OpenCode(), string.Empty,
            "/v1/chat/completions", HttpMethod.Post, JsonBody("not-json"));

        Assert.Equal("not-json", await request.Content!.ReadAsStringAsync());
        // Header fingerprint vẫn gắn (không phụ thuộc parse được body)
        Assert.Matches(@"^ses_[0-9a-f]{12}[0-9A-Za-z]{14}$", Header(request, "x-opencode-session")!);
        // Không đọc được user text → id ngẫu nhiên nhưng vẫn đúng format
        Assert.Matches(@"^msg_[0-9a-f]{12}[0-9A-Za-z]{14}$", Header(request, "x-opencode-request")!);
    }

    [Fact]
    public async Task Create_OpenCodeFreeChat_TransformedBody_ContentLengthMatchesNewBytes()
    {
        // Content-Length copy từ body CŨ trong khi transform chèn decoy làm bytes dài hơn →
        // HttpRequestException "Sent N bytes, but Content-Length promised M" lúc gửi thật
        // (bắt thật qua harness gửi request 2026-10-05; unit test chỉ đọc content nên trước đây không thấy)
        using var request = ProviderRequestFactory.Create(OpenCode(), string.Empty,
            "/v1/chat/completions", HttpMethod.Post, JsonBody(ChatBody));

        var bytes = await request.Content!.ReadAsByteArrayAsync();
        Assert.Equal(bytes.Length, request.Content.Headers.ContentLength);
    }
}
