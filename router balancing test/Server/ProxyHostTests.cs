using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Server;
using RouterBalancing.Core.Settings;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Server;

public class ProxyHostTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly AppSettingsService _settings;
    private readonly LogService _log;
    private readonly ClientKeyService _clientKeys;
    private readonly HttpClient _client = new();

    public ProxyHostTests()
    {
        // TestDb chỉ tạo file trống — phải migrate schema trước khi service đọc AppSettings
        DbInitializer.Initialize(_db.CreateFactory());
        var factory = _db.CreateFactory();
        _settings = new AppSettingsService(factory, new DpapiSecretProtector());
        _log = new LogService(factory);
        _clientKeys = new ClientKeyService(_db.CreateFactory());
        // Port 0-style: lấy port trống để test không đụng port thật của app đang chạy
        _settings.Set(SettingsKeys.Port, PortSelector.FindAvailable(24000));
    }

    public void Dispose()
    {
        _client.Dispose();
        _log.Dispose();
        _settings.Dispose();
        _db.Dispose();
    }

    private async Task<ProxyHost> StartHostAsync()
    {
        var host = new ProxyHost(_settings, _log, _db.CreateFactory(), new DpapiSecretProtector(), _clientKeys);
        await host.StartAsync();
        _client.BaseAddress = new Uri($"http://127.0.0.1:{host.Port}");
        return host;
    }

    [Fact]
    public async Task StartAsync_WhenPortFree_RunsOnPreferredPort()
    {
        await using var host = await StartHostAsync();

        Assert.True(host.IsRunning);
        Assert.Equal(_settings.Port, host.Port);
        var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task StartAsync_WhenPreferredPortBusy_FallsBackToAvailablePort()
    {
        // Chiếm port ưa thích rồi host phải tự chọn port khác
        var busyPort = _settings.Port;
        using var blocker = new System.Net.Sockets.TcpListener(IPAddress.Loopback, busyPort);
        blocker.Start();

        await using var host = await StartHostAsync();

        Assert.NotEqual(busyPort, host.Port);
        var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_WhenApiKeyEnabled_StillOpen()
    {
        await _clientKeys.CreateAsync(new ClientKeyDraft("health", null, null));
        await using var _ = await StartHostAsync();

        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Models_WhenApiKeyEnabled_RequiresKey()
    {
        var (_, plaintext) = await _clientKeys.CreateAsync(new ClientKeyDraft("models", null, null));
        await using var _ = await StartHostAsync();

        var withoutKey = await _client.GetAsync("/v1/models");
        using var withKey = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        withKey.Headers.Authorization = new("Bearer", plaintext);
        var withKeyResponse = await _client.SendAsync(withKey);

        Assert.Equal(HttpStatusCode.Unauthorized, withoutKey.StatusCode);
        Assert.Equal(HttpStatusCode.OK, withKeyResponse.StatusCode);
    }

    [Fact]
    public async Task Models_ReturnsOpenAiListShape_WithEnabledModels()
    {
        using (var db = _db.CreateFactory().CreateDbContext())
        {
            var provider = new Provider { Name = "OpenAI", BaseUrl = "https://api.openai.com" };
            provider.Models.Add(new Model { ModelId = "gpt-4o-mini", Enabled = true });
            provider.Models.Add(new Model { ModelId = "gpt-4o", Enabled = false });
            db.Providers.Add(provider);
            db.SaveChanges();
        }
        await using var host = await StartHostAsync();

        var response = await _client.GetFromJsonAsync<JsonElement>("/v1/models");

        Assert.Equal("list", response.GetProperty("object").GetString());
        var data = response.GetProperty("data").EnumerateArray().ToList();
        Assert.Single(data);
        Assert.Equal("gpt-4o-mini", data[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task StopAsync_ThenRequest_Fails()
    {
        var host = await StartHostAsync();

        await host.StopAsync();

        Assert.False(host.IsRunning);
        await Assert.ThrowsAsync<HttpRequestException>(() => _client.GetAsync("/health"));
        await host.DisposeAsync();
    }

    [Fact]
    public async Task RestartAsync_PicksUpNewPortSetting()
    {
        var host = await StartHostAsync();
        var oldPort = host.Port;
        _settings.Set(SettingsKeys.Port, PortSelector.FindAvailable(25000));

        await host.RestartAsync();

        Assert.True(host.IsRunning);
        Assert.NotEqual(oldPort, host.Port);
        _client.BaseAddress = new Uri($"http://127.0.0.1:{host.Port}");
        var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await host.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_WhenStarted_RaisesStateChanged()
    {
        var host = new ProxyHost(_settings, _log, _db.CreateFactory(), new DpapiSecretProtector(), _clientKeys);
        var raised = 0;
        host.StateChanged += () => raised++;

        await host.StartAsync();
        await host.StartAsync(); // no-op — không được phát event lần hai

        Assert.Equal(1, raised);
        await host.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_WhenCalledTwice_IsIdempotent()
    {
        var host = new ProxyHost(_settings, _log, _db.CreateFactory(), new DpapiSecretProtector(), _clientKeys);
        var raised = 0;
        host.StateChanged += () => raised++;

        await host.StartAsync();
        var port = host.Port;
        await host.StartAsync();

        Assert.True(host.IsRunning);
        Assert.Equal(port, host.Port);
        Assert.Equal(1, raised);
        _client.BaseAddress = new Uri($"http://127.0.0.1:{host.Port}");
        var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await host.DisposeAsync();
    }

    [Fact]
    public async Task StopAsync_WhenCalledTwice_IsIdempotent()
    {
        var host = await StartHostAsync();
        var raised = 0;
        host.StateChanged += () => raised++;

        await host.StopAsync();
        Assert.Equal(1, raised);

        await host.StopAsync();

        Assert.False(host.IsRunning);
        Assert.Null(host.Port);
        Assert.Equal(1, raised);
        await host.DisposeAsync();
    }
}
