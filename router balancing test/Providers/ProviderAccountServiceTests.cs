// router balancing test/Providers/ProviderAccountServiceTests.cs
using System.Net;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Providers;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Providers;

/// <summary>CRUD/test ProviderAccountService trên DB file tạm — không network, DPAPI thật.</summary>
public class ProviderAccountServiceTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ISecretProtector _protector = new DpapiSecretProtector();
    private readonly ProviderAccountService _service;

    public ProviderAccountServiceTests()
    {
        _db = _testDb.CreateFactory();
        DbInitializer.Initialize(_db);
        // CRUD dùng factory ném — không được phép đụng network
        _service = new ProviderAccountService(_db, _protector, new NeverHttpFactory(), new NullLog());
    }

    /// <summary>Giải phóng TestDb (xóa file tạm).</summary>
    public void Dispose() => _testDb.Dispose();

    /// <summary>HttpClientFactory ném nếu bị gọi — CRUD không được đụng network.</summary>
    private sealed class NeverHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("CRUD must not perform HTTP calls.");
    }

    /// <summary>Service với HTTP stub — test TestAll không đụng mạng.</summary>
    private ProviderAccountService ServiceWith(HttpMessageHandler handler) =>
        new(_db, _protector, new StubFactory(handler), new NullLog());

    /// <summary>IHttpClientFactory trả HttpClient gắn handler test.</summary>
    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>Trả 200 khi auth chứa "sk-ok", ngược lại 401 — mô phỏng 1 key sống 1 key chết.</summary>
    private sealed class KeyedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var auth = request.Headers.Authorization?.ToString() ?? string.Empty;
            var ok = auth.Contains("sk-ok", StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(ok ? HttpStatusCode.OK : HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{}"),
            });
        }
    }

    /// <summary>Trả đúng status cố định cho mọi request.</summary>
    private sealed class FixedHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
    }

    /// <summary>Trả status cố định cho mọi request + ghi request để assert header.</summary>
    private sealed class CapturingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
        }
    }

    /// <summary>Seed provider + các account (tên/khối/ưu tiên) — trả ProviderId.</summary>
    private async Task<long> SeedProviderAsync(params (string Name, bool Enabled, int Priority)[] accounts)
    {
        using var db = _db.CreateDbContext();
        var provider = new Provider
        {
            Name = "P",
            Type = ProviderType.OpenAI,
            BaseUrl = "https://api.example.com",
            MaxConcurrent = 4,
        };
        foreach (var (name, enabled, priority) in accounts)
        {
            provider.Accounts.Add(new ProviderAccount
            {
                Name = name,
                Enabled = enabled,
                Priority = priority,
                ApiKeyEncrypted = _protector.Protect($"sk-{name}"),
            });
        }
        db.Providers.Add(provider);
        await db.SaveChangesAsync();
        return provider.Id;
    }

    /// <summary>Bản nháp account hợp lệ cho test (ProviderId tùy chỗ gọi).</summary>
    private static ProviderAccountDraft Draft(long providerId, string name = "acct", string key = "sk-1") => new()
    {
        ProviderId = providerId,
        Name = name,
        ApiKey = key,
        Enabled = true,
        Weight = 100,
        Priority = 0,
    };

    /// <summary>Key lưu xuống DB phải là ciphertext DPAPI, không plaintext.</summary>
    [Fact]
    public async Task Create_WithKey_SavesDpapiEncrypted()
    {
        var providerId = await SeedProviderAsync();

        var account = await _service.CreateAsync(Draft(providerId));

        Assert.NotEqual("sk-1", account.ApiKeyEncrypted);
        Assert.Equal("sk-1", _protector.Unprotect(account.ApiKeyEncrypted));

        using var db = _db.CreateDbContext();
        var saved = await db.ProviderAccounts.SingleAsync(a => a.Id == account.Id);
        Assert.Equal(account.ApiKeyEncrypted, saved.ApiKeyEncrypted);
    }

    /// <summary>Trùng Name trong cùng provider → InvalidOperationException.</summary>
    [Fact]
    public async Task Create_DuplicateNameInSameProvider_ThrowsInvalidOperation()
    {
        var providerId = await SeedProviderAsync();
        await _service.CreateAsync(Draft(providerId, name: "dup"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CreateAsync(Draft(providerId, name: "dup")));
    }

    /// <summary>Cùng Name giữa 2 provider là hợp lệ.</summary>
    [Fact]
    public async Task Create_SameNameDifferentProviders_Ok()
    {
        var first = await SeedProviderAsync();
        var second = await SeedProviderAsync();
        await _service.CreateAsync(Draft(first, name: "shared"));

        var account = await _service.CreateAsync(Draft(second, name: "shared"));

        Assert.Equal(second, account.ProviderId);
    }

    /// <summary>Provider không tồn tại → KeyNotFoundException.</summary>
    [Fact]
    public async Task Create_WhenProviderMissing_ThrowsKeyNotFound()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.CreateAsync(Draft(999)));
    }

    /// <summary>ModelPatterns → JSON array trong cột TEXT.</summary>
    [Fact]
    public async Task Create_WithPatterns_SerializesJsonArray()
    {
        var providerId = await SeedProviderAsync();

        var account = await _service.CreateAsync(new ProviderAccountDraft
        {
            ProviderId = providerId,
            Name = "scoped",
            ApiKey = "sk-1",
            ModelPatterns = ["gpt-4o*", "o3*"],
        });

        Assert.Equal("""["gpt-4o*","o3*"]""", account.ModelPatterns);
    }

    /// <summary>Pattern rỗng/trắng → ArgumentException.</summary>
    [Fact]
    public async Task Create_EmptyPattern_ThrowsArgument()
    {
        var providerId = await SeedProviderAsync();
        var draft = Draft(providerId);
        draft.ModelPatterns = ["ok", "   "];

        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateAsync(draft));
    }

    [Fact]
    public async Task Create_NoKeyWithEmptyKey_SavesEmptyCiphertext()
    {
        var providerId = await SeedProviderAsync();
        var draft = Draft(providerId, key: string.Empty);
        draft.NoKey = true;

        var account = await _service.CreateAsync(draft);

        // No-key = cột rỗng — KHÔNG Protect("") (D1)
        Assert.Equal(string.Empty, account.ApiKeyEncrypted);
    }

    [Fact]
    public async Task Update_NoKeyTrue_ClearsSavedKey()
    {
        var providerId = await SeedProviderAsync();
        var account = await _service.CreateAsync(Draft(providerId, key: "sk-old"));
        var draft = Draft(providerId, name: "acct", key: string.Empty);
        draft.NoKey = true;

        await _service.UpdateAsync(account.Id, draft);

        using var db = _db.CreateDbContext();
        var saved = await db.ProviderAccounts.SingleAsync(a => a.Id == account.Id);
        // NoKey=true là hành động chủ động xóa key (D5) — khác key trống mặc định = giữ (đã có test pin)
        Assert.Equal(string.Empty, saved.ApiKeyEncrypted);
    }

    /// <summary>Name > 100 ký tự → ArgumentException.</summary>
    [Fact]
    public async Task Create_NameTooLong_ThrowsArgument()
    {
        var providerId = await SeedProviderAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.CreateAsync(Draft(providerId, name: new string('a', 101))));
    }

    /// <summary>Lần lượt các rule Weight/Priority/limit vi phạm → ArgumentException.</summary>
    [Theory]
    [InlineData("", "sk", 100, 0, null, null)]   // name rỗng
    [InlineData("A", "", 100, 0, null, null)]    // create bắt buộc key
    [InlineData("A", "sk", -1, 0, null, null)]   // weight < 0
    [InlineData("A", "sk", 10001, 0, null, null)] // weight > 10000
    [InlineData("A", "sk", 100, -1001, null, null)] // priority < -1000
    [InlineData("A", "sk", 100, 1001, null, null)]  // priority > 1000
    [InlineData("A", "sk", 100, 0, 0, null)]     // token limit <= 0
    [InlineData("A", "sk", 100, 0, null, -1)]    // request limit <= 0
    public async Task Create_InvalidDraft_ThrowsArgument(
        string name, string key, int weight, int priority, int? tokenLimit, int? requestLimit)
    {
        var providerId = await SeedProviderAsync();
        var draft = Draft(providerId, name, key);
        draft.Weight = weight;
        draft.Priority = priority;
        draft.DailyTokenLimit = tokenLimit;
        draft.DailyRequestLimit = requestLimit;

        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateAsync(draft));
    }

    /// <summary>Quá 50 pattern → ArgumentException.</summary>
    [Fact]
    public async Task Create_TooManyPatterns_ThrowsArgument()
    {
        var providerId = await SeedProviderAsync();
        var draft = Draft(providerId);
        draft.ModelPatterns = Enumerable.Range(0, 51).Select(i => $"m{i}").ToArray();

        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateAsync(draft));
    }

    /// <summary>Key rỗng khi sửa = giữ key đã lưu.</summary>
    [Fact]
    public async Task Update_EmptyApiKey_KeepsExistingKey()
    {
        var providerId = await SeedProviderAsync();
        var account = await _service.CreateAsync(Draft(providerId, key: "sk-old"));
        var draft = Draft(providerId, name: "renamed", key: string.Empty);

        await _service.UpdateAsync(account.Id, draft);

        using var db = _db.CreateDbContext();
        var saved = await db.ProviderAccounts.SingleAsync(a => a.Id == account.Id);
        Assert.Equal("renamed", saved.Name);
        Assert.Equal("sk-old", _protector.Unprotect(saved.ApiKeyEncrypted));
    }

    /// <summary>Key toàn khoảng trắng cũng = giữ (chống persist key rỗng).</summary>
    [Fact]
    public async Task Update_WhitespaceApiKey_KeepsExistingKey()
    {
        var providerId = await SeedProviderAsync();
        var account = await _service.CreateAsync(Draft(providerId, key: "sk-old"));
        var draft = Draft(providerId, name: "renamed", key: "   ");

        await _service.UpdateAsync(account.Id, draft);

        using var db = _db.CreateDbContext();
        var saved = await db.ProviderAccounts.SingleAsync(a => a.Id == account.Id);
        Assert.Equal("renamed", saved.Name);
        Assert.Equal("sk-old", _protector.Unprotect(saved.ApiKeyEncrypted));
        Assert.Equal(1, await db.ProviderAccounts.CountAsync(a => a.ProviderId == providerId));
    }

    /// <summary>Có key mới → ciphertext đổi.</summary>
    [Fact]
    public async Task Update_NewKey_ReplacesEncrypted()
    {
        var providerId = await SeedProviderAsync();
        var account = await _service.CreateAsync(Draft(providerId, key: "sk-old"));

        await _service.UpdateAsync(account.Id, Draft(providerId, key: "sk-new"));

        using var db = _db.CreateDbContext();
        var saved = await db.ProviderAccounts.SingleAsync(a => a.Id == account.Id);
        Assert.Equal("sk-new", _protector.Unprotect(saved.ApiKeyEncrypted));
    }

    /// <summary>Trùng tên chính nó không phải lỗi.</summary>
    [Fact]
    public async Task Update_DuplicateNameExcludingSelf_Ok()
    {
        var providerId = await SeedProviderAsync();
        var account = await _service.CreateAsync(Draft(providerId, name: "only"));
        var draft = Draft(providerId, name: "only", key: string.Empty);

        await _service.UpdateAsync(account.Id, draft); // trùng chính nó vẫn hợp lệ

        using var db = _db.CreateDbContext();
        Assert.Equal(1, await db.ProviderAccounts.CountAsync(a => a.ProviderId == providerId));
    }

    /// <summary>Account không tồn tại → KeyNotFoundException.</summary>
    [Fact]
    public async Task Update_WhenAccountMissing_ThrowsKeyNotFound()
    {
        var providerId = await SeedProviderAsync();

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _service.UpdateAsync(999, Draft(providerId)));
    }

    /// <summary>Xoá account cuối → InvalidOperationException.</summary>
    [Fact]
    public async Task Delete_LastAccount_ThrowsInvalidOperation()
    {
        var providerId = await SeedProviderAsync(("only", true, 0));
        using (var db = _db.CreateDbContext())
        {
            var id = await db.ProviderAccounts
                .Where(a => a.ProviderId == providerId)
                .Select(a => a.Id)
                .SingleAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => _service.DeleteAsync(id));
        }
    }

    /// <summary>Còn >1 account → xoá được.</summary>
    [Fact]
    public async Task Delete_NotLast_Succeeds()
    {
        var providerId = await SeedProviderAsync(("a", true, 0), ("b", true, 0));
        using (var db = _db.CreateDbContext())
        {
            var id = await db.ProviderAccounts
                .Where(a => a.ProviderId == providerId && a.Name == "a")
                .Select(a => a.Id)
                .SingleAsync();
            await _service.DeleteAsync(id);
        }

        using var db2 = _db.CreateDbContext();
        Assert.Equal(1, await db2.ProviderAccounts.CountAsync(a => a.ProviderId == providerId));
    }

    /// <summary>Tắt account → Enabled=false được persist xuống DB.</summary>
    [Fact]
    public async Task SetEnabled_WhenToggled_FlipsAndPersists()
    {
        var providerId = await SeedProviderAsync(("a1", true, 0));
        var accountId = (await _service.ListAsync(providerId))[0].Id;

        await _service.SetEnabledAsync(accountId, false);

        using var db = _db.CreateDbContext();
        var saved = await db.ProviderAccounts.SingleAsync(a => a.Id == accountId);
        Assert.False(saved.Enabled);
    }

    /// <summary>Account không tồn tại → KeyNotFoundException.</summary>
    [Fact]
    public async Task SetEnabled_WhenAccountMissing_ThrowsKeyNotFound()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.SetEnabledAsync(999, false));
    }

    /// <summary>Thứ tự Priority tăng dần rồi Name.</summary>
    [Fact]
    public async Task ListAsync_OrdersByPriorityThenName()
    {
        var providerId = await SeedProviderAsync(("mid", true, 5), ("first", true, 0), ("last", true, 9));

        var list = await _service.ListAsync(providerId);

        Assert.Equal(new[] { "first", "mid", "last" }, list.Select(a => a.Name).ToArray());
    }

    /// <summary>Xoá provider cascade accounts (FK OnDelete).</summary>
    [Fact]
    public async Task DeleteProvider_CascadesToAccounts()
    {
        var providerId = await SeedProviderAsync(("a", true, 0), ("b", true, 0));

        using (var db = _db.CreateDbContext())
        {
            var provider = await db.Providers.SingleAsync(p => p.Id == providerId);
            db.Providers.Remove(provider);
            await db.SaveChangesAsync();
        }

        using var db2 = _db.CreateDbContext();
        Assert.False(await db2.ProviderAccounts.AnyAsync(a => a.ProviderId == providerId));
    }

    /// <summary>Ghi LastTest* từng account + Provider = AND + composition message.</summary>
    [Fact]
    public async Task TestAllAsync_MixedResults_WritesEachAndProviderAnd()
    {
        var providerId = await SeedProviderAsync(("ok", true, 1), ("bad", true, 2));
        var service = ServiceWith(new KeyedHandler());

        var results = await service.TestAllAsync(providerId);

        Assert.Equal(2, results.Count);
        Assert.True(results.Single(r => r.AccountName == "ok").Success);
        var bad = results.Single(r => r.AccountName == "bad");
        Assert.False(bad.Success);
        Assert.Contains("401", bad.Message);

        using var db = _db.CreateDbContext();
        Assert.True(await db.ProviderAccounts
            .Where(a => a.ProviderId == providerId && a.Name == "ok")
            .Select(a => a.LastTestSuccess)
            .SingleAsync());
        var provider = await db.Providers.SingleAsync(p => p.Id == providerId);
        Assert.False(provider.LastTestSuccess); // AND của 2 account: 1 fail → false
        Assert.NotNull(provider.LastTestAt);
        Assert.Equal("bad: HTTP 401 Unauthorized", provider.LastTestMessage); // composition "Name: message", chỉ account fail
    }

    /// <summary>Không account enabled → Provider.LastTest* = null.</summary>
    [Fact]
    public async Task TestAllAsync_NoEnabledAccounts_SetsProviderTestNull()
    {
        var providerId = await SeedProviderAsync(("off", false, 0));
        var service = ServiceWith(new FixedHandler(HttpStatusCode.OK));

        var results = await service.TestAllAsync(providerId);

        Assert.Empty(results);
        using var db = _db.CreateDbContext();
        var provider = await db.Providers.SingleAsync(p => p.Id == providerId);
        Assert.Null(provider.LastTestSuccess);
        Assert.Null(provider.LastTestAt);
    }

    [Fact]
    public async Task TestAllAsync_NoKeyAccount_ProbesWithoutAuthorization()
    {
        var providerId = await SeedProviderAsync(("free", true, 0)); // seed có key "sk-free"
        using (var db = _db.CreateDbContext())
        {
            var account = await db.ProviderAccounts.SingleAsync(a => a.ProviderId == providerId);
            account.ApiKeyEncrypted = string.Empty; // chuyển sang no-key
            await db.SaveChangesAsync();
        }
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var service = ServiceWith(handler);

        var results = await service.TestAllAsync(providerId);

        // Trước khi fix: Unprotect("") ném CryptographicException → caught → success=false
        Assert.True(Assert.Single(results).Success);
        Assert.Null(handler.LastRequest!.Headers.Authorization);
    }

    /// <summary>Provider không tồn tại → KeyNotFoundException.</summary>
    [Fact]
    public async Task TestAllAsync_WhenProviderMissing_ThrowsKeyNotFound()
    {
        var service = ServiceWith(new FixedHandler(HttpStatusCode.OK));

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.TestAllAsync(999));
    }
}
