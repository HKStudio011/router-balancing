namespace RouterBalancing.Core.Engine;

/// <summary>
/// Capability riêng của Chat streaming để truyền grace sau EOF theo request mà không thay đổi
/// <see cref="IProxyProtocol"/> hoặc các protocol khác.
/// </summary>
internal interface IChatEofGraceTee
{
    /// <summary>Tee SSE/JSON với grace sau EOF; JSON/non-SSE giữ behavior thông thường.</summary>
    Task<UsageCapture.TeeResult> TeeWithEofGraceAsync(HttpContent source, Stream dest,
        TimeSpan gracePeriod, CancellationToken ct, bool captureCompletion = false);
}
