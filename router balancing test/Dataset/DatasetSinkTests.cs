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
        // Guard byte của queue (spec §7.6 "chặn theo tổng byte 32MB"): raw size của MỘT capture đã
        // vượt 32MB → Capture drop tại chỗ + đếm SkippedQueueFull, không enqueue — deterministic,
        // không phụ thuộc tốc độ writer. Kịch bản cũ "đẩy 129 capture khi writer kẹt" không còn
        // sau ruling recovery: writer chết vì IO đặt _disabled và Capture return ngay khi disabled,
        // nên không còn hàng-đợi-đày quan sát được qua depth. Đường recovery có test riêng.
        await using var sink = CreateSink();

        var hugeBody = new byte[33 * 1024 * 1024]; // trên QueueByteLimit nội bộ (32MB) một chút
        sink.Capture(new DatasetCapture(
            RequestId: "req-huge",
            Endpoint: ProxyEndpoint.Chat,
            FinishedAt: DayOne.AddHours(1),
            ClientBody: hugeBody,
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

        Assert.Equal(1, sink.Stats.SkippedQueueFull); // đếm ĐỒNG BỘ trên thread gọi Capture
        Assert.Equal(0, sink.Stats.RecordsWritten);

        // Fail-open: record quá khổ queue không giết sink — capture bình thường vẫn ghi.
        sink.Capture(ChatCapture("req-normal"));
        WaitUntil(() => sink.Stats.RecordsWritten == 1);
    }

    [Fact]
    public async Task SettingsSaveAfterIoFailure_ResumesWriting()
    {
        // Spec §7.6 (binding): lỗi IO tắt sink "cho tới khi Settings được lưu lại" — save phải
        // chạy lại writer và hàng đợi cũ (record chưa ghi) phải được drain.
        var log = new CapturingLog();
        Directory.CreateDirectory(_dir);
        var blockedDir = Path.Combine(_dir, "dataset");
        File.WriteAllText(blockedDir, "file nằm thay chỗ thư mục đích"); // CreateDirectory ném
        _settings.Set(SettingsKeys.DatasetExportPath, blockedDir);

        await using var sink = CreateSink(log);

        // Capture đầu enqueue + mồi writer; writer fail ở open TRƯỚC khi consume → disabled.
        sink.Capture(ChatCapture("req-first"));
        WaitUntil(() => log.ErrorCount == 1); // EnsureFileForCurrentDay set _disabled trước khi log
        Assert.Equal(0, sink.Stats.RecordsWritten);
        // Open fail trước khi tạo được thư mục đích → chưa có file nào tồn tại (ruling round 1).
        Assert.False(File.Exists(Path.Combine(blockedDir, "requests-2026-11-06.jsonl")));

        // Disabled: Capture phải là no-op — không enqueue, không đếm SkippedQueueFull,
        // dù đẩy nhiều hơn depth limit của hàng đợi.
        for (var i = 0; i < 130; i++)
        {
            sink.Capture(ChatCapture($"req-during-{i}"));
        }

        Assert.Equal(0, sink.Stats.SkippedQueueFull);
        Assert.Equal(0, sink.Stats.RecordsWritten);

        // Fix đích + lưu settings qua service THẬT (Set luôn phát SettingsChanged, key khác cũng
        // được — sink coi mọi save là lệnh chạy lại).
        File.Delete(blockedDir);
        _settings.Set(SettingsKeys.LogRetentionDays, 30);

        // Restart là LƯỜI ở Capture kế tiếp: push 5 capture — nếu Capture đầu tình cờ vướng cửa sổ
        // writer cũ chưa completed thì Capture thứ hai chắc chắn restart; mọi capture đều được
        // enqueue và drain, không mất dòng nào.
        for (var i = 0; i < 5; i++)
        {
            sink.Capture(ChatCapture("req-resume"));
        }

        WaitUntil(() => sink.Stats.RecordsWritten == 6); // req-first (chờ trong queue) + 5 req-resume
        var lines = ReadAllLinesShared(Path.Combine(blockedDir, "requests-2026-11-06.jsonl"));
        Assert.Equal(6, lines.Length);
        Assert.Contains("req-first", lines[0]);
        Assert.All(lines[1..], l => Assert.Contains("req-resume", l));
        Assert.Equal(0, sink.Stats.SkippedQueueFull);
        Assert.Equal(1, log.ErrorCount); // mở lại thành công — không log lỗi thêm
    }

    [Fact]
    public async Task Capture_DisposedSink_CapturesAreNoOps()
    {
        var sink = CreateSink();
        await using (sink)
        {
            sink.Capture(ChatCapture("req-1"));
            WaitUntil(() => sink.Stats.RecordsWritten == 1);
        }

        // Sau dispose channel đã complete — Capture phải là no-op toàn phần: không ném, và đặc biệt
        // KHÔNG đếm SkippedQueueFull (TryWrite false post-dispose không phải hàng đợi đầy thật).
        sink.Capture(ChatCapture("req-late"));
        sink.Capture(ChatCapture("req-later"));

        Assert.Equal(0, sink.Stats.SkippedQueueFull);
        Assert.Equal(1, sink.Stats.RecordsWritten);
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
        // Các capture sau khi disabled return ngay (ruling) — không enqueue nên hàng đợi
        // không bao giờ đầy: SkippedQueueFull phải giữ 0.
        Assert.Equal(0, sink.Stats.SkippedQueueFull);
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

    [Fact]
    public async Task TestCountSkip_Synthetic_IncrementsStatsCounter()
    {
        await using var sink = CreateSink();

        // V2 §7.2: response synthesized của sidecar không đi qua Capture (không tạo record) mà
        // handler gọi CountSkip trực tiếp — counter phải có mặt trong stats và tăng tại chỗ.
        Assert.Equal(0, sink.Stats.SkippedSynthetic);

        sink.CountSkip(DatasetSkipReason.Synthetic);

        Assert.Equal(1, sink.Stats.SkippedSynthetic);
        // Đếm đúng nhóm của nó — không lẫn vào counter của builder hay hàng đợi.
        Assert.Equal(0, sink.Stats.RecordsWritten);
        Assert.Equal(0, sink.Stats.SkippedEmpty);
        Assert.Equal(0, sink.Stats.SkippedQueueFull);
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
