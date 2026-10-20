using RouterBalancing.Core.Capabilities;
using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Providers;

/// <summary>
/// Một bước trong chain tra metadata model: Ollama tags → NVIDIA catalog → probe endpoint của provider → null.
/// Implement phải trả <see langword="null"/> (không ném) khi không biết —
/// lỗi mạng/parse được nuốt có log ở tầng service, chain mới tiếp tục được.
/// </summary>
public interface IModelMetadataProvider
{
    /// <summary>
    /// true khi nguồn này liệt kê ĐẦY ĐỦ các dimension mà nó phát biểu — vắng key
    /// trong patch nghĩa là "đã biết là không", writer được phép sinh KnownAbsent (R-T7-2/C-P).
    /// false cho probe positive-only (endpoint <c>/v1/models/{id}</c>) và catalog tĩnh.
    /// </summary>
    bool ReturnsCompleteFacts { get; }

    /// <summary>
    /// Nguồn của các fact bước này ghi (C-E/C-P): catalog chính thức = <see cref="CapabilitySource.Catalog"/>,
    /// probe <c>/v1/models/{id}</c> = <see cref="CapabilitySource.Endpoint"/>. Không được hard-code
    /// ở service vì UI phải phân biệt catalog với endpoint (spec §9.10).
    /// </summary>
    CapabilitySource Source { get; }

    /// <summary>Trả metadata hoặc <see langword="null"/> nếu bước này không biết.</summary>
    Task<ModelMetadata?> FetchAsync(Provider provider, Model model, CancellationToken ct = default);
}
