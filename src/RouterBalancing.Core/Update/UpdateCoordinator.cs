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

    /// <summary>Không đọc được số request in-flight — không xác minh được drain nên huỷ update.</summary>
    DrainFailed,
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

    // Số thứ tự run — ReleaseDrainAndReset của run cũ chỉ có hiệu lực khi _runId còn khớp;
    // nếu không, release trailing (Fail/Cancel/Dispose chạy SAU khi handler đã Start run mới)
    // sẽ CancelDrain/reset nhầm trạng thái của run #2 (Review Round 2 Minor).
    private int _runId;

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
        int runId;
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
            runId = ++_runId;
            _downloadPercent = 0;
        }

        Changed?.Invoke();

        _gate.BeginDrain();

        // Seed lỗi (DI hỏng, host dừng giữa chừng) không được thoát ra ngoài —
        // nếu không UpdateInProgress kẹt true + gate đóng vĩnh viễn, không có Failed nào báo UI
        int inFlight;
        try
        {
            inFlight = _proxy.GetTotalInFlight();
        }
        catch (Exception ex)
        {
            Fail(UpdateError.DrainFailed, $"Update aborted: cannot read in-flight count: {ex.Message}");
            return;
        }

        SetInFlightCount(inFlight);

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

        bool wrote;
        try
        {
            wrote = await _proxy.TryWriteQueueSnapshotAsync();
        }
        catch (Exception ex)
        {
            // Chi tiết exception vào thẳng message của Fail — chỉ một lần Warn, không nuốt lỗi
            Fail(UpdateError.SnapshotFailed, $"Update aborted: queue snapshot write failed: {ex.Message}");
            return;
        }

        if (!wrote)
        {
            Fail(UpdateError.SnapshotFailed, "Update aborted: queue snapshot write failed.");
            return;
        }

        bool installed;
        try
        {
            installed = await _updates.InstallUpdateAsync();
        }
        catch (Exception ex)
        {
            DeleteSnapshot();
            Fail(UpdateError.InstallFailed, $"Update aborted: installer launch failed: {ex.Message}");
            return;
        }

        if (!installed)
        {
            // Snapshot không hợp lệ nữa (install fail) — xoá để tránh restore oan ở boot sau
            DeleteSnapshot();
            Fail(UpdateError.InstallFailed, "Update aborted: installer launch failed.");
            return;
        }

        try
        {
            _shutdown.Quit();
        }
        catch (Exception ex)
        {
            // Quit lỗi = process vẫn sống: không được để finalise giữ gate đóng vĩnh viễn.
            // Update thật sự đã thành công (updater đã launch) nên chỉ Warn + mở lại gate +
            // reset — không bắn Failed (báo "cập nhật thất bại" sẽ sai sự thật).
            _log.Warn($"Application quit failed after update install: {ex.Message}");
            ReleaseDrainAndReset(runId);
        }
    }

    /// <summary>
    /// Hủy update (nút Hủy trong modal) — no-op khi chưa start, đã finalise hoặc đã huỷ
    /// trước đó. Luôn CancelDrain để dispatcher resume ngay. Handler của <see cref="Cancelled"/>
    /// thấy <see cref="UpdateInProgress"/> còn <see langword="true"/> — Start lại từ handler sẽ throw.
    /// </summary>
    public void CancelUpdate()
    {
        int runId;
        lock (_sync)
        {
            // IsCancellationRequested: cancel thứ hai (handler reentrant/concurrent) là no-op —
            // tránh bắn Cancelled đôi; cts.Cancel cùng lock với guard để transition nhất quán
            var cts = _cts;
            if (!_updateInProgress || _isFinalizing || cts is null || cts.IsCancellationRequested)
            {
                return;
            }

            runId = _runId;
            cts.Cancel();
        }

        Cancelled?.Invoke();
        ReleaseDrainAndReset(runId);
    }

    /// <summary>
    /// Teardown: huỷ CTS và giải phóng update đang chờ (nếu có). Không bắn
    /// <see cref="Cancelled"/> — đây không phải hành động người dùng. Nếu Dispose chạy trong
    /// window finalise (snapshot/install đang chạy) thì pipeline vẫn tiếp tục tới Quit —
    /// app đang teardown, cho phép process thoát sạch; event có thể vẫn fire sau Dispose.
    /// </summary>
    public void Dispose()
    {
        int runId;
        bool resume;
        lock (_sync)
        {
            _cts?.Cancel();
            runId = _runId;
            resume = _updateInProgress && !_isFinalizing;
        }

        if (resume)
        {
            // Đóng giữa chừng vẫn phải mở lại gate — dispatcher không park vĩnh viễn
            ReleaseDrainAndReset(runId);
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

            int total;
            try
            {
                total = _proxy.GetTotalInFlight();
            }
            catch (Exception ex)
            {
                // Poll fault mà bỏ qua thì snapshot proceed khi in-flight chưa chắc == 0 —
                // fail đúng nghĩa (Failed + CancelDrain + reset), không được đi tiếp
                Fail(UpdateError.DrainFailed, $"Update aborted: in-flight poll failed: {ex.Message}");
                return;
            }

            SetInFlightCount(total);
            if (downloadTask.IsCompleted && total == 0)
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

    /// <summary>
    /// Dừng update vì lỗi: huỷ CTS của run, log Warn, bắn <see cref="Failed"/>, MỞ lại
    /// gate drain rồi MỚI reset trạng thái (dispatcher resume — tuyệt đối không để gate
    /// đóng vĩnh viễn). Handler của <see cref="Failed"/> thấy <see cref="UpdateInProgress"/>
    /// còn <see langword="true"/> — Start lại không qua <see cref="Dispose"/> sẽ throw;
    /// nếu handler Dispose rồi Start run mới, release trailing phía sau là no-op nhờ
    /// check <c>runId</c>.
    /// </summary>
    private void Fail(UpdateError error, string message)
    {
        bool active;
        int runId;
        lock (_sync)
        {
            active = _updateInProgress;
            runId = _runId;
            if (active)
            {
                // Huỷ CTS TRƯỚC khi bắn Failed: pipeline (StartUpdateAsync) đang chờ
                // WhenAny sẽ thoát tại check IsCancellationRequested; nếu không, nó rơi vào
                // `await downloadTask` treo vô hạn với _cts còn sống — run cũ có thể
                // finalise chồng lên run mới bắt đầu từ handler (Review Round 2 Important).
                _cts?.Cancel();
            }
        }

        // Warn luôn chạy kể cả run đã bị cancel/dispose dừng trước — mỗi lỗi phải vào log
        _log.Warn(message);
        if (!active)
        {
            return;
        }

        Failed?.Invoke(error);
        ReleaseDrainAndReset(runId);
    }

    /// <summary>
    /// Mở lại gate drain, RỒI reset cờ dưới lock, rồi mới bắn <see cref="Changed"/> —
    /// CancelDrain luôn đứng TRƯỚC khi cờ false, nên handler nào khởi động run mới từ
    /// event/Changed cũng không bị CancelDrain muộn của run cũ mở cửa gate (Finding 1).
    /// No-op khi run đã kết thúc hoặc <paramref name="runId"/> không còn khớp — release
    /// của run cũ không được phá trạng thái run mới (Review Round 2 Minor).
    /// </summary>
    /// <param name="runId">Số thứ tự run muốn release — bắt từ caller dưới cùng lock với state.</param>
    private void ReleaseDrainAndReset(int runId)
    {
        lock (_sync)
        {
            if (!_updateInProgress || _runId != runId)
            {
                return;
            }

            // CancelDrain dưới lock an toàn: subscriber duy nhất là DispatcherLoop.OnWake
            // (SemaphoreSlim.Release — không re-entrant vào _sync); Changed vẫn bắn ngoài lock
            _gate.CancelDrain();
            _updateInProgress = false;
            _isFinalizing = false;
        }

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
