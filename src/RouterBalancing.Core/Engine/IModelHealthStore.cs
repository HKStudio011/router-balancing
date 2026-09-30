namespace RouterBalancing.Core.Engine;

/// <summary>Model đang ManualRetry — data source cho gate walk (T5) và watchdog (T7).</summary>
/// <param name="ModelId">Model id.</param>
/// <param name="AttemptsMade">Số probe đã tính (0 = fuse vừa mở, chưa probe).</param>
/// <param name="NextProbeAt">Lịch probe kế; <see langword="null"/> = hết lượt probe tự động.</param>
public sealed record ManualRetryModel(string ModelId, int AttemptsMade, DateTimeOffset? NextProbeAt);

/// <summary>Kết quả ghi 1 probe thất bại.</summary>
/// <param name="AttemptsMade">Số probe đã tính sau lần gọi này (0 = model không trong state mở fuse).</param>
/// <param name="NextProbeAt">Lịch probe kế; <see langword="null"/> = model không tồn tại hoặc hết lượt.</param>
public sealed record ProbeFailureResult(int AttemptsMade, DateTimeOffset? NextProbeAt);

/// <summary>
/// Circuit breaker per-model (spec 3C §3.4): <c>Healthy</c> (không entry) |
/// <c>ManualRetry(consecutiveFailures, attemptsMade, nextProbeAt)</c>. Singleton, 1 lock
/// cho dict — thread-safe. Store TỰ ghi 4 log state-transition của §5 qua SafeLog
/// (log không được phá request path — I2). Đồng hồ inject qua <see cref="TimeProvider"/>.
/// </summary>
public interface IModelHealthStore
{
    /// <summary>Model đang mở fuse không — chưa từng lỗi = <see langword="false"/>.</summary>
    /// <param name="modelId">Model cần kiểm tra.</param>
    bool IsManualRetry(string modelId);

    /// <summary>2xx — xóa entry + hủy lịch probe; nếu vừa từ ManualRetry → log Info recover.</summary>
    /// <param name="modelId">Model vừa thành công.</param>
    void RecordSuccess(string modelId);

    /// <summary>
    /// Một request exhaustion retryable — +1 <c>consecutiveFailures</c>; đạt <c>MaxRetry</c> →
    /// mở fuse (<c>NextProbeAt = now</c> floor <paramref name="retryAfter"/>), log Warn chuyển
    /// trạng thái đúng 1 lần; fuse đang mở → chỉ cộng, không đè lịch probe.
    /// </summary>
    /// <param name="modelId">Model cần cộng lỗi.</param>
    /// <param name="retryAfter"><c>Retry-After</c> đã parse — floor lịch probe khi mở fuse (§3.6).</param>
    void RecordFailure(string modelId, TimeSpan? retryAfter = null);

    /// <summary>Danh sách model đang ManualRetry — watchdog quét theo lịch (§3.5).</summary>
    IReadOnlyList<ManualRetryModel> GetManualRetryModels();

    /// <summary>
    /// Ghi 1 probe thất bại: <c>attemptsMade+1</c>; đạt <c>MaxRetry</c> → <c>NextProbeAt = null</c>
    /// (Warn hết lượt); ngược lại <c>now + max(60s×attemptsMade, retryAfter)</c> (Warn backoff).
    /// </summary>
    /// <param name="modelId">Model probe fail.</param>
    /// <param name="retryAfter"><c>Retry-After</c> của probe (null khi không có / lỗi mạng).</param>
    /// <returns>Lượt probe + lịch kế; model không tồn tại hoặc chưa mở fuse → (0, <see langword="null"/>) side-effect-free.</returns>
    ProbeFailureResult RecordProbeFailure(string modelId, TimeSpan? retryAfter = null);
}
