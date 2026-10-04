using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;

namespace RouterBalancing.Core.Engine;

/// <inheritdoc cref="IManualRetryStore"/>
public sealed class ManualRetryStore(ILogService log, TimeProvider time) : IManualRetryStore
{
    private readonly object _lock = new();
    private readonly Dictionary<(ManualRetryLevel Level, long Id, string ModelId), ManualRetryEntry>
        _entries = new();

    /// <inheritdoc/>
    public event Action? Changed;

    /// <inheritdoc/>
    public void Park(ManualRetryLevel level, long id, string modelId, ManualRetryReason reason)
    {
        // V4: chuẩn hóa key trong store — mọi caller không phải nhớ convention (§2.1)
        if (level == ManualRetryLevel.Model)
            id = 0;
        else
            modelId = "";
        var key = (level, id, modelId);

        string? message = null;
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                if (existing.Reason == reason)
                    return; // đã ở đúng state — im lặng, không log/không event
                _entries[key] = existing with { Reason = reason };
            }
            else
            {
                _entries[key] = new ManualRetryEntry(level, id, modelId, reason, time.GetUtcNow());
                // Transition log đúng 1 lần (§5) — level lowercase theo i18n key, không log body/key
                message = $"Đưa {LevelName(level)} '{KeyText(level, id, modelId)}' " +
                    $"vào danh sách retry thủ công: {reason}";
            }
        }
        // Log + event NGOÀI lock: subscriber chậm/SQLite lỗi không được giữ lock hay phá caller
        if (message is not null)
            SafeLog(() => log.Warn(message, LogCategory.App));
        Changed?.Invoke();
    }

    /// <inheritdoc/>
    public void Unpark(ManualRetryLevel level, long id, string modelId)
    {
        if (level == ManualRetryLevel.Model)
            id = 0;
        else
            modelId = "";
        bool removed;
        lock (_lock)
            removed = _entries.Remove((level, id, modelId));
        if (removed)
            Changed?.Invoke(); // log Info phục hồi do caller (Dashboard/ping) ghi — store không log (§5)
    }

    /// <inheritdoc/>
    public bool IsProviderParked(long providerId)
    {
        lock (_lock)
            return _entries.ContainsKey((ManualRetryLevel.Provider, providerId, ""));
    }

    /// <inheritdoc/>
    public bool IsAccountParked(long accountId)
    {
        lock (_lock)
            return _entries.ContainsKey((ManualRetryLevel.Account, accountId, ""));
    }

    /// <inheritdoc/>
    public bool IsModelParked(string modelId)
    {
        lock (_lock)
            return _entries.ContainsKey((ManualRetryLevel.Model, 0, modelId));
    }

    /// <inheritdoc/>
    public IReadOnlyList<ManualRetryEntry> GetEntries()
    {
        lock (_lock)
            return _entries.Values.OrderByDescending(e => e.ParkedAt).ToList();
    }

    private static string LevelName(ManualRetryLevel level) => level switch
    {
        ManualRetryLevel.Provider => "provider",
        ManualRetryLevel.Account => "account",
        _ => "model",
    };

    private static string KeyText(ManualRetryLevel level, long id, string modelId) =>
        level == ManualRetryLevel.Model
            ? modelId
            : id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Ghi log bọc nuốt — store cam kết không ném ra caller; SQLite lỗi không được phá request path.</summary>
    private static void SafeLog(Action write)
    {
        try
        {
            write();
        }
        catch
        {
            // Nuốt chủ đích: backend log (SQLite) không được làm hỏng request path
        }
    }
}
