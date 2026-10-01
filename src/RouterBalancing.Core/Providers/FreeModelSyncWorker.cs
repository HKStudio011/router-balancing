using RouterBalancing.Core.Logging;

namespace RouterBalancing.Core.Providers;

/// <summary>
/// Đồng bộ model free cho các preset đang bật: trễ 60s lần đầu (không đụng DB/network
/// ngay lúc app mở) rồi lặp mỗi 6 giờ — end-user không tự bấm sync nên phải chạy nền
/// (spec provider-free §7). Pattern tái dùng từ <see cref="LogRetentionWorker"/>.
/// </summary>
public sealed class FreeModelSyncWorker : IAsyncDisposable
{
    private readonly IFreeModelSyncService _sync;
    private readonly ILogService _log;
    private readonly TimeSpan _period;
    private readonly TimeSpan _initialDelay;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <param name="sync">Service sync — singleton, chia sẻ với nút manual của UI.</param>
    /// <param name="log">Ghi lỗi mỗi chu kỳ — một lần fail không được giết vòng lặp.</param>
    /// <param name="period">Chu kỳ sync — mặc định 6 giờ (§7).</param>
    /// <param name="initialDelay">Trễ lần đầu — mặc định 60s; test truyền <c>TimeSpan.Zero</c>.</param>
    public FreeModelSyncWorker(
        IFreeModelSyncService sync,
        ILogService log,
        TimeSpan? period = null,
        TimeSpan? initialDelay = null)
    {
        _sync = sync;
        _log = log;
        _period = period ?? TimeSpan.FromHours(6);
        _initialDelay = initialDelay ?? TimeSpan.FromSeconds(60);
    }

    /// <summary>Chạy nền: trễ initialDelay rồi sync + lặp mỗi period — gọi lần 2 chỉ là no-op.</summary>
    public void Start()
    {
        if (_loop is not null) return;
        // Capture CTS local: DisposeAsync có thể đặt _cts = null trước khi Task kịp chạy
        var cts = _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(cts.Token));
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Trễ lần đầu: app vừa mở còn nặng, tránh đụng DB/network lúc startup (§7)
            await Task.Delay(_initialDelay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return; // app đóng trước khi tới lần sync đầu
        }

        using var timer = new PeriodicTimer(_period);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // SyncAllEnabledAsync đã catch lỗi từng provider — catch ở đây cho lỗi cấp
                // hệ thống (DB hỏng...) để vòng lặp không chết
                await _sync.SyncAllEnabledAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Lỗi một chu kỳ không được giết vòng lặp — vẫn thử ở chu kỳ sau.
                // Ghi lỗi cũng có thể ném khi log DB hỏng — nuốt là chấp nhận được vì đây là
                // best-effort diagnostics, nếu không vòng lặp sẽ chết vĩnh viễn
                // (y hệt LogRetentionWorker).
                try
                {
                    _log.Error("Đồng bộ model free định kỳ thất bại.", ex);
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

    /// <summary>Dừng vòng lặp nền — chờ nó kết thúc để không cắt sync giữa chừng khi app thoát.</summary>
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
