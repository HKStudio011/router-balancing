namespace RouterBalancing.Core.Engine;

/// <summary>Gom các network chunk thành payload của từng SSE data event.</summary>
internal sealed class SseDataEventParser
{
    private readonly MemoryStream _line = new();
    private MemoryStream? _data;
    private bool _pendingCr;

    /// <summary>Xử lý một chunk và trả các event hoàn chỉnh, mỗi event có một prefix <c>data: </c>.</summary>
    public List<byte[]> HandleChunk(ReadOnlySpan<byte> chunk)
    {
        var events = new List<byte[]>();
        foreach (var value in chunk)
        {
            if (_pendingCr)
            {
                _pendingCr = false;
                if (value == (byte)'\n') continue;
            }

            if (value == (byte)'\r')
            {
                CompleteLine(events);
                _pendingCr = true;
            }
            else if (value == (byte)'\n')
            {
                CompleteLine(events);
            }
            else
            {
                _line.WriteByte(value);
            }
        }
        return events;
    }

    /// <summary>Xử lý phần cuối stream và trả event còn pending, hoặc <see langword="null"/>.</summary>
    public byte[]? Finish()
    {
        _pendingCr = false;
        if (_line.Length > 0)
            ProcessLine();
        return FlushEvent();
    }

    /// <summary>
    /// Nhìn event cuối như khi gặp EOF nhưng không tiêu hủy parser state, để caller có thể
    /// quyết định chờ và tiếp tục ghép byte nếu nguồn non-standard resume sau temporary EOF.
    /// </summary>
    public byte[]? PeekFinish()
    {
        var clone = new SseDataEventParser();
        clone._line.Write(_line.GetBuffer().AsSpan(0, (int)_line.Length));
        if (_data is not null)
        {
            clone._data = new MemoryStream();
            clone._data.Write(_data.GetBuffer().AsSpan(0, (int)_data.Length));
        }
        clone._pendingCr = _pendingCr;
        return clone.Finish();
    }

    private void CompleteLine(List<byte[]> events)
    {
        if (_line.Length > 0)
        {
            ProcessLine();
            return;
        }

        if (FlushEvent() is { } payload)
            events.Add(payload);
    }

    private void ProcessLine()
    {
        var line = _line.GetBuffer().AsSpan(0, (int)_line.Length);
        _line.SetLength(0);
        if (line.IsEmpty)
            return;

        if (!line.StartsWith("data:"u8))
            return;

        var value = line["data:"u8.Length..];
        if (!value.IsEmpty && value[0] == (byte)' ')
            value = value[1..];

        if (_data is null)
            _data = new MemoryStream();
        else
            _data.WriteByte((byte)'\n');
        _data.Write(value);
    }

    private byte[]? FlushEvent()
    {
        if (_data is not { Length: > 0 } data)
        {
            _data = null;
            return null;
        }

        var value = data.ToArray();
        var payload = new byte["data: "u8.Length + value.Length];
        "data: "u8.CopyTo(payload);
        value.CopyTo(payload, "data: "u8.Length);
        _data = null;
        return payload;
    }
}
