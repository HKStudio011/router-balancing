using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Providers;

/// <summary>
/// Một bước trong chain tra metadata model: Ollama tags → endpoint của provider → static catalog → null.
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

    /// <summary>Trả metadata hoặc <see langword="null"/> nếu bước này không biết.</summary>
    Task<ModelMetadata?> FetchAsync(Provider provider, Model model, CancellationToken ct = default);
}
