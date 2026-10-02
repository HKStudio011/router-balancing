using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Proxies;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Proxies;

public class ProxyServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _factory;
    private readonly ISecretProtector _protector = new DpapiSecretProtector();
    private readonly FakePool _pool = new();

    // Mutable: test TestAsync gán script echo mới trước khi CreateService() đọc field
    private StubEchoClient _echo = new(() => new ProxyTestResult(true, null, 200, TimeSpan.FromMilliseconds(12)));

    public ProxyServiceTests()
    {
        _factory = _db.CreateFactory();
        DbInitializer.Initialize(_factory);
    }

    public void Dispose() => _db.Dispose();

    private ProxyService CreateService() => new(_factory, _protector, _pool, _echo);

    private static ProxyDraft Draft(
        string scheme = "http", string host = "127.0.0.1", int port = 8080,
        string username = "", string? password = null) =>
        new() { Scheme = scheme, Host = host, Port = port, Username = username, Password = password };

    [Fact]
    public async Task CreateAsync_InvalidScheme_ThrowsValidationError()
    {
        var ex = await Assert.ThrowsAsync<ProxyValidationException>(
            () => CreateService().CreateAsync(Draft(scheme: "ftp")));

        Assert.Equal("proxies.error.scheme", ex.Errors[nameof(ProxyDraft.Scheme)]);
    }

    [Fact]
    public async Task CreateAsync_HostBlank_ThrowsValidationError()
    {
        var ex = await Assert.ThrowsAsync<ProxyValidationException>(
            () => CreateService().CreateAsync(Draft(host: "  ")));

        Assert.Equal("proxies.error.host", ex.Errors[nameof(ProxyDraft.Host)]);
    }

    [Fact]
    public async Task CreateAsync_PortOutOfRange_ThrowsValidationError()
    {
        var ex = await Assert.ThrowsAsync<ProxyValidationException>(
            () => CreateService().CreateAsync(Draft(port: 70000)));

        Assert.Equal("proxies.error.port", ex.Errors[nameof(ProxyDraft.Port)]);
    }

    [Fact]
    public async Task CreateAsync_DuplicateEndpoint_CaseInsensitiveHost_Throws()
    {
        var service = CreateService();
        await service.CreateAsync(Draft(host: "LocalHost", port: 9000));

        // Host hoa/thường không phân biệt (Design decision 6)
        var ex = await Assert.ThrowsAsync<ProxyValidationException>(
            () => service.CreateAsync(Draft(host: "localhost", port: 9000)));

        Assert.Equal("proxies.error.duplicate", ex.Errors[nameof(ProxyDraft.Host)]);
        // Khác scheme vẫn tạo được
        await service.CreateAsync(Draft(scheme: "socks5", host: "localhost", port: 9000));
    }

    [Fact]
    public async Task CreateAsync_WithCredentials_EncryptsPasswordAtRest()
    {
        var created = await CreateService().CreateAsync(Draft(username: "user", password: "secret"));

        using var db = _factory.CreateDbContext();
        var raw = await db.OutboundProxies.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        Assert.NotNull(raw.PasswordEncrypted);
        Assert.NotEqual("secret", raw.PasswordEncrypted); // không plaintext trong DB
        Assert.Equal("secret", _protector.Unprotect(raw.PasswordEncrypted!)); // decrypt đúng
    }

    [Fact]
    public async Task CreateAsync_UsernameBlank_DropsPassword()
    {
        var created = await CreateService().CreateAsync(Draft(username: "", password: "orphan"));

        using var db = _factory.CreateDbContext();
        var raw = await db.OutboundProxies.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        // Invariant Design decision 5 — không password mồ côi không username
        Assert.Null(raw.Username);
        Assert.Null(raw.PasswordEncrypted);
    }

    [Fact]
    public async Task UpdateAsync_PasswordNull_KeepsExistingPassword()
    {
        var service = CreateService();
        var created = await service.CreateAsync(Draft(username: "user", password: "old-pass"));

        await service.UpdateAsync(created.Id,
            Draft(host: "10.0.0.2", port: 3128, username: "user", password: null));

        using var db = _factory.CreateDbContext();
        var raw = await db.OutboundProxies.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        Assert.Equal("old-pass", _protector.Unprotect(raw.PasswordEncrypted!));
        Assert.Equal("10.0.0.2", raw.Host);
    }

    [Fact]
    public async Task UpdateAsync_UsernameBlank_ClearsAuth()
    {
        var service = CreateService();
        var created = await service.CreateAsync(Draft(username: "user", password: "old-pass"));

        await service.UpdateAsync(created.Id, Draft(username: "", password: "ignored"));

        using var db = _factory.CreateDbContext();
        var raw = await db.OutboundProxies.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        Assert.Null(raw.Username);
        Assert.Null(raw.PasswordEncrypted); // username rỗng thắng — xóa cả cặp (spec §3.2)
    }

    [Fact]
    public async Task UpdateAsync_UnknownId_ThrowsKeyNotFound()
        => await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateService().UpdateAsync(999_999, Draft()));

    [Fact]
    public async Task DeleteAsync_UnknownId_ThrowsKeyNotFound()
        => await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateService().DeleteAsync(999_999));

    [Fact]
    public async Task SetEnabledAsync_TogglesRow()
    {
        var service = CreateService();
        var created = await service.CreateAsync(Draft());

        await service.SetEnabledAsync(created.Id, enabled: false);

        using var db = _factory.CreateDbContext();
        Assert.False((await db.OutboundProxies.AsNoTracking().SingleAsync(p => p.Id == created.Id)).Enabled);
    }

    [Fact]
    public async Task EveryCrudOperation_InvalidatesPool()
    {
        var service = CreateService();
        var created = await service.CreateAsync(Draft(port: 9001));

        await service.UpdateAsync(created.Id, Draft(port: 9001, host: "127.0.0.1"));
        await service.SetEnabledAsync(created.Id, false);
        await service.DeleteAsync(created.Id);

        Assert.Equal(4, _pool.Invalidations); // Create + Update + SetEnabled + Delete
    }

    [Fact]
    public async Task ListAsync_ReturnsRowsOrderedById()
    {
        var service = CreateService();
        await service.CreateAsync(Draft(port: 9002));
        await service.CreateAsync(Draft(port: 9003));

        var list = await service.ListAsync();

        Assert.Equal(2, list.Count);
        Assert.True(list[0].Id < list[1].Id);
    }

    [Fact]
    public async Task TestAsync_EchoSucceeds_PersistsLastTestSuccess()
    {
        var created = await CreateService().CreateAsync(Draft());
        _echo = new StubEchoClient(() => new ProxyTestResult(true, null, 200, TimeSpan.FromMilliseconds(30), "203.0.113.7"));

        var result = await CreateService().TestAsync(created.Id, CancellationToken.None);

        Assert.True(result.Success);
        using var db = _factory.CreateDbContext();
        var row = await db.OutboundProxies.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        Assert.True(row.LastTestSuccess);
        Assert.NotNull(row.LastTestAt);
        Assert.Null(row.LastTestMessage);
        Assert.Equal("203.0.113.7", row.LastTestIp);
    }

    [Fact]
    public async Task TestAsync_EchoThrowsHttpRequestException_PersistsError()
    {
        var created = await CreateService().CreateAsync(Draft());
        _echo = new StubEchoClient(() => throw new HttpRequestException("Connection refused"));

        var result = await CreateService().TestAsync(created.Id, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Connection refused", result.Error);
        using var db = _factory.CreateDbContext();
        var row = await db.OutboundProxies.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        Assert.False(row.LastTestSuccess);
        Assert.Equal("Connection refused", row.LastTestMessage);
        Assert.NotNull(row.LastTestAt);
    }

    [Fact]
    public async Task TestAsync_UserCancels_RethrowsWithoutPersisting()
    {
        var created = await CreateService().CreateAsync(Draft());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _echo = new StubEchoClient(() => throw new OperationCanceledException(cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateService().TestAsync(created.Id, cts.Token));

        using var db = _factory.CreateDbContext();
        var row = await db.OutboundProxies.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        Assert.Null(row.LastTestAt); // hủy thật → không ghi gì
    }

    [Fact]
    public async Task TestAsync_Timeout_PersistsError()
    {
        var created = await CreateService().CreateAsync(Draft());
        _echo = new StubEchoClient(() => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));

        var result = await CreateService().TestAsync(created.Id, CancellationToken.None);

        Assert.False(result.Success);
        using var db = _factory.CreateDbContext();
        var row = await db.OutboundProxies.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        Assert.False(row.LastTestSuccess);
        Assert.NotNull(row.LastTestMessage);
    }

    /// <summary>Echo theo script — lambda throw được nên test giả lập được cả nhánh lỗi.</summary>
    private sealed class StubEchoClient(Func<ProxyTestResult> script) : IProxyEchoClient
    {
        public Task<ProxyTestResult> EchoAsync(ProxyAttempt proxy, CancellationToken ct) =>
            Task.FromResult(script());
    }

    /// <summary>IProxyPool ghi nhận Invalidate — health methods không dùng ở service test này.</summary>
    private sealed class FakePool : IProxyPool
    {
        public int Invalidations;

        public ProxyAttempt? GetNext() => null;

        public bool ReportFailure(long proxyId) => false;

        public void ReportSuccess(long proxyId)
        {
        }

        public void Invalidate() => Invalidations++;

        public IReadOnlyList<ProxyRuntimeStatus> Snapshot() => [];
    }
}
