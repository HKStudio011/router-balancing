using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Providers;

/// <summary>
/// Fingerprint cho OpenCode (host <c>opencode.ai</c>) — áp cho cả free lane (key rỗng/
/// "public") và keyed lane. Free-tier gate trả 403 FreeTierError "OpenCode's free tier can
/// only be used from within OpenCode" nếu thiếu bất kỳ thành phần nào: UA
/// <c>opencode/&lt;ver&gt;</c>, header <c>x-opencode-*</c>, và bộ 4 tool decoy
/// <c>bash/glob/grep/read</c> trong body (probe live 2026-10-04; đối chiếu 9router
/// <c>open-sse/executors/opencode.js</c> + <c>open-sse/utils/opencodeFingerprint.js</c>).
/// </summary>
public static class OpenCodeFreeFingerprint
{
    /// <summary>
    /// UA giả lập CLI — upstream kiểm tra regex <c>opencode/&lt;major&gt;.&lt;minor&gt;</c>
    /// (major &gt; 1 hoặc 1.x với minor ≥ 17); 1.18.31 là giá trị 9router đã probe qua gate.
    /// </summary>
    private const string UserAgent = "opencode/1.18.31";

    private const string DecoyDescription = "This tool is currently unavailable and must not be used.";

    private const string Base62Chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    /// <summary>Tên tool (lowercase) mà gate yêu cầu có mặt trong body chat request.</summary>
    private static readonly string[] Quartet = ["bash", "glob", "grep", "read"];

    /// <summary>
    /// Session stable theo BaseUrl trong đời sống process. Mint session mới mỗi request sẽ
    /// đốt quota free tier (429 FreeUsageLimitError) — quota tính theo session, trong khi
    /// CLI thật giữ 1 session dài hạn cho mỗi identity.
    /// </summary>
    private static readonly ConcurrentDictionary<string, string> Sessions = new(StringComparer.Ordinal);

    /// <summary>
    /// Host <c>opencode.ai</c>: gate kiểm theo client fingerprint chứ không phân biệt auth —
    /// 9router áp cùng bộ này cho cả lane key thật (open-sse/executors/opencode-zen.js,
    /// "upstream 403s requests without the file-search quartet").
    /// </summary>
    public static bool IsApplicable(Provider provider) =>
        provider.Type == ProviderType.OpenAI
        && Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var uri)
        && string.Equals(uri.Host, "opencode.ai", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gắn fingerprint vào request chat: transform body (canonicalize tên quartet trùng casing,
    /// bỏ duplicate, chèn decoy thiếu, <c>tool_choice=none</c> khi client không có tools) +
    /// header Authorization/UA/x-opencode-*/Accept. Body không phải JSON hợp lệ → giữ nguyên.
    /// </summary>
    /// <param name="request">Request đã có URI — gắn header vào đây.</param>
    /// <param name="provider">Provider đích (dùng BaseUrl làm khóa session).</param>
    /// <param name="apiKey">Key rỗng → gắn <c>Bearer public</c> (free lane).</param>
    /// <param name="content">Body chat — thay nếu transform đổi bytes.</param>
    public static void Apply(HttpRequestMessage request, Provider provider, string apiKey, HttpContent content)
    {
        // Factory đồng bộ; content luôn là ByteArray/StringContent (in-memory) nên task
        // hoàn tất ngay trên thread — không có rủi ro block chờ I/O mạng.
        var raw = content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        var transformed = TransformBody(raw, out var isStream);
        if (!ReferenceEquals(raw, transformed))
        {
            ContentReplacer.Replace(request, content, transformed);
        }

        if (apiKey.Length == 0)
        {
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "public");
        }
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("x-opencode-client", "desktop");
        request.Headers.TryAddWithoutValidation("x-opencode-project", "global");

        var session = SessionFor(provider, apiKey);
        request.Headers.TryAddWithoutValidation("x-opencode-session", session);
        request.Headers.TryAddWithoutValidation("x-opencode-request", RequestIdFor(session, raw));

        // Client không yêu cầu stream → Accept */* (không ép upstream trả SSE)
        request.Headers.TryAddWithoutValidation("Accept", isStream ? "text/event-stream" : "*/*");
    }

    /// <summary>
    /// Session stable theo BaseUrl + digest key trong đời sống process. Mint session mới mỗi
    /// request sẽ đốt quota free tier (429 FreeUsageLimitError) — quota tính theo session, trong
    /// khi CLI thật giữ 1 session dài hạn cho mỗi identity. Key khác (account khác) → session
    /// riêng, không dùng chung quota.
    /// </summary>
    private static string SessionFor(Provider provider, string apiKey)
    {
        var identity = apiKey.Length == 0
            ? string.Empty
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)))[..16];
        return Sessions.GetOrAdd(provider.BaseUrl + "\0" + identity, _ => RandomId("ses_"));
    }

    /// <summary>
    /// Derive <c>msg_&lt;12 hex&gt;&lt;14 base62&gt;</c> deterministic từ session + last user
    /// message — retry cùng turn giữ nguyên id như CLI thật; không đọc được user text
    /// (body hỏng) thì sinh id ngẫu nhiên đúng format.
    /// </summary>
    private static string RequestIdFor(string session, byte[] body)
    {
        var text = LastUserText(body);
        if (text.Length == 0) return RandomId("msg_");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"opencode-req\0{session}\0{text}"));
        return FormatId("msg_", hash);
    }

    private static string RandomId(string prefix)
    {
        var buffer = new byte[20];
        RandomNumberGenerator.Fill(buffer);
        return FormatId(prefix, buffer);
    }

    /// <summary>6 byte đầu → 12 hex; 14 byte kế → 14 ký tự base62 (mod 62 như 9router).</summary>
    private static string FormatId(string prefix, byte[] hash)
    {
        var builder = new StringBuilder(prefix.Length + 26);
        builder.Append(prefix);
        builder.Append(Convert.ToHexString(hash.AsSpan(0, 6)).ToLowerInvariant());
        for (var i = 6; i < 20; i++)
            builder.Append(Base62Chars[hash[i] % 62]);
        return builder.ToString();
    }

    /// <summary>
    /// Text của message user cuối cùng (tối đa 600 ký tự cuối) — nội dung turn hiện tại,
    /// dùng làm input derive request id. Lỗi parse → chuỗi rỗng.
    /// </summary>
    private static string LastUserText(byte[] body)
    {
        try
        {
            if (JsonNode.Parse(Encoding.UTF8.GetString(body)) is not JsonObject obj
                || obj["messages"] is not JsonArray messages)
                return string.Empty;

            for (var i = messages.Count - 1; i >= 0; i--)
            {
                if (messages[i] is not JsonObject message) continue;
                var role = message["role"] is JsonValue roleValue
                    && roleValue.TryGetValue<string>(out var roleText) ? roleText : null;
                if (role is not null && role != "user") continue;

                var text = ExtractText(message["content"]);
                if (text.Length > 0)
                    return text[^Math.Min(600, text.Length)..];
            }
        }
        catch (JsonException)
        {
            return string.Empty;
        }
        return string.Empty;
    }

    private static string ExtractText(JsonNode? content)
    {
        switch (content)
        {
            case JsonValue scalar when scalar.TryGetValue<string>(out var single):
                return single.Trim();
            case JsonArray parts:
                var texts = new List<string>(parts.Count);
                foreach (var part in parts)
                {
                    switch (part)
                    {
                        case JsonValue s when s.TryGetValue<string>(out var piece):
                            texts.Add(piece);
                            break;
                        case JsonObject obj:
                            foreach (var key in new[] { "text", "input_text" })
                            {
                                if (obj[key] is JsonValue v && v.TryGetValue<string>(out var t))
                                {
                                    texts.Add(t);
                                    break;
                                }
                            }
                            break;
                    }
                }
                return string.Join(' ', texts).Trim();
            default:
                return string.Empty;
        }
    }

    /// <summary>
    /// Transform body chat: canonicalize tên quartet trùng casing (Bash → bash), bỏ duplicate
    /// (upstream từ chối Bash + bash), chèn decoy thiếu, retarget tool_choice theo tên đã rename,
    /// đặt <c>tool_choice=none</c> khi client không có tools (decoy không được chọn).
    /// Trả về mảng gốc nếu không phải JSON object hoặc không có gì đổi.
    /// </summary>
    private static byte[] TransformBody(byte[] raw, out bool isStream)
    {
        isStream = false;
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(Encoding.UTF8.GetString(raw));
        }
        catch (JsonException)
        {
            return raw;
        }
        if (node is not JsonObject obj) return raw;

        isStream = obj.TryGetPropertyValue("stream", out var streamNode)
            && streamNode is JsonValue streamValue
            && streamValue.TryGetValue<bool>(out var flag)
            && flag;

        var hadClientTools = obj["tools"] is JsonArray existingTools && existingTools.Count > 0;
        var changed = ApplyTools(obj, out var renamed);
        changed = RetargetToolChoice(obj, renamed) || changed;

        if (!hadClientTools && !HasToolChoice(obj))
        {
            obj["tool_choice"] = "none";
            changed = true;
        }

        return changed ? Encoding.UTF8.GetBytes(obj.ToJsonString()) : raw;
    }

    /// <returns>Có thay đổi tools nào không.</returns>
    private static bool ApplyTools(JsonObject body, out HashSet<string> renamed)
    {
        renamed = [];
        var tools = body["tools"] as JsonArray;
        if (tools is null)
        {
            tools = [];
            body["tools"] = tools;
        }

        var changed = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = new List<int>();
        for (var i = 0; i < tools.Count; i++)
        {
            if (tools[i] is not JsonObject tool) continue;
            var name = ToolName(tool);
            var key = QuartetKey(name);
            if (key.Length == 0) continue; // tool thường — giữ nguyên, nằm ngoài fingerprint contract

            if (!seen.Add(key))
            {
                duplicates.Add(i); // Bash + bash = duplicate bị upstream từ chối
                continue;
            }
            if (!string.Equals(name, key, StringComparison.Ordinal))
            {
                RenameTool(tool, key);
                renamed.Add(key);
                changed = true;
            }
        }
        for (var i = duplicates.Count - 1; i >= 0; i--)
        {
            tools.RemoveAt(duplicates[i]);
            changed = true;
        }

        foreach (var name in Quartet)
        {
            if (seen.Contains(name)) continue;
            tools.Add(DecoyTool(name));
            changed = true;
        }
        return changed;
    }

    private static bool RetargetToolChoice(JsonObject body, HashSet<string> renamed)
    {
        if (renamed.Count == 0 || body["tool_choice"] is not JsonObject choice) return false;

        if (choice["function"] is JsonObject function)
            return RenameIfQuartet(function, renamed);
        return RenameIfQuartet(choice, renamed);
    }

    private static bool RenameIfQuartet(JsonObject holder, HashSet<string> renamed)
    {
        if (holder["name"] is not JsonValue nameValue
            || !nameValue.TryGetValue<string>(out var name))
            return false;
        var key = QuartetKey(name);
        if (key.Length == 0 || !renamed.Contains(key)) return false;
        holder["name"] = key;
        return true;
    }

    private static bool HasToolChoice(JsonObject body) =>
        body.TryGetPropertyValue("tool_choice", out var choice) && choice is not null;

    private static string ToolName(JsonObject tool)
    {
        if (tool["function"] is JsonObject function
            && function["name"] is JsonValue nameValue
            && nameValue.TryGetValue<string>(out var functionName))
            return functionName.Trim();
        if (tool["name"] is JsonValue topLevel
            && topLevel.TryGetValue<string>(out var topName))
            return topName.Trim();
        return string.Empty;
    }

    /// <summary>Tên lowercase nếu thuộc quartet fingerprint; chuỗi rỗng nếu không.</summary>
    private static string QuartetKey(string name)
    {
        var lower = name.ToLowerInvariant();
        return Array.IndexOf(Quartet, lower) >= 0 ? lower : string.Empty;
    }

    private static void RenameTool(JsonObject tool, string key)
    {
        if (tool["function"] is JsonObject function)
            function["name"] = key;
        else
            tool["name"] = key;
    }

    private static JsonObject DecoyTool(string name) =>
        new()
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = name,
                ["description"] = DecoyDescription,
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject(),
                },
            },
        };
}
