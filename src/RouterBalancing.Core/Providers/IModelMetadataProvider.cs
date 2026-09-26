using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Providers;

/// <summary>
/// Một bước trong chain tra metadata model: endpoint của provider → static catalog → null.
/// Implement phải trả <see langword="null"/> (không ném) khi không biết —
/// lỗi mạng/parse được nuốt có log ở tầng service, chain mới tiếp tục được.
/// </summary>
public interface IModelMetadataProvider
{
    /// <summary>Trả metadata hoặc <see langword="null"/> nếu bước này không biết.</summary>
    Task<ModelMetadata?> FetchAsync(Provider provider, Model model, CancellationToken ct = default);
}
