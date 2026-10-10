namespace RouterBalancing.Core.Stats;

/// <inheritdoc cref="IStatsInvalidationSource"/>
public sealed class StatsInvalidationSource : IStatsInvalidationSource
{
    /// <inheritdoc />
    // delegate {} — không cần null-check khi chưa có subscriber
    public event Action Changed = delegate { };

    /// <inheritdoc />
    // Lỗi subscriber để nổi lên caller: RequestJournalWriter bọc try/catch + log.
    // Không nuốt ở đây (và không có logger) — subscriber hỏng phải được thấy trong log.
    // Changed không null-check: delegate {} khởi tạo, chỉ class này assign
    public void Invalidate() => Changed.Invoke();
}
