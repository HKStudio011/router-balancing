namespace RouterBalancing.Core.Server;

/// <summary>
/// Ghi usage token (sau khi response xong) vào counter daily + window TPM (spec §6).
/// Tách riêng để handler/unit test không cần DB thật.
/// </summary>
public interface IClientKeyUsageSink
{
    /// <param name="clientKeyId">null = request đi qua khi auth mở — chỉ ghi journal, không cộng counter.</param>
    Task RecordAsync(long? clientKeyId, int promptTokens, int completionTokens, CancellationToken ct = default);
}
