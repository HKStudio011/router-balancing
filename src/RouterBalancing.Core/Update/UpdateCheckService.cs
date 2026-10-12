using RouterBalancing.Core.Logging;

namespace RouterBalancing.Core.Update;

/// <summary>
/// Kết quả một lần kiểm tra update — UI chọn cách phản hồi: toast khi
/// <see cref="UpdateAvailable"/> (hoặc MainLayout qua <see cref="UpdateCheckService.UpdateFound"/>
/// ở lần phát hiện đầu), toast khi <see cref="AlreadyLatest"/>, toast lỗi khi
/// <see cref="Failed"/> với nút Check thủ công; check nền vẫn im lặng.
/// </summary>
public enum UpdateCheckResult
{
    /// <summary>GitHub trả về bản mới hơn phiên bản hiện tại.</summary>
    UpdateAvailable,

    /// <summary>Đã là bản mới nhất — trạng thái badge (nếu có) bị xoá.</summary>
    AlreadyLatest,

    /// <summary>Không kiểm tra được (mất mạng/lỗi GitHub) — giữ nguyên trạng thái.</summary>
    Failed,
}

/// <summary>
/// Kiểm tra update nền: trễ 5s lần đầu rồi lặp mỗi 24 giờ — end-user không tự bấm
/// check nên phải chạy nền. UI đọc badge qua <see cref="IsUpdateAvailable"/> và
/// đăng ký <see cref="UpdateFound"/> để toast đúng 1 lần mỗi chuyển tiếp (không lặp mỗi 24h).
/// Pattern tái dùng từ <see cref="LogRetentionWorker"/>.
/// </summary>
public sealed class UpdateCheckService : IAsyncDisposable
{
    private readonly IAppUpdateService _updates;
    private readonly ILogService _log;
    private readonly bool _isSupported;
    private readonly TimeSpan _initialDelay;

    // Thread-safety theo pattern UpdateCoordinator: loop chạy nền ghi, UI thread đọc
    // property — transition trạng thái dưới _sync, event bắn NGOÀI lock để handler
    // re-entrant không giữ lock.
    private readonly object _sync = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private ReleaseInfo? _availableRelease;

    // Guard chống check chồng lấp (spec §9.4): 24h loop + nút "Kiểm tra cập nhật" của
    // Settings có thể chạy song song — mỗi lần là một HTTP request thừa. Try-acquire
    // không chờ: đang có check chạy thì lần kia trả trạng thái hiện tại ngay.
    private readonly SemaphoreSlim _checkGate = new(1, 1);

    /// <summary>
    /// Tạo service — vòng lặp nền chỉ khởi động khi gọi <see cref="Start"/>.
    /// </summary>
    /// <param name="updates">Dịch vụ kiểm tra/tải update — tự trả <see langword="null"/> khi lỗi hoặc không phải Windows.</param>
    /// <param name="log">Ghi Info khi phát hiện bản mới, Warn khi kiểm tra thất bại.</param>
    public UpdateCheckService(IAppUpdateService updates, ILogService log)
        : this(updates, log, OperatingSystem.IsWindows(), initialDelay: null)
    {
    }

    // Overload nội bộ cho test: guard theo OperatingSystem.IsWindows() trong Start() không
    // kiểm chứng được trên máy test Windows — truyền isSupported=false để test noop;
    // initialDelay=Zero để loop (nếu guard hỏng) chạy check ngay trong lúc test chờ,
    // thay vì phải chờ 5s thật mới thấy hành vi.
    internal UpdateCheckService(
        IAppUpdateService updates,
        ILogService log,
        bool isSupported,
        TimeSpan? initialDelay = null)
    {
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(log);

        _updates = updates;
        _log = log;
        _isSupported = isSupported;
        _initialDelay = initialDelay ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>Release mới nhất được phát hiện — <see langword="null"/> khi chưa có hoặc đã xác minh là bản mới nhất.</summary>
    public ReleaseInfo? AvailableRelease
    {
        get
        {
            lock (_sync)
            {
                return _availableRelease;
            }
        }
    }

    /// <summary>Có bản update khả dụng không — UI hiển thị badge.</summary>
    public bool IsUpdateAvailable
    {
        get
        {
            lock (_sync)
            {
                return _availableRelease is not null;
            }
        }
    }

    /// <summary>Bắn khi trạng thái badge thay đổi — UI re-render.</summary>
    public event Action? StateChanged;

    /// <summary>
    /// Bắn đúng một lần cho mỗi chuyển tiếp không→có (không lặp lại khi 24h sau vẫn báo
    /// cùng bản) — UI toast chỉ hiện một lần.
    /// </summary>
    public event Action<ReleaseInfo>? UpdateFound;

    /// <summary>
    /// Chạy nền: trễ 5s rồi kiểm tra và lặp mỗi 24 giờ — ngoài Windows là noop;
    /// gọi lần 2 chỉ là no-op.
    /// </summary>
    public void Start()
    {
        // Updater là process Windows-only — mobile/mac không có gì để check nên Start noop
        if (!_isSupported || _loop is not null) return;

        // Capture CTS local: DisposeAsync có thể đặt _cts = null trước khi Task kịp chạy
        var cts = _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(cts.Token));
    }

    /// <summary>
    /// Kiểm tra một lần (nút "Kiểm tra cập nhật" trong Settings gọi thẳng) —
    /// thất bại trả <see cref="UpdateCheckResult.Failed"/> và GIỮ nguyên trạng thái:
    /// một lần check lỗi không được xoá badge của bản update đã phát hiện trước đó.
    /// Đã có lần check đang chạy (24h loop + manual chồng lấp, spec §9.4) → trả trạng
    /// thái hiện tại ngay, không gửi HTTP lần thứ hai.
    /// </summary>
    /// <returns>Kết quả lần kiểm tra.</returns>
    public async Task<UpdateCheckResult> CheckOnceAsync()
    {
        // Try-acquire không chờ: đang có check chạy thì không duplicate HTTP — trả
        // trạng thái badge đã biết (Available) hoặc Failed (im lặng, giữ nguyên state)
        if (!_checkGate.Wait(0))
        {
            return AvailableRelease is not null
                ? UpdateCheckResult.UpdateAvailable
                : UpdateCheckResult.Failed;
        }

        try
        {
            return await CheckOnceCoreAsync();
        }
        finally
        {
            _checkGate.Release();
        }
    }

    private async Task<UpdateCheckResult> CheckOnceCoreAsync()
    {
        ReleaseInfo? release;
        try
        {
            release = await _updates.CheckForUpdatesAsync();
        }
        catch (Exception ex)
        {
            // IAppUpdateService contract đã trả null khi lỗi — catch thêm cho impl khác
            // không để exception lọt ra caller (UI không toast lỗi khi check fail)
            _log.Warn($"Update check failed: {ex.Message}");
            return UpdateCheckResult.Failed;
        }

        if (release is null)
        {
            _log.Warn("Update check failed.");
            return UpdateCheckResult.Failed;
        }

        if (!_updates.IsUpdateAvailable(release))
        {
            ReleaseInfo? previous;
            lock (_sync)
            {
                previous = _availableRelease;
                _availableRelease = null;
            }

            // Đã xác minh là bản mới nhất — xoá badge (user có thể đã tự cài bản mới)
            if (previous is not null)
            {
                StateChanged?.Invoke();
            }

            return UpdateCheckResult.AlreadyLatest;
        }

        ReleaseInfo? prior;
        lock (_sync)
        {
            prior = _availableRelease;
            _availableRelease = release;
        }

        var firstDiscovery = prior is null;

        // So sánh theo TagName thay vì record equality: mỗi lần check GitHub deserialize
        // một instance mới (List<ReleaseAsset> so bằng reference) — nếu không, StateChanged
        // vẫn bắn mỗi 24h dù badge không đổi.
        if (prior is null || prior.TagName != release.TagName)
        {
            StateChanged?.Invoke();
        }

        if (firstDiscovery)
        {
            // Info chỉ khi phát hiện lần đầu — tránh lặp dòng log mỗi 24h cho cùng bản
            _log.Info($"Update available: {release.TagName}");
            UpdateFound?.Invoke(release);
        }

        return UpdateCheckResult.UpdateAvailable;
    }

    /// <summary>
    /// Vòng lặp nền: trễ <see cref="_initialDelay"/> rồi check, sau đó lặp mỗi 24 giờ —
    /// một chu kỳ lỗi không được giết vòng lặp, không bao giờ ném ra ngoài.
    /// </summary>
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Trễ lần đầu: app vừa mở còn nặng — tránh hit GitHub ngay lúc startup
            await Task.Delay(_initialDelay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return; // app đóng trước lần check đầu tiên
        }

        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // CheckOnceAsync đã tự catch lỗi network → Failed; catch ở đây cho lỗi
                // cấp hệ thống (handler event ném...) để vòng lặp không chết
                await CheckOnceAsync();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Lỗi một chu kỳ không được giết vòng lặp — vẫn check ở chu kỳ sau.
                // Ghi log cũng có thể ném (log DB hỏng) — nuốt là chấp nhận được vì đây
                // là best-effort diagnostics, nếu không vòng lặp sẽ chết vĩnh viễn
                // (y hệt LogRetentionWorker).
                try
                {
                    _log.Warn($"Update check loop error: {ex.Message}");
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

    /// <summary>Dừng vòng lặp nền — chờ check đang chạy kết thúc để không cắt giữa chừng khi app thoát.</summary>
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
