namespace RouterBalancing.Core.Engine;

/// <summary>Track request đang phục vụ + enforce <c>Provider.MaxConcurrent</c> (spec §2.1).</summary>
public interface IExecutionList
{
    /// <summary>Bắn sau mỗi Exit thành công — dispatcher dùng làm wake signal để thử request bị park.</summary>
    event Action? Exited;

    /// <summary>Hỏi xem provider còn slot không — KHÔNG mutate (selector dùng khi chọn).</summary>
    Task<bool> CanEnterAsync(long providerId, CancellationToken ct);

    /// <summary>Reserve 1 slot nếu còn chỗ (query <c>MaxConcurrent</c> mới nhất từ DB). Trả <see langword="false"/> = hết slot hoặc provider không tồn tại.</summary>
    Task<bool> TryEnterAsync(long providerId, string requestId, string providerName, string modelId,
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
