using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Engine;

public class DispatcherLoopTests : IDisposable
{
    private const string SseBody = "data: {\"x\":1}\n\ndata: [DONE]\n\n";

    private readonly TestDb _db = new();
    private readonly RequestQueue _queue = new();
    private readonly ExecutionList _executions;
    private readonly DpapiSecretProtector _protector = new();
    private DispatcherLoop? _loop;

    public DispatcherLoopTests()
    {
        DbInitializer.Initialize(_db.CreateFactory());
        _executions = new ExecutionList(_db.CreateFactory());
    }

    public void Dispose()
    {
        // StopAsync (IHostedService) trả Task — không có AsTask(); ?. nuốt cả chuỗi khi loop chưa khởi tạo.
        // CancellationToken.None: BackgroundService không có overload không tham số — token None =
        // chỉ StopAsync mới cancel (ExecuteAsync dừng khi loop tự thấy cancellation).
        _loop?.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        _db.Dispose();
    }

    private long SeedProvider(string name, int maxConcurrent = 4, string modelId = "m1",
        int accounts = 1)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        var provider = new Provider
        {
            Name = name,
            BaseUrl = "https://api.openai.com",
            MaxConcurrent = maxConcurrent,
        };
        provider.Models.Add(new Model { ModelId = modelId, Enabled = true });
        // Key distinct theo (provider, TK) — test walk nhận diện upstream nhận request trên TK nào
        for (var i = 1; i <= accounts; i++)
        {
            provider.Accounts.Add(new ProviderAccount
            {
                Name = $"a{i}",
                Enabled = true,
                ApiKeyEncrypted = _protector.Protect($"sk-{name}-a{i}"),
            });
        }
        db.Providers.Add(provider);
        db.SaveChanges();
        return provider.Id;
    }

    private ModelCandidate Candidate(long providerId, string? modelId = null)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        // Accounts bắt buộc: Handler.ResolveFirstEnabledKey đọc nav này để lấy key (như ComboResolver/ModelResolver)
        var provider = db.Providers
            .Include(p => p.Models)
            .Include(p => p.Accounts)
            .First(p => p.Id == providerId);
        return new ModelCandidate(provider, modelId is null
            ? provider.Models[0]
            : provider.Models.First(m => m.ModelId == modelId));
    }

    private void AddModel(long providerId, string modelId)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        var provider = db.Providers.Include(p => p.Models).First(p => p.Id == providerId);
        provider.Models.Add(new Model { ModelId = modelId, Enabled = true });
        db.SaveChanges();
    }

    /// <summary>Tắt/bật toàn bộ TK của provider — fixture đổi trạng thái giữa các lần dispatch (DB thật).</summary>
    private void SetAccountsEnabled(long providerId, bool enabled)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        var provider = db.Providers.Include(p => p.Accounts).First(p => p.Id == providerId);
        foreach (var account in provider.Accounts)
            account.Enabled = enabled;
        db.SaveChanges();
    }

    private long AccountIdOf(long providerId, string accountName)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        return db.Providers.Include(p => p.Accounts).First(p => p.Id == providerId)
            .Accounts.First(a => a.Name == accountName).Id;
    }

    private static ProxyRequest Req(string id, string model = "m1")
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        return new ProxyRequest(id, RequestPriority.Normal, model, Encoding.UTF8.GetBytes("{}"), ctx);
    }

    private async Task StartAsync(IComboResolver resolver, IModelSelector selector,
        IUpstreamClient upstream, CapturingLog log, IExecutionList? executions = null)
    {
        var handler = new ChatCompletionsHandler(upstream, _protector, log, new NullUsageSink());
        _loop = new DispatcherLoop(_queue, executions ?? _executions, resolver, selector, handler, log);
        await _loop.StartAsync(CancellationToken.None);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(50);
        Assert.True(condition(), "điều kiện không đạt trong 5s");
    }

    private sealed class StubResolver(SelectionResult result) : IComboResolver
    {
        public Task<SelectionResult> ResolveAsync(string model, CancellationToken ct) =>
            Task.FromResult(result);
    }

    private sealed class ThrowingOnceResolver(SelectionResult result) : IComboResolver
    {
        private int _calls;

        public Task<SelectionResult> ResolveAsync(string model, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
                throw new InvalidOperationException("boom");
            return Task.FromResult(result);
        }
    }

    private sealed class GatingResolver(SelectionResult result) : IComboResolver
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;
        public void OpenGate() => _gate.TrySetResult();

        public Task<SelectionResult> ResolveAsync(string model, CancellationToken ct)
        {
            _entered.TrySetResult();
            return AwaitGateAsync();
        }

        private async Task<SelectionResult> AwaitGateAsync()
        {
            await _gate.Task;
            return result;
        }
    }

    /// <summary>
    /// Resolve đọc DB thật (phản ánh TK bị tắt giữa chừng) nhưng CÓ barrier: lần resolve
    /// thứ <paramref name="blockAtCall"/> chặn TRƯỚC khi đọc — test tắt TK xong mới mở
    /// barrier → snapshot LUÔN mới, không straddle với lúc disable (sentinel stale = 503 spec).
    /// </summary>
    private sealed class BarrierResolver(Func<SelectionResult> factory, int blockAtCall) : IComboResolver
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        /// <summary>Mở barrier — các lần resolve đến sau đi thẳng (đọc DB ngay).</summary>
        public void Release() => _release.TrySetResult();

        public async Task<SelectionResult> ResolveAsync(string model, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == blockAtCall)
                await _release.Task.WaitAsync(ct);
            return factory();
        }
    }

    private sealed class CountingSelector(IModelSelector inner) : IModelSelector
    {
        public int Calls;

        public Task<ModelCandidate?> TrySelectAsync(SelectionSuccess selection, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return inner.TrySelectAsync(selection, ct);
        }
    }

    /// <summary>
    /// Double IExecutionList: trả <see cref="TryEnterResult.NoAccountLeft"/> cho lần TryEnter
    /// THỨ 2 của 1 provider (lần 1 = dispatch đầu, lần 2 = account-advance) — test không phụ
    /// thuộc nhánh reachability của <see cref="ExecutionList"/> thật.
    /// </summary>
    private sealed class NoAccountLeftOnSecondEnter(IExecutionList inner, long providerId)
        : IExecutionList
    {
        private int _calls;

        public event Action? Exited
        {
            add => inner.Exited += value;
            remove => inner.Exited -= value;
        }

        public Task<bool> CanEnterAsync(long pid, CancellationToken ct) =>
            inner.CanEnterAsync(pid, ct);

        public Task<TryEnterResult> TryEnterAsync(long pid, string requestId, string providerName,
            string modelId, RequestPriority priority, DateTimeOffset enqueuedAt,
            IReadOnlySet<long>? excludedAccounts, CancellationToken ct) =>
            pid == providerId && Interlocked.Increment(ref _calls) == 2
                ? Task.FromResult<TryEnterResult>(new TryEnterResult.NoAccountLeft())
                : inner.TryEnterAsync(pid, requestId, providerName, modelId, priority, enqueuedAt,
                    excludedAccounts, ct);

        public void Exit(string requestId) => inner.Exit(requestId);

        public bool Contains(string requestId) => inner.Contains(requestId);

        public int GetInFlight(long pid) => inner.GetInFlight(pid);

        public IReadOnlyList<ExecutionEntry> Snapshot() => inner.Snapshot();
    }

    private sealed class StubUpstream : IUpstreamClient
    {
        public int Calls;

        public Task<HttpResponseMessage> PostChatCompletionAsync(
            Provider provider, string apiKey, byte[] body, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SseBody, Encoding.UTF8, "text/event-stream"),
            });
        }
    }

    private sealed class GatedUpstream : IUpstreamClient
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;
        public void Release() => _release.TrySetResult();

        public async Task<HttpResponseMessage> PostChatCompletionAsync(
            Provider provider, string apiKey, byte[] body, CancellationToken ct)
        {
            _entered.TrySetResult();
            await _release.Task;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SseBody, Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    private static HttpResponseMessage Sse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(SseBody, Encoding.UTF8, "text/event-stream"),
    };

    private static HttpResponseMessage Resp429(string body = """{"error":{"message":"rate limited"}}""") =>
        new(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage Resp401() =>
        new(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"error":{"message":"invalid key"}}""",
                Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage Resp403() =>
        new(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"error":{"message":"forbidden"}}""",
                Encoding.UTF8, "application/json"),
        };

    /// <summary>404 thường (không có error.code) — ClassifyFatal xếp cấp Provider.</summary>
    private static HttpResponseMessage Resp404Other() =>
        new(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"error":{"message":"no such endpoint"}}""",
                Encoding.UTF8, "application/json"),
        };

    /// <summary>404 có error.code=model_not_found — ClassifyFatal xếp cấp Model.</summary>
    private static HttpResponseMessage Resp404ModelNotFound() =>
        new(HttpStatusCode.NotFound)
        {
            Content = new StringContent(
                """{"error":{"message":"The model does not exist","code":"model_not_found"}}""",
                Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage Resp429RetryAfter(string body, string retryAfter)
    {
        var response = Resp429(body);
        response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        return response;
    }

    /// <summary>Đọc trường <c>model</c> trong body upstream — phân biệt attempt đi model nào.</summary>
    private static string ModelOf(byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("model").GetString()!;
    }

    private sealed class ScriptedUpstream(Func<Provider, HttpResponseMessage> factory) : IUpstreamClient
    {
        public int Calls;

        public Task<HttpResponseMessage> PostChatCompletionAsync(
            Provider provider, string apiKey, byte[] body, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            // Factory throw được (mạng giả) — ném đồng bộ, handler bắt trong try có sẵn
            return Task.FromResult(factory(provider));
        }
    }

    // Góc capacity: call#1 giữ slot tới khi Release, call#2 lỗi 429 (advance), call#3+ OK
    private sealed class CapacityCornerUpstream : IUpstreamClient
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);
        public Task Entered => _entered.Task;
        public void Release() => _release.TrySetResult();

        public async Task<HttpResponseMessage> PostChatCompletionAsync(
            Provider provider, string apiKey, byte[] body, CancellationToken ct)
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                _entered.TrySetResult();
                await _release.Task;
                return Sse();
            }
            return call == 2 ? Resp429() : Sse();
        }
    }

    /// <summary>
    /// Upstream ghi nhận (provider, apiKey) mỗi lần gọi rồi ủy factory — test xác nhận đúng
    /// TK/provider nào đã nhận request; factory throw được (mạng giả / OCE).
    /// </summary>
    private sealed class WalkUpstream(
        Func<Provider, string, byte[], CancellationToken, Task<HttpResponseMessage>> factory)
        : IUpstreamClient
    {
        private readonly List<(string Provider, string Key)> _calls = [];

        public IReadOnlyList<(string Provider, string Key)> Calls
        {
            get
            {
                lock (_calls)
                    return _calls.ToArray();
            }
        }

        public Task<HttpResponseMessage> PostChatCompletionAsync(
            Provider provider, string apiKey, byte[] body, CancellationToken ct)
        {
            lock (_calls)
                _calls.Add((provider.Name, apiKey));
            return factory(provider, apiKey, body, ct);
        }
    }

    private sealed class CapturingLog : ILogService
    {
        public List<string> Infos { get; } = [];
        public List<string> Warns { get; } = [];
        public List<string> Errors { get; } = [];
        public List<string> Debugs { get; } = [];

        public event Action<LogEntry>? LogAdded { add { } remove { } }

        // ChatCompletionsHandler ghi journal qua Write (spec §7) — route theo Severity để các
        // assert Infos/Warns/Errors cũ (LogForwarded, lỗi upstream) vẫn bắt được dòng mới
        public void Write(LogEntry entry)
        {
            switch (entry.Severity)
            {
                case LogSeverity.Debug: Debugs.Add(entry.Message); break;
                case LogSeverity.Warning: Warns.Add(entry.Message); break;
                case LogSeverity.Error: Errors.Add(entry.Message); break;
                default: Infos.Add(entry.Message); break;
            }
        }
        public void Info(string message, LogCategory category = LogCategory.App) =>
            Write(new LogEntry { Severity = LogSeverity.Info, Category = category, Message = message });
        public void Warn(string message, LogCategory category = LogCategory.App) =>
            Write(new LogEntry { Severity = LogSeverity.Warning, Category = category, Message = message });
        public void Error(string message, Exception? exception = null, LogCategory category = LogCategory.App) =>
            Write(new LogEntry { Severity = LogSeverity.Error, Category = category, Message = message });
        public void LogRequestUsage(string? requestId, long? clientKeyId, int promptTokens, int completionTokens) { }
        public IReadOnlyList<LogEntry> Query(LogQuery query) => [];
        public int Count(LogQuery query) => 0;
    }

    [Fact]
    public async Task Loop_WhenRequestEnqueued_ResolvesSelectsServesAndCompletesHandled()
    {
        var pid = SeedProvider("p1");
        var resolver = new StubResolver(new SelectionSuccess([Candidate(pid)], ComboMode.RoundRobin));
        var selector = new CountingSelector(new ModelSelector(_executions));
        var upstream = new StubUpstream();
        var log = new CapturingLog();
        await StartAsync(resolver, selector, upstream, log);

        var request = Req("req00001");
        _queue.Enqueue(request);

        var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsType<DispatchOutcome.Handled>(outcome);
        Assert.False(_queue.Contains("req00001"));
        Assert.False(_executions.Contains("req00001"));
        Assert.Equal(1, upstream.Calls);
        Assert.Single(log.Infos);
        Assert.Contains("m1", log.Infos[0]);
        Assert.Contains("p1", log.Infos[0]);
    }

    [Fact]
    public async Task Loop_WhenProviderSaturated_ParksOnceAndWakesOnExited()
    {
        var pid = SeedProvider("p1", maxConcurrent: 1);
        var resolver = new StubResolver(new SelectionSuccess([Candidate(pid)], ComboMode.RoundRobin));
        var selector = new CountingSelector(new ModelSelector(_executions));
        var upstream = new GatedUpstream();
        var log = new CapturingLog();
        await StartAsync(resolver, selector, upstream, log);

        var r1 = Req("req00001");
        var r2 = Req("req00002");
        _queue.Enqueue(r1);
        await upstream.Entered.WaitAsync(TimeSpan.FromSeconds(5)); // r1 giữ trọn slot
        // BackgroundService.StartAsync chạy ExecuteAsync qua Task.Run — enqueue r2 ngay lập tức
        // race với lần drain r1 còn dang dở (token wake sót → thử chọn > 1 lần). Chờ loop thật sự
        // park (r1 đã xong, queue chờ event) rồi mới enqueue để đúng 1 lần thử chọn cho r2.
        await Task.Delay(200);
        _queue.Enqueue(r2);

        // Park: đúng 1 lần thử chọn cho r2 rồi đứng yên — KHÔNG polling capacity lặp lại
        await Task.Delay(300);
        Assert.Equal(2, selector.Calls);
        Assert.False(_executions.Contains("req00002"));
        Assert.False(r2.Completion.Task.IsCompleted);

        // r1 xong → Exit bắn Exited → wake → r2 được serve (park vô hạn, không 503)
        upstream.Release();
        Assert.IsType<DispatchOutcome.Handled>(await r1.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsType<DispatchOutcome.Handled>(await r2.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, _executions.GetInFlight(pid));
    }

    [Fact]
    public async Task Loop_WhenClientAbortsWhileQueued_DoesNotServeAndKeepsRunning()
    {
        var pid = SeedProvider("p1");
        var resolver = new GatingResolver(new SelectionSuccess([Candidate(pid)], ComboMode.RoundRobin));
        var selector = new CountingSelector(new ModelSelector(_executions));
        var upstream = new StubUpstream();
        var log = new CapturingLog();
        await StartAsync(resolver, selector, upstream, log);

        // Callback copy đúng logic Register của endpoint chat (T7) — spec §3.4/§5 "RequestAborted
        // queued": còn trong queue → TryRemove + log Info + Cancelled, không serve
        using var abort = new CancellationTokenSource();
        var r1 = Req("req00001");
        r1.Context.RequestAborted = abort.Token;
        r1.Context.RequestAborted.Register(() =>
        {
            if (_queue.TryRemove(r1.Id, out var removed))
            {
                log.Info($"Request {r1.Id} bị client ngắt khi đang chờ.", LogCategory.Request);
                removed.Completion.TrySetResult(new DispatchOutcome.Cancelled());
            }
        });
        _queue.Enqueue(r1);
        await resolver.Entered.WaitAsync(TimeSpan.FromSeconds(5)); // dispatcher kẹt trong resolve

        // Client ngắt giữa chừng: TryRemove thắng trước Take — race một bên thắng, không item lơ lửng
        abort.Cancel();
        resolver.OpenGate(); // nhả dispatcher — Take fail → Exit slot + bỏ qua (spec §3.4)

        var r2 = Req("req00002");
        _queue.Enqueue(r2);
        var outcome2 = await r2.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsType<DispatchOutcome.Handled>(outcome2);
        Assert.IsType<DispatchOutcome.Cancelled>(await r1.Completion.Task);
        Assert.False(_queue.Contains("req00001"));
        Assert.Equal(1, upstream.Calls); // chỉ r2 tới upstream — r1 không bao giờ được serve
        Assert.False(_executions.Contains("req00001"));
        Assert.Contains(log.Infos, m => m.Contains(r1.Id) && m.Contains("ngắt"));
    }

    [Fact]
    public async Task Loop_WhenResolveReturnsNotFound_TakesItemAndCompletesError404()
    {
        var resolver = new StubResolver(new SelectionFailure("m1", ResolveFailure.NotFound));
        var selector = new CountingSelector(new ModelSelector(_executions));
        var log = new CapturingLog();
        await StartAsync(resolver, selector, new StubUpstream(), log);

        var request = Req("req00001");
        _queue.Enqueue(request);

        var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var error = Assert.IsType<DispatchOutcome.Error>(outcome);
        Assert.Equal(404, error.Status);
        Assert.Equal("The model 'm1' does not exist", error.Message);
        Assert.Equal("model", error.Param);
        Assert.Equal("model_not_found", error.Code);
        Assert.False(_queue.Contains("req00001")); // item được gỡ khỏi queue khi resolve fail
        Assert.Single(log.Warns);
        Assert.Contains("không tồn tại", log.Warns[0]);
        Assert.Empty(log.Infos);
    }

    [Fact]
    public async Task Loop_WhenResolveReturnsAnthropic_CompletesError503()
    {
        var resolver = new StubResolver(new SelectionFailure("m1", ResolveFailure.AnthropicNotSupported));
        var selector = new CountingSelector(new ModelSelector(_executions));
        var log = new CapturingLog();
        await StartAsync(resolver, selector, new StubUpstream(), log);

        var request = Req("req00001");
        _queue.Enqueue(request);

        var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var error = Assert.IsType<DispatchOutcome.Error>(outcome);
        Assert.Equal(503, error.Status);
        Assert.Equal("The model 'm1' is not supported yet", error.Message);
        Assert.Equal("server_error", error.Type);
        Assert.Null(error.Param);
        Assert.Null(error.Code);
        Assert.Single(log.Warns);
        Assert.Contains("Anthropic", log.Warns[0]);
    }

    [Fact]
    public async Task Loop_WhenResolverThrows_LogsErrorAndKeepsServing()
    {
        var pid = SeedProvider("p1");
        var success = new SelectionSuccess([Candidate(pid)], ComboMode.RoundRobin);
        var resolver = new ThrowingOnceResolver(success);
        var selector = new CountingSelector(new ModelSelector(_executions));
        var upstream = new StubUpstream();
        var log = new CapturingLog();

        // Enqueue TRƯỚC StartAsync: ExecuteAsync chạy async trên thread pool nên enqueue sau
        // StartAsync có thể để release của Changed "treo" khi loop đang dispatch (chưa tới
        // WaitAsync) — wake thừa đó retry ngay sau lỗi làm item bị serve trước khi assert.
        var r1 = Req("req00001");
        _queue.Enqueue(r1);
        await StartAsync(resolver, selector, upstream, log);
        await WaitUntilAsync(() => log.Errors.Count == 1);

        Assert.Contains("Lỗi dispatcher", log.Errors[0]);
        Assert.True(_queue.Contains("req00001")); // exception trước Take — item giữ nguyên

        // Event sau (enqueue r2) → retry r1 (lần gọi resolver thứ 2 thành công) → cả 2 được serve
        var r2 = Req("req00002");
        _queue.Enqueue(r2);

        Assert.IsType<DispatchOutcome.Handled>(await r1.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsType<DispatchOutcome.Handled>(await r2.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, upstream.Calls);
        Assert.Single(log.Errors);
    }

    [Fact]
    public async Task Loop_WhenFirstCandidate429_AdvancesToSecondProviderAndCompletesHandled()
    {
        var p1 = SeedProvider("p1", modelId: "m1");
        var p2 = SeedProvider("p2", modelId: "m1"); // cùng model 2 provider — RR sort (p1, p2)
        var resolver = new StubResolver(new SelectionSuccess([Candidate(p1), Candidate(p2)], ComboMode.RoundRobin));
        var selector = new CountingSelector(new ModelSelector(_executions));
        var upstream = new ScriptedUpstream(p => p.Name == "p1" ? Resp429() : Sse());
        var log = new CapturingLog();
        await StartAsync(resolver, selector, upstream, log);

        var request = Req("req00001");
        _queue.Enqueue(request);

        var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsType<DispatchOutcome.Handled>(outcome);
        Assert.Equal(2, upstream.Calls);
        // Advance Warn nêu đúng provider/model/status/request rồi chuyển candidate kế (§5)
        Assert.Contains(log.Warns, w =>
            w.Contains("Chuyển candidate kế") && w.Contains("'p1'/'m1'")
            && w.Contains("HTTP 429") && w.Contains("req00001"));
        Assert.Contains(log.Infos, i => i.Contains("m1") && i.Contains("p2")); // chỉ Info lần thành công
        Assert.False(_executions.Contains("req00001"));
    }

    [Fact]
    public async Task Loop_WhenAllCandidates429_CompletesPassthroughWithLastResponseAndLogsExhaustion()
    {
        var p1 = SeedProvider("p1", modelId: "m1");
        var p2 = SeedProvider("p2", modelId: "m1");
        var resolver = new StubResolver(new SelectionSuccess([Candidate(p1), Candidate(p2)], ComboMode.RoundRobin));
        var selector = new CountingSelector(new ModelSelector(_executions));
        var upstream = new ScriptedUpstream(p => p.Name == "p1"
            ? Resp429("""{"error":{"message":"from-p1"}}""")
            : Resp429("""{"error":{"message":"from-p2"}}"""));
        var log = new CapturingLog();
        await StartAsync(resolver, selector, upstream, log);

        var request = Req("req00001");
        _queue.Enqueue(request);

        var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Exhaustion contract: attempt cuối (p2) quyết định — passthrough nguyên response đó (§4)
        var passthrough = Assert.IsType<DispatchOutcome.Passthrough>(outcome);
        Assert.Equal(429, passthrough.Status);
        Assert.Equal("""{"error":{"message":"from-p2"}}""", Encoding.UTF8.GetString(passthrough.Body));
        Assert.Equal(2, upstream.Calls);
        Assert.Contains(log.Errors,
            e => e.Contains("req00001") && e.Contains("thất bại sau 2 candidate"));
        // Đúng 1 Warn advance (p1→p2) — hết candidate nên không log lần 2
        Assert.Single(log.Warns);
        // Handler không tự ghi response — ctx untouched cho endpoint ghi
        Assert.Equal(200, request.Context.Response.StatusCode);
        Assert.Equal(0, ((MemoryStream)request.Context.Response.Body).Length);
    }

    [Fact]
    public async Task ServeAsync_WhenExhaustedOnNetworkFailure_Returns502()
    {
        var p1 = SeedProvider("p1", modelId: "m1");
        var p2 = SeedProvider("p2", modelId: "m1");
        var resolver = new StubResolver(new SelectionSuccess([Candidate(p1), Candidate(p2)], ComboMode.RoundRobin));
        var selector = new CountingSelector(new ModelSelector(_executions));
        var upstream = new ScriptedUpstream(_ => throw new HttpRequestException("connection refused"));
        var log = new CapturingLog();
        await StartAsync(resolver, selector, upstream, log);

        var request = Req("req00001");
        _queue.Enqueue(request);

        var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Attempt cuối là mạng → 502 y như 3A, không passthrough body rỗng (§4)
        var error = Assert.IsType<DispatchOutcome.Error>(outcome);
        Assert.Equal(502, error.Status);
        Assert.Equal("Upstream provider request failed", error.Message);
        Assert.Equal(2, upstream.Calls);
        // 2 lỗi mạng của handler + 1 exhaustion của dispatcher
        Assert.Equal(3, log.Errors.Count);
        Assert.Contains(log.Errors, e => e.Contains("thất bại sau 2 candidate"));
    }

    [Fact]
    public async Task Loop_WhenFirstCandidate429ThenNetworkError_CompletesError502OnLastAttempt()
    {
        var p1 = SeedProvider("p1", modelId: "m1");
        var p2 = SeedProvider("p2", modelId: "m1");
        var resolver = new StubResolver(new SelectionSuccess([Candidate(p1), Candidate(p2)], ComboMode.RoundRobin));
        var selector = new CountingSelector(new ModelSelector(_executions));
        var upstream = new ScriptedUpstream(p => p.Name == "p1"
            ? Resp429()
            : throw new HttpRequestException("connection refused"));
        var log = new CapturingLog();
        await StartAsync(resolver, selector, upstream, log);

        var request = Req("req00001");
        _queue.Enqueue(request);

        var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Attempt cuối (mạng) quyết định — không lấy response 429 của attempt đầu (§4)
        var error = Assert.IsType<DispatchOutcome.Error>(outcome);
        Assert.Equal(502, error.Status);
        Assert.Equal("Upstream provider request failed", error.Message);
        Assert.Equal(2, upstream.Calls);
        Assert.Contains(log.Warns, w => w.Contains("HTTP 429"));
    }

    [Fact]
    public async Task Loop_WhenCandidateReturns400_CompletesPassthroughWithoutAdvancing()
    {
        var p1 = SeedProvider("p1", modelId: "m1");
        var p2 = SeedProvider("p2", modelId: "m1");
        var resolver = new StubResolver(new SelectionSuccess([Candidate(p1), Candidate(p2)], ComboMode.RoundRobin));
        var selector = new CountingSelector(new ModelSelector(_executions));
        var upstream = new ScriptedUpstream(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":{"message":"bad"}}""", Encoding.UTF8, "application/json"),
        });
        var log = new CapturingLog();
        await StartAsync(resolver, selector, upstream, log);

        var request = Req("req00001");
        _queue.Enqueue(request);

        var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 4xx non-retryable: passthrough NGAY — không advance, không cộng counter (spec exhaustive-failover §3.1)
        var passthrough = Assert.IsType<DispatchOutcome.Passthrough>(outcome);
        Assert.Equal(400, passthrough.Status);
        Assert.Equal(1, upstream.Calls);
        Assert.Empty(log.Warns);
        Assert.Empty(log.Errors);
        Assert.Contains(log.Infos, i => i.Contains("HTTP 400")); // Info parity 3A
    }

    [Fact]
    public async Task Loop_WhenFirstCandidate401_AdvancesToSecondProvider()
    {
        var p1 = SeedProvider("p1", modelId: "m1");
        var p2 = SeedProvider("p2", modelId: "m1"); // cùng model 2 provider — RR sort (p1, p2)
        var resolver = new StubResolver(new SelectionSuccess([Candidate(p1), Candidate(p2)], ComboMode.RoundRobin));
        var selector = new CountingSelector(new ModelSelector(_executions));
        var upstream = new ScriptedUpstream(p => p.Name == "p1"
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"error":{"message":"invalid key"}}""",
                    Encoding.UTF8, "application/json"),
            }
            : Sse());
        var log = new CapturingLog();
        await StartAsync(resolver, selector, upstream, log);

        var request = Req("req00001");
        _queue.Enqueue(request);

        var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Fatal 401 cấp Account → TK kế cùng provider (p1 chỉ 1 TK → NoAccountLeft) rồi mới
        // provider kế — client thấy 200 của p2 (spec exhaustive-failover §1.3 #2/§3.1)
        Assert.IsType<DispatchOutcome.Handled>(outcome);
        Assert.Equal(2, upstream.Calls);
        Assert.Contains(log.Warns, w =>
            w.Contains("Chuyển candidate kế") && w.Contains("'p1'/'m1'")
            && w.Contains("HTTP 401") && w.Contains("req00001"));
    }

    [Fact]
    public async Task Loop_WhenAllCandidatesFatal404_CompletesPassthroughWithLastResponse()
    {
        var p1 = SeedProvider("p1", modelId: "m1");
        var p2 = SeedProvider("p2", modelId: "m2");
        var resolver = new StubResolver(new SelectionSuccess([Candidate(p1), Candidate(p2)], ComboMode.Fallback));
        var selector = new CountingSelector(new ModelSelector(_executions));
        var upstream = new ScriptedUpstream(p => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(
                $"{{\"error\":{{\"message\":\"from-{p.Name}\"}}}}", Encoding.UTF8, "application/json"),
        });
        var log = new CapturingLog();
        await StartAsync(resolver, selector, upstream, log);

        var request = Req("req00001");
        _queue.Enqueue(request);

        var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 404 thường = sai endpoint/provider — hết candidate
        // passthrough nguyên response cuối (§4), có log Error exhaustion (§5)
        var passthrough = Assert.IsType<DispatchOutcome.Passthrough>(outcome);
        Assert.Equal(404, passthrough.Status);
        Assert.Contains("from-p2", Encoding.UTF8.GetString(passthrough.Body));
        Assert.Equal(2, upstream.Calls);
        Assert.Contains(log.Errors,
            e => e.Contains("req00001") && e.Contains("thất bại sau 2 candidate"));
        Assert.Contains(log.Warns, w => w.Contains("Chuyển candidate kế") && w.Contains("HTTP 404"));
    }

    [Fact]
    public async Task Loop_WhenNextCandidateHasNoCapacity_ReenqueuesAndWakesWhenSlotFrees()
    {
        var p1 = SeedProvider("p1", maxConcurrent: 1, modelId: "m1");
        var p2 = SeedProvider("p2", maxConcurrent: 1, modelId: "m1");
        var resolver = new StubResolver(new SelectionSuccess([Candidate(p1), Candidate(p2)], ComboMode.RoundRobin));
        var selector = new CountingSelector(new ModelSelector(_executions));
        var upstream = new CapacityCornerUpstream();
        var log = new CapturingLog();
        await StartAsync(resolver, selector, upstream, log);

        // r2 enqueue trước → cursor 0 → p1, call#1 giữ trọn slot (gated)
        var r2 = Req("req00002");
        _queue.Enqueue(r2);
        await upstream.Entered.WaitAsync(TimeSpan.FromSeconds(5));

        // r1 → p2 (p1 bận), call#2 → 429 → advance p1 nhưng p1 kẹt → re-enqueue (park)
        var r1 = Req("req00001");
        _queue.Enqueue(r1);
        await WaitUntilAsync(() => _queue.Contains("req00001") && upstream.Calls == 2);
        await Task.Delay(200); // chắc chắn đã park — không còn call nào chạy dở
        Assert.Equal(2, upstream.Calls);
        Assert.False(r1.Completion.Task.IsCompleted);

        // r2 xong → Exit p1 → Exited wake → r1 dispatch lại (chỉ còn p1 chưa thử) → call#3 OK
        upstream.Release();
        Assert.IsType<DispatchOutcome.Handled>(await r2.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsType<DispatchOutcome.Handled>(await r1.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(3, upstream.Calls);
        Assert.False(_queue.Contains("req00001"));
    }

    [Fact]
    public async Task ServeAsync_When401_TriesNextAccountOfSameProvider_BeforeChangingProvider()
    {
        var pidA = SeedProvider("p1", accounts: 2);
        var pidB = SeedProvider("p2");
        var resolver = new StubResolver(new SelectionSuccess(
            [Candidate(pidA), Candidate(pidB)], ComboMode.RoundRobin));
        var selector = new CountingSelector(new ModelSelector(_executions));
        var upstream = new WalkUpstream((p, key, body, ct) => Task.FromResult(
            key == "sk-p1-a1" ? Resp401() : Sse()));
        var log = new CapturingLog();
        await StartAsync(resolver, selector, upstream, log);

        var request = Req("req00001");
        _queue.Enqueue(request);

        var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 401 cấp Account: thử TK kế cùng provider TRƯỚC khi đổi provider
        Assert.IsType<DispatchOutcome.Handled>(outcome);
        var calls = upstream.Calls;
        Assert.Equal(2, calls.Count);
        Assert.Equal(("p1", "sk-p1-a1"), calls[0]);
        Assert.Equal(("p1", "sk-p1-a2"), calls[1]);
        Assert.DoesNotContain(calls, c => c.Provider == "p2"); // KHÔNG gửi sang provider B
        Assert.False(_executions.Contains("req00001"));
    }

    [Fact]
    public async Task ServeAsync_When429_TriesNextAccountOfSameProvider()
    {
        var pidA = SeedProvider("p1", accounts: 2);
        var pidB = SeedProvider("p2");
        var resolver = new StubResolver(new SelectionSuccess(
            [Candidate(pidA), Candidate(pidB)], ComboMode.RoundRobin));
        var selector = new CountingSelector(new ModelSelector(_executions));
        var upstream = new WalkUpstream((p, key, body, ct) => Task.FromResult(
            key == "sk-p1-a1" ? Resp429() : Sse()));
        var log = new CapturingLog();
        await StartAsync(resolver, selector, upstream, log);

        var request = Req("req00001");
        _queue.Enqueue(request);

        var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Retryable (429) dispatcher gán cấp Account — TK kế cùng provider trước provider kế
        Assert.IsType<DispatchOutcome.Handled>(outcome);
        var calls = upstream.Calls;
        Assert.Equal(2, calls.Count);
        Assert.Equal(("p1", "sk-p1-a1"), calls[0]);
        Assert.Equal(("p1", "sk-p1-a2"), calls[1]);
        Assert.DoesNotContain(calls, c => c.Provider == "p2");
    }

    [Fact]
    public async Task ServeAsync_WhenAllAccountsTried_MovesToNextProvider()
    {
        var pidA = SeedProvider("p1", accounts: 2);
        var pidB = SeedProvider("p2");
        var resolver = new StubResolver(new SelectionSuccess(
            [Candidate(pidA), Candidate(pidB)], ComboMode.RoundRobin));
        var selector = new CountingSelector(new ModelSelector(_executions));
        var upstream = new WalkUpstream((p, key, body, ct) => Task.FromResult(key switch
        {
            "sk-p1-a1" => Resp401(),
            "sk-p1-a2" => Resp403(),
            _ => Sse(),
        }));
        var log = new CapturingLog();
        await StartAsync(resolver, selector, upstream, log);

        var request = Req("req00001");
        _queue.Enqueue(request);

        var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Hết TK của p1 (401 + 403) → mới rơi xuống p2, client thấy 200
        Assert.IsType<DispatchOutcome.Handled>(outcome);
        var calls = upstream.Calls;
        Assert.Equal(3, calls.Count);
        Assert.Equal(("p1", "sk-p1-a1"), calls[0]);
        Assert.Equal(("p1", "sk-p1-a2"), calls[1]);
        Assert.Equal("p2", calls[2].Provider);
    }

    [Fact]
    public async Task ServeAsync_WhenAllAccountsOfProviderAUnauthorized_MovesToProviderB()
    {
        // Review Focus #4 — exclude scope theo provider: tập TK đã thử của p1 KHÔNG loại p2
        var pidA = SeedProvider("p1", accounts: 2);
        var pidB = SeedProvider("p2");
        var resolver = new StubResolver(new SelectionSuccess(
            [Candidate(pidA), Candidate(pidB)], ComboMode.RoundRobin));
        var selector = new CountingSelector(new ModelSelector(_executions));
        var upstream = new WalkUpstream((p, key, body, ct) => Task.FromResult(
            p.Name == "p1" ? Resp401() : Sse()));
        var log = new CapturingLog();
        await StartAsync(resolver, selector, upstream, log);

        var request = Req("req00001");
        _queue.Enqueue(request);

        var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsType<DispatchOutcome.Handled>(outcome);
        var calls = upstream.Calls;
        Assert.Equal(3, calls.Count);
        Assert.Equal("sk-p1-a1", calls[0].Key);
        Assert.Equal("sk-p1-a2", calls[1].Key);
        Assert.Equal(("p2", "sk-p2-a1"), calls[2]); // TK của p2 vẫn được chọn
    }

    [Fact]
    public async Task ServeAsync_WhenNetworkError_SkipsProvider_WithoutTryingOtherAccounts()
    {
        var pidA = SeedProvider("p1");
        AddModel(pidA, "m2");
        var pidB = SeedProvider("p2");
        // Fallback để attempt theo thứ tự Position xác định: A/m1 → A/m2 → B/m1
        var resolver = new StubResolver(new SelectionSuccess(
            [Candidate(pidA, "m1"), Candidate(pidA, "m2"), Candidate(pidB, "m1")],
            ComboMode.Fallback));
        var selector = new CountingSelector(new ModelSelector(_executions));
        var upstream = new WalkUpstream((p, key, body, ct) => p.Name == "p1"
            ? throw new HttpRequestException("connection refused")
            : Task.FromResult(Sse()));
        var log = new CapturingLog();
        await StartAsync(resolver, selector, upstream, log);

        var request = Req("req00001");
        _queue.Enqueue(request);

        var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Lỗi mạng = provider-wide: bỏ CẢ provider, không quét model/TK khác cùng provider
        Assert.IsType<DispatchOutcome.Handled>(outcome);
        Assert.Equal(2, upstream.Calls.Count);
        Assert.Equal(1, upstream.Calls.Count(c => c.Provider == "p1")); // A/m2 không bao giờ được thử
        Assert.Equal("p2", upstream.Calls[1].Provider);
    }

    [Fact]
    public async Task ServeAsync_WhenModelNotFound_SkipsCandidate()
    {
        var pid = SeedProvider("p1");
        AddModel(pid, "m2");
        var resolver = new StubResolver(new SelectionSuccess(
            [Candidate(pid, "m1"), Candidate(pid, "m2")], ComboMode.RoundRobin));
        var selector = new CountingSelector(new ModelSelector(_executions));
        var triedModels = new List<string>();
        var upstream = new WalkUpstream((p, key, body, ct) =>
        {
            lock (triedModels)
                triedModels.Add(ModelOf(body));
            return Task.FromResult(ModelOf(body) == "m1" ? Resp404ModelNotFound() : Sse());
        });
        var log = new CapturingLog();
        await StartAsync(resolver, selector, upstream, log);

        var request = Req("req00001");
        _queue.Enqueue(request);

        var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 404 model_not_found = bỏ đúng cặp (provider, model) — model khác cùng provider vẫn thử
        Assert.IsType<DispatchOutcome.Handled>(outcome);
        Assert.Equal(2, upstream.Calls.Count);
        Assert.Equal(new[] { "m1", "m2" }, triedModels);
    }

    [Fact]
    public async Task ServeAsync_When404Other_SkipsProvider()
    {
        var pidA = SeedProvider("p1");
        AddModel(pidA, "m2");
        var pidB = SeedProvider("p2");
        var resolver = new StubResolver(new SelectionSuccess(
            [Candidate(pidA, "m1"), Candidate(pidA, "m2"), Candidate(pidB, "m1")],
            ComboMode.Fallback));
        var selector = new CountingSelector(new ModelSelector(_executions));
        var upstream = new WalkUpstream((p, key, body, ct) => Task.FromResult(
            p.Name == "p1" ? Resp404Other() : Sse()));
        var log = new CapturingLog();
        await StartAsync(resolver, selector, upstream, log);

        var request = Req("req00001");
        _queue.Enqueue(request);

        var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 404 thường = sai endpoint/provider — bỏ nguyên provider (model khác cùng provider cũng không thử)
        Assert.IsType<DispatchOutcome.Handled>(outcome);
        Assert.Equal(2, upstream.Calls.Count);
        Assert.Equal(1, upstream.Calls.Count(c => c.Provider == "p1"));
        Assert.Equal("p2", upstream.Calls[1].Provider);
    }

    [Fact]
    public async Task ServeAsync_WhenNoAccountLeft_MarksPairAndAdvances()
    {
        // Review Focus #3 — TryEnter giả trả NoAccountLeft tại account-advance
        var pidA = SeedProvider("p1");
        var pidB = SeedProvider("p2");
        var resolver = new StubResolver(new SelectionSuccess(
            [Candidate(pidA), Candidate(pidB)], ComboMode.RoundRobin));
        var selector = new CountingSelector(new ModelSelector(_executions));
        var upstream = new WalkUpstream((p, key, body, ct) => Task.FromResult(
            p.Name == "p1" ? Resp401() : Sse()));
        var log = new CapturingLog();
        await StartAsync(resolver, selector, upstream, log,
            new NoAccountLeftOnSecondEnter(_executions, pidA));

        var request = Req("req00001");
        _queue.Enqueue(request);

        var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsType<DispatchOutcome.Handled>(outcome);
        Assert.True(request.Retry.IsTried(pidA, "m1")); // pair bị đánh dấu trước khi advance
        Assert.True(request.Retry.IsAccountTried(pidA, AccountIdOf(pidA, "a1")));
        Assert.Equal(2, upstream.Calls.Count); // p1 đúng 1 attempt — không loop
        Assert.Equal("p2", upstream.Calls[1].Provider);
    }

    [Fact]
    public async Task ServeAsync_WhenUntriedAccountsFull_Reenqueues_AndDoesNotRetryTriedAccounts()
    {
        // Review Focus #2 — park khi TK chưa thử đầy → Exited wake → chỉ thử TK chưa fail
        var pid = SeedProvider("p1", maxConcurrent: 1, accounts: 2);
        var resolver = new StubResolver(new SelectionSuccess([Candidate(pid)], ComboMode.RoundRobin));
        var selector = new CountingSelector(new ModelSelector(_executions));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a1Calls = 0;
        var upstream = new WalkUpstream(async (p, key, body, ct) =>
        {
            // call đầu của a1 (r0) giữ slot tới khi release; mọi attempt khác → 401
            if (key == "sk-p1-a1" && Interlocked.Increment(ref a1Calls) == 1)
            {
                await release.Task;
                return Sse();
            }
            return Resp401();
        });
        var log = new CapturingLog();
        await StartAsync(resolver, selector, upstream, log);

        var r0 = Req("req00001");
        _queue.Enqueue(r0);
        await WaitUntilAsync(() => upstream.Calls.Count >= 1); // r0 giữ trọn a1 (gated)
        await Task.Delay(200); // chắc loop đã chờ event — enqueue r1 để TryEnter thấy a1 đầy

        var r1 = Req("req00002");
        _queue.Enqueue(r1);
        // r1 vào a2 → 401 → a2 đánh dấu, a1 đầy → park (KHÔNG đánh dấu pair)
        await WaitUntilAsync(() => _queue.Contains("req00002") && upstream.Calls.Count == 2);
        Assert.False(r1.Completion.Task.IsCompleted);

        // r0 xong → Exited wake → dispatch lại: exclude={a2} → chỉ thử a1 (TK chưa fail)
        release.SetResult();
        Assert.IsType<DispatchOutcome.Handled>(await r0.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        var outcome = await r1.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // a1 cũng 401 → NoAccountLeft → pair marked → exhaustion passthrough attempt cuối
        var passthrough = Assert.IsType<DispatchOutcome.Passthrough>(outcome);
        Assert.Equal(401, passthrough.Status);
        Assert.Equal(2, upstream.Calls.Count(c => c.Key == "sk-p1-a1")); // r0 (200) + r1 (401)
        Assert.Equal(1, upstream.Calls.Count(c => c.Key == "sk-p1-a2")); // KHÔNG thử lại TK đã fail
        Assert.False(_queue.Contains("req00002"));
        Assert.False(_executions.Contains("req00002"));
    }

    [Fact]
    public async Task ServeAsync_WhenExhausted_PassesThroughLastHttpResponse()
    {
        var pidA = SeedProvider("p1");
        var pidB = SeedProvider("p2");
        var resolver = new StubResolver(new SelectionSuccess(
            [Candidate(pidA), Candidate(pidB)], ComboMode.RoundRobin));
        var selector = new CountingSelector(new ModelSelector(_executions));
        var upstream = new WalkUpstream((p, key, body, ct) => Task.FromResult(
            p.Name == "p1" ? Resp429() : Resp429RetryAfter("""{"error":{"message":"final"}}""", "7")));
        var log = new CapturingLog();
        await StartAsync(resolver, selector, upstream, log);

        var request = Req("req00001");
        _queue.Enqueue(request);

        var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Exhaustion contract: attempt cuối có HTTP → passthrough nguyên status + body + Retry-After (§4)
        var passthrough = Assert.IsType<DispatchOutcome.Passthrough>(outcome);
        Assert.Equal(429, passthrough.Status);
        Assert.Equal("""{"error":{"message":"final"}}""", Encoding.UTF8.GetString(passthrough.Body));
        Assert.Equal("7", passthrough.RetryAfterHeader);
        Assert.Equal(2, upstream.Calls.Count);
        Assert.Contains(log.Errors,
            e => e.Contains("req00001") && e.Contains("thất bại sau 2 candidate"));
    }

    [Fact]
    public async Task ServeAsync_WhenClientAbortsDuringAccountSwitch_ReturnsAborted()
    {
        var pid = SeedProvider("p1", accounts: 2);
        var resolver = new StubResolver(new SelectionSuccess([Candidate(pid)], ComboMode.RoundRobin));
        var selector = new CountingSelector(new ModelSelector(_executions));
        using var abort = new CancellationTokenSource();
        var calls = 0;
        var upstream = new WalkUpstream((p, key, body, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                abort.Cancel(); // client ngắt NGAY sau fail đầu — giữa 2 lần thử TK
                return Task.FromResult(Resp401());
            }
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Sse());
        });
        var log = new CapturingLog();
        await StartAsync(resolver, selector, upstream, log);

        var request = Req("req00001");
        request.Context.RequestAborted = abort.Token;
        _queue.Enqueue(request);

        var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsType<DispatchOutcome.Aborted>(outcome);
        // Aborted: endpoint không ghi JSON — stream untouched
        Assert.Equal(0, ((MemoryStream)request.Context.Response.Body).Length);
        Assert.Equal(200, request.Context.Response.StatusCode);
        Assert.Equal("sk-p1-a1", upstream.Calls[0].Key); // fail đầu trên TK1, không serve tiếp
    }

    [Fact]
    public async Task TryDispatch_WhenComboFallback_KeepsPositionalOrderAcrossProviders()
    {
        var pidA = SeedProvider("p1", accounts: 2);
        var pidB = SeedProvider("p2");
        var resolver = new StubResolver(new SelectionSuccess(
            [Candidate(pidA), Candidate(pidB)], ComboMode.Fallback));
        var selector = new CountingSelector(new ModelSelector(_executions));
        var upstream = new WalkUpstream((p, key, body, ct) => Task.FromResult(key switch
        {
            "sk-p1-a1" => Resp401(),
            "sk-p1-a2" => Resp403(),
            _ => Sse(),
        }));
        var log = new CapturingLog();
        await StartAsync(resolver, selector, upstream, log);

        var request = Req("req00001");
        _queue.Enqueue(request);

        var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Fallback: exhaust mọi TK của Position 1 (p1) theo thứ tự mới rơi xuống Position 2 (p2)
        Assert.IsType<DispatchOutcome.Handled>(outcome);
        var calls = upstream.Calls;
        Assert.Equal(3, calls.Count);
        Assert.Equal("sk-p1-a1", calls[0].Key);
        Assert.Equal("sk-p1-a2", calls[1].Key);
        Assert.Equal("p2", calls[2].Provider);
    }

    [Fact]
    public async Task TryDispatch_WhenWalkEmptyAndNothingTried_Returns503()
    {
        var pid = SeedProvider("p1");
        SetAccountsEnabled(pid, false); // walk rỗng ngay — mọi TK disabled, CHƯA thử ai
        var resolver = new StubResolver(new SelectionSuccess([Candidate(pid)], ComboMode.RoundRobin));
        var selector = new CountingSelector(new ModelSelector(_executions));
        var upstream = new StubUpstream();
        var log = new CapturingLog();
        await StartAsync(resolver, selector, upstream, log);

        var request = Req("req00001");
        _queue.Enqueue(request);

        var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Chưa thử ai → 503 tạm thời (không phải exhaustion passthrough/502)
        var error = Assert.IsType<DispatchOutcome.Error>(outcome);
        Assert.Equal(503, error.Status);
        Assert.Equal("The model 'm1' is temporarily unavailable", error.Message);
        Assert.Equal("server_error", error.Type);
        Assert.Equal(0, upstream.Calls);
        Assert.False(_queue.Contains("req00001"));
    }

    [Fact]
    public async Task Loop_WhenRequeuedRequestHasNoUntriedCandidates_CompletesExhaustionInsteadOfGate503()
    {
        // Re-dispatch với HasTried=true (p2 đã fail cấp Provider) + không còn candidate nào
        // (tắt TK p1 qua DB giữa chừng) → exhaustion, KHÔNG phải 503 walk-rỗng.
        var pidA = SeedProvider("p1", maxConcurrent: 1);
        var pidB = SeedProvider("p2");
        // Barrier ở resolve #3 = lần re-dispatch ĐẦU TIÊN sau khi r1 park (resolve #1 = r0,
        // #2 = dispatch của r1): chặn trước khi đọc DB → test tắt TK rồi mới mở → không straddle.
        var resolver = new BarrierResolver(() => new SelectionSuccess(
            [Candidate(pidA), Candidate(pidB)], ComboMode.RoundRobin), blockAtCall: 3);
        var selector = new CountingSelector(new ModelSelector(_executions));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var upstream = new WalkUpstream(async (p, key, body, ct) =>
        {
            if (p.Name == "p1")
            {
                await release.Task; // r0 giữ slot p1
                return Sse();
            }
            throw new HttpRequestException("connection refused");
        });
        var log = new CapturingLog();
        await StartAsync(resolver, selector, upstream, log);

        var r0 = Req("req00001");
        _queue.Enqueue(r0);
        await WaitUntilAsync(() => upstream.Calls.Count >= 1); // r0 kẹt ở p1 (gated)
        await Task.Delay(200);

        var r1 = Req("req00002");
        _queue.Enqueue(r1);
        // r1 → p2 (p1 đầy) → lỗi mạng → MarkProviderFailed(p2) + MarkTried → p1 đầy → park
        await WaitUntilAsync(() => _queue.Contains("req00002") && upstream.Calls.Count == 2);
        Assert.False(r1.Completion.Task.IsCompleted);

        // Tắt toàn bộ TK p1 — re-dispatch không còn candidate nào chưa thử. Resolve #3 đang
        // chặn ở barrier (trước khi đọc DB) nên snapshot LUÔN đọc thấy TK đã tắt — không có
        // snapshot stale → không đi nhánh sentinel Entered(0) forward 503 (spec §sentinel).
        SetAccountsEnabled(pidA, false);
        resolver.Release();

        // HasTried → exhaustion theo contract: attempt cuối là mạng → 502 (không phải 503)
        var outcome = await r1.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var error = Assert.IsType<DispatchOutcome.Error>(outcome);
        Assert.Equal(502, error.Status);
        Assert.Equal("Upstream provider request failed", error.Message);
        Assert.Equal(2, upstream.Calls.Count); // không attempt nào thêm sau khi park
        Assert.False(_queue.Contains("req00002"));

        // Dọn r0 cuối cùng: release để r0 thoát p1 (không tính vào Calls — cùng call đã gate)
        release.SetResult();
        Assert.IsType<DispatchOutcome.Handled>(await r0.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
