using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Server;

namespace RouterBalancing.Core.Update;

/// <summary>
/// Loại lỗi khiến luồng cập nhật dừng giữa chừng — UI chọn thông điệp toast theo loại (spec §11).
/// </summary>
public enum UpdateError
{
    /// <summary>Tải zip thất bại (mất mạng, asset sai...).</summary>
    DownloadFailed,

    /// <summary>Ghi queue-snapshot thất bại.</summary>
    SnapshotFailed,

    /// <summary>Khởi chạy updater thất bại — snapshot bị xoá để tránh restore oan ở lần boot sau.</summary>
    InstallFailed,
}

/// <summary>
/// Thoát ứng dụng sau khi updater đã được khởi chạy — tách interface để Core test được
/// (impl MAUI gọi <c>Application.Current.Quit()</c>, spec §7 bước 5).
/// </summary>
public interface IAppShutdown
{
    /// <summary>Đóng ứng dụng hiện tại.</summary>
    void Quit();
}

/// <summary>
/// Orchestrator luồng cập nhật (spec §6.2/§7): tải zip → chờ in-flight drain về 0
/// (dispatcher đã ngưng nhận request mới qua <see cref="DrainGate"/>) → ghi queue-snapshot
/// → khởi chạy updater → thoát app. Mọi nhánh huỷ/thất bại đều gọi
/// <see cref="DrainGate.CancelDrain"/> để dispatcher không bao giờ park vĩnh viễn.
/// </summary>
public sealed class UpdateCoordinator : IDisposable
{
    private readonly IAppUpdateService _updates;
    private readonly DrainGate _gate;
    private readonly IProxyHost _proxy;
    private readonly IAppShutdown _shutdown;
    private readonly ILogService _log;
    private readonly string _snapshotPath;

    // Thread-safety đơn giản: poll loop chạy nền còn UI đọc property — mọi transition
    // trạng thái đều dưới _sync; Changed/Cancelled/Failed bắn NGOÀI lock để handler
    // re-entrant không giữ lock (handler chỉ đọc state, không ghi).
    private readonly object _sync = new();

    private CancellationTokenSource? _cts;
    private bool _updateInProgress;
    private bool _isFinalizing;
    private int _downloadPercent;
    private int _inFlightCount;

    /// <summary>Tạo coordinator mới.</summary>
    /// <param name="updates">Dịch vụ tải/cài zip update.</param>
    /// <param name="gate">Cờ drain — BeginDrain khi start, CancelDrain khi fail/cancel.</param>
    /// <param name="proxy">Bridge đếm in-flight và ghi queue-snapshot.</param>
    /// <param name="shutdown">Thoát app sau khi updater được khởi chạy.</param>
    /// <param name="log">Ghi Warn khi pipeline fail.</param>
    /// <param name="snapshotPath">Đường dẫn file queue-snapshot (dùng để xoá khi install fail).</param>
    public UpdateCoordinator(IAppUpdateService updates, DrainGate gate, IProxyHost proxy,
        IAppShutdown shutdown, ILogService log, string snapshotPath)
    {
        _updates = updates;
        _gate = gate;
        _proxy = proxy;
        _shutdown = shutdown;
        _log = log;
        _snapshotPath = snapshotPath;
    }

    /// <summary>Có update đang chạy không — UI khoá nút "Cập nhật ngay".</summary>
    public bool UpdateInProgress
    {
        get
        {
            lock (_sync)
            {
                return _updateInProgress;
            }
        }
    }

    /// <summary><see langword="true"/> khi đang snapshot/install — UI ẩn nút Hủy (không huỷ được giữa chừng).</summary>
    public bool IsFinalizing
    {
        get
        {
            lock (_sync)
            {
                return _isFinalizing;
            }
        }
    }

    /// <summary>Tiến độ tải zip 0..100.</summary>
    public int DownloadPercent
    {
        get
        {
            lock (_sync)
            {
                return _downloadPercent;
            }
        }
    }

    /// <summary>Số request còn in-flight — UI hiển thị "Còn N request" trong lúc chờ drain.</summary>
    public int InFlightCount
    {
        get
        {
            lock (_sync)
            {
                return _inFlightCount;
            }
        }
    }

    /// <summary>Bắn khi một property đổi — modal UI re-render.</summary>
    public event Action? Changed;

    /// <summary>Bắn khi update thất bại (đã CancelDrain + reset trạng thái) — UI toast lỗi.</summary>
    public event Action<UpdateError>? Failed;

    /// <summary>Bắn khi người dùng Hủy trước khi finalise — UI toast "Đã hủy cập nhật".</summary>
    public event Action? Cancelled;

    /// <summary>
    /// Chạy pipeline cập nhật: tải zip (báo tiến độ qua <see cref="DownloadPercent"/>), chờ
    /// download xong + in-flight về 0, ghi snapshot, khởi chạy updater rồi thoát app.
    /// Fire-and-forget nội bộ — trả về khi pipeline kết thúc (thành công, thất bại hoặc huỷ).
    /// </summary>
    /// <param name="release">Release cần tải.</param>
    /// <returns>Task hoàn tất khi pipeline kết thúc.</returns>
    /// <exception cref="InvalidOperationException">Khi đã có update đang chạy.</exception>
    public async Task StartUpdateAsync(ReleaseInfo release)
    {
        // Updater chỉ hoạt động trên Windows desktop — mobile/mac không có luồng này
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        CancellationTokenSource cts;
        lock (_sync)
        {
            if (_updateInProgress)
            {
                throw new InvalidOperationException("An update is already in progress.");
            }

            // CTS lần chạy trước đã kết thúc (không còn ai Cancel) — dispose để gỡ registration
            _cts?.Dispose();
            _cts = cts = new CancellationTokenSource();
            _updateInProgress = true;
            _downloadPercent = 0;
        }

        Changed?.Invoke();

        _gate.BeginDrain();
        SetInFlightCount(_proxy.GetTotalInFlight());

        var downloadTask = Task.Run(() => _updates.DownloadUpdateAsync(release, new PercentProgress(this)));
        var pollTask = Task.Run(() => PollUntilReadyAsync(downloadTask, cts.Token));
        await Task.WhenAny(pollTask, Task.Delay(Timeout.Infinite, cts.Token));

        // CancelUpdate/Dispose thắng: canceller đã dọn state (CancelDrain + cờ), chỉ return
        if (cts.IsCancellationRequested)
        {
            return;
        }

        string? zipPath;
        try
        {
            zipPath = await downloadTask;
        }
        catch (Exception ex)
        {
            // Exception không được nuốt: Warn + Failed để UI biết và dispatcher resume
            Fail(UpdateError.DownloadFailed, $"Update download failed: {ex.Message}");
            return;
        }

        if (zipPath is null)
        {
            Fail(UpdateError.DownloadFailed, "Update download failed: no zip file returned.");
            return;
        }

        // Check + set atomic dưới _sync: hoặc CancelUpdate thấy IsFinalizing (no-op),
        // hoặc pipeline thấy !UpdateInProgress (return) — không thể cùng đi tiếp
        lock (_sync)
        {
            if (!_updateInProgress || cts.IsCancellationRequested)
            {
                return;
            }

            _isFinalizing = true;
        }

        Changed?.Invoke();

        if (!await TryWriteSnapshotAsync())
        {
            Fail(UpdateError.SnapshotFailed, "Update aborted: queue snapshot write failed.");
            return;
        }

        if (!await TryInstallAsync())
        {
            // Snapshot không hợp lệ nữa (install fail) — xoá để tránh restore oan ở boot sau
            DeleteSnapshot();
            Fail(UpdateError.InstallFailed, "Update aborted: installer launch failed.");
            return;
        }

        _shutdown.Quit();
    }

    /// <summary>
    /// Hủy update (nút Hủy trong modal) — no-op khi chưa start hoặc đã finalise
    /// (không thể huỷ giữa snapshot/install). Luôn CancelDrain để dispatcher resume ngay.
    /// </summary>
    public void CancelUpdate()
    {
        lock (_sync)
        {
            if (!_updateInProgress || _isFinalizing)
            {
                return;
            }

            // Cancel trong lock: transition trạng thái luôn dưới _sync — không thể
            // tranh với Start lần sau dispose CTS, và pipeline thấy trạng thái nhất quán
            _cts?.Cancel();
            _updateInProgress = false;
        }

        Cancelled?.Invoke();
        _gate.CancelDrain();
        Changed?.Invoke();
    }

    /// <summary>
    /// Teardown: huỷ CTS và giải phóng update đang chờ (nếu có). Không bắn
    /// <see cref="Cancelled"/> — đây không phải hành động người dùng.
    /// </summary>
    public void Dispose()
    {
        bool resume;
        lock (_sync)
        {
            _cts?.Cancel();
            resume = _updateInProgress && !_isFinalizing;
            if (resume)
            {
                _updateInProgress = false;
            }
        }

        if (resume)
        {
            // Đóng giữa chừng vẫn phải mở lại gate — dispatcher không park vĩnh viễn
            _gate.CancelDrain();
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Poll 500ms cho tới khi cả hai gate đóng: download hoàn tất AND in-flight == 0.
    /// Thoát ngay khi <paramref name="token"/> huỷ (CancelUpdate/Dispose đã dọn state trước đó).
    /// </summary>
    private async Task PollUntilReadyAsync(Task downloadTask, CancellationToken token)
    {
        while (true)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            SetInFlightCount(_proxy.GetTotalInFlight());
            if (downloadTask.IsCompleted && InFlightCount == 0)
            {
                return;
            }

            try
            {
                await Task.Delay(500, token);
            }
            catch (OperationCanceledException)
            {
                // Expected khi CancelUpdate/Dispose — không phải lỗi
                return;
            }
        }
    }

    /// <summary>Ghi snapshot qua bridge của proxy; exception → coi như fail (đã log Warn).</summary>
    private async Task<bool> TryWriteSnapshotAsync()
    {
        try
        {
            return await _proxy.TryWriteQueueSnapshotAsync();
        }
        catch (Exception ex)
        {
            _log.Warn($"Queue snapshot write failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Khởi chạy updater; exception → coi như fail (đã log Warn).</summary>
    private async Task<bool> TryInstallAsync()
    {
        try
        {
            return await _updates.InstallUpdateAsync();
        }
        catch (Exception ex)
        {
            _log.Warn($"Update install failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Dừng update vì lỗi: log Warn, bắn <see cref="Failed"/>, MỞ lại gate drain
    /// (dispatcher resume — tuyệt đối không để gate đóng vĩnh viễn) và reset trạng thái.
    /// </summary>
    private void Fail(UpdateError error, string message)
    {
        lock (_sync)
        {
            _updateInProgress = false;
            _isFinalizing = false;
        }

        _log.Warn(message);
        Failed?.Invoke(error);
        _gate.CancelDrain();
        Changed?.Invoke();
    }

    /// <summary>
    /// Xoá snapshot khi install fail — tránh boot sau restore oan queue đã đóng (spec §7 bước 5).
    /// </summary>
    private void DeleteSnapshot()
    {
        try
        {
            File.Delete(_snapshotPath);
        }
        catch (IOException ex)
        {
            _log.Warn($"Cannot delete queue snapshot '{_snapshotPath}': {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Warn($"Cannot delete queue snapshot '{_snapshotPath}': {ex.Message}");
        }
    }

    private void SetInFlightCount(int value)
    {
        lock (_sync)
        {
            if (_inFlightCount == value)
            {
                return;
            }

            _inFlightCount = value;
        }

        Changed?.Invoke();
    }

    private void SetDownloadPercent(int value)
    {
        var clamped = Math.Clamp(value, 0, 100);
        lock (_sync)
        {
            if (_downloadPercent == clamped)
            {
                return;
            }

            _downloadPercent = clamped;
        }

        Changed?.Invoke();
    }

    // IProgress trực tiếp thay vì Progress<T>: Progress<T> post qua SynchronizationContext
    // của luồng tạo — trong test (không có context) report sẽ trễ hoặc đến sai luồng.
    private sealed class PercentProgress(UpdateCoordinator owner) : IProgress<int>
    {
        public void Report(int value) => owner.SetDownloadPercent(value);
    }
}
