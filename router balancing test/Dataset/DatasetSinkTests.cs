using System.Text;
using System.Text.Json.Nodes;
using RouterBalancing.Core.Dataset;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Settings;
using RouterBalancing.Core.Storage;
using router_balancing_test.Engine;

namespace router_balancing_test.Dataset;

/// <summary>
/// DatasetSink (spec §7.6): queue bounded + writer thread nền ghi file JSONL theo ngày UTC,
/// retention purge, fail-open — Capture không bao giờ ném và không làm IO trên thread request.
/// Harness: ManualTimeProvider (đóng băng/đẩy clock) + AppSettingsService thật trên TestDb +
/// thư mục temp ngẫu nhiên mỗi test (luôn xoá ở Dispose).
/// </summary>
public class DatasetSinkTests : IDisposable
{
    /// <summary>"Hôm nay" của phần lớn test — file hôm nay là requests-2026-11-06.jsonl.</summary>
    private static readonly DateTimeOffset DayOne = new(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);

    private readonly TestDb _db = new();
    private readonly AppSettingsService _settings;
    private readonly ManualTimeProvider _time = new(DayOne);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rb-ds-" + Guid.NewGuid().ToString("N"));

    public DatasetSinkTests()
    {
        // TestDb chỉ tạo file trống — phải migrate schema trước khi service đọc AppSettings.
        DbInitializer.Initialize(_db.CreateFactory());
        _settings = new AppSettingsService(_db.CreateFactory());
        _settings.Set(SettingsKeys.DatasetExportEnabled, true);
        _settings.Set(SettingsKeys.DatasetExportPath, _dir);
    }

    public void Dispose()
    {
        _settings.Dispose();
        _db.Dispose();
        // Xoá sạch thư mục test — không để rác trong %TEMP% (brief Task 4).
        Cleanup(_dir);
    }

    private static void Cleanup(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Writer chưa kịp nhả handle — để lại rác temp thay vì làm test cleanup nổ.
        }
    }

    [Fact]
    public async Task Capture_DisabledExport_WritesNothingAndCreatesNoDirectory()
    {
        _settings.Set(SettingsKeys.DatasetExportEnabled, false);
        await using var sink = CreateSink();

        sink.Capture(ChatCapture("req-off"));

        // Gate đọc ở MỖI Capture: tắt → không enqueue, writer chưa từng start →
        // không được tạo thư mục đích (spec §7.6 "tắt thì không tốn gì").
        Assert.False(Directory.Exists(_dir));
        Assert.Equal(0, sink.Stats.RecordsWritten);
        Assert.Equal(0, sink.Stats.SkippedQueueFull);
    }

    [Fact]
    public async Task Capture_TwoCaptures_AppendOneLineEachToTodaysFile()
    {
        await using var sink = CreateSink();

        sink.Capture(ChatCapture("req-1"));
        sink.Capture(ChatCapture("req-2"));

        WaitUntil(() => sink.Stats.RecordsWritten == 2);

        var path = Path.Combine(_dir, "requests-2026-11-06.jsonl");
        Assert.True(File.Exists(path), $"thiếu file {path}");
        // Writer vẫn đang giữ handle append — đọc phải qua ReadAllLinesShared (xem helper).
        var lines = ReadAllLinesShared(path);
        Assert.Equal(2, lines.Length);
        // Mỗi dòng là JSON hợp lệ v:1, request_id đúng thứ tự enqueue (channel FIFO).
        for (var i = 0; i < 2; i++)
        {
            var line = (JsonObject)JsonNode.Parse(lines[i])!;
            Assert.Equal(1, line["v"]!.GetValue<int>());
            Assert.Equal($"req-{i + 1}", line["request_id"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Capture_DayRollover_OpensNewFileForNewUtcDay()
    {
        await using var sink = CreateSink();

        sink.Capture(ChatCapture("req-day1"));
        WaitUntil(() => sink.Stats.RecordsWritten == 1);

        // Đẩy clock sang ngày UTC hôm sau → record tiếp theo phải vào file mới.
        _time.AdvanceDays(1);

        sink.Capture(ChatCapture("req-day2"));
        WaitUntil(() => sink.Stats.RecordsWritten == 2);

        var files = Directory.GetFiles(_dir, "requests-*.jsonl");
        Assert.Equal(2, files.Length);
        Assert.Contains(files, f => Path.GetFileName(f) == "requests-2026-11-06.jsonl");
        Assert.Contains(files, f => Path.GetFileName(f) == "requests-2026-11-07.jsonl");
    }

    [Fact]
    public async Task Constructor_SameDayExistingFile_AppendsDoesNotTruncate()
    {
        // File cùng ngày đã có một dòng của "phiên chạy trước" — sink mới phải append, không truncate.
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "requests-2026-11-06.jsonl");
        File.WriteAllText(path, "{\"v\":1,\"request_id\":\"req-old\"}\n");

        await using var sink = CreateSink();
        sink.Capture(ChatCapture("req-new"));
        WaitUntil(() => sink.Stats.RecordsWritten == 1);

        // Writer vẫn đang giữ handle append — đọc phải qua ReadAllLinesShared (xem helper).
        var lines = ReadAllLinesShared(path);
        Assert.Equal(2, lines.Length);
        Assert.Contains("req-old", lines[0]);
        Assert.Contains("req-new", lines[1]);
    }

    [Fact]
    public async Task Purge_RetentionZero_KeepsAllFiles()
    {
        // Retention 0 (mặc định) = giữ mãi — không purge dù file đã cũ hơn một năm.
        Directory.CreateDirectory(_dir);
        var ancientFile = Path.Combine(_dir, "requests-2025-01-01.jsonl");
        File.WriteAllText(ancientFile, "{\"v\":1}\n");

        await using var sink = CreateSink();
        sink.Capture(ChatCapture("req-1"));
        WaitUntil(() => sink.Stats.RecordsWritten == 1);

        Assert.True(File.Exists(ancientFile), "retention 0 không được xoá file nào");
    }

    [Fact]
    public async Task Purge_RetentionThreeDays_DeletesOlderFiles()
    {
        // Brief: file requests-2026-11-01, clock 2026-11-07, retention 3 → file cũ bị xoá.
        _time.SetUtcNow(new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.Zero));
        _settings.Set(SettingsKeys.DatasetRetentionDays, 3);
        Directory.CreateDirectory(_dir);
        var oldFile = Path.Combine(_dir, "requests-2026-11-01.jsonl");
        File.WriteAllText(oldFile, "{\"v\":1}\n");
        // Biên retention: age 2 ngày < 3 → phải CÒN (giữ đúng N ngày gần nhất kể cả hôm nay).
        var boundaryFile = Path.Combine(_dir, "requests-2026-11-05.jsonl");
        File.WriteAllText(boundaryFile, "{\"v\":1}\n");

        await using var sink = CreateSink();
        sink.Capture(ChatCapture("req-1"));
        WaitUntil(() => sink.Stats.RecordsWritten == 1);

        Assert.False(File.Exists(oldFile), "file cũ hơn retention phải bị purge khi writer start");
        Assert.True(File.Exists(boundaryFile), "file trong cửa sổ retention không được xoá");
        Assert.True(File.Exists(Path.Combine(_dir, "requests-2026-11-07.jsonl")));
    }

    [Fact]
    public async Task Capture_RecordTooLarge_IncrementsSkippedTooLarge_StillServing()
    {
        await using var sink = CreateSink();

        // Body vượt cap 1MB ngay 1 byte → builder skip TooLarge (spec §7.3/§7.6).
        var bigBody = new byte[DatasetRecordBuilder.MaxFieldBytes + 1];
        sink.Capture(new DatasetCapture(
            RequestId: "req-big",
            Endpoint: ProxyEndpoint.Chat,
            FinishedAt: DayOne.AddHours(1),
            ClientBody: bigBody,
            CompletionJson: ValidCompletion,
            CompletionComplete: true,
            Provider: "openrouter",
            Account: "acct-1",
            ModelUpstream: "gpt-x-2026",
            ModelRequested: null,
            Mode: null,
            ClientKeyId: null,
            PromptTokens: null,
            CompletionTokens: null,
            Lane: DatasetLane.Plain,
            ToolsRewritten: false));

        WaitUntil(() => sink.Stats.SkippedTooLarge == 1);
        Assert.Equal(0, sink.Stats.RecordsWritten);

        // Fail-open: record to không được giết writer — capture hợp lệ tiếp theo vẫn ghi.
        sink.Capture(ChatCapture("req-after"));
        WaitUntil(() => sink.Stats.RecordsWritten == 1);
    }

    [Fact]
    public async Task Capture_QueueFull_DropsAndIncrementsSkippedQueueFull()
    {
        // Writer "bị chặn": đích là một FILE (không phải thư mục) → CreateDirectory ném ngay khi
        // writer start, TRƯỚC khi consume record nào → channel không ai rút hàng.
        var destFile = Path.Combine(_dir, "blocked");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(destFile, "not a directory");
        _settings.Set(SettingsKeys.DatasetExportPath, destFile);

        await using var sink = CreateSink();

        // 129 = QueueDepthLimit nội bộ của sink (128) + 1 — capture cuối phải bị drop + đếm.
        const int captures = 129;
        for (var i = 0; i < captures; i++)
        {
            sink.Capture(ChatCapture($"req-{i}")); // không được ném bất kể channel đầy
        }

        WaitUntil(() => sink.Stats.SkippedQueueFull >= 1);
        Assert.Equal(0, sink.Stats.RecordsWritten);
    }

    [Fact]
    public async Task Capture_DestinationUnwritable_LogsOnceAndStopsWriting()
    {
        // Review Focus 5: đích là một file → Directory.CreateDirectory ném; log Error đúng MỘT lần
        // (MaxIoErrorLog), không spam mỗi dòng, và request path không ném.
        var log = new CapturingLog();
        var destFile = Path.Combine(_dir, "blocked");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(destFile, "not a directory");
        _settings.Set(SettingsKeys.DatasetExportPath, destFile);

        await using var sink = CreateSink(log);

        for (var i = 0; i < 5; i++)
        {
            sink.Capture(ChatCapture($"req-{i}"));
        }

        WaitUntil(() => log.ErrorCount == 1);
        Assert.Equal(1, log.ErrorCount); // đúng một lần cho điều kiện lỗi — không spam mỗi dòng
        Assert.Contains("DatasetSink", log.FirstError); // lỗi là của sink, không phải thành phần khác
        Assert.Equal(0, sink.Stats.RecordsWritten);
    }

    [Fact]
    public async Task Capture_StatsRecordsWrittenAndBytesWrittenIncrease()
    {
        await using var sink = CreateSink();

        sink.Capture(ChatCapture("req-1"));
        WaitUntil(() => sink.Stats.RecordsWritten == 1);
        var afterFirst = sink.Stats;
        Assert.True(afterFirst.BytesWritten > 0, "BytesWritten phải tăng sau dòng đầu");

        sink.Capture(ChatCapture("req-2"));
        WaitUntil(() => sink.Stats.RecordsWritten == 2);

        Assert.True(sink.Stats.BytesWritten > afterFirst.BytesWritten, "BytesWritten phải tăng dần");
    }

    [Fact]
    public async Task Sink_RecordsWrittenCounterCountsLineNotCapture()
    {
        await using var sink = CreateSink();

        // CompletionJson null → builder skip Empty: capture được nhận nhưng KHÔNG tạo dòng —
        // RecordsWritten đếm DÒNG GHI, không đếm số capture (spec §7.6).
        sink.Capture(ChatCapture("req-skip", completionJson: null));
        WaitUntil(() => sink.Stats.SkippedEmpty == 1);

        Assert.Equal(0, sink.Stats.RecordsWritten);
    }

    private DatasetSink CreateSink(CapturingLog? log = null) =>
        new(_settings, (ILogService?)log ?? new NullLog(), _time);

    /// <summary>Body chat tối thiểu hợp lệ cho builder (messages có content).</summary>
    private const string ValidBody = """{"model":"m","messages":[{"role":"user","content":"hi"}]}""";

    /// <summary>Cả object chat.completion — shape CompletionJson của endpoint chat (spec §7.3).</summary>
    private const string ValidCompletion =
        """{"id":"1","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"hello"},"finish_reason":"stop"}],"usage":{"prompt_tokens":2,"completion_tokens":3}}""";

    /// <summary>
    /// Capture chat hợp lệ; truyền <c>completionJson: null</c> tường minh để tạo record bị builder
    /// skip Empty (test counter).
    /// </summary>
    private DatasetCapture ChatCapture(string requestId, string? completionJson = ValidCompletion) => new(
        RequestId: requestId,
        Endpoint: ProxyEndpoint.Chat,
        FinishedAt: DayOne.AddHours(1),
        ClientBody: Encoding.UTF8.GetBytes(ValidBody),
        CompletionJson: completionJson,
        CompletionComplete: true,
        Provider: "openrouter",
        Account: "acct-1",
        ModelUpstream: "gpt-x-2026",
        ModelRequested: null,
        Mode: null,
        ClientKeyId: null,
        PromptTokens: 2,
        CompletionTokens: 3,
        Lane: DatasetLane.Plain,
        ToolsRewritten: false);

    /// <summary>
    /// Đọc file JSONL mà writer của sink VẪN đang giữ handle append: Windows yêu cầu share mode
    /// của reader phải cho phép access Write của handle đã có (đo thực tế: FileShare.None/Read đều
    /// bị ACCESS_DENIED, chỉ ReadWrite mở được) — File.ReadAllLines (Share.None) không dùng được.
    /// </summary>
    private static string[] ReadAllLinesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Writer chạy nền — poll điều kiện (counter/file) thay vì sleep cố định.</summary>
    private static void WaitUntil(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 >= deadline)
            {
                throw new TimeoutException($"Điều kiện test không đạt sau {timeoutMs}ms.");
            }

            Thread.Sleep(10);
        }
    }

    /// <summary>
    /// Log bắt sự kiện — thread-safe vì writer task ghi, test thread đọc (pattern CapturingLog
    /// trong ProxyRequestHandlerTests, thêm lock vì hai chiều thật sự chạy song song ở đây).
    /// </summary>
    private sealed class CapturingLog : ILogService
    {
        private readonly object _gate = new();
        private readonly List<string> _errors = [];

        public int ErrorCount
        {
            get { lock (_gate) return _errors.Count; }
        }

        public string? FirstError
        {
            get { lock (_gate) return _errors.Count > 0 ? _errors[0] : null; }
        }

        public event Action<LogEntry>? LogAdded { add { } remove { } }

        public void Write(LogEntry entry)
        {
            if (entry.Severity == LogSeverity.Error)
            {
                lock (_gate) _errors.Add(entry.Message);
            }
        }

        public void Info(string message, LogCategory category = LogCategory.App) { }
        public void Warn(string message, LogCategory category = LogCategory.App) { }
        public void Error(string message, Exception? exception = null, LogCategory category = LogCategory.App) =>
            Write(new LogEntry { Severity = LogSeverity.Error, Category = category, Message = message });
        public void LogRequestUsage(string? requestId, long? clientKeyId, int promptTokens, int completionTokens) { }
        public IReadOnlyList<LogEntry> Query(LogQuery query) => [];
        public int Count(LogQuery query) => 0;
    }
}
