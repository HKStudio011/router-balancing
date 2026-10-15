using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Server;
using RouterBalancing.Core.Settings;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Server;

/// <summary>
/// E2E integration test: request POST chat tới opencode.ai/.../-free model đi qua
/// Go sidecar thật (binary đã commit) và nhận 200 SSE — verify end-to-end routing
/// qua OpenCodeSidecarHandler + OpenCodeSidecarService với upstream thật.
/// </summary>
public class OpenCodeSidecarE2ETests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly AppSettingsService _settings;
    private readonly LogService _log;
    private readonly ClientKeyService _clientKeys;
    private readonly DpapiSecretProtector _protector = new();
    private WebApplication? _app;
    private HttpClient? _client;

    public OpenCodeSidecarE2ETests()
    {
        DbInitializer.Initialize(_db.CreateFactory());
        var factory = _db.CreateFactory();
        _settings = new AppSettingsService(factory);
        _log = new LogService(factory);
        _clientKeys = new ClientKeyService(factory);
    }

    public void Dispose()
    {
        _client?.Dispose();
        _app?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _log.Dispose();
        _settings.Dispose();
        _db.Dispose();
    }

    private void SeedOpenCodeFreeProvider()
    {
        using var db = _db.CreateFactory().CreateDbContext();
        var provider = new Provider
        {
            Name = "opencode",
            BaseUrl = "https://opencode.ai/zen",
            Type = ProviderType.OpenAI,
        };
        provider.Models.Add(new Model { ModelId = "mimo-v2.6-flash-free", Enabled = true });
        provider.Accounts.Add(new ProviderAccount
        {
            Name = "anon",
            Enabled = true,
            ApiKeyEncrypted = _protector.Protect(""),
        });
        db.Providers.Add(provider);
        db.SaveChanges();
    }

    private async Task<HttpClient> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IAppSettingsService>(_settings);
        builder.Services.AddSingleton<ILogService>(_log);
        builder.Services.AddSingleton<IDbContextFactory<RouterBalancingDbContext>>(_db.CreateFactory());
        builder.Services.AddSingleton<IClientKeyService>(_clientKeys);

        ProxyApp.ConfigureServices(builder, _protector, new DirectProxyPool());

        var app = builder.Build();
        ProxyApp.ConfigurePipeline(app);
        await app.StartAsync();
        _app = app;
        _client = app.GetTestClient();
        return _client;
    }

    [Fact]
    public async Task ChatCompletion_WhenOpenCodeFreeModel_ReturnsSseThroughSidecar()
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "opencode-proxy.exe")))
        {
            return; // binary chưa build — skip
        }

        SeedOpenCodeFreeProvider();
        var client = await StartAsync();

        var body = """{"model":"mimo-v2.6-flash-free","messages":[{"role":"user","content":"Reply with exactly: pong"}],"stream":true}""";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        // SSE cho phép comment lines (`: keep-alive`) trước data — skip tới data đầu tiên.
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        string? line;
        var sawData = false;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                sawData = true;
                break;
            }
        }
        Assert.True(sawData, "SSE stream không chứa data line nào.");
    }

    /// <summary>
    /// Regression: wire body title-gen THẬT (capture 2026-10-15 — system prompt
    /// PROMPT_TITLE chính chủ) từng bị gate 403 FreeTierError deterministic.
    /// Sidecar phải paraphrase system prompt title-gen trong normalizeBody và
    /// upstream trả 200 SSE.
    /// </summary>
    [Fact]
    public async Task TitleShapedRequest_TitlePromptRewritten_ReturnsSse()
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "opencode-proxy.exe")))
        {
            return; // binary chưa build — skip
        }

        SeedOpenCodeFreeProvider();
        var client = await StartAsync();

        // Fixture giữ nguyên model wire thật ("opencode-free/...") — map về model
        // id đã seed trong provider để router match đúng lane OpenCode free.
        var titleNode = JsonNode.Parse(
            await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "title-captured.json")))!;
        titleNode["model"] = "mimo-v2.6-flash-free";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(titleNode.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        string? line;
        var sawData = false;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                sawData = true;
                break;
            }
        }
        Assert.True(sawData, "SSE stream không chứa data line nào.");
    }
}
