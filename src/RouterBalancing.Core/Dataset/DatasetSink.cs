using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Settings;

namespace RouterBalancing.Core.Dataset;

/// <summary>
/// Ống export dataset JSONL (spec §7.6): <see cref="Capture"/> trên thread request chỉ đo size +
/// đẩy tham chiếu vào channel bounded — một writer task nền build dòng qua
/// <see cref="DatasetRecordBuilder"/> và append vào file thường/large/payload theo ngày UTC của FinishedAt:
/// <c>requests(-large|-payload)-YYYY-MM-DD.jsonl</c>, xoay ngày + purge theo retention.
/// Mọi lỗi chỉ log (fail-open, pattern <c>ApiMonitorStore.SafeLog</c>) — không bao giờ ném ra
/// caller, request đang phục vụ chạy bình thường dù đĩa hỏng. Lỗi IO đặt sink vào chế độ tắt:
/// <c>Capture</c> return ngay, không enqueue; lưu Settings (bất kỳ key nào) bật lại writer và
/// reset giới hạn log-một-lần cho từng điều kiện lỗi (spec §7.6).
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
    private int _skippedSynthetic;
    private int _largeRecordsWritten;
    private int _payloadRecordsWritten;
    private int _externalizedPayloadCount;
    private long _externalizedPayloadBytes;
    private long _bytesWritten;

    /// <summary>Tổng byte đang nằm chờ trong queue — thêm ở Capture, trừ khi writer đọc.</summary>
    private long _pendingBytes;

    // Trạng thái file — chỉ writer task chạm (DisposeAsync không tự đóng: nó đợi writer thoát).
    private DateOnly _currentDay;
    private StreamWriter? _writer;
    private StreamWriter? _largeWriter;
    private StreamWriter? _payloadWriter;
    private string? _currentFile;
    private string? _currentDirectory;
    private bool _storageReady;

    // Capture đã dequeue nhưng chưa mở được file phải được giữ lại để replay sau Settings recovery.
    private DatasetCapture? _pendingCapture;

    // Chế độ tắt sau lỗi IO — spec §7.6: "tắt cho tới khi Settings được lưu lại". Capture chạy trên
    // thread request nên hai cờ này truy cập qua Volatile. _disabled do writer đặt, do
    // OnSettingsChanged (thread gọi Set) xoá; _disposed do DisposeAsync đặt và không bao giờ xoá.
    private bool _disabled;
    private bool _disposed;

    // Mỗi điều kiện lỗi một bộ đếm riêng, cùng cap MaxIoErrorLog (spec §7.6 "mỗi loại lỗi").
    // OnSettingsChanged reset cả ba để điều kiện lỗi còn tồn tại được log lại đúng một lần mới.
    private int _openErrorLogs;
    private int _purgeErrorLogs;
    private int _recordErrorLogs;

    /// <summary>
    /// Tạo sink — writer task khởi tạo lười ở capture đầu tiên thành công, nên export tắt
    /// từ đầu thì không tốn thread, không tạo thư mục (spec §7.6 "tắt thì không tốn gì").
    /// </summary>
    /// <param name="settings">Nguồn gate bật/tắt + đường dẫn đích + retention — đọc tại thời điểm dùng.</param>
    /// <param name="log">Log service — mọi lỗi chỉ về đây, không ra caller (contract fail-open).</param>
    /// <param name="time">Clock tương thích với caller; ngày của record được quyết định bởi FinishedAt.</param>
    public DatasetSink(IAppSettingsService settings, ILogService log, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(time);

        _settings = settings;
        _log = log;

        // FullMode.Wait (KHÔNG phải DropNewest): channel đầy báo về caller qua TryWrite=false
        // để sink tự đếm SkippedQueueFull — drop im lặng thì không có số nào cho UI.
        _channel = Channel.CreateBounded<DatasetCapture>(new BoundedChannelOptions(QueueDepthLimit)
        {
            FullMode = BoundedChannelFullMode.Wait,
        });

        // Spec §7.6 bắt buộc: lỗi IO chỉ tắt sink "cho tới khi Settings được lưu lại" — mọi lần
        // lưu settings (kể cả key khác) là lệnh chạy lại. Unsubscribe ở DisposeAsync, không rò
        // handler qua AppSettingsService (tuổi thọ bằng app).
        _settings.SettingsChanged += OnSettingsChanged;
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
        Volatile.Read(ref _skippedSynthetic),
        Interlocked.Read(ref _bytesWritten),
        _currentFile,
        Volatile.Read(ref _largeRecordsWritten),
        Volatile.Read(ref _payloadRecordsWritten),
        Volatile.Read(ref _externalizedPayloadCount),
        Interlocked.Read(ref _externalizedPayloadBytes));

    /// <inheritdoc />
    public void Capture(DatasetCapture capture)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(capture);

            // Sink đã tắt vì lỗi IO (chờ Settings được lưu lại) hoặc đã dispose: return NGAY —
            // không enqueue và không đếm SkippedQueueFull. Hàng đợi "đầy" khi writer không rút là
            // trạng thái không có thật, đếm vào sẽ cho UI số vô nghĩa (review ruling round 1);
            // sau dispose channel complete, mọi counter phải đứng yên.
            if (Volatile.Read(ref _disabled) || Volatile.Read(ref _disposed))
            {
                return;
            }

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
            // No-op hoá mọi Capture sau dispose (kể cả SkippedQueueFull) + cắt subscription để
            // AppSettingsService (sống bằng app) không giữ handler của sink đã chết.
            Volatile.Write(ref _disposed, true);
            _settings.SettingsChanged -= OnSettingsChanged;

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

    /// <summary>
    /// Khởi tạo writer task lười — thread-safe qua lock. Cho phép KHỞI ĐỘNG LẠI khi task trước đã
    /// completed (fail IO + Settings được lưu lại): reset field dưới lock rồi tạo task mới. Nếu chỉ
    /// early-return vì field khác null thì sink chết vĩnh viễn sau lỗi IO đầu tiên (review ruling).
    /// </summary>
    private void EnsureStarted()
    {
        lock (_startGate)
        {
            if (_writerTask is { IsCompleted: true })
            {
                _writerTask = null;
            }

            if (_writerTask is not null)
            {
                return;
            }

            _writerTask = Task.Run(WriterLoopAsync);
        }
    }

    /// <summary>
    /// Settings được lưu lại (bất kể key nào) = lệnh "chạy lại" của spec §7.6: xoá cờ tắt và reset
    /// giới hạn log-one-per-condition để điều kiện lỗi (nếu đĩa vẫn hỏng) được log lại đúng MỘT lần
    /// mới. Writer khởi động lại lười ở Capture kế tiếp qua <see cref="EnsureStarted"/> — save không
    /// tự làm IO trên thread gọi Set.
    /// </summary>
    private void OnSettingsChanged()
    {
        Volatile.Write(ref _disabled, false);
        Volatile.Write(ref _openErrorLogs, 0);
        Volatile.Write(ref _purgeErrorLogs, 0);
        Volatile.Write(ref _recordErrorLogs, 0);
    }

    /// <summary>
    /// Vòng writer: kiểm tra thư mục đích TRƯỚC khi đọc để lỗi quyền/đường dẫn không nuốt mất
    /// record đang chờ. Ngày file và writer được chọn sau khi đọc, từ FinishedAt của từng record.
    /// </summary>
    private async Task WriterLoopAsync()
    {
        // Task mới = nỗ lực chạy lại (lần đầu hoặc sau recovery qua Settings): xoá cờ tắt cũ
        // TRƯỚC khi vòng lặp bắt đầu để tiến trình fail mới sau đó set lại nhất quán.
        Volatile.Write(ref _disabled, false);

        try
        {
            while (true)
            {
                if (!EnsureDestinationDirectory())
                {
                    // _disabled đã được đặt TRƯỚC khi log — Capture mới return ngay cho tới khi
                    // Settings được lưu lại; record đang chờ vẫn nằm trong channel.
                    break;
                }

                DatasetCapture capture;
                if (_pendingCapture is { } pendingCapture)
                {
                    // Replay capture bị lỗi open TRƯỚC mọi record trong channel để giữ thứ tự.
                    capture = pendingCapture;
                    _pendingCapture = null;
                }
                else if (!_channel.Reader.TryRead(out var dequeuedCapture))
                {
                    // Chưa có record: đợi tới khi có; false = channel đã complete → thoát vòng.
                    if (!await _channel.Reader.WaitToReadAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    continue;
                }
                else
                {
                    capture = dequeuedCapture;
                }

                Process(capture);

                // Write failure LẶP lại đặt _disabled trong Process (đĩa hỏng dai dẳng) — dừng vòng
                // ở đây; hàng đợi giữ nguyên record chưa xử lý cho nỗ lực sau khi Settings lưu.
                if (Volatile.Read(ref _disabled))
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            // Writer task không được chết im lặng: log rồi thoát an toàn — request path không liên quan.
            SafeLog("writer loop", ex);
        }
        finally
        {
            CloseFiles();
        }
    }

    /// <summary>
    /// Xử lý một record trên thread writer: build dòng JSONL (parse/SHA-256 — việc nặng, không chạy
    /// trên thread request) rồi append vào file thường hoặc large theo phân loại của builder.
    /// Mọi exception đều log + drop record.
    /// </summary>
    private void Process(DatasetCapture capture)
    {
        try
        {
            // Record đã rời queue — trừ byte đang chờ (cùng công thức với Capture để counter về 0).
            // Nằm TRONG try: RawSize deref các field của capture, mà DatasetCapture là positional
            // record không validate ctor (caller có thể đưa null! / refactor tuột) — mọi lỗi input
            // bệnh hoạn phải đi đúng nhánh catch bên dưới, không được nổ lên writer loop
            // (review Important #1: một NRE ngoài try giết writer và cả record đang chờ trong queue).
            Interlocked.Add(ref _pendingBytes, -RawSize(capture));

            var recordDay = DateOnly.FromDateTime(capture.FinishedAt.UtcDateTime);
            if (!EnsureStorageForDay(recordDay))
            {
                return;
            }

            var result = DatasetRecordBuilder.Build(capture);
            if (result.Skip is DatasetSkipReason reason)
            {
                CountSkip(reason);
                InvokeChanged();
                return;
            }

            var exportId = Guid.NewGuid().ToString("N");
            var transformed = DatasetPayloadTransformer.Transform(capture, exportId);
            if (transformed is not null)
            {
                // V2 thay hoàn toàn đường v1: không fallback hay ghi trùng sang large khi có base64 large.
                WritePayload(capture, transformed, exportId, recordDay);
            }
            else
            {
                var fileName = result.IsLargeRecord
                    ? DatasetPaths.LargeFileName(recordDay)
                    : DatasetPaths.FileName(recordDay);
                var path = Path.Combine(_currentDirectory!, fileName);
                StreamWriter writer;
                try
                {
                    if (result.IsLargeRecord)
                    {
                        writer = _largeWriter ??= OpenWriter(path);
                    }
                    else
                    {
                        writer = _writer ??= OpenWriter(path);
                    }
                }
                catch (Exception ex)
                {
                    // Open khác write: capture chưa có nơi ghi nên phải giữ nguyên để replay, không drop.
                    // Cộng lại pending bytes vì capture vẫn chiếm RAM dù đã rời channel.
                    _pendingCapture = capture;
                    Interlocked.Add(ref _pendingBytes, RawSize(capture));
                    _storageReady = false; // lần retry phải resolve lại path người dùng có thể vừa sửa
                    Volatile.Write(ref _disabled, true);
                    LogOnce(ref _openErrorLogs, "open daily file", ex);
                    InvokeChanged();
                    return;
                }

                // LF cố định — chuẩn JSONL, không phụ thuộc Environment.NewLine (\r\n trên Windows).
                writer.Write(result.JsonLine!);
                writer.Write('\n');
                _currentFile = path;
                Interlocked.Add(ref _bytesWritten, result.JsonLine!.Length + 1L); // +1: newline vừa ghi
                if (result.IsLargeRecord)
                {
                    Interlocked.Increment(ref _largeRecordsWritten);
                }

                // Publish RecordsWritten cuối cùng: test/UI thấy counter mới thì cùng lượt đã có path + stats.
                Interlocked.Increment(ref _recordsWritten);
            }
        }
        catch (Exception ex)
        {
            // Build có thể ném với input JSON bệnh hoạn ngoài phạm vi catch của nó (finding Task 3),
            // Write ném khi đĩa lỗi (đầy/khoá). Lỗi ĐẦU TIÊN: log một lần + drop record, writer
            // chạy tiếp. Cùng điều kiện LẶP lại (đã chạm cap log) = hỏng hóc dai dẳng: đặt _disabled
            // — chờ Settings được lưu lại để chạy lại (review ruling round 1, spec §7.6).
            // Volatile.Read: OnSettingsChanged (thread khác) có thể vừa reset bộ đếm về 0.
            if (Volatile.Read(ref _recordErrorLogs) >= MaxIoErrorLog)
            {
                Volatile.Write(ref _disabled, true);
            }

            LogOnce(ref _recordErrorLogs, "write record", ex);
        }

        InvokeChanged();
    }

    /// <summary>
    /// Ghi source documents và binary bằng sibling temp + rename, rồi mới append manifest. Nếu bất kỳ
    /// artifact nào lỗi, exception đi qua <see cref="Process"/> và record đó chưa chạm tới payload JSONL.
    /// </summary>
    private void WritePayload(
        DatasetCapture capture,
        DatasetPayloadTransformResult transformed,
        string exportId,
        DateOnly recordDay)
    {
        WritePayloadArtifact(
            DatasetPaths.PayloadRelativePath(recordDay, exportId, "request.json"),
            JsonSerializer.SerializeToUtf8Bytes(transformed.RequestDocument));
        WritePayloadArtifact(
            DatasetPaths.PayloadRelativePath(recordDay, exportId, "completion.json"),
            JsonSerializer.SerializeToUtf8Bytes(transformed.CompletionDocument));
        foreach (var file in transformed.Files)
        {
            WritePayloadArtifact(file.RelativePath, file.Content);
        }

        var manifest = DatasetPayloadManifestFactory.Build(capture, transformed, exportId);
        var path = Path.Combine(_currentDirectory!, DatasetPaths.PayloadFileName(recordDay));
        var writer = _payloadWriter ??= OpenWriter(path);
        writer.Write(manifest);
        writer.Write('\n');
        _currentFile = path;
        Interlocked.Add(ref _bytesWritten, Encoding.UTF8.GetByteCount(manifest) + 1L);
        Interlocked.Increment(ref _payloadRecordsWritten);
        Interlocked.Add(ref _externalizedPayloadCount, transformed.Files.Count);
        Interlocked.Add(ref _externalizedPayloadBytes, transformed.Files.Sum(file => file.Content.LongLength));
        Interlocked.Increment(ref _recordsWritten);
    }

    private void WritePayloadArtifact(string relativePath, byte[] content)
    {
        // Manifest chỉ chứa '/' theo format; đổi separator duy nhất tại filesystem boundary của sink.
        var path = Path.Combine(_currentDirectory!, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            // Temp nằm cùng thư mục nên rename là atomic; overwrite=false bảo vệ artifact đã tồn tại.
            File.Move(temporaryPath, path, overwrite: false);
        }
        catch
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception ex)
            {
                // Cleanup best-effort: giữ exception artifact gốc để state machine record-error xử lý.
                SafeLog("delete temporary payload artifact", ex);
            }

            throw;
        }
    }

    /// <summary>Kiểm tra đích trước khi dequeue; file JSONL chỉ mở sau khi biết loại record.</summary>
    private bool EnsureDestinationDirectory()
    {
        if (_storageReady)
        {
            return true;
        }

        try
        {
            Directory.CreateDirectory(DatasetPaths.Resolve(_settings.DatasetExportPath));
            return true;
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _disabled, true);
            LogOnce(ref _openErrorLogs, "open daily file", ex);
            return false;
        }
    }

    /// <summary>
    /// Chuẩn bị thư mục và retention cho ngày FinishedAt. Đổi ngày đóng cả ba writer; từng loại file
    /// được mở lười ở lần ghi đầu tiên để một record không tạo file rỗng ở hai làn còn lại.
    /// </summary>
    private bool EnsureStorageForDay(DateOnly day)
    {
        if (day == _currentDay && _storageReady)
        {
            return true;
        }

        CloseFiles();
        _currentDay = day;
        _storageReady = false;

        try
        {
            var directory = DatasetPaths.Resolve(_settings.DatasetExportPath);
            Directory.CreateDirectory(directory);
            _currentDirectory = directory;
            PurgeOldFiles(directory, day);
            _storageReady = true;
            return true;
        }
        catch (Exception ex)
        {
            // Đặt _disabled TRƯỚC khi log để người quan sát log không race với trạng thái recovery.
            Volatile.Write(ref _disabled, true);
            LogOnce(ref _openErrorLogs, "open daily file", ex);
            return false;
        }
    }

    private static StreamWriter OpenWriter(string path)
    {
        // FileShare.Read cho phép reader đồng thời; reader trên Windows phải cho phép Write của
        // handle này (FileShare.ReadWrite). Append giữ dữ liệu các phiên chạy trước.
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        // UTF8Encoding(false) = không BOM; AutoFlush giữ invariant counter tăng sau khi ghi stream.
        return new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
    }

    /// <summary>
    /// Xoá đúng ba file JSONL và payload day directories cũ hơn retention: giữ đúng <c>retention</c>
    /// ngày gần nhất kể cả ngày record; 0 = giữ mãi. Tên không khớp pattern, artifact hiện tại và
    /// dữ liệu không thuộc dataset không bao giờ bị xoá.
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

        var payloadRoot = Path.Combine(directory, DatasetPaths.PayloadRootDirectoryName);
        if (!Directory.Exists(payloadRoot))
        {
            return;
        }

        foreach (var path in Directory.GetDirectories(payloadRoot))
        {
            try
            {
                if (!DateOnly.TryParseExact(Path.GetFileName(path), "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var payloadDay))
                {
                    continue;
                }

                if (today.DayNumber - payloadDay.DayNumber >= retention)
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (Exception ex)
            {
                LogOnce(ref _purgeErrorLogs, "purge old payload directories", ex);
            }
        }
    }

    /// <summary>Parse ngày từ đúng ba pattern JSONL dataset; false với mọi tên file khác.</summary>
    private static bool TryParseDay(string fileName, out DateOnly day)
    {
        string prefix;
        if (fileName.StartsWith(DatasetPaths.PayloadFileNamePrefix, StringComparison.Ordinal))
        {
            prefix = DatasetPaths.PayloadFileNamePrefix;
        }
        else if (fileName.StartsWith(DatasetPaths.LargeFileNamePrefix, StringComparison.Ordinal))
        {
            prefix = DatasetPaths.LargeFileNamePrefix;
        }
        else if (fileName.StartsWith(DatasetPaths.FileNamePrefix, StringComparison.Ordinal))
        {
            prefix = DatasetPaths.FileNamePrefix;
        }
        else
        {
            day = default;
            return false;
        }

        if (!fileName.EndsWith(DatasetPaths.FileNameExtension, StringComparison.Ordinal))
        {
            day = default;
            return false;
        }

        var datePart = fileName[prefix.Length..^DatasetPaths.FileNameExtension.Length];
        return DateOnly.TryParseExact(datePart, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
    }

    /// <summary>Đóng cả ba writer — chỉ writer task gọi (xuất vòng hoặc xoay ngày).</summary>
    private void CloseFiles()
    {
        try
        {
            _writer?.Dispose();
        }
        catch (Exception ex)
        {
            // Vẫn phải đóng writer large kể cả khi writer thường lỗi lúc dispose.
            SafeLog("close file", ex);
        }
        finally
        {
            _writer = null;
        }

        try
        {
            _largeWriter?.Dispose();
        }
        catch (Exception ex)
        {
            SafeLog("close large file", ex);
        }
        finally
        {
            _largeWriter = null;
        }

        try
        {
            _payloadWriter?.Dispose();
        }
        catch (Exception ex)
        {
            SafeLog("close payload file", ex);
        }
        finally
        {
            _payloadWriter = null;
        }

        // CurrentFile là file ghi gần nhất, không phải handle đang mở — giữ sau dispose/rotation.
    }

    /// <summary>
    /// Đếm record bị bỏ theo lý do: builder quyết định (spec §7.6), hoặc host quyết định
    /// (<see cref="DatasetSkipReason.Synthetic"/> — response synthesized không đi qua
    /// <see cref="Capture"/> nên <c>ProxyRequestHandler</c> gọi thẳng, spec v2 §7.2).
    /// </summary>
    internal void CountSkip(DatasetSkipReason reason)
    {
        switch (reason)
        {
            case DatasetSkipReason.TooLarge: Interlocked.Increment(ref _skippedTooLarge); break;
            case DatasetSkipReason.Truncated: Interlocked.Increment(ref _skippedTruncated); break;
            case DatasetSkipReason.Empty: Interlocked.Increment(ref _skippedEmpty); break;
            case DatasetSkipReason.Refusal: Interlocked.Increment(ref _skippedRefusal); break;
            case DatasetSkipReason.Synthetic: Interlocked.Increment(ref _skippedSynthetic); break;
        }
    }

    /// <summary>
    /// Kích thước thô của record trong queue — Capture (thêm) và writer (trừ) phải dùng CÙNG công
    /// thức để counter pending về 0. char ≈ byte với JSON ASCII nên xấp xỉ là đủ cho guard RAM.
    /// Null-guard cho ClientBody: positional record, ctor không kiểm tra — kích thước chỉ là con số
    /// bảo vệ RAM nên coi record thiếu field như 0 byte (record hỏng sẽ bị skip/throw ở bước sau).
    /// </summary>
    private static long RawSize(DatasetCapture capture) =>
        (capture.ClientBody?.LongLength ?? 0L) + (capture.CompletionJson?.Length ?? 0);

    /// <summary>
    /// Log tối đa <see cref="MaxIoErrorLog"/> lần cho MỖI điều kiện lỗi — không spam mỗi dòng.
    /// Check bằng Volatile.Read: OnSettingsChanged (thread gọi Set) reset bộ đếm về 0, và target
    /// ARM (Android/iOS) là weak memory model — read thường có thể thấy giá trị cũ trước reset.
    /// </summary>
    private void LogOnce(ref int counter, string operation, Exception ex)
    {
        if (Volatile.Read(ref counter) < MaxIoErrorLog)
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
