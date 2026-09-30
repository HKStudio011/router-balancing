using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Settings;
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
    private AppSettingsService? _settings;

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
        _settings?.Dispose();
        _db.Dispose();
    }

    private long SeedProvider(string name, int maxConcurrent = 4, string modelId = "m1")
    {
        using var db = _db.CreateFactory().CreateDbContext();
        var provider = new Provider
        {
            Name = name,
            BaseUrl = "https://api.openai.com",
            MaxConcurrent = maxConcurrent,
        };
        provider.Models.Add(new Model { ModelId = modelId, Enabled = true });
        provider.Accounts.Add(new ProviderAccount
        {
            Name = "a1",
            Enabled = true,
            ApiKeyEncrypted = _protector.Protect("sk-live"),
        });
        db.Providers.Add(provider);
        db.SaveChanges();
        return provider.Id;
    }

    private ModelCandidate Candidate(long providerId)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        // Accounts bắt buộc: Handler.ResolveFirstEnabledKey đọc nav này để lấy key (như ComboResolver/ModelResolver)
        var provider = db.Providers
            .Include(p => p.Models)
            .Include(p => p.Accounts)
            .First(p => p.Id == providerId);
        return new ModelCandidate(provider, provider.Models[0]);
    }

    private static ProxyRequest Req(string id, string model = "m1")
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        return new ProxyRequest(id, RequestPriority.Normal, model, Encoding.UTF8.GetBytes("{}"), ctx);
    }

    private async Task StartAsync(IComboResolver resolver, IModelSelector selector,
        IUpstreamClient upstream, CapturingLog log, ModelHealthStore? health = null)
    {
        _settings ??= new AppSettingsService(_db.CreateFactory(), _protector);
        health ??= new ModelHealthStore(_settings, log, TimeProvider.System);
        var handler = new ChatCompletionsHandler(upstream, _protector, log);
        _loop = new DispatcherLoop(_queue, _executions, resolver, selector, handler, log, health);
        await _loop.StartAsync(CancellationToken.None);
    }

    // Test pre-seed fuse cần đúng instance store mà loop sẽ dùng
    private ModelHealthStore NewStore(CapturingLog log)
    {
        _settings ??= new AppSettingsService(_db.CreateFactory(), _protector);
        return new ModelHealthStore(_settings, log, TimeProvider.System);
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

    // Resolve theo model string của request — mỗi request một danh sách candidate khác nhau
    // (test dispatch-time cần r1/r2 map sang combo khác nhau, StubResolver chỉ trả 1 kết quả)
    private sealed class ModelMapResolver(IReadOnlyDictionary<string, SelectionResult> results)
        : IComboResolver
    {
        public Task<SelectionResult> ResolveAsync(string model, CancellationToken ct) =>
            Task.FromResult(results[model]);
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

    private sealed class CountingSelector(IModelSelector inner) : IModelSelector
    {
        public int Calls;

        public Task<ModelCandidate?> TrySelectAsync(SelectionSuccess selection, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return inner.TrySelectAsync(selection, ct);
        }
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

    // Call#1 (request giữ slot) kẹp tới Release rồi kết thúc 400 non-retryable —
    // KHÔNG RecordSuccess nên không reset fuse trong lúc test mở fuse giữa chừng;
    // call#2 (attempt đầu của request walk) lỗi 429; call#3+ không được gọi tới
    // (exhaustion tại dispatch-time không được phép serve thêm).
    private sealed class GatedBadRequestUpstream : IUpstreamClient
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
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("""{"error":{"message":"bad"}}""", Encoding.UTF8,
                        "application/json"),
                };
            }
            return call == 2 ? Resp429("""{"error":{"message":"from-p2"}}""") : Sse();
        }
    }

    private sealed class CapturingLog : ILogService
    {
        public List<string> Infos { get; } = [];
        public List<string> Warns { get; } = [];
        public List<string> Errors { get; } = [];

        public event Action<LogEntry>? LogAdded { add { } remove { } }
        public void Write(LogEntry entry) { }
        public void Info(string message, LogCategory category = LogCategory.App) => Infos.Add(message);
        public void Warn(string message, LogCategory category = LogCategory.App) => Warns.Add(message);
        public void Error(string message, Exception? exception = null, LogCategory category = LogCategory.App) =>
            Errors.Add(message);
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

        // Exhaustion contract: attempt cuối (p2) quyết định — passthrough nguyên response đó (§3.3)
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
    public async Task Loop_WhenAllCandidatesFailWithNetworkError_CompletesError502()
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

        // Attempt cuối là mạng → 502 y như 3A, không passthrough body rỗng (§3.3)
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

        // Attempt cuối (mạng) quyết định — không lấy response 429 của attempt đầu (§3.3)
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

        // 4xx non-retryable: passthrough NGAY — không advance, không cộng counter (§1.4)
        var passthrough = Assert.IsType<DispatchOutcome.Passthrough>(outcome);
        Assert.Equal(400, passthrough.Status);
        Assert.Equal(1, upstream.Calls);
        Assert.Empty(log.Warns);
        Assert.Empty(log.Errors);
        Assert.Contains(log.Infos, i => i.Contains("HTTP 400")); // Info parity 3A
    }

    [Fact]
    public async Task Loop_WhenAllModelsInManualRetry_CompletesError503WithoutUpstreamCall()
    {
        var p1 = SeedProvider("p1", modelId: "m1");
        var p2 = SeedProvider("p2", modelId: "m2");
        var resolver = new StubResolver(new SelectionSuccess([Candidate(p1), Candidate(p2)], ComboMode.Fallback));
        var selector = new CountingSelector(new ModelSelector(_executions));
        var upstream = new ScriptedUpstream(_ => Sse());
        var log = new CapturingLog();
        var store = NewStore(log);
        // Mở fuse cả 2 model — 3 lần RecordFailure mỗi model (MaxRetry=3)
        for (var i = 0; i < 3; i++)
        {
            store.RecordFailure("m1");
            store.RecordFailure("m2");
        }
        await StartAsync(resolver, selector, upstream, log, store);

        var request = Req("req00001");
        _queue.Enqueue(request);

        var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Walk rỗng ngay từ đầu (chưa thử gì) → 503, KHÔNG gọi upstream (§3.3)
        var error = Assert.IsType<DispatchOutcome.Error>(outcome);
        Assert.Equal(503, error.Status);
        Assert.Equal("The model 'm1' is temporarily unavailable", error.Message);
        Assert.Equal(0, upstream.Calls);
    }

    [Fact]
    public async Task Loop_WhenSomeModelsInManualRetry_SkipsDeadModelAndServesHealthyOne()
    {
        var p1 = SeedProvider("p1", modelId: "m1");
        var p2 = SeedProvider("p2", modelId: "m2");
        var resolver = new StubResolver(new SelectionSuccess([Candidate(p1), Candidate(p2)], ComboMode.Fallback));
        var selector = new CountingSelector(new ModelSelector(_executions));
        var upstream = new ScriptedUpstream(_ => Sse());
        var log = new CapturingLog();
        var store = NewStore(log);
        for (var i = 0; i < 3; i++)
            store.RecordFailure("m1"); // chỉ m1 mở fuse
        await StartAsync(resolver, selector, upstream, log, store);

        var request = Req("req00001");
        _queue.Enqueue(request);

        var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Gate walk: model chết bị loại từ đầu — không advance, không Warn "Chuyển candidate kế"
        Assert.IsType<DispatchOutcome.Handled>(outcome);
        Assert.Equal(1, upstream.Calls);
        Assert.Contains(log.Infos, i => i.Contains("m2") && i.Contains("p2"));
        Assert.DoesNotContain(log.Warns, w => w.Contains("Chuyển candidate kế"));
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
    public async Task Loop_WhenServedRequestGets2xx_ResetsModelConsecutiveFailureCounter()
    {
        var pid = SeedProvider("p1");
        var resolver = new StubResolver(new SelectionSuccess([Candidate(pid)], ComboMode.RoundRobin));
        var selector = new CountingSelector(new ModelSelector(_executions));
        var upstream = new StubUpstream();
        var log = new CapturingLog();
        var store = NewStore(log);
        // 2/3 dưới ngưỡng — vẫn Healthy, candidate không bị gate khi dispatch
        store.RecordFailure("m1");
        store.RecordFailure("m1");
        Assert.False(store.IsManualRetry("m1"));
        await StartAsync(resolver, selector, upstream, log, store);

        var request = Req("req00001");
        _queue.Enqueue(request);
        var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 2xx qua dispatcher → health.RecordSuccess("m1") phải reset counter về 0 (spec §6.1)
        Assert.IsType<DispatchOutcome.Handled>(outcome);

        store.RecordFailure("m1");
        store.RecordFailure("m1");
        // Nếu dispatcher không reset: counter đã ở 4/3 → fuse mở từ trước — assert này fail
        Assert.False(store.IsManualRetry("m1"));
        store.RecordFailure("m1");
        // Reset đúng về 0: phải cần đủ 3 lỗi liên tiếp mới mở fuse lại
        Assert.True(store.IsManualRetry("m1"));
    }

    [Fact]
    public async Task Loop_WhenExhaustionTriedTwoDistinctModels_IncrementsFailureCounterOfEachTriedModel()
    {
        var p1 = SeedProvider("p1", modelId: "m1");
        var p2 = SeedProvider("p2", modelId: "m2");
        var resolver = new StubResolver(new SelectionSuccess([Candidate(p1), Candidate(p2)], ComboMode.Fallback));
        var selector = new CountingSelector(new ModelSelector(_executions));
        var upstream = new ScriptedUpstream(_ => Resp429());
        var log = new CapturingLog();
        var store = NewStore(log);
        // m1, m2 pre-seed 1/3 (dưới ngưỡng); m0 ngoài walk — không được cộng (Quyết định #4)
        store.RecordFailure("m1");
        store.RecordFailure("m2");
        store.RecordFailure("m0");
        store.RecordFailure("m0");
        await StartAsync(resolver, selector, upstream, log, store);

        var r1 = Req("req00001");
        _queue.Enqueue(r1);
        Assert.IsType<DispatchOutcome.Passthrough>(
            await r1.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        // Walk thử cả 2 distinct model → mỗi model đúng +1 (2/3), model ngoài TriedModels giữ nguyên
        Assert.False(store.IsManualRetry("m1"));
        Assert.False(store.IsManualRetry("m2"));
        Assert.False(store.IsManualRetry("m0"));

        var r2 = Req("req00002");
        _queue.Enqueue(r2);
        Assert.IsType<DispatchOutcome.Passthrough>(
            await r2.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        // Exhaustion lần 2 → 3/3 mở fuse cả 2 model đã thử; m0 chưa từng thử nên vẫn không mở
        Assert.True(store.IsManualRetry("m1"));
        Assert.True(store.IsManualRetry("m2"));
        Assert.False(store.IsManualRetry("m0"));
        Assert.Equal(4, upstream.Calls);
        Assert.Equal(2, log.Errors.Count(e => e.Contains("thất bại sau 2 candidate")));
    }

    [Fact]
    public async Task Loop_WhenRequeuedRequestHasNoUntriedCandidates_CompletesExhaustionInsteadOfGate503()
    {
        // p1 giữ slot cho request kẹp (call#1); p2 là attempt đã thử của r1
        var p1 = SeedProvider("p1", maxConcurrent: 1, modelId: "m1");
        var p2 = SeedProvider("p2", modelId: "m2");
        var resolver = new ModelMapResolver(new Dictionary<string, SelectionResult>
        {
            ["m1"] = new SelectionSuccess([Candidate(p1)], ComboMode.Fallback),
            ["combo1"] = new SelectionSuccess([Candidate(p2), Candidate(p1)], ComboMode.Fallback),
        });
        var selector = new CountingSelector(new ModelSelector(_executions));
        var upstream = new GatedBadRequestUpstream();
        var log = new CapturingLog();
        var store = NewStore(log);
        // m2 pre-seed 2/3 — RecordExhaustion tại dispatch-time +1 sẽ mở fuse (quan sát được)
        store.RecordFailure("m2");
        store.RecordFailure("m2");
        await StartAsync(resolver, selector, upstream, log, store);

        // r2 kẹt trên p1 (giữ trọn slot duy nhất của p1)
        var r2 = Req("req00001", "m1");
        _queue.Enqueue(r2);
        await upstream.Entered.WaitAsync(TimeSpan.FromSeconds(5));

        // r1: p2 lỗi 429 → advance p1 nhưng p1 hết slot → re-enqueue (HasTried = true)
        var r1 = Req("req00002", "combo1");
        _queue.Enqueue(r1);
        await WaitUntilAsync(() => _queue.Contains("req00002") && upstream.Calls == 2);
        await Task.Delay(200); // chắc chắn đã park — không còn call nào chạy dở
        Assert.False(r1.Completion.Task.IsCompleted);

        // Mở fuse m1 (candidate còn lại của r1) trong lúc r1 đang park
        for (var i = 0; i < 3; i++)
            store.RecordFailure("m1");
        Assert.True(store.IsManualRetry("m1"));

        // r2 kết thúc 400 non-retryable → không RecordSuccess, fuse m1 giữ nguyên
        upstream.Release();
        var outcome2 = await r2.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<DispatchOutcome.Passthrough>(outcome2);

        // Dispatch lại: p2 đã tried + p1 đang ManualRetry → rỗng mà HasTried →
        // CompleteExhaustion (m2 2/3 → 3/3) + Passthrough attempt cuối — KHÔNG phải 503 gate
        var outcome1 = await r1.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var passthrough = Assert.IsType<DispatchOutcome.Passthrough>(outcome1);
        Assert.Equal(429, passthrough.Status);
        Assert.Equal("""{"error":{"message":"from-p2"}}""", Encoding.UTF8.GetString(passthrough.Body));
        Assert.True(store.IsManualRetry("m2")); // RecordExhaustion ghi failure tại nhánh dispatch-time
        Assert.Equal(2, upstream.Calls); // nhánh này không serve thêm upstream
        Assert.Contains(log.Errors,
            e => e.Contains("req00002") && e.Contains("thất bại sau 1 candidate"));
    }
}
