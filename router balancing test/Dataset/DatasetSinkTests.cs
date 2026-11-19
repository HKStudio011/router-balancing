using System.Reflection;
using System.Security.Cryptography;
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
    public async Task RecordLarge_WritesOnlyLargeFile()
    {
        await using var sink = CreateSink();
        var largeContent = new string('x', DatasetRecordBuilder.InlineMaxFieldBytes);

        sink.Capture(ChatCapture("req-large", clientBody: ChatBodyWithContent(largeContent)));
        WaitUntil(() => sink.Stats.RecordsWritten == 1);

        var normalPath = Path.Combine(_dir, DatasetPaths.FileName(new DateOnly(2026, 11, 6)));
        var largePath = Path.Combine(_dir, DatasetPaths.LargeFileName(new DateOnly(2026, 11, 6)));
        Assert.False(File.Exists(normalPath));
        Assert.True(File.Exists(largePath));
        Assert.Equal(1, sink.Stats.LargeRecordsWritten);

        var line = (JsonObject)JsonNode.Parse(Assert.Single(ReadAllLinesShared(largePath)))!;
        Assert.Equal(1, line["v"]!.GetValue<int>());
        Assert.Equal("req-large", line["request_id"]!.GetValue<string>());
        Assert.Equal(largeContent, line["messages"]![0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task MixedRecords_CountsLargeAndTracksLastWrittenFile()
    {
        await using var sink = CreateSink();
        var largeContent = new string('x', DatasetRecordBuilder.InlineMaxFieldBytes);

        sink.Capture(ChatCapture("req-normal"));
        sink.Capture(ChatCapture("req-large", clientBody: ChatBodyWithContent(largeContent)));
        WaitUntil(() => sink.Stats.RecordsWritten == 2);

        var normalPath = Path.Combine(_dir, DatasetPaths.FileName(new DateOnly(2026, 11, 6)));
        var largePath = Path.Combine(_dir, DatasetPaths.LargeFileName(new DateOnly(2026, 11, 6)));
        Assert.Equal(2, sink.Stats.RecordsWritten);
        Assert.Equal(1, sink.Stats.LargeRecordsWritten);
        Assert.Equal(largePath, sink.Stats.CurrentFile); // large written last
        Assert.Contains("req-normal", Assert.Single(ReadAllLinesShared(normalPath)));
        Assert.Contains("req-large", Assert.Single(ReadAllLinesShared(largePath)));
    }

    [Fact]
    public async Task LargeFile_RotationAndRetention_UseFinishedAtDay()
    {
        _time.SetUtcNow(new DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero));
        _settings.Set(SettingsKeys.DatasetRetentionDays, 2);
        Directory.CreateDirectory(_dir);
        var expiredLargePath = Path.Combine(_dir, DatasetPaths.LargeFileName(new DateOnly(2026, 11, 15)));
        File.WriteAllText(expiredLargePath, "{\"v\":1}\n");

        await using var sink = CreateSink();
        var largeContent = new string('x', DatasetRecordBuilder.InlineMaxFieldBytes);
        sink.Capture(ChatCapture("req-large-16", clientBody: ChatBodyWithContent(largeContent),
            finishedAt: new DateTimeOffset(2026, 11, 16, 23, 59, 0, TimeSpan.Zero)));
        WaitUntil(() => sink.Stats.RecordsWritten == 1);

        Assert.True(File.Exists(expiredLargePath), "file một ngày tuổi phải còn ở retention hai ngày");

        sink.Capture(ChatCapture("req-large-17", clientBody: ChatBodyWithContent(largeContent),
            finishedAt: new DateTimeOffset(2026, 11, 17, 0, 1, 0, TimeSpan.Zero)));
        WaitUntil(() => sink.Stats.RecordsWritten == 2);

        var largePath = sink.Stats.CurrentFile;
        Assert.Equal(Path.Combine(_dir, DatasetPaths.LargeFileName(new DateOnly(2026, 11, 17))), largePath);
        Assert.Equal(2, sink.Stats.LargeRecordsWritten);
        Assert.False(File.Exists(expiredLargePath), "file đạt tuổi retention phải bị xoá theo FinishedAt");
        Assert.True(File.Exists(Path.Combine(_dir, DatasetPaths.LargeFileName(new DateOnly(2026, 11, 16)))));
        Assert.False(File.Exists(Path.Combine(_dir, DatasetPaths.FileName(new DateOnly(2026, 11, 16)))));
        Assert.False(File.Exists(Path.Combine(_dir, DatasetPaths.FileName(new DateOnly(2026, 11, 17)))));
    }

    [Fact]
    public async Task WritePayload_WritesSourceDocumentsBinaryThenManifest()
    {
        await using var sink = CreateSink();
        var binary = LargePng();

        sink.Capture(PayloadCapture("req-payload", binary));
        WaitUntil(() => sink.Stats.RecordsWritten == 1);

        var day = new DateOnly(2026, 11, 6);
        var payloadPath = Path.Combine(_dir, DatasetPaths.PayloadFileName(day));
        var manifest = Assert.Single(ReadPayloadManifests(day));
        Assert.Equal(2, manifest["v"]!.GetValue<int>());
        Assert.Equal("req-payload", manifest["request_id"]!.GetValue<string>());
        var exportId = manifest["export_id"]!.GetValue<string>();
        Assert.True(Guid.TryParseExact(exportId, "N", out _));

        var requestRelativePath = manifest["documents"]!["request"]!.GetValue<string>();
        var completionRelativePath = manifest["documents"]!["completion"]!.GetValue<string>();
        Assert.Equal(DatasetPaths.PayloadRelativePath(day, exportId, "request.json"), requestRelativePath);
        Assert.Equal(DatasetPaths.PayloadRelativePath(day, exportId, "completion.json"), completionRelativePath);
        var requestDocumentPath = ArtifactPath(_dir, requestRelativePath);
        var completionDocumentPath = ArtifactPath(_dir, completionRelativePath);
        Assert.True(File.Exists(requestDocumentPath));
        Assert.True(File.Exists(completionDocumentPath));

        var reference = Assert.Single(manifest["payload_refs"]!.AsArray())!;
        var binaryPath = ArtifactPath(_dir, reference["path"]!.GetValue<string>());
        Assert.True(File.Exists(binaryPath));
        Assert.Equal(binary, File.ReadAllBytes(binaryPath));
        var requestDocument = JsonNode.Parse(File.ReadAllText(requestDocumentPath))!;
        Assert.Equal(reference["url"]!.GetValue<string>(), requestDocument["messages"]![0]!
            ["content"]![1]!["image_url"]!["url"]!.GetValue<string>());
        Assert.Equal(payloadPath, sink.Stats.CurrentFile);
        Assert.False(File.Exists(Path.Combine(_dir, DatasetPaths.FileName(day))));
        Assert.False(File.Exists(Path.Combine(_dir, DatasetPaths.LargeFileName(day))));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task WritePayload_SameRequestIdDifferentExportId_DoesNotOverwrite()
    {
        await using var sink = CreateSink();
        var firstBinary = LargePng(0x20);
        var secondBinary = LargePng(0x21);

        sink.Capture(PayloadCapture("same-request", firstBinary));
        sink.Capture(PayloadCapture("same-request", secondBinary));
        WaitUntil(() => sink.Stats.RecordsWritten == 2);

        var day = new DateOnly(2026, 11, 6);
        var manifests = ReadPayloadManifests(day);
        Assert.Equal(2, manifests.Length);
        var firstExportId = manifests[0]["export_id"]!.GetValue<string>();
        var secondExportId = manifests[1]["export_id"]!.GetValue<string>();
        Assert.NotEqual(firstExportId, secondExportId);
        Assert.All(manifests, manifest => Assert.Equal("same-request", manifest["request_id"]!.GetValue<string>()));

        var firstReference = Assert.Single(manifests[0]["payload_refs"]!.AsArray())!;
        var secondReference = Assert.Single(manifests[1]["payload_refs"]!.AsArray())!;
        Assert.NotEqual(firstReference["path"]!.GetValue<string>(), secondReference["path"]!.GetValue<string>());
        Assert.Equal(firstBinary, File.ReadAllBytes(ArtifactPath(_dir, firstReference["path"]!.GetValue<string>())));
        Assert.Equal(secondBinary, File.ReadAllBytes(ArtifactPath(_dir, secondReference["path"]!.GetValue<string>())));
        Assert.True(File.Exists(ArtifactPath(_dir, manifests[0]["documents"]!["request"]!.GetValue<string>())));
        Assert.True(File.Exists(ArtifactPath(_dir, manifests[1]["documents"]!["request"]!.GetValue<string>())));
    }

    [Fact]
    public async Task WritePayload_FinishedAtControlsAllArtifactDays()
    {
        _time.SetUtcNow(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));
        await using var sink = CreateSink();
        // 00:30 ở UTC+02 vẫn là 22:30 ngày 17/11 UTC; clock writer đã sang ngày 18.
        var finishedAt = new DateTimeOffset(2026, 11, 18, 0, 30, 0, TimeSpan.FromHours(2));

        sink.Capture(PayloadCapture("req-utc-day", LargePng(), finishedAt));
        WaitUntil(() => sink.Stats.RecordsWritten == 1);

        var finishedDay = new DateOnly(2026, 11, 17);
        var manifest = Assert.Single(ReadPayloadManifests(finishedDay));
        Assert.Equal("2026-11-17T22:30:00Z", manifest["ts"]!.GetValue<string>());
        var exportId = manifest["export_id"]!.GetValue<string>();
        Assert.Equal(DatasetPaths.PayloadRelativePath(finishedDay, exportId, "request.json"),
            manifest["documents"]!["request"]!.GetValue<string>());
        var reference = Assert.Single(manifest["payload_refs"]!.AsArray())!;
        Assert.StartsWith("payloads/2026-11-17/", reference["path"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.True(File.Exists(ArtifactPath(_dir, reference["path"]!.GetValue<string>())));
        Assert.False(File.Exists(Path.Combine(_dir, DatasetPaths.PayloadFileName(new DateOnly(2026, 11, 18)))));
        Assert.False(Directory.Exists(Path.Combine(_dir, DatasetPaths.PayloadRootDirectoryName, "2026-11-18")));
    }

    [Fact]
    public async Task WritePayload_ArtifactFailure_DoesNotWriteManifest()
    {
        var log = new CapturingLog();
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, DatasetPaths.PayloadRootDirectoryName), "not a directory");
        await using var sink = CreateSink(log);

        sink.Capture(PayloadCapture("req-artifact-failure", LargePng()));
        WaitUntil(() => log.ErrorCount > 0 || sink.Stats.RecordsWritten > 0);

        Assert.Equal(0, sink.Stats.RecordsWritten);
        Assert.Equal(1, log.ErrorCount);
        Assert.Equal(0, sink.Stats.PayloadRecordsWritten);
        Assert.Equal(0, sink.Stats.ExternalizedPayloadCount);
        var day = new DateOnly(2026, 11, 6);
        var payloadJsonlPath = Path.Combine(_dir, DatasetPaths.PayloadFileName(day));
        // Manifest writer được mở trước artifact nên file rỗng tồn tại; invariant là không có dòng
        // manifest nào trỏ tới export chưa hoàn chỉnh, không phải file phải vắng mặt.
        Assert.Empty(ReadAllLinesShared(payloadJsonlPath));
        Assert.False(File.Exists(Path.Combine(_dir, DatasetPaths.FileName(day))));
        Assert.False(File.Exists(Path.Combine(_dir, DatasetPaths.LargeFileName(day))));
    }

    [Fact]
    public Task WritePayload_CompletionArtifactFailure_DoesNotWriteManifestOrCounters() =>
        AssertPayloadArtifactFailureDoesNotWriteManifestOrCounters(binaryArtifact: false);

    [Fact]
    public Task WritePayload_BinaryArtifactFailure_DoesNotWriteManifestOrCounters() =>
        AssertPayloadArtifactFailureDoesNotWriteManifestOrCounters(binaryArtifact: true);

    private async Task AssertPayloadArtifactFailureDoesNotWriteManifestOrCounters(bool binaryArtifact)
    {
        await using var sink = CreateSink();
        var capture = PayloadCapture(binaryArtifact ? "req-binary-failure" : "req-completion-failure", LargePng());
        var recordDay = DateOnly.FromDateTime(capture.FinishedAt.UtcDateTime);
        var exportId = "0123456789abcdef0123456789abcdef";
        var transformed = DatasetPayloadTransformer.Transform(capture, exportId);
        Assert.NotNull(transformed);
        Directory.CreateDirectory(_dir);
        SetCurrentDirectory(sink, _dir);
        var exportDirectory = Path.Combine(_dir, DatasetPaths.PayloadRootDirectoryName,
            recordDay.ToString("yyyy-MM-dd"), exportId);
        Directory.CreateDirectory(exportDirectory);
        var payloadJsonlPath = Path.Combine(_dir, DatasetPaths.PayloadFileName(recordDay));
        using var writer = OpenPayloadWriter(payloadJsonlPath);
        var failedArtifactPath = binaryArtifact
            ? ArtifactPath(_dir, Assert.Single(transformed.Files).RelativePath)
            : Path.Combine(exportDirectory, "completion.json");
        Directory.CreateDirectory(failedArtifactPath);

        var failure = InvokeWritePayload(sink, capture, transformed, exportId, recordDay, writer);

        Assert.NotNull(failure);
        Assert.Empty(ReadAllLinesShared(payloadJsonlPath));
        Assert.Empty(Directory.GetFiles(exportDirectory, "*.tmp-*", SearchOption.AllDirectories));
        Assert.Equal(0, sink.Stats.RecordsWritten);
        Assert.Equal(0, sink.Stats.PayloadRecordsWritten);
        Assert.Equal(0, sink.Stats.ExternalizedPayloadCount);
        Assert.Equal(0, sink.Stats.ExternalizedPayloadBytes);
        Assert.Equal(0, sink.Stats.BytesWritten);
        Assert.True(File.Exists(Path.Combine(exportDirectory, "request.json")));
        if (binaryArtifact)
        {
            Assert.True(File.Exists(Path.Combine(exportDirectory, "completion.json")));
        }

        // Replay phải tạo export mới, không ghép manifest với artifact orphan của lượt lỗi.
        Directory.Delete(failedArtifactPath);
        var recoveryExportId = "fedcba9876543210fedcba9876543210";
        var recovered = DatasetPayloadTransformer.Transform(capture, recoveryExportId);
        Assert.NotNull(recovered);
        failure = InvokeWritePayload(sink, capture, recovered, recoveryExportId, recordDay, writer);
        Assert.Null(failure);
        var manifest = Assert.Single(ReadPayloadManifests(recordDay));
        Assert.Equal(recoveryExportId, manifest["export_id"]!.GetValue<string>());
        Assert.NotEqual(exportId, manifest["export_id"]!.GetValue<string>());
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
    public async Task Retention_LeavesUnrelatedJsonlAndKeepsForeverSetting()
    {
        _time.SetUtcNow(new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.Zero));
        Directory.CreateDirectory(_dir);
        var oldNormalPath = Path.Combine(_dir, DatasetPaths.FileName(new DateOnly(2026, 11, 1)));
        var oldLargePath = Path.Combine(_dir, DatasetPaths.LargeFileName(new DateOnly(2026, 11, 1)));
        var unrelatedPath = Path.Combine(_dir, "requests-user-notes.jsonl");
        var otherJsonlPath = Path.Combine(_dir, "other.jsonl");
        foreach (var path in new[] { oldNormalPath, oldLargePath, unrelatedPath, otherJsonlPath })
        {
            File.WriteAllText(path, "{\"v\":1}\n");
        }

        _settings.Set(SettingsKeys.DatasetRetentionDays, 0);
        var keepForeverSink = CreateSink();
        await using (keepForeverSink)
        {
            keepForeverSink.Capture(ChatCapture("req-keep"));
            WaitUntil(() => keepForeverSink.Stats.RecordsWritten == 1);

            Assert.True(File.Exists(oldNormalPath));
            Assert.True(File.Exists(oldLargePath));
            Assert.True(File.Exists(unrelatedPath));
            Assert.True(File.Exists(otherJsonlPath));
        }

        _settings.Set(SettingsKeys.DatasetRetentionDays, 3);
        await using var sink = CreateSink();
        sink.Capture(ChatCapture("req-purge"));
        WaitUntil(() => sink.Stats.RecordsWritten == 1);

        Assert.False(File.Exists(oldNormalPath));
        Assert.False(File.Exists(oldLargePath));
        Assert.True(File.Exists(unrelatedPath), "file jsonl do người dùng tạo phải được giữ");
        Assert.True(File.Exists(otherJsonlPath), "file ngoài prefix dataset phải được giữ");
    }

    [Fact]
    public async Task Retention_RemovesPayloadFilesAndDirectoriesOnlyWhenExpired()
    {
        _settings.Set(SettingsKeys.DatasetRetentionDays, 3);
        Directory.CreateDirectory(_dir);
        var expiredDay = new DateOnly(2026, 11, 10);
        var retainedDay = new DateOnly(2026, 11, 15);
        var currentDay = new DateOnly(2026, 11, 17);
        var expiredManifest = Path.Combine(_dir, DatasetPaths.PayloadFileName(expiredDay));
        var retainedManifest = Path.Combine(_dir, DatasetPaths.PayloadFileName(retainedDay));
        File.WriteAllText(expiredManifest, "{\"v\":2}\n");
        File.WriteAllText(retainedManifest, "{\"v\":2}\n");

        string CreateExport(DateOnly day, string exportId)
        {
            var exportDirectory = Path.Combine(_dir, DatasetPaths.PayloadRootDirectoryName,
                day.ToString("yyyy-MM-dd"), exportId);
            Directory.CreateDirectory(exportDirectory);
            var binaryPath = Path.Combine(exportDirectory, "image.bin");
            File.WriteAllBytes(binaryPath, [1, 2, 3]);
            File.WriteAllText(Path.Combine(exportDirectory, "request.json"), "{}");
            return binaryPath;
        }

        CreateExport(expiredDay, "expired-export");
        var retainedBinary = CreateExport(retainedDay, "retained-export");
        var currentBinary = CreateExport(currentDay, "existing-current-export");
        var unrelatedDirectory = Path.Combine(_dir, DatasetPaths.PayloadRootDirectoryName, "user-created");
        Directory.CreateDirectory(unrelatedDirectory);
        var unrelatedPayload = Path.Combine(unrelatedDirectory, "keep.bin");
        File.WriteAllBytes(unrelatedPayload, [4, 5, 6]);
        var unrelatedRootFile = Path.Combine(_dir, DatasetPaths.PayloadRootDirectoryName, "keep.txt");
        File.WriteAllText(unrelatedRootFile, "keep");
        var unrelatedJsonl = Path.Combine(_dir, "requests-user-notes.jsonl");
        File.WriteAllText(unrelatedJsonl, "{\"v\":1}\n");

        await using var sink = CreateSink();
        sink.Capture(PayloadCapture("req-retention", LargePng(),
            new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero)));
        WaitUntil(() => sink.Stats.RecordsWritten == 1);

        Assert.False(File.Exists(expiredManifest));
        Assert.False(Directory.Exists(Path.Combine(_dir, DatasetPaths.PayloadRootDirectoryName, "2026-11-10")));
        Assert.True(File.Exists(retainedManifest));
        Assert.True(File.Exists(retainedBinary));
        Assert.True(File.Exists(currentBinary), "artifacts của ngày record hiện tại không được purge");
        Assert.True(File.Exists(unrelatedPayload));
        Assert.True(File.Exists(unrelatedRootFile));
        Assert.True(File.Exists(unrelatedJsonl));
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("large")]
    [InlineData("payload")]
    public async Task Retention_LastWrittenArtifacts_ExpireTogetherAfterIdleRotation(string lane)
    {
        _settings.Set(SettingsKeys.DatasetRetentionDays, 2);
        await using var sink = CreateSink();
        var day = new DateOnly(2026, 11, 6);
        var capture = lane switch
        {
            "large" => ChatCapture("req-large-old",
                clientBody: ChatBodyWithContent(new string('x', DatasetRecordBuilder.InlineMaxFieldBytes))),
            "payload" => PayloadCapture("req-payload-old", LargePng()),
            _ => ChatCapture("req-normal-old"),
        };

        sink.Capture(capture);
        WaitUntil(() => sink.Stats.RecordsWritten == 1);

        var lastWritten = sink.Stats.CurrentFile;
        Assert.NotNull(lastWritten);
        Assert.True(File.Exists(lastWritten));
        var payloadDayDirectory = Path.Combine(_dir, DatasetPaths.PayloadRootDirectoryName, "2026-11-06");
        if (lane == "payload")
        {
            Assert.Equal(Path.Combine(_dir, DatasetPaths.PayloadFileName(day)), lastWritten);
            Assert.True(Directory.Exists(payloadDayDirectory));
        }

        // Hai ngày không có write, rồi đổi sang làn normal: CloseFiles chạy trước purge nên file vừa
        // ghi lần trước không còn là handle active và phải hết hạn cùng artifact ngày của nó.
        _time.AdvanceDays(2);
        sink.Capture(ChatCapture("req-after-idle"));
        WaitUntil(() => sink.Stats.RecordsWritten == 2);

        Assert.False(File.Exists(lastWritten), $"file {lane} vừa ghi lần trước đã hết retention");
        if (lane == "payload")
        {
            Assert.Equal(File.Exists(lastWritten), Directory.Exists(payloadDayDirectory));
            Assert.False(Directory.Exists(payloadDayDirectory),
                "manifest và payload directory không được phép hết hạn độc lập");
        }

        Assert.True(File.Exists(Path.Combine(_dir, DatasetPaths.FileName(new DateOnly(2026, 11, 8)))));
    }

    [Fact]
    public async Task WritePayload_UpdatesPayloadAndExternalizedStats()
    {
        await using var sink = CreateSink();
        var binary = LargePng();

        sink.Capture(PayloadCapture("req-stats", binary));
        WaitUntil(() => sink.Stats.RecordsWritten == 1);

        var day = new DateOnly(2026, 11, 6);
        var payloadPath = Path.Combine(_dir, DatasetPaths.PayloadFileName(day));
        var manifestLine = Assert.Single(ReadAllLinesShared(payloadPath));
        var manifest = (JsonObject)JsonNode.Parse(manifestLine)!;
        var reference = Assert.Single(manifest["payload_refs"]!.AsArray())!;
        var expectedJsonlBytes = Encoding.UTF8.GetByteCount(manifestLine) + 1L;
        Assert.Equal(1, sink.Stats.RecordsWritten);
        Assert.Equal(0, sink.Stats.LargeRecordsWritten);
        Assert.Equal(1, sink.Stats.PayloadRecordsWritten);
        Assert.Equal(1, sink.Stats.ExternalizedPayloadCount);
        Assert.Equal(binary.Length, sink.Stats.ExternalizedPayloadBytes);
        Assert.Equal(expectedJsonlBytes, sink.Stats.BytesWritten);
        Assert.Equal(payloadPath, sink.Stats.CurrentFile);
        Assert.Equal(reference["sha256"]!.GetValue<string>(),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(
                ArtifactPath(_dir, reference["path"]!.GetValue<string>())))).ToLowerInvariant());
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
    public async Task StoragePreparationFailure_AfterDequeue_RestoresPendingBytes()
    {
        var log = new CapturingLog();
        using var settings = CreateSettingsWithInvalidRetention();
        await using var sink = new DatasetSink(settings, log, _time);
        var first = ChatCapture("req-storage-first");

        sink.Capture(first);
        WaitUntil(() => log.ErrorCount == 1);

        Assert.Equal(0, sink.Stats.RecordsWritten);
        var expectedPendingBytes = first.ClientBody.LongLength + first.CompletionJson!.Length;
        Assert.Equal(expectedPendingBytes, PendingBytes(sink));
        Assert.Contains("open daily file", log.FirstError);
    }

    [Fact]
    public async Task StoragePreparationFailure_AfterDequeue_ReplaysOriginalCaptureFirst()
    {
        var log = new CapturingLog();
        using var settings = CreateSettingsWithInvalidRetention();
        await using var sink = new DatasetSink(settings, log, _time);
        var targetPath = Path.Combine(_dir, DatasetPaths.FileName(new DateOnly(2026, 11, 6)));

        sink.Capture(ChatCapture("req-storage-first"));
        WaitUntil(() => log.ErrorCount == 1);
        sink.Capture(ChatCapture("req-storage-disabled"));

        settings.Set(SettingsKeys.DatasetRetentionDays, 0); // sửa lỗi storage + recovery trigger
        for (var i = 0; i < 5; i++)
        {
            sink.Capture(ChatCapture("req-storage-resume"));
        }

        WaitUntil(() => File.Exists(targetPath)
            && ReadAllLinesShared(targetPath).Count(line =>
                line.Contains("req-storage-resume", StringComparison.Ordinal)) == 5);

        var lines = ReadAllLinesShared(targetPath);
        Assert.Equal(6, lines.Length);
        Assert.Contains("req-storage-first", lines[0]);
        Assert.All(lines[1..], line => Assert.Contains("req-storage-resume", line));
        Assert.DoesNotContain(lines, line => line.Contains("req-storage-disabled", StringComparison.Ordinal));
        Assert.Equal(0, PendingBytes(sink));
        Assert.Equal(1, log.ErrorCount);
    }

    [Fact]
    public async Task OpenFailure_PayloadManifest_RetainsCaptureWithoutCreatingArtifacts()
    {
        var log = new CapturingLog();
        Directory.CreateDirectory(_dir);
        var capture = PayloadCapture("req-first", LargePng(0x31));
        var recordDay = DateOnly.FromDateTime(capture.FinishedAt.UtcDateTime);
        var payloadJsonlPath = Path.Combine(_dir, DatasetPaths.PayloadFileName(recordDay));
        // FileStream Append trên manifest phải fail trước khi bất kỳ artifact nào được tạo.
        Directory.CreateDirectory(payloadJsonlPath);
        await using var sink = CreateSink(log);

        sink.Capture(capture);
        WaitUntil(() => log.ErrorCount == 1);

        Assert.Equal(0, sink.Stats.RecordsWritten);
        Assert.Equal(0, sink.Stats.PayloadRecordsWritten);
        Assert.Same(capture, PendingCapture(sink));
        Assert.Equal(RawSize(capture), PendingBytes(sink));
        var payloadDayDirectory = Path.Combine(_dir, DatasetPaths.PayloadRootDirectoryName,
            recordDay.ToString("yyyy-MM-dd"));
        Assert.False(Directory.Exists(payloadDayDirectory));
        Assert.False(File.Exists(payloadJsonlPath));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp-*", SearchOption.AllDirectories));

        // Disabled phải chặn record này; recovery chỉ nhận đúng một capture mới, không cần capture mồi.
        sink.Capture(PayloadCapture("req-disabled", LargePng(0x32)));
        Directory.Delete(payloadJsonlPath);
        _settings.Set(SettingsKeys.LogRetentionDays, 30);
        sink.Capture(PayloadCapture("req-resume", LargePng(0x33)));

        WaitUntil(() => sink.Stats.RecordsWritten == 2);
        Assert.Equal(new[] { "req-first", "req-resume" }, ReadRequestIds(payloadJsonlPath));
        Assert.DoesNotContain("req-disabled", ReadRequestIds(payloadJsonlPath));
        Assert.Equal(0, PendingBytes(sink));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp-*", SearchOption.AllDirectories));
    }

    [Fact]
    public Task OpenFailure_NormalFile_RetainsCaptureAndDrainsItFirstAfterRecovery() =>
        AssertOpenFailureDrainsOriginalCaptureAfterRecovery(largeRecord: false);

    [Fact]
    public Task OpenFailure_LargeFile_RetainsCaptureAndDrainsItFirstAfterRecovery() =>
        AssertOpenFailureDrainsOriginalCaptureAfterRecovery(largeRecord: true);

    private async Task AssertOpenFailureDrainsOriginalCaptureAfterRecovery(bool largeRecord)
    {
        var log = new CapturingLog();
        Directory.CreateDirectory(_dir);
        var day = new DateOnly(2026, 11, 6);
        var targetPath = Path.Combine(_dir, largeRecord
            ? DatasetPaths.LargeFileName(day)
            : DatasetPaths.FileName(day));
        // Directory.CreateDirectory thành công nhưng FileStream Append trên selected JSONL phải fail.
        Directory.CreateDirectory(targetPath);
        var largeBody = largeRecord
            ? ChatBodyWithContent(new string('x', DatasetRecordBuilder.InlineMaxFieldBytes))
            : null;
        DatasetCapture Capture(string requestId) => ChatCapture(requestId, clientBody: largeBody);

        await using var sink = CreateSink(log);
        sink.Capture(Capture("req-first"));
        WaitUntil(() => log.ErrorCount == 1);
        Assert.Equal(0, sink.Stats.RecordsWritten);

        // Open failure phải disable TRƯỚC log: capture này là no-op, không được vượt pending record.
        sink.Capture(Capture("req-disabled"));

        Directory.Delete(targetPath);
        _settings.Set(SettingsKeys.LogRetentionDays, 30); // recovery trigger
        // Restart vẫn lazy sau Settings: nhiều capture mồi tránh race writer cũ chưa completed.
        for (var i = 0; i < 5; i++)
        {
            sink.Capture(Capture("req-resume"));
        }

        WaitUntil(() =>
        {
            if (!File.Exists(targetPath))
            {
                return false;
            }

            var currentLines = ReadAllLinesShared(targetPath);
            return currentLines.Any(line => line.Contains("req-resume", StringComparison.Ordinal))
                && sink.Stats.RecordsWritten >= currentLines.Length;
        });

        var lines = ReadAllLinesShared(targetPath);
        Assert.True(lines.Length >= 2);
        Assert.Contains("req-first", lines[0]);
        Assert.All(lines[1..], line => Assert.Contains("req-resume", line));
        Assert.DoesNotContain(lines, line => line.Contains("req-disabled", StringComparison.Ordinal));
        Assert.Equal(1, log.ErrorCount);
        Assert.Contains("open daily file", log.FirstError);
    }

    [Fact]
    public async Task WriterRecovery_OneCaptureAfterFailureSave_HandoffsAndDrains()
    {
        using var settings = CreateSettingsWithInvalidRetention();
        var log = new CapturingLog();
        DatasetSink? sink = null;
        log.OnError = message =>
        {
            if (message.Contains("open daily file", StringComparison.Ordinal))
            {
                SetThrowingWriter(sink!);
            }
            else if (message.Contains("close file", StringComparison.Ordinal))
            {
                // CloseFiles chạy sau khi writer cũ đã quyết định thoát. Save + đúng một Capture tại
                // đây buộc phải có handoff: EnsureStarted vẫn thấy task cũ chưa completed.
                settings.Set(SettingsKeys.DatasetRetentionDays, 0);
                sink!.Capture(ChatCapture("req-resume"));
            }
        };
        await using (sink = new DatasetSink(settings, log, _time))
        {
            var first = ChatCapture("req-first");
            sink.Capture(first);

            WaitUntil(() => sink.Stats.RecordsWritten == 2);
            var normalFile = Path.Combine(_dir, DatasetPaths.FileName(new DateOnly(2026, 11, 6)));
            Assert.Equal(new[] { "req-first", "req-resume" }, ReadRequestIds(normalFile));
            Assert.Equal(0, PendingBytes(sink));
            Assert.Null(PendingCapture(sink));
        }
    }

    [Fact]
    public async Task WriterRecovery_SaveBeforeFailure_IsNotRecovery()
    {
        var log = new CapturingLog();
        using var closeEntered = new ManualResetEventSlim();
        using var allowClose = new ManualResetEventSlim();
        Directory.CreateDirectory(_dir);
        var normalFile = Path.Combine(_dir, DatasetPaths.FileName(new DateOnly(2026, 11, 6)));
        Directory.CreateDirectory(normalFile);
        DatasetSink? sink = null;
        log.OnError = message =>
        {
            if (message.Contains("open daily file", StringComparison.Ordinal))
            {
                SetThrowingWriter(sink!);
            }
            else if (message.Contains("close file", StringComparison.Ordinal))
            {
                closeEntered.Set();
                allowClose.Wait();
                // Chỉ save này (sau failure) mới được recovery và ghi req-resume.
                _settings.Set(SettingsKeys.LogRetentionDays, 31);
                sink!.Capture(ChatCapture("req-resume"));
            }
        };
        await using (sink = CreateSink(log))
        {
            var first = ChatCapture("req-first");
            try
            {
                Task captureTask;
                lock (StartGate(sink))
                {
                    // Capture đã enqueue nhưng chưa start writer: save tại đây chắc chắn xảy ra
                    // TRƯỚC failure publish, nên không được xóa trạng thái lỗi sắp tới.
                    captureTask = Task.Run(() => sink.Capture(first));
                    WaitUntil(() => PendingBytes(sink) == RawSize(first));
                    _settings.Set(SettingsKeys.LogRetentionDays, 30);
                    Assert.False(captureTask.IsCompleted);
                }

                await captureTask.WaitAsync(TimeSpan.FromSeconds(10));
                WaitUntil(() => closeEntered.IsSet);
                Assert.True(Disabled(sink));
                Assert.Same(first, PendingCapture(sink));
                Assert.Equal(RawSize(first), PendingBytes(sink));
                sink.Capture(ChatCapture("req-disabled"));
                Assert.Equal(0, sink.Stats.RecordsWritten);

                Directory.Delete(normalFile);
                allowClose.Set();
                WaitUntil(() => sink.Stats.RecordsWritten == 2);
                Assert.Equal(new[] { "req-first", "req-resume" }, ReadRequestIds(normalFile));
                Assert.DoesNotContain("req-disabled", ReadRequestIds(normalFile));
                Assert.Equal(0, PendingBytes(sink));
            }
            finally
            {
                allowClose.Set();
            }
        }
    }

    [Fact]
    public async Task WriterDispose_RacingHandoff_DoesNotStartNewWriter()
    {
        // Nhánh dispose thắng: writer cũ đang ở CloseFiles, dispose phải chặn mọi handoff mới.
        var disposeRoot = Path.Combine(_dir, "dispose-wins");
        Directory.CreateDirectory(disposeRoot);
        var disposeFile = Path.Combine(disposeRoot, DatasetPaths.FileName(new DateOnly(2026, 11, 6)));
        Directory.CreateDirectory(disposeFile);
        _settings.Set(SettingsKeys.DatasetExportPath, disposeRoot);
        var disposeLog = new CapturingLog();
        using var disposeCloseEntered = new ManualResetEventSlim();
        using var allowDisposeClose = new ManualResetEventSlim();
        DatasetSink? disposeSink = null;
        disposeLog.OnError = message =>
        {
            if (message.Contains("open daily file", StringComparison.Ordinal))
            {
                SetThrowingWriter(disposeSink!);
            }
            else if (message.Contains("close file", StringComparison.Ordinal))
            {
                disposeCloseEntered.Set();
                allowDisposeClose.Wait();
            }
        };
        disposeSink = CreateSink(disposeLog);
        disposeSink.Capture(ChatCapture("req-dispose-pending"));
        try
        {
            WaitUntil(() => disposeCloseEntered.IsSet);
            var disposeTask = disposeSink.DisposeAsync().AsTask();
            WaitUntil(() => Disposed(disposeSink));
            allowDisposeClose.Set();
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Null(WriterTask(disposeSink));
            disposeSink.Capture(ChatCapture("req-after-dispose"));
            Assert.Null(WriterTask(disposeSink));
            Assert.Equal(0, disposeSink.Stats.RecordsWritten);
        }
        finally
        {
            allowDisposeClose.Set();
            await disposeSink.DisposeAsync();
        }

        // Nhánh handoff thắng: task mới xuất hiện trước dispose; dispose phải capture và chờ đúng
        // task mới, để pending + queue drain xong rồi mới kết thúc.
        var handoffRoot = Path.Combine(_dir, "handoff-wins");
        Directory.CreateDirectory(handoffRoot);
        var handoffFile = Path.Combine(handoffRoot, DatasetPaths.FileName(new DateOnly(2026, 11, 6)));
        Directory.CreateDirectory(handoffFile);
        _settings.Set(SettingsKeys.DatasetExportPath, handoffRoot);
        var handoffLog = new CapturingLog();
        using var handoffCloseEntered = new ManualResetEventSlim();
        using var allowHandoffClose = new ManualResetEventSlim();
        DatasetSink? handoffSink = null;
        handoffLog.OnError = message =>
        {
            if (message.Contains("open daily file", StringComparison.Ordinal))
            {
                SetThrowingWriter(handoffSink!);
            }
            else if (message.Contains("close file", StringComparison.Ordinal))
            {
                handoffCloseEntered.Set();
                allowHandoffClose.Wait();
            }
        };
        handoffSink = CreateSink(handoffLog);
        handoffSink.Capture(ChatCapture("req-first"));
        try
        {
            WaitUntil(() => handoffCloseEntered.IsSet);
            var oldTask = WriterTask(handoffSink);
            Assert.NotNull(oldTask);
            Directory.Delete(handoffFile);
            _settings.Set(SettingsKeys.LogRetentionDays, 32);
            handoffSink.Capture(ChatCapture("req-resume"));
            allowHandoffClose.Set();
            WaitUntil(() => WriterTask(handoffSink) is { } current && !ReferenceEquals(oldTask, current));
            var handoffTask = WriterTask(handoffSink);
            Assert.NotNull(handoffTask);

            await handoffSink.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(handoffTask.IsCompleted);
            Assert.Equal(new[] { "req-first", "req-resume" }, ReadRequestIds(handoffFile));
            Assert.Null(WriterTask(handoffSink));
            handoffSink.Capture(ChatCapture("req-after-handoff-dispose"));
            Assert.Null(WriterTask(handoffSink));
        }
        finally
        {
            allowHandoffClose.Set();
            await handoffSink.DisposeAsync();
        }
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Capture_NonAsciiV1_BytesWrittenCountsUtf8(bool largeRecord)
    {
        await using var sink = CreateSink();
        var content = largeRecord
            ? new string('ớ', DatasetRecordBuilder.InlineMaxFieldBytes)
            : "Xin chào 👋";

        sink.Capture(ChatCapture("req-non-ascii", clientBody: ChatBodyWithContent(content)));
        WaitUntil(() => sink.Stats.RecordsWritten == 1);

        var day = new DateOnly(2026, 11, 6);
        var path = Path.Combine(_dir, largeRecord
            ? DatasetPaths.LargeFileName(day)
            : DatasetPaths.FileName(day));
        Assert.Equal(new FileInfo(path).Length, sink.Stats.BytesWritten);
        Assert.Equal(largeRecord ? 1 : 0, sink.Stats.LargeRecordsWritten);
    }

    [Fact]
    public async Task MixedV1AndV2_BytesWrittenCountsUtf8Jsonl()
    {
        await using var sink = CreateSink();
        var binary = LargePng();

        sink.Capture(ChatCapture("req-normal-utf8",
            clientBody: ChatBodyWithContent("Xin chào 👋")));
        sink.Capture(PayloadCapture("req-payload", binary));
        WaitUntil(() => sink.Stats.RecordsWritten == 2);

        var day = new DateOnly(2026, 11, 6);
        var normalPath = Path.Combine(_dir, DatasetPaths.FileName(day));
        var payloadPath = Path.Combine(_dir, DatasetPaths.PayloadFileName(day));
        var expectedJsonlBytes = new FileInfo(normalPath).Length + new FileInfo(payloadPath).Length;
        Assert.Equal(expectedJsonlBytes, sink.Stats.BytesWritten);
        Assert.Equal(1, sink.Stats.PayloadRecordsWritten);
        Assert.Equal(binary.Length, sink.Stats.ExternalizedPayloadBytes);
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

    private DatasetCapture PayloadCapture(
        string requestId, byte[] binary, DateTimeOffset? finishedAt = null)
    {
        var dataUri = $"data:image/png;base64,{Convert.ToBase64String(binary)}";
        var body = new JsonObject
        {
            ["model"] = "gpt-vision",
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(
                    new JsonObject { ["type"] = "text", ["text"] = "look" },
                    new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject { ["url"] = dataUri },
                    }),
            }),
        };
        return ChatCapture(requestId, clientBody: Encoding.UTF8.GetBytes(body.ToJsonString()), finishedAt: finishedAt);
    }

    private static byte[] LargePng(byte fill = 0x20)
    {
        var binary = new byte[DatasetRecordBuilder.InlineMaxFieldBytes];
        binary.AsSpan().Fill(fill);
        new byte[] { 0x89, 0x50, 0x4E, 0x47 }.AsSpan().CopyTo(binary);
        return binary;
    }

    private JsonObject[] ReadPayloadManifests(DateOnly day)
    {
        var path = Path.Combine(_dir, DatasetPaths.PayloadFileName(day));
        return [.. ReadAllLinesShared(path).Select(line => (JsonObject)JsonNode.Parse(line)!)];
    }

    private static string ArtifactPath(string root, string relativePath) =>
        Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private DatasetSink CreateSink(CapturingLog? log = null) =>
        new(_settings, (ILogService?)log ?? new NullLog(), _time);

    private AppSettingsService CreateSettingsWithInvalidRetention()
    {
        var factory = _db.CreateFactory();
        using var context = factory.CreateDbContext();
        // EnsureDestinationDirectory vẫn thành công; deserialization này chỉ ném trong PurgeOldFiles,
        // sau khi writer đã dequeue capture — đúng nhánh EnsureStorageForDay cần kiểm thử.
        context.AppSettings.Add(new AppSetting
        {
            Key = SettingsKeys.DatasetRetentionDays,
            ValueJson = "\"not-an-integer\"",
        });
        context.SaveChanges();
        return new AppSettingsService(factory);
    }

    private static FieldInfo PrivateField(string name)
    {
        var field = typeof(DatasetSink).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field;
    }

    private static long PendingBytes(DatasetSink sink) =>
        Assert.IsType<long>(PrivateField("_pendingBytes").GetValue(sink));

    private static DatasetCapture? PendingCapture(DatasetSink sink) =>
        (DatasetCapture?)PrivateField("_pendingCapture").GetValue(sink);

    private static long RawSize(DatasetCapture capture) =>
        capture.ClientBody.LongLength + (capture.CompletionJson?.Length ?? 0);

    private static object StartGate(DatasetSink sink) =>
        PrivateField("_startGate").GetValue(sink)!;

    private static Task? WriterTask(DatasetSink sink) =>
        (Task?)PrivateField("_writerTask").GetValue(sink);

    private static bool Disabled(DatasetSink sink) =>
        Assert.IsType<bool>(PrivateField("_disabled").GetValue(sink));

    private static bool Disposed(DatasetSink sink) =>
        Assert.IsType<bool>(PrivateField("_disposed").GetValue(sink));

    private static void SetThrowingWriter(DatasetSink sink) =>
        PrivateField("_writer").SetValue(sink, new ThrowingDisposeStreamWriter());

    private static void SetCurrentDirectory(DatasetSink sink, string directory) =>
        PrivateField("_currentDirectory").SetValue(sink, directory);

    private static StreamWriter OpenPayloadWriter(string path)
    {
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        return new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
    }

    private static Exception? InvokeWritePayload(
        DatasetSink sink,
        DatasetCapture capture,
        DatasetPayloadTransformResult transformed,
        string exportId,
        DateOnly recordDay,
        StreamWriter writer)
    {
        var method = typeof(DatasetSink).GetMethod("WritePayload", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        object?[] arguments = [capture, transformed, exportId, recordDay, writer];
        try
        {
            method.Invoke(sink, arguments);
            return null;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            return ex.InnerException;
        }
    }

    private static string[] ReadRequestIds(string path) =>
        [.. ReadAllLinesShared(path).Select(line =>
            JsonNode.Parse(line)!["request_id"]!.GetValue<string>())];

    /// <summary>Body chat tối thiểu hợp lệ cho builder (messages có content).</summary>
    private const string ValidBody = """{"model":"m","messages":[{"role":"user","content":"hi"}]}""";

    /// <summary>Cả object chat.completion — shape CompletionJson của endpoint chat (spec §7.3).</summary>
    private const string ValidCompletion =
        """{"id":"1","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"hello"},"finish_reason":"stop"}],"usage":{"prompt_tokens":2,"completion_tokens":3}}""";

    /// <summary>
    /// Capture chat hợp lệ; truyền <c>completionJson: null</c> tường minh để tạo record bị builder
    /// skip Empty (test counter).
    /// </summary>
    private DatasetCapture ChatCapture(
        string requestId,
        string? completionJson = ValidCompletion,
        byte[]? clientBody = null,
        DateTimeOffset? finishedAt = null) => new(
        RequestId: requestId,
        Endpoint: ProxyEndpoint.Chat,
        FinishedAt: finishedAt ?? _time.GetUtcNow().AddHours(1),
        ClientBody: clientBody ?? Encoding.UTF8.GetBytes(ValidBody),
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

    private static byte[] ChatBodyWithContent(string content)
    {
        var body = JsonNode.Parse(ValidBody)!.AsObject();
        body["messages"]![0]!["content"] = content;
        return Encoding.UTF8.GetBytes(body.ToJsonString());
    }

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

    /// <summary>Cho CloseFiles một lỗi có kiểm soát để dừng writer ở finally trước handoff.</summary>
    private sealed class ThrowingDisposeStreamWriter : StreamWriter
    {
        public ThrowingDisposeStreamWriter() : base(Stream.Null) { }

        protected override void Dispose(bool disposing) =>
            throw new IOException("Lỗi đóng file giả lập để rendezvous writer handoff.");
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

        /// <summary>Rendezvous các race ở đúng boundary log, không thêm API test-only vào production.</summary>
        public Action<string>? OnError { get; set; }

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
        public void Error(string message, Exception? exception = null, LogCategory category = LogCategory.App)
        {
            Write(new LogEntry { Severity = LogSeverity.Error, Category = category, Message = message });
            OnError?.Invoke(message);
        }

        public void LogRequestUsage(string? requestId, long? clientKeyId, int promptTokens, int completionTokens) { }
        public IReadOnlyList<LogEntry> Query(LogQuery query) => [];
        public int Count(LogQuery query) => 0;
    }
}
