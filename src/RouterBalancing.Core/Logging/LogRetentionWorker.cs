using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Settings;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Logging;

/// <summary>
/// Dọn nhật ký quá hạn: purge ngay 1 lần khi khởi động rồi lặp mỗi 24 giờ.
/// End-user không mở SQLite tay được nên retention phải tự chạy; nút
/// "Dọn log cũ ngay" trong Settings gọi thẳng <see cref="PurgeAsync"/>.
/// </summary>
public sealed class LogRetentionWorker : IAsyncDisposable
{
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly IAppSettingsService _settings;
    private readonly ILogService _log;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public LogRetentionWorker(
        IDbContextFactory<RouterBalancingDbContext> db,
        IAppSettingsService settings,
        ILogService log)
    {
        _db = db;
        _settings = settings;
        _log = log;
    }

    /// <summary>Xóa mọi dòng log cũ hơn setting <c>logRetentionDays</c>.</summary>
    /// <returns>Số dòng đã xóa.</returns>
    public async Task<int> PurgeAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-_settings.LogRetentionDays);
        using var db = _db.CreateDbContext();
        // Không ghi log dòng "đã dọn" — đó là dữ liệu trong chính bảng đang purge
        return await db.LogEntries
            .Where(entry => entry.Timestamp < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Chạy nền: purge ngay rồi lặp 24h — gọi lần 2 chỉ là no-op.</summary>
    public void Start()
    {
        if (_loop is not null) return;
        // Capture CTS local: DisposeAsync có thể đặt _cts = null trước khi Task kịp chạy
        var cts = _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(cts.Token));
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await PurgeAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Lỗi một chu kỳ không được giết vòng lặp — vẫn thử ở chu kỳ sau.
                // Lần ghi lỗi này cũng có thể ném khi DB hỏng (nguyên nhân gốc làm
                // purge thất bại) — nuốt là chấp nhận được vì đây là best-effort
                // diagnostics, nếu không thì vòng lặp sẽ chết vĩnh viễn.
                try
                {
                    _log.Error("Dọn nhật ký định kỳ thất bại.", ex);
                }
                catch
                {
                    // Đã nuốt: không còn chỗ nào an toàn để báo lỗi
                }
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(cancellationToken)) break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Dừng vòng lặp nền — chờ nó kết thúc để không cắt giữa chừng khi app thoát.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_cts is null) return;
        _cts.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
                // Chu kỳ đang chạy bị hủy khi dispose — đã xử lý xong
            }
        }
        _cts.Dispose();
        _cts = null;
        _loop = null;
    }
}
