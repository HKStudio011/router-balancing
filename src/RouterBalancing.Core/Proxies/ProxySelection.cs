using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Proxies;

/// <summary>
/// Kết quả chọn proxy cho 1 request (spec proxy-per-provider §4):
/// Direct = không proxy (không health); có <c>ProxyIds</c> = dùng tập đó với <c>Mode</c>.
/// </summary>
public sealed record ProxySelection(ProxyMode? Mode, IReadOnlyList<long>? ProxyIds)
{
    /// <summary>Tất cả proxy tắt — không proxy, không health (D2).</summary>
    public static readonly ProxySelection Direct = new(null, null);

    /// <summary>Sẽ dùng proxy (tập + mode), trái ngược Direct.</summary>
    public bool IsDirect => ProxyIds is null;
}
