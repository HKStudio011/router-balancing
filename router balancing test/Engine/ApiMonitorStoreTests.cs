using System.Text;
using RouterBalancing.Core.Engine;

namespace router_balancing_test.Engine;

public class ApiMonitorStoreTests
{
    private const string Prompt = """{"messages":[]}""";

    private static readonly DateTimeOffset At = new(2026, 10, 6, 8, 0, 0, TimeSpan.Zero);

    private readonly NullLog _log = new();
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
    private readonly TraceFeed _feed;
    private readonly ApiMonitorStore _store;

    public ApiMonitorStoreTests()
    {
        _feed = new TraceFeed(_log);
        _store = new ApiMonitorStore(_feed, _log, _time);
    }

    private static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);

    private static TraceEvent Ev(string id, TraceStage stage, string model, DateTimeOffset at,
        TraceRoute? route = null, int? status = null, bool? success = null, string? mode = null) =>
        new(id, stage, model, route, null, status, success, null, at, mode);

    [Fact]
    public void StartRequest_ThenRecordResponse_UpsertsSameRecord_KeepsFirstSeenOrder()
    {
        _store.StartRequest("r1", "m1", Bytes(Prompt));
        _store.StartRequest("r2", "m2", Bytes(Prompt));
        Assert.Equal(ApiCallState.Queued, _store.Find("r1")!.State);

        _store.RecordResponse("r1", 10, 20, null, """{"ok":true}""");

        var snap = _store.Snapshot();
        Assert.Equal(2, snap.Count);
        Assert.Equal("r2", snap[0].RequestId);   // newest-first
        Assert.Equal("r1", snap[1].RequestId);   // update không reorder — giữ vị trí first-seen

        var r1 = _store.Find("r1")!;
        Assert.Equal(10, r1.PromptTokens);
        Assert.Equal(20, r1.CompletionTokens);
        Assert.Equal("""{"ok":true}""", r1.ResponseBody);
        Assert.Equal("m1", r1.Model);
        Assert.Equal(Prompt, r1.PromptBody);
    }

    [Fact]
    public void StartRequest_LongPrompt_TruncatesAt64KWithMarker()
    {
        _store.StartRequest("r1", "m1", Bytes(new string('a', 70 * 1024)));

        var r = _store.Find("r1")!;
        Assert.NotNull(r.PromptBody);
        Assert.EndsWith("[truncated]", r.PromptBody);
        Assert.True(r.PromptBody.Length <= 64 * 1024 + "[truncated]".Length,
            $"PromptBody.Length = {r.PromptBody.Length} vượt cap 64KB + marker");
    }

    [Fact]
    public void StartRequest_LongMultiBytePrompt_TruncatesAt64KBytesWithSingleMarker()
    {
        // ~80KB bytes nhưng chỉ ~40k chars — cap phải theo BYTE trước khi decode,
        // nếu cắt theo char thì body này không bị cắt marker (test RED cho byte-truncate)
        _store.StartRequest("r1", "m1", Bytes("{\"m\":\"" + new string('é', 40000) + "\"}"));

        var prompt = _store.Find("r1")!.PromptBody!;
        Assert.EndsWith("[truncated]", prompt);
        Assert.Equal(1, prompt.Split("[truncated]").Length - 1);
        Assert.True(prompt.Length <= 64 * 1024 + "[truncated]".Length,
            $"PromptBody.Length = {prompt.Length} vượt cap 64KB + marker");
    }

    [Fact]
    public void StartBeyond50_EvictsOldest_NewestFirstInSnapshot()
    {
        for (var i = 1; i <= 51; i++)
            _store.StartRequest($"r{i}", "m", Bytes(Prompt));

        var snap = _store.Snapshot();
        Assert.Equal(50, snap.Count);
        Assert.Equal("r51", snap[0].RequestId);
        Assert.Equal("r2", snap[^1].RequestId);
        Assert.Null(_store.Find("r1"));   // oldest bị evict (first-seen order)
        Assert.NotNull(_store.Find("r51"));
    }

    [Fact]
    public void FeedEvents_DrivesStateRouteAndCompletedAt()
    {
        _store.StartRequest("r1", "m1", Bytes(Prompt));

        _feed.Publish(Ev("r1", TraceStage.DispatchStarted, "m1", At, mode: "balancer"));
        var running = _store.Find("r1")!;
        Assert.Equal(ApiCallState.Running, running.State);
        Assert.Equal("balancer", running.Mode);
        Assert.Equal(Prompt, running.PromptBody);   // feed không đè prompt

        _feed.Publish(Ev("r1", TraceStage.Attempt, "m1", At.AddSeconds(1),
            route: new TraceRoute("c1", "p1", "a1")));
        var attempted = _store.Find("r1")!;
        Assert.Equal(ApiCallState.Running, attempted.State);
        Assert.Equal("c1", attempted.Combo);
        Assert.Equal("p1", attempted.Provider);
        Assert.Equal("a1", attempted.Account);
        Assert.Equal("balancer", attempted.Mode);   // Mode giữ từ DispatchStarted

        var finishedAt = At.AddSeconds(2);
        _feed.Publish(Ev("r1", TraceStage.Finished, "m1", finishedAt, status: 200, success: true));
        var done = _store.Find("r1")!;
        Assert.Equal(ApiCallState.Done, done.State);
        Assert.Equal(200, done.Status);
        Assert.True(done.Success);
        Assert.Equal(finishedAt, done.CompletedAt);
        Assert.Equal("m1", done.Model);
    }

    [Fact]
    public void FeedCanceled_SetsCancelledState()
    {
        _store.StartRequest("r1", "m1", Bytes(Prompt));
        var canceledAt = At.AddSeconds(3);

        _feed.Publish(Ev("r1", TraceStage.Canceled, "m1", canceledAt));

        var r = _store.Find("r1")!;
        Assert.Equal(ApiCallState.Cancelled, r.State);
        Assert.Equal(canceledAt, r.CompletedAt);
        Assert.Equal(Prompt, r.PromptBody);   // prompt vẫn còn để xem trong popup
    }

    [Fact]
    public void ParkedEvent_KeepsCurrentState()
    {
        _store.StartRequest("r1", "m1", Bytes(Prompt));
        _feed.Publish(Ev("r1", TraceStage.DispatchStarted, "m1", At));
        Assert.Equal(ApiCallState.Running, _store.Find("r1")!.State);

        _feed.Publish(Ev("r1", TraceStage.Parked, "m1", At));

        // Marker chỉ dành cho Live Trace (spec §2.5) — monitor không đổi state
        Assert.Equal(ApiCallState.Running, _store.Find("r1")!.State);
    }

    [Fact]
    public void RecordError429_ThenFeedFinishedSuccess_EndsDoneStatus200()
    {
        _store.StartRequest("r1", "m1", Bytes(Prompt));
        _store.RecordError("r1", 429, """{"error":"rate limited"}""");

        var err = _store.Find("r1")!;
        Assert.Equal(429, err.Status);
        Assert.Equal("http", err.FailureKind);
        Assert.Equal(ApiCallState.Queued, err.State);   // State CHƯA Error — H4 authoritative
        Assert.Equal("""{"error":"rate limited"}""", err.ErrorBody);

        // Walk retry thành công → 2xx ghi response, xóa error body cũ
        _store.RecordResponse("r1", 7, 5, null, """{"ok":1}""");
        Assert.Null(_store.Find("r1")!.ErrorBody);

        _feed.Publish(Ev("r1", TraceStage.Finished, "m1", At.AddSeconds(2), status: 200, success: true));
        var done = _store.Find("r1")!;
        Assert.Equal(ApiCallState.Done, done.State);
        Assert.Equal(200, done.Status);
        Assert.Null(done.ErrorBody);
    }

    [Fact]
    public void RecordErrorNetwork0_SetsNullStatusAndNetworkKind()
    {
        _store.StartRequest("r1", "m1", Bytes(Prompt));
        _store.RecordError("r1", 0, null);

        var err = _store.Find("r1")!;
        Assert.Null(err.Status);                    // 0 = không có HTTP status → null
        Assert.Equal("network", err.FailureKind);
        Assert.Equal(ApiCallState.Queued, err.State);

        _feed.Publish(Ev("r1", TraceStage.Finished, "m1", At.AddSeconds(1), status: null, success: false));
        var failed = _store.Find("r1")!;
        Assert.Equal(ApiCallState.Error, failed.State);
        Assert.False(failed.Success);
        Assert.Equal("network", failed.FailureKind);   // event không kind → giữ kind do RecordError ghi
    }

    [Fact]
    public void FeedFinishedNetworkFailure_SetsErrorState()
    {
        _store.StartRequest("r1", "m1", Bytes(Prompt));

        // Lỗi mạng: Finished mang Success=false và không có HTTP status
        _feed.Publish(Ev("r1", TraceStage.Finished, "m1", At.AddSeconds(1), status: null, success: false));

        var r = _store.Find("r1")!;
        Assert.Equal(ApiCallState.Error, r.State);
        Assert.False(r.Success);
        Assert.Null(r.Status);
        Assert.Equal(At.AddSeconds(1), r.CompletedAt);
    }

    [Fact]
    public void RecordResponse_ClearsErrorBody()
    {
        _store.StartRequest("r1", "m1", Bytes(Prompt));
        _store.RecordError("r1", 500, """{"error":"boom"}""");
        Assert.Equal("""{"error":"boom"}""", _store.Find("r1")!.ErrorBody);

        _store.RecordResponse("r1", 3, 4, null, """{"ok":1}""");

        var r = _store.Find("r1")!;
        Assert.Null(r.ErrorBody);
        Assert.Equal("""{"ok":1}""", r.ResponseBody);
        Assert.Equal(3, r.PromptTokens);
        Assert.Equal(4, r.CompletionTokens);
    }

    [Fact]
    public void Changed_SubscriberThrows_DoesNotPropagate()
    {
        var raised = 0;
        _store.Changed += () => raised++;
        _store.Changed += () => throw new InvalidOperationException("subscriber boom");

        var ex = Record.Exception(() => _store.StartRequest("r1", "m1", Bytes(Prompt)));

        Assert.Null(ex);
        Assert.Equal(1, raised);                    // subscriber lành vẫn nhận (thứ tự thêm)
        Assert.NotNull(_store.Find("r1"));          // mutation đã ghi trước khi fire
    }

    [Fact]
    public void Published_UnknownRequest_CreatesSkeletonNoThrow()
    {
        var ex = Record.Exception(() =>
        {
            _feed.Publish(Ev("x1", TraceStage.Received, "m1", At));
            _feed.Publish(Ev("x2", TraceStage.DispatchStarted, "m2", At.AddSeconds(1), mode: "direct"));
        });

        Assert.Null(ex);
        var x1 = _store.Find("x1")!;
        Assert.Equal(ApiCallState.Queued, x1.State);
        Assert.Equal("m1", x1.Model);
        Assert.Equal(At, x1.StartedAt);
        var x2 = _store.Find("x2")!;
        Assert.Equal(ApiCallState.Running, x2.State);   // skeleton tạo rồi chuyển stage ngay
        Assert.Equal("direct", x2.Mode);
    }

    [Fact]
    public void TodayTokens_Accumulates_AndResetsOnUtcDateChange()
    {
        _store.StartRequest("r1", "m1", Bytes(Prompt));
        _store.RecordResponse("r1", 10, 20, null, null);
        _store.StartRequest("r2", "m1", Bytes(Prompt));
        _store.RecordResponse("r2", 5, 5, null, null);
        Assert.Equal(40, _store.TodayTokens);

        _time.AdvanceDays(1);   // đổi ngày UTC → counter reset, chỉ tính record hôm sau

        // Getter phải tự reset NGAY khi đọc sau ngày mới — chưa cần RecordResponse nào
        Assert.Equal(0, _store.TodayTokens);

        _store.StartRequest("r3", "m1", Bytes(Prompt));
        _store.RecordResponse("r3", 1, 5, null, null);

        Assert.Equal(6, _store.TodayTokens);
    }

    [Fact]
    public async Task Snapshot_ConcurrentWriters_DoNotThrow()
    {
        var tasks = Enumerable.Range(0, 4).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 25; i++)
            {
                var id = $"t{t}-r{i}";
                _store.StartRequest(id, $"m{t}", Bytes(Prompt));
                _store.RecordResponse(id, 1, 2, null, "{}");
                _ = _store.Snapshot();
                _ = _store.Find(id);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        // 100 record vượt cap → đúng 50 còn lại, không có record nào bị mất giữa chừng vì lỗi race
        Assert.Equal(50, _store.Snapshot().Count);
    }
}
