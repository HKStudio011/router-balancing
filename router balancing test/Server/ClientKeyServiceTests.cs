using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Server;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Server;

public class ClientKeyServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly ClientKeyService _service;

    public ClientKeyServiceTests()
    {
        DbInitializer.Initialize(_db.CreateFactory());
        _service = new ClientKeyService(_db.CreateFactory());
    }

    public void Dispose() => _db.Dispose();

    private static ClientKeyDraft Draft(string name = "app", int? rpm = null, int? tpm = null)
        => new(name, rpm, tpm);

    [Fact]
    public async Task Create_ReturnsPlaintextAndPersistsOnlyHash()
    {
        var (key, plaintext) = await _service.CreateAsync(Draft());

        Assert.StartsWith("sk-rb-", plaintext);
        await using var db = _db.CreateFactory().CreateDbContext();
        var saved = await db.ClientKeys.SingleAsync(k => k.Id == key.Id);
        Assert.Equal(ClientKeyHasher.Hash(plaintext), saved.KeyHash);
        Assert.Equal(ClientKeyHasher.Mask(plaintext), saved.KeyMask);
        Assert.DoesNotContain(plaintext, saved.KeyHash + saved.KeyMask + saved.Name);
        Assert.True(saved.Enabled);
    }

    [Fact]
    public async Task Create_TriggersKeysChanged()
    {
        var raised = 0;
        _service.KeysChanged += () => raised++;

        await _service.CreateAsync(Draft());

        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task Create_WhenNameBlank_Throws()
        => await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateAsync(Draft("  ")));

    [Fact]
    public async Task Create_WhenNameOver100Chars_Throws()
        => await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateAsync(Draft(new string('x', 101))));

    [Fact]
    public async Task Create_WhenLimitNotPositive_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateAsync(Draft("a", rpm: 0)));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateAsync(Draft("a", tpm: -1)));
    }

    [Fact]
    public async Task List_ReturnsAllOrderedById()
    {
        await _service.CreateAsync(Draft("first"));
        await _service.CreateAsync(Draft("second"));

        var list = await _service.ListAsync();

        Assert.Equal(new[] { "first", "second" }, list.Select(k => k.Name).ToArray());
    }

    [Fact]
    public async Task Update_ChangesNameAndLimits_KeepsHash()
    {
        var (key, plaintext) = await _service.CreateAsync(Draft());

        await _service.UpdateAsync(key.Id, Draft("renamed", rpm: 60, tpm: 1000));

        var updated = await _service.ListAsync();
        var saved = Assert.Single(updated);
        Assert.Equal("renamed", saved.Name);
        Assert.Equal(60, saved.RatePerMinute);
        Assert.Equal(1000, saved.TokensPerMinute);
        Assert.Equal(ClientKeyHasher.Hash(plaintext), saved.KeyHash);
    }

    [Fact]
    public async Task Update_WhenMissing_Throws()
        => await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.UpdateAsync(999, Draft()));

    [Fact]
    public async Task Delete_RemovesRowAndTriggersKeysChanged()
    {
        var (key, _) = await _service.CreateAsync(Draft());
        var raised = 0;
        _service.KeysChanged += () => raised++;

        await _service.DeleteAsync(key.Id);

        Assert.Empty(await _service.ListAsync());
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task SetEnabled_TogglesAndTriggersKeysChanged()
    {
        var (key, _) = await _service.CreateAsync(Draft());
        var raised = 0;
        _service.KeysChanged += () => raised++;

        await _service.SetEnabledAsync(key.Id, false);

        Assert.False((await _service.ListAsync()).Single().Enabled);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task RecordRequest_IncrementsCounterAndLastUsed()
    {
        var (key, _) = await _service.CreateAsync(Draft());

        await _service.RecordRequestAsync(key.Id);

        var saved = (await _service.ListAsync()).Single();
        Assert.Equal(1, saved.RequestsUsed);
        Assert.NotNull(saved.LastUsedAt);
        Assert.NotNull(saved.UsageDate);
    }

    [Fact]
    public async Task RecordRequest_WhenUsageDateOld_ResetsCountersFirst()
    {
        var (key, _) = await _service.CreateAsync(Draft());
        await using (var db = _db.CreateFactory().CreateDbContext())
        {
            var entity = await db.ClientKeys.SingleAsync(k => k.Id == key.Id);
            entity.UsageDate = new DateOnly(2000, 1, 1);
            entity.RequestsUsed = 50;
            entity.TokensUsed = 100;
            await db.SaveChangesAsync();
        }

        await _service.RecordRequestAsync(key.Id);

        var saved = (await _service.ListAsync()).Single();
        Assert.Equal(1, saved.RequestsUsed);   // reset về 0 rồi +1
        Assert.Equal(0, saved.TokensUsed);
    }

    [Fact]
    public async Task RecordTokens_AddsPromptPlusCompletion()
    {
        var (key, _) = await _service.CreateAsync(Draft());

        await _service.RecordTokensAsync(key.Id, 12, 34);

        Assert.Equal(46, (await _service.ListAsync()).Single().TokensUsed);
    }

    [Fact]
    public async Task RecordTokens_WhenUsageDateOld_ResetsBeforeAdd()
    {
        var (key, _) = await _service.CreateAsync(Draft());
        await using (var db = _db.CreateFactory().CreateDbContext())
        {
            var entity = await db.ClientKeys.SingleAsync(k => k.Id == key.Id);
            entity.UsageDate = new DateOnly(2000, 1, 1);
            entity.RequestsUsed = 50;
            entity.TokensUsed = 100;
            await db.SaveChangesAsync();
        }

        await _service.RecordTokensAsync(key.Id, 3, 4);

        var saved = (await _service.ListAsync()).Single();
        Assert.Equal(7, saved.TokensUsed);     // reset về 0 rồi +7
        Assert.Equal(0, saved.RequestsUsed);
    }

    [Fact]
    public async Task RecordRequest_WhenKeyDeleted_NoThrow()
    {
        await _service.RecordRequestAsync(999); // fail-open: counter là thống kê, không được nổ
    }
}
