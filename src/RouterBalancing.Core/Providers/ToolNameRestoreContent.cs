using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RouterBalancing.Core.Providers;

/// <summary>
/// HttpContent bọc response OpenCode lane: map ngược tên tool đã bị fingerprint
/// canonicalize ở request (<c>Bash</c>→<c>bash</c>) về đúng tên client đăng ký trong
/// <c>tool_call</c> của response — nếu không model trả <c>bash</c> trong khi client chỉ
/// biết <c>Bash</c> → "invalid tool" / dispatch sai schema parameter (drift theo mỗi bản
/// opencode update). Chỉ dòng <c>data:</c> chứa tên khớp map mới bị đụng tới; mọi byte
/// khác giữ nguyên văn (kể cả <c>[DONE]</c>, keep-alive); JSON dòng hỏng → pass-through
/// (fail-open — không biến lỗi parse thành hỏng cả stream).
/// </summary>
public sealed class ToolNameRestoreContent : HttpContent
{
    private readonly HttpContent _inner;
    private readonly IReadOnlyDictionary<string, string> _restore;

    /// <summary>Bọc <paramref name="inner"/> với map ngược tên tool.</summary>
    /// <param name="inner">Response content gốc từ upstream.</param>
    /// <param name="restore">canonical → tên client gốc (từ <c>RestoreMapFor</c>) — khác rỗng.</param>
    public ToolNameRestoreContent(HttpContent inner, IReadOnlyDictionary<string, string> restore)
    {
        _inner = inner;
        _restore = restore;
        foreach (var header in inner.Headers)
            Headers.TryAddWithoutValidation(header.Key, header.Value);
    }

    /// <inheritdoc/>
    protected override async Task<Stream> CreateContentReadStreamAsync()
    {
        var stream = await _inner.ReadAsStreamAsync();
        return new ToolNameRestoreStream(stream, _restore);
    }

    /// <inheritdoc/>
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        var inner = await _inner.ReadAsStreamAsync();
        await using var restored = new ToolNameRestoreStream(inner, _restore);
        await restored.CopyToAsync(stream);
    }

    /// <inheritdoc/>
    protected override bool TryComputeLength(out long length)
    {
        length = -1;
        return false;
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Stream biến đổi theo DÒNG SSE: tích đủ một dòng (kể cả khi TCP cắt giữa JSON) rồi mới
/// quyết định rewrite — dòng chưa xong không được sửa dở. Output là các segment byte đã
/// biến đổi, phát dần cho caller theo từng lần read.
/// </summary>
internal sealed class ToolNameRestoreStream : Stream
{
    private readonly Stream _inner;
    private readonly Dictionary<string, string> _restore;
    // Needle đã precompute: "<tên canonical>" (kèm nháy) — quick-reject trước khi parse JSON
    private readonly byte[][] _needles;
    private readonly List<byte> _line = [];
    private readonly List<byte[]> _segments = [];
    private int _segmentIndex;
    private int _segmentOffset;
    private bool _eof;

    public ToolNameRestoreStream(Stream inner, IReadOnlyDictionary<string, string> restore)
    {
        _inner = inner;
        _restore = new Dictionary<string, string>(restore, StringComparer.Ordinal);
        _needles = _restore.Keys.Select(k => Encoding.UTF8.GetBytes($"\"{k}\"")).ToArray();
    }

    /// <inheritdoc/>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
        CancellationToken ct = default)
    {
        while (true)
        {
            if (_segmentIndex < _segments.Count)
                return DrainBuffered(buffer);

            if (_eof) return 0;

            await FillOnceAsync(ct);
        }
    }

    /// <inheritdoc/>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count,
        CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    private int DrainBuffered(Memory<byte> buffer)
    {
        var segment = _segments[_segmentIndex];
        var take = Math.Min(buffer.Length, segment.Length - _segmentOffset);
        segment.AsSpan(_segmentOffset, take).CopyTo(buffer.Span);
        _segmentOffset += take;
        if (_segmentOffset >= segment.Length)
        {
            _segments[_segmentIndex] = [];
            _segmentIndex++;
            _segmentOffset = 0;
        }
        return take;
    }

    private async Task FillOnceAsync(CancellationToken ct)
    {
        var chunk = new byte[16 * 1024];
        var read = await _inner.ReadAsync(chunk, ct);
        if (read <= 0)
        {
            _eof = true;
            FlushLine(); // dòng cuối không có \n (SSE chuẩn luôn có nhưng không assume)
            return;
        }
        for (var i = 0; i < read; i++)
        {
            if (chunk[i] != (byte)'\n')
            {
                _line.Add(chunk[i]);
                continue;
            }
            _line.Add((byte)'\n');
            FlushLine();
        }
    }

    private void FlushLine()
    {
        if (_line.Count == 0) return;
        var bytes = _line.ToArray();
        _line.Clear();
        _segments.Add(RestoreLine(bytes));
    }

    // Trả đúng bytes gốc nếu không cần đổi; nếu đổi: "data:" + prefix space gốc + JSON mới
    // + phần kết thúc dòng gốc (\r\n hay \n) — các phần khác của dòng không đụng tới.
    private byte[] RestoreLine(byte[] bytes)
    {
        // contentEnd = hết thân dòng; trailingStart = bắt đầu "\r\n"/"\n" (giữ nguyên văn)
        var trailingStart = bytes.Length;
        if (bytes.Length > 0 && bytes[^1] == (byte)'\n')
        {
            trailingStart = bytes.Length - 1;
            if (trailingStart > 0 && bytes[trailingStart - 1] == (byte)'\r') trailingStart--;
        }

        var line = bytes.AsSpan(0, trailingStart);
        if (!line.StartsWith("data:"u8)) return bytes;
        var payload = line[5..];
        var space = payload.Length > 0 && payload[0] == (byte)' ' ? 1 : 0;
        var json = payload[space..];
        if (json.IsEmpty || json.StartsWith("[DONE]"u8)) return bytes;
        if (!MayContainMappedName(json)) return bytes;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json.ToArray());
        }
        catch (JsonException)
        {
            return bytes; // dòng hỏng (TCP cắt giữa JSON) — pass-through, không phá stream
        }
        if (node is not JsonObject root || !RestoreNames(root)) return bytes;

        var rebuilt = new List<byte>(bytes.Length + 32);
        rebuilt.AddRange(Encoding.UTF8.GetBytes("data:"));
        if (space == 1) rebuilt.Add((byte)' ');
        rebuilt.AddRange(Encoding.UTF8.GetBytes(root.ToJsonString()));
        rebuilt.AddRange(bytes[trailingStart..]);
        return [.. rebuilt];
    }

    private bool MayContainMappedName(ReadOnlySpan<byte> json)
    {
        if (json.IndexOf("\"name\""u8) < 0) return false;
        foreach (var needle in _needles)
        {
            if (json.IndexOf(needle) >= 0) return true;
        }
        return false;
    }

    // Walk choices[].{delta,message}.tool_calls[].function.name — OpenAI chat shape.
    // Đổi giá trị khớp key map sang tên client gốc. Trả true nếu có đổi.
    private bool RestoreNames(JsonObject root)
    {
        if (root["choices"] is not JsonArray choices) return false;
        var changed = false;
        foreach (var choice in choices)
        {
            if (choice is not JsonObject choiceObj) continue;
            foreach (var holderKey in new[] { "delta", "message" })
            {
                if (choiceObj[holderKey] is not JsonObject holder) continue;
                if (holder["tool_calls"] is not JsonArray toolCalls) continue;
                foreach (var toolCall in toolCalls)
                {
                    if (toolCall is not JsonObject tc) continue;
                    if (tc["function"] is not JsonObject function) continue;
                    if (function["name"] is not JsonValue nameValue) continue;
                    if (!nameValue.TryGetValue<string>(out var name)) continue;
                    if (!_restore.TryGetValue(name, out var original)) continue;
                    function["name"] = original;
                    changed = true;
                }
            }
        }
        return changed;
    }

    /// <inheritdoc/>
    public override bool CanRead => _inner.CanRead;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override long Length => _inner.Length;

    /// <inheritdoc/>
    public override long Position
    {
        get => _inner.Position;
        set => throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override void Flush() => _inner.Flush();

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
