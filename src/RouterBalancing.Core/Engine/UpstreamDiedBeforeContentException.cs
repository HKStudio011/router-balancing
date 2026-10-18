namespace RouterBalancing.Core.Engine;

/// <summary>
/// Upstream cắt kết nối TRƯỚC khi byte content nào tới client — tín hiệu nội bộ
/// tương đương <see cref="DispatchOutcome.Fatal"/> với <c>Status = null</c> (lỗi mạng).
/// <see cref="ProxyRequestHandler"/> bắt nó và trả <c>Fatal(Provider, null)</c> để dispatcher
/// retry transient rồi failover, cùng contract với lỗi kết nối lúc gửi (spec exhaustive-failover §3.1).
/// Khác với mid-content death: đã có byte trên wire thì không retry được — exception gốc
/// (IOException/HttpIOException) phải giữ nguyên để endpoint map Aborted như cũ.
/// </summary>
internal sealed class UpstreamDiedBeforeContentException : Exception
{
    /// <summary>Tạo exception bọc lỗi đọc gốc từ upstream.</summary>
    /// <param name="innerException">Lỗi đọc thật (SocketException/HttpIOException...).</param>
    public UpstreamDiedBeforeContentException(Exception innerException)
        : base("Upstream closed before sending any content.", innerException)
    {
    }
}
