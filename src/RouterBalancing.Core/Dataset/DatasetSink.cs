using System.Globalization;
using System.Text;
using System.Threading.Channels;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Settings;

namespace RouterBalancing.Core.Dataset;

/// <summary>
/// Ống export dataset JSONL (spec §7.6): <see cref="Capture"/> trên thread request chỉ đo size +
/// đẩy tham chiếu vào channel bounded — một writer task nền build dòng qua
/// <see cref="DatasetRecordBuilder"/> và append vào file theo ngày UTC
/// <c>requests-YYYY-MM-DD.jsonl</c>, xoay ngày + purge theo retention.
/// Mọi lỗi chỉ log (fail-open, pattern <c>ApiMonitorStore.SafeLog</c>) — không bao giờ ném ra
/// caller, request đang phục vụ chạy bình thường dù đĩa hỏng.
/// </summary>
public sealed class DatasetSink : IDatasetSink, IAsyncDisposable
{
    /// <summary>Số record tối đa nằm chờ trong channel (bảo vệ RAM, spec §7.6).</summary>
    private const int QueueDepthLimit = 128;

    /// <summary>
    /// Tổng byte tối đa đang chờ trong queue — không có guard này thì 128 record × 1MB body
    /// là ~128MB RAM (spec §7.6). Soft cap: hai thread có thể cùng vượt qua check một nhịp.
    /// </summary>
    private const long QueueByteLimit = 32 * 1024 * 1024L;

    /// <summary>Mỗi điều kiện lỗi IO chỉ log tối đa bấy nhiêu lần — không spam mỗi dòng (spec §7.6).</summary>
    private const int MaxIoErrorLog = 1;

    /// <summary>Chờ writer drain khi dispose — đĩa đơ không được treo app exit.</summary>
    private const int DisposeTimeoutMs = 5000;

    private readonly IAppSettingsService _settings;
    private readonly ILogService _log;
    private readonly TimeProvider _time;
    private readonly Channel<DatasetCapture> _channel;

    /// <summary>Mutex chỉ cho <see cref="EnsureStarted"/> — tạo writer task đúng một lần.</summary>
    private readonly object _startGate = new();
    private Task? _writerTask;

    // Counter — Interlocked vì Capture (nhiều thread request) và writer task cập nhật song song.
    private int _recordsWritten;
    private int _skippedTooLarge;
    private int _skippedTruncated;
    private int _skippedEmpty;
    private int _skippedRefusal;
    private int _skippedQueueFull;
    private long _bytesWritten;

    /// <summary>Tổng byte đang nằm chờ trong queue — thêm ở Capture, trừ khi writer đọc.</summary>
    private long _pendingBytes;

    // Trạng thái file — chỉ writer task chạm (DisposeAsync không tự đóng: nó đợi writer thoát).
    private DateOnly _currentDay;
    private StreamWriter? _writer;
    private string? _currentFile;

    // Mỗi điều kiện lỗi một bộ đếm riêng, cùng cap MaxIoErrorLog (spec §7.6 "mỗi loại lỗi").
    private int _openErrorLogs;
    private int _purgeErrorLogs;
    private int _recordErrorLogs;

    /// <summary>
    /// Tạo sink — writer task khởi tạo lười ở capture đầu tiên thành công, nên export tắt
    /// từ đầu thì không tốn thread, không tạo thư mục (spec §7.6 "tắt thì không tốn gì").
    /// </summary>
    /// <param name="settings">Nguồn gate bật/tắt + đường dẫn đích + retention — đọc tại thời điểm dùng.</param>
    /// <param name="log">Log service — mọi lỗi chỉ về đây, không ra caller (contract fail-open).</param>
    /// <param name="time">Clock cho quy tắc ngày UTC — inject để test đóng băng/đẩy clock.</param>
    public DatasetSink(IAppSettingsService settings, ILogService log, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(time);

        _settings = settings;
        _log = log;
        _time = time;

        // FullMode.Wait (KHÔNG phải DropNewest): channel đầy báo về caller qua TryWrite=false
        // để sink tự đếm SkippedQueueFull — drop im lặng thì không có số nào cho UI.
        _channel = Channel.CreateBounded<DatasetCapture>(new BoundedChannelOptions(QueueDepthLimit)
        {
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    /// <inheritdoc />
    public event Action Changed = delegate { };

    /// <inheritdoc />
    public DatasetStats Stats => new(
        Volatile.Read(ref _recordsWritten),
        Volatile.Read(ref _skippedTooLarge),
        Volatile.Read(ref _skippedTruncated),
        Volatile.Read(ref _skippedEmpty),
        Volatile.Read(ref _skippedRefusal),
        Volatile.Read(ref _skippedQueueFull),
        Interlocked.Read(ref _bytesWritten),
        _currentFile);

    /// <inheritdoc />
    public void Capture(DatasetCapture capture)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(capture);

            // Gate đọc ở MỖI request — bật/tắt trong Settings hiệu lực ngay, không cần restart (spec §7.6).
            if (!_settings.DatasetExportEnabled)
            {
                return;
            }

            var rawSize = RawSize(capture);

            // Guard byte: tổng đang chờ vượt 32MB → drop ngay thay vì giữ thêm tham chiếu body trong RAM.
            if (Interlocked.Read(ref _pendingBytes) + rawSize > QueueByteLimit)
            {
                Interlocked.Increment(ref _skippedQueueFull);
                InvokeChanged();
                return;
            }

            Interlocked.Add(ref _pendingBytes, rawSize);

            // TryWrite thủ công (không WriteAsync): channel đầy → drop + đếm thay vì chặn thread request.
            if (!_channel.Writer.TryWrite(capture))
            {
                Interlocked.Add(ref _pendingBytes, -rawSize);
                Interlocked.Increment(ref _skippedQueueFull);
                InvokeChanged();
                return;
            }

            EnsureStarted();
        }
        catch (Exception ex)
        {
            // Fail-open: bất kỳ lỗi nào ở entry point cũng không được nổ đường request.
            SafeLog(nameof(Capture), ex);
        }
    }

    /// <summary>Hoàn tất queue, chờ writer ghi nốt + đóng file rồi thoát.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            _channel.Writer.TryComplete();
            if (_writerTask is not null)
            {
                // Chờ writer drain hết record còn lại + tự đóng file ở finally của loop; timeout để
                // đĩa đơ không treo app exit (task bị bỏ rơi — OS thu handle khi process kết thúc).
                var finished = await Task.WhenAny(_writerTask, Task.Delay(DisposeTimeoutMs)).ConfigureAwait(false);
                if (finished != _writerTask)
                {
                    SafeLog("dispose", new TimeoutException($"DatasetSink writer chưa thoát sau {DisposeTimeoutMs}ms."));
                }
            }
        }
        catch (Exception ex)
        {
            SafeLog(nameof(DisposeAsync), ex);
        }
    }

    /// <summary>Khởi tạo writer task lười — đúng một lần, thread-safe qua lock.</summary>
    private void EnsureStarted()
    {
        lock (_startGate)
        {
            if (_writerTask is not null)
            {
                return;
            }

            _writerTask = Task.Run(WriterLoopAsync);
        }
    }

    /// <summary>
    /// Vòng writer: mỗi record xử lý độc lập — kiểm tra ngày/mở file TRƯỚC khi đọc để lỗi đích
    /// không nuốt mất record nào (item chờ vẫn nằm trong channel, sink chỉ chuyển chế độ tắt).
    /// </summary>
    private async Task WriterLoopAsync()
    {
        try
        {
            while (true)
            {
                if (!EnsureFileForCurrentDay())
                {
                    break; // không mở được file — sink chuyển chế độ tắt (spec §7.6)
                }

                if (!_channel.Reader.TryRead(out var capture))
                {
                    // Chưa có record: đợi tới khi có; false = channel đã complete → thoát vòng.
                    if (!await _channel.Reader.WaitToReadAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    // Quay lại kiểm tra ngày LẦN NỮA trước khi đọc — clock có thể đổi trong lúc chờ.
                    continue;
                }

                Process(capture);
            }
        }
        catch (Exception ex)
        {
            // Writer task không được chết im lặng: log rồi thoát an toàn — request path không liên quan.
            SafeLog("writer loop", ex);
        }
        finally
        {
            CloseFile();
        }
    }

    /// <summary>
    /// Xử lý một record trên thread writer: build dòng JSONL (parse/SHA-256 — việc nặng, không chạy
    /// trên thread request) rồi append vào file. Mọi exception đều log + drop record.
    /// </summary>
    private void Process(DatasetCapture capture)
    {
        // Record đã rời queue — trừ byte đang chờ (cùng công thức với Capture để counter về 0).
        Interlocked.Add(ref _pendingBytes, -RawSize(capture));

        try
        {
            var result = DatasetRecordBuilder.Build(capture);
            if (result.Skip is DatasetSkipReason reason)
            {
                CountSkip(reason);
                InvokeChanged();
                return;
            }

            // EnsureFileForCurrentDay đã đảm bảo _writer mở trước khi Process được gọi.
            var writer = _writer;
            if (writer is null)
            {
                return;
            }

            // LF cố định — chuẩn JSONL, không phụ thuộc Environment.NewLine (\r\n trên Windows).
            writer.Write(result.JsonLine!);
            writer.Write('\n');
            Interlocked.Add(ref _bytesWritten, result.JsonLine!.Length + 1L); // +1: newline vừa ghi
            Interlocked.Increment(ref _recordsWritten);
        }
        catch (Exception ex)
        {
            // Build có thể ném với input JSON bệnh hoạn ngoài phạm vi catch của nó (finding Task 3),
            // Write ném khi đĩa lỗi (đầy/khoá). Cả hai: log tối đa một lần + drop record —
            // không bao giờ để writer task chết, không chạm request path.
            LogOnce(ref _recordErrorLogs, "write record", ex);
        }

        InvokeChanged();
    }

    /// <summary>
    /// Đảm bảo file của ngày hiện tại đang mở (append): đổi ngày hoặc lần đầu → đóng file cũ,
    /// purge theo retention, mở file mới. Trả về false khi không tạo được thư mục/mở được file —
    /// caller dừng writer (sink chuyển chế độ tắt, spec §7.6).
    /// </summary>
    private bool EnsureFileForCurrentDay()
    {
        var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        if (today == _currentDay && _writer is not null)
        {
            return true; // Cùng ngày, file vẫn mở — không IO.
        }

        // Đổi ngày (hoặc khởi động lần đầu): purge chạy ở ĐÚNG hai thời điểm này theo spec §7.6.
        CloseFile();
        _currentDay = today;

        try
        {
            var directory = DatasetPaths.Resolve(_settings.DatasetExportPath);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, DatasetPaths.FileName(today));
            // FileShare.Read: cho phép reader đồng thời (bộ nạp dataset, script kiểm tra) đọc file
            // khi app đang chạy — reader vẫn phải mở với share mode cho phép Write của handle này
            // (thực tế Windows: chỉ FileShare.Write/ReadWrite bên đọc mới mở được; Python open()
            // mặc định đủ rộng nên hoạt động). Mở độc quyền sẽ chặn mọi việc xem file giữa chừng.
            // FileMode.Append tự seek cuối file khi append, tạo mới khi chưa có.
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            // UTF8Encoding(false) = không BOM: file JSONL thuần, loader nào cũng đọc được.
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            _currentFile = path;
            PurgeOldFiles(directory, today);
            return true;
        }
        catch (Exception ex)
        {
            // Đích chưa tồn tại và không tạo được / mất quyền ghi: log một lần, sink tắt.
            LogOnce(ref _openErrorLogs, "open daily file", ex);
            return false;
        }
    }

    /// <summary>
    /// Xoá file hàng ngày cũ hơn retention (spec §7.6): giữ đúng <c>retention</c> ngày gần nhất
    /// kể cả hôm nay (age &gt;= retention mới xoá); 0 = giữ mãi, không purge. File không khớp
    /// pattern ngày hoặc chính file đang mở không bao giờ bị xoá.
    /// </summary>
    private void PurgeOldFiles(string directory, DateOnly today)
    {
        var retention = _settings.DatasetRetentionDays;
        if (retention <= 0)
        {
            return; // 0 = giữ mãi — không purge (spec §7.6).
        }

        foreach (var path in Directory.GetFiles(directory, $"{DatasetPaths.FileNamePrefix}*{DatasetPaths.FileNameExtension}"))
        {
            try
            {
                if (!TryParseDay(Path.GetFileName(path), out var fileDay) || path == _currentFile)
                {
                    continue; // tên không khớp pattern ngày hoặc là file đang ghi — không xoá.
                }

                // DayNumber = số ngày kể từ 0001-01-01 — hiệu hai DayNumber là tuổi file (ngày).
                if (today.DayNumber - fileDay.DayNumber >= retention)
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                // Purge best-effort: một file xoá không được (khoá/quyền) không cản file khác,
                // và không giết cả sink — ghi tiếp vẫn là ưu tiên.
                LogOnce(ref _purgeErrorLogs, "purge old files", ex);
            }
        }
    }

    /// <summary>Parse ngày từ tên file <c>requests-YYYY-MM-DD.jsonl</c>; false khi không khớp pattern.</summary>
    private static bool TryParseDay(string fileName, out DateOnly day)
    {
        if (!fileName.StartsWith(DatasetPaths.FileNamePrefix, StringComparison.Ordinal)
            || !fileName.EndsWith(DatasetPaths.FileNameExtension, StringComparison.Ordinal))
        {
            day = default;
            return false;
        }

        var datePart = fileName[DatasetPaths.FileNamePrefix.Length..^DatasetPaths.FileNameExtension.Length];
        return DateOnly.TryParseExact(datePart, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
    }

    /// <summary>Đóng file hiện tại — chỉ writer task gọi (xuất vòng hoặc xoay ngày).</summary>
    private void CloseFile()
    {
        try
        {
            _writer?.Dispose();
        }
        catch (Exception ex)
        {
            // Lỗi khi đóng không được phá vòng writer/rollover.
            SafeLog("close file", ex);
        }
        finally
        {
            _writer = null;
            _currentFile = null;
        }
    }

    /// <summary>Đếm record bị bỏ theo lý do từ builder (spec §7.6).</summary>
    private void CountSkip(DatasetSkipReason reason)
    {
        switch (reason)
        {
            case DatasetSkipReason.TooLarge: Interlocked.Increment(ref _skippedTooLarge); break;
            case DatasetSkipReason.Truncated: Interlocked.Increment(ref _skippedTruncated); break;
            case DatasetSkipReason.Empty: Interlocked.Increment(ref _skippedEmpty); break;
            case DatasetSkipReason.Refusal: Interlocked.Increment(ref _skippedRefusal); break;
        }
    }

    /// <summary>
    /// Kích thước thô của record trong queue — Capture (thêm) và writer (trừ) phải dùng CÙNG công
    /// thức để counter pending về 0. char ≈ byte với JSON ASCII nên xấp xỉ là đủ cho guard RAM.
    /// </summary>
    private static long RawSize(DatasetCapture capture) =>
        capture.ClientBody.LongLength + (capture.CompletionJson?.Length ?? 0);

    /// <summary>Log tối đa <see cref="MaxIoErrorLog"/> lần cho MỖI điều kiện lỗi — không spam mỗi dòng.</summary>
    private void LogOnce(ref int counter, string operation, Exception ex)
    {
        if (counter < MaxIoErrorLog)
        {
            counter++;
            SafeLog(operation, ex);
        }
    }

    /// <summary>
    /// Fire <see cref="Changed"/> với try/catch: subscriber (UI render) nổ không được lan ra
    /// caller — multicast delegate dừng ở subscriber nổ nên phải bọc cả chuỗi (pattern ApiMonitorStore).
    /// </summary>
    private void InvokeChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            SafeLog(nameof(Changed), ex);
        }
    }

    /// <summary>Ghi lỗi ra log — chính log nổ cũng không được phá contract fail-open.</summary>
    private void SafeLog(string operation, Exception ex)
    {
        try
        {
            _log.Error($"DatasetSink: {operation} failed", ex);
        }
        catch
        {
            // intentional swallow — không còn chỗ report nào an toàn hơn (mirror ApiMonitorStore.SafeLog)
        }
    }
}
