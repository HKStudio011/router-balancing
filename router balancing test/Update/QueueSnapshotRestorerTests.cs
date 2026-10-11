using System.IO.Pipelines;
using System.Text;
using Microsoft.AspNetCore.Http;
using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Update;

namespace router_balancing_test.Update;

public class QueueSnapshotRestorerTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"queue-restore-{Guid.NewGuid():N}");

    private string SnapshotPath => Path.Combine(_directory, "queue-snapshot.json");

    private readonly RequestQueue _queue = new();
    private readonly StubMonitor _monitor = new();
    private readonly StubTrace _trace = new();

    public QueueSnapshotRestorerTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private QueueSnapshotRestorer CreateRestorer() => new(_queue, _monitor, _trace, new NullLog());

    private Task WriteSnapshotAsync(params ProxyRequest[] requests) =>
        QueueSnapshotStore.SaveAsync(requests, SnapshotPath);

    private static ProxyRequest CreateRequest(string id, RequestPriority priority, string model,
        string body, ProxyEndpoint endpoint = ProxyEndpoint.Chat)
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = Stream.Null;
        return new ProxyRequest(id, priority, model, Encoding.UTF8.GetBytes(body), ctx, endpoint);
    }

    [Fact]
    public async Task RestoreAsync_EnqueuesItemsInFileOrder_AndDeletesFile()
    {
        var highest = CreateRequest("req-highest", RequestPriority.Highest, "gpt-5",
            """{"model":"gpt-5"}""");
        highest.EnqueuedAt = new DateTimeOffset(2026, 10, 11, 8, 0, 0, TimeSpan.Zero);
        var normal = CreateRequest("req-normal", RequestPriority.Normal, "claude-sonnet-4-5",
            """{"model":"claude-sonnet-4-5"}""", ProxyEndpoint.Responses);
        normal.EnqueuedAt = new DateTimeOffset(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);
        await WriteSnapshotAsync(highest, normal);

        await CreateRestorer().RestoreAsync(SnapshotPath);

        // Snapshot() xếp theo bucket Priority rồi Sequence — Highest trước, Normal sau,
        // đúng thứ tự file (file đã sort theo Priority khi ghi)
        var restored = _queue.Snapshot();
        Assert.Equal(["req-highest", "req-normal"], restored.Select(r => r.Id));
        Assert.False(File.Exists(SnapshotPath));
    }

    [Fact]
    public async Task RestoreAsync_PreservesEnqueuedAtFromSnapshot()
    {
        // Review Focus #3: RequestQueue.Enqueue ghi đè EnqueuedAt = UtcNow —
        // restorer phải set lại SAU enqueue thì timestamp sống đúng qua restart
        var savedAt = new DateTimeOffset(2026, 10, 10, 14, 30, 0, TimeSpan.FromHours(7));
        var request = CreateRequest("req-time", RequestPriority.Normal, "gpt-5", "{}");
        request.EnqueuedAt = savedAt;
        await WriteSnapshotAsync(request);

        await CreateRestorer().RestoreAsync(SnapshotPath);

        var restored = Assert.Single(_queue.Snapshot());
        Assert.Equal(savedAt, restored.EnqueuedAt);
    }

    [Fact]
    public async Task RestoreAsync_WhenIsStream_PublishesPipeInContextItems()
    {
        var stream = CreateRequest("req-stream", RequestPriority.Normal, "gpt-5", "{}");
        stream.Context.Items[SsePipeItems.Key] = new Pipe();
        var plain = CreateRequest("req-plain", RequestPriority.Normal, "gpt-5", "{}");
        await WriteSnapshotAsync(stream, plain);

        await CreateRestorer().RestoreAsync(SnapshotPath);

        var restoredStream = _queue.Snapshot().Single(r => r.Id == "req-stream");
        var restoredPlain = _queue.Snapshot().Single(r => r.Id == "req-plain");
        Assert.IsType<Pipe>(restoredStream.Context.Items[SsePipeItems.Key]);
        Assert.False(restoredPlain.Context.Items.ContainsKey(SsePipeItems.Key));

        // Dọn: completion xong để drain task thoát thay vì chờ ReadAsync vô hạn
        restoredStream.Completion.TrySetResult(new DispatchOutcome.Cancelled());
    }

    [Fact]
    public async Task RestoreAsync_CallsMonitorStartRequest()
    {
        var body = """{"model":"gpt-5","messages":[]}""";
        var request = CreateRequest("req-mon", RequestPriority.Normal, "gpt-5", body);
        await WriteSnapshotAsync(request);

        await CreateRestorer().RestoreAsync(SnapshotPath);

        var call = Assert.Single(_monitor.Started);
        Assert.Equal("req-mon", call.RequestId);
        Assert.Equal("gpt-5", call.Model);
        Assert.Equal(Encoding.UTF8.GetBytes(body), call.PromptBody);

        // Đồng bộ trace: event Received như request thường (spec §8)
        var traceEvent = Assert.Single(_trace.Events);
        Assert.Equal("req-mon", traceEvent.RequestId);
        Assert.Equal(TraceStage.Received, traceEvent.Stage);
        Assert.Equal(RequestPriority.Normal, traceEvent.Priority);
        Assert.Equal("chat", traceEvent.Endpoint);
    }

    [Fact]
    public async Task RestoreAsync_Noop_WhenFileMissing()
    {
        await CreateRestorer().RestoreAsync(SnapshotPath);

        Assert.Empty(_queue.Snapshot());
        Assert.Empty(_monitor.Started);
        Assert.Empty(_trace.Events);
        Assert.False(File.Exists(SnapshotPath));
    }

    [Fact]
    public async Task RestoreAsync_DeletesFileAndSkips_WhenCorrupt()
    {
        await File.WriteAllTextAsync(SnapshotPath, "not-a-json");

        await CreateRestorer().RestoreAsync(SnapshotPath);

        Assert.Empty(_queue.Snapshot());
        Assert.Empty(_monitor.Started);
        Assert.False(File.Exists(SnapshotPath));
    }

    [Fact]
    public async Task RestoreAsync_DeletesFileAndSkips_WhenVersionUnknown()
    {
        var request = CreateRequest("req-ver", RequestPriority.Normal, "gpt-5", "{}");
        await WriteSnapshotAsync(request);
        // Version 99 mô phỏng file ghi bởi app bản khác schema — restorer phải xoá, không enqueue
        var json = File.ReadAllText(SnapshotPath)
            .Replace($"\"version\": {QueueSnapshotStore.CurrentVersion}", "\"version\": 99");
        await File.WriteAllTextAsync(SnapshotPath, json);

        await CreateRestorer().RestoreAsync(SnapshotPath);

        Assert.Empty(_queue.Snapshot());
        Assert.Empty(_monitor.Started);
        Assert.False(File.Exists(SnapshotPath));
    }

    [Fact]
    public async Task RestoreAsync_SkipsDuplicateId()
    {
        var first = CreateRequest("req-dup", RequestPriority.Normal, "gpt-5",
            """{"model":"gpt-5"}""");
        var second = CreateRequest("req-dup", RequestPriority.Normal, "other-model",
            """{"model":"other-model"}""");
        await WriteSnapshotAsync(first, second);

        await CreateRestorer().RestoreAsync(SnapshotPath);

        // Item thứ hai id trùng → Enqueue false → skip, giữ nguyên item đầu
        var restored = Assert.Single(_queue.Snapshot());
        Assert.Equal("gpt-5", restored.Model);
        Assert.False(File.Exists(SnapshotPath));
    }

    /// <summary>IApiMonitorStore ghi nhận call StartRequest — xác minh restorer đồng bộ H1.</summary>
    private sealed class StubMonitor : IApiMonitorStore
    {
        public List<(string RequestId, string Model, byte[] PromptBody)> Started { get; } = [];

        public event Action Changed { add { } remove { } }

        public long TodayTokens => 0;

        public IReadOnlyList<ApiCallRecord> Snapshot() => [];

        public ApiCallRecord? Find(string requestId) => null;

        public void StartRequest(string requestId, string model, byte[] promptBody) =>
            Started.Add((requestId, model, promptBody));

        public void RecordResponse(string requestId, int? promptTokens, int? completionTokens,
            DateTimeOffset? firstTokenAt, string? responseBody)
        {
        }

        public void RecordError(string requestId, int status, string? errorBody)
        {
        }
    }

    /// <summary>ITraceFeed ghi nhận Publish — xác minh event Received của request restore.</summary>
    private sealed class StubTrace : ITraceFeed
    {
        public List<TraceEvent> Events { get; } = [];

        public event Action<TraceEvent>? Published { add { } remove { } }

        public void Publish(TraceEvent e) => Events.Add(e);

        public IReadOnlyList<TraceEvent> Snapshot() => Events;

        public void PurgeAll() => Events.Clear();
    }
}
