namespace RouterBalancing.Core.Engine;

/// <summary>
/// Trạng thái retry của MỘT request: tập (providerId, modelId) đã thử + lỗi retryable
/// gần nhất (spec 3C §3.2). Sống trên <see cref="ProxyRequest"/> nên giữ nguyên qua park
/// và capacity-corner re-enqueue — dispatch lại chỉ xét candidate chưa thử, không có
/// vòng lặp nóng. Không lock: 1 logical owner (dispatcher serve task); happens-before
/// qua queue lock (ghi trước khi re-Enqueue, đọc sau Peek/Take).
/// </summary>
public sealed class RetryState
{
    private readonly HashSet<(long ProviderId, string ModelId)> _tried = [];

    /// <summary>Lỗi retryable gần nhất của request — exhaustion convert thành Passthrough/Error(502).</summary>
    public DispatchOutcome.Retryable? LastRetryable { get; set; }

    /// <summary>Đã thử ít nhất 1 candidate chưa (phân biệt 503 walk-rỗng vs exhaustion).</summary>
    public bool HasTried => _tried.Count > 0;

    /// <summary>Số lần thử — cùng 1 model trên 2 provider tính 2 (2 cặp khác nhau).</summary>
    public int TriedCount => _tried.Count;

    /// <summary>Distinct model id đã thử — <c>RecordFailure</c> theo model (Quyết định #4: +1/exhaustion).</summary>
    public IReadOnlyList<string> TriedModels =>
        _tried.Select(p => p.ModelId).Distinct().ToList();

    /// <summary>Ghi nhận đã thử 1 candidate.</summary>
    /// <param name="providerId">Id provider của candidate.</param>
    /// <param name="modelId">Model id của candidate.</param>
    public void MarkTried(long providerId, string modelId) => _tried.Add((providerId, modelId));

    /// <summary>Cặp (provider, model) này đã thử chưa — filter candidate khi walk/dispatch lại.</summary>
    /// <param name="providerId">Id provider của candidate.</param>
    /// <param name="modelId">Model id của candidate.</param>
    public bool IsTried(long providerId, string modelId) => _tried.Contains((providerId, modelId));
}
