using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Settings;

namespace RouterBalancing.Core.Engine;

/// <inheritdoc cref="IModelHealthStore"/>
public sealed class ModelHealthStore(
    IAppSettingsService settings, ILogService log, TimeProvider time) : IModelHealthStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new();

    /// <inheritdoc/>
    public bool IsManualRetry(string modelId)
    {
        // MaxRetry đọc per-call ngoài lock — setting đổi có hiệu lực ngay, tránh lock nesting
        var maxRetry = settings.MaxRetry;
        lock (_lock)
            return _entries.TryGetValue(modelId, out var entry)
                && entry.ConsecutiveFailures >= maxRetry;
    }

    /// <inheritdoc/>
    public void RecordSuccess(string modelId)
    {
        var maxRetry = settings.MaxRetry;
        bool wasOpen;
        lock (_lock)
        {
            if (!_entries.TryGetValue(modelId, out var entry))
                return;
            wasOpen = entry.ConsecutiveFailures >= maxRetry;
            _entries.Remove(modelId);
        }
        // Recover là transition hiếm — log ngoài lock, chỉ khi vừa từ fuse mở về (§5)
        if (wasOpen)
            SafeLog(() => log.Info($"Model '{modelId}' phục hồi — trở lại Healthy.", LogCategory.App));
    }

    /// <inheritdoc/>
    public void RecordFailure(string modelId, TimeSpan? retryAfter = null)
    {
        var maxRetry = settings.MaxRetry;
        var justOpened = false;
        var failures = 0;
        lock (_lock)
        {
            if (!_entries.TryGetValue(modelId, out var entry))
                _entries[modelId] = entry = new Entry();

            // wasOpen tính TRƯỚC khi cộng — chỉ lần vượt ngưỡng mới là transition (log 1 lần)
            var wasOpen = entry.ConsecutiveFailures >= maxRetry;
            entry.ConsecutiveFailures++;
            failures = entry.ConsecutiveFailures;
            if (!wasOpen && failures >= maxRetry)
            {
                // Mở fuse: floor Retry-After (§3.6); các RecordFailure sau không đè lịch probe
                // watchdog đang dùng
                entry.NextProbeAt = time.GetUtcNow() + (retryAfter ?? TimeSpan.Zero);
                justOpened = true;
            }
        }
        if (justOpened)
            SafeLog(() => log.Warn(
                $"Model '{modelId}' chuyển sang ManualRetry sau {failures} lỗi liên tiếp.",
                LogCategory.App));
    }

    /// <inheritdoc/>
    public IReadOnlyList<ManualRetryModel> GetManualRetryModels()
    {
        var maxRetry = settings.MaxRetry;
        lock (_lock)
            return _entries
                .Where(kv => kv.Value.ConsecutiveFailures >= maxRetry)
                .Select(kv => new ManualRetryModel(kv.Key, kv.Value.AttemptsMade, kv.Value.NextProbeAt))
                .ToList();
    }

    /// <inheritdoc/>
    public ProbeFailureResult RecordProbeFailure(string modelId, TimeSpan? retryAfter = null)
    {
        var maxRetry = settings.MaxRetry;
        ProbeFailureResult result;
        string? message = null;
        lock (_lock)
        {
            if (!_entries.TryGetValue(modelId, out var entry)
                || entry.ConsecutiveFailures < maxRetry)
            {
                // Chưa mở fuse / model lạ — probe không nên gọi hàm này: side-effect-free (§2.1 amend)
                result = new ProbeFailureResult(0, null);
            }
            else
            {
                entry.AttemptsMade++;
                if (entry.AttemptsMade >= maxRetry)
                {
                    entry.NextProbeAt = null;
                    message = $"Model '{modelId}' hết lượt probe tự động — chờ Retry now (slice UI)";
                }
                else
                {
                    var backoff = TimeSpan.FromSeconds(60 * entry.AttemptsMade);
                    var wait = retryAfter is { } ra && ra > backoff ? ra : backoff;
                    entry.NextProbeAt = time.GetUtcNow() + wait;
                    message =
                        $"Probe model '{modelId}' thất bại (lần {entry.AttemptsMade}/{maxRetry}) — " +
                        $"thử lại sau {(long)wait.TotalSeconds}s";
                }
                result = new ProbeFailureResult(entry.AttemptsMade, entry.NextProbeAt);
            }
        }
        if (message is not null)
            SafeLog(() => log.Warn(message, LogCategory.App));
        return result;
    }

    /// <summary>Ghi log bọc nuốt — store cam kết không ném ra caller; SQLite lỗi không được phá outcome (I2).</summary>
    private static void SafeLog(Action write)
    {
        try
        {
            write();
        }
        catch
        {
            // Nuốt chủ đích: backend log (SQLite) không được làm hỏng request path/circuit state
        }
    }

    private sealed class Entry
    {
        public int ConsecutiveFailures;
        public int AttemptsMade;
        public DateTimeOffset? NextProbeAt;
    }
}
