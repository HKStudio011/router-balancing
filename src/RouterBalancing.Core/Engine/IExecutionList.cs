namespace RouterBalancing.Core.Engine;

/// <summary>Track request đang phục vụ + enforce <c>Provider.MaxConcurrent</c> mỗi tài khoản (spec §2.1, D-B1).</summary>
public interface IExecutionList
{
    /// <summary>Bắn sau mỗi Exit thành công — dispatcher dùng làm wake signal để thử request bị park.</summary>
    event Action? Exited;

    /// <summary>
    /// Hỏi xem còn slot cho ≥1 TK enabled của provider — KHÔNG mutate (selector dùng khi chọn).
    /// Provider không tồn tại hoặc mọi TK enabled đầy → <see langword="false"/>;
    /// 0 TK enabled → <see langword="true"/> (TryEnter trả sentinel → forward 503, không park — V1).
    /// </summary>
    Task<bool> CanEnterAsync(long providerId, CancellationToken ct);

    /// <summary>
    /// Reserve 1 slot cho TK ít in-flight nhất nếu còn chỗ (query <c>MaxConcurrent</c> mới nhất từ DB).
    /// Trả Id TK đã chọn; <see langword="null"/> = hết slot hoặc provider không tồn tại (park — D-B4/D-B7);
    /// trả <c>0</c> = sentinel khi provider không có TK enabled nào (entry vẫn tạo để forward 503 — V1).
    /// </summary>
    Task<long?> TryEnterAsync(long providerId, string requestId, string providerName, string modelId,
        RequestPriority priority, DateTimeOffset enqueuedAt, CancellationToken ct);

    /// <summary>Trả slot + gỡ entry + bắn <see cref="Exited"/>. Idempotent: id không có thì không bắn event.</summary>
    void Exit(string requestId);

    /// <summary>Id có đang phục vụ không (cancel endpoint phân biệt 409 vs 404).</summary>
    bool Contains(string requestId);

    /// <summary>Số request in-flight của provider.</summary>
    int GetInFlight(long providerId);

    /// <summary>Snapshot toàn bộ entry đang phục vụ — data source cho <c>GET /v1/requests</c>.</summary>
    IReadOnlyList<ExecutionEntry> Snapshot();
}
