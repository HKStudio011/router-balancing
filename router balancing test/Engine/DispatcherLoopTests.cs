using System.Net;
using System.Text;
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

    private static ProxyRequest Req(string id)
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        return new ProxyRequest(id, RequestPriority.Normal, "m1", Encoding.UTF8.GetBytes("{}"), ctx);
    }

    private async Task StartAsync(IComboResolver resolver, IModelSelector selector,
        IUpstreamClient upstream, CapturingLog log)
    {
        var handler = new ChatCompletionsHandler(upstream, _protector, log);
        _loop = new DispatcherLoop(_queue, _executions, resolver, selector, handler, log);
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
        await StartAsync(resolver, selector, upstream, log);

        var r1 = Req("req00001");
        _queue.Enqueue(r1);
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
}
