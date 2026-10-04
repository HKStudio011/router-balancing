namespace RouterBalancing.Core.Engine;

/// <summary>Cấp gây lỗi — quyết định entity nào bị đưa vào danh sách retry thủ công.</summary>
public enum ManualRetryLevel
{
    /// <summary>Provider chết / sai endpoint — do ping hoặc lỗi mạng request-time.</summary>
    Provider,

    /// <summary>Sai auth (401/403) — auth nằm ở account (spec §1.3 #6).</summary>
    Account,

    /// <summary>Model không tồn tại ở provider (404 + error.code=model_not_found).</summary>
    Model,
}

/// <summary>Lý do vào danh sách — hiển thị trong UI và log transition.</summary>
public enum ManualRetryReason
{
    Unauthorized,
    NotFound,
    ModelNotFound,
    Unreachable,
}

/// <summary>Level + Id (providerId/accountId) + ModelId ("" với Provider/Account) — key định danh entry.</summary>
public sealed record ManualRetryEntry(
    ManualRetryLevel Level, long Id, string ModelId,
    ManualRetryReason Reason, DateTimeOffset ParkedAt);

/// <summary>
/// Danh sách retry thủ công 3 cấp (in-memory, spec manual-retry §2.1) — thay ModelHealthStore.
/// Thread-safe; không persist DB (restart = danh sách sạch, request/ping park lại khi lỗi còn).
/// </summary>
public interface IManualRetryStore
{
    /// <summary>Phát sau mỗi lần Park/Unpark làm thay đổi danh sách — UI re-render.</summary>
    event Action? Changed;

    /// <summary>
    /// Đưa entity vào danh sách. Idempotent: đã park → giữ <c>ParkedAt</c>, cập nhật <paramref name="reason"/>;
    /// cùng lý do → im lặng (không log, không event). Log Warn transition đúng 1 lần.
    /// </summary>
    void Park(ManualRetryLevel level, long id, string modelId, ManualRetryReason reason);

    /// <summary>Gỡ entity khỏi danh sách — entry không tồn tại thì no-op.</summary>
    void Unpark(ManualRetryLevel level, long id, string modelId);

    /// <summary>Provider có đang trong danh sách không.</summary>
    bool IsProviderParked(long providerId);

    /// <summary>Account có đang trong danh sách không.</summary>
    bool IsAccountParked(long accountId);

    /// <summary>Model (exact-id) có đang trong danh sách không.</summary>
    bool IsModelParked(string modelId);

    /// <summary>Toàn bộ entry, xếp theo <see cref="ManualRetryEntry.ParkedAt"/> giảm dần (mới nhất trước).</summary>
    IReadOnlyList<ManualRetryEntry> GetEntries();
}
