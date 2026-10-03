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

    /// <summary>Tạo proxy qua service, trả Id — port khác nhau mỗi test để tránh trùng endpoint.</summary>
    private async Task<long> AddProxyAsync(int port)
    {
        var created = await CreateService().CreateAsync(Draft(host: "127.0.0.1", port: port));
        return created.Id;
    }

    /// <summary>Gán provider vào DB (ProxyService không quản lý provider).</summary>
    private async Task<long> AddProviderAsync()
    {
        using var db = _factory.CreateDbContext();
        var provider = new Provider
        {
            Name = "Test",
            Type = ProviderType.OpenAI,
            BaseUrl = "https://api.example.com",
        };
        db.Providers.Add(provider);
        await db.SaveChangesAsync();
        return provider.Id;
    }

    /// <summary>Gán account vào DB cho provider (ProxyService không quản lý account).</summary>
    private async Task<long> AddAccountAsync(long providerId)
    {
        using var db = _factory.CreateDbContext();
        var account = new ProviderAccount
        {
            ProviderId = providerId,
            Name = "Acc",
            ApiKeyEncrypted = _protector.Protect("secret"),
        };
        db.ProviderAccounts.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

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

    [Fact]
    public async Task TestAsync_WhenPasswordDecryptFails_PersistsFailure()
    {
        var created = await CreateService().CreateAsync(Draft(username: "user", password: "secret"));
        using (var seed = _factory.CreateDbContext())
        {
            var corrupt = await seed.OutboundProxies.SingleAsync(p => p.Id == created.Id);
            // Base64 hợp lệ nhưng không phải DPAPI blob → Unprotect ném CryptographicException
            // (bytes rác thô sẽ vấp FormatException ở FromBase64String — không phải lỗi decrypt)
            corrupt.PasswordEncrypted = Convert.ToBase64String(new byte[] { 1, 2, 3 });
            await seed.SaveChangesAsync();
        }

        var result = await CreateService().TestAsync(created.Id, CancellationToken.None);

        Assert.False(result.Success); // không throw — đã persist
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        using var db = _factory.CreateDbContext();
        var row = await db.OutboundProxies.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        Assert.False(row.LastTestSuccess);
        Assert.NotNull(row.LastTestAt);
        Assert.False(string.IsNullOrWhiteSpace(row.LastTestMessage));
    }

    [Fact]
    public async Task AssignProviderProxies_PersistsAndInvalidate()
    {
        var service = CreateService();
        var proxy = await AddProxyAsync(port: 9500);
        var providerId = await AddProviderAsync();
        var invalidationsBefore = _pool.Invalidations;

        await service.AssignProviderProxiesAsync(providerId, new[] { proxy }, ProxyMode.Fallback);

        // Reload từ DB (không có IProviderService.GetProviderAsync) — EF tự load junction.
        using var db = _factory.CreateDbContext();
        var provider = await db.Providers
            .Include(p => p.ProviderProxies)
            .SingleAsync(p => p.Id == providerId);

        Assert.Equal(ProxyMode.Fallback, provider.ProxyMode);
        Assert.Single(provider.ProviderProxies);
        Assert.Equal(proxy, provider.ProviderProxies.Single().ProxyId);
        // Mọi mutation phải Invalidate pool (spec §5) — delta thay vì absolute (FakePool dùng chung class).
        Assert.True(_pool.Invalidations > invalidationsBefore);
    }

    [Fact]
    public async Task AssignProviderProxies_Empty_Clears()
    {
        var service = CreateService();
        var proxy = await AddProxyAsync(port: 9501);
        var providerId = await AddProviderAsync();

        await service.AssignProviderProxiesAsync(providerId, new[] { proxy }, ProxyMode.Fallback);
        // proxyIds rỗng = gỡ toàn bộ gán (không validate)
        await service.AssignProviderProxiesAsync(providerId, [], ProxyMode.Fallback);

        using var db = _factory.CreateDbContext();
        var provider = await db.Providers
            .Include(p => p.ProviderProxies)
            .SingleAsync(p => p.Id == providerId);

        Assert.Empty(provider.ProviderProxies);
    }

    [Fact]
    public async Task AssignProviderProxies_IdempotentResave_SameSet_Succeeds()
    {
        var service = CreateService();
        var proxy = await AddProxyAsync(port: 9510);
        var providerId = await AddProviderAsync();

        await service.AssignProviderProxiesAsync(providerId, [proxy], ProxyMode.Fallback);
        // Re-save cùng tập không được đụng hàng junction cũ — idempotent (D-A1)
        await service.AssignProviderProxiesAsync(providerId, [proxy], ProxyMode.Fallback);

        using var db = _factory.CreateDbContext();
        var provider = await db.Providers
            .Include(p => p.ProviderProxies)
            .SingleAsync(p => p.Id == providerId);
        Assert.Single(provider.ProviderProxies);
        Assert.Equal(proxy, provider.ProviderProxies.Single().ProxyId);
    }

    [Fact]
    public async Task AssignProviderProxies_OverlappingResave_ReplacesDifference()
    {
        var service = CreateService();
        var proxyA = await AddProxyAsync(port: 9511);
        var proxyB = await AddProxyAsync(port: 9512);
        var providerId = await AddProviderAsync();

        await service.AssignProviderProxiesAsync(providerId, [proxyA], ProxyMode.Fallback);
        // Tập mới chồng lấp: giữ hàng A, thêm hàng B — không replace toàn bộ (D-A1)
        await service.AssignProviderProxiesAsync(providerId, [proxyA, proxyB], ProxyMode.Fallback);

        using var db = _factory.CreateDbContext();
        var provider = await db.Providers
            .Include(p => p.ProviderProxies)
            .SingleAsync(p => p.Id == providerId);
        Assert.Equal(2, provider.ProviderProxies.Count);
        Assert.Contains(provider.ProviderProxies, r => r.ProxyId == proxyA);
        Assert.Contains(provider.ProviderProxies, r => r.ProxyId == proxyB);
    }

    [Fact]
    public async Task AssignProviderProxies_SameProxySecondProvider_BothPersist()
    {
        var service = CreateService();
        var proxy = await AddProxyAsync(port: 9513);
        var providerA = await AddProviderAsync();
        var providerB = await AddProviderAsync();

        await service.AssignProviderProxiesAsync(providerA, [proxy], ProxyMode.RoundRobin);
        // Triệu chứng user báo: cùng 1 proxy gán cho 2 provider phải có 2 hàng junction
        await service.AssignProviderProxiesAsync(providerB, [proxy], ProxyMode.RoundRobin);

        using var db = _factory.CreateDbContext();
        var rows = await db.Set<ProviderProxy>().AsNoTracking()
            .Where(r => r.ProxyId == proxy)
            .ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.ProviderId == providerA);
        Assert.Contains(rows, r => r.ProviderId == providerB);
    }

    [Fact]
    public async Task AssignProviderProxies_DisabledProxy_Allowed()
    {
        var service = CreateService();
        var created = await service.CreateAsync(Draft(port: 9502));
        var providerId = await AddProviderAsync();

        await service.SetEnabledAsync(created.Id, enabled: false);

        // Proxy tắt vẫn gán được — pool tự lọc, request tự Direct (D-A2)
        await service.AssignProviderProxiesAsync(providerId, [created.Id], ProxyMode.Fallback);

        using var db = _factory.CreateDbContext();
        var provider = await db.Providers
            .Include(p => p.ProviderProxies)
            .SingleAsync(p => p.Id == providerId);
        Assert.Contains(provider.ProviderProxies, r => r.ProxyId == created.Id);
    }

    [Fact]
    public async Task AssignProviderProxies_UnknownProxy_ThrowsKeyNotFound()
    {
        var service = CreateService();
        var providerId = await AddProviderAsync();

        // id không tồn tại ≠ proxy tắt — contract ValidateProxyIdsAsync là KeyNotFound
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.AssignProviderProxiesAsync(providerId, [999_999], ProxyMode.RoundRobin));
    }

    [Fact]
    public async Task AssignAccountProxies_OverridesProvider()
    {
        var service = CreateService();
        var proxyA = await AddProxyAsync(port: 9503);
        var proxyB = await AddProxyAsync(port: 9504);
        var providerId = await AddProviderAsync();
        var accountId = await AddAccountAsync(providerId);

        await service.AssignProviderProxiesAsync(providerId, new[] { proxyA }, ProxyMode.RoundRobin);
        // Account override provider (D1): gán account không xóa tập của provider.
        await service.AssignAccountProxiesAsync(accountId, new[] { proxyB }, ProxyMode.Fallback);

        var assignments = await service.GetAssignmentsAsync(providerId);

        var account = assignments.Single(a => !a.IsProvider);

        Assert.Equal(new[] { proxyB }, account.ProxyIds);
        Assert.Equal(ProxyMode.Fallback, account.Mode);
        Assert.False(account.IsProvider);
    }

    [Fact]
    public async Task GetAssignments_ReturnsProviderAndAccounts()
    {
        var service = CreateService();
        var proxy = await AddProxyAsync(port: 9505);
        var providerId = await AddProviderAsync();
        var accountId = await AddAccountAsync(providerId);

        await service.AssignProviderProxiesAsync(providerId, new[] { proxy }, ProxyMode.Fallback);
        await service.AssignAccountProxiesAsync(accountId, new[] { proxy }, ProxyMode.RoundRobin);

        var assignments = await service.GetAssignmentsAsync(providerId);

        Assert.Equal(2, assignments.Count);
        var provider = assignments.Single(a => a.IsProvider);
        var account = assignments.Single(a => !a.IsProvider);

        Assert.Equal(providerId, provider.Id);
        Assert.Equal("Test", provider.Name);
        Assert.True(provider.IsProvider);
        Assert.Equal(ProxyMode.Fallback, provider.Mode);
        Assert.Equal(new[] { proxy }, provider.ProxyIds);

        Assert.Equal(accountId, account.Id);
        Assert.Equal("Acc", account.Name);
        Assert.False(account.IsProvider);
        Assert.Equal(ProxyMode.RoundRobin, account.Mode);
        Assert.Equal(new[] { proxy }, account.ProxyIds);
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

        public ProxyAttempt? GetNext(IReadOnlyList<long>? allowedIds) => null;

        public IReadOnlyList<ProxyAttempt> GetLivingInOrder(IReadOnlyList<long> ids) => [];

        public bool ReportFailure(long proxyId) => false;

        public void ReportSuccess(long proxyId)
        {
        }

        public void Invalidate() => Invalidations++;

        public IReadOnlyList<ProxyRuntimeStatus> Snapshot() => [];
    }
}
