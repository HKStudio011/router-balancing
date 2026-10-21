namespace RouterBalancing.Core.Server;

/// <summary>
/// Ghi usage sau khi upstream trả response 2xx: token cho client key (counter ngày +
/// window TPM) và request/token cho provider account (sổ <c>UsageDaily</c>).
/// Tách interface để handler/unit test không cần DB thật.
/// </summary>
public interface IRequestUsageSink
{
    /// <param name="clientKeyId">null = request đi qua khi auth mở — không cộng token/TPM cho key.</param>
    /// <param name="providerAccountId">null = không có account (sentinel) — không ghi usage account.</param>
    /// <param name="promptTokens">0 khi upstream không trả usage; account vẫn được tính 1 request.</param>
    /// <param name="completionTokens">0 khi upstream không trả usage.</param>
    /// <param name="ct">Token request — bị hủy trong lúc ghi thì bỏ qua (không phải lỗi DB); lỗi DB thật vẫn log Error.</param>
    Task RecordAsync(long? clientKeyId, long? providerAccountId, int promptTokens, int completionTokens,
        CancellationToken ct = default);
}
