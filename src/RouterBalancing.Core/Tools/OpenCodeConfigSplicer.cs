using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RouterBalancing.Core.Tools;

/// <summary>
/// Chèn/thay block <c>providers["router-balancing"]</c> trong file config opencode mà không
/// parse-rồi-ghi-lại toàn file (spec §9.8): định vị block cũ bằng token của <see cref="Utf8JsonReader"/>
/// rồi splice text — mọi byte ngoài block giữ nguyên, an toàn cả với comment/trailing comma (JSONC).
/// </summary>
public static class OpenCodeConfigSplicer
{
    /// <summary>
    /// Trả về text mới sau khi xoá mọi block <c>router-balancing</c> cũ (trong <c>provider</c>
    /// và <c>providers</c>) và chèn <paramref name="providerBlock"/> vào <c>providers</c>
    /// (tạo key <c>providers</c> nếu file chưa có).
    /// </summary>
    /// <param name="text">Nội dung file config (UTF-8 đã decode, có thể chứa comment/trailing comma).</param>
    /// <param name="providerBlock">Object provider mới (thường từ <see cref="OpenCodeConfigGenerator.BuildProviderObject"/>).</param>
    /// <exception cref="OpenCodeConfigFormatException">JSON hỏng, root không phải object,
    /// <c>providers</c> không phải object, hoặc kết quả splice không hợp lệ.</exception>
    public static string Upsert(string text, JsonObject providerBlock)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(providerBlock);

        // Utf8JsonReader trả offset BYTE; text là UTF-16 — phải map trước khi cắt string,
        // nếu không mọi file có ký tự non-ASCII (vd comment tiếng Việt) sẽ bị cắt lệch.
        var utf8 = Encoding.UTF8.GetBytes(text);
        var byteToChar = BuildUtf8ToUtf16Map(utf8);

        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        JsonObjectNode parsed;
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                throw new OpenCodeConfigFormatException("opencode config root must be a JSON object.");
            parsed = ParseObject(ref reader);
            // Đọc tiếp phải hết input; còn token nữa = file có nội dung thừa sau root.
            if (reader.Read())
                throw new OpenCodeConfigFormatException("opencode config has trailing content after the root object.");
        }
        catch (JsonException ex)
        {
            throw new OpenCodeConfigFormatException("opencode config is not valid JSON.", ex);
        }

        var root = ToCharOffsets(parsed, byteToChar);
        var provider = FindLast(root, "provider");
        var providers = FindLast(root, "providers");
        if (providers is { Child: null })
            throw new OpenCodeConfigFormatException("'providers' must be a JSON object.");

        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var indentUnit = root.Members.Count > 0 ? LineIndent(text, root.Members[0].NameStart) : "";
        if (indentUnit.Length == 0) indentUnit = "    ";

        var removals = new List<(int Start, int End)>();
        CollectRemovalRanges(provider, removals);
        CollectRemovalRanges(providers, removals);

        var edits = new List<TextEdit>();
        foreach (var (start, end) in removals)
        {
            edits.Add(new TextEdit(start, end, Replacement: "", IsInsertion: false));
        }

        if (providers?.Child is { } providersObject)
        {
            edits.Add(BuildInsertEdit(
                text, providersObject, OpenCodeConfigGenerator.ProviderId, providerBlock,
                parentIndent: LineIndent(text, providers!.NameStart), indentUnit, newline));
        }
        else
        {
            // Chưa có providers → chèn cả member "providers" vào root (sau khi đã cắt block cũ).
            var providersValue = new JsonObject
            {
                [OpenCodeConfigGenerator.ProviderId] = providerBlock.DeepClone(),
            };
            edits.Add(BuildInsertEdit(
                text, root, key: "providers", value: providersValue,
                parentIndent: "", indentUnit, newline));
        }

        var updated = ApplyEdits(text, edits);
        Validate(updated);
        return updated;
    }

    // ---------- parse tree ----------

    private sealed record JsonMember(
        string Name, int NameStart, int ValueStart, int ValueEnd, JsonObjectNode? Child);

    private sealed record JsonObjectNode(int Start, int End, List<JsonMember> Members);

    private readonly record struct TextEdit(int Start, int End, string Replacement, bool IsInsertion);

    private static JsonObjectNode ParseObject(ref Utf8JsonReader reader)
    {
        var start = checked((int)reader.TokenStartIndex);
        var members = new List<JsonMember>();
        while (true)
        {
            if (!reader.Read())
                throw new OpenCodeConfigFormatException("Unexpected end of config JSON.");

            if (reader.TokenType == JsonTokenType.EndObject)
                return new JsonObjectNode(start, checked((int)reader.BytesConsumed), members);

            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new OpenCodeConfigFormatException(
                    $"Expected a JSON property name but found {reader.TokenType}.");

            var name = reader.GetString()!;
            var nameStart = checked((int)reader.TokenStartIndex);
            if (!reader.Read())
                throw new OpenCodeConfigFormatException("Unexpected end of config JSON.");
            var valueStart = checked((int)reader.TokenStartIndex);

            JsonObjectNode? child = null;
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                child = ParseObject(ref reader);
            }
            else if (reader.TokenType == JsonTokenType.StartArray)
            {
                SkipArray(ref reader);
            }

            members.Add(new JsonMember(
                name, nameStart, valueStart, checked((int)reader.BytesConsumed), child));
        }
    }

    private static void SkipArray(ref Utf8JsonReader reader)
    {
        var depth = 1;
        while (depth > 0)
        {
            if (!reader.Read())
                throw new OpenCodeConfigFormatException("Unexpected end of config JSON.");
            depth += reader.TokenType switch
            {
                JsonTokenType.StartObject or JsonTokenType.StartArray => 1,
                JsonTokenType.EndObject or JsonTokenType.EndArray => -1,
                _ => 0,
            };
        }
    }

    /// <summary>
    /// Map offset byte → offset char của UTF-8 (4-byte sequence ứng với surrogate pair = 2 char).
    /// Chỉ offset tại biên token được dùng, nên map cho byte giữa ký tự không cần chính xác.
    /// </summary>
    private static int[] BuildUtf8ToUtf16Map(byte[] utf8)
    {
        var map = new int[utf8.Length + 1];
        var charIndex = 0;
        var i = 0;
        while (i < utf8.Length)
        {
            var b = utf8[i];
            var byteLength = b < 0x80 ? 1
                : (b & 0xE0) == 0xC0 ? 2
                : (b & 0xF0) == 0xE0 ? 3
                : 4;
            map[i] = charIndex;
            for (var k = 1; k < byteLength && i + k < map.Length; k++) map[i + k] = charIndex;
            i += byteLength;
            charIndex += byteLength == 4 ? 2 : 1;
        }
        map[utf8.Length] = charIndex;
        return map;
    }

    private static JsonObjectNode ToCharOffsets(JsonObjectNode node, int[] byteToChar) =>
        new(
            CharOffset(byteToChar, node.Start),
            CharOffset(byteToChar, node.End),
            node.Members.Select(member => member with
            {
                NameStart = CharOffset(byteToChar, member.NameStart),
                ValueStart = CharOffset(byteToChar, member.ValueStart),
                ValueEnd = CharOffset(byteToChar, member.ValueEnd),
                Child = member.Child is null ? null : ToCharOffsets(member.Child, byteToChar),
            }).ToList());

    private static int CharOffset(int[] byteToChar, int byteIndex) =>
        byteToChar[Math.Clamp(byteIndex, 0, byteToChar.Length - 1)];

    // ---------- block location ----------

    private static JsonMember? FindLast(JsonObjectNode parent, string name)
    {
        JsonMember? found = null;
        foreach (var member in parent.Members)
        {
            if (string.Equals(member.Name, name, StringComparison.Ordinal)) found = member;
        }
        return found;
    }

    private static void CollectRemovalRanges(JsonMember? parentMember, List<(int Start, int End)> ranges)
    {
        if (parentMember?.Child is not { } parent) return;

        var members = parent.Members;
        var index = 0;
        while (index < members.Count)
        {
            if (!IsProviderBlock(members[index]))
            {
                index++;
                continue;
            }

            // Gom cả run block trùng liền kề thành MỘT range — xử lý từng block riêng sẽ sinh
            // range chồng nhau và dễ bỏ sót dấu phẩy khi run nằm cuối object (review fix).
            var first = index;
            while (index + 1 < members.Count && IsProviderBlock(members[index + 1])) index++;
            var last = index;

            var next = last + 1 < members.Count ? members[last + 1] : null;
            if (next is not null)
            {
                // Còn member sau run: xoá từ tên block đầu tới tên member kế (gồm dấu phẩy sau run,
                // giữ dấu phẩy + whitespace trước run).
                ranges.Add((members[first].NameStart, next.NameStart));
            }
            else if (first > 0)
            {
                // Run ở cuối, có member trước: xoá từ sau value member trước (dấu phẩy nằm trong)
                // tới hết block cuối.
                ranges.Add((members[first - 1].ValueEnd, members[last].ValueEnd));
            }
            else
            {
                // Run là toàn bộ object: xoá từ sau '{' tới hết block cuối, giữ whitespace trước '}'.
                ranges.Add((parent.Start + 1, members[last].ValueEnd));
            }

            index++;
        }
    }

    private static bool IsProviderBlock(JsonMember member) =>
        string.Equals(member.Name, OpenCodeConfigGenerator.ProviderId, StringComparison.Ordinal);

    // ---------- insertion ----------

    private static TextEdit BuildInsertEdit(
        string text, JsonObjectNode parent, string key, JsonNode value,
        string parentIndent, string indentUnit, string newline)
    {
        var childIndent = parent.Members.Count > 0
            ? LineIndent(text, parent.Members[0].NameStart)
            : "";
        // Member đầu nằm cùng dòng với '{' → không suy được indent từ nó, rơi về parent + 1 cấp.
        if (childIndent.Length == 0) childIndent = parentIndent + indentUnit;

        var remainingMembers = 0;
        foreach (var member in parent.Members)
        {
            if (!string.Equals(member.Name, key, StringComparison.Ordinal)) remainingMembers++;
        }

        var memberText = FormatMember(key, value, childIndent, indentUnit, newline);
        var interior = text[(parent.Start + 1)..(parent.End - 1)];
        var replacement = newline + memberText;
        if (remainingMembers > 0)
        {
            replacement += ",";
        }
        else if (!interior.Contains('\n'))
        {
            // Object rỗng một dòng ({} hoặc { }) — phải tự ngắt dòng trước dấu đóng.
            replacement += newline + parentIndent;
        }

        return new TextEdit(parent.Start + 1, parent.Start + 1, replacement, IsInsertion: true);
    }

    /// <summary>
    /// Serialize một member <c>"key": value</c> ở đúng indent của file: JSON serializer chỉ có
    /// một mức indent cố định (2 space) nên re-indent từng dòng theo <paramref name="indentUnit"/>
    /// của file gốc (spec §9.8: giữ indent 4 space của file user).
    /// </summary>
    private static string FormatMember(
        string key, JsonNode value, string indent, string indentUnit, string newline)
    {
        var wrapper = new JsonObject { [key] = value.DeepClone() };
        var json = wrapper.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var lines = json.Split('\n');
        var builder = new StringBuilder();
        for (var i = 1; i < lines.Length - 1; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var spaces = 0;
            while (spaces < line.Length && line[spaces] == ' ') spaces++;
            builder.Append(indent);
            // Wrapper serialize mức 2 space: line đầu (member key) đang ở level 1 — trừ 1 để
            // member key nằm đúng `indent`, field con nằm indent + 1 đơn vị của file gốc.
            for (var level = 0; level < spaces / 2 - 1; level++) builder.Append(indentUnit);
            builder.Append(line, spaces, line.Length - spaces);
            if (i < lines.Length - 2) builder.Append(newline);
        }
        return builder.ToString();
    }

    private static string LineIndent(string text, int index)
    {
        var lineStart = index;
        while (lineStart > 0 && text[lineStart - 1] != '\n') lineStart--;
        for (var i = lineStart; i < index; i++)
        {
            if (text[i] != ' ' && text[i] != '\t') return "";
        }
        return text[lineStart..index];
    }

    // ---------- apply + guard ----------

    private static string ApplyEdits(string text, List<TextEdit> edits)
    {
        // Offset giảm dần để offset các edit sau (đã tính trên text gốc) vẫn đúng;
        // cùng offset: xoá trước, chèn sau (insertion point nằm trong range bị xoá).
        edits.Sort((a, b) =>
        {
            var byStart = b.Start.CompareTo(a.Start);
            return byStart != 0 ? byStart : a.IsInsertion.CompareTo(b.IsInsertion);
        });

        var builder = new StringBuilder(text);
        foreach (var edit in edits)
        {
            if (edit.End > edit.Start) builder.Remove(edit.Start, edit.End - edit.Start);
            if (edit.Replacement.Length > 0) builder.Insert(edit.Start, edit.Replacement);
        }
        return builder.ToString();
    }

    /// <summary>Guard chống off-by-one: kết quả splice phải parse được và có đúng block mới.</summary>
    private static void Validate(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("providers", out var providers)
                || providers.ValueKind != JsonValueKind.Object
                || !providers.TryGetProperty(OpenCodeConfigGenerator.ProviderId, out var block)
                || block.ValueKind != JsonValueKind.Object)
            {
                throw new OpenCodeConfigFormatException(
                    $"Spliced config is missing providers[\"{OpenCodeConfigGenerator.ProviderId}\"].");
            }
        }
        catch (JsonException ex)
        {
            throw new OpenCodeConfigFormatException("Spliced config is not valid JSON.", ex);
        }
    }
}
