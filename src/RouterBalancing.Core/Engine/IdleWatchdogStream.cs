namespace RouterBalancing.Core.Engine;

/// <summary>
/// Stream bọc upstream với watchdog idle-read: mỗi <c>ReadAsync</c> chạy dưới linked CTS có
/// <c>CancelAfter(idleTimeout)</c> — timer reset sau mỗi byte về nên upstream sống nếu cứ có data,
/// treo đúng <c>idleTimeout</c> nếu im lặng (bảo vệ against HttpClient.Timeout = infinite:
/// blackhole treo vĩnh viễn — xem OpenAiUpstreamClient).
/// </summary>
/// <remarks>
/// Quy tắc exception:
/// - Caller ct bị hủy (client abort) → propagate <see cref="OperationCanceledException"/> nguyên vẹn
///   để dispatcher map Aborted như cũ — KHÔNG convert thành lỗi mạng.
/// - Timer ném OCE khi caller còn sống → <see cref="UpstreamIdleTimeoutException"/> (IOException).
/// - Lỗi đọc gốc khác → <see cref="UpstreamStreamDiedException"/> kèm silence gap.
/// </remarks>
internal sealed class IdleWatchdogStream : Stream
{
    private readonly Stream _inner;
    private readonly TimeSpan _idleTimeout;
    private DateTimeOffset _lastByteAt;

    /// <summary>Thời điểm byte đọc được gần nhất — cấp nhật sau mỗi read &gt; 0.</summary>
    public DateTimeOffset LastByteAt => _lastByteAt;

    /// <summary>Bọc <paramref name="inner"/> bằng watchdog idle-read.</summary>
    /// <param name="inner">Stream đọc từ upstream response.</param>
    /// <param name="idleTimeout">Ngưỡng im lặng tối đa cho mỗi lần đọc; &gt; 0.</param>
    public IdleWatchdogStream(Stream inner, TimeSpan idleTimeout)
    {
        _inner = inner;
        _idleTimeout = idleTimeout;
        // Byte "gần nhất" khởi tạo tại thời điểm bọc — không thì death ngay sau wrap có gap vô hạn
        _lastByteAt = DateTimeOffset.UtcNow;
    }

    /// <inheritdoc/>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
        CancellationToken ct = default)
    {
        // Linked CTS mới mỗi read: CancelAfter chạy song song caller ct, timer độc lập theo read —
        // CancelAfter trên CTS dùng chung sẽ không reset được sau khi đã fire.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_idleTimeout);
        int read;
        try
        {
            read = await _inner.ReadAsync(buffer, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timer ném trong lúc caller còn sống → idle timeout, không phải client abort
            throw new UpstreamIdleTimeoutException(_idleTimeout, _lastByteAt);
        }
        catch (OperationCanceledException)
        {
            // Caller ct đã hủy — giữ OCE nguyên vẹn cho đường client abort
            throw;
        }
        catch (Exception ex)
        {
            // Lỗi đọc gốc (RST/EOF hỏng...) — bọc kèm silence gap để journal phân loại
            throw new UpstreamStreamDiedException(ex, DateTimeOffset.UtcNow - _lastByteAt);
        }

        if (read > 0) _lastByteAt = DateTimeOffset.UtcNow;
        return read;
    }

    /// <inheritdoc/>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count,
        CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) =>
        _inner.Read(buffer, offset, count);

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
    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

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
